# Quartermaster Builder - Pending Work

Stand: 2026-05-20 nach Etappe G code-fertig (G.1 Backend-Read-Infrastructure + G.2 Mesh-driven backend + Bucket-Template + G.3 Frontend-Rewrite mit dynamic Vanilla MI picker). End-to-End-Test ueber GUI pending.

Lebende Plan-Datei fuer den Building-Creator-Workstream. Inhalte:
- Was schon erledigt ist (Done)
- Was als naechstes ansteht (Etappen A-F)
- Die 12 Design-Punkte aus der Planungs-Session (mit Entscheidungen)
- Spaetere Themen (out-of-scope fuer Step 1)
- Offene Fragen / TODOs / Risiken

---

## Done

- Etappe A: DLL liest Items aus `qm_items.json` neben der DLL (Runtime-Loader). Inkl. Self-Locating-Pfad, Mini-JSON-Parser, file-static String-Storage, Logging. Konsumer-Code (Hook + Inject) unveraendert.
  - `Tools/DllProxy/dxgi/qm_config.hpp` umgestellt auf Pointer + writable Count/Filter
  - `Tools/DllProxy/dxgi/qm_config.cpp` komplett neu (Loader)
  - `Tools/DllProxy/dxgi/main.cpp` ruft `QmConfigLoad()` im WorkerThread
  - `Tools/DllProxy/dxgi/qm_items.json` mit aktuellen Dev-Items (QmBedrl + QmPainting)
  - `Tools/DllProxy/dxgi/deploy.bat` kopiert JSON mit
  - **Status:** im Game getestet, funktioniert weiterhin wie bisher, committed (Commit `d3f8053`)

- Etappe A.1 (Idle-Mode): DLL ueberspringt MinHook + UE-Probe komplett wenn `g_injectableItemCount == 0`. Damit ist die DLL bei "Profil ohne Buildings" oder "qm_items.json missing/empty" **vollstaendig untaetig** - kein per-Frame-Overhead, keine Hook-Install-Crash-Surface. Voraussetzung fuer Variant C des DLL-Lifecycle (siehe Punkt 13).
  - `Tools/DllProxy/dxgi/main.cpp`: Early-return im WorkerThread nach Config-Load wenn keine Items konfiguriert. Log-Line: `[Config] no injectable items configured - DLL goes idle (no MinHook, no UE probe)`
  - **Status:** gebaut + deployed, weiter mit bisheriger Items-Liste in qm_items.json gestestet (aktiv-Pfad unveraendert). Idle-Pfad noch nicht in-Game verifiziert (Test-Plan: leere/fehlende JSON, Game starten, log checken).

- Etappe B: Spike-Pipeline als wiederverwendbare Library nach `Tools/QuartermasterCore/BuildingCreator/` extrahiert.
  - `BuildingTemplate.cs`: deklarative Daten-Klassen (`BuildingTemplate`, `MaterialSlotTemplate`, `VectorParamOverride`) + statische Factory `BuildingTemplate.Painting()` als erstes konkretes Template
  - `BuildingPatcher.cs`: orchestriert pro Building die 6 Pipeline-Schritte (Stage + Mesh-Patch + Vanilla-MI-Extract + Per-Slot-Clone+Tint + Vanilla-DA-Extract + DA-Patch). Out-of-scope: retoc to-zen (Pak-Build) und GameDeployer.
  - `BuildingInputs` + `BuildingSlotInputs` als Aufruf-Bundles, `BuildingPatchResult` mit StagedFiles + Warnings fuer SSE-Stream.
  - **Status:** baut clean (warning-only NU1903 vom unverwandten Microsoft.Bcl.Memory). **Noch nicht End-to-End-getestet** - Validierung kommt in Etappe F.

- Etappe C: Backend-API fuer Building Creator.
  - `Tools/QuartermasterCore/Profile.cs` erweitert: neues Feld `Profile.CustomBuildings: List<CustomBuilding>` + Klassen `CustomBuilding` und `CustomBuildingSlot` (Id, TemplateId, Name, Description, CookedFolderPath, AssetPrefix, MeshStem, IconStem, Slots-Dict mit Albedo/Normal/MTRM Custom-Texture-Stems).
  - `GUI/Web/BuildingDto.cs` neu: Web-side `BuildingTemplateDto`, `BuildingTemplateSlotDto`, `CookedFolderScanDto`, `CookedFolderEntryDto`.
  - `GUI/Web/Endpoints/BuildingTemplatesEndpoint.cs` neu: serves `Painting` template catalog (`GET /api/building-templates`).
  - `GUI/Web/Endpoints/BuildingsEndpoint.cs` neu: cooked-folder scan helper (`GET /api/buildings/scan-cooked?path=`) mit Klassifikation pro File (mesh/icon/texture/material/matinst/blueprint/data/sidecar/other).
  - `GUI/Web/Endpoints/ProfilesEndpoint.cs` erweitert: `CloneCustomBuildings` + `CloneCustomBuildingSlots` Helper, neuer Summary-Feld `customBuildingCount`.
  - `GUI/Web/Program.cs`: neue Endpoints registriert.
  - **Status:** QuartermasterCore baut clean. Web baut, sobald die laufende Quartermaster.Web-Instanz beendet wird (Datei-Lock auf bin\Debug\net10.0\Quartermaster.Web.dll). **Noch nicht End-to-End-getestet**.

