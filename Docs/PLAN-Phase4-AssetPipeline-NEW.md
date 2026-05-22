# PLAN - Phase 4: Refactor, Asset-Pipeline, Multi-Item

> **VERALTET / Retrospective.** Alle drei urspruenglichen Workstreams
> sind ausgeliefert worden. Seitdem ist die Architektur zweigeteilt:
> Asset-Patching ist vom DLL-Inject-Pfad auf CUE4Parse/UAssetAPI am
> Build-Server migriert, die DLL lebt aber weiter mit reduziertem
> Scope (Build-Menu-Widget-Inject + Item-Config-Loading). Aktive
> Pipeline-Doku: `Docs/HowTo-AuthorBuildingItem.md` + `README.md`.
> DLL-spezifische Recovery-Hinweise: `Tools/DllProxy/dxgi/GAME_UPDATE_RECOVERY.md`.

## Was urspruenglich geplant war

Ein Mod-Autor sollte:

1. Eigenes Mesh + Material + Icon im Unreal-Editor erstellen.
2. Ein `DA_BI_*`-DataAsset auf Basis eines Vanilla-Items duplizieren.
3. Pak cooken und nach `R5/Content/Paks/~mods/` legen.
4. Einen Eintrag in eine Config-Datei schreiben + Ziel-Kategorie waehlen.
5. DLL deployen, Spiel starten - Item erscheint baubar.

Damals war Schritt 3 + 5 (per-Item hardcoded) erledigt, der Rest fehlte.

## Was tatsaechlich geliefert wurde

| Original-Workstream | Lieferung | Heutiger Stand |
|---|---|---|
| **A: Code-Refactor** (main.cpp 1752 Zeilen -> qm_*.cpp/hpp Splits) | Commit `1d29543` "DLL: split main.cpp into focused modules" | Modul-Split lebt unveraendert. 22 .cpp/.hpp unter `Tools/DllProxy/dxgi/` sind aktiv im Repo und werden bei jedem Build deployed |
| **B: Multi-Item Config** (constexpr Array von `InjectableItem`) | Commit `6ca6169` "DLL: multi-item config (workstream B)", spaeter `d3f8053` "DLL: runtime JSON config loader (qm_items.json)" | Aktiv - `qm_config.cpp` liest weiterhin `qm_items.json` neben der DLL. Die Datei wird heute vom GUI-"Build"-Knopf beim Deploy geschrieben, statt von Hand gepflegt |
| **C: Asset-Pipeline-Anleitung** (UE Editor -> Pak Schritt-fuer-Schritt) | Wurde nie in der ursprueglich geplanten Form gemacht, weil die Pipeline auf Build-Time Asset-Patching umgestellt wurde | `Docs/HowTo-AuthorBuildingItem.md` (rewrite mit GUI-zentriertem Flow) |

## Was sich seither geaendert hat (DLL vs. CUE4Parse)

Die Architektur ist heute zweigeteilt:

**DLL-Pfad (lebt weiter, `Tools/DllProxy/dxgi/`):**
- DXGI-Proxy + MinHook-Bootstrap (`main.cpp`)
- UFunction-Detour auf `R5HFSM_BuildingPanel::GetBuildingGroupsByCategoryTag` (`qm_hook.cpp`)
- Per-Inject Widget-Spawn-Pipeline fuer den Build-Menu-Eintrag (`qm_inject.cpp`)
- Runtime-JSON-Loader fuer die Item-Liste (`qm_config.cpp` liest `qm_items.json`)
- Auto-Discovery der UE-Offsets pro Game-Update (`qm_scan.cpp`)
- Infrastruktur: `qm_crash`, `qm_log`, `qm_ue`, `qm_alloc`, `qm_state`, `qm_diag`
- Wird per `GameDeployer.cs` aktiv nach jedem Build deployed (`dxgi.dll`, `dxgi_original.dll`, `qm_items.json` -> `R5/Binaries/Win64/`)

Ohne die DLL erscheinen Custom Buildings nicht im Build-Menu - der `.pak` allein reicht nicht, weil das Build-Menu seine Eintraege erst zur Laufzeit aus dem `GetBuildingGroupsByCategoryTag`-Callsite zieht und die DLL dort fremde DAs reinhaengt.

