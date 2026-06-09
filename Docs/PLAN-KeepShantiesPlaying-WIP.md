# Work In Progress: Keep Shanties Playing (DLL-Hook)

Stand: 2026-06-09

## Status (Kurzfassung)

**Singleplayer funktioniert + ist in-game verifiziert:** der Crew-Shanty hoert
nicht mehr auf, wenn man das Steuer verlaesst. B startet/stoppt am Steuer wie
vanilla. Implementiert in `qm_shanty.cpp::QmShanty_OnProcessEvent`, verdrahtet an
den globalen `ProcessEvent`-Net-Hook. DLL committet (`aa32512`), Configurator-
Integration gebaut (Misc-Card-Toggle "Keep Shanties Playing", profil-bezogene
Sentinel `qm_shanty_<profil>.txt`).

**Multiplayer ist NICHT abgedeckt (dieses Doc).** Das aktuelle Feature ist rein
singleplayer-tauglich gebaut und getestet; in MP hat der Suppress-Ansatz konkrete
Bruchstellen (siehe unten). Die Misc-Card traegt deshalb einen "Singleplayer
only"-Hinweis. MP ist ein eigener, noch nicht begonnener Workstream.

## Ziel (Minimalfeature)

Nur verhindern, dass der Shanty beim Verlassen des Steuers aufhoert - nicht mehr,
nicht weniger. Kein N/Next, kein Remote-Stop, kein Auto-Next. Vanilla: am Steuer
`B` = Start/Stop, Steuer verlassen = Stop.

## Wie der Singleplayer-Weg funktioniert (recon-bestaetigt)

Drei Signale laufen alle ueber den globalen `ProcessEvent`-Net-Hook:

| Signal | UFunction | Bedeutung |
|---|---|---|
| Helm-B-Toggle | `InpActEvt_IA_ShipToggleShanty*` (auf Helm-UI) | Spieler-Intent, feuert <100ms VOR dem folgenden Enable/Disable |
| Start | `ServerEnableShanty` (auf Audio-Komponente) | Shanty an (nur am Steuer) |
| Stop | `ServerDisableShanty` (auf Audio-Komponente) | feuert SOWOHL bei B-Stop am Steuer ALS AUCH beim Steuer-Verlassen |

**Diskriminierung (offset-frei):** Der Tick des letzten Toggle-Inputs wird gemerkt;
ein Enable (Start) oder ein manueller Disable CONSUMET ihn. Ein `ServerDisableShanty`
ohne frischen (unverbrauchten) Toggle-Input, auf der Komponente die wir starten
sahen, ist ein Helm-Verlassen -> der Net-Hook leitet das Original-`ProcessEvent`
NICHT weiter, der Disable laeuft nie, der Shanty spielt weiter. Keine Property-
Reads, kein Re-Play.

Recon-Beweis (drei Szenarien aus `Quartermaster_Inject.log`):
- B-Stop am Steuer: `ServerDisableShanty` ~78ms NACH einem `IA_ShipToggleShanty`.
- Steuer verlassen: `ServerDisableShanty` OHNE vorausgehenden Toggle-Input.
- `ToggleShanty` wurde nie gerufen - das echte Helm-Signal ist der Enhanced-Input-Event.

## Warum der SP-Weg in Multiplayer bricht

Der Suppress-Trick haengt an zwei SP-Annahmen, die in MP nicht mehr halten:

1. **Disziplinierung ueber lokalen Input.** "B-Stop vs. Helm-Verlassen" wird rein
   ueber das LOKALE `IA_ShipToggleShanty`-Enhanced-Input-Event unterschieden. In MP
   schickt ein anderer Spieler `ServerDisableShanty` an die Authority (Host); auf
   deiner Maschine feuert dafuer KEIN lokaler Toggle-Input -> unsere Logik liest
   "kein frischer Input" -> sie wuerde den manuellen B-Stop des Fremdspielers
   FAELSCHLICH als Helm-Verlassen einstufen und unterdruecken.

