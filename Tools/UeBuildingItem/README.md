# Quartermaster - UE5.6 Building-Item Template

Dieses Verzeichnis enthaelt einen einmal auszufuehrenden Python-Helper, der
in deinem UE5.6 Editor-Projekt ein wiederverwendbares Template-Set erzeugt.
Danach duplizierst du nur noch die Template-DataAsset und fuellst die Refs.

## Was am Ende rauskommt

Im UE-Projekt unter `/Game/Quartermaster/Template/`:

| Asset | Typ | Zweck |
|---|---|---|
| `BP_QmBuildingItem` | Blueprint-Klasse (Parent: `PrimaryDataAsset`) | Definiert die Properties, die jedes Mod-Item braucht (Mesh, Material, Icon, DisplayName, Description). |
| `DA_Template_QmItem` | DataAsset-Instanz von `BP_QmBuildingItem` | Dein "leeres" Start-Asset. Fuer jedes neue Mod-Item: duplizieren -> umbenennen -> Refs setzen. |
| `M_Template_QmItem` | Material | Leeres Material als Slot. Open + editieren oder durch eigenes Material ersetzen. |

## Workflow Schnell-Uebersicht

```
EINMALIG:                                FUER JEDES NEUE ITEM:
+------------------+                     +------------------------+
| Python-Script    |                     | Duplicate              |
| ausfuehren       |                     | DA_Template_QmItem     |
+--------+---------+                     +-----------+------------+
         |                                           |
         V                                           V
+------------------+                     +------------------------+
| BP-Properties    |                     | Refs setzen:           |
| manuell anlegen  |                     |   Mesh = SM_MyItem     |
| (Mesh/Mat/...)   |                     |   Material = M_MyItem  |
+------------------+                     |   Icon = T_MyItem_Icon |
                                         +-----------+------------+
                                                     |
                                                     V
                                            Howto Schritt 9 (Cook)
```

## Setup

### 1. UE5.6 Projekt vorbereiten

Falls noch keins existiert:

- Epic Launcher -> Unreal Engine -> 5.6.x -> "Launch"
- **Games -> Blank**, Blueprint, kein Starter-Content
- Name: `QuartermasterModSamples` (Vorschlag), Location frei waehlbar
- "Create"

### 2. Python-Plugin aktivieren

- **Edit -> Plugins**
- Suche nach "Python"
- Aktiviere **"Python Editor Script Plugin"**
- Editor neu starten

### 3. Script ausfuehren

Zwei Wege - der zweite ist gemuetlicher:

**Weg A: Output Log Prompt**

- **Window -> Output Log** oeffnen
- Im unteren Eingabefeld den Dropdown von "Cmd" auf **"Python"** umstellen
- Folgende Zeile einfuegen (Pfad ggf. anpassen):

  ```python
  exec(open(r"E:\Windrose\Mods\Quartermaster\Tools\UeBuildingItem\create_template.py").read())
  ```

- Enter

**Weg B: Tools-Menue**

- **Tools -> Execute Python Script...**
- Datei `create_template.py` aus diesem Repo waehlen
- "Open"

### 4. Verifizieren

Im Content Browser zu `/Game/Quartermaster/Template/` navigieren -
dort liegen jetzt BP, DA und Material.

Output-Log sollte `[Quartermaster] === DONE ===` und ein paar
"Next Steps" Zeilen anzeigen.

## Properties einrichten (einmalig im BP-Editor)

Das Python-Script erzeugt die Blueprint-Huelle, aber die Variablen
muessen einmal manuell angelegt werden (UE-Python-API ist da
versionsabhaengig instabil). Ist in 3 Minuten erledigt:

1. **Doppelklick auf `BP_QmBuildingItem`** im Content Browser.
2. **My Blueprint** Panel (links unten) -> Section **"Variables"** -> `+ Variable`
3. Lege folgende Variablen an. Fuer jede:
   - Name eingeben
   - Im Details-Panel (rechts): **Variable Type** auf den Wunsch-Typ stellen
   - **Instance Editable** anhaken (damit man die Variable in der DA setzen kann)

   | Variable | Variable Type | Hinweis |
   |---|---|---|
   | `Mesh` | `Static Mesh` -> Object Reference | Drag-Slot fuers Static-Mesh-Asset |
   | `Material` | `Material Interface` -> Object Reference | Drag-Slot fuers Material |
   | `Icon` | `Texture 2D` -> Object Reference | UI-Icon, quadratisch |
   | `DisplayName` | `Text` | Anzeigename im Build-Menue |
   | `Description` | `Text` | Tooltip-Text |

4. **Compile** (Ctrl+F7) und **Save** (Ctrl+S).
5. Doppelklick auf `DA_Template_QmItem` - die fuenf Slots sollten jetzt
   sichtbar sein, alle leer.

## Anwenden im Mod-Workflow

Folge ab hier wieder der [Hauptanleitung](../../Docs/Howto_AuthorBuildingItem.md):

- Aus Schritt 4-7 kannst du den "Vanilla-Asset-Import"-Teil ueberspringen -
  dein Start-Asset ist jetzt `DA_Template_QmItem` (duplicate it).
- Eigenes Mesh, Material, Icon importieren wie in Schritt 5-7 beschrieben.
- Refs in deinem duplizierten DataAsset setzen.
- Cook + retoc to-zen + Deploy + Config-Eintrag wie in Schritt 9-13.

## Bekannte Einschraenkungen (v1)

| Issue | Wirkung | Workaround |
|---|---|---|
| **Class-Ref im cooked Asset** | Nach Cook hat die `.uasset` `BP_QmBuildingItem_C` als Class, aber das Game erwartet `R5BuildingItem`. Ohne Patch wird das Asset zwar geladen, aber Mesh/Material/Icon-Refs werden vom Game ignoriert (falsche Property-Layout). | Erste echte Cook-Iteration zeigt was passiert. Sobald wir das Symptom haben, bauen wir ein Post-Cook-Class-Ref-Patcher-Skript dazu (kleines Python-Tool, das im NameMap der cooked `.uasset` den Class-Eintrag von `BP_QmBuildingItem_C` auf `R5BuildingItem` umschreibt). |
| **BP-Properties nicht via Script** | Du musst die 5 Variablen einmal manuell im BP-Editor anlegen. | Im README-Abschnitt "Properties einrichten" Schritt-fuer-Schritt dokumentiert. |
| **Keine Mesh-Auto-Import** | Script erzeugt kein Static-Mesh-Asset (ohne Quell-FBX kein Inhalt). | User importiert eigenes Mesh wie in Howto Schritt 7. |

Die Class-Ref-Patcher-Frage klaeren wir beim ersten echten In-Game-Test
gemeinsam - moeglich dass das gar nicht noetig ist, falls der Donor-Class-
Override des DLL-Hooks die Properties beim Hydraten neu mappt.

## Wiederholtes Ausfuehren

Script ist idempotent: bestehende Template-Assets werden geprueft und
nicht ueberschrieben. Du kannst es jederzeit nochmal laufen lassen ohne
Datenverlust.

Falls du das Template **bewusst** zuruecksetzen willst:

1. Im Content Browser zu `/Game/Quartermaster/Template/`
2. Alle drei Assets markieren + Delete
3. Script erneut laufen lassen
