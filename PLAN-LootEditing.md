# PLAN: Loot-Editing als zweite Konfig-Domain (analog zu Stack-Size)

Status: Konzept, noch nicht umgesetzt. Erstellt nach Analyse der Reference-Mod
`MoreEnemyResources_2x_P.pak` und der Vanilla-LootTables im Game-Pak.

**Override-Scope**: Globals bleiben einfach (Multiplier auf Min+Max gekoppelt
pro Top-Level-Bucket). Per-LT-Overrides sind hingegen **vollständiger Entry-
CRUD** — jedes Feld eines Eintrags editierbar (Min, Max, Weight, LootItem,
LootTable), Vanilla-Einträge entfernbar, neue Einträge anhängbar. Damit ist
Loot in einem Profil vollständig modellierbar, inkl. der Custom-Add-Drops, die
die Reference-Mod bei Sergeant/Sailor macht.

## Ausgangslage

### Datenlage Vanilla-Pak (`R5/Plugins/R5BusinessRules/Content/LootTables/`)

| Kategorie           | LT-Files |
|---------------------|---------:|
| Chests              |      415 |
| Mobs                |      358 |
| Equipments          |      300 |
| Foliage             |      225 |
| Ships               |       80 |
| ConsumableContainer |       57 |
| PickupResource      |       51 |
| Food                |       41 |
| Crop                |       36 |
| Alchemy             |       12 |
| Fishing             |        8 |
| Ammo                |        1 |
| TMP/Test            |        5 |
| **Total**           | **~1589** |

LootTableType-Verteilung: 482 List, 266 Weight, 17 WeightedOnetime, 8 Ordered.

### Schema (überall identisch)

```json
{
  "$type": "R5BLLootParams",
  "LootTableId": { "TagName": "None" },
  "LootTableType": "Weight" | "List" | "WeightedOnetime" | "Ordered",
  "LootData": [
    {
      "Min": 1,
      "Max": 2,
      "Weight": 30,
      "LootItem": "/R5BusinessRules/InventoryItems/.../DA_DID_Resource_TeaLeaf_T04",
      "ItemAttributeModifiers": [],
      "LootTable": "None"
    },
    {
      "Min": 1, "Max": 1, "Weight": 0,
      "LootItem": "None",
      "LootTable": "/R5BusinessRules/LootTables/Mobs/Rss/DA_LT_Mob_BlackBeard_..."
    }
  ],
  "AssetBundleData": { "Bundles": [] },
  "NativeClass": "/Script/CoreUObject.Class'/Script/R5BusinessRules.R5BLLootParams'"
}
```

