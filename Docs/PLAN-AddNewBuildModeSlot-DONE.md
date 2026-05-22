# WIP: Add new Build Mode slot

Status: **ERLEDIGT + erweitert.** Die DLL-Plugin-Loesung aus Phase B5 ist live und wurde danach in ein vollwertiges GUI-getriebenes Building-Authoring-System ausgebaut. Per Quartermaster-GUI kann der User beliebig viele eigene Buildings anlegen (Naming-Schema `QmBldg_<8hex>`), bekommt sie nach dem Build-Knopf-Druck im Build-Menue der gewuenschten Sparte angezeigt und kann sie ingame platzieren - inkl. eigenem Mesh/Icon/Recipe/Snap-Verhalten und (optional) Flame-Preset.

Naechster Schritt: **Sparten-Targeting** ist seit B5+ implementiert. Der Inject-Mechanismus filtert pro Item ueber `targetCategorySubstring` (Inject nur in Groups deren erstes Item den angegebenen Substring im Asset-Path hat - default `"BuildingDecoration"`), und global ueber `tabPurityFilter` (Hook ignoriert Aufrufe deren Result nicht in dem konfigurierten Build-Tab landet). Beides wird beim Build vom GUI in `qm_items.json` neben der DLL geschrieben (siehe `GameDeployer.cs::WriteItemsJson()`). Quartermaster-Bedroll war der Spike-Build, heute laeuft das System fuer beliebige `QmBldg_<hash>`-Buildings.

Das Doc steht als Engineering-History stehen - die Architektur-Entscheidungen (Native-Hook statt Pak-Patching, FName-from-String fuer Discovery-Bypass, Auto-Discovery der Offsets) sind weiterhin gueltig und der Basis-Code lebt unveraendert.

## Ziel

In einer bestimmten Sparte des Build-Modes einen **zusaetzlichen** Slot anzeigen ohne einen Vanilla-Slot zu ersetzen. UE5.6 IoStore + AssetRegistry-Pinning macht das ueber Pak-Patching unmoeglich (Phase B1-B3 ausgiebig verifiziert). Loesung: native Runtime-Injection via DLL-Hook.

## Was funktioniert

### Phase A - Override existing slot (gesicherter Fallback)

| Iteration | Was gepatcht | Mechanik | Ergebnis |
|---|---|---|---|
| A1 | Round-Trip ohne Aenderung | `retoc unpack-raw` -> `retoc pack-raw` Bucket-Chunk | Vanilla-Eimer bleibt funktional. Pipeline intakt. |
| A2 | Mesh-Swap `SM_BucketWooden_01` -> `SM_BarrelWooden_01` | Inplace-byte-replace im Zen-Chunk (gleiche Laenge) | Eimer-Slot rendert Holz-Fass. |
| A3 | Mesh-Swap + Icon-Swap `T_BI_Bucket_01` -> `T_GoatMegaHead` + CSV-Name "Quartermaster Bucket" | Inplace im Zen-Chunk + Legacy-Pak mit BuildingItems.csv | Vollstaendiger Override (Mesh, Icon, Display-Name). |

Beweis: jeder Vanilla-Slot ist beliebig umgestaltbar. Gesicherter Fallback wenn Phase B5 brechen sollte.

### Phase B5 - Native UE5 DLL-Plugin (Durchbruch)

Loesung des Discovery-Problems durch Runtime-Hook in `UR5BuildingPanelWidget::GetBuildingGroupsByCategoryTag`. Wir warten bis UE5 die Vanilla-Items in die Items-TArray gepackt hat, dann injizieren wir unseren eigenen UR5BuildingItemWidget mit modifiziertem SoftPath. Pak-Daten werden lazy via IoStore aus dem Mod-Pak hydratet - der AssetManager-Filter wird komplett umgangen.

