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

/// The hold file is honored only if written after the current boot and less than HoldMaxAge ago.
/// A stale one (previous boot, or a shutdown script that died) is deleted.
let private holdActive (s: Settings) =
    if not (File.Exists s.HoldFile) then
        false
    else
        let written =
            match DateTimeOffset.TryParse((File.ReadAllText s.HoldFile).Trim()) with
            | true, t -> Some t
            | _ -> None
        match written with
        | Some t when t > Dns.bootTime () && DateTimeOffset.Now - t < s.HoldMaxAge -> true
        | _ ->
            try File.Delete s.HoldFile with _ -> ()
            false

let private blocker (s: Settings) =
    if holdActive s then Some $"hold file active ({s.HoldFile})"
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
