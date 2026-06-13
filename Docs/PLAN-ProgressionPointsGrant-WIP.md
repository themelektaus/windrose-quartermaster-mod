# Work In Progress: Progression-Punkte granten (Attribut + Talent)

Stand: 2026-06-13

## Status (Kurzfassung)

**Vertagt nach 6 fehlgeschlagenen Live-Memory-Ansaetzen.** Ziel war, im "Reward
Spawner" (ehem. "Item Spawner") zwei In-Game-Felder zu ergaenzen, die dem Spieler
**freie Attributpunkte** und **freie Talentpunkte** direkt gutschreiben (mit
**Minus-Werten** = abziehen), analog zum funktionierenden "Add XP"-Button.

- **Add XP funktioniert + bleibt** (eigener bewiesener Pfad, siehe
  `PLAN-XpForKills-DONE.md`: CDO-Klon von `R5ScenarioTask_AddExp` ->
  `FireConstructGrant`). Add XP gibt **indirekt** schon Punkte: XP -> Level-Ups ->
  Level-Up vergibt Attribut- + Talentpunkte (Talent erst ab Level 3).
- **Direkter Punkte-Grant zur Laufzeit: nicht gefunden.** Alle sechs Pfade waren
  off-path oder transient. Kein Crash, kein Datenverlust (jeder Schreibpfad war
  self-verifying gated - es wurde **nie** etwas Falsches geschrieben).
- **Aufgeraeumt + committet:** die nicht funktionierenden Felder + das komplette
  RE-/Recon-Geruest sind raus (Commits `f55474e` Core, `5656537` DLL). Der Reward
  Spawner ist auf **Items + XP** reduziert und lifecycle-sauber.

Dieses Doc ist die **Negativ-Chronik**: warum der Live-Write nicht geht, welche
Offsets/RVAs/Klassen wir kennen, und der empfohlene Weg, falls das Feature spaeter
doch gebaut wird.

## Ziel

Im Misc-Tab unter "Add XP" zwei weitere Sektionen:

- **"Attribute Points"** - Zahlenfeld (Default 1, **negativ erlaubt**) + Button.
- **"Talent Points"** - dito.

Klick -> der getippte Betrag wird auf den freien Pool des Spielers **addiert**
(negativ = abgezogen). Wie Add XP: Klick-Zeit-Read der Box (kein Polling),
Reopen-Persistenz, Lifecycle-Einbindung (Snapshot/Teardown wie alle Spawner-Latches).

Das **Frontend + die Verdrahtung waren komplett gebaut** (Row-Typen `attrCount`/
`talentCount`, Commands `add_attr_points`/`add_talent_points`, `LONG_MIN`-Sentinel-
Fix in `ReadCountBox`, damit ein getipptes `-1` nicht als "keine Box" missdeutet
wird). **Nur der eigentliche Write hatte kein funktionierendes Ziel** - genau das
ist das offene Problem.

## Was wir aus dem SDK wissen (Dumper-7, statisch sicher)

Der **Serialisierungs-Layout** der Progression (das, was der Save-Patcher sieht):

```
UR5BLPlayer (UObject)
  +0x210  PlayerMetadata        (FR5BLPlayerMetadata, ScriptStruct)
  +0x0E8    PlayerProgression   (FR5BLEntityProgression)
  +0x030      TalentTree        (FR5BLProgressionTree)
  +0x028        ProgressionPoints (int32)  -> UR5BLPlayer + 0x350  = Talentpunkte (frei)
  +0x070      StatTree          (FR5BLProgressionTree)
  +0x028        ProgressionPoints (int32)  -> UR5BLPlayer + 0x390  = Attributpunkte (frei)
  +0x040    PlayerName          (FString)  -> UR5BLPlayer + 0x40   (Marker-Kandidat)
```

Ziel-Command-Layout (struktureller Zwilling von AddExp, das wir erfolgreich treiben):

