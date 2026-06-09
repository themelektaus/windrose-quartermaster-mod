# Done: XP for Kills (DLL-Hook)

Stand: 2026-06-09

## Status (Kurzfassung)

**Kernfeature funktioniert + ist in-game verifiziert (persistent):** jeder
Gegner-Kill grantet XP ueber die genuine Engine-Logik (echtes Level-Up + Punkte +
Notification + Save), seed-frei (kein POI noetig). Implementiert in
`qm_killxp.cpp::FireConstructGrant`, verdrahtet an `OnPawnEnemyDead`.

**Per-Gegner-XP + Configurator-Integration erledigt** (Commits `eec6555`,
`d9d4d5e`): XP-Werte sind pro Gegner-Klasse konfigurierbar (Keyword/Substring,
laengster Match gewinnt), Default 0 = "vanilla" (kein Grant). Trigger: profil-
bezogene Sentinel `qm_killxp_onkill_<profil>.txt` (Format `key=value` + `default=N`),
**einmalig beim Start** gelesen. Im Configurator: Misc-Card-Slider (Default-XP
0-100) + eigener "XP for Kills"-Tab (Keyword-Tabelle aus dem Vanilla-Pak).

**End-to-end verifiziert (2026-06-09):** der GUI-generierte Config wurde in-game
bestaetigt - eigenes Profil, eigene Per-Gegner-Werte im "XP for Kills"-Tab, Build,
Play; Keyword-Matching ueber die profil-bezogene `qm_killxp_onkill_<profil>.txt`
greift wie erwartet. Damit ist das Feature vollstaendig (DLL + Configurator-
Integration + Docs) - dieses Doc ist abgeschlossen.

Der Rest dieses Dokuments ist die RE-Chronik (wichtig fuer Game-Update-Recovery,
da alle RVAs/Offsets pro Update driften). Die historischen Phasen 2/2b/2c sind
**ueberholt** und nur als Lernpfad erhalten; der gebaute Weg steht unter
**"Phase 3"** + **"Phase 4"**.

## Ziel

Der Spieler soll fuer das Toeten von Gegnern XP bekommen (Vanilla: XP gibt es nur
ueber Quest-/POI-Abschluss). Gewaehlter Weg: **DLL-Hook** (bestehender DllProxy),
kein Pak-Szenario.

## Recon-Verdikt (diese Session)

XP-System in Windrose, verifiziert ueber Dumper-7-SDK + AssetRegistry:

- **XP ist KEIN GameplayAttribute.** Es ist das Feld `TotalExp` (int32) im nativen
  Business-Rules-Record `FR5BLEntityProgression`, eingebettet als
  `PlayerProgression` @ `0xE8` in `FR5BLPlayerMetadata`.
- **Die komplette reflektierte Progression-Oberflaeche ist read-only**
  (`UR5EntityProgressionVM` hat nur Getter). Es gibt **keine** BlueprintCallable
  "Add XP" / "Apply Quest Reward"-Funktion. Der Grant ist rein internes C++.
- **Gegner haben kein XP-/Bounty-/Reward-Feld.** XP existiert sonst nur als
  UI-Notification (`OnExperienceAdded(int ExpToAdd, bool)` auf
  `BP_PickupNotification_Manager_SC_C` - reiner Notification-Handler, vergibt nichts).

### `FR5BLEntityProgression_V0_9_0` Layout (0xB0)

| Offset | Feld | Typ |
|---|---|---|
| 0x00 | EntityProgressionParams (TSoftObjectPtr -> DA_HeroLevels) | 0x28 |
| **0x28** | **TotalExp** | int32 |
| 0x2C | RewardLevel | int32 |
| 0x30 | TalentTree | 0x40 |
| 0x70 | StatTree | 0x40 |

Eingebettet: `FR5BLPlayerMetadata.PlayerProgression` @ 0xE8 -> TotalExp liegt also
bei `metadata + 0xE8 + 0x28 = metadata + 0x110`.

## Phase 1: Kill-Signal-Erkennung (FERTIG, verifiziert in-game)

Modul `qm_killxp.cpp/.hpp`, reitet auf dem globalen ProcessEvent-Net-Hook
(`qm_hook.cpp`). Sentinel `qm_killxp.txt` neben dxgi.dll = armed. Reines Logging.

**In-game-Ergebnis (2026-06-08, mehrere Dodo-/Boar-Kills):**

- **`OnPawnEnemyDead`** auf `R5ScenarioTracker_EnemiesKilledCount` ist das
  zuverlaessige Signal: feuert **1x pro Kill**, ueber alle Kreatur-Typen,
  Tracker-Instanz konstant ueber die Session. Param-Layout (Dumper-7 verifiziert,
  identisch auf Tracker/Listener/ManyClass):
  - `APawn* Pawn` @ 0x000 (das getoetete Wesen) - **robust ausgelesen**
  - `FGameplayEffectSpec` @ 0x008 (inline, 0x298)
  - `float IncomingDamage` @ 0x2A0, `float DealtDamage` @ 0x2A4 - **robust**
- Der Talent-Pfad `GA_Base_ApplyEffectForKill_C::OnDamageDealt_Event` feuert NICHT
  (talent-gated, nur mit ausgeruestetem Talent aktiv). Daher ist `OnPawnEnemyDead`
  das Signal der Wahl.

