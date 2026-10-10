/// Entry point: Windows service (no arguments) or the commands check / once / install / uninstall.
module DnsAutoSwitch.Program

open System
open System.Diagnostics
open System.IO
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
let displayName = "DNS AutoSwitch (primary <-> fallback)"
let description =
    "Keeps the LAN DNS on the primary server (AdGuard Home, Pi-hole, ...) while it answers, and on the fallback "
    + "(router) otherwise. Configuration: appsettings.json next to the executable. Hold and pause files pause it."

let usage () =
    printfn "DnsAutoSwitch [check|once|install|uninstall] [--primary IP] [--fallback IP]"
    printfn "  (no arguments)      run as a Windows service (as a console app when started by hand)"
    printfn "  check               one dry run: current state and what would change"
    printfn "  once                one real round (elevated prompt)"
    printfn "  install             register the service (automatic, delayed start) and start it (elevated prompt)"
    printfn "  uninstall           stop and remove the service (elevated prompt)"
    printfn "  --primary/--fallback  override the configured servers (check/once)"
    printfn ""
    printfn "Configuration: %s (see appsettings.example.json)" configFile

/// Arguments: --primary/--fallback overrides, plus at most one command.
let rec private parse (overrides: FileSettings -> FileSettings) (cmds: string list) (args: string list) =
    match args with
    | "--primary" :: ip :: rest -> parse (overrides >> fun (f: FileSettings) -> { f with Primary = ip }) cmds rest
    | "--fallback" :: ip :: rest -> parse (overrides >> fun (f: FileSettings) -> { f with Fallback = ip }) cmds rest
    | cmd :: rest -> parse overrides (cmd :: cmds) rest
    | [] -> overrides, List.rev cmds

let private isAdmin () =
    use identity = WindowsIdentity.GetCurrent()
    WindowsPrincipal(identity).IsInRole WindowsBuiltInRole.Administrator

let private requireAdmin () =
    if not (isAdmin ()) then
        eprintfn "This command needs an elevated (administrator) prompt."
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
        | Some _ -> printfn "service already registered."; 0
        | None ->
            sc $"create {serviceName} binPath= \"\\\"{exe}\\\"\" start= delayed-auto DisplayName= \"{displayName}\""
    if created <> 0 then 1
    else
        let rcDescription = sc $"description {serviceName} \"{description}\""
        let rcFailure = sc $"failure {serviceName} reset= 86400 actions= restart/20000"
        let rcStart =
            match status () with
            | Some ServiceControllerStatus.Running -> printfn "already running."; 0
            | _ -> sc $"start {serviceName}"
        if rcDescription = 0 && rcFailure = 0 && rcStart = 0 then 0 else 1

let uninstall () =
    requireAdmin ()
    match status () with
    | None -> printfn "service not registered."; 0
    | Some current ->
        if current <> ServiceControllerStatus.Stopped then
            use svc = new ServiceController(serviceName)
            svc.Stop()
            svc.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds 15.)
            printfn "stopped."
        sc $"delete {serviceName}"

/// check (dry run) and once (real): print the state and the decision.
let private describe (s: Settings) (dryRun: bool) =
    task {
        printfn "boot: %s" ((Dns.bootTime ()).ToString "yyyy-MM-dd HH:mm:ss")
        printfn "hold: %s   pause: %s"
            (if File.Exists s.HoldFile then "PRESENT" else "no")
            (if File.Exists s.PauseFile then "PRESENT" else "no")
        for a in Dns.lanAdapters s.AdapterPrefix s.Gateway do
            printfn "adapter [%s] %O  DNS=%s" a.Name a.Ipv4 (formatDns a.Dns)
        let! outcome = tick s dryRun
        match outcome with
        | Skipped why -> printfn "no action: %s" why
        | Reconciled(up, changes) ->
            printfn "primary %O: %s" s.Primary (if up then "reachable" else "UNREACHABLE")
            if changes.IsEmpty then printfn "DNS already right, nothing to change."
            for c in changes do
                match c.Result with
                | Ok () -> printfn "%s [%s] DNS %s -> %O" (if dryRun then "WOULD:" else "DONE: ") c.Adapter (formatDns c.From) c.To
                | Error e -> printfn "ERROR [%s] DNS -> %O: %s" c.Adapter c.To e
        return 0
    }

let private runService (s: Settings) =
    let builder = Host.CreateApplicationBuilder(HostApplicationBuilderSettings(ContentRootPath = baseDirectory))
    builder.Services.AddWindowsService(fun o -> o.ServiceName <- serviceName) |> ignore
    builder.Services.AddSingleton<Settings>(s) |> ignore
    builder.Services.AddHostedService<Worker>() |> ignore
    builder.Logging.AddProvider(new FileLoggerProvider(s.LogFile)) |> ignore
    // By default only Warning and above reach the Event Log; switches are Information.
    builder.Logging.AddFilter<EventLogLoggerProvider>(fun (level: LogLevel) -> level >= LogLevel.Information) |> ignore
    builder.Build().Run()

/// A service has no console: a configuration error goes to the default log file next to the executable.
let private reportConfigError (asService: bool) (message: string) =
    eprintfn "%s" message
    if asService then
        try
            let path = Path.Combine(baseDirectory, (defaultFileSettings ()).LogFile)
            let stamp = DateTime.Now.ToString "yyyy-MM-dd HH:mm:ss"
            File.AppendAllText(path, $"{stamp} Error       {message}{Environment.NewLine}")
        with _ -> ()
    2

[<EntryPoint>]
let main argv =
    let overrides, commands = parse id [] (List.ofArray argv)
    match commands with
    | [] | [ "check" ] | [ "once" ] ->
        match load overrides with
        | Error message -> reportConfigError commands.IsEmpty message
        | Ok settings ->
            match commands with
            | [ "check" ] -> (describe settings true).GetAwaiter().GetResult()
            | [ "once" ] -> requireAdmin (); (describe settings false).GetAwaiter().GetResult()
            | _ -> runService settings; 0
    | [ "install" ] -> install ()
    | [ "uninstall" ] -> uninstall ()
    | _ -> usage (); 2