**CUE4Parse-Pfad (neu hinzugekommen, `Tools/QuartermasterCore/`):**
- DA-Klonen, Mesh-Slot-Rewrite, BP-Cloning, NameMap-Rewrites
- StringTable/CSV-Patcher fuer Lokalisierung
- Per-Building/Per-Item Pak-Inhalt wird am Build-Server erzeugt, nicht zur Laufzeit injiziert

## Warum Asset-Patching vom DLL- auf den CUE4Parse-Pfad migriert wurde

Runtime-Asset-Manipulation per Detour war fragil:
- Pro Game-Update neu kalibrierte Offsets/Hooks fuer jeden Patch-Eingriff.
- Tab-Purity-Heuristik + Spawn-Pool waren Engine-State-abhaengig.
- Lifecycle-Bugs (Save-Game-Pop-in, foreign-item-capture) brauchten
  immer wieder neue Workarounds (siehe Commits `c738b95`,
  `525bd53`, `1df6d61` aus der DLL-Asset-Patching-Phase).

Der Wechsel zu **Build-Time Asset-Patching** (CUE4Parse-basiert) bringt:
- Items + Buildings sind echte vanilla-Klassen (`R5ItemBase`,
  `R5BuildingItem`) im Pak und werden vom Spiel ohne Asset-Code-Patch geladen.
- Patcher laufen offline am Build-Server, der Game-Prozess bleibt
  fuer den Asset-Anteil unberuehrt.
- Multi-Item ist trivial (jeder Profil-Eintrag = ein eigener Patch-Pass).

Was die DLL **weiterhin** macht, ist nicht Asset-Patching sondern UI-Wiring:
sie sorgt dafuer dass die vom Pak gelieferten DAs im Build-Menu-Widget
sichtbar werden. Dieser UI-Hook ist nicht ohne weiteres durch ein Pak
ersetzbar, weil das Widget seine Eintraege ueber einen Code-Pfad zieht
der nicht datengetrieben ist.

## Wo der aequivalente Code heute lebt

| Original | Heute |
|---|---|
| `main.cpp` Boot-Logic | unveraendert in `Tools/DllProxy/dxgi/main.cpp` |
| `qm_config.cpp` JSON-Loader | unveraendert - liest `qm_items.json`. Quelle der Datei: `GUI/Web` schreibt `Profiles/<name>.json`, `Tools/QuartermasterCore` (`BuildPipeline.cs`) generiert daraus `qm_items.json` beim Deploy |
| `qm_inject` OverrideTarget + Spawn-Pool | unveraendert - sorgt fuer Sichtbarkeit im Build-Menu |
| Asset-Pipeline UE Editor -> Pak | `HowTo-AuthorBuildingItem.md` (GUI cook + Build-Button), Patcher-Kette in `Tools/QuartermasterCore/Patchers/*.cs` |

## Lessons Learned (fuer kuenftige Phase-Planung)

- DLL-Detour-Hooks sind ein gueltiges Werkzeug an Stellen wo das Spiel
  einen nicht-datengetriebenen Code-Pfad hat (Build-Menu-Widget). Dort
  lebt die DLL bis heute stabil.
- Aber: fuer reine **Asset-Manipulation** (DAs klonen, Properties
  umschreiben, NameMaps biegen) ist Runtime-Patching die teure Variante.
  Build-Time-Patching am Pak ist robuster, weil das Spiel die fertigen
  Assets wie Vanilla laedt und sich keine Engine-Pfade unter den Fuessen
  veraendern koennen.
- Daraus folgt die heutige Aufteilung: **CUE4Parse fuer Assets, DLL
  fuer den Code-Hook der die Assets sichtbar macht** - statt "alles mit
  einem Werkzeug".
- Wenn ein Plan-Doc drei Workstreams hat und der dritte (Asset-Pipeline)
  die Architektur der ersten beiden grundlegend veraendert: lieber den
  dritten zuerst spiken, dann sieht man frueh wo die Grenze zwischen
  Build-Time- und Runtime-Anteil verlaeuft.