Pro `LootData[]`-Eintrag genau eines: entweder `LootItem` (konkretes Item) ODER
`LootTable` (Sub-LT-Reference, immer noch mit Min/Max für "wie oft wird die
Sub-Table gerollt"). Bei Weight-Tables gibt's typisch einen "no drop"-Eintrag
mit `Min=Max=0, LootItem="None"`.

### Was die Reference-Mod konkret macht

- 171 Files modifiziert, alle in `LootTables/Mobs/`.
- `Min` und `Max` jedes `LootData`-Eintrags ×2 (z.B. Tea Leaves `1-2 → 2-4`).
- Edge-Case: einige Sergeant/Sailor-Drops bekommen **zusätzliche LootData-Einträge**
  (z.B. neuer +1-5 Gunpowder-Eintrag im Sergeant-Drop). Diese Add-Operation ist
  out-of-scope für Phase 1 (wäre eine eigene "Add Custom Drop"-Mechanik im
  Editor — möglich, aber andere UI-Anforderung).

### Architektur-Lücken in der bestehenden Pipeline

| Komponente              | heute                                  | für Loot-Editing |
|-------------------------|----------------------------------------|------------------|
| `VanillaDumper`         | nur `InventoryItems` Prefix            | + `LootTables` Prefix |
| `WindroseGameSecrets`   | `InventoryItemsPath` const             | + `LootTablesPath` const |
| `StackPatcher`          | `MaxCountInSlot`-Regex                 | erweitert ODER zweiter Patcher (Decision unten) |
| `Profile`-Schema        | `globals.stackSize` + `overrides`      | + `globals.loot.byCategory[bucket]` + `lootOverrides[ltId]` |
| `BuildPipeline`         | applies stack-patcher, packs           | applies BOTH patchers, packs ein gemeinsames Pak |
| `GUI` Frontend          | eine Page (Profile + Items)            | Tabs/Pages: "Items" + "Loot Tables" |
| `/api/items`-Endpoint   | items only                             | + `/api/loot-tables` |

## Architektur-Entscheidungen (mit User abgestimmt)

| # | Entscheidung |
|---|--------------|
| 1 | **Top-Level Domains im selben Profile-File**, GUI mit Tabs. Ein Profil = ein Build-Pak. Stack-Size + Loot-Konfig teilen sich Profile-Identity. |
| 2 | **Globals = Multiplier auf Min+Max gekoppelt** (wie Reference-Mod). **Per-LT-Override = vollständiger Entry-CRUD** — pro Eintrag jedes Feld editierbar (Min, Max, Weight, LootItem, LootTable), zusätzlich Einträge entfernen und neue Einträge hinzufügen. |
| 3 | **Per-Kategorie Multiplier** (Mobs/Chests/Foliage/Crop/Equipments/...) plus Per-LT-Overrides. Kein Sub-Kategorie-Tree. |
| 4 | **VanillaDumper macht zwei `repak unpack -i ...`-Calls** in einer Run (InventoryItems + LootTables). `WindroseGameSecrets.LootTablesPath` als Konstante. |

> Reference-Pak `MoreEnemyResources_2x_P.pak` liegt im Repo-Root nur als Round-Trip-Sanity-Quelle für Phase 1.4. Sobald der Round-Trip-Test erfolgreich war, kann die Datei gelöscht werden (sie ist absichtlich nicht commitet, gehört nicht ins Repo).

## Profile-Schema-Erweiterung

```csharp
public sealed class Profile
{
    public string Id;
    public string Name;
    public string Description;
    public DateTimeOffset CreatedAt;
    public DateTimeOffset ModifiedAt;

    public ProfileGlobals Globals;
    public Dictionary<string, ItemOverride> Overrides;        // existing (item-zentrisch)
    public Dictionary<string, LootTableOverride> LootOverrides;   // NEW
}

public sealed class ProfileGlobals
{
    public StackSizeGlobal StackSize;
    public LootGlobal Loot;          // NEW; null = Loot-Domain inaktiv
}

public sealed class LootGlobal
{
    // Per-Kategorie Multiplier (Top-Level-Bucket-Name → Faktor).
    // null oder fehlend = Bucket bleibt vanilla. "*" als Wildcard
    // erlaubt einen Default für nicht aufgelistete Buckets.
    public Dictionary<string, double> ByCategory;
}

public sealed class LootTableOverride
{
    // Per LootData-Index (0-based) eine sparse Override-Spec.
    // Felder die null sind = vanilla-Wert bleibt, evtl. mit Multiplier
    // (gilt nur für Min/Max). Index, weil mehrere Einträge in einer LT
    // identische LootItem-Refs haben können (z.B. Weight-Slot + No-Drop-Slot).
    public Dictionary<int, LootEntryEdit> Entries;

    // Vanilla-Indices die in der Output-LT NICHT mehr erscheinen sollen.
    // Indices in `Entries` referenzieren weiterhin die VANILLA-Indices, nicht
    // die Post-Removal-Indices — der Patcher löst das beim Apply auf.
    public List<int> Removed;

    // Brand-neue Einträge die nach den (verbliebenen) Vanilla-Einträgen
    // angehängt werden. Vollständiges Schema, keine sparse-Felder, weil
    // ohne Vanilla-Basis nichts geerbt werden kann.
    public List<LootEntry> Added;
}

// Sparse: nur gesetzte Felder überschreiben Vanilla. null = unverändert.
public sealed class LootEntryEdit
{
    public int? Min;
    public int? Max;
    public int? Weight;
    // LootItem und LootTable: null = unverändert; ein Pfad = neu setzen;
    // Sentinel-String "None" = explizit auf "None" zurücksetzen (z.B. um
    // einen LootItem-Slot in einen Sub-Table-Slot umzuschreiben).
    public string LootItem;
    public string LootTable;
    // ItemAttributeModifiers bleiben in Phase 1 read-only/unverändert.
    // Schema-Slot offen für Phase 5+.
}

// Vollständiges Schema für neue Einträge (Added).
public sealed class LootEntry
{
    public int Min;
    public int Max;
    public int Weight;          // 0 für List-Tables, >0 für Weight-Tables
    public string LootItem;     // "None" oder ein Item-Pfad
    public string LootTable;    // "None" oder ein Sub-Table-Pfad
    // ItemAttributeModifiers wird beim Schreiben als [] emittiert.
}
```

### Beispiel-JSON auf Disk

```json
{
  "id": "ab12...",
  "name": "x2 Mob Drops + Custom Banana",
  "globals": {
    "stackSize": { "multiplier": 4 },
    "loot": {
      "byCategory": {
        "Mobs": 2.0,
        "Foliage": 1.5
      }
    }
  },
  "overrides": {
    "DA_CID_Food_Raw_Banana_T01": { "stackSize": 200 }
  },
  "lootOverrides": {
    "Mobs/DA_LT_Mob_BlackBeard_Sergeant_Final": {
      "entries": {
        "0": { "min": 2, "max": 2 },
        "4": { "min": 5, "max": 10, "weight": 50 }
      },
      "removed": [3],
      "added": [
        {
          "min": 1, "max": 5, "weight": 0,
          "lootItem": "/R5BusinessRules/InventoryItems/Ammo/DA_AID_Ammo_Gunpowder_Homemade_T02.DA_AID_Ammo_Gunpowder_Homemade_T02",
          "lootTable": "None"
        }
      ]
    }
  }
}
```

`lootOverrides`-Keys sind LT-Pfade relativ zu `LootTables/`, ohne `.json`-
Extension — eindeutig und lesbar (analog zu `itemId` für Items).

## Resolver-Reihenfolge pro LootTable

Pseudo-Algorithmus pro LT (nicht pro Entry — Removed/Added wirken auf Listenebene):

```
ovr = lootOverrides[ltId]                       // null wenn kein Override
mult = globals.loot.byCategory[bucket(ltId)]    // null = 1.0

result = []
for (i = 0; i < vanilla.LootData.Count; i++):
    if ovr?.Removed.Contains(i): continue        // Eintrag löschen

    edit = ovr?.Entries[i]                       // sparse edit oder null
    entry = vanilla.LootData[i].Clone()

    // Min/Max: Edit hat Vorrang vor Multiplier
    entry.Min = edit?.Min ?? round(entry.Min * mult)
    entry.Max = edit?.Max ?? round(entry.Max * mult)

    // Andere Felder: Edit überschreibt direkt, kein Multiplier
    if edit?.Weight    != null: entry.Weight    = edit.Weight
    if edit?.LootItem  != null: entry.LootItem  = edit.LootItem
    if edit?.LootTable != null: entry.LootTable = edit.LootTable

    result.Add(entry)

// Neue Einträge anhängen (verbatim, keine Multiplier-Anwendung)
foreach (e in ovr?.Added ?? []): result.Add(e)

if result equals vanilla.LootData (deep): SKIP — LT bleibt vanilla
else: write modified LT to build-tmp
```

Edge-Cases:

- `vanillaMin == vanillaMax == 0` (No-Drop-Slot in Weight-Tables): Multiplier
  greift technisch (0×n=0), bleibt also faktisch unverändert.
- `LootItem == "None" && LootTable != "None"` (Sub-Table-Reference): Multiplier
  greift wie überall, d.h. Sub-Table wird N-mal gerollt. Das ist gewollt: "alle
  Mob-Drops ×2" muss auch nested Tables doppelt rollen, sonst kompensiert die
  Mod sich selbst.
- Round-Half-Up oder Truncate? Empfehlung: `Math.Round(v, MidpointRounding.AwayFromZero)`
  damit `1 × 1.5 = 2` (intuitiv "anderthalb-mal" rundet auf), nicht 1.
- **Index-Drift bei Removed**: Indices in `Entries` und `Removed` referenzieren
  immer die **Vanilla-Indices** vor jedem Removal. Der Patcher iteriert vanilla
  vorwärts, skipped removed, und appended Added am Ende. So bleibt der Schema-
  Vertrag stabil auch wenn das Game später Vanilla-Einträge umsortiert.
- **Vanilla-Schema-Drift bei Game-Patches**: wenn nach einem Game-Update ein
  Vanilla-Index nicht mehr existiert (zu wenig Einträge in der LT), wird die
  betroffene Override-Edit gewarnt und übersprungen, statt zu crashen.

## Implementierungs-Phasen

### Phase 1 — Pipeline-Plumbing (kein UI)

| Step | Was |
|------|-----|
| 1.1 | `WindroseGameSecrets.LootTablesPath = "R5/Plugins/R5BusinessRules/Content/LootTables"` |
| 1.2 | `VanillaDumper.Run()` macht zwei `RunRepakUnpack`-Calls (InventoryItems + LootTables) statt einem; Statistik-Block summiert beide Bäume |
| 1.3 | Setup-Probe (`SetupRunner.Probe()`) prüft auch `Sources/Vanilla/.../LootTables/` ist nicht leer (sonst Re-Run nötig) |
| 1.4 | `Tools/QuartermasterCore/LootPatcher.cs` — neue Klasse, parallele Architektur zu `StackPatcher`. Liest `Sources/Vanilla/.../LootTables/**/*.json`, wendet Profile.Loot-Konfig an (Multiplier + Entries-Edits + Removed + Added), schreibt nur veränderte Files in den Build-Tmp-Ordner. Anders als `StackPatcher`, der mit Regex über JSON-Text fährt, parsed `LootPatcher` JSON via `System.Text.Json` und reserialisiert mit `WriteIndented = true` + Tabs (Vanilla-Format reproduzieren). Round-Trip-Tests: (a) Profile mit `byCategory: { Mobs: 2.0 }` und keinen Overrides muss byte-identisch zu `MoreEnemyResources_2x_P.pak` für die 169 reinen Multiplier-LTs produzieren. (b) Profile mit explizitem `added`-Eintrag für Sergeant muss exakt den Custom-Drop reproduzieren, den die Reference-Mod via Add-Op einfügt. |
| 1.5 | `BuildPipeline` ruft beide Patcher (Stack + Loot) sequenziell, beide schreiben in dasselbe `.build-tmp/<id>/`, dann ein `repak pack` |
| 1.6 | CLI-Smoke `dotnet run --project GUI -- --test-patcher --profile <profile-mit-loot>` |

### Phase 2 — Backend-Endpoints

| Endpoint | Was |
|----------|-----|
| `GET /api/loot-tables` | Liste aller LTs als Array: `{ id, category, type, entries: [{index, min, max, weight, lootItemId, lootTablePath}] }`. Items werden mit `Sources/Vanilla/InventoryItems/`-Daten gemerged, sodass `lootItemId` direkt im UI mit Icon + Name verlinkt werden kann. |
| Profile-CRUD | bestehend; Profile-Schema akzeptiert jetzt `lootOverrides` und `globals.loot` zusätzlich |
| `POST /api/build` | bestehend; BuildPipeline berücksichtigt Loot-Domain automatisch wenn im Profile gesetzt |

### Phase 3 — Frontend (Tabs)

| Bestehend | Neu |
|-----------|-----|
| `index.html` (Item-Configurator) | Tabs **Items** ↔ **Loot Tables** im Header |
| `app.js` rendert Item-Liste | erweitert um `renderLootTablesView()` |
| `app.css` | Tab-Styling |

**Loot Tables Page Layout:**

```
+------------------------------------------------------------+
| GLOBALS (Loot)                                             |
|  Mobs:    [2.0  ] x       [reset]                          |
|  Chests:  [1.5  ] x       [reset]                          |
|  Foliage: [     ] x (none)                                 |
|  ... (per Top-Level Bucket eine Zeile)                     |
+------------------------------------------------------------+
| Filter: [search...] [category v] [type v]    [ ] only-mod  |
+------------------------------------------------------------+
| v Mobs/DA_LT_Mob_BlackBeard_Sergeant_Final  (List, 5 entries)
|   [icon] BlackbeardSign  min[1] max[1] w[0]  vanilla 1-1   [edit][x]
|   [icon] Gunpowder_Hom.. min[1] max[5] w[0]  vanilla 1-5   [edit][x]
|   [icon] Guinea          min[1] max[1] w[0]  vanilla 1-1   [edit][x]
|   ...
|   [+ Add entry]   pick item / sub-table, set min/max/weight
| > Mobs/DA_LT_Mob_Brit_TeaLeaves  (Weight, 2 entries)
|   [icon] TeaLeaf         min[ ] max[ ] w[ ]  vanilla 1-2   [edit][x]
|   No-drop                min[ ] max[ ] w[ ]  vanilla 0-0   [edit][x]
+------------------------------------------------------------+
```

Pro Eintrag in der ausgeklappten LT:
- Item-Icon + Name (resolved aus `lootItem`-Pfad → `itemId` → `/Icons/<id>.png`,
  `/Icons/<id>.json` für Lokalisierung) ODER bei Sub-Table-Refs ein
  "Tabelle: Mobs/DA_LT_..."-Link
- Inline-Inputs für Min, Max, Weight (leer = vanilla bzw. multiplier-derived)
- `[edit]` öffnet einen Detail-Dialog wo auch `LootItem`/`LootTable` getauscht
  werden können (Item-Picker für LootItem, LT-Picker für LootTable, exklusiv)
- `[x]` markiert den Vanilla-Index in `removed` (visueller Strikethrough,
  Klick-noch-mal stellt wieder her)
- Computed Min-Max (Globals-Multiplier × Vanilla, falls nicht überschrieben)
  als kleines Sub-Label

Pro LT zusätzlich ein **`[+ Add entry]`**-Button, der einen neuen Eintrag in
`added` anlegt (initial mit `lootItem="None", lootTable="None", min=1, max=1, weight=0`,
visuell markiert als "added", User füllt aus).

Phase-1-Scope für die Add/Edit-UI: **Pflichtfelder Min/Max/Weight + LootItem
ODER LootTable** (genau eines). `ItemAttributeModifiers` wird beim Add immer
als `[]` emittiert, beim Edit unverändert mitgeschleppt.

### Phase 4 — Build-Validierung

| Test | Erwartung |
|------|-----------|
| Profile mit `byCategory: { Mobs: 2.0 }` ohne Overrides | Output-Pak strukturell identisch zur `MoreEnemyResources_2x_P.pak` für die 169 Mob-LTs (bis auf die Custom-Add-Drops, die unsere Mod nicht macht). Per-File-Diff zeigt nur Min/Max-Werte als Δ. |
| Combined Profile (Stack ×4 + Loot Mobs ×2) | Pak enthält geänderte InventoryItems UND LootTables-Files; Game lädt sauber, kein Crash beim ersten Mob-Kill. |
| Profile mit nur Per-LT-Overrides, ohne Globals | Pak enthält nur die paar geänderten LT-Files, alle anderen unverändert. |

## Doku- und Cleanup-Aufgaben

| Aufgabe | Wann |
|---------|------|
| README/DETAILS um Loot-Editing-Sektion erweitern | nach Phase 3 |
| Built-in Loot-Profiles? (z.B. `loot-mobs-2x.json`) | optional Phase 5 |
| Bestehende Stack-Builtins erweitern um leere `loot`-Domain? | NICHT nötig, null=inaktiv ist bereits gut |
| Description-Plan (`PLAN-DescriptionOverrides.md`) bleibt unberührt | parallel verfolgbar, hat eigene Domain |

## Out of Scope für Phase 1

- **`ItemAttributeModifiers`** editieren (für Equipments mit Stat-Rolls).
  Komplexes Sub-Schema (Modifier-Typen, Werte, Conditions) — separat planen
  wenn jemand das modden will. Für Phase 1: vanilla mitschleppen, beim Add neu
  immer als `[]`.
- **Komplette LT überschreiben mit eigener** als Top-Level-Mode (z.B. "diese
  LT komplett ersetzen, Vanilla ignorieren"). Geht funktional auch über
  `removed: [0..N-1] + added: [...]`, aber das wäre als expliziter "replace"-
  Mode UI-freundlicher. Phase 5+.
- **Sub-Table-Auflösung im Editor**: Der LT-Picker zeigt die Sub-Tables als
  Liste, aber kein In-Place-Drilldown ("expand inline und edit dort"). User
  muss die Sub-LT separat öffnen.
- **Globals-Erweiterung um Per-Kategorie-Min-Cap / Max-Cap**: nur Multiplier,
  keine Clamps. Sinnvoll später wenn jemand "alles ×10 aber höchstens 99"
  fahren will.

## Was JETZT die nächste Implementations-Action wäre

Phase 1.1–1.4 ist der kritische Pfad. Phase 1.4 ist der größte Posten und der
Round-Trip-Test gegen die Reference-Mod ist die Sanity-Probe, dass die
Multiplier-Mathematik stimmt. Sobald das byte-identisch (modulo Custom-Adds)
funktioniert, sind die anderen Phasen deutlich kleiner.