```cpp
FR5BLProgression_AddExp               { FR5BLRecordPath EntityProgressionPath @0x00 (0x18); int32 exp         @0x18; }  // 0x20
FR5BLProgression_AddProgressionPoints { FR5BLRecordPath ProgressionTreePath  @0x00 (0x18); int32 PointsCount @0x18; }  // 0x20
```

**Native Symbole vorhanden** (nicht exportiert, ueber RTTI-String-Xref -> `.pdata`
lokalisiert; preferred base `0x140000000`, **alle bei jedem Game-Update fragil**):

| Funktion | RVA |
|---|---|
| `R5BLProgression_AddProgressionPointsRule::Do_Impl` | `0x6F707B0` |
| `R5BLProgression_AddProgressionPointsRule::Can_Impl` | `0x6F59B50` |
| `R5BLEntityProgressionCntr::AddProgressionPoints` | `0x6F4BF70` |

**Warnung fuer Minus-Werte:** das Binary enthaelt die Asserts
`"FreeProgressionPoints >= 0"` und `"Locked progression points {} greater than
available progression points {}"`. Ein nativer Grant mit negativem `PointsCount`
wuerde den Pool vermutlich nur bis 0 senken (refund-artig) bzw. den Assert reissen.
Beim direkten int-Write ist Minus trivial (Ergebnis auf `>= 0` clampen).

## Die sechs Sackgassen (chronologisch)

