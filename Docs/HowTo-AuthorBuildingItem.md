# Howto: Eigenes Building-Item via Quartermaster-GUI authoren

Stand: 2026-05-22

Diese Anleitung beschreibt den End-to-End-Workflow um ein eigenes Building
(Mesh + Icon + Material-Setup + optional Flame-FX) in Windrose zu bringen.
Die komplette Patch-/Pack-Pipeline laeuft ueber die Quartermaster-GUI -
kein retoc-Subprocess, kein DLL-Recompile, keine manuelle .pak-Erzeugung.

Was du selbst machst:

1. Dein Mesh + Icon + Texturen in einem leeren UE 5.6-Projekt importieren
2. Das Projekt cooken (UE-Editor, "Cook Content for Windows")
3. In der Quartermaster-GUI ein neues Building anlegen, das Template
   waehlen, den Cooked-Folder eintragen, pro Material-Slot eine Vanilla-MI
   als Parent picken
4. **Build** klicken

Was die Pipeline automatisch macht:

- Vanilla-Building-DA klonen + NameMap-Rewrite (Mesh/Icon/Recipe/FText-Keys
  auf deine Stems)
- Per-Slot MI-Clones unter `/Game/Quartermaster/Items/MI_<Prefix>_<Slot>`
  mit deinen Param-Overrides
- Lokalisierungs-CSV-Row mit deinem Name + Description
- (Optional) Flame-FX-BP-Clone + ItemClass-Rewrite + Socket-getriebene
  Flammenposition
- `_P.pak/.ucas/.utoc` packen
- `qm_items.json` schreiben + nach `~mods/` deployen

## Was am Ende rauskommt

- Ein neuer Slot in **"Vorgefertigte Strukturen"** in der Build-UI ingame
- Eigenes Icon im Slot, eigener Name + Description im Tooltip
- Beim Bauen erscheint dein Mesh mit deinen Materials
- Optional: Flame-FX (Niagara + Point Light + Loop-SFX) an einem
  Socket deines Meshes

## Voraussetzungen

| Was | Detail |
|---|---|
| **Quartermaster eingerichtet** | GUI laeuft, Vanilla-Setup ist durch (Sources/Vanilla + Icons extrahiert). Siehe README "Run the configurator". |
| **Unreal Engine 5.6.x Editor** | Vom Epic Games Launcher. Gleiche Major-Version wie Windrose (5.6.1). Source-Build nicht noetig. |
| **Eigenes Mesh** | `.fbx` oder `.obj`. UV-Map + Material-Slots gesetzt. Polygon-Count fuer ersten Test < 10k. |
| **Eigene Texturen** | `.png` (RGBA) oder `.tga`. Mindestens Diffuse. Optional Normal, Roughness, Metallic. Power-of-2 Aufloesungen. |
| **Eigenes Icon** | `.png`, quadratisch, 256x256 oder 512x512, transparenter Hintergrund. |
| **Game darf laufen** | Aber nicht muss - die GUI testet sich ohne laufendes Spiel. |

## Pipeline-Uebersicht

```
        UE 5.6 Editor                    Quartermaster GUI                  ~mods/
        =============                    =================
+--------------------+              +-------------------------+           +--------+
| Mesh + Icon +      |   Cook       | New Building +          |  Build    | _P.pak |
| Textures importieren| ---------->  | Template + CookedFolder | --------> | _P.ucas|
| (alle prefixiert)  |  Content     | + Slots + Flame-Preset  |           | _P.utoc|
+--------------------+              +-------------------------+           +--------+
        |                                       ^                              |
        |                                       |                              |
        |  Cooked-Output unter                  |  inspect-cooked              |
        |  Saved/Cooked/Windows/.../Content/    |  liest Mesh +                v
        |  Quartermaster/Items/                 |  listet Slots + MIs       Ingame:
        v                                       |                          Build-Menu
   SM_QmFoo_01.uasset/.uexp/.ubulk              |
   T_QmFoo_Icon.uasset/.uexp/.ubulk             |
   T_QmFoo_Diffuse.uasset/.uexp/.ubulk          |
   MI_QmFoo_Body.uasset/.uexp (optional)        |
```

## Schritt 1: UE 5.6 Editor installieren

1. Epic Games Launcher -> **Unreal Engine** Tab -> **Library** -> **Engine
   Versions** -> Plus -> **5.6.x**
