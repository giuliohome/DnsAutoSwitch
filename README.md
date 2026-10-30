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
   runtime needed) and, from an elevated prompt, unpack it into `C:\Program Files\DnsAutoSwitch`.
2. Copy `appsettings.example.json` to `appsettings.json` next to the executable and set at least
   `Primary` and `Fallback`.
3. Check the decision without changing anything (no elevation needed):

   ```powershell
   & 'C:\Program Files\DnsAutoSwitch\DnsAutoSwitch.exe' check
   ```

4. From an elevated prompt, register and start the service (automatic, delayed start, restart on failure):

   ```powershell
   & 'C:\Program Files\DnsAutoSwitch\DnsAutoSwitch.exe' install
   ```

`install` also:

- refuses to go on, without changing anything, if the folder is not its own (its name must start with
  `DnsAutoSwitch`: `install` replaces the permissions of everything in it) or if an ordinary user can rename
  or replace one of the parent folders;
- locks the install folder: owner Administrators, SYSTEM and Administrators full control, Users read and
  execute, nothing inherited from the parent folder, everything below reset to inherit that;
- creates `%ProgramData%\DnsAutoSwitch` for the hold and pause files and `%ProgramData%\DnsAutoSwitch\logs`
  for the log (see *Hold and pause*);
- checks everything again and registers the service only if nothing is left that an ordinary user could
  change.

The service runs as LocalSystem. `DnsAutoSwitch.exe uninstall` stops and removes it; the files stay.

### Why the folder matters

Whoever can replace the executable, drop a DLL next to it, edit `appsettings.json`, or plant a link where
the service writes its log, can run code as SYSTEM or send all your DNS queries to a server of their
choice. A folder created directly under `C:\` (`C:\Tools`, `C:\Apps`, ...) inherits *Authenticated Users:
Modify* from the drive root: every local user, and every program they run, can do all of that, and can
also rename the folder and put a different one in its place. `C:\Program Files` can only be changed by
administrators.

`check` prints a `permissions:` line with what is wrong, if anything. The service checks at startup too: it
logs a warning when the install folder is not safe, and does not write the log file at all when the log
folder is not safe (the Event Log still gets everything).

### Upgrading from 1.1.1 or earlier

Up to 1.1.1 this README suggested `C:\Tools\DnsAutoSwitch`, and hold, pause and log files next to the
executable or in `C:\Tools`. From an elevated prompt:

```powershell
& 'C:\Tools\DnsAutoSwitch\DnsAutoSwitch.exe' uninstall
Expand-Archive DnsAutoSwitch-win-x64.zip 'C:\Program Files\DnsAutoSwitch'
Copy-Item 'C:\Tools\DnsAutoSwitch\appsettings.json' 'C:\Program Files\DnsAutoSwitch\'
notepad 'C:\Program Files\DnsAutoSwitch\appsettings.json'   # remove HoldFile, PauseFile and LogFile
& 'C:\Program Files\DnsAutoSwitch\DnsAutoSwitch.exe' install
```

Then point your shutdown script at the new hold file (see *Hold and pause*) and delete the old folder.

## Configuration

`appsettings.json` next to the executable, section `DnsAutoSwitch`. Missing keys keep their defaults.
Paths may contain environment variables such as `%ProgramData%`. Relative paths are relative to the
executable's folder, which only administrators can write: fine for the log, wrong for hold and pause.

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
| `HoldFile` | `%ProgramData%\DnsAutoSwitch\DnsAutoSwitch.hold` | See *Hold and pause* |
| `PauseFile` | `%ProgramData%\DnsAutoSwitch\DnsAutoSwitch.paused` | See *Hold and pause* |
| `LogFile` | `%ProgramData%\DnsAutoSwitch\logs\DnsAutoSwitch.log` | Text log, rotated at 512 KB. Only written if ordinary users cannot write its folder |

`check` and `once` accept `--primary IP` and `--fallback IP` to try other servers without editing the file.

## Commands

```text
DnsAutoSwitch [check|once|install|uninstall] [--primary IP] [--fallback IP]
  (no arguments)      run as a Windows service (as a console app when started by hand)
  check               one dry run: current state, permissions, and what would change
  once                one real round (elevated prompt)
  install             lock down the folders, register the service (automatic, delayed start) and
                      start it (elevated prompt)
  uninstall           stop and remove the service (elevated prompt)