2. **Ein einzelner globaler Aktiv-Pointer.** `g_activeComponent` ist EIN globaler
   Pointer auf "die" Audio-Komponente. Mehrere Schiffe / mehrere Spieler haben
   mehrere `R5ShipAudioComponent`-Instanzen gleichzeitig -> sie ueberschreiben sich.
   In MP braucht es Per-Komponenten-State (keyed pro Komponente), nicht ein Global.

3. **Replikation statt RPC-Suppression.** In MP laeuft Start/Stop NICHT nur ueber die
   `Server*`-Calls, sondern wird ueber `OnRep_Shanty` repliziert. Ein "Original nicht
   weiterleiten" greift dann gar nicht am richtigen Pfad - der replizierte State
   stoppt das Audio trotzdem. Der MP-Weg muss das Audio aktiv RE-PLAYEN (wie die
   Referenz-Mod), nicht den Disable unterdruecken.

Starkes Indiz: die Referenz-Mod liefert **zwei getrennte Versionen (SP/MP)**.

## Was die MP-Referenz-Mod zusaetzlich macht (`References/AlwaysShanties-Multiplayer`)

UE4SS-Lua, dient nur als Blaupause (read-only, wird nie integriert). Relevante
MP-Mechanik fuer einen kuenftigen QM-MP-Weg:

- **`OnRep_Shanty`-Hook** als primaerer MP-Pfad (statt nur `Server*`): erkennt
  replizierte Start/Stop und re-played das Audio via `ShantyAudio:Play(elapsed)`.
- **Ownership-Guards** (`requireOwnedShipForPersistent`): prueft ueber die lokale
  `ShipownerComponent` (`IsOwningShip` / `GetEquippedShipId`), ob die Komponente zum
  EIGENEN Schiff gehoert - nur dann keep-alive. Sonst wuerden Fremd-Schiffe
  mitgehalten/unterdrueckt.
- **Per-Komponenten-State-Table** (`states[key]`, keyed via `GetFullName`), kein
  globaler Aktiv-Pointer.
- **Recent-B-Intent-Window**, um replizierte Starts von lokalem Spieler-Intent zu
  trennen (`requireRecentBForOnRepStart`).
- Property-Reads `ShantyIdx` / `ShantyAudio` (fuer Re-Play). Die braeuchte unser
  C++-Weg per Offset (Dumper-7-Lauf), die das SP-Suppress bewusst vermeidet.

## MP-Plan (noch nicht begonnen)

1. **Eigener MP-Recon-Lauf in einer echten MP-Session** (Host + Client, eigenes +
   fremdes Schiff): protokollieren, welche UFunctions/`OnRep_Shanty` mit welcher
   Authority feuern und ob das Audio ueber Disable oder ueber den replizierten State
   stoppt. Erst danach steht der Weg fest (Suppress reicht vermutlich nicht ->
   Re-Play).
2. **Property-Offsets** via Dumper-7: `R5ShipAudioComponent.ShantyIdx`,
   `.ShantyAudio` (fuer Re-Play). Pro Game-Update neu (Steam-Update-Recovery).
3. **Ownership-Guard in C++**: lokale `ShipownerComponent` aufloesen + Ship-ID der
   Komponente vergleichen (`IsOwningShip`/`GetEquippedShipId`-Aequivalent ueber
   Reflection). Nur das eigene Schiff keep-alive.
4. **Per-Komponenten-State** statt `g_activeComponent` (kleine Map Component* ->
   State), damit mehrere Schiffe/Spieler sich nicht ueberschreiben.
5. **Re-Play statt Suppress** auf dem `OnRep_Shanty`-Pfad (Audio mit `elapsed`
   wieder anwerfen), falls der MP-Recon bestaetigt, dass Suppression allein nicht
   reicht.
6. **Configurator-Anbindung**: ggf. ein separater Toggle / Modus-Hinweis, sobald
   MP gebaut + verifiziert ist. Bis dahin bleibt der "Singleplayer only"-Hinweis
   in der Card stehen.

## Offen / verbleibend

- Der gesamte MP-Plan oben (Punkte 1-6) ist NICHT begonnen.
- Frontend-Integration des SP-Features committen (Misc-Card-Toggle + Profil/Deploy/
  Pipeline-Verdrahtung; DLL selbst ist als `aa32512` committet).
