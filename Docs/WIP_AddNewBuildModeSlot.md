# WIP: Add new Build Mode slot

Status: **Phase B5 SUCCESS. Native UE5 DLL-Plugin baut neue Build-Items ohne AssetRegistry-Patching.** Quartermaster-Bedroll (`DA_BI_QmBedrl_01`) ist im Build-Menue platzierbar, hat eigenes Mesh/Icon und wird vom Spiel als regulaerer Slot behandelt. Discovery-Mauer aus Phase B1-B3 ist umgangen, nicht ueberwunden - wir injizieren nach dem nativen Filter zur Runtime.

Naechster Schritt: **Sparten-Targeting** - aktuell wird das Item in jede Build-Kategorie injiziert (Fanout). Soll nur in eine spezifische Sparte (z.B. Decorations) erscheinen.

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
| **Phase 1 - DLL-Bootstrap** | dxgi.dll Proxy + MinHook + Logging | `dxgi.dll`-Hijack in `R5/Binaries/Win64/`, Sleep-Hook als proof-of-life. Alle Vanilla-DXGI-Exports werden via `/EXPORT:foo=dxgi_org.foo` weitergereicht. |
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

1. **Capture** (einmalig auf erstem Hit): Donor = `Groups[0].Items[0]`. Source-Group merken.
2. **Spawn** (einmalig): WBP_Building_Item_C-Klasse aus `donor->Class`, SpawnObject UFunction ruft NewObject-Equivalent. ItemData via memcpy 0x30 Bytes vom Donor initialisiert.
3. **Override** (einmal pro Hit erneut versucht, bis appliziert): FNameFromString konstruiert die zwei FNames fuer `/Game/Gameplay/Building/BuildingDecoration/DA_BI_QmBedrl_01` und `DA_BI_QmBedrl_01`. Werden in `ItemData.PackageName/AssetName` geschrieben, WeakPtr genullt.
4. **Fanout** (jeder Hit, jede Group): Wenn Items[] der Group den Spawned-Pointer noch nicht enthaelt und `Num < Max`: append, `Num++`.

Resultat: in jeder Build-Kategorie taucht der QmBedrl-Slot zusaetzlich am Ende der ersten Group auf. Klick + Bauen funktioniert.

## Naechster Schritt: Sparten-Targeting

Aktuell injizieren wir in **jede** Kategorie ueber Fanout. Das ist gut fuer den Spike, aber falsch fuer ein finales Mod - Quartermaster-Bedroll soll nur in der **Decorations**-Sparte erscheinen, nicht in Walls/Floors/Roof/etc.

### Offene Fragen vor Implementation

| Frage | Wie pruefen |
|---|---|
| Welche Sparten gibt es? | Bereits im Log sichtbar - 5-6 verschiedene Categories pro Build-Session werden gehookt. CategoryTag-FName ist aber `<unresolved cmp=0 num=0>` weil wir den Stack-Slot fuer Param nicht zuverlaessig dekodiert haben. |
| Wie wird der CategoryTag uebergeben? | UFunction-Signatur: `GetBuildingGroupsByCategoryTag(FGameplayTag CategoryTag, UR5BuildingBrush* SelectedBrush, TArray<...>& ReturnValue)`. FGameplayTag ist 0x08 (eine FName). Wir lesen aktuell aus Stack+offset, aber das Offset stimmt nicht zuverlaessig. |
| Welchen Tag hat "Decorations"? | Wahrscheinlich `Building.Category.Decoration` oder aehnlich - im Dumper-Output unter `R5BuildingItem::CategoryTags` oder via Trial: einmal in jeder Kategorie ein anderes Item injizieren und Icon visuell zuordnen. |

### Implementations-Optionen

