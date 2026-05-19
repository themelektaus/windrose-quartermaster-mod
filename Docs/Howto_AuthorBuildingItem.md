# Howto: Eigenes Building-Item im Unreal Editor aufbereiten

Stand: 2026-05-18

Diese Anleitung beschreibt, wie ein eigenes Mod-Building-Item mit eigenem Mesh, Material, Icon und Anzeigename erstellt wird. Collision und Spielverhalten werden vom Vanilla-Item geerbt - wir ersetzen nur die visuellen Komponenten.

> **Abkuerzung statt Schritt 4 + Teile von Schritt 8**: Es gibt einen
> einmal-auszufuehrenden Python-Helper, der dir ein wiederverwendbares
> Template-Set im UE-Projekt anlegt (Blueprint-Klasse + DataAsset +
> Placeholder-Material). Details und Setup-Schritte siehe
> [`Tools/UeBuildingItem/README.md`](../Tools/UeBuildingItem/README.md).
> Mit dem Template entfaellt das Vanilla-Asset-Extrahieren als
> Inspirations-Quelle und das manuelle Class-Picking im DataAsset-Dialog.

## Was am Ende rauskommt

- Eigener Pak (`.pak`/`.ucas`/`.utoc`) mit:
  - `DA_BI_MyItem_01.uasset/.uexp` - das DataAsset (Building-Item-Definition)
  - `SM_MyItem.uasset/.uexp/.ubulk` - eigenes Static Mesh
  - `M_MyItem.uasset/.uexp` - eigenes Material
  - `T_MyItem_Diffuse.uasset/.uexp/.ubulk` - eigene Texturen
  - `T_MyItem_Icon.uasset/.uexp/.ubulk` - eigenes UI-Icon
- Ein Eintrag in `qm_config.cpp` der das DataAsset in der gewuenschten Sparte injiziert
- Im Spiel: ein neuer Slot in der Decoration-Sparte mit dem eigenen Icon, beim Bauen das eigene Mesh

## Voraussetzungen

| Was | Detail |
|---|---|
| **Unreal Engine 5.6.x Editor** | Von Epic Games Launcher. Windrose laeuft auf 5.6.1, also gleiche Major-Version verwenden. Source-Build nicht noetig - die Binary-Version reicht. |
| **Eigenes Mesh** | `.fbx` oder `.obj`. FBX bevorzugt (UV-Map + Material-Slots schon drin). Polygon-Count bewusst unter 10k halten fuer den ersten Test. |
| **Eigene Texturen** | `.png` (RGBA) oder `.tga`. Mindestens Diffuse. Optional Normal, Roughness, Metallic. Power-of-2 Aufloesungen (512x512, 1024x1024, 2048x2048). |
| **Eigenes Icon** | `.png`, quadratisch, 256x256 oder 512x512. Transparenter Hintergrund. |
| **`retoc.exe`** | Liegt im Repo-Root. Wird fuers Cooked-Output -> Zen-Pak Konvertieren gebraucht. |
| **Game muss laufen** | Damit unsere DLL den Inject macht und die DataAsset-Klasse zur Laufzeit per Donor-Clone setzt. |

## Pipeline-Uebersicht

```
                                                              .pak/.ucas/.utoc
   Vanilla-Pak             UE 5.6 Editor                          |
   ============            ==============                          |
                                                                  V
+-------------+   retoc    +------------+    Editor     +----------------+
| pakchunk*   |  unpack    | Vanilla    |   Refs        | Cooked Output  |
| .utoc/.pak  +-----------> DataAsset   +---------------> .uasset/.uexp  |
+-------------+            +------------+   anpassen    +-------+--------+
                                |                               |
                                |  duplicate                    |  retoc to-zen
                                |  + Mesh/Mat/Icon              |
                                V                               V
                         +------------+                  +--------------+
                         | MyItem     |                  | MyItem_P.pak |
                         | DataAsset  |                  +-------+------+
                         +------------+                          |
                                                                 |  deploy
                                                                 V
                                                       R5/Content/Paks/~mods/
                                                                 |
                                                                 |  Quartermaster DLL
                                                                 V
                                                       Slot in Decoration-Sparte
```

## Schritt 1: UE 5.6 Editor installieren

