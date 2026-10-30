module DnsAutoSwitch.Tests.ConfigTests

open System
open System.IO
open Xunit
open DnsAutoSwitch.Config

let private programData = Environment.GetFolderPath Environment.SpecialFolder.CommonApplicationData

[<Fact>]
let ``by default hold and pause go to ProgramData, the log to its logs subfolder`` () =
    match toSettings { defaultFileSettings () with Primary = "192.168.1.202"; Fallback = "192.168.1.1" } with
    | Error e -> failwith e
    | Ok s ->
        Assert.Equal(Path.Combine(programData, "DnsAutoSwitch", "DnsAutoSwitch.hold"), s.HoldFile)
        Assert.Equal(Path.Combine(programData, "DnsAutoSwitch", "DnsAutoSwitch.paused"), s.PauseFile)
        Assert.Equal(Path.Combine(programData, "DnsAutoSwitch", "logs", "DnsAutoSwitch.log"), s.LogFile)

[<Fact>]
let ``environment variables are expanded, as in appsettings.example.json`` () =
    Assert.Equal(Path.Combine(programData, "DnsAutoSwitch", "x.hold"), resolvePath @"%ProgramData%\DnsAutoSwitch\x.hold")

[<Fact>]
let ``relative paths are next to the executable`` () =
    Assert.Equal(Path.Combine(baseDirectory, "x.log"), resolvePath "x.log")