**Offener Punkt - Killer/Spieler-Filter:** Der Versuch, den Killer aus dem
`FGameplayEffectSpec.EffectContext` (Handle @ spec+0x278) zu lesen, liefert
Muell (`<idx 930690912 unresolved>` - rohe Pointer-Low-Dwords, keine
GObjects-Indizes). Die angenommene Stock-UE5-Layout (`Instigator`
TWeakObjectPtr @ ctx+0x08) stimmt fuer Windroses R5-EffectContext nicht.
Offen, ob wir den Filter ueberhaupt brauchen (billiger Behavioral-Test:
laesst ein Raubtier eine Beute toeten -> feuert `OnPawnEnemyDead`? Wenn nein,
feuert der Tracker eh nur bei Spieler-Kills).

## Phase 2: XP-Vergabe (Ghidra-RE) - AKTUELL

Der Grant ist internes natives C++ (keine UFunction -> keine Adresse dumpbar).
Wir muessen ihn per Ghidra/IDA finden. Bootstrap: die `[KillXP-RE]`-Anker, die der
DLL-Anker-Dump beim ersten Antreffen von `R5EntityProgressionVM` ausgibt.

### Anker (in-game gedumpt 2026-06-08)

```
module base = 0x00007FF7325B0000
(rva = file/IDA-Offset bei preferred base 0x140000000)

GetCurrentExp      rva=0x8124DA0   -> Ghidra 0x148124DA0
GetCurrentLevel    rva=0x8124DE0   -> Ghidra 0x148124DE0
GetExpToNextLevel  rva=0x8125010   -> Ghidra 0x148125010
GetMaxLevel        rva=0x8125260   -> Ghidra 0x148125260
```

> Diese RVAs sind die **Exec-Thunks** der nativen UFunctions
> (`execGetCurrentExp` etc.), NICHT die eigentlichen Getter-Bodies. Der Thunk
> entpackt den FFrame und ruft die reale C++-Methode auf (oder hat sie inlined).

### Schritt-fuer-Schritt (Ghidra)

**A. Setup**
1. `Windrose-Win64-Shipping.exe` in Ghidra laden, Image-Base auf `0x140000000`
   lassen (default). Auto-Analyse durchlaufen lassen.
2. Goto `0x148124DA0` (= `execGetCurrentExp`). Decompile (F5 / CodeBrowser).

**B. Zeiger-Kette zu TotalExp finden (hohe Confidence, einfach)**
3. Der Exec-Thunk sieht etwa so aus:
   ```c
   void execGetCurrentExp(UObject *Context, FFrame *Stack, void *Result) {
       // P_FINISH (Stack->Code++)
       *(int *)Result = GetCurrentExp_real(Context);   // <- dem CALL folgen
   }
   ```
   Dem `CALL` auf die reale Methode folgen (Doppelklick auf `sub_...`).
4. Die reale `GetCurrentExp` dekompiliert zur **Zeiger-Kette**, z.B.:
   ```c
   int GetCurrentExp_real(longlong this) {
       return *(int *)( <kette von derefs ab `this`> + 0x28 );
   }
   ```
   Das **finale `+ 0x28`** ist `TotalExp` -> Bestaetigung, dass die Kette stimmt.
5. **Cross-Check** (validiert die Kette wasserdicht):
   - `0x148124DE0` (`GetCurrentLevel`) muss die **gleiche** Kette bis zur
     `FR5BLEntityProgression`-Basis haben, nur final `+ 0x2C` (RewardLevel)
     statt `+ 0x28`.
   - `0x148125010` (`GetExpToNextLevel`) dereferenziert zusaetzlich
     `EntityProgressionParams` @ `progression+0x00` (Soft-Ptr -> DA_HeroLevels
     `Levels`-Array mit den Exp-Schwellen) und vergleicht gegen `TotalExp`.
   Wenn alle drei dieselbe Basis-Kette teilen -> Kette = sicher. Notiere die
   exakte Offset-Kette `VM -> ... -> FR5BLEntityProgression*` und die **Klasse**
   des Holder-Objekts (das, worin `PlayerProgression` @ 0xE8 sitzt - vermutlich
   `FR5BLPlayerMetadata` / ein Player-State/BusinessRules-Entity).

**C. Grant-Funktion (TotalExp-Writer) finden**
6. Ziel: die Funktion, die `[progression + 0x28]` **schreibt/inkrementiert**
   (nicht nur liest). Erkennungsmerkmal im Disas:
   ```
   add dword ptr [reg + 28h], ecx      ; reg = FR5BLEntityProgression*
   ; oder read-modify-write: mov eax,[reg+28h]; add eax,delta; mov [reg+28h],eax
   ```
   Wenn vom Metadata-Holder aus zugegriffen: Offset `0xE8 + 0x28 = 0x110`.
7. Wege zum Writer:
   - **Typ-XREF:** In Ghidra dem Holder-Objekt aus Schritt 5 einen Struct-Typ
     mit Feld `TotalExp` @ richtigem Offset geben, dann Field-XREFs auf das Feld
     auflisten. Der schreibende XREF = Grant.
   - **Pattern-Scan:** nach `add [reg+28h]` / RMW auf 0x28 (bzw. 0x110) in
     Funktionen suchen, die den Holder-Typ als Parameter nehmen.
