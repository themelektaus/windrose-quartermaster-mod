# Work In Progress: Weather Control (on-use item -> weather change)

Stand: 2026-06-06

> **Benennung (final):** Das ausgelieferte Feature heisst **"Weather Control"** -
> ein Item nach dem **Rum-Bottle**-Template mit einem Wetter-Wechsel-Use-Effekt
> (DLL-Token `QmWeatherControl_`). Es ist KEINE "Whistle". Dieses Entwicklungs-Log
> beschreibt auch verworfene Boar-/Spawner-Whistle-Ansaetze aus fruehen Stages -
> diese **Vanilla**-Begriffe (Boar Whistle, Spawner-Whistle) bleiben bewusst als
> historischer Record stehen; nur der fruehere Arbeitsname unseres eigenen
> Features wurde projektweit auf "Weather Control" vereinheitlicht.

## Ziel

Nachbau der Referenz-Mod "Windrose Weather Control" (UE4SS), aber OHNE UE4SS.
Wunsch-UX: ein Consumable-Item ("Weather Control", Klon der Boar-Whistle) das
beim Benutzen das Wetter aendert; spaeter im Item-Creator ein 3. Template mit
einem Use-Effekt-Dropdown ((vanilla) / "Change weather").

## Referenz-Mod (References/Windrose Weather Control)

UE4SS-Lua. Findet zur Runtime die Live-`R5N_WeatherComponent` (FindAllOf, ueber-
springt das `Default__`-CDO) und setzt `CheatWeatherID` (int8). Tabelle:

| id | Name | id | Name |
|---|---|---|---|
| 0 | Sunny | 7 | Windy |
| 1 | Cloudy | 8 | HighPressure |
| 2 | Fog | 9 | Rainbow |
| 3 | Mist | 10 | Overcast |
| 4 | Rain | 11 | AshlandsFog |
| 5 | RainHeavy | 12 | TortugaMist |
| 6 | Storm | 13 | Default |

## Recon-Verdikt: rein per Pak (ohne Runtime) NICHT moeglich

Wetter wird im Spiel nur auf drei Wegen geaendert:
1. `CheatWeatherID` an der Live-`R5N_WeatherComponent` schreiben (Runtime; das macht die Ref-Mod).
2. Scenario-Graph-Tasks `R5ScenarioTask_ChangeWeather` (PresetName/Season) / `StartInfinityWeather` / `StopInfinityWeather` - cooked Scenario-Graphs, vom Scenario-Subsystem gestartet.
3. Das Weather-Subsystem selbst (Probability/Seasons/Presets - cooked DAs, global, zeitgesteuert).

**Item-on-Use rein per Pak scheitert**, weil:
- Das Item triggert beim Use `ActivationAbilityTag: GAS.Consumable.Activate.Spawner` + ein cooked `DA_ConsumableAbilityData_SpawnerBoar` (`ConsumableData`-Pointer im Item-JSON).
- Eine erschoepfende Suche ueber ALLE `GA_*`-Pakete zeigt: **keine GameplayAbility beruehrt das Wetter** (nur `R5WetnessAbility.OnWeatherChanged` *liest* es). Es gibt also kein Weather-Asset, auf das man `ConsumableData`/`ActivationAbilityTag` umbiegen koennte.
- Die Daten-Hebel eines Consumables (`ConsumeEffects` = nur Attribute, `ConsumeCue` = kosmetisch, `EventsOnSpend` = Event-Tags) erreichen das Weather-Subsystem nicht.
- Eine neue Weather-Ability als BPGC zu authoren scheitert am usmap/CDO-Blocker (siehe PLAN-CustomItem_MobSwap-WIP.md).
- Die Scenario-Event-Bruecke (`SendScenarioEvent` -> `R5ScenarioListener_GameplayTagApplied.WaitedTag`) braucht einen selbst-authored cooked Scenario-Graph -> derselbe blockierte Pfad.

Reine Pak-Alternative waere nur ein GLOBALER Wetter-Override (cooked Weather-DA
ueberschreiben) - nicht on-use. Wurde zugunsten Option B verworfen.

## Gewaehlter Weg: Option B - bestehende dxgi.dll schreibt CheatWeatherID

Kein UE4SS. Die vorhandene `Tools/DllProxy/dxgi/` (hookt schon ProcessEvent +
hat GObjects-Zugriff fuer den Build-Mode-Inject) schreibt `CheatWeatherID` wie
die Ref-Mod, getriggert durch den Item-Use.

### Verifizierte Fakten (Dumper-7 SDK 5.6.1-0+UE5-R5)

- `UR5N_WeatherComponent` (R5Weather_classes.hpp:668): `int8 CurrentWeatherID @ 0x120` (Net, RepNotify), `int8 CheatWeatherID @ 0x122` (Net). Kein public Setter ausser `OnRep_CurrentWeatherID` -> Raw-Write ist der Weg.
- Live-Instanz: `...PersistentLevel.R5NatureLogicActor.R5WeatherComponent` (GObjects). CDO heisst `Default__R5N_WeatherComponent` -> beim Suchen ueberspringen.
- Item-Use-Chain: `R5BLInventoryItem.ActivationAbilityTag` + `.ConsumableData` (-> cooked `UR5ConsumeAbilityData`). Ability-Basis `UR5ConsumeAbility : UR5Ability` (R5_classes.hpp:16296) mit `Params_0 @ 0x3C0`, `InventoryView @ 0x3E0`, Funktion `EventReceived(FGameplayTag, FGameplayEventData)`.
- `FGameplayEventData` (GameplayAbilities_structs.hpp): `EventTag@0x00, Instigator@0x08, Target@0x10, OptionalObject@0x18, OptionalObject2@0x20, ...` - `OptionalObject` ist der Kandidat fuer das konsumierte Item-DA (in-game zu bestaetigen).

## Staged Plan

### Stage 1 - CORE de-risk (DONE - in-game verified 2026-06-06)

Entkoppelt von Item-Detektion: beweisen, dass ein Foreign-DLL-Write auf das
replizierte `CheatWeatherID` das Wetter ueberhaupt aendert.

- Neues Modul `qm_weather.{hpp,cpp}`: liest Sentinel `qm_weather.txt` (neben der DLL) -> int Wetter-id (0..13). Heartbeat (auf dem bestehenden Lifecycle-Hook, Game-Thread, gameplay-map-gated, ~5s) findet die Live-`R5N_WeatherComponent` (GObjects-Scan, Skip `Default__`) und pinnt `CheatWeatherID`.
- `main.cpp`: `QmWeather_Init()` nach `QmConfigLoad()`; Idle-Gate erweitert (`itemCount==0 && !weatherEnabled` -> idle), damit ein Weather-only-Deploy die DLL aktiv haelt.
- `qm_hook.cpp`: `QmWeather_Heartbeat()` im Gameplay-Branch von `Hook_LifecyclePreWarm`.
- Deploy: `dxgi.dll` (dev) + `qm_weather.txt`=6 (Storm) in `R5/Binaries/Win64/`.

**Ergebnis (in-game):** Sturm zieht auf und bleibt. Der Client-seitige Write
auf das Net-Property GREIFT in diesem Spiel - bestaetigt.

**Fix dabei (1. Versuch traf das falsche Objekt):** Der Finder durfte nicht nur
das namentliche `Default__`-CDO ueberspringen. Er landete auf dem Default-
Subobject-TEMPLATE *im* `R5NatureLogicActor`-Klassendefault (Name != `Default__`,
Adresse im EXE-/CDO-Bereich `0x7FF4...`, tickt nie -> Write ohne Wirkung). Der
Finder verwirft jetzt via `EObjectFlags` das Component-CDO/Archetype selbst UND
jedes Component, dessen Owner (`Outer`) ein CDO/Archetype ist. Uebrig bleibt die
laufzeit-gespawnte Live-Komponente (Heap-Adresse `0x000002...`).

