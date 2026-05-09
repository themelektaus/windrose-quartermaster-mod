# Plan: Description (und andere FText-Felder) per Item überschreiben

Status: **Planungsnotiz** — noch nicht umgesetzt. Aufgegriffen wird das, sobald
ein konkreter Use-Case dafür spricht (oder Lust am Wochenende).

## Ausgangslage

Die `ItemDescription` (und `ItemName`, `EffectsDescriptions[]`, `VanityText`,
`SetEffectsDescriptions[]`) in den Vanilla-JSONs sind **keine Strings**, sondern
`FText`-Referenzen auf eine StringTable:

```json
"ItemDescription": {
  "TableId": "InventoryItems",
  "Key": "CID_Food_Raw_Banana_T01_ItemDescription"
}
```

Der eigentliche Text liegt in einer `StringTable.uasset` im Game-Pak und wird
pro Sprache über `.locres`-Files lokalisiert (12 Cultures: `de, en, es, fr,
it, ja, ko, pl, pt-BR, ru, tr, zh-Hans`). Den `IconExtractor` nutzen wir heute
schon, um diese Strings beim Icon-Extract pro Sprache aufzulösen und als
Sidecar-JSON zu speichern.

## Drei Wege wurden evaluiert

| Ansatz | Wie | Aufwand | Tradeoff |
|---|---|---|---|
| A) Inline-String (CultureInvariantText) | JSON-Struct durch literal String ersetzen | klein | **Lokalisierung weg**, alle Sprachen identisch |
| **B) .locres-Override pro Sprache** | **Mod-Pak liefert eigene `.locres`-Files mit nur den geänderten Keys** | **mittel-hoch** | **Saubere Lokalisierung; Engine merged Mod-Strings über Vanilla** |
| C) StringTable.uasset überschreiben | Custom UE-Asset bauen | hoch | UE-Asset-Format binär + version-sensitiv, kein Tooling vorhanden |

**Entscheidung:** Ansatz B.

## Wie .locres-Files funktionieren (Recherche-Notizen)

Quelle: `Tools/CUE4Parse/CUE4Parse/UE4/Localization/FTextLocalizationResource.cs`
(Reader-Implementierung) sowie `FTextLocalizationResourceString.cs`.

### Datei-Layout (`ELocResVersion.Optimized_CRC32`, das aktuelle UE5-Format)

```
+0   FGuid LocResMagic = {0x7574140E, 0xFC034A67, 0x9D90154A, 0x1B7F37C3}
+16  byte  VersionNumber                 (= 3 für Optimized_CRC32)
+17  long  LocalizedStringArrayOffset    (absolut, am Ende der Datei)
+25  uint  EntriesCount                  (Total über alle Namespaces)
+29  uint  NamespaceCount
     foreach Namespace:
        FTextKey Namespace      (uint hash + FString)
        uint     KeyCount
        foreach Key:
            FTextKey Key        (uint hash + FString)
            uint     SourceStringHash
            int      LocalizedStringIndex   (Index in das StringArray)
     ...
+X   FTextLocalizationResourceString[]  (Array, am LocalizedStringArrayOffset)
        FString String
        int     RefCount
```

### `FTextKey`

Ein Namespace/Key-Eintrag ist ein `(uint StrCrc32, FString Str)`-Tupel. Der
Hash ist eine **CRC32 über die UTF-16-Bytes der lowercased Strings** (genau:
`StrCrc32_CaseInsensitive`). Im Reader wird er nur gelesen; beim Schreiben
müssen wir ihn selbst berechnen.

### `SourceStringHash`

CRC32 über den **englischen** Source-String, wird zur Drift-Detection benutzt
(wenn der Vanilla-Text sich ändert, invalidiert UE den Override). Für unsere
Mod-Override-Use-Case nehmen wir entweder den korrekten Hash aus dem Vanilla-
`.locres` (haben wir gelesen) oder `0` als "always trust".

### `FString`

UE-Format: `int Length` gefolgt von Bytes.
- `Length > 0`: ASCII (1 Byte/Char), nullterminated
- `Length < 0`: UTF-16 LE (`-Length` Codepoints), nullterminated
- `Length == 0`: leerer String

CJK + Akzent-Kram braucht UTF-16; ASCII-only kann 1-Byte bleiben (kompakter).
Sicherer Default: immer UTF-16 schreiben.

### Localized-String-Array & Refcounting