8. **Grant-Funktion validieren** (sollte mehrere dieser Anker treffen):
   - Schreibt **auch** `RewardLevel` @ 0x2C (Level-Recalc) und/oder liest die
     `Levels`-Exp-Schwellen (Level-Up-Check).
   - Liegt nahe am **`OnExperienceAdded`**-Broadcast (BP-Event-Dispatch mit dem
     `ExpToAdd`-Delta - der Notification-Trigger sitzt direkt am Grant).
   - Liegt nahe am **Quest-Reward-Apply**: `GetData_QuestExperience` liest
     `R5BLQuestParams::ExperienceCount`; die Reward-Apply-Funktion liest dasselbe
     Feld und ruft danach den Grant mit diesem int.

### Ergebnis -> in die DLL

- **Variante 1 (sauber): Grant-Funktion aufrufen.** Signatur (this-Typ + int
  amount) aus Ghidra. `this` = der Progression-Holder, erreichbar ueber die Kette
  aus Schritt 5 (von einem live UObject wie dem VM/PlayerState aus). Funktions-RVA
  als `OFFSET_*` in `qm_ue.hpp`, auf dem Game-Thread aus dem Kill-Hook aufrufen.
- **Variante 2 (fallback): direkter Write** auf `TotalExp` ueber die Kette +
  nativen Recalc/Notification anstossen. Riskanter (Level-Up-Belohnungen +
  Persistenz sind diskrete native Events; ein roher Write fuellt evtl. nur die
  Leiste ohne korrektes Level-Up/Save).

Beide Varianten brauchen die Kette aus Schritt 5 - der erste Ghidra-Schritt ist
also in jedem Fall der wichtigste.

### DLL-Chain-Validator (GEBAUT 2026-06-08, deployed)

`qm_killxp.cpp::TryScanXpChain()` - reitet auf dem PE-Net-Hook, one-shot. Greift
zur Laufzeit eine **live** `R5EntityProgressionVM`-Instanz (kein CDO/Archetype),
liest den Ground-Truth `TotalExp` ueber den reflektierten `GetCurrentExp()`-Getter
(+ `GetCurrentLevel()`), und brute-scannt dann **Pointer-Ketten (Tiefe <= 2)** vom
VM aus, die auf genau diesen Wert landen. Jede Treffer-Kette wird mit ihrer
Offset-Kette geloggt (`[KillXP-RE] d1 hit:` / `d2 hit:`), inkl. Nachbar-Int @ +4
(Kandidat `RewardLevel`) zum Gegencheck.

Voraussetzung: eine live VM existiert (Charakter-/Stat-Menue einmal oeffnen) und
`TotalExp > 0` (sonst zu viele Null-Kollisionen -> der Scan wartet + retryt alle
4 s). Re-entrant-sicher (der `GetCurrentExp`-ProcessEvent-Call re-entert den
PE-Hook; via `g_scanInProgress` + Throttle abgefangen).

**Konvergenz:** Die Runtime-Kette, deren finaler Offset auf `TotalExp` zeigt, muss
mit Ghidras dekompiliertem `GetCurrentExp`-Deref uebereinstimmen. Damit ist die
Kette aus Schritt 5 doppelt bestaetigt (statisch + live), ohne raten.

## DURCHBRUCH (2026-06-08, Ghidra + SDK-Re-Recon)

### GetCurrentExp vollstaendig dekompiliert -> Lesekette bestaetigt

```c
// execGetCurrentExp (0x148124DA0)
GetProgressionSnapshot(VM, &snap);   // = FUN_14811dee0, gemeinsamer Accessor
result = *(int*)(snap + 0x08);       // Exp  = snapshot[0x08]
// execGetCurrentLevel (0x148124DE0): result = *(int*)(snap + 0x00) = Level
```

`GetProgressionSnapshot` kopiert aus `owner = *(VM + 0x70)` (weak-ptr-validiert)
einen 0x1C-Byte-Snapshot: `owner+0x98 -> snap[0x00]` (Level),
`owner+0xA0 -> snap[0x08]` (**TotalExp**), `owner+0xA8`, `owner+0xB0`.

**Lesekette: `VM + 0x70` -> Model -> `+0xA0` = TotalExp (Level @ +0x98).**
`owner` = `UR5EntityProgressionModel` (SDK: Pad_98[0x20] unreflected ab 0x98 -
exakt wo Level/Exp liegen; `VM+0x70` = Model-Ptr der `UR5MVVMViewModel`-Basis).

> WICHTIG: Das ist der **UI-Spiegel** (ViewModel/Model), NICHT der schreibbare
> Save-Record `FR5BLEntityProgression`. Ein Write auf `owner+0xA0` aendert nur die
> Anzeige, nicht den persistenten Zustand / kein Level-Up. Also NICHT das Write-Ziel.

### Der authoritative Grant-Pfad existiert als strukturierte Daten

Re-Recon der GObjects-Dumps fand einen kompletten "AddExp"-Pfad:

