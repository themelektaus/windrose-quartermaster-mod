# Work In Progress: CSV Localization Patcher

Stand: 2026-05-17

## Big Picture

Ziel: Beliebige im Spiel sichtbare Texte (Item-Namen, Decoration-Namen,
Beschreibungen, UI-Strings) sollen ueber eine simple String-Edit-Mechanik
veraenderbar sein, ohne ein Asset im UE-Editor anfassen zu muessen.

Status: **Mechanik verifiziert, Feature noch nicht implementiert.** Ein manueller
Probetest hat gezeigt dass eine modifizierte `BuildingItems.csv` aus einem Mod-Pak
zur Laufzeit greift und die Anzeige im Build-Modus aendert.

---

## Discovery / verifizierter Probetest

Am 2026-05-17 wurde manuell getestet ob das Spiel Localization-CSVs zur Laufzeit
direkt liest, obwohl UE5 normalerweise nur kompilierte `.locres`-Dateien
verwendet.

**Probe-Setup:**

```
.smoke-test/CSVProbe_P.pak (V11, mount-point ../../../)
  R5/Content/Localization/Data/BuildingItems.csv
    Decorations_Bucket_01_Name -> "Quartermaster Test"
```

Pak in `R5/Content/Paks/~mods/` deployed, Spiel gestartet, Build-Modus geoeffnet,
Kategorie Dekorationen.

**Beobachtetes Ergebnis:** Der Eimer wird im Build-Menue als "Quartermaster Test"
angezeigt statt unter seinem Vanilla-Namen.

**Schlussfolgerung:** Das Spiel liest entweder die CSV direkt zur Laufzeit, oder
unser Mod-Pak hat hoehere Prioritaet als die kompilierte `Game.locres`. Beide
Faelle bedeuten dass wir Texte ueber das CSV-Override-Verfahren patchen koennen,
ohne `.locres` selbst neu bauen zu muessen.

---

## Wie der Lookup-Pfad funktioniert (vermutet)

```
Build-Menue zeigt Decoration
  -> Game-Code construct't einen Lookup-Key aus dem Asset-Stem:
     DA_BI_Bucket_01 -> Decorations_Bucket_01_Name
  -> FText-Lookup im Namespace
  -> findet Eintrag in BuildingItems.csv (Pak-Override schlaegt Vanilla)
  -> zeigt "Quartermaster Test"
```

Welche Lookup-Keys das Spiel fuer welche Slots construct't ist asset-typ-abhaengig
und teilweise rate-basiert. Fuer Decorations matched offensichtlich
`Decorations_<Stem>_Name`. Andere CSVs muessen wir noch erkunden.

---

## Welche CSVs gibt es im Vanilla (Stand recon)

Bekannt aus den extrahierten Vanilla-Paks:

| CSV | Was sie vermutlich uebersetzt | Verifiziert? |
|---|---|---|
| `R5/Content/Localization/Data/BuildingItems.csv` | Decoration-Namen im Build-Modus | ja (Probetest) |
| weitere `*.csv` unter `R5/Content/Localization/Data/` | Inventory, Quests, UI, Tooltips - tbd | nein |

Vor der Implementierung muss ein Scrape-Run alle CSVs unter `R5/Content/Localization/Data/`
listen und ihre Spaltenstruktur dokumentieren. Schema-Annahme: `Key,SourceString,Comment` mit UTF-8 BOM.

---

## Was sich patchen laesst, was nicht

| Kategorie | Patchbar via CSV? | Anmerkung |
|---|---|---|
| Decoration-Namen im Build-Modus | ja (verifiziert) | `BuildingItems.csv` |
| Inventory-Item-Namen | wahrscheinlich ja | analog vermutet, eigene CSV - muss verifiziert werden |
| Item-Tooltips / Descriptions | wahrscheinlich ja | analog vermutet |
| Quest-Texte | tbd | analog vermutet |
| UI-Strings (Menues, HUD) | tbd | analog vermutet |
| Hard-coded FText in Blueprint-Properties | **nein** | muessten weiter ueber locres-Patch oder Asset-Edit gehen |
| Generated/Procedural-Strings (Code-Format) | nein | Code-Pfad nicht von CSV-Lookup abhaengig |

---

