/// Settings: built-in defaults, then appsettings.json next to the executable, then command-line overrides.
module DnsAutoSwitch.Config

open System
open System.IO
open System.Net
open Microsoft.Extensions.Configuration

/// Typed settings used by the rest of the program.
type Settings =
    { /// DNS server to use while it answers (AdGuard Home, Pi-hole, ...).
      Primary: IPAddress
      /// DNS server to use when the primary does not answer (typically the router).
      Fallback: IPAddress
      /// Only touch adapters whose default gateway is this address. None = any adapter with a default gateway.
      Gateway: IPAddress option
      /// Only touch adapters whose IPv4 address starts with this prefix. "" = any.
      AdapterPrefix: string
      /// Name sent by the probe. Any reply counts, so a name the primary resolves locally keeps the probe
      /// independent from the Internet.
      ProbeName: string
      ProbeTimeout: TimeSpan
      ProbeAttempts: int
      /// How often to check.
      Interval: TimeSpan
      /// File a shutdown script writes with an ISO-8601 timestamp. While it is valid, DNS is left alone.
      HoldFile: string
      /// A hold is valid only if written after the current boot and less than this long ago.
      HoldMaxAge: TimeSpan
      /// While this file exists, DNS is left alone.
      PauseFile: string
      LogFile: string }

/// Shape of the "DnsAutoSwitch" section of appsettings.json: plain strings and numbers, mutable so the
/// configuration binder can fill it. Keys that are missing keep their defaults.
[<CLIMutable>]
type FileSettings =
    { Primary: string
      Fallback: string
      Gateway: string
      AdapterPrefix: string
      ProbeName: string
      ProbeTimeoutMs: int
      ProbeAttempts: int
      IntervalSeconds: int
      HoldMaxAgeMinutes: int
      HoldFile: string
      PauseFile: string
      LogFile: string }

let baseDirectory = AppContext.BaseDirectory
let configFile = Path.Combine(baseDirectory, "appsettings.json")

/// A fresh copy each time: the binder mutates the instance it is given.
let defaultFileSettings () : FileSettings =
    { Primary = ""
      Fallback = ""
      Gateway = ""
      AdapterPrefix = ""
      ProbeName = "probe.invalid"
      ProbeTimeoutMs = 1500
      ProbeAttempts = 2
      IntervalSeconds = 60
      HoldMaxAgeMinutes = 30
      HoldFile = "DnsAutoSwitch.hold"
      PauseFile = "DnsAutoSwitch.paused"
      LogFile = "DnsAutoSwitch.log" }

type private ResultBuilder() =
    member _.Bind(r, f) = Result.bind f r
    member _.Return x = Ok x

let private result = ResultBuilder()

let private parseIp (label: string) (text: string) =
    match IPAddress.TryParse text with
    | true, ip -> Ok ip
    | _ -> Error $"{label}: '{text}' is not a valid IP address"

/// Relative paths are relative to the executable, not to the current directory (a service starts in System32).
let private resolvePath (path: string) =
    if Path.IsPathRooted path then path else Path.Combine(baseDirectory, path)

/// Validates and converts the file settings into typed settings.
let toSettings (f: FileSettings) : Result<Settings, string> =
    if String.IsNullOrWhiteSpace f.Primary || String.IsNullOrWhiteSpace f.Fallback then
        Error $"Primary and Fallback are required. Set them in {configFile} (see appsettings.example.json)."
    else
        result {
            let! primary = parseIp "Primary" f.Primary
            let! fallback = parseIp "Fallback" f.Fallback
            let! gateway =
                if String.IsNullOrWhiteSpace f.Gateway then Ok None
                else parseIp "Gateway" f.Gateway |> Result.map Some
            return
                { Primary = primary
                  Fallback = fallback
                  Gateway = gateway
                  AdapterPrefix = (if isNull f.AdapterPrefix then "" else f.AdapterPrefix.Trim())
                  ProbeName = (if String.IsNullOrWhiteSpace f.ProbeName then "probe.invalid" else f.ProbeName.Trim())
                  ProbeTimeout = TimeSpan.FromMilliseconds(float (max f.ProbeTimeoutMs 100))
                  ProbeAttempts = max f.ProbeAttempts 1
                  Interval = TimeSpan.FromSeconds(float (max f.IntervalSeconds 5))
                  HoldFile = resolvePath f.HoldFile
                  HoldMaxAge = TimeSpan.FromMinutes(float (max f.HoldMaxAgeMinutes 1))
                  PauseFile = resolvePath f.PauseFile
                  LogFile = resolvePath f.LogFile }
        }

/// Defaults, then the "DnsAutoSwitch" section of appsettings.json (optional), then the overrides.
let load (overrides: FileSettings -> FileSettings) : Result<Settings, string> =
    let configuration =
        ConfigurationBuilder()
            .SetBasePath(baseDirectory)
            .AddJsonFile("appsettings.json", optional = true, reloadOnChange = false)
            .Build()
    let fromFile = defaultFileSettings ()
    configuration.GetSection("DnsAutoSwitch").Bind(fromFile)
    toSettings (overrides fromFile)
