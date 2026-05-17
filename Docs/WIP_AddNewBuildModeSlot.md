# WIP: Add new Build Mode slot (A1-A3 -> B1-B3.3)

Status: **Add-new-slot BLOCKIERT. Wir sind in einem silent-skip-Failure-Modus den wir mit den aktuellen Tools nicht aufloesen koennen.** Override (A) ist gel?st und ein gesicherter Fallback. Naechster Approach: UE5-Source / retoc-Source studieren um zu verstehen wo der AssetManager unseren neuen Slot silent verwirft.

## Ziel

In der Kategorie "Dekorationen" des Build-Modes einen **zus?tzlichen** Slot anzeigen ohne einen Vanilla-Slot zu ersetzen. Klingt trivial, ist es nicht - UE5.6 IoStore mit AssetRegistry-Pinning macht Add-Operations ungleich riskanter als reine Overrides.

## Was funktioniert (Phase A - "Override existing slot")

| Iteration | Was gepatcht | Mechanik | Ergebnis |
|---|---|---|---|
| A1 | Round-Trip ohne ?nderung | `retoc unpack-raw` ? `retoc pack-raw` Bucket-Chunk | Vanilla-Eimer bleibt funktional. Pipeline intakt. |
| A2 | Mesh-Swap `SM_BucketWooden_01` ? `SM_BarrelWooden_01` | Inplace-byte-replace im Zen-Chunk (gleiche L?nge) | Eimer-Slot rendert Holz-Fass. |
| A3 | Mesh-Swap + Icon-Swap `T_BI_Bucket_01` ? `T_GoatMegaHead` + CSV-Name "Quartermaster Bucket" | Inplace im Zen-Chunk + Legacy-Pak mit BuildingItems.csv | Vollst?ndiger Override (Mesh, Icon, Display-Name). |

**Beweis:** Wir k?nnen jeden Vanilla-Decoration-Slot beliebig umgestalten. Das ist der gesicherte Fallback wenn Phase B endg?ltig scheitert.

## Was nicht funktioniert (Phase B - "Add new slot")

Vier Iterationen, alle mit dem gleichen Outcome im UI: **nur der Vanilla-Slot sichtbar, neuer Slot fehlt**. Jede Iteration hat aber eine konkrete H?rde abgebaut.

### B1 - Naiver utoc-Append

Bucket-Chunk geklont, FName + Pfad-Suffix `Bucket_01` ? `QmBuck_01` inplace ersetzt, Chunk-ID via CityHash64 manuell neu berechnet (`1d289a756966c9ef`), via `retoc pack-raw` mit chunk_paths-Manifest verpackt.

**Fail-Modus:** Container hat 0 packages, kein container_header. Discovery findet das Asset nicht weil `pack-raw` kein PackageStore schreibt - es schiebt nur Bytes durch.

### B2.1 - Mod-AssetRegistry-Probe

Vanilla `R5/AssetRegistry.bin` (17.7 MB) kopiert, inplace `DA_BI_Bucket_01` ? `DA_BI_BuckeX_01` (2 Occurrences), als `R5/AssetRegistry.bin` im Mod-Pak deployt.

**Wichtigste Erkenntnis:** Mod-AR wird gelesen ?, **additiv mit Vanilla-AR gemerged**, R5Check feuert 1x weil BuckeX nicht auf disk existiert. Vanilla-Bucket bleibt sichtbar.

### B2.2 - to-zen statt pack-raw

Bucket-Asset via `retoc to-legacy` extrahiert, FNames im Legacy-Asset umbenannt, mit `retoc to-zen --version UE5_6` zur?ck zu Zen konvertiert. to-zen schreibt komplettes utoc inkl. **container_header + PackageStoreEntry + korrekt berechnete CityHash64-Chunk-IDs**.

**Fail-Modus:** Container-mechanisch perfekt, aber AR enth?lt unseren Eintrag nicht ? AssetManager-Enumeration findet QmBuck nicht. Vanilla-Bucket bleibt sichtbar.