2. Installations-Optionen: nur **Engine** reicht. Starter-Content,
   Templates, Platforms koennen weg.
3. Wartezeit: 20-60 Min, ~25 GB Plattenplatz.

## Schritt 2: UE-Projekt anlegen (einmalig)

1. UE 5.6 starten -> **New Project** -> **Games** -> **Blank**
2. **Blueprint**, **No Starter Content**, **Raytracing** aus
3. Project Name: z.B. `WindroseMod`, Location: `E:\UnrealProjects\`
4. **Create** - Wartezeit 1-2 Min (Shader-Compile)

### Cook-Settings einstellen

- **Edit** -> **Project Settings** -> **Project** -> **Packaging**
- **Cook everything in the project content directory** = an
- **Use Pak File** = aus (Quartermaster pak-t selbst)

### Folder-Konvention

Lege im Content Browser **einmalig** `/Game/Quartermaster/Items/` an.
Alle Assets fuer Custom-Buildings landen dort - die Pipeline scannt
genau diesen Ordner.

## Schritt 3: Asset-Prefix waehlen

Jedes Building braucht einen eigenen **Asset-Prefix** der konsequent
in allen Asset-Namen vorkommt. Der Prefix:

- macht deine Assets eindeutig (mehrere Buildings koexistieren ohne
  Namens-Kollision)
- ist der Filter mit dem die Pipeline scannt was zu deinem Building gehoert
- wird **automatisch aus deinem Mesh-Namen abgeleitet**: aus
  `SM_QmWieselburger_01` wird der Prefix `QmWieselburger`

**Namens-Konvention (wichtig):**

| Asset-Typ | Stem-Pattern | Beispiel |
|---|---|---|
| Static Mesh | `SM_<Prefix>_<Suffix>` | `SM_QmWieselburger_01` |
| Icon-Textur | `T_<Prefix>_Icon` (oder beliebig mit Prefix) | `T_QmWieselburger_Icon` |
| Diffuse-Textur | `T_<Prefix>_Diffuse` | `T_QmWieselburger_Diffuse` |
| Normal/Roughness | `T_<Prefix>_<Was>` | `T_QmWieselburger_Normal` |
| Material Instance | `MI_<Prefix>_<Slot>` (siehe Schritt 6.2) | `MI_QmWieselburger_Body` |
| Material (Master) | `M_<Prefix>_*` - **wird beim Build geskipped, crasht shipping** | - |

Wichtig: **Master-Materials (`M_*`) werden vom Build absichtlich rausgefiltert** - die crashen das Game. Wenn du eigene Materials brauchst, geht das ueber **Vanilla-MI-Parents** (Schritt 6.2) - die Pipeline klont eine Vanilla-MI und uebernimmt deine Parameter-Werte.

## Schritt 4: Texturen importieren

1. Content Browser -> `/Game/Quartermaster/Items/` -> Drag&Drop deiner PNGs
2. Pro Textur im Import-Dialog:
   - **Texture Group**: `World` (Mesh-Texturen), `UI` (Icon)
   - **sRGB**: an fuer Diffuse + Icon, aus fuer Normal/Roughness/Metallic
   - **Compression Settings**:
     - Diffuse / Icon: `Default (DXT1/5)`
     - Normal: `Normalmap (DXT5, BC5 on DX11+)`
     - Roughness / Metallic: `Masks (no sRGB)`
3. Naming: alle Stems mit deinem Prefix (`T_QmWieselburger_*`)

## Schritt 5: Mesh importieren

1. Content Browser -> `/Game/Quartermaster/Items/` -> FBX reinziehen
2. Import-Dialog:
   - **Static Mesh** an, **Skeletal Mesh** aus
   - **Generate Lightmap UVs** an
   - **Auto Generate Collision** an
   - **Import Materials** aus (Materials kommen in Schritt 6 dazu)
   - **Import Textures** aus (haben wir schon)
3. Stem **muss** mit `SM_<Prefix>` anfangen (`SM_QmWieselburger_01`)
4. (Empfehlung) Mesh-Editor: **LOD Settings -> Number of LODs = 4**,
   **Auto Compute LOD Distances = true**
5. **Bei Nanite**: nur wenn der Vanilla-Donor Nanite nutzt; sonst weglassen

### Socket (optional, fuer Flame-Preset)

Wenn du das Flame-FX-Preset nutzen willst, im Mesh-Editor einen Socket
anlegen. **Der Name ist beliebig** - die Pipeline nimmt den ersten
Socket, den sie im Mesh findet. Konvention: `flame` nennen, damit es
selbst-dokumentierend bleibt.

1. Mesh oeffnen -> **Window** -> **Socket Manager**
2. **Create Socket** -> Name z.B. `flame`
3. Position/Rotation/Scale wo die Flamme sitzen soll (UE-cm, Z = oben)
4. Speichern

Beim Build liest die Pipeline den ersten Socket und schreibt die
Niagara-Component + Point-Light-Positionen in den geklonten Flame-BP
um. Ohne Socket bleibt die Flamme auf der Vanilla-Torch-Z-Hoehe (~150 cm).

Mehrere Sockets im Mesh sind erlaubt, aber **nur der erste wird
konsumiert** - mehrere Flames pro Building gehen aktuell nicht
(SCS-Cloning gecrashed beim ersten Anlauf, Rollback hat es bei
"erster Socket, name-agnostic" hinterlassen).

Wenn dein Mesh **keinen** Socket hat aber ein Flame-Preset aktiv ist,
zeigt der Build-Log einen Warn-Hinweis + faellt auf Vanilla-Position
zurueck (kein Fail).

## Schritt 6: Material-Setup

Hier gibt es zwei Wege - meistens reicht **Variante A**.

### Variante A: Slots leer lassen (Vanilla-MI uebernimmt komplett)

1. Mesh-Slots im Mesh-Editor mit **`WorldGridMaterial`** belegen (Default
   wenn nichts gesetzt ist - macht UE selbst)
2. Cooken
3. In der GUI dann pro Slot eine Vanilla-MI als Parent picken - die
   Pipeline klont die MI komplett, ohne Param-Overrides

### Variante B: Eigene MI mit Param-Overrides

1. Content Browser -> Rechtsklick auf Vanilla-MI Referenz **oder**
   "Create Material Instance" auf einem Master-Material das die
   gewuenschten Params hat
2. Name: `MI_<Prefix>_<SlotName>` (`MI_QmWieselburger_Body`)
3. Im MI-Editor Params setzen (Texturen reinziehen aus
   `/Game/Quartermaster/Items/`, Skalare/Vektoren editieren)
4. Mesh-Slot auf diese MI setzen
5. Cooken

**Was die GUI dann macht:** sie liest die MI, vergleicht den Parent
mit der Vanilla-MI die du gleich pickst, und uebernimmt deine
Param-Werte als Default in den GUI-Slots. Du kannst sie dort
weiter editieren.

## Schritt 7: Cooken

- **File** -> **Cook Content for Windows**
- Wartezeit: 1-5 Min beim ersten Mal

Output landet unter:

```
<UEProj>/Saved/Cooked/Windows/<ProjectName>/Content/Quartermaster/Items/
  SM_QmWieselburger_01.uasset / .uexp / .ubulk
  T_QmWieselburger_Diffuse.uasset / .uexp / .ubulk
  T_QmWieselburger_Icon.uasset / .uexp / .ubulk
  MI_QmWieselburger_Body.uasset / .uexp        (nur wenn Variante B)
  ...
