# Work In Progress: Camp-Wide Deposit (DLL-Hook) - PARKED

Stand: 2026-06-14

## Status (Kurzfassung)

**Geparkt ("lassen wir grundsaetzlich fuers erste").** Das Feature ist NICHT
fertig. Camp-weites Deponieren ueber den Pfad, den der User wirklich benutzt
(Inventar-Button "Stack All"), ist mit allen reflektierten Mitteln bewiesen
NICHT erreichbar. Der einzige verbleibende Weg ist ein nativer Decompile +
Patch (Option B, siehe unten) - bewusst zurueckgestellt.

**Was funktioniert:** Single-Chest-Deposit (vanilla, unveraendert). Der
MoveAll-exec-Hook sitzt korrekt auf dem echten Stack-All-Verb und feuert
zuverlaessig - aber der camp-weite Multipass darauf deponiert nur in die offene
Kiste, nicht in die Nachbarkisten.

**Was im Code liegt (DLL, NICHT committet, reiner Test-/Recon-Stand):**
`qm_deposit.cpp` / `qm_deposit.hpp`. Enthaelt mehrere Recon-/Versuchs-Pfade
(camp-scan, swap-proof, native getter+F-Multipass, MoveAll-exec-Multipass,
Always-On-Breadcrumb). Armed via Sentinel `qm_deposit*.txt` im Sidecar.

## Ziel (Minimalfeature)

Wie die Nexus "Camp-Deposit"-Mod: Ein Deposit auf EINE Camp-Kiste wird auf ALLE
Camp-Kisten wiederholt - jede Kiste bekommt die Items, die mit ihrem Inhalt
stacken (vanilla "Deposit Similar", camp-weit angewandt). Kein Loeschen, Items
bleiben abrufbar.

## Der entscheidende Befund: zwei verschiedene "Deposit"-Mechaniken

In Windrose gibt es ZWEI getrennte Wege zu deponieren, und sie laufen ueber
voellig verschiedene Maschinerie:

| Mechanik | Trigger | Pfad | Wer benutzt es |
|---|---|---|---|
| **Radiale Welt-Aktion** "Deposit Similar" | GAS-Ability `GA_InteractOption_DepositSimilar_C` | nativer GAS-Transfer (`F` @ RVA `0x08b0A0A0`) | **Die Referenz-Mod augmentiert DIESEN Pfad** |
| **Inventar-Button "Stack All"** | `WBP_InventoryDefaultAndAction_Panel_C::HandleStackAllButtonClick` | reflektiertes `R5DefaultInventoryVM::MoveAll(tag, bOnlyStack)` -> nativer Exec @ RVA `+0x8976260` | **Der User benutzt AUSSCHLIESSLICH diesen** |

**Das ist die Wurzel des ganzen Problems.** Die Referenz-Mod und der User sind
auf verschiedenen Pfaden. Ueber alle Test-Sessions hinweg hat der User immer
`HandleStackAllButtonClick` benutzt (im Log nachgewiesen: 00:14, 14:27, 21:07);
die radiale GAS-Aktion `GA_InteractOption_DepositSimilar_C` ist in der
Transfer-Rolle KEIN EINZIGES Mal durch den Hook gefeuert. Die Referenz-Mod
wuerde - so wie sie ist - beim Workflow des Users gar nichts tun.

## Beweis-Kette: alle reflektierten Pfade sind tot (3 harte Negativbefunde)

Jeder dieser Pfade wurde mit einem in-game-Test widerlegt, nicht geraten:

### 1. HandledInventories-Slot-Swap (widerlegt)
**Hypothese:** `MoveAll` liest sein Ziel aus dem Nicht-Source-View in der
gebundenen VM (`HandledInventories` TSet). Swap diesen Slot auf eine Nachbarkiste.
**Test (`QmDeposit_SwapProof`):** Gate `CanMoveAll` AKZEPTIERTE den getauschten
Slot (`canMoveAll=yes`), der Write ging durch, `MoveAll` feuerte - **trotzdem
landeten die Items in der offenen Kiste.**
**Schluss:** `MoveAll` liest den `HandledInventories`-Slot NICHT als Routing.
Der Slot ist Anzeige/Buchhaltung. `CanMoveAll` prueft nur "kann die Quelle alles
abgeben", nicht "wohin".