### B2.3 + B2.4 - utoc + AR Kombination

B2.2-utoc + B2.1-AR (mit `QmBuck_01` statt `BuckeX_01`) deployed. B2.3 hatte Substring-Kollision (`T_BI_Bucket_01` Icon-Ref versehentlich auch renamed). B2.4 fixt das via gezieltem 15-char-Prefix `DA_BI_Bucket_01`.

**Fail-Modus:** Logs zeigen `R5Check fires 1x` aber **kein** SkipPackage f?r QmBuck - das hei?t Asset wird gefunden, aber irgendwo schl?gt der Cast NULL zur?ck.

### B2.5 - Unlock-Hypothese-Test (Bedroll statt Bucket)

Vermutung: Eimer ist im Spielverlauf gated, Bedroll ist von Anfang an freigeschaltet. Pipeline identisch zu B2.4, nur f?r Bedroll-Asset.

**Fail-Modus:** Genau gleiche Logs wie B2.4. Unlock-Hypothese widerlegt. **AR-byte-rename allein reicht nicht.**

### B2.6 - Strukturell-korrekte AR (NameMap-Extend)

Forensik der B2.5-AR zeigt: Vanilla-Hash blieb intakt obwohl String renamed wurde. Game macht beim Resolve einen CityHash64-Lookup ? findet keinen passenden Hash ? NULL.

Eigener AR-Parser/Writer in Python gebaut (`tools/ar_writer/ar_patcher.py`). NameMap erweitert um 2 neue Eintr?ge mit korrekten CityHash64-Hashes (`DA_BI_QmBedrl_01` ? `0x5473de19c002c0bb`). Neuer AssetData-Record als Klon des vanilla Bedroll appended.

**Fail-Modus:** AssetManager-Log: `Found duplicate PrimaryAssetID R5BuildingItem:DA_BI_Bedroll_01` - unser Clone teilt die FStore-Tag-Pair-Range mit vanilla Bedroll inkl. dem `PrimaryAssetName`-Tag der noch auf vanilla zeigt.

### B2.7 - FStore-Extension

ar_patcher erweitert um NumberlessNames/NumberlessPairs-Re-Serialisierung. 1 neuer NumberlessName-Eintrag, 3 neue Pairs (NativeClass shared, PrimaryAssetName ? `DA_BI_QmBedrl_01`, PrimaryAssetType shared). Tag-Handle des Clones zeigt auf neuen PairBegin.

**Fail-Modus:** Duplicate-Warning verschwindet ?, aber **QmBedrl taucht 0x im Log auf**. Silent skip. Kein R5Check, kein duplicate warning, kein SkipPackage. Worst-of-all Failure-Pattern.

### B2.8 - Visual Distinction Test

Hypothese: Discovery + Loading klappt vielleicht silent, aber im UI ist der Slot unsichtbar weil er visuell identisch zum Vanilla-Bedroll ist (gleiches Mesh, gleiches Icon).

Asset-Chunk gepatcht: Icon-Ref `T_BedT01_01` -> `T_Cannon_01` (11 chars, inplace replace_all, 2 Occurrences). Mesh-Ref bleibt vanilla. AR + CSV bleiben wie B2.7 (von B2.6/B2.7 wiederverwendet).

**Fail-Modus:** Kein Slot mit Kanonen-Icon irgendwo im Build-Menu gefunden. Discovery scheitert wirklich silent - es ist KEIN Visualisierungs-Problem.

### B3.1 + B3.2 + B3.3 - Batched blind iteration

User entschied "blind alle drei batchen, einmal testen". Drei orthogonale Hypothesen parallel deployed:

**B3.1 - AR-Record-Subtle-Patch:** `chunk_ids=[]` (leer statt `[0]`). Hypothese: `[0]` zwingt Lookup im Default-Chunk der nur Vanilla-Inhalt hat.

**B3.2 - INI-Override mit SpecificAssets:** `Game.ini`-Override mit zusaetzlichem `+PrimaryAssetTypesToScan` der `SpecificAssets=(/Game/.../DA_BI_QmBedrl_01)` listet + `bShouldManagerDetermineTypeAndName=True`.

