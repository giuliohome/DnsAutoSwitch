module DnsAutoSwitch.Tests.HoldTests

open System
open System.IO
open System.Text
open Xunit
open DnsAutoSwitch.Switch
open DnsAutoSwitch.Tests.TempFolder

let private boot = DateTimeOffset(2026, 10, 30, 8, 0, 0, TimeSpan.FromHours 1.0)
let private now = boot.AddHours 2.0
let private maxAge = TimeSpan.FromMinutes 30.0
let private valid = holdValid boot now maxAge

[<Fact>]
let ``a recent hold counts`` () = Assert.True(valid (now.AddMinutes -5.0))

[<Fact>]
let ``a hold from before the boot does not`` () = Assert.False(valid (boot.AddMinutes -1.0))

[<Fact>]
let ``a hold older than HoldMaxAge does not`` () = Assert.False(valid (now.AddMinutes -31.0))

[<Fact>]
let ``a hold far in the future does not`` () = Assert.False(valid (now.AddHours 5.0))

[<Fact>]
let ``a few seconds of clock skew are tolerated`` () = Assert.True(valid (now.AddSeconds 30.0))

/// What Pre-Shutdown.ps1 writes: Get-Date -Format o | Set-Content -Encoding ASCII
let private stamp = "2026-10-30T20:15:42.1234567+01:00"

[<Fact>]
let ``a hold written by a shutdown script is read`` () =
    use temp = new TempFolder()
    let file = Path.Combine(temp.Path, "DnsAutoSwitch.hold")
    File.WriteAllText(file, stamp + "\r\n", Encoding.ASCII)
    Assert.Equal(Some(stamp + "\r\n"), readHold file)

[<Fact>]
let ``UTF-16 with a BOM is read too`` () =
    use temp = new TempFolder()
    let file = Path.Combine(temp.Path, "DnsAutoSwitch.hold")
    File.WriteAllText(file, stamp, Encoding.Unicode)
    Assert.Equal(Some stamp, readHold file)

[<Fact>]
let ``only the start of a big file is read`` () =
    use temp = new TempFolder()
    let file = Path.Combine(temp.Path, "DnsAutoSwitch.hold")
    File.WriteAllBytes(file, Array.create (4 * 1024 * 1024) (byte 'x'))
    Assert.Equal(256, (readHold file).Value.Length)

[<Fact>]
let ``a missing hold file reads as nothing`` () =
    use temp = new TempFolder()
    Assert.Equal(None, readHold (Path.Combine(temp.Path, "DnsAutoSwitch.hold")))
