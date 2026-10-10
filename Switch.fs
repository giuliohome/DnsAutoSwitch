/// La logica di un giro: hold/pausa, adapter, sonda, stato desiderato, cambio.
module DnsAutoSwitch.Switch

open System
open System.IO
open System.Net
open DnsAutoSwitch.Config

type Change =
    { Adapter: string
      From: IPAddress list
      To: IPAddress
      /// Ok in dry-run o se netsh e' andato bene
      Result: Result<unit, string> }

type Outcome =
    /// Non si tocca nulla, e perche'
    | Skipped of reason: string
    /// Sonda fatta; la lista contiene solo gli adapter il cui DNS andava cambiato
    | Reconciled of adGuardUp: bool * changes: Change list

let formatDns (servers: IPAddress list) =
    match servers with
    | [] -> "(nessuno)"
    | _ -> servers |> List.map string |> String.concat ","

let isOk (c: Change) =
    match c.Result with
    | Ok () -> true
    | Error _ -> false

/// Hold di Pre-Shutdown.ps1: vale solo se scritto dopo il boot corrente e da meno di HoldMaxAge.
/// Un hold vecchio (boot precedente, o script interrotto) viene rimosso.
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
    if holdActive s then Some "hold di Pre-Shutdown.ps1 attivo"
    elif File.Exists s.PauseFile then Some $"pausa manuale ({s.PauseFile})"
    else None

/// Un giro: decide e, se non dryRun, applica. Il blocco (hold/pausa) e' ricontrollato dopo la sonda,
/// per non cambiare il DNS se l'hold e' arrivato mentre il giro era in corso.
let tick (s: Settings) (dryRun: bool) =
    task {
        match blocker s with
        | Some why -> return Skipped why
        | None ->
            match Dns.lanAdapters s.LanPrefix s.Gateway with
            | [] -> return Skipped $"nessun adapter {s.LanPrefix}* con gateway {s.Gateway}"
            | adapters ->
                let! up = Dns.probe s.AdGuard s.ProbeName s.ProbeTimeout s.ProbeAttempts
                match blocker s with
                | Some why -> return Skipped why
                | None ->
                    let desired = if up then s.AdGuard else s.Router
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
