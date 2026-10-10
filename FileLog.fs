/// Minimal file logger: one line per event, rotated at 512 KB.
module DnsAutoSwitch.FileLog

open System
open System.IO
open Microsoft.Extensions.Logging

type FileLogger(path: string, gate: obj) =
    interface ILogger with
        member _.BeginScope(_state) = null
        member _.IsEnabled(level) = level >= LogLevel.Information
        member _.Log(level, _eventId, state, ex, formatter) =
            if level >= LogLevel.Information then
                let text = formatter.Invoke(state, ex)
                let suffix = if isNull ex then "" else $" | {ex.GetType().Name}: {ex.Message}"
                let stamp = DateTime.Now.ToString "yyyy-MM-dd HH:mm:ss"
                let line = $"{stamp} {(string level).PadRight 11} {text}{suffix}"
                lock gate (fun () ->
                    try
                        let info = FileInfo path
                        if info.Exists && info.Length > 512L * 1024L then File.Move(path, path + ".1", true)
                        File.AppendAllText(path, line + Environment.NewLine)
                    with _ -> ())

type FileLoggerProvider(path: string) =
    let gate = obj ()
    interface ILoggerProvider with
        member _.CreateLogger(_category) = FileLogger(path, gate) :> ILogger
        member _.Dispose() = ()

/// Wraps another provider so that a failing sink can never throw into the code that logs.
/// Case seen in the wild: during a Windows shutdown the Event Log service stops before this one and
/// every write fails with "The interface is unknown"; without this wrapper the logging library rethrows
/// that as "An error occurred while writing to logger(s)" and the host reports "BackgroundService failed".
type SafeLogger(inner: ILogger) =
    interface ILogger with
        member _.BeginScope(state) = try inner.BeginScope state with _ -> null
        member _.IsEnabled(level) = try inner.IsEnabled level with _ -> false
        member _.Log(level, eventId, state, ex, formatter) =
            try inner.Log(level, eventId, state, ex, formatter) with _ -> ()

type SafeLoggerProvider(inner: ILoggerProvider) =
    interface ILoggerProvider with
        member _.CreateLogger(category) = SafeLogger(inner.CreateLogger category) :> ILogger
        member _.Dispose() = inner.Dispose()