**`CurrentWeatherID=0` trotz Sturm ist ERWARTET, kein Bug:** Die Komponente hat
drei getrennte Felder - `CurrentWeatherID @ 0x120` (Net, RepNotify; das
*natuerliche* Sim-Wetter, server-autoritativ), `NextWeatherID @ 0x121` und
`CheatWeatherID @ 0x122` (der Override). Der Renderer liest `CheatWeatherID` mit
Prioritaet und zeigt Sturm, schreibt ihn aber NICHT in `CurrentWeatherID` zurueck;
letzteres bleibt der natuerliche Wert (0 = Sunny/Default), bis der naechste
*natuerliche* Wechsel ansteht. Die Referenz-Mod setzt ebenfalls nur
`CheatWeatherID` (main.lua:41) und beruehrt `CurrentWeatherID` nie. Das echte
Erfolgssignal im Log ist der `CheatWeatherID`-Readback (`6 -> 6`, haelt ueber alle
Beats) + sichtbarer Sturm.

### Stage 2 - Item-Use-Detektion

Geteilt in 2a (Detektions-Recon, reine DLL) und 2b (Item-Pak + Trigger-Wiring),
um die EINE echte Unbekannte (feuert der Hook? welches Feld traegt das Item?)
isoliert zu de-risken - analog zur Stage-1-Methodik.

#### Stage 2a - Detektions-Recon (DONE - in-game verified 2026-06-06)

- `qm_hook.cpp`: neuer ExecFunction-Detour `Hook_ConsumeEventReceived` auf
  `R5ConsumeAbility::EventReceived(FGameplayTag, FGameplayEventData)`
  (R5_classes.hpp:16310). Probe `TryProbeConsumeHook` (native Klasse -> installiert
  ~pass#1) in der Probe-Loop, nicht-blockierend fuer die anderen Hooks.
- **Recon ONLY** - aendert nichts, forwarded das Original. Loggt pro Consumable-Use
  die Kandidaten-Identitaeten: Context (Ability) Cls/Name, `Params_0 @ 0x3C0`
  (AbilityData, z.B. `DA_ConsumableAbilityData_SpawnerBoar` - bei einem Klon
  identisch, daher KEIN per-Item-Diskriminator), `EventTag`, und aus dem FFrame
  (`Locals @ Stack+0x28` -> flat parms `{EventTag@0x00, EventData@0x08}`) die
  `FGameplayEventData`-Felder Instigator/Target/**OptionalObject @ +0x18**/
  OptionalObject2. `OptionalObject` ist der Primaer-Kandidat fuers konsumierte
  Item-DA.
- **Offene In-Game-Fragen (genau hierfuer der Recon):** (1) feuert der
  EventReceived-ExecFunction-Thunk bei Item-Use ueberhaupt? (2) welches Feld
  traegt den eindeutigen Item-DA-Namen unseres kuenftigen Weather Control?
- Falls der Thunk NICHT feuert (native Direct-Call statt ProcessEvent): Fallback
  = globaler ProcessEvent-Hook (vtbl idx 0x4C) mit Funktionsnamen-Filter
  `EventReceived` + Context-Klassen-Check.

**Ergebnis (in-game, mehrere Vanilla-Consumables, keine Boar-Whistle noetig):**
- Der ExecFunction-Thunk FEUERT zuverlaessig - auch fuer BPGC-Subklassen
  (`GA_FoodConsumableAbility_C`, `GA_BandageConsumableAbility_C`), weil der Hook
  auf der Basisklasse `R5ConsumeAbility::EventReceived` sitzt. Game-Thread.
- **Diskriminator = `Params_0` (die ConsumableData), NICHT `OptionalObject`.**
  Jeder Consumable-Typ traegt eine eindeutig benannte `R5ConsumeAbilityData`:
  Rum -> `DA_ConsumableAbilityData_Potion_RumBottle`, Coconana -> `..._Food_Drink_Coconana_T01`,
  Pepper -> `..._Food_Raw_Pepper_T01`, Bandage -> `..._Bandages_T01`.
- `EventData.OptionalObject` war eine Fehlannahme: konstant `BP_R5Character_C`
  (der konsumierende Spieler), traegt NICHT das Item-DA.
- `EventTag` trennt die Phasen: nur `GAS.Consumable.SpendConsumable` ist der echte
  Konsum-Moment (daneben `CanFinishAbility`, `Cmd.Interrupt`).
- **Wichtig fuers Design:** `R5ConsumeAbilityData` ist ein `UPrimaryDataAsset`
  (regulaerer DataAsset, KEIN BPGC) -> mit unserer Pipeline klonbar. Ein
  Weather-Control-Klon kann auf eine eindeutig benannte ConsumableData
  (`DA_ConsumableAbilityData_WeatherControl`) zeigen -> kollisionsfreies Matching
  ueber `Params_0`, ganz ohne den BPGC/usmap-Blocker.

#### Stage 2b - Trigger-De-risk auf Vanilla-Consumable (DONE - deployed, wartet auf In-Game-Test)

Isoliert die LETZTE Unbekannte (kann der Hook den Wetter-Write wirklich
AUSLOESEN?) von der Item-Authoring-Arbeit, indem ein bereits besessener
Vanilla-Consumable als temporaerer Trigger dient.

- `qm_weather.{hpp,cpp}`: zweites Sentinel `qm_weather_trigger.txt`
  (`<substring> <weatherId>`, z.B. `Bandages 6`). Neue Funktion
  `QmWeather_TryConsumableTrigger(consumableDataName, eventTag)` - matcht den
  `Params_0`-Namen (substring) auf der `GAS.Consumable.SpendConsumable`-Phase und
  setzt + pinnt das Wetter ueber den bewiesenen `CheatWeatherID`-Write-Pfad
  (Write + Component-Find als `WriteCheatWeather`/`ResolveWeatherComp` faktorisiert,
  von Heartbeat UND Trigger genutzt). `QmWeather_Init` liest jetzt Pin-Datei UND
  Trigger-Datei; `QmWeather_IsEnabled` = Pin ODER Trigger armed.
- `qm_hook.cpp`: `Hook_ConsumeEventReceived` liest `Params_0`-Name + `EventTag`
  auf JEDEM Hit (vorher nur verbose) und ruft den Trigger; reine Recon-Logs bleiben.
- Deploy: `qm_weather.txt` (Dauer-Pin) ENTFERNT -> Welt startet natuerlich;
  `qm_weather_trigger.txt`=`Bandages 6` -> Bandage-Use schaltet auf Sturm.

#### Stage 2c - Echtes Weather-Control-Item

Ziel: ein eigenes Item nach Boar-Whistle-Vorlage, das (1) beim Use Wetter setzt
und (2) den Boar-Spawn NICHT ausloest. User-Wahl fuer (2): DLL unterdrueckt den
Spawn (Item-Klon bleibt triviales loses JSON).

##### Verworfene / korrigierte Annahmen (Recon 2026-06-06)

- **ConsumableData-Klon faellt weg.** `UR5ConsumeAbilityData : UPrimaryDataAsset`
  (NICHT `UR5JsonRuntimePDA`) -> NICHT als loses JSON ladbar wie die Item-DAs.
  Ausserdem teilen sich Boar UND Croc dieselbe `DA_ConsumableAbilityData_SpawnerBoar`
  -> sie ist KEIN per-Item-Diskriminator. Der `Params_0`-Substring-Match bleibt
  also auf `SpawnerBoar` (trifft alle Spawner-Whistles + unsere Klone). Eine
  Unterscheidung Klon-vs-Vanilla-Boar ist data-only nicht moeglich; akzeptiert
  (der User fuegt nur das Weather-Control hinzu).
- **L1 vs L2 (aus PLAN-CustomItem_MobSwap-WIP.md):** L1-Klon ist BEWIESEN
  in-game (benutzbar, Cooldown laeuft, **spawnt einen Boar**). L2 galt dort als
  "WIP-Stub, spawnt nichts" - aber ob ein L2-Klon dabei *benutzbar* bleibt (und
  damit den Spawn GRATIS unterdrueckt) war NIE getestet. Genau das de-risken wir
  in 2c-1, BEVOR wir den DLL-Spawn-Suppress-Hook bauen.

##### Stage 2c-1 - Klon-Test-Pak + Weather-on-Use (GETESTET - teilweiser Erfolg)

IN-GAME-BEFUND (2026-06-06): Pak laedt, beide Klone droppen + sind benutzbar.
- **L1**: Boar spawnt, **KEIN** Wetter.
- **L2**: Boar spawnt **NICHT** (Requirement #1 "kein Boar-Spawn" = GRATIS via L2-Daten
  bestaetigt -> DLL-Spawn-Suppress-Hook / Stage 2c-2 entfaellt), aber **KEIN** Wetter.

URSACHE (Log: 0 `EventReceived hit#`-Zeilen ueber die ganze Session, obwohl L1/L2
benutzt wurden): Die Spawner-Whistle aktiviert `GA_SpawnerConsumableAbility_C` - eine
LEERE, rein native `UR5ConsumeAbility`-Subklasse (kein BP-Ubergraph, anders als
Food/Bandage = `GA_CommonConsumableAbility_C`). Ihr Entry-`EventReceived` laeuft
NATIV und erreicht den von uns gehookten Script-VM-ExecFunction-Thunk NIE. Der in
Stage 2b bewiesene Trigger feuert daher fuer die Whistle nicht. -> Fix in 2c-1b.

##### Stage 2c-1b - Completion-Hooks fuer die native Spawner-Whistle (WIDERLEGT in-game 2026-06-06)

**Ergebnis: feuert NICHT.** In-Game-Test mit der 18:28-DLL (Whistle nachweislich
benutzt: L1 Boar / L2 kein Boar) -> **0 `[Consume] hit#`** in der ganzen Session,
obwohl alle drei Thunks sauber installiert waren (`EventReceived`/`OnMontageEnd`/
`FinishAbility`, jeweils `*** INSTALLED ***`). Heisst: die native Spawner-Whistle
dispatcht KEINE dieser drei UFunctions ueber den Script-VM-Exec-Thunk - auch die
Montage-Completion nicht (Annahme "Montage-Callback ist dynamischer Delegate ->
ProcessEvent" stimmt fuer DIESE Ability nicht, oder sie nutzt eine andere/native
Bindung). -> Eskalation auf Stage 2c-1c (globaler ProcessEvent-Netz-Hook). Die
drei Thunk-Hooks bleiben drin (fangen weiterhin BP-getriebenes Food/Bandage +
dienen als Kontrolle).

(Urspruenglicher 2c-1b-Plan, jetzt historisch:)

DLL-only (Trigger-Pak/Sentinel unveraendert: `SpawnerBoar 6`). Zusaetzlich zu
`EventReceived` werden jetzt die zwei weiteren `UR5ConsumeAbility`-UFunctions gehookt:
`OnMontageEnd(FGameplayTag, FGameplayEventData)` + `FinishAbility()`. Beide sind
Montage-Task-Completion-Callbacks, als dynamische Delegates gebunden -> ueber
ProcessEvent invoked -> ExecFunction-Thunk-hookbar, auch fuer die native Whistle.
- `qm_hook.cpp`: geteilter `ConsumeHitCore(ConsumeFnKind, ...)`; drei duenne Detours
  (EventReceived/OnMontageEnd/FinishAbility); `InstallConsumeFnHook()` generisch;
  `TryProbeConsumeHook` installiert alle drei (EventReceived REQUIRED, andere best-effort).
- `qm_weather.{hpp,cpp}`: neuer `QmWeather_TryConsumableTriggerOnComplete()` - Substring-
  Match OHNE Spend-Tag-Gate (Substring `SpawnerBoar` ist der Diskriminator; Food/Bandage
  matchen ihn nie), GetTickCount64-Debounce (1.5s) gegen Doppel-Fire der zwei Completions.
- Build dev: 0 Fehler, deployed nach `E:\Games\...\R5\Binaries\Win64\`.

Test beantwortet: feuert OnMontageEnd/FinishAbility fuer die Whistle (Log:
`[Consume] *** OnMontageEnd hit# ***` / `*** FinishAbility hit# ***` mit
`Params_0 = ...SpawnerBoar`) und kommt der Sturm (`[Weather] *** TRIGGER (via ...) ***`)?
Falls keine der zwei feuert -> Eskalation: globaler ProcessEvent-Netz-Hook (PE-Adresse
ist via `GetProcessEventFn()` schon aufgeloest), gefiltert auf Consume/Spawner-Ability.