| Sub-Phase | Was bewiesen | Mechanik |
|---|---|---|
| **Phase 1 - DLL-Bootstrap** | dxgi.dll Proxy + MinHook + Logging | `dxgi.dll`-Hijack in `R5/Binaries/Win64/`, Sleep-Hook als proof-of-life. Alle Vanilla-DXGI-Exports werden via `/EXPORT:foo=dxgi_original.foo` weitergereicht. |
| **Phase 2a - UFunction-Hook** | UE5-Reflection erreichbar, Detour fired | GObjects-Walk findet `UR5BuildingPanelWidget`, dessen `GetBuildingGroupsByCategoryTag` UFunction hat einen native ExecFn-Pointer. MinHook detoured ExecFn auf unseren Trampoline. Original-Funktion wird forwarded, dann inspizieren wir Stack/Result. |
| **Phase 2b.1 - Result-Inspection** | TArray-Layout korrekt gelesen | `Groups[]` ist `TArray<UR5BuildingGroupWidget*>` (Header @ Stack+0x10), jede Group hat `Items[]` @ +0x350. Layout-Annahme byte-perfekt verifiziert. |
| **Phase 2b.2 - Group-Spoof (Pivot)** | Group-Pointer-Append wird von UMG dedupliziert | Erstes Group[0]-Append landet im TArray aber UMG zeichnet zwei gleiche Group-Pointer nur einmal. Beweist: Pointer-Inject auf Group-Ebene ist falsche Abstraktion. |
| **Phase 2b.2-redux - Item-Spoof** | Item-Pointer-Append rendert visuell als zusaetzlicher Slot | Eine Ebene tiefer: `Groups[0].Items[N]` = `Groups[0].Items[0]`, `Items.Num++`. UMG rendert tatsaechlich einen zweiten sichtbaren Slot fuer denselben Item-Pointer. Discovery-Layer erfolgreich umgangen. |
| **Phase 2b.3a - SoftPath-Recon** | ItemData-Layout vollstaendig dekodiert | `UR5BuildingItemWidget @ 0x340` haelt `FR5BuildingItemRuntimeData` (struct, 0x30 bytes): `TSoftObjectPtr` (WeakPtr 0x00, PackageName-FName 0x08, AssetName-FName 0x10, SubPath FUtf8String 0x18) + 3 bools. Read-Only-Walk dumpt PackageName/AssetName fuer 3 Items pro Group. |
| **Phase 2b.3b-Lite - Foreign-Item-Inject** | Items sind cross-group renderbar **und baubar** | Donor-Pointer (z.B. Building-Center aus Utilities-Group) in fremde Groups appended. UI rendert, Klick funktioniert, Bauen geht. Beweist: kein Owner-Check, kein Group-Membership-Filter. Persistent-Inject (jeder Hit re-injiziert) loest Kategorie-Wechsel-Verschwinden. |
| **Phase 2b.3-c2a - Eigener Widget-Spawn** | NewObject-aequivalent via UFunction | `UGameplayStatics::SpawnObject(UClass*, UObject*)` ist eine UFunction. CDO von GameplayStatics via `Class->ClassDefaultObject @ 0x0110`, ProcessEvent ruft SpawnObject mit `{ObjectClass=donor->Class, Outer=donor->Outer}`. ItemData wird via memcpy 0x30 Bytes vom Donor initialisiert. |
| **Phase 2b.3-c2b - SoftPath-Override via GObjects-Lookup (failed)** | Discovery-Mauer doch noch in der Schleife | Override-Code suchte `R5BuildingItem::DA_BI_QmBedrl_01` per GObjects-Walk. Asset war nie geladen weil keine Vanilla-Referenz auf unser Mod-Asset zeigt - Phase-B-Mauer schlaegt von der Asset-Seite zu. |
| **Phase 2b.3-c2c - FName-from-String (Durchbruch)** | Asset wird lazy via IoStore hydratet, AssetRegistry komplett umgangen | `UKismetStringLibrary::Conv_StringToName` ist eine UFunction die einen FString in eine FName umwandelt. Pkg- und Asset-FName werden zur Runtime aus hardcoded Strings konstruiert (`/Game/Gameplay/Building/BuildingDecoration/DA_BI_QmBedrl_01`), in `ItemData.PackageName/AssetName` geschrieben, WeakPtr genullt. Beim naechsten Render resolviert UE5 den SoftRef direkt aus dem PackageStore/IoStore und findet unser Mod-Pak. **AssetManager-Filter wird nie aufgerufen.** |