```

Example of `check` with the primary down and DNS already on the fallback:

```text
boot: 2026-10-10 20:26:09
hold: no (C:\ProgramData\DnsAutoSwitch\DnsAutoSwitch.hold)
pause: no (C:\ProgramData\DnsAutoSwitch\DnsAutoSwitch.paused)
permissions: OK
adapter [Wi-Fi 2] 192.168.1.2  DNS=192.168.1.1
primary 192.168.1.202: UNREACHABLE
DNS already right, nothing to change.
```

## Hold and pause

Both files live in `%ProgramData%\DnsAutoSwitch` (usually `C:\ProgramData\DnsAutoSwitch`). `install`
creates that folder so that ordinary users can create files in it and change or delete the files they
created, without elevation. They cannot delete or rename the folder, its `logs` subfolder, or other
people's files.

**Pause**: while the pause file exists, the service leaves DNS alone. Create it before setting DNS by
hand, delete it to resume. The log shows `waiting: pause file present` and `resumed`. Create it from a
normal prompt: one created from an elevated prompt belongs to Administrators, and only an elevated prompt
can delete it.

```powershell
New-Item 'C:\ProgramData\DnsAutoSwitch\DnsAutoSwitch.paused'
Remove-Item 'C:\ProgramData\DnsAutoSwitch\DnsAutoSwitch.paused'
```

**Hold**: for shutdown scripts. If your shutdown routine switches DNS to the fallback and then powers
off the primary, the service would switch DNS back to the primary in between, and the machine would boot
with DNS pointing at a server that is off. The script writes an ISO-8601 timestamp into the hold file
right before touching anything:

```powershell
Get-Date -Format o | Set-Content -LiteralPath 'C:\ProgramData\DnsAutoSwitch\DnsAutoSwitch.hold' -Encoding ASCII
```

The hold is honoured only if the timestamp is **later than the current boot**, **not in the future** and
**younger than `HoldMaxAgeMinutes`**; anything else is ignored. So a hold never survives a reboot and never
outlives a script that died, and there is nothing to clean up: the next shutdown overwrites the file. If
the script aborts the shutdown, it should delete the file so the service resumes within a minute. No
elevation is needed for any of this, which is the reason it is a file and not a `Stop-Service`.

Since any local user can write these files, the service treats them as untrusted input. It never writes or
deletes them (up to 1.1.1 it deleted stale holds, as SYSTEM, in a folder users could write), ignores them
if they are links, and reads at most 256 bytes of the hold file. The most anyone can do with them is
pause the switching, which is what they are for.

## Logging

- `LogFile` (default `%ProgramData%\DnsAutoSwitch\logs\DnsAutoSwitch.log`): one line per event, local
  time. The service writes and rotates it as SYSTEM, so its folder must be writable by administrators only;
  otherwise the service skips the file and says why in the Event Log.
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
dotnet test tests\DnsAutoSwitch.Tests       # Windows only
dotnet publish -c Release -o publish        # framework-dependent (needs the .NET 10 runtime)
dotnet publish -c Release --self-contained -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true -o publish   # single exe
```

Publish to a folder of your own, without elevation, then copy the result into the install folder from an
elevated prompt (the executable is locked while the service runs):

```powershell
Stop-Service DnsAutoSwitch
Copy-Item publish\* 'C:\Program Files\DnsAutoSwitch' -Recurse -Force
Start-Service DnsAutoSwitch
```

Copied files take the locked permissions of the folder. Do not publish straight into the install folder
from an elevated prompt: the whole build would run elevated in your source tree. `appsettings.json` is
never overwritten (a publish only carries `appsettings.example.json`). After an update, `check` should
still say `permissions: OK`.

CI: `.github/workflows/build.yml` builds and tests on GitHub (Windows runner) and attaches the
self-contained zip to releases on `v*` tags. `.gitea/workflows/build.yaml` does the same build on a self-hosted Gitea runner
(Linux container with the .NET SDK, cross-publishing win-x64); Gitea ignores the `.github` folder when
`.gitea/workflows` exists, and GitHub ignores `.gitea`.

| File | Content |
|---|---|
| `Config.fs` | `Settings`, defaults, `appsettings.json` loading and validation |
| `Security.fs` | permission checks (install folder, its parents, log folder) and the permissions `install` sets |
| `Dns.fs` | UDP probe (hand-built DNS packet), LAN adapters, `netsh`, cache flush, boot time |
| `Switch.fs` | one round: hold/pause, probe, desired state, changes. `tick settings dryRun` → `Skipped` / `Reconciled` |
| `FileLog.fs` | minimal `ILoggerProvider` writing to a file |
| `Worker.fs` | `BackgroundService` with a `PeriodicTimer`, logging transitions only |
| `Program.fs` | commands, `sc.exe` for install/uninstall, host setup |
| `tests/DnsAutoSwitch.Tests` | xUnit tests: permission checks, `install`'s locking on a temporary folder, configuration paths, hold parsing |

## License

MIT.