##### Stage 2c-1c - Globaler ProcessEvent-Netz-Hook (DONE - deployed 18:46, wartet auf In-Game-Test)

DLL-only (Pak/Sentinel unveraendert: `SpawnerBoar 6`). Nachdem die drei per-UFunction-
Thunks fuer die native Whistle nicht feuern, hooken wir den EINEN zentralen
Dispatcher `UObject::ProcessEvent` (Adresse via `GetProcessEventFn()`/vtable-Slot
schon aufgeloest) - jeder script-geroutete UFunction-Call laeuft hier durch.

`qm_hook.cpp` (neuer Block + Probe-Wiring):
- **Funktional:** wenn `self` von `UR5ConsumeAbility` abstammt (SuperStruct-Walk
  gegen die gecachte `R5ConsumeAbility`-UClass), wird `Params_0` (ConsumableData
  @ 0x3C0) gelesen und an den bestehenden `QmWeather_TryConsumableTriggerOnComplete`
  gefuettert (Substring `SpawnerBoar` + 1.5s-Debounce, geteilt mit dem Thunk-Pfad).
  -> EIN PE-Dispatch auf die Spawner-Ability waehrend des Use genuegt fuer den Sturm.
- **Recon:** PE-Calls mit spawner/montage/consumable-verdaechtigem Klassennamen
  (`Spawn|Boar|Whistle|Montage|Consum|Cooldown|Notify`) werden geloggt (`[PE-recon]
  Cls::Fn`), rate-limited 1/2s pro UFunction. Falls der funktionale Pfad NICHT
  feuert (PE trifft das Ability-Objekt nie), zeigt das Log den echten Chokepoint.
- **Cost:** direkt-gemappter Klassen-Memo (UClass* -> Verdict-Byte, 32768 Slots);
  teure Namensaufloesung/Chain-Walk nur 1x pro Klasse, danach Deref + Array-Lookup.
  Nur installiert wenn `QmWeather_TriggerArmed()` -> Nicht-Weather-User zahlen 0.
  SEH-guarded; forwardet immer das Original.
- Probe-Loop: installiert nach dem Consume-Hook (gleiche Voraussetzungen);
  Exit-Gate um `peNetDone` erweitert. Build dev 18:46: 0 Fehler.

Test beantwortet: kommt der Sturm bei Whistle-Use (`[PE] *** weather triggered ***`)?
Falls nicht: `[PE-recon]`-Zeilen um den Use-Zeitpunkt zeigen, welche Fn(en) die
Whistle real ueber PE dispatcht -> naechste Iteration verdrahtet genau die. Falls
GAR keine `[PE-recon]`/`[PE] ability-call`-Zeilen beim Use -> die Whistle laeuft
voll nativ (kein PE), dann braucht es einen anderen Chokepoint (Spawn-Fn / Cooldown-
GE-Apply / ASC-GameplayEvent).

