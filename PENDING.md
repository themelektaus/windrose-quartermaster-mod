# Quartermaster Builder - Pending Work

Stand: 2026-05-19 nach Etappen C+D (Backend-API + Frontend-Tab, ungetestet)

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

## Spaetere Themen (out-of-scope fuer Step 1)

- **Automatisch alle Materialien mitnehmen** - automatisch erkennen welche Materials das Mesh referenziert + automatisch passende Vanilla-MI-Parents waehlen
- **Automatisch die richtigen Texturen zu den Materialien waehlen** - User bringt nur Bilder mit, GUI ordnet sie korrekt den Albedo/Normal/MTRM-Slots zu
- Mehrere Templates ueber "Painting" hinaus (Furniture, Light, ...)
- Pak fuer mehrere Profile parallel (statt nur aktives)
- Auto-Deploy bei Profile-Change (statt nur "Build"-Button)
- Live-Reload im Game ohne Restart (DLL haette dafuer `QmConfigReload()` Hook)

---

## Offene Fragen / TODOs / Risiken

- Die Spike-Logik in `.build-tmp/da-patch-test/Program.cs` ist die Quelle fuer Etappe B - sicherstellen dass alle Patch-Schritte sauber uebernommen werden (besonders die Reihenfolge: Cooked-Stage **vor** Mesh-Patch, Material-Klone **vor** DA-Patch).
- `qm_items.json` Default-Datei im Source-Tree: muss spaeter durch eine GUI-erzeugte ersetzt werden. Sicherstellen dass GameDeployer die Datei beim Deploy **ueberschreibt** statt anzuhaengen.
- Beim Profile-Wechsel: **soll** der bisherige Deploy stehen bleiben oder weg? Per Entscheidung 4+5: bleibt stehen bis User explizit auf "Build" drueckt (= Profil-Wechsel triggert nichts).
- **DLL-Source fuer GameDeployer (Etappe E)**: Wo holt der GameDeployer die `dxgi.dll` her die er ins Game kopiert? Aktuelle Annahme: `<Workspace>/Tools/DllProxy/dxgi/dxgi.dll` (Dev-Workflow - GUI laeuft im Source-Tree). Spaeter beim Shipping: Bundled mit der GUI-Installation in einem `Assets/`-Ordner. Vorerst dev-only.
- Test-Plan fuer Etappe F: mindestens **2** Buildings mit gleichem Template anlegen + deployen + im Game beide platzieren + 30+ Re-Opens (Smart-Reuse muss weiter funktionieren).
- **Default-VT-Texturen (Punkt 9 aus PENDING)**: Aktuell muss der User die 3 Texturen (`T_QmPainting_White`, `T_QmPainting_NormalFlat`, `T_QmPainting_MTRMDefault`) selber im UE-Editor cooken und in den Cooked-Folder seines Buildings legen. Der Staging-Step pickt sie via Asset-Prefix `QmPainting` auf. Spaeter: Builder shipt sie automatisch (ergo: Embedded-Resources mit fertig gecooketen .uasset/.uexp/.ubulk). Workaround fuer Etappe F: User stellt sicher dass die Default-Texturen in seinem Cooked-Output liegen.
- **CSV-Localization fuer DisplayName/Description (Punkt 10 aus PENDING)**: NICHT in Etappe E implementiert. Bei Tests wird der Game vermutlich den Localization-Key ("Decoration_<BuildingId>_Name") als Anzeigetext zeigen statt "Mein Bild". Polish-Task fuer Etappe F+ (Pattern wie ItemCreatorPatcher.CsvWritten).