### 2. ProcessEvent-Replay des GAS-Deposits (widerlegt: nativ)
**Hypothese:** Den DepositSimilar-UFunction-Call mitschneiden und pro Nachbarkiste
mit getauschtem `TargetModel @ 0x3E8` neu feuern.
**Test (GAS-Ring-Recon):** Ueber eine ganze Session kreuzt **KEIN**
`DepositSimilar`/`MoveAll`/`Transfer`/`AddItem`/`MoveItem` UFunction den
ProcessEvent-Net-Hook - nur die Folge-Notifications
(`OnInventoryViewChanged`/`OnStorageComponentChanged`) blubbern hoch.
**Schluss:** Der Transfer laeuft NATIV (GAS-native Execution / native ExecFunction),
es gibt keinen reflektierten UFunction zum Abfangen oder Replayen.

### 3. VM-MoveAll-Replay ueber alle Camp-VMs (widerlegt)
**Hypothese:** Beim echten Stack-All (volle UI-Bindung) den nativen MoveAll-exec
abfangen und reflektiertes `MoveAll(similar)` auf jeder anderen gebundenen
Camp-VM neu feuern.
**Test (`RunMoveAllCampWide`, 21:07-Log):** MoveAll-exec-Hook feuerte exakt beim
Stack-All (Zeile 538). 5 VMs gesehen, 4 Nachbarn - **alle 4 melden
`canMoveAll(similar)=no`** auf beiden Containern (`Inventory.InventoryContainer.Left/Right`
= Spieler-Inventar). 0 Deposits gefeuert, 8 Tags ausgegated. Nur die EINE offene
Kiste (Origin-VM) ist im Zustand, in dem `MoveAll` deponiert.
**Schluss:** Der reflektierte `MoveAll` einer anderen VM lenkt NICHT auf deren
Kiste um. Das Deposit-Ziel haengt an UI-/nativem State, nicht an der VM, die wir
reflektiert ansprechen.

## SDK-Recon: warum das so ist (Datenmodell)

| Befund | Konsequenz |
|---|---|
| `UR5BaseInventoryVM` (0xF0) hat **kein** Ziel-Feld - nur `UIInventoryContainers` (Quelle), `HandledInventories` (Views), `PersonalChestObserver` | `MoveAll`s Ziel steckt NICHT im VM, sondern im `FR5BLRecordPath` *in* der InventoryView, nativ ueber die R5BusinessRules-Engine aufgeloest. Deshalb war der Slot-Swap wirkungslos. |
| Engine-Primitiv `FR5BLInventory_ActionMoveItemsBetweenInventories` hat `TArray<FR5BLRecordPath> TargetInventoriesPaths` | **Multi-Target by design** - die Engine kann in EINEM Call in mehrere Kisten deponieren. Das ist der eigentliche Hebel. |
| Die Rule-Klassen, die diese Action konsumieren, haben **0 UFunctions** (reine native Prozessoren) | **Kein reflektierter Shortcut.** Die Multi-Target-Action ist nur NATIV ansprechbar. |
| `MoveAll` wird aus Blueprint ueber seine native ExecFunction gerufen (nicht ueber ProcessEvent) | **Deshalb** sah der PE-Net-Hook `MoveAll` nie. Der einzige Weg, den echten Deposit zu beobachten/augmentieren, ist die ExecFunction direkt zu hooken (was wir jetzt tun). |
| Camp-Storage-Aggregat (`UR5BuildingCenterStorageComponent` / `UR5ProximityStorageComponent`) kam zur Laufzeit als Modul-Region-Pointer (`0x7FF4...` = CDO/Permanent-Pool), `InventoryViews=0`, `PlayerInventoryView=null` | Die saubere "alle Camp-Kisten aus dem Aggregator"-Liste gibt es ueber diese Komponenten NICHT zur Laufzeit. Kisten muessen ueber GObjects (`AR5LootableInventoryBox`-Actors) enumeriert werden. |

