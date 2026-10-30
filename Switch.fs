/// One round: hold/pause, adapters, probe, desired state, change.
module DnsAutoSwitch.Switch

open System
open System.IO
open System.Net
open DnsAutoSwitch.Config

type Change =
    { Adapter: string
      From: IPAddress list
      To: IPAddress
      /// Ok in dry-run mode or when netsh succeeded
      Result: Result<unit, string> }

type Outcome =
    /// Nothing touched, and why
    | Skipped of reason: string
    /// Probe done; the list holds only the adapters whose DNS had to change
    | Reconciled of primaryUp: bool * changes: Change list

let formatDns (servers: IPAddress list) =
    match servers with
    | [] -> "(none)"
    | _ -> servers |> List.map string |> String.concat ","

let isOk (c: Change) =
    match c.Result with
    | Ok () -> true
    | Error _ -> false

// The hold and pause files live in a folder ordinary users can write (shutdown scripts run without elevation),
// so they are untrusted input. The service, running as SYSTEM, never writes or deletes them (a delete through a
// planted link would delete any file), ignores links, and reads at most 256 bytes. The most a user can do
// with them is pause switching, which is what they are for.

type Hold =
    | NoHold
    | HoldActive
    /// Present but not honoured: stale, in the future, unreadable, a link, or not a timestamp.
    | HoldIgnored

/// A hold counts if written after the current boot, not in the future (one minute of clock tolerance) and
/// less than maxAge ago. So it never survives a reboot and never outlives a script that died.
let holdValid (boot: DateTimeOffset) (now: DateTimeOffset) (maxAge: TimeSpan) (written: DateTimeOffset) =
    written > boot && written <= now + TimeSpan.FromMinutes 1.0 && now - written < maxAge

/// The first 256 bytes of the hold file as text (any BOM honoured), or None for a link or an unreadable file.
let readHold (path: string) : string option =
    try
        let info = FileInfo path
        if not info.Exists || info.Attributes.HasFlag FileAttributes.ReparsePoint then
            None
        else
            use stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite ||| FileShare.Delete, 1)
            let buffer = Array.zeroCreate<byte> 256
            let length = stream.Read(buffer, 0, buffer.Length)
            use reader = new StreamReader(new MemoryStream(buffer, 0, length), Text.Encoding.UTF8, true)
            Some(reader.ReadToEnd())
    with _ ->
        None

let holdState (s: Settings) =
    if not (File.Exists s.HoldFile) then
        NoHold
    else
        match readHold s.HoldFile |> Option.map (fun text -> DateTimeOffset.TryParse(text.Trim())) with
        | Some(true, written) when holdValid (Dns.bootTime ()) DateTimeOffset.Now s.HoldMaxAge written -> HoldActive
        | _ -> HoldIgnored

let private blocker (s: Settings) =
    if holdState s = HoldActive then Some $"hold file active ({s.HoldFile})"
    elif File.Exists s.PauseFile then Some $"pause file present ({s.PauseFile})"
    else None

/// One round: decide and, unless dryRun, apply. Hold/pause are checked again after the probe,
/// so a hold raised while the round was running is still respected.
let tick (s: Settings) (dryRun: bool) =
    task {
        match blocker s with
        | Some why -> return Skipped why
        | None ->
            match Dns.lanAdapters s.AdapterPrefix s.Gateway with
            | [] ->
                let gateway = s.Gateway |> Option.map string |> Option.defaultValue "any"
                return Skipped $"no adapter matches (prefix '{s.AdapterPrefix}', gateway {gateway})"
            | adapters ->
                let! up = Dns.probe s.Primary s.ProbeName s.ProbeTimeout s.ProbeAttempts
                match blocker s with
                | Some why -> return Skipped why
                | None ->
                    let desired = if up then s.Primary else s.Fallback
                    let changes =
                        adapters
                        |> List.filter (fun a -> a.Dns <> [ desired ])
                        |> List.map (fun a ->
                            let result =
                                if dryRun then Ok ()
                                else Dns.setDns a.Name desired |> Result.map ignore
                            { Adapter = a.Name; From = a.Dns; To = desired; Result = result })
                    if not dryRun && List.exists isOk changes then Dns.flush ()
                    return Reconciled(up, changes)
    }