### Phase 3 - Auto-Discovery der Offsets (Steam-Update-Resilienz)

| Komponente | Strategie |
|---|---|
| **GObjects** | Validation-based Scan auf `.data`-Sections: walke 8-aligned, validiere jedes Candidate-Layout (MaxElements 0x10000-0x600000, NumChunks 1-100, Chunk-Ptrs deref-bar, erste 16 UObjects haben gueltige UClass). |
| **ProcessEvent** | vtable[0x4C] des ersten UObjects in GObjects - keine Pattern-Suche noetig. |
| **AppendString** | Hardcoded Offset + Smoke-Test (Function-Prologue-Pattern + Executable-Section-Check). Wenn ein Steam-Update bricht, ist die Log-Zeile loud genug. |
| **Fallback** | Hardcoded Offsets als safety net + Rescan in jeder Init-Iteration solange GObjects empty. Steam-Update soll silent ueberlebt werden, sobald Game weit genug initialisiert ist. |

Auto-Discovery wurde direkt nach einem realen Steam-Update um 09:52 entwickelt, das alle 4 Offsets verschoben hat. Re-Dump war manuell, danach Auto-Discovery eingebaut um den naechsten Update-Schock zu absorbieren.

## Was nicht funktioniert hat (Phase B1-B3 - Engineering-History)

Drei Wochen Pak-Patching-Iterationen, alle mit silent-skip-Failure. Hier nur Kurzform zur Referenz - Detail siehe Git-History bis Commit `c8226fa`.

| Iteration | Ansatz | Fail-Modus |
|---|---|---|
| B1 | Naiver utoc-Append via `retoc pack-raw` | Container hat 0 packages, kein container_header. Discovery findet Asset nicht. |
| B2.1 | Mod-AssetRegistry-Probe (inplace-byte-rename) | AR wird additiv mit Vanilla gemerged. Aber AR-Eintrag allein reicht nicht. |
| B2.2 | `retoc to-zen` schreibt korrekten Container | Container-mechanisch perfekt, aber Asset im AR fehlt - silent skip. |
| B2.3-B2.4 | utoc + AR Kombination, Substring-Kollision gefixt | R5Check fires 1x, kein SkipPackage - Cast NULL silent. |
| B2.5 | Bedroll statt Bucket (Unlock-Hypothese-Test) | Identisches Fail - AR-byte-rename allein reicht nicht. |
| B2.6 | Strukturell korrekte AR (NameMap-Extend, CityHash64-aware) | "Found duplicate PrimaryAssetID" - FStore-Tags zeigen weiter auf Vanilla. |
| B2.7 | FStore-Extension (NumberlessNames/Pairs re-serialisiert) | Duplicate-Warning weg, aber QmBedrl taucht 0x im Log auf. Silent skip. |
| B2.8 | Visual-Distinction-Test (Cannon-Icon) | Kein Cannon-Icon im UI sichtbar. Discovery ist es, nicht Render. |
| B3.1 | `chunk_ids=[]` Variante | Identisch silent skip. |
| B3.2 | INI-Override mit `SpecificAssets` PrimaryAssetType | Greift nicht. |
| B3.3 | Verbose-Logs erzwingen | Mod-Engine.ini wird zu spaet gelesen (IoStore-Frueh-Init). |

**Eliminierte Hypothesen via Recon C:** Kein zentrales Index-Asset (`DA_BuildingUICategories`, `DA_BuildList_*`, Recipe-Discovery, UI-Widget-Whitelist, PrimaryAssetLabel) - Discovery laeuft ausschliesslich ueber `UAssetManager::ScanPathsForPrimaryAssets` mit einem nativen Filter den wir nicht inspizieren konnten.

Konsequenz: **AR-Patching war eine Sackgasse.** Pak-Layer wird vom Filter ignoriert, Runtime-Inject ist der einzige Weg.

## Komponenten der B5-Loesung (in `Tools/DllProxy/dxgi/`)

