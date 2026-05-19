# Quartermaster Builder - Pending Work

Stand: 2026-05-19 nach Etappe A (DLL Runtime-JSON-Loader deployed, ungetested)

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
  - **Status:** deployed nach `<Game>/R5/Binaries/Win64/dxgi.dll` + `qm_items.json`, **noch nicht im Game getestet**

- (vorher) Smart-Reuse-Pool fuer Spawn-Widgets - Items verschwinden nach 8 Re-Opens-Bug gefixt, stabil bei Pool=2 nach 28 Re-Opens
- (vorher) Stale-Donor-Detection - Crash-bei-Re-Open-Bug gefixt
- (vorher) Vanilla-MI-Klon-Pattern fuer Custom-Materials validiert (Painting mit eigenem Bild + Holzrahmen funktioniert im Game)

---

## Naechste Schritte

### Sofort

- [ ] **Test Etappe A**: Windrose starten und verifizieren
  - Log-Line `[Config] looking for ...qm_items.json` erscheint
  - Log-Line `[Config] loaded 2 item(s), tabPurityFilter='BuildingDecoration'` erscheint
  - Per-Item-Listing erscheint
  - Build-Menue -> Decoration: QmBedrl + QmPainting verfuegbar wie gestern
  - Edge-Case: JSON umbenennen -> Log `[Config] file not present`, keine Items
  - Edge-Case: JSON kaputt machen -> Log `[Config] parse error`, keine Items

### Etappen B-F (in dieser Reihenfolge)

- [ ] **Etappe B**: Spike-Pipeline aus `.build-tmp/da-patch-test/Program.cs` als wiederverwendbare Library nach `Tools/QuartermasterCore/BuildingCreator/BuildingPatcher.cs` extrahieren
  - Schritte abbilden: Cooked-Stage + Mesh-Material-Rewrite + Vanilla-MI-Klon + DA-Patch
  - `BuildingTemplate.cs` als Daten-Klasse (`vanillaMaterialPath`, `parentBuildingDA`, `slotMaterials[]`, `categoryTag`, `defaultTextures`)
  - Geschaetzt 1-2h
- [ ] **Etappe C**: Backend-API
  - `Profile.cs` Schema-Erweiterung: neues Feld `CustomBuildings: List<BuildingDto>`
  - `GUI/Web/BuildingDto.cs` neu
  - `GUI/Web/Endpoints/BuildingsEndpoint.cs` neu (REST: list, create, update, delete; deploy via SSE)
  - `GUI/Web/Endpoints/BuildingTemplatesEndpoint.cs` neu (haerter-kodierte Template-Liste, vorerst nur "Wandbild")
  - Geschaetzt 3-5h
- [ ] **Etappe D**: Frontend
  - `GUI/Web/wwwroot/tabs/buildings.html` neu (Buildings-Liste links + Detail-Form rechts: Template-Picker, Name, Description, Cooked-Pfad, Asset-Praefix, Deploy-Button)
  - `GUI/Web/wwwroot/tabs/buildings.css` neu
  - `GUI/Web/wwwroot/tabs/buildings.js` neu (CRUD-Handler + Deploy-Trigger via SSE)
  - `GUI/Web/wwwroot/index.html` Tab-Button `data-tab="buildings"` einbauen
  - Geschaetzt 3-5h
- [ ] **Etappe E**: Game-Deployer
  - `Tools/QuartermasterCore/Deploy/GameDeployer.cs` neu - `Deploy()` + `Rollback()`
  - `Deploy()`: Pak triple -> `<Game>/R5/Content/Paks/~mods/Quartermaster_P.{pak,ucas,utoc}` + `dxgi.dll` -> `<Game>/R5/Binaries/Win64/` (falls noch nicht da) + `qm_items.json` daneben
  - `Rollback()`: Pak triple + dxgi.dll + qm_items.json wieder weg
  - `ProfilesEndpoint.cs` editieren: bei "active profile change" alten Deploy rollbacken
  - Geschaetzt 3-5h
- [ ] **Etappe F**: End-to-End-Test - QmPainting via GUI anlegen -> Deploy -> im Game testen

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
   - Template "Wandbild" -> klon `MI_Paintings_01`, Albedo auf User-Texture umbiegen.

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
    - Entscheidung: Template legt Kategorie fest. "Wandbild" = `BuildingDecoration`.

12. **Status-Anzeige + Deploy-Feedback**
    - Entscheidung: SSE-Pattern aus dem Mods-Tab ("Export building assets"). Streamt Log-Lines + Final-Status live in die GUI.

---

## Datei-Map (Soll-Stand nach Etappen B-E)