| Option | Ansatz | Aufwand |
|---|---|---|
| **A - CategoryTag-Read fixen** | UFunction-Param-Layout via Dumper-Output verifizieren, ExecFn nutzt FFrame mit `P_GET_STRUCT_REF` - Stack-Slot via Frame-Walk lesen. Bei jedem Hit den Tag-String dumpen, mit gewuenschtem Tag matchen. | Mittel (1-2h) - Frame-Layout neu pruefen |
| **B - Group-Identifizierung statt Tag** | Group-Widget hat vermutlich Properties die die Kategorie identifizieren (z.B. `CategoryTag` oder `GroupName`). Wenn wir die in Group-Widget lesen koennen, brauchen wir den Funktion-Param nicht. | Klein (~30min) - Class-Children-Walk auf WBP_Building_Group_C |
| **C - Asset-Driven Kategorisierung** | DA_BI_QmBedrl_01 hat selbst eine `CategoryTags` Property in R5BuildingItem. Wenn der Spawned-Widget seine SoftRef hydratet, fragt das Game die Tags ab und sortiert ihn moeglicherweise selbst in die richtige Sparte. Testbar: aktuell injizieren wir in ALLE Kategorien - wenn wir Source-Group skippen, ist QmBedrl in N-1 Kategorien sichtbar. Korrekte Loesung waere ihn in 1 Kategorie sichtbar zu haben. | Sehr klein (~15min) - nur Testaufwand |

Pragmatisch: **erst B testen** (Group-Properties), das beantwortet auch die Frage ob das Game den Slot via Asset-Tags selbst kategorisiert.

## Reference-Artefakte (Phase A + B Engineering-History)

| Datei | Zweck |
|---|---|
| `Tools/ar_writer/ar_patcher.py` | Strukturell korrekter AssetRegistry.bin-Append-Patcher. NameMap-Hash-Aware, FStore-Extension-aware. Letzter Stand: B3.1 (chunk_ids=[] Variante). Hat die Sackgasse bewiesen - in B5-Solution unbenutzt. |
| `Tools/ar_writer/ar_parser.py` | UE5.6 AssetRegistry.bin-Parser. Header / NameMap / FStore (text-first) / FAssetData. Verifiziert gegen R5 AssetRegistry.bin (Version 21). |
| `.build-tmp/b28-bedroll/` | B2.8 Build-Artefakte (Cannon-Icon Asset) - in B5 als Mod-Pak-Source weiterverwendet (`QmBedrl_P.{pak,ucas,utoc}` aktuell in `~mods/`) |
| `Docs/WIP_CsvLocalizationPatcher.md` | Verwandt: CSV-StringTable-Override-Mechanik |
| `Docs/WIP_StaticMeshReplacement.md` | Verwandt: Mesh-Reference-Patching im Zen-Chunk |

## Erkenntnisse fuer kuenftiges Plugin-Engineering

1. **UE5 IoStore + Hard-Filter im AssetManager** lassen sich nicht ueber Pak-Patching umgehen, nur ueber Runtime-Inject nach dem Filter.
2. **UFunctions sind der ergonomische Native-Hook-Pfad in UE5** - ExecFn-Pointer hat stabile Layout, Detour ist trivial mit MinHook.
3. **FName-from-String via Conv_StringToName** ist der Discovery-Bypass-Schluessel: konstruiert FNames ohne AR-Lookup, SoftRef hydratet direkt aus IoStore/PackageStore.
4. **SpawnObject UFunction** ist UE5's NewObject-Equivalent fuer DLL-Plugin-Code - keine Adress-Suche fuer `StaticConstructObject_Internal` noetig.
5. **Auto-Discovery der Offsets per Validation-Scan** ist Pflicht fuer Update-Resilienz. Dumper-7 ist nur als initial-bootstrap noetig.
6. **`vtable[0x4C]` ist ProcessEvent fuer UObject** in dieser UE5.6-Build-Variante - ergonomischer als Pattern-Scan im .text.
7. **Logging-Gating per `QM_BUILD_PRODUCTION`** spart 8 KB DLL-Groesse und macht Production-Builds ruhig.