| Objekt | Art | Felder |
|---|---|---|
| `R5ScenarioTask_AddExp` | Class (Szenario-Task) | `int exp` @0x118, `bool bHideNotification` @0x11C |
| `R5BLProgression_AddExp` | ScriptStruct (BR-Command) | `EntityProgressionPath` @0x00, `int exp` @0x18 |
| `R5BLProgression_AddExpRule` | Class (BR-Regel) | wendet das Command auf den Record an |
| `R5AddExpNotificationSignal` | ScriptStruct | die XP-Notification |
| `R5ScenarioTask_AddReward` | Class | Reward (Items) + `bHideNotification` |

Architektur: Szenario laeuft `R5ScenarioTask_AddExp(exp=N)` -> baut BR-Command
`R5BLProgression_AddExp{Path, exp}` -> `R5BLProgression_AddExpRule` mutiert
`TotalExp` im Record + feuert `R5AddExpNotificationSignal`. **Das ist exakt der
Pfad, den Quest-Abschluss nutzt** - inkl. Level-Up + Persistenz.

**Kein reflektierter Aufruf moeglich:** Szenario-Tasks executen ueber eine native
virtuelle `Execute(context)` (keine UFunction); BR-Regeln ueber nativen Dispatch.
`BPFL_ScenarioHelpers` hat nur Getter (keine "AddExp"-Helper). Also weiter
nativer Code - ABER mit viel besserem RE-Anker als "Writer von +0x28": die
benannte Klasse `R5ScenarioTask_AddExp` + ihre CDO/vtable.

### Konsequenz fuer die Strategie

Drei realistische Wege (Entscheidung offen):

- **Weg A - reiner Pak-Szenario-Mod:** Ein Always-On-Szenario
  `R5ScenarioListener_EnemyKilled -> R5ScenarioTask_AddExp(exp=N)` als Datenasset
  bauen. Kein DLL, kein RE, **update-fest**, distributierbar wie unsere anderen
  Mods. Risiko: ob Szenario-Definitionen ueberhaupt als moddbare Daten vorliegen
  (vs. cooked Graphen) und wie man ein Szenario "immer an" bekommt. Recon noetig.
- **Weg B - DLL ruft Grant:** `R5ScenarioTask_AddExp::Execute` (oder die
  AddExpRule) via CDO/vtable-Dump lokalisieren, RVA in `qm_ue.hpp`, aus dem
  Kill-Hook aufrufen. Braucht RE + den Execution-Context (Szenario/Blackboard).
- **Weg C - DLL direkter Write:** TotalExp ueber die (gleich live bestaetigte)
  Kette schreiben + Notification poken. Schnellster Prototyp, am fragilsten
  (Level-Up/Save sind diskrete native Events).

## Weg-A-Verdikt (2026-06-08): NICHT machbar als reiner Pak-Mod

Recon des Szenario-Systems (Dumper-7 + AssetRegistry):

- Szenario-**Definitionen** sind **`R5ScenarioBlueprint`-Assets** (668 Stueck im
  Spiel, alle `SC_*`). Das sind **gecookte Blueprint-Graphen** aus
  `R5ScenarioGraphNode`-Objekten (Listener-Node -> Task-Node, verdrahtet ueber
  `ChildrenNodes`-Sets + `OnListenerEnd`/`OnTaskEnd`-Multicast-Delegates).
- `R5ScenarioSettings` (Container mit `Scenarios`-Array) und die Nodes sind
  native UObjects mit Delegate-Wiring - **kein flaches DataAsset** wie unsere
  BoardingParams/Cannon-Structs. Ein solches Graph-Asset von Hand (ohne
  Game-Editor + Scenario-Plugin) zu erzeugen oder umzuverdrahten ist praktisch
  unmoeglich (Plugin-Module shippen nicht im gecookten Spiel).
- **Always-On** ist nicht datengetrieben: aktive Szenarien laufen ueber
  **Executor/Blackboard**, getrackt im Business-Rules-Save
  (`R5BLScenario_InitScenarioSaveModel.DefaultScenarios` etc.) - ein
  Runtime/Save-Mechanismus, keine statische Startup-Liste zum Anhaengen.