- Etappe E: Game-Deployer + Build-Pipeline-Integration.
  - `Tools/QuartermasterCore/SteamLocator.cs`: neuer `FindBinariesWin64Dir()` Helper - resolved Game's R5/Binaries/Win64 vom Vanilla-Pak abgeleitet.
  - `Tools/QuartermasterCore/Deploy/GameDeployer.cs` neu:
    - `EnsureDllInstalled()`: idempotente DLL-Installation (skipped wenn schon da, mit Guard gegen Game-eigene dxgi.dll), kopiert `C:\Windows\System32\dxgi.dll` -> `<Game>/Binaries/Win64/dxgi_org.dll` als Renamer falls noch nicht da, dann Proxy-DLL.
    - `WriteItemsJson(buildings, tabFilter)`: schreibt `qm_items.json` neben die DLL. Empty list -> leere `items`-Array, DLL geht in Idle-Mode dank Etappe A.1.
    - `CleanupGame(pakBasename?)`: explizite Total-Uninstall-Action (DLL + JSON + optional Pak-Triple).
  - `Tools/QuartermasterCore/BuildPipeline.cs` erweitert:
    - `_buildingPatcher` Field + Construction
    - `PatchCustomBuildings(profile, tmpDir)`: pro Building Tool-Resolution (retoc, usmap, AES, paks dir) + `BuildingPatcher.Patch(...)` Aufruf
    - `ResolveBuildingTemplate(id)` + `BuildBuildingInputs(b, template)` Helper
    - `HasCustomBuildingsConfiguration(profile)` Helper mit Per-Entry-Validity-Gate (skipped skeleton cards)
    - Nach Pak-Build: GameDeployer wird invoked nur wenn `buildings.Count > 0` (DLL deploy) ODER wenn DLL bereits im Target (dann empty JSON fuer Idle-Mode). Stack/Loot-Only-Profile bleiben unbehelligt.
    - `BuildPipelineResult.BuildingResults` neu fuer Per-Building-Report
    - Cleanup-Liste um `__buildings`-Temp-Dir erweitert
  - `GUI/Web/Endpoints/BuildEndpoint.cs` erweitert: `customBuildings`-Block im JSON-Response mit BuildingId/TemplateId/OutputDaStem/StagedFileCount/Warnings pro Building.
  - **Status:** `QuartermasterCore` baut clean. Web-Compile blockiert durch laufende Quartermaster.Web (PID 13760) + VS Insiders Lock auf `bin\Debug\net10.0\Quartermaster.Web.dll`. Aenderung in BuildEndpoint.cs ist trivial typeof-konsistent (`Linq.Select` ueber `List<BuildingPatchResult>` mit `using BuildingCreator;` neu), kein Risiko fuer Compile-Fail. **End-to-End-Test kommt in Etappe F**.

- Etappe D: Frontend Building-Tab.
  - `GUI/Web/wwwroot/tabs/buildings.{html,css,js}` neu (Card-Liste, Cooked-Folder-Picker mit Debounce-Scan, Per-Slot-Inputs fuer Image-Stem/Path).
  - `GUI/Web/wwwroot/index.html`: neuer Tab-Button + CSS/JS-Link.
  - `GUI/Web/wwwroot/app.js`: `buildingTemplates`-State, `TAB_NAMES` erweitert, `loadBuildingTemplates` Hook in Boot, Tab-Switch-Render, `onSave`/`onNew`/`loadProfile` Init fuer `customBuildings`, `bindBuildingsHandlers()`.
  - **Status:** Code vollstaendig. Nicht-Blockierende Auto-Scans pro Card. Warnings fuer fehlende mesh/icon/required-albedo + Hinweis auf User-cooked Materials die der Build skippt.

- Etappe F (End-to-End-Test): Painting via GUI gebaut + im Game platzierbar. Validierte den End-to-End-Flow vom User-Cooked Folder zum spielbaren Building.

- Etappe G.1 (Backend Read Infrastructure, Commit `fdfa559`):
  - `Tools/QuartermasterCore/BuildingCreator/MaterialInstanceInspector.cs`: liest MaterialInstanceConstant uassets (Legacy-Format), exposed Scalar/Vector/Texture-Param-Bloecke + Parent-Master-Material-Ref. Reader-Pattern aus `.build-tmp/mi-probe`.
  - `Tools/QuartermasterCore/BuildingCreator/VanillaMaterialCatalog.cs`: lazy Catalog ueber alle MI_*.uasset im Vanilla-Pak-Set (via CUE4Parse provider scan). Public API: `Search(query, limit)`.
  - `Tools/QuartermasterCore/BuildingCreator/CookedFolderInspector.cs`: liest Mesh-Slot-Liste + alle user-cooked MIs im Folder.
  - `GUI/Web/BuildingDto.cs` + `VanillaMaterialsEndpoint.cs` + `BuildingsEndpoint.cs`: 3 neue Endpoints:
    - `GET /api/vanilla-materials?search=&limit=` -> catalog search
    - `GET /api/vanilla-materials/inspect?path=` -> on-demand retoc + Param-Inspection
    - `GET /api/buildings/inspect-cooked?path=&meshStem=` -> Mesh + User-MI defaults
  - Verifiziert via curl: hunderte MIs im Catalog (Wood=35, Glass=11), MI_CraftStation_01 inspect zeigt 4 scalars + 1 vector + 3 textures + parent=M_Object, SM_QmPainting_01 zeigt 2 mesh-slots ("WorldGridMaterial" + "lambert1") mit korrekten User-MI-Refs.

- Etappe G.2 (Mesh-driven Backend + Bucket-Template, Commit `d5566ce`):
  - `Profile.cs` CustomBuildingSlot: alte Custom-Texture-Felder raus, neu: VanillaMaterialParentPath + ScalarParams + VectorParams + TextureParams (Dicts).
  - `BuildingTemplate.cs`: Slots/MaterialSlotTemplate/VectorParamOverride raus. Templates definieren jetzt nur gameplay-side stuff. Neue Factory `Bucket()` mit `DA_BI_Bucket_01` + `SM_BucketWooden_01` + `T_BI_Bucket_01`.
  - `BuildingPatcher.cs` Rewrite: mesh-driven slot iteration. Pro Slot clone des user-gepicktem Vanilla-MIs, dann Scalar+Vector+Texture-Param-Patching ueber 3 separate Wege (NameMap rewrite fuer Texturen, UAssetAPI in-place edit fuer Scalar+Vector). Kein Param-Add-Path noetig (Variant A).
  - `BuildPipeline.BuildBuildingInputs`: liest CookedFolderInspector um Slot-Liste zu derivieren, merged Profile.CustomBuildingSlot per index.
  - `BuildingTemplatesEndpoint`: ships Painting + Bucket templates.
  - Verifiziert via /api/building-templates: beide Templates da.

