# DnsAutoSwitch

A small Windows service that keeps your LAN DNS pointed at a **primary** server (AdGuard Home, Pi-hole, any
local resolver) while it answers, and switches to a **fallback** (typically the router) when it does not.
One check per minute, one DNS server at a time, no cloud, no dependency on name resolution.

Written in F# on .NET 10. Tested on Windows 11 with AdGuard Home; Pi-hole works the same way.

## Why not just set a secondary DNS server?

Because Windows does not fail over deterministically. With two servers configured, the resolver happily
sends queries to the secondary whenever the primary is slow, and keeps using it for a while. With a
filtering resolver that means ads and trackers slip through at random, and local names stop resolving.
The usual advice for Pi-hole and AdGuard users is "one DNS server only", which leaves you without DNS
whenever that server is off. DnsAutoSwitch keeps the single-server setup and does the switching for you.

## How it works

Every `IntervalSeconds` (default 60) the service:

1. does nothing if a **hold** or **pause** file is present (see below);
2. selects the adapters to manage: up, with an IPv4 address matching `AdapterPrefix` and a default
   gateway (the configured `Gateway`, or any gateway when none is configured). Virtual adapters and
   Internet Connection Sharing interfaces have no default gateway, so they are left alone;
3. probes the primary with a direct UDP DNS query to `Primary:53` for `ProbeName`. Any reply with the
   right transaction ID counts, even NXDOMAIN: the question is whether the server is alive, not what it
   answers. The probe works by IP, so it keeps working while the system DNS is dead;
4. checks hold and pause again, so a hold raised during the round is respected;
5. sets every managed adapter whose DNS differs from the desired server (`Primary` if reachable,
   `Fallback` otherwise) to that single static server and flushes the resolver cache;
6. logs transitions and changes only. A quiet minute writes nothing.

There is deliberately **no hysteresis**: when the primary reboots, DNS goes to the fallback and comes
back a minute later. That is the correct behaviour, not noise.

## Install

1. Download `DnsAutoSwitch-win-x64.zip` from the [releases](../../releases) (self-contained, no .NET
   runtime needed) and unpack it somewhere permanent, for example `C:\Tools\DnsAutoSwitch`.
2. Copy `appsettings.example.json` to `appsettings.json` next to the executable and set at least
   `Primary` and `Fallback`.
3. Check the decision without changing anything (no elevation needed):

   ```powershell
   .\DnsAutoSwitch.exe check
   ```

4. From an elevated prompt, register and start the service (automatic, delayed start, restart on failure):

   ```powershell
   .\DnsAutoSwitch.exe install
   ```

The service runs as LocalSystem. `DnsAutoSwitch.exe uninstall` stops and removes it; the files stay.

## Configuration

`appsettings.json` next to the executable, section `DnsAutoSwitch`. Missing keys keep their defaults.
Relative paths are relative to the executable's folder.

| Key | Default | Meaning |
|---|---|---|
| `Primary` | required | DNS server to use while it answers (AdGuard Home, Pi-hole, ...) |
| `Fallback` | required | DNS server to use otherwise (usually the router) |
| `Gateway` | `""` | Only manage adapters whose default gateway is this address. Empty = any adapter with a default gateway. Set it if your machine ever joins other `192.168.x` networks |
| `AdapterPrefix` | `""` | Only manage adapters whose IPv4 address starts with this, e.g. `192.168.1.`. Empty = any |
| `ProbeName` | `probe.invalid` | Name sent by the probe. Any reply counts, but a name the primary resolves locally (a rewrite or a local record) keeps the probe independent from the Internet |
| `ProbeTimeoutMs` | `1500` | Timeout per attempt |
| `ProbeAttempts` | `2` | Attempts per round; one reply is enough |
| `IntervalSeconds` | `60` | Time between rounds (minimum 5) |
| `HoldMaxAgeMinutes` | `30` | How long a hold file stays valid |
| `HoldFile` | `DnsAutoSwitch.hold` | See *Hold and pause* |
| `PauseFile` | `DnsAutoSwitch.paused` | See *Hold and pause* |
| `LogFile` | `DnsAutoSwitch.log` | Text log, rotated at 512 KB |

`check` and `once` accept `--primary IP` and `--fallback IP` to try other servers without editing the file.