**B3.3 - Verbose-Logging:** `Engine.ini`-Override mit `LogAssetManager=Verbose`, `LogStreaming=Verbose`, `LogAssetRegistry=Verbose`, `LogR5BuildingItem=Verbose`, `LogIoStore=Verbose` etc.

**Deployed:**
- `BedrollB31AR_P.pak` - AR mit chunk_ids=[] und FStore-Extension
- `INIOverride_P.pak` - Game.ini mit SpecificAssets
- `VerboseLog_P.pak` - Engine.ini mit Verbose-Kategorien
- `BedrollB28_P.{pak,ucas,utoc}` + `BedrollB28CSV_P.pak` - unveraendert

**Fail-Modus:** Nur Vanilla Bedroll sichtbar. Aber das schlimmste: **Verbose-Logs greifen nicht.** Log enthaelt 0x `LogAssetManager`, 0x `LogR5BuildingItem`, 0x `QmBedrl`, 0x `PrimaryAsset` Mention. Andere Verbose-Kategorien (`R5LogDataKeeper: Verbose:`, `R5LogCoopProxy: Verbose:`) sind aktiv - unsere INI wird also gar nicht angewendet.

**Root cause (Hypothese):** UE5 mit IoStore liest Engine.ini SEHR FRUEH waehrend Engine-Init, bevor `~mods/`-Paks gemountet werden. Mod-Engine.ini kommt zu spaet an. Echter Engine.ini-Override braucht entweder:
- Direkt in `%LOCALAPPDATA%/R5/Saved/Config/Windows/Engine.ini` (User-Override, kommt vor Mod-Paks)
- Steam-Launch-Option `-LogCmds="LogAssetManager Verbose, LogAssetRegistry VeryVerbose"`

User-Entscheidung: Stop fuer heute, frisch morgen weiter mit neuem Ansatz.

## Reference-Artefakte

| Datei | Zweck |
|---|---|
| `tools/ar_writer/ar_patcher.py` | Strukturell korrekter AssetRegistry.bin-Append-Patcher. NameMap-Hash-Aware, FStore-Extension-aware. Letzter Stand: B3.1 (chunk_ids=[] Variante). |
| `tools/ar_writer/ar_parser.py` | UE5.6 AssetRegistry.bin-Parser. Header / NameMap / FStore (text-first) / FAssetData / DependsNode / PackageData. Verifiziert gegen R5 AssetRegistry.bin (Version 21). |
| `.build-tmp/b31-ar/` | B3.1 AR-Build mit chunk_ids=[] |
| `.build-tmp/b32-ini/` | B3.2 Game.ini-Override mit SpecificAssets |
| `.build-tmp/b33-verbose/` | B3.3 Engine.ini mit Verbose-Kategorien (greift NICHT, IoStore-Frueh-Init-Problem) |
| `.build-tmp/b28-bedroll/` | B2.8 Build-Artefakte (Cannon-Icon Asset) |
| `.build-tmp/b27-bedroll/` | B2.7 inkl. ar-stage mit Vanilla-AR-Kopie |
| `.build-tmp/b25-bedroll/zen-in/` | Legacy-extrahiertes QmBedrl-Asset (Source fuer alle Bedroll-Builds) |
| `Docs/WIP_CsvLocalizationPatcher.md` | Verwandt: CSV-StringTable-Override-Mechanik |
| `Docs/WIP_StaticMeshReplacement.md` | Verwandt: Mesh-Reference-Patching im Zen-Chunk |

## Was wir bisher gelernt haben (unabh?ngig von B2.8-Ergebnis)