- Etappe G.3 (Mesh-driven Frontend, Commit `32895f4`):
  - `app.js` `migrateLegacyCustomBuildings()`: detected alte CustomBuildingSlot Schema beim Profile-Load, wirft Buildings raus + Warning-Alert (Hard-Break wie locked).
  - `tabs/buildings.js` (full rewrite): nach cookedFolderPath + meshStem set fires `/api/buildings/inspect-cooked` und cached pro building.id. Pro mesh-slot ein Parent-Picker (live-search ueber /api/vanilla-materials). Bei Parent-Pick fires `/api/vanilla-materials/inspect` + rendert Param-Controls dynamisch:
    - Scalar -> number input + reset-to-Vanilla button
    - Vector -> HTML color picker + alpha number input + reset
    - Texture -> dropdown der T_*-Stems aus dem Cooked-Folder + reset
  - Pre-Fill: wenn user-cooked MI fuer den Slot denselben parentPath wie das vom User gewaehlte Vanilla-MI hat, deren Werte als non-destructive defaults eingetragen.
  - Required-Banner extended fuer per-slot VanillaMaterialParentPath.
  - `tabs/buildings.css`: Styling fuer alle neuen UI-Komponenten.

- (vorher) Smart-Reuse-Pool fuer Spawn-Widgets - Items verschwinden nach 8 Re-Opens-Bug gefixt, stabil bei Pool=2 nach 28 Re-Opens
- (vorher) Stale-Donor-Detection - Crash-bei-Re-Open-Bug gefixt
- (vorher) Vanilla-MI-Klon-Pattern fuer Custom-Materials validiert (Painting mit eigenem Bild + Holzrahmen funktioniert im Game)

---

## Naechste Schritte

### Etappe F (in dieser Reihenfolge)

**Game-Verzeichnis-Pre-State (19.05. abends bereinigt - bereit fuer Reproduktion via GUI):**
- `~mods/` ist **leer** (alte Spike-Paks QmBedrl_P* + QmPainting_P* geloescht)
- `qm_items.json` in `Binaries/Win64/` ist auf `{ "items": [] }` zurueckgesetzt -> DLL geht beim naechsten Game-Start in Idle-Mode (Log: `[Config] no injectable items configured - DLL goes idle`). Damit ist verifizierbar dass die nachfolgenden Aktivierungen wirklich von der GUI-Pipeline kommen.
- `dxgi.dll` + `dxgi_org.dll` bleiben liegen (Variant C: One-Time-Install)

**Cooked-Folder fuer Test-Building** (aus dem 19.05. Spike-Staging, identisch zu dem was der UE-Editor produziert haette):
- Pfad: `E:\Windrose\Mods\Quartermaster\.build-tmp\qm-painting-build\staging\R5\Content\Quartermaster\Items`
- Enthaelt: SM_QmPainting_01, T_QmPainting_Icon, T_QmPainting_Image (Default-Bild), T_QmPainting_White/NormalFlat/MTRMDefault (Shared-VT-Defaults, Punkt 9), MI_QmPainting_Canvas/Frame (User-cooked MIs - werden vom Patcher per Skip-List ignoriert), DA_BI_QmPainting_01 (Vorlage, kein direkter Pickup), MI sind irrelevant da Patcher Vanilla-MI klont

**Test-Schritte:**
1. Quartermaster.Web starten (App schliessen falls noch offen, dann frisch `dotnet run` oder via Quartermaster.App)
2. Buildings-Tab oeffnen, "New Building" klicken
3. Template "Painting" auswaehlen, Name z.B. "Mein Bild", AssetPrefix `QmPainting`, CookedFolderPath = `E:\Windrose\Mods\Quartermaster\.build-tmp\qm-painting-build\staging\R5\Content\Quartermaster\Items`, MeshStem `SM_QmPainting_01`, IconStem `T_QmPainting_Icon`
4. Canvas-Slot: Image stem `T_QmPainting_Image` + Path `/Game/Quartermaster/Items/T_QmPainting_Image`. Frame-Slot leer lassen (nutzt Defaults).
5. Save profile
6. Build-Button im Mods-Tab klicken
7. Verifizieren: `Quartermaster_P.{pak,ucas,utoc}` in `~mods/`, `qm_items.json` in `Binaries/Win64/` enthaelt den Painting-Eintrag (sollte vom Patcher generierte Asset-ID + Path zeigen)
8. Windrose starten, Logs checken: `[Config] loaded 1 item(s)`, Build-Menue -> Decoration -> "Mein Bild" platzieren
9. Smart-Reuse-Test: 30+ Build-Mode Re-Opens, Painting bleibt in Liste, Pool=1
10. Idle-Mode-Test: Building in der GUI loeschen + Save + Build druecken. Verifizieren: JSON wird leer geschrieben, neuer Game-Start landet im Idle-Mode-Log

### Etappe G (in Bearbeitung, gestartet 2026-05-20)

**Architektur-Wandel:** Heute sind Material-Slots **template-driven** (Painting hardcoded mit Frame+Canvas + festem Vanilla-Parent + festen Param-Defaults). Ab Etappe G werden Material-Slots **mesh-driven**: das Mesh selber definiert die Slot-Liste, und der User configed pro Slot komplett frei (Vanilla-Parent, Texturen, Vector/Scalar-Params).

**Recon vom 20.05. morgens (kritisch fuer Design):**
- `DA_BI_Bucket_01` existiert in Vanilla-Decoration -> perfekter Template-Parent fuer Bucket-Test
- `Sources/Vanilla` ist Zen-Format, nicht direkt mit UAssetAPI lesbar - braucht `retoc to-legacy --filter <stem>` als Extract-Step (so wie BuildingPatcher das bereits macht). Alternativ CUE4Parse mit ZenPackage-Provider, das die BuildPipeline schon nutzt
- User-cooked MIs sind direkt mit UAssetAPI lesbar (Legacy-Format, vom UE-Editor produziert)
- **`mi-probe` Spike in `.build-tmp/mi-probe/`** ist bereits voll funktionsfaehig und liest NameMap+ImportMap+Param-Bloecke - kann 1:1 als Reader-Vorlage fuer den `MaterialInstanceInspector` dienen
- **MI_Paintings_01 enthaelt NICHT die erwarteten Standard-PBR-Properties** (Metallic/Roughness/Specular/NormalStrength kommen vom Parent-Master-Material `M_Object`). Was wirklich drin ist:
  - 3 Scalars: `AO Position`, `AO Contrast`, `RefractionDepthBias` (Painting-spezifische artistische Tuning-Params)
  - 2 Vectors: `Edge Color`, `AO Color`
  - 3 Textures: `Albedo`, `MTRM`, `Normal`
