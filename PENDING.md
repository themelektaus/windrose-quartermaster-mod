# Quartermaster Builder - Pending Work

Stand: 2026-05-22 - Etappen A-J alle in-Game verifiziert + Flame-Preset-System (Phase 1+2) committed. Picker-UX-Polish (Recipe/VanillaMI/VanillaBuilding) abgeschlossen. Doc-Set reorganisiert (DONE/WIP/PLAN/NEW-Konvention, Howto neu geschrieben, 5 WIPs refreshed). Aktueller Branch ist 11 Commits vor `origin/main`.

Lebende Plan-Datei fuer den Building-Creator-Workstream. Inhalte:
- Status-Snapshot (was lebt, was offen ist)
- Done (komprimierte Historie der Etappen A-J + Polish)
- Aktive Workstreams (Flame-Presets Phase 3 + Doc-getriebene Themen aus dem WIP-Set)
- Die 12 Design-Punkte aus der urspruenglichen Planungs-Session (mit Entscheidungen, unveraendert)
- Spaetere Themen (out-of-scope momentan)
- Offene Fragen / Risiken

---

## Status-Snapshot

| Bereich | Stand |
|---|---|
| **DLL-Pipeline** (qm_config.cpp / qm_hook / qm_inject / qm_scan / FMallocProxy) | Stabil. Idle-Mode + One-Time-Install + Variant-C Always-deployed-self-disabling. Lifecycle-Pre-Warm gegen Savegame-Pop-In. Skip-Pfad fuer Non-Windows (Linux/Mac CI). Recovery-Playbook in `GAME_UPDATE_RECOVERY.md`. |
| **Asset-Patching** (CUE4Parse/UAssetAPI Build-Time) | Stabil. Mesh-driven Material-Slots, dynamische Vanilla-MI-Param-Editierung, CSV-Loca-Synthese, FText-Key-Rewrite, Recipe-Editor, Vanilla-DA-Template-Catalog (849 Eintraege gescannt). |
| **Building-Routing** | Alle Custom-Buildings landen in der "Vorgefertigte Strukturen"-Tab. tabPurityFilter ist auf `"BuildingBrushes"` fest verdrahtet, FMallocProxy-Bypass haengt die Items per Hook in den BuildingBrush-Panel-Spawn. |
| **Default-Textures** | T_MTRMZero, T_MTRMOne, T_MTRMGlass, T_QmWhite, T_QmNormalFlat - alle gebundelt im Build-Pipeline-Stage, single source of truth. |
| **Flame-Preset-System** | Phase 1+2 done (per-Building BP-Clone mit Mesh-Rewrite, Socket-driven Position/Rotation/Scale aus `flame`-Socket - name-agnostic, erster Socket gewinnt). Phase 3 (Multi-Socket -> Multi-Flame per SCS-Node-Duplikation) gespiked + rolled back wegen UE-Load-Crash (NiagaraComponent.OverrideParameters Subtype in UAssetAPI nicht voll implementiert). Offen, Spike-Notizen siehe Commit-History `82458b2`/`d90ba5b`/`4ff87ca`. |
| **GUI-Polish** | Building-Tab-Picker (Recipe + VanillaMI + VanillaBuilding) verhaelt sich konsistent: Click-/Focus-/Tipp-Open, Re-Open-Suppression nach Pick, token-driven CSS. |
| **Docs** | DONE/WIP/PLAN/NEW-Naming-Convention. Howto neu geschrieben (GUI-zentriert). 5 WIP-Docs (Csv-Loca, MobSwap, Shovel, StaticMesh, Phase4) auf aktuellen Code-Stand gebracht. |

---

## Done (komprimierte Historie)

### Etappen A-F (Building-Creator-Grundgeruest)

- **A + A.1**: DLL liest `qm_items.json` zur Laufzeit, geht in Idle-Mode wenn leer (kein MinHook, kein UE-Probe). Commit `d3f8053`, `1841633`.
- **B**: `BuildingPatcher` als wiederverwendbare Library nach `Tools/QuartermasterCore/BuildingCreator/`. Commit `bc5c0fc`.
- **C + D**: Backend-API (BuildingTemplatesEndpoint, BuildingsEndpoint scan-cooked, ProfilesEndpoint mit CustomBuildings-Clone) + Frontend-Tab (Cooked-Folder-Picker, Per-Slot-Inputs). Commit `a37b2b6`.
- **E**: GameDeployer (EnsureDllInstalled / WriteItemsJson / CleanupGame) + BuildPipeline-Integration. Commit `0c10210`.
- **F**: End-to-End-Test - Painting via GUI gebaut + im Game platzierbar.

