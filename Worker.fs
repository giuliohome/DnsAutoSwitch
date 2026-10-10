/// Il loop del servizio: un giro ogni Interval; nel log solo transizioni e cambi, niente rumore.
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
            if lastSkip <> Some reason then logger.LogInformation("in attesa: {Reason}", reason)
            lastSkip <- Some reason
        | Reconciled(up, changes) ->
            if lastSkip.IsSome then logger.LogInformation("ripresa")
            lastSkip <- None
            if lastUp <> Some up then
                logger.LogInformation("AdGuard {AdGuard} {State}", settings.AdGuard,
                                      (if up then "raggiungibile" else "NON raggiungibile"))
            lastUp <- Some up
            for c in changes do
                match c.Result with
                | Ok () -> logger.LogInformation("[{Adapter}] DNS {From} -> {To}", c.Adapter, formatDns c.From, c.To)
                | Error e -> logger.LogError("[{Adapter}] DNS {From} -> {To} FALLITO: {Error}", c.Adapter, formatDns c.From, c.To, e)

    override this.ExecuteAsync(ct: CancellationToken) =
        task {
            logger.LogInformation("avvio: AdGuard {AdGuard}, router {Router}, sonda {Probe} ogni {Seconds}s",
                                  settings.AdGuard, settings.Router, settings.ProbeName, settings.Interval.TotalSeconds)
            try
                use timer = new PeriodicTimer(settings.Interval)
                let mutable go = true
                while go do
                    try
                        let! outcome = tick settings false
                        this.Report outcome
                    with ex when not ct.IsCancellationRequested ->
                        logger.LogError(ex, "giro fallito")
                    let! next = timer.WaitForNextTickAsync ct
                    go <- next
            with :? OperationCanceledException ->
                ()
            logger.LogInformation("stop")
        }