- User-cooked `MI_QmPainting_Canvas` hat **exakt die gleichen Param-Names + ExpressionGUIDs** wie das Vanilla-MI, nur andere Werte/Texturen (weil sie vom gleichen Parent `M_Object` vererbt sind)

**Design-Entscheidungen (alle locked):**
- **Vanilla-MI-Parent-Wahl** (locked 19.05.): Variante A - User picked explizit pro Slot aus einem Dropdown
- **Dropdown-Quelle** (locked 19.05.): Full-Scan ueber Vanilla-Paks beim Backend-Start, GUI zeigt Liste mit Such-Filter. **Detail aus Recon**: Sources/Vanilla ist nur partial-Extract - der echte Scan muss aus dem AES-encrypted Vanilla-Pak direkt kommen (via CUE4Parse ZenPackage-Provider, der schon in BuildPipeline laeuft), nicht aus Sources/Vanilla
- **Template-Konzept** (locked 19.05.): bleibt erhalten, aber **nur** fuer Gameplay-Setup (DA-Parent = Snap-Sockets, Collision-Box, Placement-Rules, Size, Category). Materialien gehoeren nicht mehr zum Template
- **Migration bestehender Painting-Buildings** (locked 19.05.): Variante A - Hard break, User legt die "My Painting"-Card im neuen System neu an
- **Param-Set in der GUI** (locked 20.05. nach Recon): Variante A - **dynamisch**. Die GUI rendert genau die Params die in der vom User gewaehlten Vanilla-MI vorhanden sind (statt hardcoded PBR-Set). Heisst: kein Param-Add-Path noetig, kein NameMap-Append, keine Risiko-Klasse fuer "Param erbt vom Parent-M_". Konsequenz: Begriffe wie "Metallic"/"Roughness" tauchen nur auf wenn die jeweilige Vanilla-MI sie hat
- **Pre-Fill aus User-cooked MIs** (locked 20.05.): Variante 1 - **auto pre-fill**. Wenn der User eine MI im Cooked-Folder hat die denselben Parent-Master hat wie das gewaehlte Vanilla-MI, uebernimmt die GUI deren Werte+Texture-Refs als Initial-Defaults. User sieht seine UE-Editor-Arbeit als Startpunkt

**Mesh-driven Material-Slot-Workflow im Detail:**

| Schritt | Was passiert | Wo |
|---|---|---|
| 1 | User cooked seine Assets in UE-Editor (Mesh + Textures + User-MIs auf irgendeinem M_) | UE-Editor (extern) |
| 2 | GUI scannt Cooked-Folder + liest das Mesh (`SM_*.uasset`) und extrahiert **Material-Slot-Namen + Slot-Count** | `CookedFolderInspector` (neu, Backend) |
| 3 | GUI liest jede User-MI im Folder und extrahiert **Texturen + Vector/Scalar-Params** als Default-Werte | `CookedFolderInspector` |
| 4 | Pro Material-Slot zeigt GUI eine Karte mit:<br>- Vanilla-Parent-Dropdown (User picked, mit Such-Filter aus dem Vanilla-MI-Catalog)<br>- Texture-Picker pro Param (pre-filled aus User-MI, Dropdown listet Files aus Cooked-Folder + Shared-Defaults)<br>- Scalar-Slider pro Param (Metallic, Roughness, Specular, NormalStrength etc., pre-filled aus User-MI)<br>- Color-Picker pro Vector-Param (Tint etc., pre-filled aus User-MI) | `buildings.{html,css,js}` (frontend) |
| 5 | Build-Pipeline klont das vom User gewaehlte Vanilla-MI, schiebt User-Params + Texturen rein, patched Mesh-Material-Slots auf die Klone | `BuildingPatcher.cs` (Backend) |

**Welche Properties editierbar sind:**
Dynamisch pro Slot-Pick. **Was die gewaehlte Vanilla-MI in ihren `ScalarParameterValues`/`VectorParameterValues`/`TextureParameterValues`-Bloecken auflistet** -> das ist editierbar. Beispiele:
- `MI_Paintings_01` (Painting-Template Decoration) -> Scalars: AO Position/Contrast, RefractionDepthBias; Vectors: Edge Color, AO Color; Textures: Albedo, MTRM, Normal
- Ein hypothetisches `MI_Metal_01` (z.B. fuer Bucket) -> vermutlich Metallic, Roughness als Scalars
- User waehlt also implizit das Param-Set ueber den Vanilla-Parent-Pick

Pre-Fill bei Cooked-Folder-Match: User-MI-Werte ersetzen die Vanilla-Defaults, wenn beide vom selben Parent-Master abstammen.

**Konkrete Datei-Aenderungen fuer Etappe G:**