## Wie das Feature aussehen koennte (Implementierungs-Skizze)

### Backend

1. **`CsvLocalizationCatalog.cs`** - listet alle CSVs aus dem Vanilla-Dump,
   parsed Headers, sammelt verfuegbare Keys. Wird beim Build-Setup einmalig
   gefuellt (analog zu `ItemCatalog`).
2. **`CsvLocalizationPatcher.cs`** - nimmt User-Overrides (Key -> Replacement-Text),
   mergeed in die jeweilige Vanilla-CSV, schreibt geaenderte CSV nach Staging.
3. **`Profile.cs`** - neuer Block `LocalizationOverrides`:
   ```json
   "LocalizationOverrides": {
     "BuildingItems.csv": {
       "Decorations_Bucket_01_Name": "My Bucket",
       "Decorations_DecorBooks_02_Name": "Old Books"
     }
   }
   ```
4. **`BuildPipeline.cs`** - vor dem IoStore-Build die Vanilla-CSV laden,
   Overrides anwenden, geaenderte CSV ins legacy-Staging schreiben.
   IoStore-Composite-Builder zieht sie dann mit ein.

### Frontend

Neuer Tab "Localization" mit einer 2-Stufen-UI:

- **Stufe 1** - Dropdown / Suche nach CSV (z.B. "BuildingItems")
- **Stufe 2** - Tabelle aller Keys aus der CSV mit zwei Spalten:
  - Vanilla-String (read-only)
  - User-Override (textbox, leer = Vanilla, gefuellt = Override)

Convenience: Suchfeld ueber die Tabelle, "Reset all" Button pro CSV.

### Pak-Format

CSV liegt im Mod-Pak als legacy V11 (kein IoStore noetig fuer reine Daten-Files,
das Spiel liest sie direkt). Alternativ via IoStore-Composite mit reingenommen
falls wir die paks konsolidieren wollen.

---

## Offene Fragen / Risiken

| Frage | Wie zu klaeren | Risiko |
|---|---|---|
| Welcher Sprache-Switch? Die CSV ist offensichtlich nur eine - aber wenn das Spiel sprachen-spezifisch liest, brauchen wir locres-spezifisches Patching | Probetest mit Sprache umschalten - greift die CSV auch wenn der User auf English / Deutsch spielt? | mittel - falls sprachen-spezifisch, mehr Engineering |
| Welche anderen CSVs greifen runtime und welche nicht? | Per CSV einen Probe-Override deployen | niedrig - kostet jeweils nur 5 Minuten |
| Werden CSV-Overrides vom Server (Multiplayer-Saves) validiert? | Im Multiplayer testen | niedrig - im Worst-Case nur Single-Player-Feature |
| Was ist der genaue Lookup-Schluessel-Schema pro Asset-Typ? | Asset-by-asset reverse-engineering durch Stem-Pattern-Probing | mittel - kann pro Asset-Typ anders sein |

---

## Realistische Aufwandsschaetzung

- Backend (Catalog + Patcher + Profile + Wiring): ~1-1.5 Tage
- Frontend (Tab + Tabelle + Search + Reset): ~0.5-1 Tag
- CSV-Discovery-Run + Schema-Doku: ~0.5 Tag
- Sprachen-Verhalten verifizieren: ~0.5 Tag

Netto: 2.5 - 3.5 Tage fuer ein nutzbares Feature.

---

## Verhaeltnis zum Build-Mode-MVP

Der Build-Mode-MVP (eigener Decoration-Slot in der Kategorie Dekorationen) ist
**unabhaengig** von diesem Feature. Er braucht das CSV-Override-Verfahren aber
intern: wenn wir einen neuen Slot `DA_BI_QmCustom_01` clonen, brauchen wir auch
einen passenden Eintrag `Decorations_QmCustom_01_Name = "Test Decoration"` in
der CSV.

Sequenz:
1. Build-Mode-MVP zuerst (verifiziert "Spiel discovered neue Slots")
2. Sobald MVP laeuft: dieses Feature als generischer Patcher dazubauen
   (Benutzer-Override-UI fuer alle Texte, nicht nur den eigenen Slot)

Reihenfolge umgekehrt waere okay, aber der Build-Mode-MVP gibt uns das groessere
Discovery-Signal.