## Native Fakten (verifiziert, fuer den verbleibenden Weg)

### Reference-Mod Mechanismus (hash-gematcht auf UNSEREN Build `client 2a4f36e9`)
Aus der hardcoded Per-Hash-Tabelle der Referenz-DLL (Ghidra-Dumps der `main.dll`,
ROW0). Die Tabelle speichert NUR die drei Site-RVAs, KEINE Byte-Signaturen.

| Site | RVA | Original-Bytes | Rolle |
|---|---|---|---|
| site1 (body) | `0x08b08a6b` | `E9 30 16 00 00` = `jmp +0x1630` | Tail-Call in Deposit-Dispatcher **F @ `0x08b0A0A0`** |
| site2 (getter call) | `0x08b0a0b8` | `E8 E3 AD BB 00` = `call` | ruft Getter @ `0x096c4ea0` |
| site3 (getter entry) | `0x096c4ea0` | live: `40 53 48 83 EC 20 48 8B...` (`push rbx; sub rsp,0x20; ...`, Nicht-Leaf!) | der Deposit-Target-Getter (liest `[rcx+0x3E8]`) |

**Wichtige Korrektur (fuer kuenftige Iterationen):** Die fruehe Annahme, der
Getter sei ein triviales Leaf `48 8B 81 E8 03 00 00 C3` (`mov rax,[rcx+0x3E8]; ret`),
war FALSCH. Die echten Bytes bei `0x096c4ea0` sind ein echter Funktions-Prolog.
Das hat einen Build-Zyklus gekostet (Getter-Signatur-Mismatch -> Hook installierte
nicht).

**Reference-Retarget:** Getter intercepten (objekt-agnostisch) -> staged den
**Kisten-Actor selbst** (live `UObject*`, gefiltert auf
`r5lootableinventorybox`/`r5buildingblock`/`bp_storage_`, nur auf Lebendigkeit
validiert, NICHT transformiert) in `ctx[8]` -> `F` pro Nachbarkiste neu feuern ->
`ctx[8]=0`. Der Deposit-Body akzeptiert einen Kisten-Actor an Stelle des
`R5InteractionTargetModel`.

### Der echte User-Pfad (Stack All)
| Was | Wert |
|---|---|
| Verb | `R5DefaultInventoryVM::MoveAll` (native ExecFunction) |
| Exec-RVA | `+0x8976260` (live: `exec=0x...CD6C6260`, base `0x...C4D50000`) |
| Aufloesung | per Name zur Laufzeit (`FindClassByName("R5DefaultInventoryVM")` -> `FindFunctionOnClass(..., "MoveAll")` -> ExecFn), nicht hardcoded |
| Capture verifiziert | 21:07-Log Zeile 538: `MoveAll exec FIRED (outermost)` beim Stack-All |

## Verbleibender Weg (Option B - bewusst zurueckgestellt)

**Native Decompile + Multi-Target-Erweiterung.** Der einzige Pfad, der den ECHTEN
Stack-All-Verb des Users camp-weit macht:

1. **Game-Binary in Ghidra importieren** (bisher liegt nur die Referenz-DLL
   `main.dll` im Ghidra-Projekt, NICHT `Windrose-Win64-Shipping.exe`).
2. **MoveAll-exec @ `+0x8976260` dekompilieren:** Wie loest es seine Ziel-Kiste
   auf? Wo baut es die `FR5BLInventory_ActionMoveItems`-Action mit
   `TargetInventoriesPaths`?
3. **Nativ erweitern:** `TargetInventoriesPaths` von `[offene Kiste]` auf
   `[alle Camp-Kisten]` (aus GObjects `AR5LootableInventoryBox`-Enumeration). Ein
   nativer Call, engine-eigenes Multi-Target-Moving - genau das `IDepositBackend`-
   Konzept, das auch die Referenz-Mod nutzt.

**Aufwand/Risiko:** Schwer (grosse Binary, tiefe RE), pro Game-Update neu (der
User hat den Patch-Aufwand explizit akzeptiert). Erster Write auf echte Items -
Item-Safety ist die Failure-Mode, daher mit Test-Items absichern.