Das StringArray am Ende ist eine **Deduplizierungs-Optimierung**: identische
Übersetzungen werden nur einmal gespeichert, alle Keys verweisen per Index
darauf. `RefCount = -1` heißt "permanent referenced". Beim Schreiben können
wir der Einfachheit halber `RefCount = 1` pro Eintrag setzen und keine
Deduplizierung machen — die Mod-`.locres`-Files sind klein (50-100 Einträge),
das spart kaum Bytes.

## Auflöser-Reihenfolge der Engine

UE lädt `.locres`-Files in folgender Reihenfolge und merged sie:

1. Game `.locres` aus `Engine/Content/Localization/`
2. Game `.locres` aus `Game/Content/Localization/`
3. **Mod-Pak `.locres` aus `Game/Content/Localization/`** (höhere Priority)

Heißt: Wir packen unsere Override-`.locres`-Files in dasselbe Pak wie die JSONs
und sie überschreiben Vanilla für die Keys, die wir reinschreiben — **ohne**
dass wir eine vollständige Kopie brauchen.

## Konkrete Pfade im Game-Pak

Beispiel Vanilla:
```
R5/Content/Localization/InventoryItems/de/InventoryItems.locres
R5/Content/Localization/InventoryItems/en/InventoryItems.locres
R5/Content/Localization/InventoryItems/zh-Hans/InventoryItems.locres
...
```

(Tatsächliche Pfade vor Implementierung verifizieren via repak unpack des
Vanilla-Paks — die JSONs nennen oft nur `TableId: "InventoryItems"`, der
File-Mount-Path muss gefunden werden.)

## Profile-Schema-Erweiterung

Heute:
```csharp
public sealed class ItemOverride { public int? StackSize; }
```

Erweitert:
```csharp
public sealed class ItemOverride
{
    public int? StackSize;
    public Dictionary<string, LocalizedTextOverride> Texts;  // null = keine Text-Overrides
}

public sealed class LocalizedTextOverride
{
    // key = "name" | "description" | "vanityText" | "effects[0]" | "effects[1]" | ...
    //       | "setEffects[0].name" | "setEffects[0].description" | ...
    public Dictionary<string, string> ByCulture;  // "de" -> "Mein Text", "en" -> "My text"
    // Cultures, die NICHT im Dict stehen, fallen auf Vanilla zurück.
}
```

JSON auf Disk:
```json
{
  "id": "...",
  "name": "Custom Stacks + Lore",
  "globals": { "stackSize": { "multiplier": 4 } },
  "overrides": {
    "DA_CID_Food_Raw_Banana_T01": {
      "stackSize": 200,
      "texts": {
        "description": {
          "byCulture": {
            "de": "Eine besonders große Banane. Hält 7 Minuten.",
            "en": "An exceptionally large banana. Lasts 7 minutes."
          }
        }
      }
    }
  }
}
```

## Implementierungs-Phasen

### Phase 1: `.locres`-Reader-Roundtrip (Sanity-Check)

CUE4Parse hat schon einen Reader. Bevor wir einen Writer bauen, **roundtrippen**
wir ein Vanilla-`.locres`:

1. CUE4Parse `FTextLocalizationResource.Read()` → in-memory model
2. Eigener Writer schreibt das Model wieder als bytes
3. Diff gegen das Original → muss byte-identisch sein (oder zumindest
   semantisch identisch nach erneutem Re-Read)

Damit haben wir bewiesen, dass unser Writer-Verständnis des Formats korrekt ist.

### Phase 2: Override-Builder

Klasse `LocResWriter` (in `Tools/QuartermasterCore/`):

```csharp
public sealed class LocResWriter
{
    private readonly Dictionary<string, Dictionary<string, string>> _entries = [];
    // namespace -> key -> localizedString

    public void Add(string ns, string key, string localizedString) { ... }

    public void WriteTo(Stream output) { ... }
    // schreibt im Optimized_CRC32-Format inkl. Magic-GUID + Version-Byte
}
```

CRC32-Hash: `System.IO.Hashing.Crc32` (BCL ab .NET 7).
Strings via UTF-16 LE schreiben (sichere Default-Wahl, deckt CJK ab).

### Phase 3: BuildPipeline-Integration

Neue Stufe in `BuildPipeline.Run()` zwischen Patch und Pak:

```
foreach culture in Profile.Cultures-mit-Overrides:
    var writer = new LocResWriter();
    foreach (itemId, override) in profile.Overrides:
        if override.Texts == null) continue;
        foreach (textKey, textOverride) in override.Texts:
            if (!textOverride.ByCulture.TryGetValue(culture, out var str)) continue;
            // Vanilla-Sidecar liefert TableId+Key für textKey:
            var (ns, key) = ResolveTextRef(itemId, textKey);
            writer.Add(ns, key, str);
    writer.WriteTo($"{tempDir}/R5/Content/Localization/InventoryItems/{culture}/InventoryItems.locres");
```

`ResolveTextRef`: liest aus `Sources/Vanilla/<itemId>.json` die `{TableId,Key}`
für `name`/`description`/`vanityText`/`effects[i]`/`setEffects[i].name|description`.

### Phase 4: GUI

- Item-Card bekommt einen Expand-Button (`>` / `v`) der Text-Override-Editor
  ausklappt
- Editor zeigt aktuelle Sprache (Default: Browser-Locale, mit Sprachwahl-Dropdown)
- Pro Text-Feld (name, description, vanityText, effects[]): Textarea mit
  Vanilla-Wert als placeholder, User schreibt drüber → Override
- "Reset" pro Feld löscht den Override
- Visualisierung: Cards mit Text-Overrides bekommen einen zusätzlichen Marker
  (z.B. ein zweites Triangle-Tile in anderer Farbe)

### Phase 5: Edge-Cases

- **Locked Items** (vanilla=1, kein Promote): Text-Override soll trotzdem
  funktionieren — `.locres`-Override braucht keinen Stack-Patch zum Item-JSON.
- **Items mit `effects[]`-Array unterschiedlicher Länge zwischen Versionen**:
  Index-basierte Keys (`effects[2]`) können brechen, wenn Game-Update das Array
  umordnet. Mitigation: Override hält den **TableId+Key** des Vanilla-Eintrags
  fest (snapshot at edit time), nicht den Array-Index. Resolve-Step warnt,
  wenn Vanilla-Item den Key nicht mehr referenziert.
- **Description-Placeholder `{0}`/`{1}`**: Wenn der User die Description
  überschreibt, sind die Curve-Werte sein Problem. Optional: Warnung im UI,
  wenn der Text noch `{N}` enthält.
- **Drift-Detection via SourceStringHash**: Setzen wir auf `0` (= "trust mod"),
  damit Game-Updates die Mod-Strings nicht invalidieren, solange Namespace+Key
  noch existieren.

## Aufwand-Schätzung

| Phase | LoC | Risiko |
|---|---:|---|
| 1 — Roundtrip-Test | ~80 | gering (Reader existiert, nur Writer + Diff) |
| 2 — `LocResWriter` | ~150 | mittel (Format-Details, CRC32-Casing-Bug-Potential) |
| 3 — BuildPipeline | ~100 | gering (klare Erweiterung) |
| 4 — GUI-Editor | ~250 | mittel (UI ist die meiste Arbeit) |
| 5 — Edge-Cases + Tests | ~100 | gering |
| **gesamt** | **~680** | **mittel** |

Plus: ~1-2 Tage Recherche-/Debug-Pufer für das `.locres`-Format, weil das die
unbekannteste Komponente ist.

## Was JETZT noch zu tun wäre, falls priorisiert

1. Phase-1-Roundtrip schreiben — bevor wir alles plant haben, beweist das den
   kritischen Pfad. Wenn der Roundtrip nicht byte-identisch ist (oder nach
   Re-Read semantisch identisch), brauchen wir mehr Recherche.
2. Vanilla-`.locres`-Pfade im Pak verifizieren (`repak list` auf
   `R5/Content/Localization/`).
3. CRC32-Variante verifizieren: UE nutzt `Crc32_CaseInsensitive` über
   UTF-16-Bytes der lowercased Strings. Vor dem Writer-Bau einmal an einem
   bekannten Vanilla-Eintrag verifizieren.

## Out of Scope (für jetzt)

- **Item-Name-Override**: technisch identisch zur Description, GUI-mäßig
  trivial — bleibt aber einer Folge-Iteration vorbehalten, weil der Use-Case
  "Description tunen" zuerst kam.
- **VanityText / Effects / SetEffects**: dito, gleicher Mechanismus.
- **Eigene Cultures hinzufügen** (z.B. ein Item nur auf Schwäbisch): Engine
  muss die Culture im Stage-Set haben, sonst wird das `.locres` nie geladen.
  Sollte im Vanilla-Set bleiben (12 Cultures).
- **StringTable-Asset komplett ersetzen** (Ansatz C): bleibt Plan B, falls
  `.locres`-Override sich als zu zerbrechlich erweist.
