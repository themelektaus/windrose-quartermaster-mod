# Work In Progress: Static Mesh Replacement

Stand: 2026-05-17

## Big Picture

Ziel: Ein vanilla Static Mesh (Inventar-Item, Schiffsteil, Moebel, Welt-Prop) soll durch
ein vom User selbst erstelltes Modell ersetzt werden koennen, inklusive Material und
Textur. Der Workflow soll analog zur funktionierenden Ship-Music-Pipeline laufen:
User cookt einmal im UE5.6-Editor, Quartermaster patcht die FName-Table auf den
Vanilla-Slot und buendelt das Ergebnis in einen IoStore-Pak.

Aktueller Status: noch nicht implementiert. Dieses Dokument beschreibt den geplanten
Workflow + offene Fragen.

---

## Warum nicht "FBX hochladen und Quartermaster cookt"

Static Meshes lassen sich nicht template-splice-en wie Bink-Audio. Gruende:

| Problem | Detail |
|---|---|
| Kein Standalone-Encoder | UE5-Mesh-Cook ist tief im Editor verdrahtet (GPU-Vertex-Format, LOD-Build, Lightmap-UV-Pack, Collision-Build). Keine `.lib` wie bei Bink. |
| Jeder Asset hat eigene Shape | Vertex-Count, Material-Slot-Count, LOD-Count, Collision-Typ sind pro Asset anders. Single-Template-Splice greift nicht. |
| Cross-Asset-Referenzen | Static Mesh referenziert Materials, Materials referenzieren Texturen. Replace ohne Begleitassets = visuelle Glitches. |
| Nanite | Falls Windrose Nanite nutzt: proprietaeres GPU-Format, praktisch nicht nachbaubar. |

Konsequenz: User muss den Cook im UE5.6-Editor selbst machen. Quartermaster uebernimmt
nur das Asset-Routing (FName-Patch + IoStore-Bundle).

---

## User-Workflow (UE5.6-Editor)

### Vorbereitung

- UE5.6.x installiert (Epic Launcher oder Source-Build)
- Eigenes Mesh als `.fbx` oder `.obj` (FBX bevorzugt, weil Materialgruppen + UVs mit drin)
- Optional: Textur-Dateien als `.png` / `.tga` / `.jpg`

### Schritt 1: Leeres UE5.6-Projekt anlegen

- Epic Launcher -> Unreal Engine -> 5.6.x -> "Launch"
- **Games -> Blank**, Blueprint, No Starter Content
- Name: `MeshExporter` (oder beliebig), Location frei waehlbar
- "Create"

Optional: vorhandenes `AudioEncoder`-Projekt wiederverwenden, kostet nichts.

### Schritt 2: Cook-Settings einmalig anpassen

- **Edit -> Project Settings -> Project -> Packaging**
- Aktivieren: **"Cook everything in the project content directory"**

Damit Quartermaster auch freistehende Assets bekommt (analog Audio-Setup).

### Schritt 3: Texturen importieren