1. Epic Games Launcher oeffnen -> **Unreal Engine** Tab
2. **Library** -> **Engine Versions** -> Plus-Symbol -> **5.6.x** waehlen
3. Installations-Optionen: nur "Engine" reicht. Starter-Content, Templates, Platforms koennen weg.
4. Wartezeit: 20-60 Min, ~25 GB Plattenplatz
5. Verify: nach Install ist `UnrealEditor.exe` unter `C:\Program Files\Epic Games\UE_5.6\Engine\Binaries\Win64\` (Pfad kann je nach Custom-Install variieren)

## Schritt 2: Vanilla-Asset als Basis extrahieren

Wir nehmen `DA_BI_Bedroll_01` als Basis (kennen wir schon, einfache Geometrie, ein Mesh, ein Material). Spaeter kann jedes andere Decoration-Item als Basis dienen.

### 2.1 Game-Pak finden

```
E:\Games\steamapps\common\Windrose\R5\Content\Paks\pakchunk0-Windows.utoc
```

### 2.2 Vanilla-Asset extrahieren

Mit `retoc.exe` (liegt im Repo-Root) das DA + den referenzierten Mesh + Material + Texturen unpacken. AES-Key wird gebraucht (siehe CLAUDE.md).

```
E:\Windrose\Mods\Quartermaster\retoc.exe unpack ^
    --aes-key 0x5F430BF9FEF2B0B91B7C79C313BDAF291BA076A1DAB5045974186333AA16CFAE ^
    --filter "/Game/Gameplay/Building/BuildingDecoration/DA_BI_Bedroll_01" ^
    E:\Games\steamapps\common\Windrose\R5\Content\Paks\pakchunk0-Windows.utoc ^
    E:\Windrose\Mods\Quartermaster\.build-tmp\vanilla-bedroll
```

> **TODO**: exakte retoc-Subcommands fuer Filter-Pfad sind je nach Version unterschiedlich - vor dem ersten Lauf `retoc.exe unpack --help` checken und ggf. anpassen.

### 2.3 Was wir kriegen sollten

```
.build-tmp/vanilla-bedroll/
  R5/Content/Gameplay/Building/BuildingDecoration/
    DA_BI_Bedroll_01.uasset
    DA_BI_Bedroll_01.uexp
  R5/Content/Gameplay/Building/BuildingDecoration/Bedroll/Mesh/
    SM_Bedroll_01.uasset
    SM_Bedroll_01.uexp
    SM_Bedroll_01.ubulk
  R5/Content/Gameplay/Building/BuildingDecoration/Bedroll/Materials/
    M_Bedroll_01.uasset
    M_Bedroll_01.uexp
  R5/Content/Gameplay/Building/BuildingDecoration/Bedroll/Textures/
    T_Bedroll_Diffuse.uasset/.uexp/.ubulk
    T_Bedroll_Normal.uasset/.uexp/.ubulk
    ...
  R5/Content/UI/Icons/Building/
    T_Icon_Bedroll.uasset/.uexp/.ubulk