**Alternative (billiger, ungetestet):** Radial-Test - der reference-treue
F-Multipass-Hook (getter+F @ `0x096c4ea0`/`0x08b0A0A0`) ist bereits gebaut und
installiert. Falls die radiale "Deposit Similar"-Weltaktion im Spiel des Users
existiert, koennte sie sofort camp-weit funktionieren. Wurde nie sauber getestet,
weil der User immer Stack-All statt der Radial-Aktion benutzt. Wert: 1 Test-Runde
ohne Build.

## Praktische Lektionen (fuer den Wiedereinstieg)

- **Log wird pro Start ROTIERT, nicht geloescht.** Vorige Session landet als
  `Quartermaster_Inject_<timestamp>.log` neben dem Live-Log. Nichts geht verloren.
  ABER: Eine kurze Genlandia/Szenario-Session (keine Camp-Kisten) kann das
  Live-Log fuellen und den eigentlichen Deposit-Test verdraengen. Beim Test:
  in die Welt MIT den Kisten laden, deponieren, komplett zum Desktop beenden,
  NICHT neu starten vor dem Log-Lesen.
- **Storage-Notifications als Ground-Truth:** `OnInventoryViewChanged` +
  `OnStorageComponentChanged` feuern bei JEDEM echten Deposit durch den
  PE-Net-Hook (unabhaengig von unseren Hooks). Fehlen sie komplett -> in der
  Session fand gar kein Deposit statt (kein Hook-Fehler).
- **Always-On-Breadcrumb** (`DF_NOTIFY`) loggt jeden Deposit/Storage-Event
  AUCH bei aktivem Native-Pfad (der sonst im PE-Hook frueh zurueckkehrt). Das hat
  den Fehlermodus eindeutig offengelegt: Breadcrumb mit `HandleStackAllButtonClick`,
  aber ohne `body-dispatcher F entered` -> User deponiert via Stack-All, nicht via
  Radial-GAS.
- **Hooks selbst-validieren** gegen Byte-Signaturen: ein Game-Update, das Code
  verschiebt, laesst die Hooks schlicht nicht installieren (kein Crash).

## Code-Stand (`qm_deposit.cpp`, was wo liegt)

| Funktion | Zweck | Status |
|---|---|---|
| `QmDeposit_OnProcessEvent` | PE-Net-Hook-Einstieg: Breadcrumb + (frueher) Recon + Native-Install-Trigger | aktiv |
| `QmDeposit_QuickDeposit` | reflektierter `MoveAll`-Multipass ueber alle VMs (INSERT-Trigger) | tot (Nachbarn gaten aus) |
| `QmDeposit_CampScan` | read-only View-Topologie-Dump | Recon, erledigt |
| `QmDeposit_SwapProof` | HandledInventories-Slot-Swap-Experiment | widerlegt |
| `QmDeposit_EnsureNativeInstalled` | MinHook auf getter(`0x096c4ea0`)+F(`0x08b0A0A0`)+MoveAll-exec(`+0x8976260`) | installiert; F-Pfad ungenutzt (User benutzt Stack-All), MoveAll-Multipass tot |
| `RunMoveAllCampWide` | MoveAll-exec-getriebener camp-weiter Multipass | tot (alle Nachbarn `canMoveAll=no`) |

Sentinel: `qm_deposit_recon.txt` (dev/manuell) bzw. `qm_deposit_<profile>.txt`.
Kein Frontend/Configurator-Anbindung (das Feature ist nie ueber Recon
hinausgekommen). Kein Commit.

## Offen / verbleibend

- Option B (nativer Decompile + Multi-Target-Erweiterung) - NICHT begonnen.
- Optional: 1 Radial-Test-Runde (reference-treuer F-Multipass ist schon
  installiert), um zu klaeren ob die radiale Aktion existiert.
- Aufraeumen: Falls das Feature endgueltig fallen gelassen wird, sollten die
  Recon-/Versuchs-Pfade aus `qm_deposit.cpp` zurueckgebaut werden (aktuell
  ~1400 Zeilen reiner Test-Code, nicht committet).
