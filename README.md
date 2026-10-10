# DnsAutoSwitch

Servizio Windows in F# (.NET 10) per la Beast (MyMain, `192.168.1.2`): tiene il DNS della LAN su
**AdGuard Home** (Ubuntu `192.168.1.202`) quando risponde, e sul **router** Plenitude (`192.168.1.1`) quando no.
Ogni minuto. Senza cloud, senza dipendenze dalla risoluzione dei nomi.

Contesto e storia: repo `hello-gitea`, [`DNS_AUTOSWITCH.md`](../hello-gitea/DNS_AUTOSWITCH.md) e
[`RESUME_DNS_TOGGLE_CHAT.md`](../hello-gitea/RESUME_DNS_TOGGLE_CHAT.md).

## Perché

- AdGuard gira su Ubuntu `.202`, che non è sempre acceso. Con il DNS su `.202` e Ubuntu spento la Beast è senza nomi.
- Un DNS secondario non è una soluzione: il resolver di Windows non fa failover deterministico e bypassa AdGuard
  a caso (`TROUBLESHOOTING_WIN11_BEAST.md`, Issue 3). Quindi **un solo server**, e qualcuno deve cambiarlo.
- Fino al 2026-10-10 quel qualcuno era l'utente: shortcut sul desktop dopo il boot, `Pre-Shutdown.ps1` allo spegnimento.
  Il buco: boot con Ubuntu spento dopo uno shutdown dal menu Start (che salta `Pre-Shutdown.ps1`), o Ubuntu che si
  spegne/riaccende durante il giorno. Il crash loop di Cloudflared del 2026-10-05 era esattamente questo caso.

## Cosa fa, un giro ogni 60 s

1. **Blocchi**: se esiste l'hold di `Pre-Shutdown.ps1` (valido) o il file di pausa, non fa nulla.
2. **Adapter**: solo quelli Up, con IPv4 in `192.168.1.x` e default gateway `192.168.1.1`.
   In pratica `Wi-Fi 2`. Esclude l'ICS (`Ethernet_Power_Side`, `192.168.137.1`) e gli adapter virtuali.
   Fuori dalla LAN di casa (gateway diverso) non tocca niente.
3. **Sonda**: una query DNS UDP diretta a `192.168.1.202:53` per `gitea.home` (rewrite di AdGuard: risponde da solo,
   niente Internet di mezzo). Timeout 1,5 s, due tentativi. Qualunque risposta con lo stesso ID = AdGuard vivo.
   La sonda è il **servizio DNS**, non Ubuntu: con `:22` aperto e AdGuard giù, il DNS va sul router.
4. **Ricontrollo dei blocchi** dopo la sonda, così un hold arrivato durante il giro viene rispettato.
5. **Stato desiderato**: AdGuard risponde → `192.168.1.202`, altrimenti → `192.168.1.1`.
   Per ogni adapter con DNS diverso da quello: `netsh interface ipv4 set dnsservers ... source=static address=<ip>`
   (un solo server, `validate=no`) e flush della cache (`DnsFlushResolverCache`).
6. **Log** solo delle transizioni (AdGuard raggiungibile/non raggiungibile, in attesa/ripresa) e dei cambi.

**Niente isteresi, per scelta**: durante un riavvio di Ubuntu il DNS va sul router e torna su AdGuard un minuto
dopo. È il comportamento corretto, non rumore.

## File

| Cosa | Dove |
|---|---|
| Binari (publish) | `C:\Tools\DnsAutoSwitch\DnsAutoSwitch.exe` + dll |
| Log | `C:\Tools\DnsAutoSwitch.log` (rotazione a 512 KB in `.log.1`) e Event Log *Application*, sorgente `DnsAutoSwitch` |
| Hold | `C:\Tools\DnsAutoSwitch.hold` — lo scrive `Pre-Shutdown.ps1` (data ISO). Vale solo se la data è **dopo il boot corrente** e da **meno di 30 min**; uno vecchio viene cancellato dal servizio stesso |
| Pausa manuale | `C:\Tools\DnsAutoSwitch.paused` — finché esiste il servizio non tocca il DNS (es. per usare gli shortcut a mano) |
| Costanti | [`Config.fs`](Config.fs): IP, nome della sonda, timeout, cadenza, percorsi. Si cambiano lì e si ricompila |