##### Stage 2c-1 (ALT) - Klon-Test-Pak Detail

Reines Pak, KEINE DLL-Aenderung (Trigger ist datengetrieben). Baut zwei Klone
nach dem bewiesenen L1-PoC-Muster (loses JSON, Vanilla-ItemTag/ConsumableData/
ActivationAbilityTag, Inline-FText-Name); obtainbar via 4 Fiber-Foliage-LootTables
(Weight 1). Pak via repak (`--mount-point ../../../ --version V8B`).

- `DA_CID_Misc_WeatherControlL1_T01` (ItemTag `...SpawnerBoar.L1.T01`) - Erwartung:
  benutzbar, **spawnt Boar**, Wetter -> Sturm. Baseline + bestaetigt die Pipeline.
- `DA_CID_Misc_WeatherControlL2_T02` (ItemTag `...SpawnerBoar.L2.T02`) - HYPOTHESE:
  benutzbar, Cooldown laeuft, **spawnt nichts**, Wetter -> Sturm. Wenn wahr ->
  Requirement #1 ("kein Boar-Spawn") GRATIS, der DLL-Spawn-Suppress-Hook entfaellt.
- Trigger: `qm_weather_trigger.txt` = `SpawnerBoar 6` (Boar/Croc/Klon-Use -> Sturm).
- Deploy: `Quartermaster_WeatherControlTest_P.pak` -> `E:\Windrose\Mods` (= `~mods`-Symlink).

Beide Klone droppen aus Fiber-Pflanzen (Default/Small/Medium/Big Fiber). Ein
Test beantwortet: (a) Klon benutzbar? (b) L1 spawnt Boar? (c) L2 spawnt nichts =
gratis no-spawn? (d) triggert der Use Sturm (Substring `SpawnerBoar`)?

##### Stage 2c-2 - Spawn-Suppression (ENTFAELLT - L2 liefert gratis no-spawn, in-game bestaetigt)

- Recon (breit, log-only - Methodik wie Stage 2a): welche native Funktion spawnt
  den Boar? Kandidaten: EQS-Factory `UR5AbilityTask_SpawnAICharacter::SpawnAICharacter`
  (hat UFunction+ExecFunction -> hookbar; `Count`-Param -> auf 0 setzen unterdrueckt)
  ODER der Montage-Pfad `UR5AMTask_SpawnAICharacter` (KEINE eigene UFunction ->
  ExecFunction-Hook greift evtl. nicht; dann globaler ProcessEvent-Netz-Hook,
  gegated auf das Fenster direkt nach einem SpawnerBoar-Consume).
- Arming: der bestehende Consume-Hook erkennt `SpawnerBoar` in `Params_0` und armt
  "naechsten Spawn unterdruecken"; der Spawn-Hook unterdrueckt + disarmt.

##### Stage 2c-3 - Mapping Item -> Wetter-id (TODO)

- pro-Wetter data-driven via `qm_weather_*.json` (analog `qm_items_*.json`); der
  Substring-Match muss dann per Item unterscheiden (z.B. ItemTag-Lesung statt der
  geteilten ConsumableData) - offen.

##### Stage 2c-4 - Per-Item Cooldown (NEUE Anforderung, User 2026-06-06)

User-Befund: die geklonten Whistles **teilen sich den Cooldown** (untereinander
und mit der originalen Boar-Whistle, vermutlich auch Croc). Soll je Item separat
werden. Cooldown-LAENGE editierbar war schon frueher gewuenscht (Slider, vorerst
zurueckgestellt).

**Warum geteilt (SDK-belegt, 5.6.1-0+UE5-R5):** Consumable-Cooldowns sind ein
**per-Spieler Gameplay-Tag-Bucket**, kein per-Item-Timer.
- `FR5ConsumableActivationParams` (in der ConsumableData) haelt:
  - `CooldownConsumableAbilityTags` (FGameplayTagContainer @ 0x08) = der **Bucket-
    Key**, gegen den die Ability vor Aktivierung prueft (`UR5AbilitySystemComponent::
    K2_GetCooldownRemainingTime(CooldownConsumableAbilityTags)`).
  - `CooldownEffects` (TArray<TSubclassOf<UGameplayEffect>> @ 0xE8) = das beim Use
    angewandte Cooldown-GE (`GE_SpawnerCooldown_C`), das genau diesen Tag fuer X s
    auf die Spieler-ASC legt.
- ALLE Spawner-Whistles (Klon L1/L2 + Vanilla-Boar + Croc) referenzieren **dieselbe
  cooked `DA_ConsumableAbilityData_SpawnerBoar`** -> denselben Bucket-Tag -> ein Use
  blockt alle. (Exakt dasselbe Shared-Asset-Problem wie beim Diskriminator.)

**Cooked-Asset-Recon (2026-06-06, retoc to-legacy + UAssetAPI-Dump) - praezisiert
die Architektur und kippt den Data-Weg:**
- Der Cooldown haengt an der **Ability**, nicht an den Item-Daten:
  `GA_SpawnerConsumableAbility_C` (CDO) -> `CooldownGameplayEffectClass =
  GE_SpawnerCooldown_C`. Standard-GAS: der Cooldown-Check liest die Granted-Tags
  des Ability-Cooldown-GE.
- `GE_SpawnerCooldown_C` **backt den Tag fest ein**: `TargetTagsGameplayEffect-
  Component` grantet `GAS.Cooldown.Cons.Spawner`; Dauer = ScalableFloat Value(1) x
  CurveTable `CT_OtherGEValues[BoarFriend_Cooldown]`.
- `DA_ConsumableAbilityData_SpawnerBoar` hat **`CooldownEffects` LEER** (default);
  ihr `CooldownConsumableAbilityTags` ist nur Check/UI. **-> ein ConsumableData-
  Klon allein aendert am echten Cooldown NICHTS.**
- Item -> `ActivationAbilityTag = GAS.Consumable.Activate.Spawner` triggert die EINE
  geteilte Ability -> EIN Cooldown fuer alle Spawner-Whistles.

**Optionen (aktualisiert 2026-06-06 - Granting bewiesen, Pivot auf (A)):**
- **(A) Per-Item eigene Ability+GE (data-only): GEWAEHLTER WEG (User 2026-06-06).** Ein
  separater Cooldown braucht eine **eigene Ability pro Item** (eigenes Cooldown-GE +
  eigener Tag), denn das GE ist ability-seitig. Klonen von GE + Ability ist mechanisch
  machbar (Vorlage: `.build-tmp/da-patch-test` -> `DataAssetPatcher` Name-Map-Rename +
  `retoc to-zen`).
  - **GRANTING = DATENGETRIEBEN, BEWIESEN (2026-06-06):** `DA_Hero_AbilitySystemParams`
    (`/Game/Gameplay/Character/Player/Parameters/`) listet/importiert **alle**
    Consumable-Abilities explizit (Bandage/Common/Elixir/Food/Lantern/Oil/Pills/Salve/
    **Spawner**/UseConsumableLootContainer) - dump via `retoc to-legacy` + `.build-tmp/
    dumper`. Die fruehere "native Fixliste"-Annahme war FALSCH. Eine geklonte Ability
    laesst sich also granten, indem man dieses DataAsset erweitert/umbiegt.
  - **EINZIGE Rest-Unbekannte: neue Gameplay-Tags aus Pak.** Keine lose
    `DefaultGameplayTags.ini` im Spielordner; Tag-Registrierung ist gecookt/gebacken ->
    statisch nicht klaerbar, ob ein Pak einen NEUEN Activation-/Cooldown-Tag einbringen
    kann. -> Minimal-De-risk gebaut (siehe unten).
