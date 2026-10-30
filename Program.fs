/// Entry point: Windows service (no arguments) or the commands check / once / install / uninstall.
module DnsAutoSwitch.Program

open System
open System.Diagnostics
open System.IO
open System.Security.Principal
open System.ServiceProcess
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.Hosting
open Microsoft.Extensions.Hosting.WindowsServices
open Microsoft.Extensions.Logging
open Microsoft.Extensions.Logging.EventLog
open Microsoft.Win32
open DnsAutoSwitch.Config
open DnsAutoSwitch.Security
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
    printfn "  check               one dry run: current state, permissions, and what would change"
    printfn "  once                one real round (elevated prompt)"
    printfn "  install             lock down the folders, register the service (automatic, delayed start) and"
    printfn "                      start it (elevated prompt)"
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

/// Full path: a bare "sc.exe" is looked up in the executable's folder first.
let private sc (args: string) =
    let exe = Path.Combine(Environment.SystemDirectory, "sc.exe")
    let psi = ProcessStartInfo(exe, args, UseShellExecute = false, RedirectStandardOutput = true)
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

/// The executable the service is registered with, if it is registered.
let private registeredExecutable () =
    use key = Registry.LocalMachine.OpenSubKey("SYSTEM\\CurrentControlSet\\Services\\" + serviceName)
    match (if isNull key then null else key.GetValue "ImagePath") with
    | :? string as image ->
        let image = image.Trim()
        if image.StartsWith '"' then
            let close = image.IndexOf('"', 1)
            Some(if close > 0 then image.Substring(1, close - 1) else image.Trim '"')
        else
            Some image
    | _ -> None

let private samePath (a: string) (b: string) =
    try String.Equals(Path.GetFullPath a, Path.GetFullPath b, StringComparison.OrdinalIgnoreCase)
    with _ -> false

let private report (title: string) (found: Finding list) =
    title :: (summarize 12 found |> List.map (fun line -> "  " + line))

/// Everything install does before registering the service. Stops at the first step that fails, before
/// anything is registered: an unsafe service is worse than none.
let private prepareInstall (exe: string) (logFile: string) : Result<unit, string list> =
    let folder = Path.GetDirectoryName exe
    let steps: (unit -> Result<unit, string list>) list =
        [ // install replaces the permissions of everything in its folder: it must be the service's own folder,
          // never a shared one like C:\Tools or, through "dotnet DnsAutoSwitch.dll", the .NET folder.
          fun () ->
            if not (String.Equals(Path.GetFileName exe, "DnsAutoSwitch.exe", StringComparison.OrdinalIgnoreCase)) then
                Error [ $"Run DnsAutoSwitch.exe install directly, not through {Path.GetFileName exe}." ]
            elif not ((Path.GetFileName folder).StartsWith("DnsAutoSwitch", StringComparison.OrdinalIgnoreCase)) then
                Error
                    [ $"install replaces the permissions of everything in {folder}, so the files need a folder of"
                      @"their own named DnsAutoSwitch, e.g. C:\Program Files\DnsAutoSwitch." ]
            else
                Ok()
          fun () ->
            match registeredExecutable () with
            | Some registered when not (samePath registered exe) ->
                Error
                    [ $"The service is registered with another executable: {registered}"
                      "Run 'uninstall' first (either copy works), then 'install' again from here." ]
            | _ -> Ok()
          // Parent folders first: install cannot fix them, so nothing is changed if they are wrong.
          fun () ->
            match auditParents folder with
            | [] -> Ok()
            | found ->
                Error(
                    report $"Ordinary users can rename or replace a parent folder of {folder}:" found
                    @ [ @"Move the files to a folder only administrators can change, e.g. C:\Program Files\DnsAutoSwitch." ]
                )
          fun () ->
            lockTree lockedSddl inheritSddl folder
            |> Result.map (fun () -> printfn "%s: SYSTEM and Administrators full control, Users read." folder)
            |> Result.mapError (fun e -> [ $"Could not set the permissions of {folder}: {e}" ])
          fun () ->
            prepareFolder controlSddl stateFolder
            |> Result.bind (fun () -> prepareFolder lockedSddl logFolder)
            |> Result.bind (fun () -> lockTree lockedSddl inheritSddl logFolder)
            |> Result.map (fun () ->
                printfn "%s: hold and pause files, users may create them." stateFolder
                printfn "%s: log, administrators only." logFolder)
            |> Result.mapError (fun e -> [ $"Could not prepare {stateFolder}: {e}" ])
          // Last: whatever install could not fix, e.g. a LogFile configured in a folder users can write.
          fun () ->
            match auditInstall folder @ auditLog logFile |> List.distinct with
            | [] -> Ok()
            | found -> Error(report "Still unsafe: ordinary users can change what the service runs as SYSTEM:" found) ]
    steps |> List.fold (fun result step -> result |> Result.bind step) (Ok())