| # | Datei | Aenderung | Aufwand |
|---|---|---|---|
| 1 | `Tools/QuartermasterCore/BuildingCreator/VanillaMaterialCatalog.cs` **(neu)** | Beim Backend-Start CUE4Parse-Provider mit AES auf Vanilla-Paks lossen, alle `MI_*.uasset` aus dem AssetRegistry indexen. Index pro MI: PackagePath, DisplayName. Optional Lazy-Cache fuer Inspect-Resultat. Public API: `Search(query, limit) -> VanillaMaterialEntry[]`. | ~2h |
| 2 | `Tools/QuartermasterCore/BuildingCreator/MaterialInstanceInspector.cs` **(neu)** | Liest eine MaterialInstanceConstant (legacy Format) und gibt strukturiert zurueck: ParentMasterMaterialPath, ScalarParams[], VectorParams[], TextureParams[]. Reader-Pattern aus `.build-tmp/mi-probe/Program.cs` extrahiert. Wird sowohl fuer Vanilla-MIs (nach retoc-extract) als auch User-MIs (direkt) genutzt. | ~2h |
| 3 | `Tools/QuartermasterCore/BuildingCreator/CookedFolderInspector.cs` **(neu)** | Mesh-Reader (`SM_*.uasset`): liest StaticMaterials-Array, gibt Slot-Names + Slot-Count + per-Slot Default-MI-Ref zurueck. MI-Reader: nutzt MaterialInstanceInspector. Liefert dem Frontend einen kompakten "InspectionResult"-Struct: alles was die GUI fuer Initial-Render braucht. | ~3h |
| 4 | `Tools/QuartermasterCore/BuildingCreator/BuildingTemplate.cs` | `MaterialSlotTemplate`-Klasse entfernen. Template bekommt jetzt nur noch:<br>- `Id`, `DisplayName`, `Description`<br>- `ParentBuildingDAPath` (Vanilla-DA fuer Snap/Collision/Category)<br>- `CategoryTag`<br>Factory-Methode: `BuildingTemplate.Painting()`, neu `BuildingTemplate.Bucket()` mit `DA_BI_Bucket_01` als Parent. | ~1h |
| 5 | `Tools/QuartermasterCore/Profile.cs` | `CustomBuildingSlot` umbauen:<br>- `VanillaMaterialParentPath: string` (User-Pick aus Dropdown, required)<br>- `ScalarParams: Dictionary<string,float>` (Param-Name -> User-Override-Wert)<br>- `VectorParams: Dictionary<string,float[]>` (Param-Name -> RGBA als 4 floats)<br>- `TextureParams: Dictionary<string,string>` (Param-Name -> Texture-Stem aus Cooked-Folder oder Shared-Default)<br>Alte Felder `CustomAlbedoStem/Path` etc. **raus**. | ~1h |
| 6 | `Tools/QuartermasterCore/BuildingCreator/BuildingPatcher.cs` | Rewrite des MI-Patch-Schritts:<br>- Pro Slot vom Mesh: User-gepicktes Vanilla-MI extrahieren (via retoc to-legacy, cached pro distinct Path)<br>- Klon erstellen, dann **alle drei Param-Bloecke** ueberschreiben:<br>  - ScalarParameterValues: Werte aus `slot.ScalarParams` reinpatchen<br>  - VectorParameterValues: Werte aus `slot.VectorParams` reinpatchen (bestehender Pattern erweitert)<br>  - TextureParameterValues: Texture-Refs aus `slot.TextureParams` via NameMap-Rewrite umbiegen<br>- Mesh-Patch: pro Material-Slot der Index aus dem Mesh -> Klon-MI-Pfad<br>- **Kein Param-Add-Path** noetig (Variant A garantiert dass User-Konfig nur Params hat die in der Vanilla-MI sind) | ~3h |
| 7 | `GUI/Web/BuildingDto.cs` | Neu: `VanillaMaterialDto` (PackagePath, DisplayName) fuer Catalog-Listing. `MaterialInstanceDto` (ScalarParams[], VectorParams[], TextureParams[]) fuer Inspect-Resultat. `CookedFolderInspectionDto` mit Mesh-Slots + per-MI-Defaults. | ~30min |
| 8 | `GUI/Web/Endpoints/VanillaMaterialsEndpoint.cs` **(neu)** | `GET /api/vanilla-materials?search=&limit=` -> Catalog-Search. `GET /api/vanilla-materials/inspect?path=` -> einzelne MI inspizieren (Lazy, on-demand). | ~30min |
| 9 | `GUI/Web/Endpoints/BuildingsEndpoint.cs` | `GET /api/buildings/inspect-cooked?path=&assetPrefix=` neu - liest Mesh + User-MIs, gibt komplett-Struct fuer Frontend zurueck. Existierendes `scan-cooked` bleibt fuer den File-Klassifikations-Panel. | ~1h |
| 10 | `GUI/Web/Endpoints/BuildingTemplatesEndpoint.cs` | Template-Catalog: Slots-Property weg. Bucket-Template als zweites Template hinzufuegen. | ~20min |
| 11 | `GUI/Web/wwwroot/tabs/buildings.{html,css,js}` | Rewrite der Slot-UI:<br>- Wenn `cookedFolderPath` gesetzt -> auto-inspect-cooked -> rendert Slot-Liste dynamisch (Mesh-driven)<br>- Pro Slot:<br>  - Vanilla-Parent: Search-Dropdown mit `/api/vanilla-materials?search=`, on-pick -> `/api/vanilla-materials/inspect` -> Param-UI rendern<br>  - Pre-Fill: wenn User-MI im Folder mit gleichem Parent existiert, alle Werte als Defaults uebernommen (sichtbar als "User-cooked default" pre-fill-state, mit Reset auf Vanilla)<br>  - Scalar-Params: Slider+Number-Input pro Eintrag (mit Min/Max-Heuristik z.B. 0..1 oder -10..10)<br>  - Vector-Params: HTML Color-Input + Alpha-Slider<br>  - Texture-Params: Dropdown aus Cooked-Folder-Files (mit `T_*.uasset` filter) + Default-VTs<br>- Required: VanillaMaterialParentPath pro Slot | ~5h |
| 12 | `GUI/Web/wwwroot/app.js` | Hard-Break-Migration: beim Profile-Load wenn `customBuildings[i].slots[k]` ein altes Schema-Feld (`customAlbedoStem`) hat -> Warning toasten + diese Cards rauswerfen. | ~30min |
| 13 | `Tools/QuartermasterCore/BuildPipeline.cs` | `BuildBuildingInputs` umstellen auf Mesh-driven (`CookedFolderInspector` liefert Slot-Liste, statt vom Template). `HasCustomBuildingsConfiguration`-Gate anpassen (Required: VanillaMaterialParentPath pro Slot). | ~1h |
| 14 | `GUI/Web/Program.cs` | VanillaMaterialCatalog beim Startup initialisieren (lange Initial-Scan, einmalig). VanillaMaterialsEndpoint registrieren. | ~15min |

Geschaetzt 18-22h (voller Tag plus Polish).