| # | Ansatz | Was gemacht | Warum gescheitert |
|---|---|---|---|
| 1 | **Native-Rule-Hook** (`Do_Impl` + `EntityCntr::AddProgressionPoints`) | MinHook am ersten Byte der per `.pdata` lokalisierten RVAs, loggt `rcx/rdx/r8/r9` + Pointer-Hexdumps | **0 Treffer trotz bestaetigtem Level-Up mit Talentpunkt-Vergabe.** Die per Hand lokalisierten RVAs liegen **nicht** auf dem Punkte-Grant-Pfad. "installiert" != "richtige Funktion". |
| 2 | **Command-Bus-Capture** (BusCap) | Hook auf dem Bus-Dispatcher aus der XP-RE, loggt jeden Publish (cmdType + Hexdump) | Bus traegt nur **Notifications**: gefangen wurden `Inventory.Notification.AddExp` (Payload: Betrag @0x00, PlayerState-Ptr @0x38) + `Scenario.Notification.Hide`. **Kein** `AddProgressionPoints` - die Punkte feuern **keine** Notification. |
| 3 | **ProcessEvent-BP-Capture** (ProgCap) | Loggt jede progression-artige UFunction (`Progression`/`LevelUp`/`TalentPoint`/`ApplyPoint`/`Respec`) mit Objekt + Param-Hexdump | **0 Treffer.** Der Punkte-Grant ist eine rein **native** Business-Rule (`Do_Impl`) - kein BP, kein ProcessEvent, fuer beide Recon-Hooks unsichtbar. |
| 4 | **GObjects-Record-Write** (`R5BLPlayer`) | `FindFirstInstanceOfClass("R5BLPlayer")`, dann int-Write @ +0x390/+0x350 (SDK-Offsets), self-verifying before/after | `no live R5BLPlayer record yet` - die Klasse existiert in GObjects, aber **null lebende Instanzen**. `UR5BLPlayer` ist `final`, **kein Konsument haelt den Record direkt** (alle halten `UR5BLPlayerView`). Die BL-Records leben **nativ im BL-Registry, nicht als UObjects**. |
| 5 | **View-Bridge** (`R5BLPlayerView`) | `UR5BLViewBase` traegt 0x30 Bytes opaken nativen State @0x28-0x58; 6 Pointer-Slots x 2 Layout-Hypothesen, Marker-Validierung (PlayerName-FString @+0x40 druckbar + beide Pools 0..100000) | `live but no slot validates as the player record`. Der Record liegt **nicht direkt** hinter einem Slot. |
| 6a | **Pointer-Chase (BFS)** | Breitensuche durch heap-artige Qwords ab den 6 Slots (Tiefe 3, max 96 Knoten), jeder Knoten gegen die Marker-Validierung | Keine Validierung. Aber die Slot-Dumps verrieten die **Architektur**: `+0x28` beginnt mit ASCII `R5BLPlayer` + size=10 + capacity=15 = **MSVC `std::string` (SSO)** = Registry-Eintrag (Typname->Record); `+0x30`/`+0x50` Erst-Qword = **Image-Pointer** (`0x7FF6...`) = Vtables = **native C++-Objekte, keine UObjects**. -> Der Record im Speicher hat **nicht das SDK-Layout** (das beschreibt nur die Save-Form). Darum konnte die Marker-Validierung prinzipbedingt nie greifen. |
| 6b | **Value-Needle-Scan** (ProgScan) | `R5BLPlayer`-Registry-Eintrag als sicheren Anker erkannt, Kandidaten 0x400 Bytes gedumpt, getippten Punktestand als Suchnadel durch die Dumps gescannt | Nadel "8" (Attribut) traf bei `+0x340` **und** `+0x3D0` (beide in baumartigen Strukturen, mehrdeutig); Nadel "2" (Talent) **0 Treffer** (Pool ausserhalb des Fensters). Kleine Zahlen aliasen zu stark. |
| 6c | **Differential-Snapshot** (ProgDiff) | Baseline-Snapshots aller Record-Regionen (0x800), Folge-Klick loggt int32-Diffs nach genau 1 ausgegebenem Punkt | **Zu verrauscht.** Nur ein doppelt gepufferter **Timer** (`+0x094/+0x098`, +~700/Fenster) und UI-Navigations-Rauschen aenderten sich. **Kein** int ging sauber um 1 runter. |
| 6d | **Reflektierte VM-Getter** (ProgVM, read-only) | `R5HFSM{Stat,Talent}TreeComponent` -> `Get{Stat,Talent}TreeVM()` -> `GetFree*Points()` per ProcessEvent; `FindAllInstancesOfClass` (alle Instanzen) | Persistente **Komponenten**: `0 live instances` (existieren zur Laufzeit nicht als UObjects). Die **VMs** leben (4x StatTreeVM, 2x TalentTreeVM), aber **alle** liefern `free=0 avail=0`, obwohl real 8/2. Die VMs sind **transiente UI-Spiegel**, die nur den Wert halten, *waehrend der Charakter-/Talent-Screen gebunden offen ist* - geschlossen lesen sie sauber 0. |

## Schlussfolgerung

Der **echte freie-Punkte-Wert** lebt im **nativen BL-Registry-Record** - plain
modernes C++ (`std::string`, Vtables), **ausserhalb GObjects**, **nicht im
SDK-Layout** (das SDK-Layout ist nur die Serialisierungsform fuer den Save). Die
reflektierten VMs sind kein Anker (transient, lesen 0 wenn der Screen zu ist).
**Es gibt keinen reflektierten Granter** - nur Getter + Spend/Reset (und die Spend-
Rule laeuft ueber datengetriebenen Dispatch, nicht vtable-virtuell, vgl. die
gleiche Sackgasse beim AddExp-Rule-Diff in `PLAN-XpForKills-DONE.md`).

Ein Live-Write *waere* prinzipiell moeglich, braeuchte aber:
- entweder den **echten** `Do_Impl`/`EntityCntr::AddProgressionPoints`-Pfad per
  Ghidra **dekompiliert** (statt RVA-Raten) - analog zur AddExp-RE, die ueber den
  Execute-vtable-Slot 101 + Gate-Diff erfolgreich war. Der Punkte-Pfad hat keinen
  so sauberen vtable-Anker gefunden.
