# Quartermaster Builder - Pending Work

Stand: 2026-06-02

Diese Datei listet nur noch **offene Themen ohne eigenes PLAN-Doc**. Alles wofuer
es ein `Docs/PLAN-*` gibt (egal ob `-NEW`, `-WIP` oder `-DONE`), wird dort getrackt
und steht hier nicht mehr. Done-Historie, Design-Entscheidungen und bereits
erledigte Punkte leben in der Git-History (Frontend-Refactoring zuletzt bei
`915bdf6`).

> Eigene PLAN-Docs (separat getrackt, **nicht** hier): StaticMeshReplacement-WIP,
> CsvLocalizationPatcher-WIP, CustomItem_MobSwap-WIP, ShovelMod-WIP,
> DescriptionOverrides-NEW, DllProxy-OrphanItemSkip-NEW, ShipMusicAddTracks-DONE,
> AddNewBuildModeSlot-DONE, Phase4-AssetPipeline-NEW (+ Archive/LootEditing).

---

## Flame-Preset Phase 3 (Multi-Socket -> Multi-Flame) - blockiert

_Kein eigenes PLAN-Doc - dokumentiert nur hier + in der Commit-History
`82458b2` / `d90ba5b` / `4ff87ca`._

Spike hat gezeigt: SCS-Node-Duplikation per UAssetAPI ist machbar (Roundtrip stable,
RootNodes/AllNodes 3->5 erweitert), aber **UE-Engine crashed beim Laden** mit
"Bad name index 16777472/93" auf der geklonten NiagaraComponent. Root Cause:
`OverrideParameters` StructProperty enthaelt `NiagaraVariableWithOffsetPropertyData`-
Sub-Records die UAssetAPI nicht voll implementiert hat - beim Klonen werden ungelesene
Bytes geteilt und der serialisierte Output enthaelt position-abhaengige Inkonsistenzen
die UE als FName-Index-Out-of-Bounds interpretiert.

Optionen wenn das Thema reaktiviert wird:
- **a)** UAssetAPI um NiagaraVariableWithOffsetPropertyData erweitern (PR upstream oder in unserem Submodule patchen).
- **b)** OverrideParameters auf geklonten Components komplett leeren (Engine faellt auf System-Defaults zurueck) - aber dann sehen die Flammen vielleicht identisch aus, was nicht das Phase-3-Ziel ist.
- **c)** Statt SCS-Node-Duplikation einen 2. BP-Klon spawnen + via Game-Logic an Sockets attachen - braucht aber wieder einen DLL-Hook und ist gegen den "build-time-only"-Ansatz.

Aktuell out-of-scope. PENDING bleibt bei "erster Socket gewinnt".

### Audio-Component-Socket-Support (Teil von Phase 3)

Aktuell hat die Vanilla-`BP_BuildingBlock_FloorTorch_C` Audio-Component kein
`RelativeLocation`-Property, deshalb haengt der Audio-Source am Building-Root. Falls
Audio-Spatialisierung relevant wird: PropertyData neu einfuegen statt nur ueberschreiben.

---

## Offene Fragen / Risiken

- **NiagaraVariableWithOffsetPropertyData in UAssetAPI**: Blocker fuer Flame-Preset-Phase 3. Wenn das jemals re-aktiviert wird, ist UAssetAPI-Submodule-Patch der wahrscheinlichste Pfad.
- **Game-Update-Resilience**: jeder UE-Engine-Versions-Bump kann `qm_scan` Offsets brechen. Recovery-Playbook in `Tools/DllProxy/dxgi/GAME_UPDATE_RECOVERY.md`.
- **Vanilla-DA-Template-Catalog-Bootup**: 849 Eintraege werden beim Web-Start indiziert. Wenn weitere Catalogs dazukommen (StaticMesh, Locres) eventuell auf Lazy-On-Demand umstellen.
- **OverrideParameters bei NiagaraComponent**: aktuell bleiben die System-Defaults (Vanilla-Torch-Niagara) beim BP-Klon erhalten. Falls jemand explizit per-Building die Flammenfarbe/Groesse aendern will: separater Patch-Path noetig.