- Vorhandene EnemyKilled-Szenarien sind **POI-scoped** ("toete alle Gegner in
  diesem POI -> Reward"), nicht global. Ein generelles "jeder Kill ueberall ->
  XP" existiert vanilla nicht und liesse sich nicht per Daten nachruesten.

**Fazit:** Pure-Pak-Szenario-Route ist tot. Zurueck zum **DLL-Weg** - aber mit
dem `AddExp`-Wissen als besserem Ziel. Naechster Schritt: DLL-Diagnose erweitern,
um die Grant-Seite zu bootstrappen (analog zu den `[KillXP-RE]`-VM-Ankern):
- `R5ScenarioTask_AddExp`-CDO + vtable-RVAs dumpen (-> natives `Execute()`),
- `R5BLProgression_AddExpRule`-CDO dumpen,
- den **live authoritative** Progression-Record finden (nicht den VM-UI-Spiegel):
  Scan nach einem Objekt, dessen `+0x28`==`TotalExp` UND `+0x2C`==`RewardLevel`
  (Cross-Check gegen `GetCurrentExp()`/`GetCurrentLevel()`), Holder mit
  `PlayerProgression` @ 0xE8.

## Phase 2b: DLL-Derisk Test-Write (ABGELOEST 2026-06-08)

> ABGELOEST durch Phase 2c. Der rohe Record-Write feuerte nie: weder die BL-View
> noch der VM erreichten den authoritative `FR5BLEntityProgression`-Record (RECORD
> DIAG: 0 Kandidaten ueber 5 Holder - der Record liegt in der Player-Metadata-
> Schicht ausserhalb der Scan-Reichweite). Statt den Record-Scan zu erweitern, hat
> die Ghidra-RE den **nativen Grant-Pfad** vollstaendig erschlossen (siehe 2c) -
> der ist sauberer (echtes Level-Up + Notification + Save) als ein roher Write.

`qm_killxp.cpp::TryGrantXpTest()` - billiger Derisk VOR dem Ghidra-RE: schreibt
einmalig roh in den authoritative `TotalExp`, um zu sehen, ob das ein echtes
Level-Up + Persistenz ausloest (oder ob wir zwingend den nativen AddExp-Grant
brauchen).

- **2. Sentinel** `qm_killxp_grant.txt` (neben dxgi.dll, zusaetzlich zum
  `qm_killxp.txt`). Rising-edge-gated: feuert 1x pro Praesenz; entfernen +
  neu anlegen = nochmal feuern. Ohne den 2. Sentinel keinerlei Mutation.
- **Record-Lokalisierung:** live `R5BLEntityProgressionView` (BL-Schicht, NICHT
  der UI-VM/Model-Spiegel) per Reflection finden; darin (embedded + Pointer-Hops
  Tiefe<=2) den Record suchen, der BEIDE Anker erfuellt: `+0x28`==`GetCurrentExp()`
  UND `+0x2C`==`GetCurrentLevel()` (starker Doppel-Int-Anker). Fallback: Scan vom
  live VM. Alles SEH-guarded.
- **Aktion:** `TotalExp += 500`, before/after von Exp UND RewardLevel geloggt
  (`[KillXP-RE] *** GRANT TEST ***`). RewardLevel-after zeigt, ob das Spiel das
  Level selbst nachrechnet (wenn nein -> roher Write fuellt nur die Leiste, wir
  brauchen den Rule-Pfad).
- Voraussetzung: `qm_killxp.txt` armed (sonst laeuft der PE-Hook nicht),
  Stat-Menue einmal offen (bindet VM + BL-View), `GetCurrentExp() > 0`.

## DURCHBRUCH 2: Kompletter Grant-Pfad dekompiliert (2026-06-08)

RE-Route: vtable-Diff der CDOs statt blinder Write-XREF-Suche.

- **Rule-Diff** (`R5BLProgression_AddExpRule` vs `_AddProgressionPointsRule`):
  nur Reflection-/Namens-Getter-Glue (`Do_Impl`-Strings ohne XREF). Die Apply-
  Logik der Regel ist **NICHT** vtable-virtuell -> sie laeuft ueber datengetriebenen
  Dispatch. Sackgasse fuer den Rule-Pfad.
- **Task-Diff** (`R5ScenarioTask_AddExp` vs `R5ScenarioTask_Delay`, sauberes
  `break`-am-Tabellenende): genau **1 Logik-Slot** `task vt[101]` =
  **`R5ScenarioTask_AddExp::Execute`** @ **rva `0x9803390`** (Ghidra `0x149803390`).

### `Execute` dekompiliert: publish-to-command-bus (kein Direkt-Write)

```c
// FUN_149803390  (param_1 = task; param_1[0x23] = *(int*)(task+0x118) = exp)
if (0 < (int)param_1[0x23]) {                  // if (exp > 0)
    ... owner/target aufloesen + GetState()-Gate (vtable +0x2D0) ...
    uVar4 = FUN_148808060(param_1);             // (1) target = command bus
    uVar2 = _DAT_150cced20;                     // (2) cmdType (global value)
    uStackX_8 = (int)param_1[0x23];             //     payload.exp        @ +0x00
    uStackX_c = *(byte*)(param_1 + 0x11c);      //     payload.hideNotif  @ +0x04
    uVar5 = FUN_148a20de0();                    // (3) executor (ensure-init)
    FUN_148807b80(uVar4, uVar2, uVar5, &uStackX_8, 1);   // PUBLISH AddExp command
}
```

`FUN_148807b80` ist ein **Command-Bus-Publish**: `target` ist eine TSet
`cmdType -> [Handler]`; die Funktion sucht die Handler-Liste und ruft jeden
Handler `(*pcVar2)(obj, &cmdType, &executor, &payload)`. Einer der Handler ist
`R5BLProgression_AddExpRule::Do_Impl` -> macht den echten `TotalExp += exp` +
Level-Recompute + `R5AddExpNotificationSignal` + Save.

### `target` ist task-FREI (der entscheidende Befund)

```c
FUN_148808060(task): lVar1 = FUN_14476f040(DAT_150becef0, task, 2);
                     if (*(longlong*)(lVar1+0x228) != 0) return FUN_1488074a0();  // task = nur GATE
FUN_1488074a0():     lVar1 = FUN_148806f40(); ...weak-validate... write -> out+0x100
FUN_148806f40():     if (_DAT_150ca73b8 == 0) FUN_1415e3720(.., &_DAT_150ca73b8, ctor, 0x80, ..);
                     return _DAT_150ca73b8;   // <- LAZY-INIT-GLOBAL-SINGLETON (0x80B TSet)
```

Der Task fliesst **nur als Gate** ein, nie in `target`. `target` entsteht
ausschliesslich aus `FUN_148806f40()` = lazy-init Global-Singleton. -> Die DLL
kann den Grant **task-frei** fahren. Kein UObject (nativer Manager via Factory),
daher **nicht** per Reflection findbar; wir rufen die Funktion direkt.

### Alle 4 Dispatcher-Argumente (RVA @ preferred base 0x140000000)

| Arg | Quelle | RVA |
|---|---|---|
| `target` (bus) | `FUN_148806f40()` (init + return Singleton) | `0x8806F40` |
| `cmdType` | `*_DAT_150cced20` (global value) | glob `0x10CCED20` |
| `executor` | `FUN_148a20de0()` (ensure-init) -> `*_DAT_150cba398` | `0x8A20DE0` / glob `0x10CBA398` |
| `payload` | `{ int32 exp; uint8 hideNotif }` selbst gebaut | - |
| Dispatcher | `FUN_148807b80(target, cmdType, executor, &payload, 1)` | `0x8807B80` |

## Phase 2c: Nativer Grant-Call (GEBAUT 2026-06-08, deployed)

`qm_killxp.cpp::TryGrantXpTest()` (Body komplett ersetzt) repliziert Executes
Dispatch-Block task-frei: `getCmdBus()` -> `ensureExec()` -> Globals lesen ->
`payload{exp=+500, hideNotif=0}` -> `dispatch(bus, cmdType, executor, &payload, 1)`.

- **Sicherheit:** der Dispatcher CALLt Funktionszeiger aus `bus` (ACE-/Crash-
  Gefahr bei falschem target). Daher: (1) komplett SEH-guarded, (2)
  Struktur-Plausibilitaets-Gate auf den Bus (TSet-Form: `+0x38` count, `+0x64`
  cap sane; bei count>0 `+0x30` elements = LooksLikePtr), (3) alle 4 Argumente +
  beforeExp werden VOR dem Feuern geloggt (`[KillXP-RE] *** GRANT(native) ***`).
  Schlaegt Gate/SEH zu -> kein Feuern, Save unangetastet, Log sagt warum
  (Verdacht: target = der FUN_1488074a0-Wrapper statt roher Bus).
- **Gate:** 2. Sentinel `qm_killxp_grant.txt` (rising-edge), BL-Progression live
  (`R5BLProgression_AddExpRule`-Klasse da), live Hero-VM mit `GetCurrentExp() > 0`
  (before-Snapshot + bestaetigt gebundene Progression). Re-entrant-sicher via
  `g_grantInProgress` (der Bus-Broadcast re-entert den PE-Hook).
- **Erfolg = `[KillXP-RE] *** GRANT(native) FIRED ***`** -> in-game pruefen:
  XP-Leiste? echtes Level-Up? persistiert nach Save+Reload?

## Phase 3: Durchbruch - seed-freier Grant "from nothing" (GEBAUT + verifiziert 2026-06-09)

Die Phasen 2/2b/2c oben sind **ueberholt**. Chronik der Sackgassen, die zum
funktionierenden Weg fuehrten:

| Versuch | Ergebnis |
|---|---|
| Roher Record-Write (`TotalExp += N`) | **kosmetisch** - Level-VFX, aber keine Punkte, nicht persistent |
| Nativer Dispatcher-Call (task-frei, Phase 2c) | `target` ohne echten Task nicht beschaffbar - roher Bus + GWorld crashen beide den TSet-Walk |
| Clone-and-Replay (lebenden Task byte-klonen, exp ueberschreiben, Execute) | **kein Crash** (`DONE`), aber **Stale-State**: Replay ~0,4 s nach Abschluss des echten Tasks, der Szenario-Graph meldet "fertig" -> Grant-Block uebersprungen |

**Das Messinstrument, das alles loeste - `MeasureExecuteGates` (RE-Geruest, inzwischen
entfernt):** misst alle Execute-Gates auf dem lebenden Task (im Execute-Hook, vor dem
echten Grant) UND auf dem Klon, side-by-side. Dabei wurde der **echte `target`** gedumpt.

### Schluesselbefund: `target` = `GameplayMessageSubsystem`

`target` ist ein **persistentes, per Reflection findbares World-Subsystem** - KEIN
transienter Szenario-Graph. Klon und echter Grant loesen **byte-identisch dieselbe**
`GameplayMessageSubsystem`-Instanz auf. Damit war die "target ist task-gebunden/
transient"-Sorge widerlegt. Aufloesungs-Kette:

```
task->GetContext() (vt+0x180, FUN_141694fd0: walkt Outer @+0x20)
  -> World
  -> GameplayMessageSubsystem.Get(World)
```

Die uebergeordnete Resolution (`FUN_148808060` -> `FUN_14476f040`, Fallback
`DAT_150bea890` = GWorld) holt das Subsystem **robust aus der aktuellen World**,
egal ob GetContext null liefert. Deshalb kein Crash + richtiges Ziel.

### Der gebaute Weg: CDO-Klon

`FireConstructGrant` klont das **CDO** von `R5ScenarioTask_AddExp` (immer in
GObjects, **kein POI-Seed noetig**), setzt die gate-relevanten Felder und ruft das
echte `Execute`:

| Feld | Offset | Wert | Gate |
|---|---|---|---|
| exp | +0x118 | Betrag | G1 (`exp > 0`) |
| owner | +0xC8 | live `BP_R5PlayerState_C` | G2/G4 + G5a |
| state | +0xC0 | 0 | G3 (`FUN_147e6a580` liest task+0xC0) |
| Outer | +0x20 | live World | ctx-Resolution (GetContext walkt Outer) |

Execute @ rva `0x9803390`. Alles SEH-gekapselt; ein falsches Feld -> sauberer
Fault, Save unangetastet.

## Phase 4: Owner-Validierung + Kill-Verdrahtung + Cleanup (2026-06-09)

### G5a: das einzige nicht-triviale Gate haengt nur am Owner

Der erste Construct-Grant scheiterte an **G5** (`G5a=0`). Der Gate-Diff (live vs.
Konstrukt) zeigte: genau ein Gate kippt.

- `FUN_149818cd0(task)` (G5a-Resolver) liest **kein neues Task-Feld**: es ruft
  `FUN_148295840(task)` = der Owner-Getter (task+0xC8), loest darueber die
  BL-Entity des PlayerState aus der Registry auf (`FUN_141745f90(owner, registry)`).
- `FUN_1457d9570(entity)` (Check) = "ist die aufgeloeste Entity aktiv"
  (`entity+0x48 != 0 && [0x48]->vtable[1]() != 0`).

**Wurzel:** In der Multiplayer-Welt (`GenlandiaMulty`) gibt es **mehrere**
`BP_R5PlayerState_C`. "Nimm den ersten" war PlayerState-Roulette - nur der LOKALE
Spieler loest eine Entity auf. Adress-Beweis: gleicher Owner wie der Live-Grant
-> G5a=1, anderer -> G5a=0.

**Fix:** `FindGrantableOwner` enumeriert **alle** PlayerStates, misst G5a je
read-only (`EvalOwnerG5a`), pinnt den ersten grantbaren. Seed-frei +
multiplayer-robust, ohne weiteres RE. Der validierte Owner wird gecacht
(`g_grantableOwner`) und pro Grant nur noch billig re-validiert.

**In-game verifiziert (2026-06-09):** echter, persistenter +500-Grant from
nothing - Punkte + Level + Notification + Save.

### Kill-Verdrahtung

- Erstversuch: Grant an `OnDamageDealt_Event` (`bIsKillDamage`). **Feuerte nie** -
  talent-gated (nur mit ausgeruestetem Talent). Im Log 0 Dispatches.
- Umverdrahtet auf **`OnPawnEnemyDead`** (immer-an Szenario-Kill-Zaehler, lief
  201x sauber in einer Session). Spieler/Team-skopiert per Design.
- **Per-Victim-Dedup** (Victim-Pointer-Ring, 750 ms): ein Tod = genau ein Grant,
  auch wenn mehrere Listener im selben Frame feuern; gleichzeitige *verschiedene*
  Kills (AoE) granten weiterhin einzeln. Null-Victim -> 60 ms Cooldown als Backstop.
- `OnDamageDealt_Event` bleibt als reine Beobachtung (`DMG-KILL`-Log), falls ein
  Build den Pfad doch dispatcht (re-promote-bar).

### Cleanup

Das gesamte RE-Geruest entfernt (Anchor-Dumps, Chain-Scan, Raw-Write,
Clone-Replay, Construct-Probe, `MeasureExecuteGates`, Wiring-Capture + der
Execute-MinHook in `qm_hook.cpp`). DLL 369 -> **354 KB**. Geblieben: der schlanke
Produktionspfad (Kill-Detektion + `FireConstructGrant`).

## Hardcoded RVAs/Offsets (Game-Update-Recovery)

Alle preferred-base `0x140000000`. Bei jedem Game-Update neu zu verifizieren
(geplanter KillXP-Abschnitt in `GAME_UPDATE_RECOVERY.md`):

| Konstante | Wert | Bedeutung |
|---|---|---|
| `RVA_ScenarioExecute` | `0x9803390` | `R5ScenarioTask_AddExp::Execute` (vt-Slot 101) |
| `RVA_GateResolveX` | `0x9818CD0` | `FUN_149818cd0(task)` - G5a-Resolver (Owner -> BL-Entity) |
| `RVA_GateCheckX` | `0x57D9570` | `FUN_1457d9570(entity)` - G5a-Check (Entity aktiv) |
| `OFF_TaskExp` | `+0x118` | int32 exp |
| `OFF_TaskHideNotif` | `+0x11C` | uint8 bHideNotification |
| `OFF_TaskStateByte` | `+0xC0` | Szenario-State-Byte (State-Virtual liest task+0xC0) |
| `OFF_TaskOwnerCached` | `+0xC8` | gecachter Owner (PlayerState) |
| `OFF_TaskOuter` | `+0x20` | UObject::Outer (GetContext walkt zur World) |

Klassen per Reflection (FName, update-stabil): `R5ScenarioTask_AddExp` (CDO-Klon),
`BP_R5PlayerState_C` (Owner). `target` = `GameplayMessageSubsystem` (per World
aufgeloest, nicht hardcoded). Die Param-Offsets von `OnPawnEnemyDead`
(Pawn @0x000, IncomingDamage @0x2A0, DealtDamage @0x2A4) sind Dumper-7-verifiziert.

## Update-Fragilitaet

Alle in der Offset-Tabelle oben genutzten RVAs/Offsets verschieben sich bei jedem
Game-Update. Nach dem Cleanup re-dumpt `qm_killxp.cpp` **nicht** mehr automatisch
(das RE-Geruest ist raus) - das Nachziehen laeuft daher ueber den
**KillXP-Abschnitt in `GAME_UPDATE_RECOVERY.md`** (ergaenzt 2026-06-09: Symptome,
Konstanten-Tabelle, Re-RE-Prozedur). Symptom eines Drifts: `OnPawnEnemyDead` wird
erkannt (`KILL #N` im Log), aber `GRANT(kill) FAULTED` oder `granted=0` dauerhaft.

## Dateien

| Datei | Rolle |
|---|---|
| `Tools/DllProxy/dxgi/qm_killxp.cpp/.hpp` | Kill-Detektion + seed-freier Grant (`FireConstructGrant`) |
| `Tools/DllProxy/dxgi/qm_hook.cpp` | globaler PE-Net-Hook (ruft `QmKillXp_OnProcessEvent`) |
| `Tools/DllProxy/dxgi/main.cpp` | `QmKillXp_Init()` + Idle-Gate |
| Sentinel `qm_killxp.txt` (neben dxgi.dll) | armt das Modul (optional - eine `qm_killxp_onkill*.txt` armt allein) |
| Sentinel `qm_killxp_onkill_<profil>.txt` | profil-bezogen, geglobt + gemergt (bei Key-Kollision gewinnt der groessere Wert); Format `key=value` (Keyword/Substring -> XP) + `default=N` (Default 0 = vanilla); einmalig beim Start gelesen |
| Sentinel `qm_killxp_construct_grant.txt` | manueller One-Shot-Testgrant (rising-edge) |

## Offen / geplant

1. **Sentinel-Re-Read-Intervall (ERLEDIGT 2026-06-09).** Das periodische Neu-Lesen
   (`RefreshOnKillConfig`, alle 1,5 s) ist komplett entfernt - die Config wird jetzt
   **einmalig beim Start** in `QmKillXp_Init()` geparst. Kein per-Frame-File-I/O
   mehr. Live-Edit braucht damit einen Spiel-Neustart (so gewuenscht).

2. **Per-Gegner-XP statt globalem Betrag (ERLEDIGT 2026-06-09, Commits `eec6555` +
   `d9d4d5e`).** Im Kill-Hook liegt der Victim bereits vor (`OnPawnEnemyDead`
   Pawn @0x000 -> `victimObj`), also ist die Klasse pro Kill bekannt. Umgesetzt als:
   - **Config:** profil-bezogene `qm_killxp_onkill_<profil>.txt` (geglobt + gemergt,
     bei Key-Kollision gewinnt der groessere Wert), Format `key=value` + `default=N`.
   - **Matching:** case-insensitives **Keyword/Substring**-Matching, **laengster
     Key gewinnt** - `Mob_Boar=5` erschlaegt alle Boar-Varianten, ein laengerer
     `Mob_Boar_Mega=25` ueberschreibt gezielt. Pro `Class*` memoized (Hot-Path =
     Pointer-Compare). Rueckwaertskompatibel: exakte FName-Keys matchen weiter, eine
     nackte Zahl wird als `default=N` gelesen.
   - **Default 0 = vanilla:** nicht gematchte Gegner -> kein Grant (faellt gratis
     raus, da `FireConstructGrant(amount<=0)` schon `false` liefert).
   - **Frontend-Anbindung (erledigt):** Misc-Card-Slider (Default-XP 0-100) + eigener
     "XP for Kills"-Tab mit Keyword-Tabelle, generiert zur Laufzeit aus dem Vanilla-
     Pak (`KillXpMobCatalog`, CUE4Parse - wie `/api/npc-spawners`). Deploy schreibt
     `qm_killxp_onkill_<profil>.txt` neben `dxgi.dll` (DLL-only, kein Pak).

3. **`GAME_UPDATE_RECOVERY.md` KillXP-Abschnitt (ERLEDIGT 2026-06-09).** Eigener
   Abschnitt "XP-for-Kills module (qm_killxp) recovery" ergaenzt: Drift-Symptome,
   die komplette Konstanten-Tabelle (RVAs + Task-Offsets) und die Re-RE-Prozedur
   (Execute-Slot 101 als Source-of-Truth). Bei kuenftigen Aenderungen an den
   Offsets dort mit nachziehen.

4. **In-game-Verifikation des GUI-generierten Configs (ERLEDIGT 2026-06-09).**
   Keyword-Matching ueber die profil-bezogene Datei (vom Configurator geschrieben)
   in-game bestaetigt: eigenes Profil mit eigenen Per-Gegner-Werten im "XP for
   Kills"-Tab, Build, Play - greift wie erwartet. Damit ist das Feature komplett
   und das Doc abgeschlossen (umbenannt von `-WIP` auf `-DONE`).