```

**Diesen Pfad merken** - den brauchst du im naechsten Schritt.

## Schritt 8: Building in der GUI anlegen

1. Quartermaster starten (Desktop-App oder `dotnet run --project GUI\Web`)
2. Profil oeffnen (oder neu erstellen)
3. **Buildings**-Tab oeffnen -> **New Building**
4. Pro Card:

   | Feld | Was rein |
   |---|---|
   | **Name** | Wie das Building ingame heisst (Build-Menu + Tooltip) |
   | **Description** | Tooltip-Text |
   | **Template** | Picker -> Vanilla-DA von dem geclont wird. Bestimmt Snap-Verhalten, Hit-Box, Build-Tab. Beispiele: `DA_BI_DishesCup_01` (free-standing), `DA_BI_Paintings_HighLands_02` (wall-mount). 849 Vanilla-Templates verfuegbar. |
   | **Cooked Folder** | Absoluter Pfad zum Cooked-Items-Ordner aus Schritt 7 (`<UEProj>/Saved/Cooked/Windows/.../Quartermaster/Items/`). |
   | **Mesh stem** | Stem deines Meshes ohne `.uasset` (`SM_QmWieselburger_01`). |
   | **Icon stem** | Stem deines Icons (`T_QmWieselburger_Icon`). |
   | **Flame Preset** (optional) | Dropdown: None / Torch. Siehe Schritt 8.2. |

5. **Slots-Sektion** erscheint sobald Cooked-Folder + Mesh-Stem gesetzt
   sind. Die GUI ruft `/api/buildings/inspect-cooked` auf, liest die
   Material-Slots des Meshes + die per-Slot user-cooked MIs falls da:

   | Feld pro Slot | Was rein |
   |---|---|
   | **Vanilla MI Parent** | Picker -> Vanilla-MI deren Material-Struktur du nutzen willst (z.B. eine Holz-MI, eine Stoff-MI). Bestimmt welche Params nachher editierbar sind. |
   | **Param-Overrides** | Erscheinen automatisch sobald Parent gesetzt - Scalars/Vectors/Textures mit Slidern + Color-Picker + Textur-Dropdown |

6. **Recipe** (optional): per-Building Bau-Kosten - leer = Default aus
   dem Template uebernehmen. Liste mit Vanilla-Resource-DAs + Counts.

### 8.1 Was bei "Flame Preset = Torch" passiert

Wenn du das Torch-Preset anwaehlst:
- Das Template wird intern durch **`DA_BI_FloorTorch`** ueberschrieben,
  d.h. das Building erbt die FloorTorch-Properties (Snap-Rules,
  Lighting-Gameplay-Tag, Wood-Light-Torch-Recipe)
- Der Vanilla-Torch-BP wird per Building geklont (siehe
  `FlamePresetCatalog.cs` fuer Details)
- Dein Mesh ersetzt im geklonten BP das Torch-Mesh (NameMap-Rewrite)
- Wenn dein Mesh einen `flame`-Socket hat, werden Niagara + Point-Light
  dorthin verschoben

Effektiv: "Flame Preset Torch" = "Torch-Building mit deinem Mesh".

## Schritt 9: Build

1. Header -> **Build**-Button
2. Build-Log live in der Status-Sektion. Wichtige Phasen:
   ```
   [OK] === [QmBldg_<8hex>] Step 1: scan cooked folder ===
   [OK] === [QmBldg_<8hex>] Step 2: rewrite mesh material slots ===
   [OK] === [QmBldg_<8hex>] Step 3: clone MIs per slot ===
   [OK] === [QmBldg_<8hex>] Step 4: patch DA (NameMap + FText keys) ===
   [OK] === [QmBldg_<8hex>] Step 5: append CSV row ===
   ```
3. Bei Flame-Preset zusaetzlich:
   ```
   === [Flame:torch:QmBldg_<8hex>] Step 1: extract vanilla BP 'BP_BuildingBlock_FloorTorch' ===
   === [Flame:torch:QmBldg_<8hex>] Step 2: rewrite NameMap and FolderName ===
   [Flame] socket 'flame' (X=0 Y=0 Z=80 | ...) applied to 2 component(s)
   ```
4. Am Ende: Output landet **direkt** unter
   `<Windrose>\R5\Content\Paks\~mods\Quartermaster_<Profile>_P.{pak,ucas,utoc}`

Kein manueller Copy-Step, kein retoc-Aufruf, kein DLL-Rebuild.

## Schritt 10: Test ingame

1. Windrose starten + in eine Welt laden
2. Build-Mode oeffnen
3. Tab **"Vorgefertigte Strukturen"** oeffnen
4. Dein Building taucht als Slot mit deinem Icon auf
5. Klicken + bauen -> Preview zeigt dein Mesh
6. Bauen -> Mesh + (falls Flame-Preset) Niagara-Flamme + Point Light
   + Loop-SFX

### Logs wenn was schiefgeht

| Log | Pfad |
|---|---|
| Quartermaster Build-Log | UI-Status-Sektion (live) oder im Profil-Ordner |
| DLL-Inject-Log | `%LOCALAPPDATA%\R5\Saved\Logs\Quartermaster_Inject.log` |
| Game-Log | `%LOCALAPPDATA%\R5\Saved\Logs\R5.log` |

## Wenn was nicht klappt

| Symptom | Ursache | Fix |
|---|---|---|
| **Build-Step "rewrite mesh material slots" warnt mit "mesh has no user-cooked MI slots"** | Du nutzt Variante A (alle Slots auf `WorldGridMaterial`) | OK so - der Step wird harmlos skipped, Vanilla-MI uebernimmt komplett. |
| **Building taucht nicht im Build-Menu auf** | Falsche Template-Wahl oder DLL-Inject nicht aktiv | DLL-Inject-Log pruefen, Quartermaster_P.pak in `~mods/` pruefen, Template muss ein DA_BI_* sein |
| **Mesh nicht sichtbar, nur Schatten** | Mesh-Cook fehlgeschlagen oder Mesh-Stem falsch im Card-Feld | Pruefen ob `<CookedFolder>/<MeshStem>.uasset` existiert |
| **Flame-Preset aktiv, aber Flamme an falscher Position** | Kein `flame`-Socket im Mesh | Socket in UE-Editor anlegen, neu cooken, rebuilden |
| **Build-Log "FText key not found in DA body"** | Vanilla-Template hat den deklarierten Description-Key nicht | An mich melden - heisst die `BuildingTemplate`-Definition referenziert den falschen Vanilla-Key |
| **Game crasht beim Bauen** | Wahrscheinlich Master-Material (`M_*`) wurde mitgestaged | Im UE-Editor pruefen ob alle Material-Slots auf MIs oder `WorldGridMaterial` zeigen, keine M_-Files |

## Referenz: Was die Pipeline unter der Haube macht

(Falls du verstehen willst was passiert, oder Bugs reportest)

1. **CookedFolderInspector** scannt den Cooked-Folder, listet
   Asset-Stems die mit deinem `AssetPrefix` anfangen
2. **DataAssetPatcher** klont die Vanilla-Building-DA, schreibt deren
   NameMap um: Vanilla-Mesh -> dein Mesh, Vanilla-Icon -> dein Icon,
   Vanilla-Recipe-Ref -> deine geclonte Recipe
3. **BuildingPatcher Step 7** macht einen Binary-Inline-Rewrite der
   FText-Keys im DA-Body (Vanilla-Loca-Key -> per-Building-Key, gleiche
   Byte-Laenge, mit Underscores gepadded)
4. **BuildingItemsCsvPatcher** appendet eine CSV-Row mit deinem
   Name + Description unter dem per-Building-Key
5. **MaterialInstancePatcher** klont pro Slot die Vanilla-MI, wendet
   deine Param-Overrides an, emittiert sie als
   `MI_<Prefix>_<SlotKey>.uasset`
6. **RecipePatcher** klont (falls Recipe-Override gesetzt) die
   Vanilla-Recipe-JSON, ersetzt RecipeCost, schreibt sie als
   `DA_RD_Qm<BuildingId>.uasset` raus
7. **BlueprintPatcher** (nur bei Flame-Preset) klont den Vanilla-FlameBP,
   schreibt NameMap-Refs auf die per-Building-Stems um
   (Mesh + BP-Self-Ref + Material-Refs), liest den `flame`-Socket aus
   dem User-Mesh und patcht Niagara/Light/Audio Transform-Properties
   im BP
8. **GameDeployer** packt alle gestageten Files in
   `Quartermaster_<Profile>_P.{pak,ucas,utoc}`, schreibt
   `qm_items.json` neben der DLL, kopiert alles nach `~mods/`
9. **DLL** (laeuft beim Spielstart): liest `qm_items.json`, injected
   die Items in den Build-Menu-Tab `BuildingDecoration`

## Verwandte Docs

- `Docs/PLAN-AddNewBuildModeSlot-DONE.md` - wie ein neuer **Tab** in der
  Build-UI angelegt wird (anderes Thema - hier addieren wir nur Slots
  in bestehenden Tabs)
- `Docs/PLAN-ShipMusicAddTracks-NEW.md` - Praezedenz: Custom-Audio-Pipeline
  mit aehnlicher Cook-then-NameMap-Rewrite-Strategie
- `Docs/PLAN-CsvLocalizationPatcher-WIP.md` - wie die per-Building-CSV-Rows
  in die BuildingItems.csv kommen
