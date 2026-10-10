/// Entry point: servizio Windows (nessun argomento) o comandi check / once / install / uninstall.
module DnsAutoSwitch.Program

open System
open System.Diagnostics
open System.IO
open System.Net
open System.Security.Principal
open System.ServiceProcess
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.Hosting
open Microsoft.Extensions.Logging
open Microsoft.Extensions.Logging.EventLog
open DnsAutoSwitch.Config
open DnsAutoSwitch.Switch
open DnsAutoSwitch.FileLog
open DnsAutoSwitch.Worker

let serviceName = "DnsAutoSwitch"
let displayName = "DNS AutoSwitch (AdGuard <-> Router)"
let description =
    "Ogni minuto: se AdGuard su 192.168.1.202 risponde, DNS della LAN su AdGuard, altrimenti sul router. "
    + "Hold da Pre-Shutdown.ps1 (C:\\Tools\\DnsAutoSwitch.hold), pausa manuale con C:\\Tools\\DnsAutoSwitch.paused. "
    + "Log: C:\\Tools\\DnsAutoSwitch.log. Sorgente: repo hello-gitea, cartella DnsAutoSwitch."

let usage () =
    printfn "DnsAutoSwitch [check|once|install|uninstall] [--adguard IP] [--router IP]"
    printfn "  (nessun argomento)  gira come servizio Windows (in console, se lanciato a mano)"
    printfn "  check               un giro in dry-run: stato e cosa farebbe"
    printfn "  once                un giro vero (prompt elevato)"
    printfn "  install             registra (Automatico, avvio ritardato) e avvia il servizio (prompt elevato)"
    printfn "  uninstall           ferma e rimuove il servizio (prompt elevato)"
    printfn "  --adguard/--router  override degli IP, per check/once"

/// Argomenti: opzioni --adguard/--router, piu' al massimo un comando.
let rec private parse (s: Settings) (cmds: string list) (args: string list) =
    match args with
    | "--adguard" :: ip :: rest -> parse { s with AdGuard = IPAddress.Parse ip } cmds rest
    | "--router" :: ip :: rest -> parse { s with Router = IPAddress.Parse ip } cmds rest
    | cmd :: rest -> parse s (cmd :: cmds) rest
    | [] -> s, List.rev cmds

let private isAdmin () =
    use identity = WindowsIdentity.GetCurrent()
    WindowsPrincipal(identity).IsInRole WindowsBuiltInRole.Administrator

let private requireAdmin () =
    if not (isAdmin ()) then
        eprintfn "Serve un prompt elevato (amministratore)."
        exit 2

let private sc (args: string) =
    let psi = ProcessStartInfo("sc.exe", args, UseShellExecute = false, RedirectStandardOutput = true)
    use p = Process.Start psi
    let out = p.StandardOutput.ReadToEnd()
    p.WaitForExit()
    printfn "> sc %s\n%s" args (out.Trim())
    p.ExitCode

let private status () =
    try
        use svc = new ServiceController(serviceName)
        Some svc.Status
    with _ -> None

let install () =
    requireAdmin ()
    let exe = Environment.ProcessPath
    let created =
        match status () with
        | Some _ -> printfn "servizio gia' registrato."; 0
        | None ->
            sc $"create {serviceName} binPath= \"\\\"{exe}\\\"\" start= delayed-auto DisplayName= \"{displayName}\""
    if created <> 0 then 1
    else
        let rcDescription = sc $"description {serviceName} \"{description}\""
        let rcFailure = sc $"failure {serviceName} reset= 86400 actions= restart/20000"
        let rcStart =
            match status () with
            | Some ServiceControllerStatus.Running -> printfn "gia' in esecuzione."; 0
            | _ -> sc $"start {serviceName}"
        if rcDescription = 0 && rcFailure = 0 && rcStart = 0 then 0 else 1

let uninstall () =
    requireAdmin ()
    match status () with
    | None -> printfn "servizio non registrato."; 0
    | Some current ->
        if current <> ServiceControllerStatus.Stopped then
            use svc = new ServiceController(serviceName)
            svc.Stop()
            svc.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds 15.)
            printfn "fermato."
        sc $"delete {serviceName}"

/// check (dry-run) e once (vero): stampa stato e decisione.
let private describe (s: Settings) (dryRun: bool) =
    task {
        printfn "boot: %s" ((Dns.bootTime ()).ToString "yyyy-MM-dd HH:mm:ss")
        printfn "hold: %s   pausa: %s"
            (if File.Exists s.HoldFile then "PRESENTE" else "no")
            (if File.Exists s.PauseFile then "PRESENTE" else "no")
        for a in Dns.lanAdapters s.LanPrefix s.Gateway do
            printfn "adapter [%s] %O  DNS=%s" a.Name a.Ipv4 (formatDns a.Dns)
        let! outcome = tick s dryRun
        match outcome with
        | Skipped why -> printfn "nessuna azione: %s" why
        | Reconciled(up, changes) ->
            printfn "AdGuard %O: %s" s.AdGuard (if up then "raggiungibile" else "NON raggiungibile")
            if changes.IsEmpty then printfn "DNS gia' giusto, niente da cambiare."
            for c in changes do
                match c.Result with
                | Ok () -> printfn "%s [%s] DNS %s -> %O" (if dryRun then "FAREI:" else "FATTO:") c.Adapter (formatDns c.From) c.To
                | Error e -> printfn "ERRORE [%s] DNS -> %O: %s" c.Adapter c.To e
        return 0
    }

let private runService (s: Settings) =
    let builder = Host.CreateApplicationBuilder()
    builder.Services.AddWindowsService(fun o -> o.ServiceName <- serviceName) |> ignore
    builder.Services.AddSingleton<Settings>(s) |> ignore
    builder.Services.AddHostedService<Worker>() |> ignore
    builder.Logging.AddProvider(new FileLoggerProvider(s.LogFile)) |> ignore
    // Il default scrive nell'Event Log solo da Warning in su: qui anche gli switch (Information)
    builder.Logging.AddFilter<EventLogLoggerProvider>(fun (level: LogLevel) -> level >= LogLevel.Information) |> ignore
    builder.Build().Run()

[<EntryPoint>]
let main argv =
    let settings, commands = parse defaults [] (List.ofArray argv)
    match commands with
    | [] -> runService settings; 0
    | [ "check" ] -> (describe settings true).GetAwaiter().GetResult()
    | [ "once" ] -> requireAdmin (); (describe settings false).GetAwaiter().GetResult()
    | [ "install" ] -> install ()
    | [ "uninstall" ] -> uninstall ()
    | _ -> usage (); 2