| Pfad | Status | Inhalt |
|---|---|---|
| `Tools/DllProxy/dxgi/qm_config.{hpp,cpp}` | DONE | Runtime-JSON-Loader |
| `Tools/DllProxy/dxgi/qm_items.json` | DONE (Dev-Default) | wird spaeter von GUI geschrieben |
| `Tools/QuartermasterCore/BuildingCreator/BuildingPatcher.cs` | TODO Etappe B | Orchestriert pro Building: Cooked-Stage + Mesh-Material-Rewrite + Vanilla-MI-Klon + DA-Patch |
| `Tools/QuartermasterCore/BuildingCreator/BuildingTemplate.cs` | TODO Etappe B | Daten-Klasse |
| `GUI/Web/BuildingDto.cs` | TODO Etappe C | DTO `{ id, name, description, templateId, cookedFolderPath, assetPrefix, displayName, ... }` |
| `GUI/Web/Endpoints/BuildingsEndpoint.cs` | TODO Etappe C | REST + SSE-Deploy |
| `GUI/Web/Endpoints/BuildingTemplatesEndpoint.cs` | TODO Etappe C | Template-Liste |
| `Tools/QuartermasterCore/Profile/Profile.cs` | TODO Etappe C | neues Feld `CustomBuildings` |
| `GUI/Web/wwwroot/tabs/buildings.{html,css,js}` | TODO Etappe D | Frontend-Tab |
| `GUI/Web/wwwroot/index.html` | TODO Etappe D | Tab-Registration |
| `Tools/QuartermasterCore/Deploy/GameDeployer.cs` | TODO Etappe E | `Deploy()` + `Rollback()` |
| `GUI/Web/Endpoints/ProfilesEndpoint.cs` | TODO Etappe E | bei Profile-Change Rollback ggf. |

---

## Build- / Deploy-Flow (Soll, nach Etappe E)

1. User klickt "Build" im Buildings-Tab
2. SSE-Stream startet
3. Pro Building:
   - Cooked-Stage (Files vom angegebenen `Content/`-Root mit Asset-Prefix greifen)
   - Vanilla-DA-Extract
   - DA-Patch (NameMap-Renames)
   - Mesh-Patch (Material-Slots auf Klon-MIs umbiegen)
   - Vanilla-MI-Klone (Canvas + Frame) anlegen, Parameter-Override
4. Default-Texturen (WhiteSquare/NormalFlat/MTRMDefault) ins Staging
5. Pak bauen via `retoc to-zen`
6. `GameDeployer.Deploy()`:
   - Pak triple -> `<Game>/R5/Content/Paks/~mods/Quartermaster_P.{pak,ucas,utoc}`
   - `dxgi.dll` -> `<Game>/R5/Binaries/Win64/` (falls noch nicht da)
   - `qm_items.json` (mit Liste aller Buildings aus aktivem Profil) daneben
7. Stream meldet "deployed"

## Rollback-Flow

1. User loescht letztes Building **und** klickt "Build" (oder spaeter expliziter "Rollback Game"-Button - TBD)
2. `GameDeployer.Rollback()`:
   - `Quartermaster_P.{pak,ucas,utoc}` aus `~mods/` loeschen
   - `dxgi.dll` aus `Binaries/Win64/` loeschen
   - `qm_items.json` daneben loeschen

---

## Spaetere Themen (out-of-scope fuer Step 1)

- **Automatisch alle Materialien mitnehmen** - automatisch erkennen welche Materials das Mesh referenziert + automatisch passende Vanilla-MI-Parents waehlen
- **Automatisch die richtigen Texturen zu den Materialien waehlen** - User bringt nur Bilder mit, GUI ordnet sie korrekt den Albedo/Normal/MTRM-Slots zu
- Mehrere Templates ueber "Wandbild" hinaus (Furniture, Light, ...)
- Pak fuer mehrere Profile parallel (statt nur aktives)
- Auto-Deploy bei Profile-Change (statt nur "Build"-Button)
- Live-Reload im Game ohne Restart (DLL haette dafuer `QmConfigReload()` Hook)

---

## Offene Fragen / TODOs / Risiken

- Die Spike-Logik in `.build-tmp/da-patch-test/Program.cs` ist die Quelle fuer Etappe B - sicherstellen dass alle Patch-Schritte sauber uebernommen werden (besonders die Reihenfolge: Cooked-Stage **vor** Mesh-Patch, Material-Klone **vor** DA-Patch).
- `qm_items.json` Default-Datei im Source-Tree: muss spaeter durch eine GUI-erzeugte ersetzt werden. Sicherstellen dass GameDeployer die Datei beim Deploy **ueberschreibt** statt anzuhaengen.
- Beim Profile-Wechsel: **soll** der bisherige Deploy stehen bleiben oder weg? Per Entscheidung 4+5: bleibt stehen bis User explizit auf "Build" drueckt (= Profil-Wechsel triggert nichts).
- Test-Plan fuer Etappe F: mindestens **2** Buildings mit gleichem Template anlegen + deployen + im Game beide platzieren + 30+ Re-Opens (Smart-Reuse muss weiter funktionieren).