### Etappe G (mesh-driven Material-Slots)

- **G.1**: Backend-Lese-Infrastruktur (MaterialInstanceInspector, VanillaMaterialCatalog, CookedFolderInspector + 3 neue Endpoints). Commit `fdfa559`.
- **G.2**: Profile-Schema umgestellt auf VanillaMaterialParentPath + Scalar/Vector/Texture-Params-Dicts, BuildingPatcher-Rewrite, Bucket-Template als zweites Template. Commit `d5566ce`.
- **G.3**: Frontend-Rewrite mit dynamischem Vanilla-MI-Picker, Param-Controls (Scalar/Vector/Texture), Pre-Fill aus User-MI bei gleichem Parent-Master. Hard-Break-Migration fuer alte Painting-Cards. Commit `32895f4`.
- **G.4**: CSV-Localization-Synthese (BuildingItemsCsvPatcher) + FText-Key-Rewrite (FTextKeyRewriter, Same-Length-Splice). Commit `da0b05e`.
- Polish: `2ec8f59` Frontend rendert customBuildings + drop false no-output-pak warning, `0a69375` Doc-Update.

### Etappe H1+H2 (Tab-Routing + Recipe-Editor)

- **H1**: Alle Custom-Buildings auf "Vorgefertigte Strukturen"-Tab geroutet via tabPurityFilter=BuildingBrushes. Commit `0ac28b7`.
- **H2 Backend**: VanillaResourceCatalog (159 Resources in 132ms) + RecipePatcher + inspect-recipe-Endpoint. Commit `3b1c3f3`.
- **H2 Frontend**: Per-Building Recipe-Editor mit Add/Remove/Reset, Resource-Search-Dropdown, Count-Input. Commit `b12c563`.
- Polish: `f6904aa` Picker-Unification (alle Dropdowns auf loot-tab-style zentralisiert), `029de08` adaptive FText-Key-Shortening + dark picker input.

### Etappe I (Vanilla-DA-Template-Catalog)

- **I Backend**: VanillaBuildingTemplateCatalog (849 DA_BI_*-Eintraege, exkl. BuildingBrushes/Houses/DecorationBrushes) + VanillaBuildingTemplateInspector (liest PreviewMeshes/Icon/BuildingCost/Name/Description SoftObjectPaths). Painting + Bucket bleiben als Sentinel-Legacy-IDs. Commit `6847e82`.
- **I Frontend**: Template-Picker mit Search + Category-Filter. Lazy-Loader + per-id Inspection-Cache. Commit `79312dd`.

### Etappe J (FMallocProxy-Bypass fuer R5BuildingItem in BuildingBrush-Tab)

- Custom-Items werden ueber FMallocProxy-Bypass in den BuildingBrushes-Panel-Spawn injected (statt nur in den ueblichen Decoration-Pfad). Commit `1df6d61`.
- Structural Multi-Building Fixes + Shared Default Textures. Commit `5801ccf`.
- Vanilla-aware Overrides + Asset-Allowlist + FolderName-Normalization. Commit `8d8e5b3`.
- Alloc/Inject Drop-All-Fallbacks + GAME_UPDATE_RECOVERY-Playbook. Commit `525bd53`.

### Flame-Preset-System

- **Phase 1** (`d90ba5b`): Per-Building BP-Clone mit Mesh-Path-Rewrite. Flame-Niagara + PointLight + Audio aus Vanilla `BP_BuildingBlock_FloorTorch_C` werden pro Building geklont und auf das User-Mesh umgebogen.
- **Phase 2** (`82458b2`): Socket-driven Position/Rotation/Scale - StaticMeshSocketReader liest den ersten Socket im User-Mesh (name-agnostic), PatchSocketTransform schreibt RelativeLocation/Rotation/Scale3D in die geklonten Component-Templates.
- Polish (`4ff87ca`): Empty-MI-Slot-Guard im BuildingPatcher.PatchMeshMaterialSlots (graceful skip statt ArgumentException), korrekter FText-Key fuer Description (`Decorations_NoComfortFloorTorch_Description`).
- **Phase 3** (rolled back): Multi-Socket -> Multi-Flame via SCS-Node-Duplikation. Spike funktionierte als UAssetAPI-Roundtrip, crashed beim UE-Load wegen `NiagaraComponent.OverrideParameters` (SortedParameterOffsets-Subtype nicht implementiert -> Bad name index). Rollback hat Phase 2 + "first socket wins"-Semantik wiederhergestellt.