```

Die exakte Pfad-Struktur sehen wir erst nach dem ersten Extract. Wenn der Pfad anders ist: einfach im DataAsset im Editor nachsehen welche Refs es hat.

## Schritt 3: UE-Editor-Projekt anlegen

1. UE5.6 starten
2. **New Project** -> **Games** -> **Blank**
3. **Blueprint**, **No Starter Content**, **Raytracing** aus
4. Project Name: `WindroseMod`
5. Location: `E:\UnrealProjects\` (oder wo Platz ist)
6. **Create**

Wartezeit: 1-2 Min beim ersten Mal (Shader-Compile).

### 3.1 Cook-Settings einstellen

- **Edit** -> **Project Settings** -> **Project** -> **Packaging**
- **"Cook everything in the project content directory"** an
- **"Use Pak File"** aus (wir nehmen retoc, nicht den eingebauten Pak-Builder)

### 3.2 Template-Set erzeugen (empfohlen - 5 Min)

Statt jedes neue Mod-Item von Hand anzulegen, einmal das Template-Script
ausfuehren - danach ist `DA_Template_QmItem` der Start-Punkt fuer jedes
weitere Item (duplicate-rename-fill).

- Plugin **"Python Editor Script Plugin"** in **Edit -> Plugins** aktivieren, Editor neu starten
- **Window -> Output Log**, Eingabe-Modus von "Cmd" auf **"Python"** stellen
- Folgende Zeile einfuegen (Pfad anpassen):

  ```python
  exec(open(r"E:\Windrose\Mods\Quartermaster\Tools\UeBuildingItem\create_template.py").read())
  ```

Output-Log zeigt `=== Quartermaster template generator ===` und legt
folgendes unter `/Game/Quartermaster/Template/` an:

| Asset | Inhalt |
|---|---|
| `BP_QmBuildingItem` | Blueprint-Klasse (Parent: `PrimaryDataAsset`) - Container fuer Properties |
| `DA_Template_QmItem` | DataAsset-Instanz von BP - dein leeres Start-Asset zum Duplizieren |
| `M_Template_QmItem` | Placeholder-Material - editieren oder durch eigenes ersetzen |

Danach **einmalig** 5 Variablen im BP anlegen
(`Mesh`/`Material`/`Icon`/`DisplayName`/`Description`) - genaue
Schritt-fuer-Schritt in
[`Tools/UeBuildingItem/README.md`](../Tools/UeBuildingItem/README.md)
Abschnitt "Properties einrichten".

Ab jetzt: pro neues Item nur noch **Duplicate -> Rename -> Refs setzen**
(siehe Schritt 8).

### 3.3 (Optional) Plugin-Mount fuer Pfad-Inheritance

Wenn wir wollen dass der Cooked-Output unter `/Game/Gameplay/Building/BuildingDecoration/...` landet (wie Vanilla), muss das Projekt diesen virtuellen Pfad mounten. Zwei Optionen:

| Variante | Pro | Contra |
|---|---|---|
| **Vanilla-Pfade behalten** (`/Game/Gameplay/Building/BuildingDecoration/DA_BI_MyItem_01`) | DataAsset matched 1:1 das was unsere DLL erwartet. Kein Pfad-Patch noetig. | Editor Content-Browser braucht den Vanilla-Ordner-Stem. Conflict-Risiko falls Vanilla-Asset gleichen Stem hat. |
| **Eigener Namespace** (`/Game/Quartermaster/Mods/MyItem/DA_BI_MyItem_01`) | Sauber getrennt, keine Conflicts. | DLL-Config muss den Namespace-Pfad kennen (geht via `qm_config.cpp`). |

> **Empfehlung**: Vanilla-Pfade fuer Asset-Refs (Mesh, Material, Texture), eigener Namespace fuer das DataAsset selbst. Das DataAsset-DLL-Routing geht ueber den Mod-Pak hoch, alles andere unter `/Game/Gameplay/Building/BuildingDecoration/MyItem/` etc.

> **Offene Frage**: Editor laesst Vanilla-Pfade als Asset-Namen erstmal nicht zu, weil der Content-Browser nur `/Game/` als Root kennt. Loesung: Asset im Editor mit eigenem Namen speichern, **post-cook FName-Patch** auf den Vanilla-Pfad (analog Ship-Music-Patcher). Das ist der etablierte Quartermaster-Weg - siehe `Docs/WIP_StaticMeshReplacement.md` fuer Praezedenz.

## Schritt 4: Vanilla-Asset im Editor importieren

### 4.1 Vanilla-DA als Inspirations-Quelle

Die unpacked `.uasset`-Dateien koennen wir nicht direkt in einen anderen UE-Editor-Projekt laden (Cooked-Assets, kein Source-Format). **Aber wir muessen das auch nicht** - wir brauchen nur:

1. Die **Asset-Refs** die das Vanilla-DA hat (Mesh-Pfad, Material-Pfad, Icon-Pfad, DisplayName-LocalizationKey)
2. Die **Properties** die das DA setzt (BuildingItemTag, MaxStack, CraftingMaterials, etc.)

Diese Infos kriegen wir per Asset-Inspection:

```
E:\Windrose\Mods\Quartermaster\retoc.exe inspect ^
    --aes-key 0x5F430BF9FEF2B0B91B7C79C313BDAF291BA076A1DAB5045974186333AA16CFAE ^
    --asset "DA_BI_Bedroll_01" ^
    E:\Games\steamapps\common\Windrose\R5\Content\Paks\pakchunk0-Windows.utoc