## Commands

```text
DnsAutoSwitch [check|once|install|uninstall] [--primary IP] [--fallback IP]
  (no arguments)      run as a Windows service (as a console app when started by hand)
  check               one dry run: current state and what would change
  once                one real round (elevated prompt)
  install             register the service (automatic, delayed start) and start it (elevated prompt)
  uninstall           stop and remove the service (elevated prompt)
```

Example of `check` with the primary down and DNS already on the fallback:

```text
boot: 2026-10-10 20:26:09
hold: no   pause: no
adapter [Wi-Fi 2] 192.168.1.2  DNS=192.168.1.1
primary 192.168.1.202: UNREACHABLE
DNS already right, nothing to change.
```

## Hold and pause

**Pause**: while the pause file exists, the service leaves DNS alone. Create it before setting DNS by
hand, delete it to resume. The log shows `waiting: pause file present` and `resumed`.

**Hold**: for shutdown scripts. If your shutdown routine switches DNS to the fallback and then powers
off the primary, the service would switch DNS back to the primary in between, and the machine would boot
with DNS pointing at a server that is off. The script writes an ISO-8601 timestamp into the hold file
right before touching anything:

```powershell
Get-Date -Format o | Set-Content -LiteralPath 'C:\Tools\DnsAutoSwitch\DnsAutoSwitch.hold' -Encoding ASCII
```

The hold is honoured only if the timestamp is **later than the current boot** and **younger than
`HoldMaxAgeMinutes`**; anything older is deleted by the service itself. So a hold never survives a reboot
and never outlives a script that died, and there is nothing to clean up. If the script aborts the
shutdown, it should delete the file so the service resumes within a minute. No elevation is needed for
any of this, which is the reason it is a file and not a `Stop-Service`.

## Logging

- `LogFile` (default `DnsAutoSwitch.log` next to the executable): one line per event, local time.
- Windows Event Log, *Application*, source `DnsAutoSwitch`: the same events, from Information up.

```text
2026-10-10 21:42:43 Information started: primary 192.168.1.202, fallback 192.168.1.1, probe gitea.home every 60s
2026-10-10 21:42:46 Information primary DNS 192.168.1.202 UNREACHABLE
2026-10-10 22:00:43 Information primary DNS 192.168.1.202 reachable
2026-10-10 22:00:43 Information [Wi-Fi 2] DNS 192.168.1.1 -> 192.168.1.202
```

## Limits

- Windows only (netsh, Windows services, Event Log). IPv4 only.
- It sets a **single static** DNS server on the managed adapters. If you rely on DHCP-assigned DNS, this
  tool replaces that.
- The probe tells whether the server answers, not whether it answers well. A primary that is up but
  slow stays selected, exactly as it would without this tool.

## Build from source

Requires the .NET 10 SDK.

```powershell
dotnet build -c Release
dotnet publish -c Release -o C:\Tools\DnsAutoSwitch             # framework-dependent
dotnet publish -c Release --self-contained -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true -o publish   # single exe
```

To update a running installation: `Stop-Service DnsAutoSwitch`, publish, `Start-Service DnsAutoSwitch`
(the executable is locked while the service runs). `appsettings.json` is never overwritten by a publish.

CI: `.github/workflows/build.yml` builds on GitHub (Windows runner) and attaches the self-contained zip to
releases on `v*` tags. `.gitea/workflows/build.yaml` does the same build on a self-hosted Gitea runner
(Linux container with the .NET SDK, cross-publishing win-x64); Gitea ignores the `.github` folder when
`.gitea/workflows` exists, and GitHub ignores `.gitea`.

| File | Content |
|---|---|
| `Config.fs` | `Settings`, defaults, `appsettings.json` loading and validation |
| `Dns.fs` | UDP probe (hand-built DNS packet), LAN adapters, `netsh`, cache flush, boot time |
| `Switch.fs` | one round: hold/pause, probe, desired state, changes. `tick settings dryRun` → `Skipped` / `Reconciled` |
| `FileLog.fs` | minimal `ILoggerProvider` writing to a file |
| `Worker.fs` | `BackgroundService` with a `PeriodicTimer`, logging transitions only |
| `Program.fs` | commands, `sc.exe` for install/uninstall, host setup |

## License

MIT.