- **(A-derisk) DEPLOYED 2026-06-06 ~20:50, wartet auf In-Game-Test:** Kill nur die
  Tag-Frage, KEIN Cooldown-Klon noch. Build `.build-tmp/whistle-cd-test` (-> Pak
  `QmWhistleCdTest_P` in `~mods`):
  1. `GA_SpawnerConsumableAbility` geklont -> `GA_QM_WeatherControl` (`/Game/
     Quartermaster/Abilities/`), `AbilityTriggers[0].TriggerTag` umbenannt
     `GAS.Consumable.Activate.Spawner` -> **`GAS.Consumable.Activate.QMWhistle`** (NEUER
     Tag). Cooldown-GE bleibt vanilla (Cooldown in diesem Test noch geteilt - egal).
  2. `DA_Hero_AbilitySystemParams` ueberschrieben: ungenutzter Slot
     `GA_Player_DebugTeleport` per NameMap-Rename -> Klon (kein Array-Add-Tooling).
  3. Loser-JSON-Whistle-Pak neu gepackt: **L2** `ActivationAbilityTag` -> neuer Tag;
     **L1** unveraendert (Kontrolle, Spawner-Tag -> vanilla Ability).
  - **Erwartung:** L1 = Boar+Sturm (vanilla-Pfad ok); L2 = Montage, KEIN Boar, Sturm,
    und PE-Log zeigt **`GA_QM_WeatherControl_C::K2_OnEndAbility`** (statt
    `GA_SpawnerConsumableAbility_C`) = neuer Tag + Granting funktionieren. Falls L2
    **inert** (keine Montage/kein Sturm/kein Boar) = neuer Tag wird verworfen -> (A)
    fuer Cooldown tot, zurueck auf (C)/DLL.
  - Falls De-risk gruen: Cooldown-GE-Klon (2. neuer Tag) ist trivialer Folgeschritt.
- **(B) Cooldown-LAENGE editieren (data-only): teilweise.** `GE_SpawnerCooldown_C`
  ScalableFloat-Duration ODER die Curve-Row `CT_OtherGEValues[BoarFriend_Cooldown]`
  per cooked-Patch - dieselbe Technik wie `CooldownsPatcher` (kennt das GE bereits in
  `BoarWhistleAssets`). Erfuellt "Slider", aber GLOBAL/geteilt - **nicht** per Item.
- **(C) DLL-verwalteter per-Item-Cooldown: GEWAEHLTER WEG (User 2026-06-06).** Der
  PE-Hook kennt den Use-Moment schon. Drei Teilprobleme: (1) **Diskriminator** - das
  konkrete Item am Use lesen; (2) den geteilten Game-Cooldown-Check fuer unser Item
  **umgehen**; (3) selbst pro Item-Timer fuehren + Re-Use blocken. (1) ist das Gate.

**De-risk fuer (C)-(1) - DEPLOYED, wartet auf In-Game-Test (DLL 2026-06-06 20:01):**
`qm_hook.cpp` `PeConsumeDiscriminatorRecon()` - am Spawner-Whistle-PE-Hit
(`K2_OnEndAbility`, self=Ability) wird ein **breites Netz** geworfen: Ability-Instanz
(@0x3C0+0x80) + die opake `InventoryView` (@0x3E0, Kopf 0x300) werden roh nach
(a) UObject-Zeigern (Cls+Name; Prize: `DA_CID_*`/Slot/Item) und (b) FName-Werten
(Tag-Strings) gescannt; itemische Objekte eine Ebene tiefer. SEH-geguarded,
rate-limited (1 Scan/3s), forwardet Original. Log-Tag `[PE-disc]`.
- **L1-vs-L2-Test** (untersch. ItemTag `...SpawnerBoar.L1.T01` vs `.L2.T02`): zeigt
  der Scan etwas, das die Items unterscheidet (versch. `DA_CID_*`-Name oder ItemTag)
  -> Diskriminator geloest (loest **gleichzeitig** Stage 2c-3 per-Item-Wetter).
- Naechste Iteration falls leer: tiefer scannen / `R5ScenarioListener_ItemConsumed::
  OnExec` als alternativer Chokepoint (im PE-recon-Log gesichtet).
- Danach (C)-(2)/(3): Cooldown-Check-Bypass + DLL-Timer.

**ERGEBNIS (C)-(1) - GESCHEITERT (In-Game-Test 2026-06-06 ~20:16):** Der `[PE-disc]`-
Scan zeigt fuer Vanilla/L1/L2 **byte-identische** Treffer: dieselbe Ability-Instanz
(`0x...E3A9B270`, geteiltes Singleton), dieselbe ConsumableData (`SpawnerBoar`),
dieselbe opake `InventoryView`, dieselbe `R5BLPlayerView`. **Null** per-Item-Marker
(kein `DA_CID_*`/L1/L2/ItemTag) - der Tiefen-Scan lief sogar in eine (VEH-gefangene)
Access Violation. R5ScenarioListener_ItemConsumed = Scenario-Quest-Node mit fixem
`RequiredItem`-Filter, kein generischer Identitaets-Traeger. Fazit: Die Item-Identitaet
wird **nativ aufgeloest, bevor irgendeine PE-erreichbare Funktion feuert** -> DLL-
Diskriminator am Ability-Layer unmoeglich. -> **Pivot auf Daten-Weg (A)** (Granting
inzwischen bewiesen, s.o.).

**ERGEBNIS Daten-Weg v1 (NEUE Tags) - GESCHEITERT, ABER BEWEISKRAEFTIG (2026-06-06 ~21:01):**
Klon `GA_QM_WeatherControl` mit **neuem** Trigger-Tag `GAS.Consumable.Activate.QMWhistle`,
Grant via DebugTeleport-Slot. In-game: L2 **komplett inert** (kein Cooldown/Effekt/Boar).
`R5.log` zeigt die Ursache hart:
```
!!! R5Check happens !!!  Condition: 'Tag.IsValid()'
Message: Invalid gameplay tag name 'GAS.Consumable.Activate.QMWhistle'
Where:   R5BLGameplayTag.r5bl.cpp:83  (TR5BLUeCppMarshallerTrait<FGameplayTag,R5BLGameplayTag>)
```
-> **BEWIESEN: ein Pak kann KEINEN neuen Gameplay-Tag einbringen.** R5 hat einen eigenen,
geschlossenen Tag-Marshaller (`R5BLGameplayTag`), der jeden Tag hart gegen eine feste
Registry validiert und unbekannte Tags ablehnt (strenger als Standard-UE). Der "neue
Activation- + neue Cooldown-Tag"-Weg ist damit **tot**. (DefaultGameplayTags.ini fehlt
lose -> Tags sind gecookt/baked.)

**Daten-Weg v2 - TAG-RECYCLING (gewaehlt 2026-06-06, DEPLOYED 21:16, wartet auf Test):**
Nur **bereits registrierte** Tags benutzen. Registrierte Consumable-Cooldown-Buckets
(via `to-legacy` belegt): `GE_SpawnerCooldown`->`Cons.Spawner`, `GE_Cooldown_Elixir`->
`Cons.Elixir`, `GE_Cooldown_Medicine`->`Cons.Medicine`, `GE_Cooldown_Potion_Recall`->
`Cons.Recall`. Nur die Spawner-Ability hat ein ability-seitiges Cooldown-GE; die anderen
Consumable-Abilities haben keins (Cooldown via ConsumableData.CooldownEffects).
- Klon `GA_QM_WeatherControl` (Kopie der Spawner-Ability): TriggerTag `Spawner`->`Elixir`
  (registriert), `CooldownGameplayEffectClass`-Import `GE_SpawnerCooldown`->`GE_Cooldown_
  Elixir` (registriert, Bucket `Cons.Elixir` != `Cons.Spawner`). 7 NameMap-Renames.