**Reihenfolge der Etappe G:**
1. **G.1** - Backend-Lese-Infrastruktur: `MaterialInstanceInspector` + `VanillaMaterialCatalog` + `CookedFolderInspector` + die zwei neuen Endpoints. Independent testbar via Browser/curl bevor wir das Frontend anfassen.
2. **G.2** - Profile-Schema-Wandel + `BuildingPatcher`-Rewrite + Bucket-Template anlegen. Backend voll-funktional ohne Frontend (Tests via direkt Profile-JSON edit).
3. **G.3** - Frontend-Rewrite. Hard-Break-Migration. End-to-End-Test mit Bucket (das war urspruengliches User-Ziel).

**Risiken (Update nach Recon):**
- ~~Param-Add-Path im Patcher~~ - **eliminiert durch Variant A** (dynamische GUI rendert nur was die MI hat -> User kann nichts editieren was nicht im Block ist)
- **Mesh-Material-Slot-Names**: das Mesh kennt die Slots unter Namen oder als Index 0..N-1. Reader muss beides handhaben. SM_QmPainting_01 ist ein konkretes Test-Subjekt da es bereits getestet ist.
- **Vanilla-MI-Catalog-Bootup-Zeit**: Full-Scan via CUE4Parse beim Backend-Start. Vermutlich akzeptabel weil CUE4Parse bereits eine offene Provider-Instanz hat (BuildPipeline), aber zu messen wenn die ersten Tests laufen.
- **Pre-Fill-Match-Robustheit**: User-MI matched gegen Vanilla-Parent via `Parent`-Import-Ref - das ist eindeutig wenn der User im UE-Editor von einem Vanilla-MI abgeleitet hat, **aber** der QmPainting-User hat sein MI gegen `M_Object` (Master-Material) direkt gebaut. In dem Fall ist der "Parent" das Master-Material, nicht ein Vanilla-MI. Match-Strategie: wenn User-MI `Parent == <gewaehltes-Vanilla-MI>` -> direkt pre-fill. Wenn User-MI `Parent == <gleicher-Master-wie-gewaehltes-Vanilla-MI>` -> auch pre-fill (gleiche Param-Struktur). Sonst nicht.
- **Backwards-Compat-Hard-Break**: Profile-JSON-Validator muss tolerant sein (`customBuildings` mit unknown Felder darf nicht crashen, nur warnen + verwerfen).

---

## Die 12 Design-Punkte (Entscheidungen)

### Architektur / Mechanik

1. **DLL-Konfiguration zur Laufzeit vs Compile-Time**
   - Entscheidung: **Runtime-JSON** (`qm_items.json` neben dxgi.dll)
   - Status: DONE in Etappe A

2. **dxgi.dll-Pfad im Game**
   - Entscheidung: Deploy nach `<Game>/R5/Binaries/Win64/dxgi.dll`. Rollback = unser dxgi.dll + JSON + Pak loeschen. Vanilla-Game hat dort keine eigene dxgi.dll.

3. **Pak-Strategie**
   - Entscheidung: **Ein** `Quartermaster_P.pak` pro aktivem Profil (Items + Buildings vereint)

4. **Auto-Patch-Trigger**
   - Entscheidung: **Expliziter "Build"-Button** im Builder triggert den build-endpoint. Das ist der **einzige** Zeitpunkt wo irgendwas passiert. Kein Auto-Sync auf Feld-Save, kein Auto-Deploy bei Profil-Wechsel.

5. **Profil-Isolation**
   - Entscheidung: Nur das aktive (=letzte geladene) Profil ist im Game deployed. Profil-Wechsel selbst triggert nichts - erst wenn User "Build" drueckt wird das aktive Profil neu deployed.

### Asset-Pipeline

6. **Cooked-Ordner-Konvention**
   - Entscheidung: User waehlt den **`Content/`-Wurzel-Pfad** aus, GUI scannt rekursiv. Pro Building filtert sie auf einen Subpath (z.B. `Items/QmPainting_*`).

7. **Welche Assets gehoeren zu welchem Building**
   - Entscheidung: Pro Building: User gibt Asset-Stamm-Praefix (z.B. `QmPainting`). GUI greift alle Files mit diesem Praefix.

8. **Material-Strategy ist im Template festgenagelt**
   - Entscheidung: Aus Bisect wissen wir: User-cooked Materials crashen. Loesung = Vanilla-MI-Klon, **welches** Vanilla-MI geklont wird ist eine **Template-Property**, nicht User-Wahl.
   - Template "Painting" -> klon `MI_Paintings_01`, Albedo auf User-Texture umbiegen.

9. **Default-Texturen WhiteSquare / NormalFlat / MTRMDefault**
   - Entscheidung: **GUI shipt diese 3 VT-Defaults automatisch** (einmal beim ersten Deploy ins Pak). Buildings referenzieren sie. User muss sie nicht manuell anlegen.
   - Werte:
     - White: 4x4 RGBA (255,255,255,255), DXT1, World-Group, sRGB=True, VT=True
     - NormalFlat: 4x4 RGBA (128,128,255,255), Normalmap-Compression, WorldNormalMap-Group, sRGB=False, VT=True
     - MTRMDefault: 4x4 RGBA (0,128,255,255) = M=0/R=0.5/AO=1, Masks-Compression, World-Group, sRGB=False, VT=True

10. **DisplayName + Description Localization**
    - Entscheidung: gleiches CSV-Synthese-Pattern wie der bestehende `ItemCreatorPatcher`. StringTable-Entry pro Building.

### UX

11. **Build-Kategorie im Game**
    - Entscheidung: Template legt Kategorie fest. "Painting" = `BuildingDecoration`.

12. **Status-Anzeige + Deploy-Feedback**
    - Entscheidung: SSE-Pattern aus dem Mods-Tab ("Export building assets"). Streamt Log-Lines + Final-Status live in die GUI.

### DLL-Lifecycle