### GUI-Polish (mehrere Commits)

- **f6904aa**: Picker-Unification - alle Building-Creator-Dropdowns (Recipe-Resource, Vanilla-MI-Parent, Vanilla-Building-Template) verwenden zentralen Picker im loot-tab-Style.
- **406fac2**: Building Picker UX Polish + token-driven CSS Color Cleanup.
- **2574adc**: Re-Open-Suppression beim Building-Template-Picker via "next-user-gesture"-Flag (statt Time-Window). 3 Reopen-Pfade abgedeckt: focusin, click, change (auf detached old Input nach DOM-Replace).

### Infrastruktur / Misc

- **ea6130e**: Deploy skipped dxgi-Inject auf Non-Windows mit klarer Log-Meldung.
- **9da47d6**: Veralteter `Tools/UeBuildingItem/`-Python-Helper aus Repo entfernt.
- **db3e592**: T_MTRMGlass default geshipped + Stem-Listen auf single source of truth zusammengefuehrt.
- **1800fa8**: T_MTRMZero + T_MTRMOne als Default-Texturen geshipped.
- **c738b95**: Savegame-Pop-In-Bug gefixt via Lifecycle-Pre-Warm-Hook.

### Doc-Set

- **df1dec0**: Howto + 5 WIP-Docs auf aktuellen Code-Stand gebracht (Howto komplett neu geschrieben, GUI-zentriert ohne DLL-Recompile-Boilerplate; WIPs mit korrektem Deploy-Stand + Status-Markern; PLAN-Phase4 als Retrospektive mit DLL-vs-CUE4Parse-Aufteilung umgeschrieben).
- **b07740c**: `WIP_AddNewBuildModeSlot.md` -> `PLAN-AddNewBuildModeSlot-DONE.md` (Sparten-Targeting + tabPurityFilter sind seit B5+ in qm_inject.cpp implementiert).
- **13d8a0a**: Doc-Naming standardisiert (`PLAN-*-DONE` / `-WIP` / `-NEW`-Konvention, Howto kapitalisiert+Bindestrich, Screenshots nach `Media/`). 4 Cross-Refs in CLAUDE.md + dxgi-Sources + make_T_MTRMGlass.ps1 angepasst.

---

## Aktive Workstreams / Naechste Themen

### Flame-Preset-Phase 3 (Multi-Socket -> Multi-Flame) - blockiert

Spike hat gezeigt: SCS-Node-Duplikation per UAssetAPI ist machbar (Roundtrip stable, RootNodes/AllNodes 3->5 erweitert), aber **UE-Engine crashed beim Laden** mit "Bad name index 16777472/93" auf der geklonten NiagaraComponent. Root Cause: `OverrideParameters` StructProperty enthaelt `NiagaraVariableWithOffsetPropertyData`-Sub-Records die UAssetAPI nicht voll implementiert hat - beim Klonen werden ungelesene Bytes geteilt und der serialisierte Output enthaelt position-abhaengige Inkonsistenzen die UE als FName-Index-Out-of-Bounds interpretiert.

Optionen wenn das Thema reaktiviert wird:
- **a)** UAssetAPI um NiagaraVariableWithOffsetPropertyData erweitern (PR upstream oder in unserem Submodule patchen).
- **b)** OverrideParameters auf geklonten Components komplett leeren (Engine faellt auf System-Defaults zurueck) - aber dann sehen die Flammen vielleicht identisch aus, was nicht das Phase-3-Ziel ist.
- **c)** Statt SCS-Node-Duplikation einen 2. BP-Klon spawnen + via Game-Logic an Sockets attachen - braucht aber wieder einen DLL-Hook und ist gegen den "build-time-only"-Ansatz.

Aktuell out-of-scope. PENDING bleibt bei "erster Socket gewinnt".

### Doc-getriebene Workstreams (aus den WIP-Plans)

