/// Costanti del servizio: IP, sonda, file di stato. Si cambiano qui e si ricompila.
module DnsAutoSwitch.Config

open System
open System.Net

type Settings =
    { /// DNS in modalita' normale: AdGuard Home su Ubuntu .202 (filtering + nomi .home custom)
      AdGuard: IPAddress
      /// DNS in modalita' degraded: router Plenitude (nomi .home solo DHCP-based, niente filtering)
      Router: IPAddress
      /// Guardia: si agisce solo se il default gateway e' questo (LAN di casa, non un'altra 192.168.1.x)
      Gateway: IPAddress
      /// Adapter da toccare: IPv4 che inizia cosi' (esclude ICS 192.168.137.x e adapter virtuali)
      LanPrefix: string
      /// Nome che AdGuard risolve da solo (rewrite): la sonda non dipende da Internet
      ProbeName: string
      ProbeTimeout: TimeSpan
      ProbeAttempts: int
      /// Cadenza del controllo
      Interval: TimeSpan
      /// Alzato da Pre-Shutdown.ps1 (contiene la data ISO di scrittura): finche' vale, il DNS non si tocca
      HoldFile: string
      /// Un hold vale solo se scritto dopo il boot corrente e da meno di questo tempo
      HoldMaxAge: TimeSpan
      /// Pausa manuale: finche' il file esiste, il DNS non si tocca
      PauseFile: string
      LogFile: string }

let defaults =
    { AdGuard = IPAddress.Parse "192.168.1.202"
      Router = IPAddress.Parse "192.168.1.1"
      Gateway = IPAddress.Parse "192.168.1.1"
      LanPrefix = "192.168.1."
      ProbeName = "gitea.home"
      ProbeTimeout = TimeSpan.FromSeconds 1.5
      ProbeAttempts = 2
      Interval = TimeSpan.FromSeconds 60.
      HoldFile = @"C:\Tools\DnsAutoSwitch.hold"
      HoldMaxAge = TimeSpan.FromMinutes 30.
      PauseFile = @"C:\Tools\DnsAutoSwitch.paused"
      LogFile = @"C:\Tools\DnsAutoSwitch.log" }