- DA_Hero: **Elixir-Slot** (statt DebugTeleport) -> Klon. Aktiviert `Activate.Elixir` NUR
  unseren Klon (Vanilla-Elixir-Ability entfernt -> keine Kollision). Kosten: Elixiere
  unbenutzbar (pro Custom-Whistle ein Consumable-Typ "geopfert"; ~4 Buckets verfuegbar).
- L2-Item -> `ActivationAbilityTag = GAS.Consumable.Activate.Elixir`. L1 bleibt `Spawner`
  (Kontrolle). DLL-Wetter + No-Boar unveraendert (haengen an ConsumableData-Name/Item).
- **Offener Mechanik-Punkt (klaert nur der Test):** liest das Cooldown-GATE die ability-
  seitige `CooldownGameplayEffectClass` (-> Cooldown separat) ODER die ConsumableData-
  `CooldownConsumableAbilityTags` (=`Cons.Spawner` -> braucht zusaetzlich ConsumableData-
  Klon mit Check-Tag `Cons.Elixir`)? Minimal-Version zuerst, beobachten.

**ERGEBNIS Tag-Recycling (Test 2026-06-06 ~21:2x):** Klon funktionierte (registrierter
`Activate.Elixir` -> kein R5Check-Crash, L2 nutzbar, Sturm, kein Boar). ABER: (a) der
Cooldown-Check liest die geteilte ConsumableData (`Cons.Spawner`-Bucket), NICHT das
ability-seitige GE -> L2 zeigte gar keinen eigenen Cooldown, und L1/Boar-Whistle (Spawner)
sperrten L2 weiterhin mit. (b) Elixiere waren geopfert (DA_Hero-Override). **User-Entscheid:
Cooldown wird NICHT gebraucht + Elixir-Opfer unerwuenscht -> Tag-Recycling komplett
zurueckgerollt** (L2 zurueck auf `Activate.Spawner`, `QmWhistleCdTest_P` aus ~mods entfernt,
DA_Hero wieder vanilla, Elixiere intakt). Sauberer Baseline: L2 funktional (Sturm+kein Boar
via DLL), geteilter Cooldown (ok), nichts geopfert.

##### Stage 2c-5 - Per-Item-Wetter via ConsumableData-Klon (gewaehlt 2026-06-06, DEPLOYED, wartet auf Test)

Ziel: **Vanilla-Boar-Whistle soll KEINEN Sturm ausloesen**, nur unsere Whistle. Bisher
matchte die DLL den geteilten ConsumableData-Namen `SpawnerBoar` (Params_0 @
`K2_OnEndAbility`) -> alle Spawner-Consumables triggerten.

**Schluessel-Recon (hart verifiziert, nicht geraten):** `DA_ConsumableAbilityData_SpawnerBoar`
ist trivial - nur `ConsumeActivationTag=Activate.Spawner`, leerer Cooldown-Container,
`SpendItemEventTag=SpendConsumable`, `CanCancelAbilityEventTag=Ability.Cmd.Interrupt`,
`SpendCount=0`, `EndSection=EndLoop`. **Kein** Spawn-/LootTable-Config drin (beide
Whistle-Items haben `LootTableData=None`) -> der Boar-Spawn haengt am **ItemTag**-Tier
(`SpawnerBoar.L1` Boar vs `.L2` nichts), NICHT an der ConsumableData. Alle Tags registriert
-> Klon = kein R5Check-Risiko.

**Loesung:** ConsumableData unter eigenem Namen klonen (`DA_ConsumableAbilityData_QmWeather
Whistle` @ `/Game/Quartermaster/Consumables/`, nur Name+Pfad+FolderName umbenannt, Tags 1:1).
L2-Item `ConsumableData` -> Klon. DLL-Sentinel-Substring `SpawnerBoar`->`QmWeatherControl`.
Das ist genau der Diskriminator, den der DLL-Scan nicht fand (alle teilten EINE
ConsumableData) - per Konstruktion erzeugt. Keine neuen Tags, kein DA_Hero-Override,
nichts geopfert. Build: `.build-tmp/whistle-cd-test` (Program.cs umgebaut) -> `to-zen` ->
`QmWhistleConsData_P` Triplet; L2-Item-Pak via repak neu.
- **Erwartung:** Vanilla-Boar + L1 (Spawner-DA) -> Boar, **KEIN Sturm**; L2 (Klon-DA) ->
  **Sturm, kein Boar**. Log: `Params_0='DA_ConsumableAbilityData_QmWeatherControl'` nur bei L2.
- **Fail-Recovery:** L2-`ConsumableData` zurueck auf SpawnerBoar (falls Klon nicht laedt ->
  L2 inert), Sentinel zurueck auf `SpawnerBoar`.

**ERGEBNIS Stage 2c-5 - BESTAETIGT (In-Game-Test 2026-06-06 ~21:46):** Log eindeutig:
L2 -> `Params_0='DA_ConsumableAbilityData_QmWeatherControl'` -> `*** weather triggered ***`;
L1 -> `Params_0='DA_ConsumableAbilityData_SpawnerBoar' (no trigger)`. Per-Item-Wetter
data-only geloest, kein Opfer. Cooldown blieb geteilt (L2 nutzt weiter Activate.Spawner
-> dieselbe Ability).

##### Stage 2c-6 - "Kein Cooldown durch L2" via DLL-Cooldown-Strip (gewaehlt 2026-06-06, DEPLOYED, wartet auf Test)

User-Wunsch: L2 soll **gar keinen** Cooldown ausloesen (auch nicht den geteilten, der
kurz die Vanilla-Boar-Whistle blockt). Optionen erwogen: (1) L2 auf cooldown-freie
Lantern-Ability umbiegen (kein Opfer), (2) Spawner-Ability klonen + Slot opfern,
(3) **DLL entfernt den Cooldown** (gewaehlt - kein Opfer, exaktes Whistle-Verhalten),
(4) so lassen.

**Verifizierte Mechanik (hart, via Dump):** `GA_SpawnerConsumableAbility`-CDO hat genau
EINE Cooldown-Eigenschaft: `CooldownGameplayEffectClass = GE_SpawnerCooldown_C` (grantet
`GAS.Cooldown.Cons.Spawner`). Der applizierte Cooldown haengt also rein an der Ability;
L2 (Item-`ActivationAbilityTag = Activate.Spawner`) triggert sie weiter -> committet
GE_SpawnerCooldown. Unser ConsumableData-Klon hatte schon den CHECK-Bucket geleert (L2
selbst nie blockiert), aber das ANWENDEN bleibt ability-seitig.

**Loesung (DLL):** wenn der Whistle-Weather-Trigger feuert (= unser Klon, debounced 1x/Use),
holt die DLL aus der Ability-Instanz die ASC via reflektierter UFunction
`GameplayAbility.GetAbilitySystemComponentFromActorInfo()` und ruft
`AbilitySystemComponent.RemoveActiveEffectsWithGrantedTags({GAS.Cooldown.Cons.Spawner})`
-> der gerade gegrantete Cooldown wird sofort wieder entfernt.
- Neue qm_ue-Helfer: `GetAbilitySystemComponentFromAbility()`, `RemoveActiveEffectsWith
  GrantedTag()` (Stack-TagContainer; ProcessEvent-Pattern wie SpawnObject/Conv_StringToName,
  inkl. FUNC_Native-Flagflip). Orchestrator `StripSpawnerCooldownAfterWhistle()` in
  qm_hook.cpp, aufgerufen auf BEIDEN Trigger-Pfaden (dedizierter Consume-Hook + globaler
  PE-Hook), gegated auf `applied>=0`. Log-Tag `[Whistle-CD]`.
