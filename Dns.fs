/// Primitive di rete: sonda DNS UDP diretta, adapter della LAN, netsh, flush della cache.
module DnsAutoSwitch.Dns

open System
open System.Diagnostics
open System.Net
open System.Net.NetworkInformation
open System.Net.Sockets
open System.Runtime.InteropServices
open System.Text
open System.Threading

/// Query DNS standard: un QNAME, tipo A, classe IN, recursion desired.
let private buildQuery (id: uint16) (name: string) =
    let header =
        [| byte (id >>> 8); byte id   // ID
           0x01uy; 0x00uy             // flags: RD
           0uy; 1uy                   // QDCOUNT = 1
           0uy; 0uy; 0uy; 0uy; 0uy; 0uy |]
    let qname =
        name.Split('.', StringSplitOptions.RemoveEmptyEntries)
        |> Array.collect (fun label -> Array.append [| byte label.Length |] (Encoding.ASCII.GetBytes label))
    Array.concat [ header; qname; [| 0uy; 0uy; 1uy; 0uy; 1uy |] ]   // fine QNAME, QTYPE A, QCLASS IN

/// Una query al server: true se arriva una risposta con lo stesso ID, qualunque sia il RCODE
/// (anche NXDOMAIN vuol dire che il server e' vivo). Lavora per IP: non dipende da nessuna risoluzione.
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

/// Sonda con tentativi: basta una risposta per dire "raggiungibile".
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
      /// Server DNS IPv4 attuali, nell'ordine di Windows
      Dns: IPAddress list }

let private isIpv4 (ip: IPAddress) = ip.AddressFamily = AddressFamily.InterNetwork

/// Adapter della LAN di casa: Up, con un IPv4 nel prefisso e il router come default gateway.
let lanAdapters (lanPrefix: string) (gateway: IPAddress) : Adapter list =
    NetworkInterface.GetAllNetworkInterfaces()
    |> Seq.filter (fun nic ->
        nic.OperationalStatus = OperationalStatus.Up
        && nic.NetworkInterfaceType <> NetworkInterfaceType.Loopback)
    |> Seq.choose (fun nic ->
        let props = nic.GetIPProperties()
        let ipv4 =
            props.UnicastAddresses
            |> Seq.map (fun u -> u.Address)
            |> Seq.tryFind (fun ip -> isIpv4 ip && ip.ToString().StartsWith(lanPrefix, StringComparison.Ordinal))
        let viaRouter = props.GatewayAddresses |> Seq.exists (fun g -> g.Address.Equals gateway)
        match ipv4 with
        | Some ip when viaRouter ->
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

/// DNS statico con un solo server, come gli script Set-DNS-*.ps1
/// (mai un secondario: il resolver di Windows non fa failover deterministico).
let setDns (adapterName: string) (server: IPAddress) =
    runProcess "netsh.exe"
        $"interface ipv4 set dnsservers name=\"{adapterName}\" source=static address={server} register=primary validate=no"

[<DllImport("dnsapi.dll")>]
extern bool DnsFlushResolverCache()

/// Equivale a ipconfig /flushdns, senza processo esterno.
let flush () = DnsFlushResolverCache() |> ignore

/// Istante del boot corrente (da GetTickCount64).
let bootTime () =
    DateTimeOffset.Now - TimeSpan.FromMilliseconds(float Environment.TickCount64)