- Content Browser -> Rechtsklick -> **Import to /All/Content/**
- Textur-Dateien auswaehlen (Diffuse / Normal / Roughness / etc.)
- Import-Dialog: Defaults sind OK, "Import All"

Resultat: pro Textur ein `Texture2D`-Asset im Projekt.

### Schritt 4: Material anlegen

- Content Browser -> Rechtsklick -> **Material -> Material**
- Name z.B. `M_MyItem`
- Doppelklick oeffnet den Material-Editor
- Texturen in den Graph ziehen (drag-and-drop aus Content Browser), an die passenden
  Eingaenge des "Result"-Nodes anbinden:
  - Diffuse / BaseColor -> `Base Color`
  - Normal -> `Normal` (Texture-Sampler muss auf `Normal` umgestellt sein, Detail-Pane)
  - Roughness -> `Roughness`
  - Metallic -> `Metallic`
- **Apply** (Knopf oben links), dann **Save** (Ctrl+S)

Tipp: fuer einen ersten Test reicht **nur Diffuse**. Material kann minimal sein.

### Schritt 5: Mesh importieren

- Content Browser -> Rechtsklick -> **Import to /All/Content/**
- FBX-Datei auswaehlen
- Import-Dialog:
  - **Static Mesh** Checkbox an, **Skeletal Mesh** aus
  - **Generate Lightmap UVs** an (wichtig fuer Beleuchtung)
  - **Auto Generate Collision** an (oder spaeter manuell)
  - **Import Materials** aus (wir nutzen unser eigenes)
  - **Import Textures** aus
- "Import All"

### Schritt 6: Material zuweisen

- Doppelklick auf das importierte Static Mesh
- Details-Panel rechts -> **Material Slots**
- Pro Slot: Dropdown -> unser Material auswaehlen
- Speichern (Ctrl+S)

### Schritt 7: Asset auf Slot-Namen umbenennen

**Kritisch:** Der Asset-Name muss exakt dem Vanilla-Slot-Stem entsprechen, den wir
ersetzen wollen (siehe Slot-Katalog unten).

- Content Browser -> Rechtsklick auf das Mesh-Asset -> **Rename**
- Neuer Name: z.B. `SM_Vanilla_SlotName` (Stem ohne Pfad / Extension)

Hintergrund: Quartermaster patcht die NameMap intern auf den Vanilla-Pfad. Wenn der
Asset-Stem schon passt, ist der Patch trivial.

Alternativ (wenn unklar welcher Stem): Asset behalten, Quartermaster patcht dann
beim Upload den Stem im NameMap nach.

### Schritt 8: Cooken

- **File -> Cook Content for Windows**
- Wartezeit: 30 Sekunden bis 2 Minuten je nach Asset-Anzahl

Output liegt unter:
```
<Projekt>/Saved/Cooked/Windows/<ProjektName>/Content/
  SM_MyItem.uasset
  SM_MyItem.uexp
  SM_MyItem.ubulk            (eventuell, falls Mesh gross)
  M_MyItem.uasset
  M_MyItem.uexp
  T_MyItem_Diffuse.uasset
  T_MyItem_Diffuse.uexp
  T_MyItem_Diffuse.ubulk
  ...
```

Pro Asset ein `.uasset` plus optional `.uexp` / `.ubulk` (gleiche Triplet-Mechanik wie
bei Audio).

### Schritt 9: Upload in Quartermaster

User waehlt im Mesh-Tab einen Vanilla-Slot, droppt **alle Cook-Output-Dateien des
Asset-Sets** rein (Mesh + Material + Texturen). Quartermaster:
1. Erkennt Mesh-Triplet anhand der Class-Tags
2. Erkennt mitgelieferte Material- und Textur-Assets als Begleitassets
3. Patcht NameMap des Mesh-Assets auf den Vanilla-Slot-Pfad
4. Behaelt Material- und Textur-Pfade unter `/Game/<UserNamespace>/` (kein NameMap-Patch
   noetig, da die intern referenziert sind und im selben Pak landen)
5. Buendelt alles in einen IoStore-Pak unter `~mods/`

---

## Quartermaster-Side: was zu bauen ist

### Backend

| Komponente | Aufgabe |
|---|---|
| `StaticMeshSlots.cs` (NEU) | Katalog der ersetzbaren Vanilla-Slots (Pfad + Anzeige-Name + Kategorie). Wird aus den vanilla paks gescraped. |
| `StaticMeshPatcher.cs` (NEU) | Nimmt User-Triplet entgegen, identifiziert das Mesh-Asset (Class = `StaticMesh`), patcht NameMap `/Game/UserPath/SM_X` -> `/Game/<VanillaSlotPath>/SM_X`. Begleitassets (Material, Texturen) bleiben unter ihrem User-Pfad. SerialSize / DataResources fixen analog zum Audio-Patcher. |
| `MeshUploadPreflight.cs` (NEU) | Vor dem Patch: pruefen ob mindestens ein `.uasset` mit Class `StaticMesh` dabei ist, Begleitassets validieren (kein orphan Material das nichts referenziert). |
| `BuildPipeline.cs` | Neuer `StaticMeshJob` analog zu `ShipMusicJob`. Pre-Staging in `__iostore/legacy/legacy/...`. |
| `Profile.cs` | `StaticMeshGlobal.Slots` Dict mit `StaticMeshSlotOverride` (sourceFileNames-Liste). |
| `WindrosePaths.cs` | `ProfileStaticMeshSlotDir` (Storage pro Slot mit allen Begleitassets). |

### Endpoint

`/api/profiles/{id}/static-mesh/{slotKey}`:
- **POST**: Multipart-Upload mit allen Asset-Files (`.uasset` / `.uexp` / `.ubulk`). Pruefung
  dass mindestens ein `StaticMesh` dabei ist.
- **GET**: Liste aktiver Slots
- **DELETE**: Slot zurueck auf vanilla

Achtung Body-Limit: Texturen koennen pro Stueck mehrere MB sein, ein Asset-Set 10-50 MB.
Aktueller Kestrel-Cap (200 MB) reicht. Falls hochaufloesende Textur-Sets: hoch auf
500 MB ziehen.

### Frontend

Neuer Tab `staticmesh.html/js/css`:
- Liste der Slot-Kategorien (Inventar / Schiff / Moebel / Props) -> aufklappbare Sections
- Pro Slot eine Card mit Drag-and-Drop fuer ganze Cook-Output-Ordner
- Status: "Vanilla" / "Custom: N Dateien (X MB)"
- "Reset to Vanilla"

Optional Phase 2: 3D-Preview im Browser via three.js / model-viewer (parsed `.uasset`-Vertex-Buffer -> WebGL).

---

## Offene Fragen (vor dem Loslegen zu klaeren)

### Slot-Katalog

Welche Vanilla-Static-Meshes sind sinnvoll als ersetzbare Slots? Drei Strategien:

| Strategie | Pro | Contra |
|---|---|---|
| Curated (z.B. 50 ausgewaehlte Inventar-Items + 20 Schiffsteile + 30 Moebel) | UI ist sauber, Slot-Namen sind sprechend ("Cutlass", "Cannon", "Treasure Chest") | Manueller Aufwand: Pfade scrapen + Anzeige-Namen pflegen |
| Auto-Scrape alles | Vollstaendig, kein Pflegeaufwand | UI: 5000+ Eintraege, unbedienbar |
| Universal-Replacer (User gibt Pfad direkt ein) | Maximum-Flexibilitaet | Power-User-Feature, kein Discovery |

Empfehlung: Curated fuer v1, Universal als Power-User-Schalter danebenstellen.

### Material-Pfad-Strategie

Wenn das User-Mesh ein Material referenziert das unter `/Game/MeshExporter/M_MyItem`
liegt, muss dieser Pfad im selben Pak landen. Zwei Optionen:

1. **Material-Pfade beibehalten** (`/Game/MeshExporter/M_MyItem`): Pak enthaelt sowohl
   Vanilla-Slot-Pfad (gepatched) als auch User-Pfad. Konflikt-Risiko falls zwei User
   den gleichen Material-Namen waehlen -> Namespace pro Slot empfohlen
   (`/Game/Quartermaster/<SlotKey>/M_MyItem`).
2. **Material auf Vanilla-Slot-Pfad mit-patchen**: komplizierter, weil Vanilla-Material
   eventuell shared ist (mehrere vanilla Items nutzen das gleiche Material). Replace
   wuerde alle betreffen -> nicht gewollt.

Empfehlung: Option 1 mit Quartermaster-eigenem Namespace.

### Collision

UE5-Static-Mesh-Cook bringt Collision automatisch mit (Box / Convex / Tri-Mesh).
Frage: muss das Vanilla-Collision-Profil (z.B. `BlockAll`) gespiegelt werden, oder
reicht das was der Editor per Default emittiert? -> Beim ersten konkreten Test
empirisch klaeren.

### LOD-Strategie

Vanilla-Items haben typischerweise 3-5 LODs. Wenn User-Mesh nur LOD0 hat, wird das
in der Distanz haesslich aussehen. UE5-Editor kann LODs auto-generieren
(`StaticMesh -> Details -> LOD Settings -> Number of LODs = 4`).

Im User-Workflow muss das als Empfehlung in der Anleitung stehen.

### Nanite

Wenn Vanilla Nanite nutzt, muesste auch das User-Mesh Nanite haben:
`StaticMesh -> Details -> Nanite Settings -> Enable Nanite = true`.

Zu klaeren: nutzt Windrose Nanite ueberhaupt? Erstmal vanilla-Mesh extrahieren und
in den serialisierten Bytes nach `bNaniteEnabled` schauen.

### Texture-Streaming

UE5-Texturen werden gestreamt (Mip-Level). Das ist eine eigene Bulk-Daten-Mechanik,
analog zu USoundWave-Streaming. Beim ersten Test wird sich zeigen ob der
Editor-Cook das automatisch korrekt emittiert, oder ob wir analog zur
`ShipMusicPatcher`-`ForceInline`-Loesung den Streaming-Modus deaktivieren muessen
(`Texture2D -> Texture Asset Compression -> Loading Behavior`?).

---

## Aufwandsschaetzung

| Phase | Aufwand | Risiko |
|---|---|---|
| Slot-Katalog scrapen + kuratieren | 1 Tag | niedrig |
| `StaticMeshPatcher` + Pipeline-Wiring | 2-3 Tage | mittel (Begleitassets-Handling, Namespace) |
| Frontend-Tab | 1 Tag | niedrig |
| Erster konkreter Smoke-Test (1 Slot ersetzen, in-Game verifizieren) | 1 Tag | mittel-hoch (Collision, LOD, Nanite, Streaming koennen alle Stolpersteine sein) |
| Bug-Fixing + Edge-Cases | unbekannt | abhaengig vom Smoke-Test |
| **Summe v1** | **~5-7 Tage** | mittel |

Vergleich Ship Music: dort waren es netto ~3 Tage. Static Mesh wird laenger weil mehr
Begleitassets im Spiel, und weil pro Asset-Typ (Mesh / Material / Textur) eigene
Cook-Eigenheiten auftreten koennen.

---

## Naechste Schritte (wenn wir loslegen)

1. **Recon**: vanilla paks nach Static-Mesh-Eintraegen scrapen, Asset-Pfad-Listen
   gruppieren (Items / Ship / Furniture / Props). Output: `StaticMeshSlots-vanilla.csv`
   als Basis fuer den Slot-Katalog.
2. **Smoke-Test mit konkretem Slot**: einen einfachen Slot ausssuchen (z.B. ein
   stationaeres Inventar-Item), Vanilla-Triplet extrahieren, Pfade und Struktur
   analysieren. Eigenen Cook auf den gleichen Stem machen, FName-Patch hardcoded
   testen, in-Game verifizieren.
3. Erst wenn Smoke-Test gruen ist: Patcher + Frontend bauen.

Bevor Schritt 1 startet, will der User noch entscheiden ob es ueberhaupt los geht
und in welchem Scope (Items only / Ship / Universal Replacer). Siehe offene Frage
"Slot-Katalog" oben.