```

> **TODO**: exakte Inspection-Methode klaeren - retoc-Subcommand oder CUE4Parse-Dump.

Alternativ: das gedumpte JSON aus der Setup-Pipeline benutzen (vom Setup gibts schon Vanilla-JSON-Dumps fuer alle DataAssets, siehe `--setup` in der GUI).

### 4.2 Was wir aus dem Dump rausziehen

```json
{
    "ClassName": "R5BuildingItem",
    "BuildingMesh": "/Game/Gameplay/Building/BuildingDecoration/Bedroll/Mesh/SM_Bedroll_01",
    "BuildingItemTag": "Building.Items.Decoration.Bedroll",
    "Icon": "/Game/UI/Icons/Building/T_Icon_Bedroll",
    "DisplayName_LocalizationKey": "Building_Bedroll_Name",
    "Description_LocalizationKey": "Building_Bedroll_Description",
    ...
}
```

Diese Werte ueberschreiben wir in unserem eigenen DataAsset.

## Schritt 5: Eigene Texturen importieren

1. UE-Editor -> Content Browser
2. Im Browser `/Game/Quartermaster/Mods/MyItem/Textures/` anlegen (Rechtsklick -> New Folder)
3. Diffuse-Textur reinziehen (`Drag & Drop` aus Explorer)
4. Import-Dialog:
   - **Texture Group**: `World` (oder `UI` fuer Icon)
   - **sRGB**: an fuer Diffuse, aus fuer Normal/Roughness
   - **Compression Settings**: `Default (DXT1/5)` fuer Diffuse, `Normalmap (DXT5, BC5 on DX11+)` fuer Normal
   - **Import**
5. Wiederholen fuer Normal, Roughness etc.
6. Icon-Textur unter `/Game/Quartermaster/Mods/MyItem/UI/T_MyItem_Icon` ablegen
   - **Texture Group**: `UI`
   - **sRGB**: an
   - **Compression Settings**: `UserInterface2D (RGBA)` falls Alpha-Channel noetig

## Schritt 6: Eigenes Material anlegen

1. Content Browser -> `/Game/Quartermaster/Mods/MyItem/Materials/` (anlegen falls noch nicht da)
2. Rechtsklick -> **Material** -> Name: `M_MyItem`
3. Doppelklick oeffnet den Material-Editor
4. Texturen reinziehen + Pins an `Result`-Node verbinden:
   - Diffuse -> `Base Color`
   - Normal -> `Normal` (Texture-Sampler im Details-Panel auf `Normal` setzen)
   - Roughness -> `Roughness`
   - (optional) Metallic -> `Metallic`
5. **Apply** (oben links) -> **Save** (Ctrl+S)

> **Tipp**: fuer den allerersten Test reicht **nur Diffuse**. Spaeter beim Polish kann mehr rein.

## Schritt 7: Eigenes Static Mesh importieren

1. Content Browser -> `/Game/Quartermaster/Mods/MyItem/Mesh/`
2. FBX reinziehen
3. Import-Dialog:
   - **Static Mesh** an, **Skeletal Mesh** aus
   - **Generate Lightmap UVs** an (wichtig fuer indirekte Beleuchtung)
   - **Auto Generate Collision** an
   - **Import Materials** aus (wir nutzen `M_MyItem`)
   - **Import Textures** aus
   - **Import All**
4. Resultat: `SM_MyItem.uasset`
5. Doppelklick aufs Mesh -> Details -> **Material Slots** -> Slot 0 auf `M_MyItem` setzen
6. (Empfehlung) **LOD Settings -> Number of LODs = 4** und **Auto Compute LOD Distances = true** - sonst sieht das Mesh in der Distanz haesslich aus
7. Ctrl+S

> **Bei Nanite**: falls Vanilla-Items Nanite nutzen, im Mesh-Editor **Nanite Settings -> Enable Nanite = true**. Vorab pruefen: `retoc inspect` auf ein Vanilla-Mesh ausfuehren und auf `bNaniteEnabled` schauen.

## Schritt 8: Eigenes DataAsset anlegen

Hier kommt die Abkuerzung ins Spiel: statt manuell ein DataAsset mit
Class-Picking anzulegen, **duplizierst du das Template-Asset** aus
Schritt 3.2 und fuellst nur die Refs.

### 8.1 Anlegen via Template-Duplicate (empfohlen)

1. Content Browser -> `/Game/Quartermaster/Template/`
2. Rechtsklick auf `DA_Template_QmItem` -> **Duplicate** (oder Strg+D)
3. Verschiebe das Duplikat per Drag&Drop nach
   `/Game/Quartermaster/Mods/MyItem/`
4. Umbenennen auf `DA_BI_MyItem_01`
5. Doppelklick zum Bearbeiten - die 5 Slots
   (Mesh/Material/Icon/DisplayName/Description) sind leer und bereit zum
   Befuellen.

> **Class-Ref-Hinweis**: Das Template-DataAsset hat Class `BP_QmBuildingItem_C`,
> nicht `R5BuildingItem`. Beim ersten echten Cook + In-Game-Test zeigt
> sich, ob das Game den Class-Ref braucht. Falls ja: Post-Cook-Patcher
> wird ergaenzt der die NameMap im cooked `.uasset` umschreibt. Siehe
> `Tools/UeBuildingItem/README.md` Abschnitt "Bekannte Einschraenkungen".

### 8.1-alt Manueller Weg (falls Template nicht verfuegbar)

Falls du das Template-Script nicht laufen lassen willst:

| Option | Wie | Anmerkung |
|---|---|---|
| **A: Generic DataAsset** | UE-Standard `DataAsset` als Base waehlen, Properties als untyped JSON setzen | DLL-Runtime macht `donor->Class` Override -> funktioniert mit dem aktuellen Inject-Pfad. Editor-Properties bleiben leer. |
| **B: Custom DataAsset C++-Klasse** mit gleichen Properties | C++-Klasse `MyR5BuildingItem` mit den Properties aus dem Dump, ueberschreibt die Vanilla-Klasse zur Cook-Zeit | Mehr Aufwand, aber Editor-Polish moeglich. |
| **C: Post-Cook FName-Patch** | DA als `DataAsset` speichern, **NameMap im uasset post-cook** auf `R5BuildingItem` umpatchen | Etablierte Quartermaster-Methode (siehe Ship-Music-Patcher). Einmaliger Tool-Aufwand. |

Anlegen:

1. Content Browser -> `/Game/Quartermaster/Mods/MyItem/`
2. Rechtsklick -> **Miscellaneous** -> **Data Asset**
3. Class-Picker: `R5BuildingItem` suchen
   - Wenn nicht da -> `PrimaryDataAsset` waehlen (Option A)
   - Falls da -> Option B-ready
4. Name: `DA_BI_MyItem_01`
5. Doppelklick zum Bearbeiten

### 8.2 Properties setzen

Im DataAsset-Editor (so wie es geht - je nachdem ob die Class bekannt ist oder nicht):

| Property | Wert |
|---|---|
| `BuildingMesh` | `SM_MyItem` (Picker aus Content Browser) |
| `BuildingItemTag` | `Building.Items.Decoration.MyItem` (FGameplayTag - falls TagManager das kennt) |
| `Icon` | `T_MyItem_Icon` |
| `DisplayName_LocalizationKey` | `Building_MyItem_Name` |
| `Description_LocalizationKey` | `Building_MyItem_Description` |
| `CraftingMaterials` | leer fuer ersten Test (Donor liefert default) |
| `MaxStack` | 1 |
| `Category` | `Decoration` |

> **Wenn Property nicht editierbar im Editor**: das ist OK fuer Option A. Unsere DLL ueberschreibt zur Runtime die Klasse + Properties vom Donor (Vanilla-Bedroll). Wir brauchen im DataAsset nur die **Refs** auf Mesh/Material/Icon - alles andere kommt vom Donor.

### 8.3 DisplayName lokalisieren

Im Editor:
- **Window** -> **Localization Dashboard**
- **Add Target** -> Name: `Game`
- Source-Path: `/Game/Quartermaster/Mods/MyItem/`
- **Gather Text** -> findet `Building_MyItem_Name`
- **Export** als `.po`, **Edit** den Wert (z.B. "Mein Bedroll"), **Import**
- **Compile** zum `.locres`

Das ist der vollstaendige Weg. Fuer den allerersten Test kann der DisplayName als **Literal** im DataAsset stehen (`FText::FromString("Mein Bedroll")`) - dann ist das Lokalisierungs-System nicht noetig.

## Schritt 9: Cooken

- **File** -> **Cook Content for Windows**
- Wartezeit: 1-5 Min beim ersten Mal

Output landet unter:
```
E:\UnrealProjects\WindroseMod\Saved\Cooked\Windows\WindroseMod\Content\
  Quartermaster\Mods\MyItem\
    DA_BI_MyItem_01.uasset
    DA_BI_MyItem_01.uexp
    Mesh\SM_MyItem.uasset/.uexp/.ubulk
    Materials\M_MyItem.uasset/.uexp
    Textures\T_MyItem_Diffuse.uasset/.uexp/.ubulk
    UI\T_MyItem_Icon.uasset/.uexp/.ubulk
