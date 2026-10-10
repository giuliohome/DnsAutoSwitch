/// Network primitives: direct UDP DNS probe, LAN adapters, netsh, resolver cache flush, boot time.
module DnsAutoSwitch.Dns

open System
open System.Diagnostics
open System.Net
open System.Net.NetworkInformation
open System.Net.Sockets
open System.Runtime.InteropServices
open System.Text
open System.Threading

/// A standard DNS query: one QNAME, type A, class IN, recursion desired.
let private buildQuery (id: uint16) (name: string) =
    let header =
        [| byte (id >>> 8); byte id   // ID
           0x01uy; 0x00uy             // flags: RD
           0uy; 1uy                   // QDCOUNT = 1
           0uy; 0uy; 0uy; 0uy; 0uy; 0uy |]
    let qname =
        name.Split('.', StringSplitOptions.RemoveEmptyEntries)
        |> Array.collect (fun label -> Array.append [| byte label.Length |] (Encoding.ASCII.GetBytes label))
    Array.concat [ header; qname; [| 0uy; 0uy; 1uy; 0uy; 1uy |] ]   // end of QNAME, QTYPE A, QCLASS IN

/// One query to the server: true if a reply with the same ID comes back, whatever its RCODE
/// (NXDOMAIN still means the server is alive). Works by IP, so it needs no name resolution.
let private querySingle (server: IPAddress) (name: string) (timeout: TimeSpan) =
    task {
        let id = uint16 (Random.Shared.Next(1, 0x10000))
        use udp = new UdpClient(server.AddressFamily)
        use cts = new CancellationTokenSource(timeout)
        try
            udp.Connect(server, 53)
            let! _ = udp.SendAsync(ReadOnlyMemory(buildQuery id name), cts.Token)
            let! reply = udp.ReceiveAsync cts.Token
            let r = reply.Buffer
            return r.Length >= 12 && ((uint16 (r[0]) <<< 8) ||| uint16 (r[1])) = id
        with _ ->
            return false
    }

/// Probe with retries: one reply is enough to call the server reachable.
let probe (server: IPAddress) (name: string) (timeout: TimeSpan) (attempts: int) =
    task {
        let mutable up = false
        let mutable left = max attempts 1
        while not up && left > 0 do
            let! ok = querySingle server name timeout
            up <- ok
            left <- left - 1
        return up
    }

type Adapter =
    { Name: string
      Ipv4: IPAddress
      /// Current IPv4 DNS servers, in Windows order
      Dns: IPAddress list }

let private isIpv4 (ip: IPAddress) = ip.AddressFamily = AddressFamily.InterNetwork

/// LAN adapters: up, not loopback, with an IPv4 address matching the prefix ("" = any) and a default
/// gateway: the configured one, or any IPv4 gateway when none is configured.
let lanAdapters (prefix: string) (gateway: IPAddress option) : Adapter list =
    NetworkInterface.GetAllNetworkInterfaces()
    |> Seq.filter (fun nic ->
        nic.OperationalStatus = OperationalStatus.Up
        && nic.NetworkInterfaceType <> NetworkInterfaceType.Loopback)
    |> Seq.choose (fun nic ->
        let props = nic.GetIPProperties()
        let ipv4 =
            props.UnicastAddresses
            |> Seq.map (fun u -> u.Address)
            |> Seq.tryFind (fun ip ->
                isIpv4 ip && (prefix = "" || ip.ToString().StartsWith(prefix, StringComparison.Ordinal)))
        let gateways =
            props.GatewayAddresses |> Seq.map (fun g -> g.Address) |> Seq.filter isIpv4 |> List.ofSeq
        let routed =
            match gateway with
            | Some wanted -> gateways |> List.exists (fun g -> g.Equals wanted)
            | None -> not gateways.IsEmpty
        match ipv4 with
        | Some ip when routed ->
            Some { Name = nic.Name; Ipv4 = ip; Dns = props.DnsAddresses |> Seq.filter isIpv4 |> List.ofSeq }
        | _ -> None)
    |> List.ofSeq

let private runProcess (file: string) (args: string) =
    let psi =
        ProcessStartInfo(file, args,
                         UseShellExecute = false, CreateNoWindow = true,
                         RedirectStandardOutput = true, RedirectStandardError = true)
    use p = Process.Start psi
    let out = p.StandardOutput.ReadToEnd()
    let err = p.StandardError.ReadToEnd()
    p.WaitForExit()
    if p.ExitCode = 0 then Ok(out.Trim())
    else
        let detail = $"{out.Trim()} {err.Trim()}".Trim()
        Error $"{file} {args} -> exit {p.ExitCode}: {detail}"

/// Static DNS with a single server. Never a secondary one: the Windows resolver does not fail over
/// deterministically, so a secondary server would silently bypass the primary.
let setDns (adapterName: string) (server: IPAddress) =
    runProcess "netsh.exe"
        $"interface ipv4 set dnsservers name=\"{adapterName}\" source=static address={server} register=primary validate=no"

[<DllImport("dnsapi.dll")>]
extern bool DnsFlushResolverCache()

/// Same as ipconfig /flushdns, without spawning a process.
let flush () = DnsFlushResolverCache() |> ignore

/// When the current boot happened (from GetTickCount64).
let bootTime () =
    DateTimeOffset.Now - TimeSpan.FromMilliseconds(float Environment.TickCount64)