## Comandi

```text
DnsAutoSwitch [check|once|install|uninstall] [--adguard IP] [--router IP]
  (nessun argomento)  gira come servizio Windows (in console, se lanciato a mano)
  check               un giro in dry-run: stato e cosa farebbe
  once                un giro vero (prompt elevato)
  install             registra (Automatico, avvio ritardato, recovery restart/20 s) e avvia il servizio (prompt elevato)
  uninstall           ferma e rimuove il servizio (prompt elevato)
  --adguard/--router  override degli IP, per check/once
```

Esempio di `check` con Ubuntu spento e DNS già sul router:

```text
boot: 2026-10-10 20:26:09
hold: no   pausa: no
adapter [Wi-Fi 2] 192.168.1.2  DNS=192.168.1.1
AdGuard 192.168.1.202: NON raggiungibile
DNS gia' giusto, niente da cambiare.
```

Per simulare "AdGuard risponde" senza Ubuntu: `check --adguard 192.168.1.1` (il router risponde alla sonda).

## Build, deploy, aggiornamento

Serve l'SDK .NET 10 (installato il 2026-10-10 con `winget install Microsoft.DotNet.SDK.10`). Il publish è
framework-dependent: usa il runtime 10 in `C:\Program Files\dotnet`.

```powershell
dotnet publish -c Release -o C:\Tools\DnsAutoSwitch
```

Prima installazione, da prompt elevato:

```powershell
C:\Tools\DnsAutoSwitch\DnsAutoSwitch.exe install
```

Aggiornamento (l'exe è bloccato finché il servizio gira), da prompt elevato:

```powershell
Stop-Service DnsAutoSwitch
dotnet publish -c Release -o C:\Tools\DnsAutoSwitch
Start-Service DnsAutoSwitch
```

Rimozione: `DnsAutoSwitch.exe uninstall` (ferma e cancella il servizio; i file restano).

## Integrazione con `Pre-Shutdown.ps1`

Allo spegnimento (`chiudi.bat` → `Pre-Shutdown.ps1`, repo `hello-gitea`) il passo **H**, prima di toccare la rete,
scrive l'hold. Senza, tra il passo 1b (DNS → router) e il poweroff di Ubuntu il servizio rimetterebbe AdGuard,
e al boot successivo la Beast partirebbe con il DNS su `.202` spento. L'hold viene tolto se lo spegnimento si
annulla (exit 2) e scartato dal servizio al boot successivo (data precedente al boot).

## Struttura

| File | Contenuto |
|---|---|
| `Config.fs` | `Settings` e `defaults`: tutte le costanti |
| `Dns.fs` | sonda UDP (pacchetto DNS costruito a mano), adapter della LAN, `netsh`, flush, istante del boot |
| `Switch.fs` | un giro: blocchi, sonda, stato desiderato, cambi. `tick settings dryRun` → `Skipped` / `Reconciled` |
| `FileLog.fs` | `ILoggerProvider` minimale su file |
| `Worker.fs` | `BackgroundService`: `PeriodicTimer`, log delle sole transizioni |
| `Program.fs` | comandi, `sc.exe` per install/uninstall, host (`AddWindowsService`, Event Log da Information in su) |

## Verifiche (2026-10-10, primo giorno)

- `check` in dry-run: adapter, sonda e decisione corretti con Ubuntu spento; `--adguard 192.168.1.1` → "raggiungibile".
- Switch vero: DNS messo a mano su `.202` (spento), `once` lo ha riportato su `192.168.1.1`.
- `install`: `AUTO_START (DELAYED)`, recovery `RESTART` dopo 20 s, servizio `Running`, log su file con avvio e stato.
- Hold, hold stale e pausa contro il servizio in esecuzione: tutti e tre ok (dettaglio in `DNS_AUTOSWITCH.md`, repo `hello-gitea`).
- Ubuntu acceso con il servizio in esecuzione (22:00:43): "AdGuard 192.168.1.202 raggiungibile" e "[Wi-Fi 2] DNS 192.168.1.1 -> 192.168.1.202" nello stesso giro, senza shortcut.