13. **DLL-Deployment-Strategie (wann ist `dxgi.dll` im Game-Verzeichnis aktiv?)**
    - Entscheidung: **Variant C - Always-deployed, self-disabling**
    - Mechanik:
      - DLL liegt permanent in `<Game>/R5/Binaries/Win64/` (einmaliges One-Time-Install, vom GameDeployer beim ersten Build idempotent kopiert)
      - DLL liest beim DllMain `qm_items.json` daneben (Etappe A)
      - Wenn JSON leer/fehlt oder keine Items darin: **DLL geht in Idle-Mode** - keine MinHook-Init, kein UE-Probe, kein per-Frame-Overhead (Etappe A.1)
      - Build-Button schreibt nur die JSON (mit Building-Liste aus aktivem Profil; bei 0 Buildings -> leere Items-Liste)
    - Warum: 1) User muss nie DLL-Deployment denken, Build-Button macht ein-File-Toggle. 2) DLL-Datei bleibt liegen, aber inaktiv - kein Game-State-Risiko. 3) Schnellstes Toggle (nur 1 File schreiben statt 4).
    - Optionale explizite "Cleanup Game"-Action im Mods-Tab fuer User die _alles_ entfernen wollen (DLL+JSON+Pak).
    - Verworfene Alternativen:
      - A (Active-profile-only, DLL on/off): Build-Button haette DLL bei jedem Profil-Wechsel deployed/entfernt - mehr File-I/O, kein Vorteil
      - B (Any-profile-globally): inkonsistent mit "Only active profile deployed"
      - D (Hybrid wie C aber mit Cleanup-Button): praktisch identisch zu C, der Cleanup-Button kommt eh als optionale Action

---

## Datei-Map (Soll-Stand nach Etappen B-E)

| Pfad | Status | Inhalt |
|---|---|---|
| `Tools/DllProxy/dxgi/qm_config.{hpp,cpp}` | DONE | Runtime-JSON-Loader |
| `Tools/DllProxy/dxgi/qm_items.json` | DONE (Dev-Default) | wird spaeter von GUI geschrieben |
| `Tools/QuartermasterCore/BuildingCreator/BuildingPatcher.cs` | DONE | Orchestriert pro Building: Cooked-Stage + Mesh-Material-Rewrite + Vanilla-MI-Klon + DA-Patch (Patch-Methode + BuildingInputs/SlotInputs/PatchResult) |
| `Tools/QuartermasterCore/BuildingCreator/BuildingTemplate.cs` | DONE | Daten-Klassen + statische `Painting()`-Factory |
| `GUI/Web/BuildingDto.cs` | DONE | `BuildingTemplateDto`, `BuildingTemplateSlotDto`, `CookedFolderScanDto`, `CookedFolderEntryDto` |
| `GUI/Web/Endpoints/BuildingsEndpoint.cs` | DONE | Cooked-Folder-Scan (`GET /api/buildings/scan-cooked?path=`) |
| `GUI/Web/Endpoints/BuildingTemplatesEndpoint.cs` | DONE | Template-Liste (vorerst nur Painting) |
| `Tools/QuartermasterCore/Profile.cs` | DONE | neue Felder `CustomBuildings`, Klassen `CustomBuilding` + `CustomBuildingSlot` |
| `GUI/Web/Endpoints/ProfilesEndpoint.cs` | DONE (C-Teil) | Clone-Logik fuer CustomBuildings beim Duplicate + Summary-Count. **TODO Etappe E**: bei Build-Trigger Rollback-Hook. |
| `GUI/Web/wwwroot/tabs/buildings.{html,css,js}` | DONE | Frontend-Tab mit Cooked-Scan + Per-Slot-Inputs |
| `GUI/Web/wwwroot/index.html` | DONE | Tab-Registration + CSS/JS-Links |
| `GUI/Web/wwwroot/app.js` | DONE (D-Teil) | State, Tab-Switch, loadProfile/onSave/onNew Init, bindBuildingsHandlers |
| `Tools/QuartermasterCore/Deploy/GameDeployer.cs` | DONE | `EnsureDllInstalled()` + `WriteItemsJson(buildings)` + `CleanupGame(pak?)` |
| `Tools/QuartermasterCore/SteamLocator.cs` | DONE (E-Teil) | neuer `FindBinariesWin64Dir()` |
| `Tools/QuartermasterCore/BuildPipeline.cs` | DONE | CustomBuildings-Stage + GameDeployer-Hook nach Pak-Build |
| `GUI/Web/Endpoints/BuildEndpoint.cs` | DONE (E-Teil) | `customBuildings`-Block im Response |

---

## Build- / Deploy-Flow (Soll, nach Etappe E)

1. User klickt "Build" im Buildings-Tab oder Mods-Tab
2. SSE-Stream startet
3. Pro Building (im aktiven Profil):
   - Cooked-Stage (Files vom angegebenen `Content/`-Root mit Asset-Prefix greifen)
   - Vanilla-DA-Extract
   - DA-Patch (NameMap-Renames)
   - Mesh-Patch (Material-Slots auf Klon-MIs umbiegen)
   - Vanilla-MI-Klone (Canvas + Frame) anlegen, Parameter-Override
4. Default-Texturen (WhiteSquare/NormalFlat/MTRMDefault) ins Staging
5. Pak bauen via `retoc to-zen`
6. `GameDeployer`:
   - **EnsureDllInstalled** (idempotent): `dxgi.dll` + `dxgi_org.dll` Setup nach `<Game>/R5/Binaries/Win64/`, falls noch nicht da
   - **WriteItemsJson(buildings)**: `qm_items.json` mit aktueller Building-Liste schreibt (`buildings.Count == 0` -> leere Items-Liste, DLL geht in Idle-Mode)
   - **DeployPak**: Pak-Triple nach `<Game>/R5/Content/Paks/~mods/Quartermaster_P.{pak,ucas,utoc}` schreibt
7. Stream meldet "deployed"

## Cleanup-Flow (optional, separate User-Action im Mods-Tab)

1. User klickt "Cleanup Game" (TBD, nicht implementiert in Etappe E)
2. `GameDeployer.CleanupGame()`:
   - `Quartermaster_P.{pak,ucas,utoc}` aus `~mods/` loeschen
   - `dxgi.dll` aus `Binaries/Win64/` loeschen
   - `qm_items.json` daneben loeschen
3. **NICHT** triggert beim "letztes Building geloescht" - die DLL geht selbst in Idle-Mode wenn keine Items configured sind, das reicht.

---

## Spaetere Themen (out-of-scope fuer Step 1 - nach G)