let install () =
    requireAdmin ()
    let exe = Environment.ProcessPath
    let logFile =
        match load id with
        | Ok settings -> settings.LogFile
        | Error message ->
            eprintfn "warning: %s" message
            defaultLogFile ()
    match prepareInstall exe logFile with
    | Error lines ->
        for line in lines do eprintfn "%s" line
        eprintfn "Service not installed."
        1
    | Ok() ->
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
        let hold =
            match holdState s with
            | NoHold -> "no"
            | HoldActive -> "ACTIVE"
            | HoldIgnored -> "present, ignored (stale or invalid)"
        printfn "hold: %s (%s)" hold s.HoldFile
        printfn "pause: %s (%s)" (if File.Exists s.PauseFile then "PRESENT" else "no") s.PauseFile
        match auditInstall baseDirectory @ auditLog s.LogFile |> List.distinct with
        | [] -> printfn "permissions: OK"
        | found ->
            for line in report "permissions: UNSAFE, ordinary users can change what the service runs as SYSTEM:" found do
                printfn "%s" line
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
    // Checked once at startup. An unsafe install folder only gets a warning: by now the files may already be
    // someone else's. An unsafe log folder gets no log file at all, because writing it is the risk.
    let installProblems = auditInstall baseDirectory
    let logProblems = auditLog s.LogFile
    let builder = Host.CreateApplicationBuilder(HostApplicationBuilderSettings(ContentRootPath = baseDirectory))
    builder.Services.AddWindowsService(fun o -> o.ServiceName <- serviceName) |> ignore
    builder.Services.AddSingleton<Settings>(s) |> ignore
    builder.Services.AddHostedService<Worker>() |> ignore
    // Only sinks that can never throw. The default Event Log provider throws once the Event Log service
    // has stopped during a Windows shutdown, and the logging library would rethrow that into the service.
    builder.Logging.ClearProviders() |> ignore
    if logProblems.IsEmpty then
        builder.Logging.AddProvider(new FileLoggerProvider(s.LogFile)) |> ignore
    if WindowsServiceHelpers.IsWindowsService() then
        let eventLog = new EventLogLoggerProvider(EventLogSettings(SourceName = serviceName))
        builder.Logging.AddProvider(new SafeLoggerProvider(eventLog)) |> ignore
    else
        builder.Logging.AddSimpleConsole() |> ignore
    builder.Logging.SetMinimumLevel LogLevel.Information |> ignore
    let host = builder.Build()
    let logger = host.Services.GetRequiredService<ILoggerFactory>().CreateLogger serviceName
    if not logProblems.IsEmpty then
        logger.LogError("log file disabled, its folder is not safe: {Details}", String.Join("; ", summarize 10 logProblems))
    if not installProblems.IsEmpty then
        logger.LogWarning(
            "unsafe permissions, ordinary users can change what this service runs as SYSTEM (see README, Install): {Details}",
            String.Join("; ", summarize 10 installProblems))
    host.Run()

/// A service has no console: a configuration error goes to the default log file, if its folder is safe.
let private reportConfigError (asService: bool) (message: string) =
    eprintfn "%s" message
    if asService then
        try
            let path = defaultLogFile ()
            if (auditLog path).IsEmpty then
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