| Datei | Inhalt |
|---|---|
| `main.cpp` | DXGI-Forwarders, Logging, MinHook-Bootstrap, UE-Probe-Loop, Hook-Detour, Inject-Pipeline (Capture/Spawn/Override/Fanout) |
| `qm_log.hpp` | `QM_LOG_ERROR/WARN/INFO/DEBUG/TRACE` Macros, compile-time-gated. `QM_BUILD_PRODUCTION` schaltet DEBUG/TRACE + DIAG-Code raus. |
| `qm_ue.hpp/cpp` | UE5-Reflection: GObjects-Access, FName-Resolve via AppendString, UClass/UFunction-Lookup, ProcessEvent-Wrapper, SpawnObject-via-UFunction, FNameFromString-via-Conv_StringToName |
| `qm_scan.hpp/cpp` | Validation-based Scan: GObjects, ProcessEvent (vtable[0x4C]), AppendString-Smoke-Test |
| `build.bat [release]` | MSVC-Build, optional `release` -> Production-Build mit `QM_BUILD_PRODUCTION`. Dev: 189 KB, Production: 181 KB. |
| `deploy.bat` | Kopiert `dxgi.dll` nach `R5/Binaries/Win64/` |
| `uninstall.bat` | Entfernt `dxgi.dll` |
| `minhook/` (submodule) | TsudaKageyu/minhook @ 05c06c5 |

`Tools/Dumper7/` Submodule + `Tools/Dumper7Setup/` (run_dump.bat + inject) liefern die initialen Offsets fuer GObjects, AppendString, GWorld, ProcessEvent (in `qm_ue.hpp` als Fallback hardcoded, fuer Auto-Discovery-Vergleich).

## Aktueller Stand der Inject-Pipeline

Jeder Hit auf `GetBuildingGroupsByCategoryTag`:

1. **Tab-Purity-Gate** (`tabPurityFilter` aus `qm_items.json`): Wenn das erste Item des Results den Filter-Substring nicht im Package-Path traegt, return ohne Mutation. Verhindert Inject z.B. in Building-Brushes-Tabs.
2. **Capture** (einmalig auf erstem passenden Hit): Donor = `Groups[0].Items[0]`. Source-Group merken. Class-Substring-Check verhindert Capture eines falschen Item-Types.
3. **Spawn** pro konfiguriertem Item (einmalig): WBP_Building_Item_C-Klasse aus `donor->Class`, SpawnObject UFunction ruft NewObject-Equivalent. ItemData via memcpy 0x30 Bytes vom Donor initialisiert.
4. **Override** pro Item (einmal pro Hit erneut versucht, bis appliziert): FNameFromString konstruiert PackageName/AssetName aus den `packagePath`/`assetName`-Strings des Items, WeakPtr genullt. Engine resolved beim naechsten Render aus IoStore/PackageStore.
5. **Per-Item-Target-Filter**: Pro Group wird `targetCategorySubstring` gegen `Groups[i].Items[0]`'s Package-Path geprueft. Match -> Append des Spawned-Pointers; Miss -> skip.

Resultat: Jedes in `qm_items.json` gelistete Item taucht nur in den Build-Kategorien auf wo der `targetCategorySubstring` zutrifft (default `"BuildingDecoration"`), und nur in den Build-Tabs wo der globale `tabPurityFilter` durchlaesst.

## Wie das GUI das heute fuettert

Beim Build-Knopf-Druck (siehe `BuildPipeline.cs`):

1. Pro Custom-Building patcht die GUI ein eigenes DataAsset (`DA_BI_QmBldg_<hash>`) + Mesh + Materials + (optional) Blueprint-Clone in einen `Quartermaster_<profile>_P.{pak,ucas,utoc}`.
2. `GameDeployer.cs` schreibt `qm_items.json` neben die `dxgi.dll` mit einem Eintrag pro Custom-Building - jedes Item bekommt seinen `targetCategorySubstring` zugewiesen (typischerweise `"BuildingDecoration"`, kann pro Building variieren wenn andere Sparte gewuenscht ist).
3. `GameDeployer` deployed bei Bedarf die DLL selbst (`dxgi.dll` + `dxgi_original.dll`) nach `R5/Binaries/Win64/`.
4. DLL liest `qm_items.json` bei Game-Start, injiziert alle Items in die jeweils gewuenschte Sparte.