- **Auto-Suggest fuer Vanilla-Parent**: Etappe G hat Variante A locked (User picked explizit). Spaeter koennte ein "Suggest"-Button neben dem Dropdown Variante B/C nachreichen (Parameter-Signatur-Matching der User-MI gegen Vanilla-MI-Pool, top-3 Vorschlaege).
- **Templates aus Vanilla-DAs auto-generieren**: heute ist jedes Template hardcoded mit `ParentBuildingDAPath`. Spaeter: Backend scannt `Sources/Vanilla/...` nach allen `DA_BI_*`-Files und exposed sie als Templates. Damit bekommen wir alle Floor-Groessen / Wall-Typen / etc. automatisch ohne Code-Eingriff. User picked "Template" = picked Vanilla-DA.
- **Multi-Material-Builder fuer komplexe Meshes**: falls Buildings mehr als ~8 Material-Slots haben (z.B. ein detailliertes Bett mit Frame + Bettzeug + Kissen + Decke + Holzfarbe + Metallbeschlag + ...), wird die Slot-UI eng. Polish-Task fuer Etappe H (Collapse-Group pro Slot, Bulk-Apply-Across-Slots).
- Pak fuer mehrere Profile parallel (statt nur aktives)
- Auto-Deploy bei Profile-Change (statt nur "Build"-Button)
- Live-Reload im Game ohne Restart (DLL haette dafuer `QmConfigReload()` Hook)
- Material-Param-Live-Preview (Sliders in der GUI zeigen Vorschaubild ohne Build-Roundtrip - braucht eigenen Preview-Renderer)
- **Floor/Wall-Snap-Sockets**: beim DA-Klon nicht nur NameMap renamen sondern Snap-Sockets erhalten. Erst beim Floor/Wall-Template noetig.
- **Glass-/Translucent-Materials** (z.B. Bottle): Vanilla-MI-Klon ueber transparente MIs validieren. Eigene Risiko-Klasse weil Shading-Model differs.

---

## Offene Fragen / TODOs / Risiken

- Die Spike-Logik in `.build-tmp/da-patch-test/Program.cs` ist die Quelle fuer Etappe B - sicherstellen dass alle Patch-Schritte sauber uebernommen werden (besonders die Reihenfolge: Cooked-Stage **vor** Mesh-Patch, Material-Klone **vor** DA-Patch).
- `qm_items.json` Default-Datei im Source-Tree: muss spaeter durch eine GUI-erzeugte ersetzt werden. Sicherstellen dass GameDeployer die Datei beim Deploy **ueberschreibt** statt anzuhaengen.
- Beim Profile-Wechsel: **soll** der bisherige Deploy stehen bleiben oder weg? Per Entscheidung 4+5: bleibt stehen bis User explizit auf "Build" drueckt (= Profil-Wechsel triggert nichts).
- **DLL-Source fuer GameDeployer (Etappe E)**: Wo holt der GameDeployer die `dxgi.dll` her die er ins Game kopiert? Aktuelle Annahme: `<Workspace>/Tools/DllProxy/dxgi/dxgi.dll` (Dev-Workflow - GUI laeuft im Source-Tree). Spaeter beim Shipping: Bundled mit der GUI-Installation in einem `Assets/`-Ordner. Vorerst dev-only.
- Test-Plan fuer Etappe F: mindestens **2** Buildings mit gleichem Template anlegen + deployen + im Game beide platzieren + 30+ Re-Opens (Smart-Reuse muss weiter funktionieren).
- **Default-VT-Texturen (Punkt 9 aus PENDING)**: Aktuell muss der User die 3 Texturen (`T_QmPainting_White`, `T_QmPainting_NormalFlat`, `T_QmPainting_MTRMDefault`) selber im UE-Editor cooken und in den Cooked-Folder seines Buildings legen. Der Staging-Step pickt sie via Asset-Prefix `QmPainting` auf. Spaeter: Builder shipt sie automatisch (ergo: Embedded-Resources mit fertig gecooketen .uasset/.uexp/.ubulk). Workaround fuer Etappe F: User stellt sicher dass die Default-Texturen in seinem Cooked-Output liegen.
- **CSV-Localization fuer DisplayName/Description (Punkt 10 aus PENDING)**: NICHT in Etappe E implementiert. Bei Tests wird der Game vermutlich den Localization-Key ("Decoration_<BuildingId>_Name") als Anzeigetext zeigen statt "Mein Bild". Polish-Task fuer Etappe F+ (Pattern wie ItemCreatorPatcher.CsvWritten).

### Recon-Aufgaben **vor** Etappe G

- **Vanilla-DA-Liste fuer Templates**: Pruefen welche Vanilla-`DA_BI_*` als Eltern-Templates Sinn machen. Konkret fuer den geplanten Bucket-Test: gibt's ein `DA_BI_Bucket_01` (oder vergleichbar) in den Vanilla-Paks? Falls nicht: welches Decoration-DA hat passende Snap+Collision (free placement, kein Wall-Lock)? `MI_Paintings_01`-DA ist wall-mounted, nicht ideal fuer einen Eimer auf dem Boden.
- **Vanilla-MI-Catalog-Inhalt sondieren**: Quick-Scan ueber `Sources/Vanilla/R5/Content/**/MI_*.uasset` - wie viele MIs sind das? Welche Naming-Conventions? Brauchen wir Category-Tags oder reicht ein einfacher Path/Name-Substring-Search fuer den GUI-Filter?
- **User-MI-Reader testen**: kann CUE4Parse aus dem User-cooked `MI_QmPainting_Canvas.uasset` (im Spike-Folder) korrekt die Texture-Refs + Param-Werte rauslesen? Spike: kurz einen Test-Reader bauen und am bestehenden Cooked-Folder validieren bevor wir den Inspector in die Pipeline einbauen.
- **Scalar-Param-Add-Path im MI**: pruefen ob `MI_Paintings_01` die Standard-Params (Metallic, Roughness, NormalStrength) direkt drin hat (Edit-Path reicht) oder ob sie vom Parent-M_ geerbt werden (Add-Path noetig). Via UAssetGUI oder direkt im Hex-View nach `ScalarParameterValues` suchen.