- **PLAN-StaticMeshReplacement-WIP**: Vanilla-Mesh-Replacement (z.B. alle Eichen austauschen). Single-Slot-Replace als Scope. Noch nicht implementiert. Code-Pfad waere ein neuer `StaticMeshPatcher.cs` + Frontend-Tab "Mesh Overrides".
- **PLAN-CsvLocalizationPatcher-WIP**: Internes CSV-Patching laeuft (BuildingItemsCsvPatcher / ItemCreatorPatcher / FTextKeyRewriter), generischer User-facing **Localization-Editor-Tab** fehlt aber. Use-Case: vanilla Items umbenennen (z.B. "Banana" -> "Riesenbanane") via Locres-Override. Plan siehe `PLAN-DescriptionOverrides-NEW.md` (Phase 1 Reader-Roundtrip-Spike ist 1-2h, derisked den Rest).
- **PLAN-CustomItem_MobSwap-WIP**: Mob-Swap-Bug ungeklaert. Wolf-Pfad crasht, Dodo macht keinen Schaden. Pakt ist nicht mehr in `~mods/` deployed. Sources noch da fuer ggf. Reaktivierung.
- **PLAN-ShovelMod-WIP**: Increase/Decrease teilweise OK, **Flatten neben Strukturen** geht nicht (`R5TerraformProcessor_Building` ist der vermutete Gate, ungelegt).
- **PLAN-ShipMusicAddTracks-NEW**: Override-Pipeline lebt (ShipMusicPatcher + ShipMusicSlots + BinkAudioEncoder + ar_writer/ar_parser). Add-Tracks-Plan ist nicht angefangen.
- **PLAN-DescriptionOverrides-NEW**: Vanilla-Item-Texte ueberschreiben via `.locres`. Phase 1 (LocResWriter-Reader-Roundtrip-Spike) noch nicht angefangen.

### Kleinkram / Polish

- **DLL-Source fuer GameDeployer beim Shipping**: erledigt. `dxgi.dll` ist als `EmbeddedResource` in der Web-Assembly gebundled (`Quartermaster.Web.csproj`) und wird auf first launch von `Program.SeedDxgiDllIfMissing()` nach `<DataRoot>/dxgi.dll` rausgeschrieben (= `<exe-dir>/QuartermasterData/dxgi.dll` fuer Deployed-Runs, analog zum *.usmap-Pattern). `GameDeployer.ResolveDllSourcePath()` probt Dev-Tree (`Tools/DllProxy/dxgi/dxgi.dll`) zuerst und faellt auf das seeded File zurueck, sodass Dev-Rebuilds weiterhin gewinnen.
- **Phase-3-Socket-Support fuer AudioComponent**: aktuell hat die Vanilla-`BP_BuildingBlock_FloorTorch_C` Audio-Component kein `RelativeLocation`-Property, deshalb haengt der Audio-Source am Building-Root. Falls Audio-Spatialisierung relevant wird: PropertyData neu einfuegen statt nur ueberschreiben.

---

## Die 12 Design-Punkte (Entscheidungen, unveraendert seit Etappe A)

### Architektur / Mechanik

1. **DLL-Konfiguration zur Laufzeit vs Compile-Time**
   - Entscheidung: **Runtime-JSON** (`qm_items.json` neben dxgi.dll). Status: DONE in Etappe A.

2. **dxgi.dll-Pfad im Game**
   - Entscheidung: Deploy nach `<Game>/R5/Binaries/Win64/dxgi.dll`. Rollback = dxgi.dll + JSON + Pak loeschen. Vanilla-Game hat dort keine eigene dxgi.dll.

3. **Pak-Strategie**
   - Entscheidung: **Ein** `Quartermaster_P.pak` pro aktivem Profil (Items + Buildings vereint).

4. **Auto-Patch-Trigger**
   - Entscheidung: **Expliziter "Build"-Button**. Einziger Zeitpunkt wo irgendwas passiert. Kein Auto-Sync, kein Auto-Deploy bei Profil-Wechsel.

5. **Profil-Isolation**
   - Entscheidung: Nur das aktive (=letzte geladene) Profil ist im Game deployed. Profil-Wechsel selbst triggert nichts.

### Asset-Pipeline