```

## Schritt 10: Pak bauen mit retoc

### 10.1 Stage-Verzeichnis anlegen

```
E:\Windrose\Mods\Quartermaster\.build-tmp\myitem-stage\
  R5\Content\Quartermaster\Mods\MyItem\
    DA_BI_MyItem_01.uasset/.uexp
    Mesh\...
    Materials\...
    Textures\...
    UI\...
```

Die Cooked-Output-Files aus Schritt 9 dahin kopieren. **Wichtig**: das `R5\Content\`-Praefix muss stimmen, sonst findet das Game den Pfad nicht.

### 10.2 retoc to-zen

```
E:\Windrose\Mods\Quartermaster\retoc.exe to-zen ^
    --aes-key 0x5F430BF9FEF2B0B91B7C79C313BDAF291BA076A1DAB5045974186333AA16CFAE ^
    E:\Windrose\Mods\Quartermaster\.build-tmp\myitem-stage ^
    E:\Windrose\Mods\Quartermaster\.build-tmp\myitem-out
```

Output:
```
.build-tmp/myitem-out/
  MyItem_P.pak
  MyItem_P.ucas
  MyItem_P.utoc
```

> **TODO**: exakte retoc-Args klaeren. Es koennte sein dass `scriptobjects.bin` mit ins Stage muss (siehe `qmbedrl-fresh/zen-in/scriptobjects.bin` als Praezedenz).

## Schritt 11: Deploy

Drei Files nach `~mods/` kopieren:

```
copy .build-tmp\myitem-out\MyItem_P.pak  E:\Games\steamapps\common\Windrose\R5\Content\Paks\~mods\
copy .build-tmp\myitem-out\MyItem_P.ucas E:\Games\steamapps\common\Windrose\R5\Content\Paks\~mods\
copy .build-tmp\myitem-out\MyItem_P.utoc E:\Games\steamapps\common\Windrose\R5\Content\Paks\~mods\
```

## Schritt 12: Config-Eintrag in der DLL

`Tools/DllProxy/dxgi/qm_config.cpp` editieren und ein Item ergaenzen:

```cpp
static const InjectableItem g_injectableItems[] = {
    {
        L"QmBedrl_01",
        L"BuildingDecoration",
        L"/Game/Gameplay/Building/BuildingDecoration/DA_BI_QmBedrl_01",
        L"DA_BI_QmBedrl_01",
        nullptr,
    },
    {
        L"MyItem",
        L"BuildingDecoration",
        L"/Game/Quartermaster/Mods/MyItem/DA_BI_MyItem_01",
        L"DA_BI_MyItem_01",
        nullptr,
    },
};
```

DLL bauen + deployen:

```
"E:/Windrose/Mods/Quartermaster/Tools/DllProxy/dxgi/build.bat" && "E:/Windrose/Mods/Quartermaster/Tools/DllProxy/dxgi/deploy.bat"
```

## Schritt 13: Test

1. `%LOCALAPPDATA%\R5\Saved\Logs\Quartermaster_Inject.log` loeschen
2. Windrose normal starten + in eine Welt laden
3. Build-Mode oeffnen
4. Decoration-Tab oeffnen (das mit 8 Subgroups)
5. Visuell pruefen:
   - In der ersten Decoration-Subgroup zwei zusaetzliche Slots
   - QmBedrl-Slot wie bisher (Bedroll-Icon)
   - **MyItem-Slot mit eigenem Icon** (das aus Schritt 5)
6. Auf MyItem klicken + bauen
7. **Eigener Mesh** sollte im Build-Preview zu sehen sein
8. Tab in andere Sparte wechseln, zurueck zu Decoration -> beide Slots wieder da

### Erwartetes Log

```
[Hook] active - 2 injectable item(s) configured:
[Hook]   item[0] 'QmBedrl_01' -> R5BuildingItem::DA_BI_QmBedrl_01 (target='BuildingDecoration')
[Hook]   item[1] 'MyItem' -> R5BuildingItem::DA_BI_MyItem_01 (target='BuildingDecoration')