- oder die **Registry-Walk-Logik** (Typname-`std::string` -> Record) komplett
  reverse-engineered, um den nativen Record-Pointer + sein echtes Memory-Layout
  (nicht das SDK-Save-Layout) zu bestimmen. Tiefes Kaninchenloch.

## Empfohlener Weg, falls spaeter doch gebaut (Option B)

**Save-Patcher statt Live-Write.** `Tools/QuartermasterCore/ProgressionSaveSlotsPatcher.cs`
existiert bereits und schreibt `StatTree.ProgressionPoints` /
`TalentTree.ProgressionPoints` als int32 **in-place** in den Save - bewiesen an
echten Charakteren. Das ist der **eine garantierte Pfad**.

- Als **GUI-Funktion** anbinden (nicht DLL): Spiel zu, Charakter gewaehlt, Steam-
  Cloud-Sync aus. Minus-Werte trivial (int-Write, auf `>= 0` clampen).
- Trade-off vs. das urspruengliche Ziel: **kein** In-Game-Live-Button mehr, sondern
  ein Offline-Patch ueber den Configurator. Dafuer zuverlaessig + update-fest
  (kein RVA-Geraet).

Alternativ bleibt **Add XP** der pragmatische Ersatz: Punkte kommen ueber Level-Ups
(Talent ab Level 3) - schon heute funktional, nur an den Level-Fortschritt gekoppelt.

## Was committet wurde (Cleanup)

| Commit | Inhalt |
|---|---|
| `b929d80` `feat(core,web)` | Rename auf "Reward Spawner" (Card, Summary, Kommentare; JSON-Key + Dateiname bleiben kompatibel) + Template-Sektionen XP/Attribute/Talent (**interim**) |
| `bf9cc64` `feat(dll)` | Add-XP-Grant + Punkte-Felder (Live-Write-Versuch) + sentinel-gated Recon (BusCap/ProgCap/ProgRule) (**interim**) |
| `f55474e` `refactor(core)` | Attribute/Talent-Sektionen aus dem Template raus (Items + XP + Rename bleiben) |
| `5656537` `refactor(dll)` | Punkte-Felder + komplettes RE-Recon-Geruest raus: `attrCount`/`talentCount`-Rows, Dispatch, Latches, `GrantProgressionPoints`, `ReconProgressionVM`, `FindAllInstancesOfClass`, BusCap/ProgCap (`KX_PROGCAP`, `g_buscapWanted`, `NameIsProgressionish`), alle Decode-Helfer |

**Unangetastet + verifiziert geblieben:** Add-XP, Item-Spawner (Kategorie/Suche/
Anzahl), Kill-Detection, die Shared-Count-Box-Infra (`AddCountBoxRow`,
`ReadCountBox`/`LONG_MIN`). DLL release 462 KB, Core 0 Fehler.

## Dateien / Anker (fuer einen spaeteren Anlauf)

| Was | Pfad |
|---|---|
| Save-Patcher (Option B, existiert) | `Tools/QuartermasterCore/ProgressionSaveSlotsPatcher.cs` |
| XP-Grant-Vorbild (bewiesener nativer Pfad) | `Tools/DllProxy/dxgi/qm_killxp.cpp::FireConstructGrant` + `PLAN-XpForKills-DONE.md` |
| Reward-Spawner-Template | `Tools/QuartermasterCore/.../GameDeployer.cs` (Sektion "Add XP" ist jetzt der letzte Block) |
| Reward-Spawner-Card (Frontend) | `GUI/Web/wwwroot/.../misc.html` + `ProfileSummary.cs` |

> Bei einem neuen Anlauf: **nicht** wieder Live-Memory raten. Entweder Ghidra-RE
> des nativen `AddProgressionPoints`-Pfads (saubere Dekompilierung, kein RVA-Raten),
> oder direkt Option B (Save-Patcher als GUI-Funktion).