1. **AssetManager merget Mod-AR additiv mit Vanilla-AR** - keine ?berschreibung, beide Listen werden iteriert.
2. **CityHash64(name.lower().utf8)** ist die FName-Hash-Funktion in UE5.6 AR.
3. **Cooked AR hat `NumDependsNodes=0` und `NumPackageData=0`** - massive Vereinfachung, weil wir keine Dependency-Graphs patchen m?ssen.
4. **PrimaryAssetID kommt aus FStore-Tags** (`PrimaryAssetName` + `PrimaryAssetType`), nicht aus AssetName. Patcht man die Tags nicht, kollidiert der Clone.
5. **retoc to-zen** schreibt korrektes utoc inkl. Container-Header + PackageStore. `retoc pack-raw` mit chunk_paths-Manifest schreibt nur Container, kein PackageStore.
6. **Asset-Pak alleine wird nicht enumeriert** - PackageStore in utoc reicht nicht, AR-Eintrag ist Pflicht.
7. **Substring-Kollisionen sind teuer** - `Bucket_01` ist Substring von `T_BI_Bucket_01`, `Bedroll_01` ist Substring von `FX_BI_Bedroll_01`. Immer mit dem l?ngsten eindeutigen Prefix umbenennen (`DA_BI_Bucket_01`).

## Naechste Schritte (Pickup-Plan fuer morgen)

### Schritt 1: Diagnose-Tooling reparieren (Pflicht vor weiterem Iterieren)

Ohne Logs sind alle Iterationen blind raten. Drei Wege Verbose-Output zu erzwingen:

1. **User-Engine.ini direkt:** `%LOCALAPPDATA%/R5/Saved/Config/Windows/Engine.ini` mit `[Core.Log]` Sektion editieren - wird VOR Mod-Paks geladen, weil aus User-Config-Pfad.
2. **Steam Launch-Options:** `-LogCmds="LogAssetManager Verbose, LogAssetRegistry VeryVerbose, LogStreaming Verbose, LogR5BuildingItem Verbose"`
3. **Game.ini Sanity:** Pruefen ob Game.ini-Mod-Override ueberhaupt greift (kein Hinweis im Log).

Erst wenn der naechste Test-Lauf `LogAssetManager: Verbose` Eintraege fuer QmBedrl produziert, sind weitere AR-Patches sinnvoll.

### Schritt 2: UE5-Source / retoc-Source Studium

Open questions die Source-Lesen beantwortet:

- **`UAssetManager::ScanPathsForPrimaryAssets`** - welche Filter-Schritte sind zwischen "AssetRegistry-Eintrag gefunden" und "Slot im UI sichtbar"? Was filtert silent?
- **`UAssetManager::GetPrimaryAssetIdForObject`** - genau wie wird die PrimaryAssetID aus FStore-Tags abgeleitet? Validiert es zusaetzliche Tags die wir nicht setzen?
- **R5-spezifisch:** Wo wird `R5BuildingItem` GetAllItems aufgerufen? Hat R5 einen zusaetzlichen Filter (Whitelist, Hash-Lookup gegen statischen Index) on top des Standard-AssetManagers?
- **retoc `to-zen`-Source:** Was schreibt `to-zen` ins PackageStore das `pack-raw` nicht schreibt? Gibt es einen statischen Hash der zur Cook-Time eingefroren wird?

### Schritt 3: Plan-B Pivot bereithalten

Wenn UE5-Source-Studium auch keine Aufloesung liefert: Override-only Pivot. Quartermaster bringt z.B. 20 thematische Custom-Decorations die jeweils einen langweiligen Vanilla-Slot ersetzen. A1-A3 Pipeline ist nachweislich solide. GUI + Asset-Library + IconBaker (existiert bereits in `tools/IconExtractor`) waeren der Mehrwert ohne weiteren Recon-Spike.

## Wichtige Erkenntnis fuer den Wiedereinstieg

**Wir sind nicht "nahe am Ziel".** Wir wissen NICHT was AssetManager mit unserem QmBedrl-Eintrag macht. Ohne Verbose-Logs ist jede weitere Iteration ein Schuss ins Blaue. Erst Tooling fixen, dann iterieren.

Stand `~mods/` beim Schluss heute: **komplett geleert** auf User-Wunsch. Frisch-Start morgen mit Vanilla-Windrose.