[Spawn] *** SUCCESS *** ... ctx=inject item[0]='QmBedrl_01' pool=1
[Spawn] *** SUCCESS *** ... ctx=inject item[1]='MyItem' pool=2
[Foreign] hit#N INJECTED item[0]='QmBedrl_01' ...
[Foreign] hit#N INJECTED item[1]='MyItem' ...
```

## Bekannte offene Punkte

| Punkt | Status | Workaround |
|---|---|---|
| **R5BuildingItem-Klasse im Editor unbekannt** | Wir haben keinen Game-Source. Editor zeigt die Klasse im Asset-Picker eventuell nicht an. | usmap-File des Games (`R5-5.6.1-0+UE5-20260518.usmap` im Repo-Root) ins Editor-Projekt-Config einbinden? Oder DataAsset als generic `PrimaryDataAsset` + Runtime-Override. |
| **Vanilla-Asset-Paths im Editor** | `/Game/Gameplay/Building/BuildingDecoration/` ist Vanilla-Pfad, Editor mountet das nicht automatisch | Eigener Namespace `/Game/Quartermaster/Mods/...` + DLL-Config matched darauf. Pfade in `qm_config.cpp` ergaenzen. |
| **DisplayName als FName-LocalizationKey** | Lokalisierungs-Pipeline ist anders als ein simples `FText::FromString` | Fuer ersten Test FText-Literal nutzen. Lokalisierung kann spaeter via `.locres`-Bundle nachgereicht werden. |
| **Asset-Pfad-Konsistenz** | Das DataAsset muss zur Runtime das Game-eigene `R5BuildingItem`-CDO als Klassen-Anker haben | Unser Inject-Pfad in `qm_inject.cpp` macht das automatisch via `donor->Class` - sehe `SpawnFreshItemWithOverride`. |
| **FGameplayTag-Resolution** | `BuildingItemTag` muss zur Runtime in der TagManager-Tabelle des Games auffindbar sein | Vanilla-Tag (`Building.Items.Decoration.Bedroll`) wiederverwenden bis wir eine Custom-Tag-Discovery haben. |
| **Pak-Mount-Reihenfolge** | Mod-Paks werden nach Vanilla-Paks geladen - sind die DataAssets aus dem Mod prioritaer? | Praezedenz: QmBedrl_P.pak laedt mit P-Suffix-Praefix, ueberschreibt nichts (nur Add). MyItem_P.pak gleiche Konvention. |

## Praezedenz: andere Mods im Quartermaster-Repo

Die folgenden Mods haben aehnliche Asset-Pipelines geloest und dienen als Praezedenz:

- **Ship Music** (`Docs/PLAN-ShipMusicAddTracks.md`): zeigt UE5.6-Cook-Workflow + retoc to-zen + Quartermaster-IoStore-Bundle
- **Persistent Loot** (`References/Persistent Loot/`): zeigt minimalen DataAsset-Patch-Workflow
- **Friendly Alpha Wolf** (`References/Friendly Alpha Wolf/`): zeigt Mob-Behaviour-Replace via DataAsset

Wenn etwas nicht klappt: zuerst dort schauen wie aehnliche Probleme dort geloest sind.

## Naechste Iteration: Asset-Authoring vereinfachen

Sobald der erste eigene Mesh durch die Pipeline laeuft, sollten folgende Punkte angegangen werden:

1. **Mod-Pak-Build-Script** als `.bat` in `Tools/Scripts/` - automatisiert Schritt 10-11
2. **Config-Loader** in der DLL der eine `Quartermaster.ini` neben der DLL liest, damit Items ohne Recompile addable werden
3. **Eigene Lokalisierung-Bundle**-Doku als zusaetzliches Howto
4. **Custom GameplayTags** ueber UE-Editor-Tag-Manager + Cooked-Tag-Tabelle in Mod-Pak