- DLL neu gebaut + deployed 22:25.
- **Erwartung:** L2 benutzen -> Sturm, kein Boar, **und** Vanilla-Boar/L1 bleibt sofort
  benutzbar (kein Block). Log: `[Whistle-CD] stripped Cons.Spawner cooldown ... removed=N`.
- **Akzeptierter Edge-Case:** L2-Use loescht auch einen gerade laufenden Boar-Whistle-Cooldown.
- **Offen (klaert Test):** ist die ASC bei `K2_OnEndAbility`/`OnMontageEnd` noch via ActorInfo
  greifbar? Falls `[Whistle-CD] no ASC` -> frueherer Hook-Punkt noetig.
- **Fail-Recovery:** alte DLL (vor 22:25) zurueckspielen; Daten-Stand bleibt gueltig.

**ERGEBNIS Stage 2c-6 - BESTAETIGT (User 2026-06-06):** "wie prognostiziert - funktioniert
genau so". L2 spammbar (kein eigener Cooldown), Vanilla-Boar nach L2 sofort wieder nutzbar
(Cooldown-Strip greift). Per-Item-Wetter + kein Cooldown, komplett ohne Opfer, exaktes
Whistle-Verhalten.

##### Stage 2c-7 - Wetter "einmal setzen" statt Dauer-Pin (User 2026-06-06, DONE - deployed 22:47)

User: "kein dauer spam vom wetter, einmal setzen und wenn es sich wieder aendert durch das
Spiel, dann passt das so." -> Heartbeat-Pin (schrieb alle 3s `CheatWeatherID`) raus aus dem
Trigger-Pfad. Neue Semantik in `qm_weather.cpp`:
- **Set-once:** der Trigger schreibt `CheatWeatherID` GENAU EINMAL (`ApplyWeatherSetOnce`).
  Bei Erfolg wird der Heartbeat NICHT armiert (`g_enabled=false`), das Spiel behaelt die
  Wetterkontrolle. Ist die Live-Komponente im Trigger-Moment noch nicht da, wird ein
  BOUNDED Retry (`kApplyOnceWindowMs=15s`) armiert, der nach dem ersten gelandeten Write
  (oder Ablauf) disarmt -> nie eine offene Schreibschleife.
- **Permanenter Pin** bleibt nur fuer das explizite Test-File `qm_weather.txt` (`g_permanentPin`).
- **Multi-Mapping:** `qm_weather_trigger.txt` liest jetzt MEHRERE Zeilen `<substring> <id>`
  (Kommentare/Leerzeilen ignoriert, `#`). Jede Zeile mappt einen ConsumableData-Namens-
  Substring auf ein Wetter -> mehrere Weather-Controls mit unterschiedlichem Wetter parallel
  moeglich. Rueckwaerts-kompatibel zur Single-Line-PoC. Match via `MatchTrigger()` (erstes
  enthaltenes Substring gewinnt).

### Stage 3 - GUI (IMPLEMENTED 2026-06-06, wartet auf End-to-End-GUI-Build-Test)

Der bewaehrte Daten+DLL-Weg ist in die Haupt-Pipeline + das Frontend gegossen. Kein Commit
(PoC-Stand). Bausteine:

- **DLL (deployed 22:47):** set-once + Multi-Mapping (Stage 2c-7).
- **Core `WeatherControlPatcher.cs`** (NEU): faltet den PoC-Klon in Core. `StageClones(...)`
  extrahiert `DA_ConsumableAbilityData_SpawnerBoar` EINMAL mit AES-Key (der Composite-Builder
  ruft `to-legacy` keyless -> daher pre-staged source), klont pro DISTINCT Wetter via
  `DataAssetPatcher` (rename Stem+Pfad+FolderName) nach `/Game/Quartermaster/Consumables/
  DA_ConsumableAbilityData_QmWeatherControl_<Weather>` und leert den Cooldown-Bucket. Liefert
  je Klon `{WeatherId, CloneStem, TriggerToken="QmWeatherControl_<W>", ConsumableDataRef}`.
  **Funktional verifiziert** gegen echte Vanilla-Paks (Wetter 6/4, Dedup von 6 -> 2 Klone,
  Cooldown je 1 geleert, `to-zen`-Triplet baut).
- **`Profile.CustomItem.WeatherId` (int?)**: null = "(vanilla)" (inerter L2-Whistle), 0..13 =
  Wetter. Round-trippt (IncludeFields), in `CloneCustomItems` ergaenzt.
- **`ItemCreatorPatcher`**: bei gesetztem `WeatherId` wird `InventoryItemGppData.ConsumableData`
  auf den per-Wetter-Klon-Ref umgebogen (reiner JSON-Edit, alle registrierten Tags bleiben).
- **`BuildPipeline`**: `ResolveWeatherControlIds` -> `weatherControlsActive` (in `ioStoreActive`
  + `compositeActive`). Neue pre-staged Composite-Source "weather-control" ruft
  `WeatherControlPatcher.StageClones` in den shared staging dir. Ergebnis fliesst via
  `BuildIoStoreCompositeOutput.WeatherControls` zurueck.
- **`GameDeployer`**: `WriteWeatherTriggerConfig(clones)` schreibt `qm_weather_trigger.txt`
  (eine Zeile je distinct Wetter, dedup per Token) bzw. loescht es. DLL wird jetzt auch bei
  Weather-Controls (ohne Buildings) deployed; `RemoveDllIfNoProfilesLeft` behaelt die DLL,
  solange das Trigger-File existiert; `CleanupGame` raeumt es mit auf.
