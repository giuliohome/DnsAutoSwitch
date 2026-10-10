/// The service loop: one round every Interval; the log gets transitions and changes only, no noise.
module DnsAutoSwitch.Worker

open System
open System.Threading
open Microsoft.Extensions.Hosting
open Microsoft.Extensions.Logging
open DnsAutoSwitch.Config
open DnsAutoSwitch.Switch

type Worker(logger: ILogger<Worker>, settings: Settings) =
    inherit BackgroundService()

    let mutable lastUp: bool option = None
    let mutable lastSkip: string option = None

    member private _.Report(outcome: Outcome) =
        match outcome with
        | Skipped reason ->
            if lastSkip <> Some reason then logger.LogInformation("waiting: {Reason}", reason)
            lastSkip <- Some reason
        | Reconciled(up, changes) ->
            if lastSkip.IsSome then logger.LogInformation("resumed")
            lastSkip <- None
            if lastUp <> Some up then
                logger.LogInformation("primary DNS {Primary} {State}", settings.Primary,
                                      (if up then "reachable" else "UNREACHABLE"))
            lastUp <- Some up
            for c in changes do
                match c.Result with
                | Ok () -> logger.LogInformation("[{Adapter}] DNS {From} -> {To}", c.Adapter, formatDns c.From, c.To)
                | Error e -> logger.LogError("[{Adapter}] DNS {From} -> {To} FAILED: {Error}", c.Adapter, formatDns c.From, c.To, e)

    override this.ExecuteAsync(ct: CancellationToken) =
        task {
            logger.LogInformation("started: primary {Primary}, fallback {Fallback}, probe {Probe} every {Seconds}s",
                                  settings.Primary, settings.Fallback, settings.ProbeName, settings.Interval.TotalSeconds)
            try
                use timer = new PeriodicTimer(settings.Interval)
                let mutable go = true
                while go do
                    try
                        let! outcome = tick settings false
                        this.Report outcome
                    with ex when not ct.IsCancellationRequested ->
                        logger.LogError(ex, "round failed")
                    let! next = timer.WaitForNextTickAsync ct
                    go <- next
            with :? OperationCanceledException ->
                ()
            logger.LogInformation("stopped")
        }