Heisst: Adding eines neuen Buildings ist ein reiner GUI-Vorgang, kein DLL-Rebuild, kein manuelles JSON-Editieren.

## Erweiterungen seit B5 (nicht in diesem Doc bisher dokumentiert)

- **Flame-Presets** (`FlamePresetCatalog.cs` + Phase 2 socket-driven placement): Buildings koennen einen Flame-Preset (z.B. `torch`) tragen, der dem Building eine NiagaraComponent + PointLight + Audio aus dem vanilla `BP_BuildingBlock_FloorTorch_C` mitgibt. Position/Rotation/Scale folgen einem im User-Mesh definierten Socket. Siehe `Docs/HowTo-AuthorBuildingItem.md` Sektion "Socket (optional, fuer Flame-Preset)".
- **Per-Building BP-Clone**: Jedes Flame-Building bekommt seinen eigenen `BP_QmFlaming_<BuildingId>` BP-Clone mit NameMap-Rewrite (damit das User-Mesh statt vanilla SM_TorchT01_01 gerendert wird).
- **CSV-Loca-Patching**: `BuildingItemsCsvPatcher.cs` schreibt pro Building eine `BuildingItems.csv`-Row mit User-Display-Name + Description. FText-Keys werden via `RewriteInlineFTextKeys` in den DA-Bytes auf per-Building-Keys umgebogen (`Decorations_FloorTorch_Name` -> `QmBldg_<hash>_Name`).

## Reference-Artefakte (Phase A + B Engineering-History)

| Datei | Zweck |
|---|---|
| `Tools/ar_writer/ar_patcher.py` | Strukturell korrekter AssetRegistry.bin-Append-Patcher. NameMap-Hash-Aware, FStore-Extension-aware. Letzter Stand: B3.1 (chunk_ids=[] Variante). Hat die Sackgasse bewiesen - in B5-Solution unbenutzt. |
| `Tools/ar_writer/ar_parser.py` | UE5.6 AssetRegistry.bin-Parser. Header / NameMap / FStore (text-first) / FAssetData. Verifiziert gegen R5 AssetRegistry.bin (Version 21). |
| `.build-tmp/b28-bedroll/` | B2.8 Build-Artefakte (Cannon-Icon Asset) - in B5 als Mod-Pak-Source weiterverwendet (`QmBedrl_P.{pak,ucas,utoc}` aktuell in `~mods/`) |
| `Docs/PLAN-CsvLocalizationPatcher-WIP.md` | Verwandt: CSV-StringTable-Override-Mechanik |
| `Docs/PLAN-StaticMeshReplacement-WIP.md` | Verwandt: Mesh-Reference-Patching im Zen-Chunk |

## Erkenntnisse fuer kuenftiges Plugin-Engineering

1. **UE5 IoStore + Hard-Filter im AssetManager** lassen sich nicht ueber Pak-Patching umgehen, nur ueber Runtime-Inject nach dem Filter.
2. **UFunctions sind der ergonomische Native-Hook-Pfad in UE5** - ExecFn-Pointer hat stabile Layout, Detour ist trivial mit MinHook.
3. **FName-from-String via Conv_StringToName** ist der Discovery-Bypass-Schluessel: konstruiert FNames ohne AR-Lookup, SoftRef hydratet direkt aus IoStore/PackageStore.
4. **SpawnObject UFunction** ist UE5's NewObject-Equivalent fuer DLL-Plugin-Code - keine Adress-Suche fuer `StaticConstructObject_Internal` noetig.
5. **Auto-Discovery der Offsets per Validation-Scan** ist Pflicht fuer Update-Resilienz. Dumper-7 ist nur als initial-bootstrap noetig.
6. **`vtable[0x4C]` ist ProcessEvent fuer UObject** in dieser UE5.6-Build-Variante - ergonomischer als Pattern-Scan im .text.
7. **Logging-Gating per `QM_BUILD_PRODUCTION`** spart 8 KB DLL-Groesse und macht Production-Builds ruhig.