- **Frontend:** Template "Weather Control" (`DA_CID_Misc_SpawnerBoar_L2_T02`, `supportsWeather`)
  im Item-Creator-Katalog; Card bekommt ein "Use effect"-Dropdown ((vanilla) / "Change weather:
  <14 Wetter>") -> `custom.weatherId`. Nur fuer weather-faehige Templates sichtbar.

**Trigger-Kette end-to-end:** GUI `weatherId` -> Profil-JSON -> ItemCreator repointet
ConsumableData -> Composite staged Klon -> Deployer schreibt `qm_weather_trigger.txt` ->
in-game Whistle-Use -> DLL matcht `QmWeatherControl_<W>` -> Wetter einmal gesetzt.

- **(Spaeter / TODO):** echter GUI-Build-Test eines Profils mit Weather-Control (Server +
  Profil + Game-Paks); optional Cooldown-Slider pro Whistle; ggf. Icon fuer das Template.

## Dateien

- NEU: `Tools/DllProxy/dxgi/qm_weather.{hpp,cpp}`
- EDIT: `Tools/DllProxy/dxgi/main.cpp` (init + idle-gate), `qm_hook.cpp` (heartbeat-call + Consume-Hook/Trigger), `build.bat` (TU)
- Deploy-Artefakte: `R5/Binaries/Win64/qm_weather.txt` (Stage-1-Dauerpin, optional), `qm_weather_trigger.txt` (Stage-2b-Trigger)

## Test (Stage 1 - permanenter Pin)

1. Spiel komplett schliessen (DLL ist sonst gelockt; war beim Deploy nicht offen).
2. Spiel starten, in eine Gameplay-Welt laden (nicht Lobby/MainMenu - der Heartbeat ist gameplay-map-gated).
3. Erwartung: nach wenigen Sekunden zwingt sich Sturm-Wetter (id 6) auf und bleibt.
4. Log pruefen: `%LOCALAPPDATA%\R5\Saved\Logs\Quartermaster_Inject.log` -> Zeilen `[Weather] *** PIN ARMED ***` und `[Weather] beat#.. wrote CheatWeatherID .. -> 6 (Storm); CurrentWeatherID=..`.
5. Andere Wetter testen: Zahl in `qm_weather.txt` aendern (0..13), Spiel neu starten. Stoppen: Datei loeschen, neu starten.

## Test (Stage 2b - Consumable-Trigger)

Deploy-Stand: `qm_weather.txt` ENTFERNT, `qm_weather_trigger.txt`=`Bandages 6`.

1. Spiel komplett schliessen und neu starten.
2. In eine Gameplay-Welt laden. Erwartung: Wetter ist NATUERLICH (kein Dauer-Pin mehr).
3. Eine **Bandage** benutzen. Erwartung: nach dem Use zieht **Sturm** auf und bleibt.
4. Log `Quartermaster_Inject.log` -> beim Bandage-Use:
   - `[Consume] *** EventReceived hit#.. ***` mit `Params_0(abilityData) = ... Name='DA_ConsumableAbilityData_Bandages_T01'`
   - `[Weather] *** TRIGGER *** '...Bandages...' matched 'Bandages' -> weather 6 (Storm) [applied now; ...]`
   - danach `[Weather] beat#.. wrote CheatWeatherID 6 -> 6 (Storm)` (Heartbeat haelt den Wert)
5. Anderen Consumable/anderes Wetter testen: `qm_weather_trigger.txt` aendern
   (z.B. `RumBottle 9` fuer Rum->Rainbow), Spiel neu starten. Stoppen: Datei loeschen.

## Test (Stage 2c-1 - Whistle-Klon-Pak + Weather-on-Use)

Deploy-Stand: `Quartermaster_WeatherControlTest_P.pak` in `E:\Windrose\Mods` (=`~mods`),
`qm_weather_trigger.txt`=`SpawnerBoar 6`. Staging:
`.build-tmp/weather-control-test/staging/` (2 Item-DAs + 4 Fiber-LootTables).

1. Spiel komplett schliessen und neu starten.
2. In eine Gameplay-Welt laden.
3. **Fiber-Pflanzen abbauen** (Default/Small/Medium/Big Fiber) bis die zwei
   Test-Items droppen: **"QM Weather Control L1 (Boar test)"** und
   **"QM Weather Control L2 (no-spawn test)"** (Legendary, Misc-Kategorie).
4. **L1 benutzen** -> Erwartung: ein Boar spawnt UND Sturm zieht auf.
5. **L2 benutzen** -> Erwartung (Hypothese): KEIN Boar, Cooldown laeuft, Sturm zieht auf.
6. Beobachten/berichten je Item:
   - Erscheint das Item im Inventar (Name korrekt = Inline-FText funktioniert)?
   - Laesst es sich benutzen (Cooldown laeuft)?
   - Spawnt ein Boar? (L1 ja erwartet, L2 NEIN erwartet)
   - Zieht Sturm auf?
   - Log `Quartermaster_Inject.log`: `[Consume] ... Params_0(abilityData) = ... Name='DA_ConsumableAbilityData_SpawnerBoar'`
     und `[Weather] *** TRIGGER *** '...SpawnerBoar...' matched 'SpawnerBoar' -> weather 6 (Storm) ...`.

Ergebnis steuert das Weitere:
- **L2 benutzbar + spawnt nichts** -> Requirement #1 GRATIS geloest; Stage 2c-2
  (DLL-Spawn-Suppress) entfaellt; weiter zu Stage 2c-3 / GUI.
- **L2 unbenutzbar / spawnt doch** -> Stage 2c-2: DLL-Spawn-Suppress-Hook bauen
  (L1 als Traeger), Recon der Spawn-Funktion.

Test-Pak entfernen: `Quartermaster_WeatherControlTest_P.pak` aus `E:\Windrose\Mods`
loeschen, Spiel neu starten. (Aendert die Fiber-Drops zurueck auf Vanilla.)

## Stage 4 - Rum-Bottle-Basis statt Boar-Whistle (2026-06-07)

Pivot (User): die Wetter-Items basieren jetzt auf der **Rum-Flasche**
(`DA_CID_Food_Rum_Bottle_T03` -> `DA_ConsumableAbilityData_Potion_RumBottle`)
statt auf dem Boar-Whistle. Hart verifiziert (Dump + Round-Trip), nicht geraten.

**Warum Rum besser ist:** die Rum-Consume-Ability ist eine Food-Ability - sie
spawnt **keinen Boar** und hat **kein ability-seitiges Cooldown-GE**. Damit
entfaellt der ganze SpawnerBoar-Ballast (L2-Workaround) UND der DLL-Cooldown-Strip
ist nur noch ein No-op (es gibt keinen `Cons.Spawner`-Cooldown mehr zu entfernen).

**Der Klon (data-only, `WeatherControlPatcher`):** Klon der Rum-ConsumableData ->
`DA_ConsumableAbilityData_QmWeatherControl_<Wetter>` (Name = DLL-Match-Token,
unveraendert). Drei Edits am Klon, alle im Round-Trip-Dump bestaetigt:
- `SpendCount = 0` (ADD - die Rum-Data laesst es auf Default => wuerde verbraucht;
  SpawnerBoar setzte es explizit 0, deshalb war das alte Whistle wiederverwendbar).
- `EffectsOnSpend` geleert (Rum-Buff `GE_Consumable_Potion_RumBottle` raus -> nur
  noch Wetter; `ConsumeEffects`/`BlockAbilities` bleiben fuer das Trink-Feeling).
- `CooldownConsumableAbilityTags` geleert (no-op bei Rum, defensiv beibehalten).

Wichtig: Der "Wetter-Effekt" ist **kein** Daten-GE (neue Tags lehnt der
`R5BLGameplayTag`-Marshaller hart ab). Wetter kommt IMMER von der DLL, die den
ConsumableData-NAMEN liest (`kOffConsumeParams0=0x3C0` in der **Basisklasse**
`R5ConsumeAbility` -> template-agnostisch, kein DLL-Edit fuer die Rum-Basis noetig).

**GUI:** das Boar-"Weather Control"-Template entfernt; das vorhandene
"Rum Bottle"-Template ist jetzt `supportsWeather=true`. "(vanilla)" = normale
Rum-Flasche; ein Wetter gewaehlt = Wetter-Item (kein Verbrauch, kein Buff).

**DLL-Bugfix (gleiche Session):** der globale 1500ms-Debounce in
`QmWeather_TryConsumableTrigger*` hat eine ZWEITE Whistle innerhalb 1,5s
verschluckt (Log-Beweis: Use #7 Sunny 0,9s nach #6 Storm = `(no trigger)`).
Ersetzt durch **name-keyed Debounce** (`kSameItemDebounceMs=2500`): nur dieselbe
ConsumableData wird entdoppelt; verschiedene Wetter (verschiedene Klon-Namen)
blocken sich NIE. Behebt das "irgendwann geht es nicht mehr".

**Offen / Cleanup (kein Blocker):** der DLL-Cooldown-Strip (`[Whistle-CD]`,
qm_hook + qm_ue) ist bei Rum toter No-op-Code - kann in einem Cleanup-Pass raus.

### Test (Stage 4)
1. **Alte Test-Items loeschen** (die 2 Boar-basierten) und im Item Creator neue
   mit Template **"Rum Bottle"** + Use-Effect-Wetter anlegen (z.B. Storm + Sunny).
2. GUI-Build fahren -> deployed Klon-Paks + `qm_weather_trigger.txt` + DLL.
3. Spiel neu starten, in Welt laden.
4. Wetter-Rum benutzen -> Trink-Animation, **kein Verbrauch** (Stack bleibt),
   **kein Rum-Buff**, Wetter wird **einmal** gesetzt.
5. **Schnell** zwischen Storm- und Sunny-Rum wechseln (<1,5s) -> beide triggern
   jetzt (Debounce-Fix). Log: je `[Weather] *** TRIGGER *** '...QmWeatherControl_<W>...'`.