6. **Cooked-Ordner-Konvention**
   - Entscheidung: User waehlt den **`Content/`-Wurzel-Pfad** aus, GUI scannt rekursiv. Pro Building filtert sie auf einen Subpath.

7. **Welche Assets gehoeren zu welchem Building**
   - Entscheidung: Asset-Stamm-Praefix (z.B. `QmPainting`). GUI greift alle Files mit diesem Praefix.

8. **Material-Strategy ist mesh-driven**
   - Entscheidung urspruenglich: User-cooked Materials crashen, Vanilla-MI-Klon ist der einzige sichere Weg.
   - **Update Etappe G**: Slot-Liste kommt vom Mesh, Vanilla-MI-Parent ist User-Pick pro Slot (kein Hardcode mehr im Template).

9. **Default-Texturen** (WhiteSquare / NormalFlat / MTRMDefault + Variants)
   - Entscheidung: **GUI shipt diese VT-Defaults automatisch** beim Build. Buildings referenzieren sie. Heute gebundelt: T_QmWhite, T_QmNormalFlat, T_MTRMZero, T_MTRMOne, T_MTRMGlass.

10. **DisplayName + Description Localization**
    - Entscheidung: CSV-Synthese-Pattern wie `ItemCreatorPatcher`. Heute: `BuildingItemsCsvPatcher` + `FTextKeyRewriter` (Same-Length-In-Place-Splice).

### UX

11. **Build-Kategorie im Game**
    - Entscheidung urspruenglich: Template legt Kategorie fest.
    - **Update Etappe H1**: Alle Custom-Buildings landen in `"Vorgefertigte Strukturen"`. tabPurityFilter ist auf `"BuildingBrushes"` fest verdrahtet, FMallocProxy-Bypass haengt R5BuildingItem-Instanzen in den BuildingBrush-Panel-Spawn.

12. **Status-Anzeige + Deploy-Feedback**
    - Entscheidung: SSE-Pattern aus dem Mods-Tab. Streamt Log-Lines + Final-Status live in die GUI.

### DLL-Lifecycle

13. **DLL-Deployment-Strategie (Variant C - Always-deployed, self-disabling)**
    - DLL liegt permanent in `<Game>/R5/Binaries/Win64/` (idempotenter One-Time-Install)
    - DllMain liest `qm_items.json` daneben
    - Wenn JSON leer/fehlt: DLL geht in Idle-Mode (kein MinHook, kein UE-Probe)
    - Build-Button schreibt nur die JSON

---

## Spaetere Themen (nicht im aktiven Backlog)

- **Auto-Suggest fuer Vanilla-Parent** (G-Planung Variant B/C): gestrichen 2026-05-20.
- **Multi-Material-Builder fuer komplexe Meshes (8+ Slots)**: gestrichen 2026-05-20.
- **Material-Param-Live-Preview**: gestrichen 2026-05-20.
- **Glass-/Translucent-Materials**: vorerst gestrichen 2026-05-20 (T_MTRMGlass-Default ist gebundelt, aber kein dedizierter Flow).
- **Live-Reload im Game ohne Restart**: gestrichen 2026-05-20.
- **Pak fuer mehrere Profile parallel**: kein User-Pain-Point.
- **Auto-Deploy bei Profile-Change**: bleibt explizit beim Build-Button.

---

## Offene Fragen / Risiken

- **NiagaraVariableWithOffsetPropertyData in UAssetAPI**: Blocker fuer Flame-Preset-Phase 3. Wenn das jemals re-aktiviert wird, ist UAssetAPI-Submodule-Patch der wahrscheinlichste Pfad.
- **Game-Update-Resilience**: jeder UE-Engine-Versions-Bump kann `qm_scan` Offsets brechen. Recovery-Playbook in `Tools/DllProxy/dxgi/GAME_UPDATE_RECOVERY.md`.
- **Vanilla-DA-Template-Catalog-Bootup**: 849 Eintraege werden beim Web-Start indiziert. Wenn weitere Catalogs dazukommen (StaticMesh, Locres) eventuell auf Lazy-On-Demand umstellen.
- **OverrideParameters bei NiagaraComponent**: aktuell bleiben die System-Defaults (Vanilla-Torch-Niagara) beim BP-Klon erhalten. Falls jemand explizit per-Building die Flammenfarbe/Groesse aendern will: separater Patch-Path noetig.
