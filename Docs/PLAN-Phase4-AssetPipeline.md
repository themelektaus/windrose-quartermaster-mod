# PLAN - Phase 4: Refactor, Asset-Pipeline, Multi-Item

Status: Phase B5 + Phase 3 stabil. QmBedrl injiziert ausschliesslich in Dekoration-Tab, Per-Inject-Spawn klappt ohne Crash, Pool waechst bis 11 Widgets ohne Probleme. Naechstes Ziel: aus dem Proof-of-Concept ein "richtiges" Mod-System machen.

## Endziel

Ein Mod-Autor kann:

1. In einem Unreal-Editor-Projekt eigene Mesh + Material + Icon erstellen.
2. Ein `DA_BI_*`-DataAsset auf Basis eines Vanilla-Items duplizieren (Collision, Verhalten erbt) und nur Mesh/Material/Icon/Name uebersteuern.
3. Das Ganze als Pak cooken und nach `R5/Content/Paks/~mods/` legen.
4. In einer Config-Datei den neuen Asset-Pfad eintragen + Ziel-Kategorie waehlen.
5. DLL deployen, Spiel starten - Item erscheint baubar in der gewuenschten Kategorie.

Aktuell nur Schritt 3 + 5 erledigt (hardcoded fuer 1 Item). Schritte 1, 2, 4 fehlen.

## Drei Workstreams (sequenziell)

### Workstream A: Code-Refactor (Code-Hygiene + Vorbereitung Multi-Item)

`main.cpp` ist auf 1752 Zeilen gewachsen und mischt 9 Verantwortlichkeiten. Vor jeder funktionalen Erweiterung wird aufgesplittet.

#### Geplante Datei-Struktur

| Datei | Inhalt (aus main.cpp extrahiert) | Lines geschaetzt |
|---|---|---|
| `main.cpp` | DllMain, WorkerThread, DXGI-Forwarders, Inject-Marker, Sleep-Test-Hook | ~250 |
| `qm_log.hpp` + `qm_log.cpp` (NEU) | Logger-Implementation aus main.cpp Section 2 in .cpp ziehen | ~110 + 80 |
| `qm_crash.hpp` + `qm_crash.cpp` (NEU) | Section 4b: VEH + UEF, Exception-Naming, State-Snapshot | ~150 |
| `qm_diag.hpp` + `qm_diag.cpp` (NEU, QM_DIAG-gated) | Section 6: alle DiagInspect* Funktionen, HexDump | ~400 |
| `qm_inject.hpp` + `qm_inject.cpp` (NEU) | Section 7: OverrideTarget, Spawn-Pool, ProbeGroupCategory, ClassifyTabPurity, InjectIntoGroup, CaptureOrInjectForeignItem | ~500 |
| `qm_hook.hpp` + `qm_hook.cpp` (NEU) | Section 8: Detour, InstallGetBuildingGroupsHook, UE_ProbePass, UeProbeThread | ~280 |
| `qm_config.hpp` + `qm_config.cpp` (NEU) | Multi-Item Config (siehe Workstream B) | ~120 |

#### Cleanup-Aufgaben waehrend des Refactors

- `MaybeRetryOverride` ist schon raus, aber Section-Header-Kommentare in main.cpp erwaehnen es noch.
- Doppelte Forward-Decls fuer Crash-Snapshot in main.cpp + qm_crash entfernen.
- `kBuildingItemsOffset = 0x350` ist als hardcoded Magic in `qm_inject` - mit Kommentar dass es aus Dumper-7 `UR5BuildingGroupWidget::BuildingItems` stammt.
- `g_groupsHookInstalled`, `g_origGetBuildingGroups` u.ae. werden bei TU-Split zu extern-Symbolen - in einem zentralen `qm_state.hpp` sammeln statt jeder TU eigene globals.
- VEH-Filter: aktuell loggt jede first-chance-Exception. Beim Boot kam einmal `READ at 0xFFFFFFFFFFFFFFFF` aus dem qm_scan - das sollte gefiltert werden (nur loggen wenn RIP NICHT in qm_scan-Range, oder kompletter Skip wenn `hits == 0` und Scan-Phase noch aktiv).

#### Akzeptanz-Kriterium Workstream A

- Build OK (dev + release), Groesse +/- 5 KB ggue. jetzt.
- Inject-Test im Game: gleicher Logoutput wie Commit `5c64ebc`, gleiche visuelle Wirkung (QmBedrl nur in Dekoration, baubar).
- Keine neue funktionale Veraenderung.

### Workstream B: Multi-Item Config

#### Ziel

Statt einer hardcoded Konstante `kOverridePackagePathW = "/Game/.../DA_BI_QmBedrl_01"` haelt der Code eine Liste von Items, die jeweils ein Donor + ein Override-Asset + eine Ziel-Kategorie haben.

#### Datenstruktur (`qm_config.hpp`)

```cpp
struct InjectableItem {
    // Wohin soll dieser Slot erscheinen? Path-Substring matched gegen Group->Items[0].Pkg.
    // Aktuell verfuegbar: "BuildingDecoration", "BuildingItems", "BuildingUtilities",
    // "BuildingCrafts", "BuildingFarming".
    const wchar_t* targetCategorySubstring;

    // Asset-Pfad in unserem Mod-Pak (FName + Package, beides Conv_StringToName-target).
    const wchar_t* packagePath;   // "/Game/Gameplay/Building/BuildingDecoration/DA_BI_QmBedrl_01"
    const wchar_t* assetName;     // "DA_BI_QmBedrl_01"

    // Aus welchem Vanilla-Item soll geclont werden? Ist OPTIONAL - wenn nullptr, nehmen
    // wir den 1. Item im aktuellen Tab als Donor (wie heute). Eintrag macht es deterministisch.
    // Format: PackageName-Substring fuer Discovery in Group.Items.
    const wchar_t* donorPackageHint;  // "DA_BI_Bedroll_01" - findet Bedroll im Decoration-Tab
};
```

#### Initial-Config (in `qm_config.cpp` als constexpr array)

```cpp
static const InjectableItem g_items[] = {
    {
        L"BuildingDecoration",
        L"/Game/Gameplay/Building/BuildingDecoration/DA_BI_QmBedrl_01",
        L"DA_BI_QmBedrl_01",
        L"DA_BI_Bedroll_01"   // clone vom Vanilla-Bedroll
    },
    // Spaeter: weitere Items hier eintragen, oder per .ini-File laden
};
```

#### Aenderungen am Inject-Pfad

| Heute | Multi-Item |
|---|---|
| 1 OverrideTarget global, einmalig resolved | Array von OverrideTargets, jeder Item-Eintrag bekommt seinen eigenen Lazy-Resolve |
| 1 Donor global (`g_donorItem`), captured im 1. Hit | Pro Item-Eintrag ein eigener Donor: bei jedem Tab-Hit Donor-Scan via `donorPackageHint` |
| `ClassifyTabPurity` returnt 1/0 fuer "alle Decoration" | `ClassifyTabPurity` returnt **welche Kategorien** in diesem Tab vertreten sind. Pro Item-Eintrag: matcht der Tab seine `targetCategorySubstring`? |
| Single-Shot: 1 Inject pro Hit | Pro passendem Item-Eintrag genau 1 Inject pro Hit. Im Dekoration-Tab werden also alle Decoration-Items injiziert. |

#### Akzeptanz-Kriterium Workstream B

- Mit 1 Item in der Config: gleiches Verhalten wie heute (QmBedrl nur in Dekoration).
- Mit 2 Items in der Config (Test-Setup: 2x QmBedrl mit unterschiedlichen Namen wenn noch kein 2. Asset existiert; oder spaeter 1 Bedroll + 1 Stool): beide erscheinen baubar im Dekoration-Tab, Pool wechst auf 2x Slot pro Dekoration-Visit.
- Bestehender Code-Pfad bleibt unter `kFallbackLegacySingleItem`-Flag aktivierbar fuer Rollback.

#### Spaetere Erweiterung (out of scope fuer Phase 4)

- `.ini`-File-Loader (`Quartermaster.ini` neben `dxgi.dll`): Items via `[Item.0001] PackagePath=... AssetName=... Donor=...` eintragbar ohne Recompile.
- Bei Bedarf Cache invalidieren wenn das File sich aendert (FileWatch in WorkerThread).

### Workstream C: Asset-Pipeline-Anleitung (Unreal Editor → Pak)

#### Voraussetzungen klaeren bevor Schritte funktionieren

- Welche UE-Version brauchen wir fuer das Mod-Projekt? Game ist UE 5.6.1 - also UE 5.6 von Epic Games Launcher. **Open question**: ist 5.6 schon stable in UE Editor verfuegbar? (zu checken)
- Brauchen wir die Game-Source-Files? Vermutlich nein, weil wir nur Vanilla-Datenklassen vom Container kopieren und nicht selbst kompilieren wollen.
- Brauchen wir die `usmap`? Im Editor-Workflow eher nicht (usmap ist Cooked-Reflection-Mapping fuer Tools wie CUE4Parse). Im Editor laeufts ueber den C++-Source.

#### Workflow (Skizze, muss verifiziert werden)

| Schritt | Was | Tool |
|---|---|---|
| **1. Editor-Projekt anlegen** | Leeres UE 5.6 Projekt "WindroseMod", Blueprint-only OK. Plugin-Pfad zu `Windrose/Content` als virtuelles Mount-Point. | Unreal Editor |
| **2. Vanilla-Asset importieren** | DA_BI_Bedroll_01 aus dem Game-Pak extrahieren (retoc unpack-raw aus `R5_5_6.utoc`) und ins Editor-Content-Verzeichnis legen. Das ist die Basis. | retoc + Editor |
| **3. Duplizieren** | `DA_BI_Bedroll_01` -> `DA_BI_QmBedrl_01`. Im DataAsset-Editor: Mesh, Material, Icon-Texture, DisplayName ueberschreiben. | Unreal Editor |
| **4. Mesh + Material + Icon importieren** | FBX/PNG via Drag-and-Drop, Texture als `T_QmBedrl_Icon`, Material als `M_QmBedrl`. Im DataAsset auf diese refs zeigen. | Unreal Editor |
| **5. Cook** | `UnrealEditor-Cmd.exe -run=Cook -Platform=Windows`. Output landet als `.uasset/.uexp/.umap/.ubulk` in `Saved/Cooked/Windows/`. | UE-CLI |
| **6. Zen-konvertieren** | `retoc to-zen` baut aus den Cooked-Files das `.pak/.ucas/.utoc`-Triple. UsMap aus Vanilla wiederverwenden. | retoc |
| **7. Deploy** | Triple nach `R5/Content/Paks/~mods/` kopieren. | manuell |
| **8. Config-Eintrag** | `qm_config` um den neuen Asset-Pfad ergaenzen + DLL re-deploy. | Code-Aenderung |

#### Knackpunkte die noch geklaert werden muessen

- **Asset-Inheritance**: aktuell vermuten wir dass das Vanilla-DA als Klasse `R5BuildingItem` Mesh/Collision/Verhalten haelt - duplizieren wir die Asset-Instanz oder die Klasse? Vermutlich Asset-Instanz (DataAsset = nur Werte, kein Verhalten). Verhalten kommt aus der C++-Klasse `R5BuildingItem`, die ohnehin im Game-EXE haengt. **Heisst: wir muessen die `R5BuildingItem`-Klasse im Editor verfuegbar haben.** Da wir keine Source haben, muesste das als kompilierte Plugin-DLL aus dem Game-Build her - was wir nicht haben.
- **Fallback wenn die Klasse fehlt**: das DataAsset duplizieren und als "leeren Generic-DataAsset" speichern, dann zur Runtime ueber unseren Override-Pfad die `Class` vom Donor uebernehmen (genau das machen wir heute via `donor->Class`). Dann braucht der Editor die R5-Klasse gar nicht zu kennen.
- **usmap-Korrektheit**: Cooked-Output muss mit der gleichen UE-Version + gleichen Engine-Aenderungen kompatibel sein wie das Game. Hier kann es subtile Inkompatibilitaeten geben.

#### Akzeptanz-Kriterium Workstream C

- Schritt-fuer-Schritt-Markdown in `Docs/Howto_AuthorBuildingItem.md` mit Screenshots aus jedem Tool.
- Verifiziert mit einem zweiten Test-Asset (z.B. neues Sitzkissen): import -> cook -> pack -> deploy -> Mod-Slot erscheint mit eigenem Mesh.
- Wenn UE5.6 Editor nicht direkt verfuegbar: Fallback-Strategie dokumentieren (UE 5.5 Editor + Cross-Version Workaround? Oder Wait-and-See).

## Reihenfolge

1. **Workstream A** zuerst. Refactor mit funktionaler Identitaet, dann ist die Code-Basis bereit fuer das was kommt.
2. **Workstream B** danach. Multi-Item ohne Editor-Asset funktionierts initial mit hardcoded `_02/_03`-Duplikaten von QmBedrl - das beweist die Config-Pipeline.
3. **Workstream C** zuletzt. Asset-Pipeline ist der grosse Investment und kommt nur dann zum Ende, wenn der Code es trivial konsumieren kann.

## Out of Scope fuer Phase 4

- Sparten-Granularitaet feiner als Tab (z.B. "Bedroll in Decorations -> Standing_Stool-Subgroup"). Heute landet QmBedrl in der ersten Decoration-Subgroup, Sortierung uebernimmt der Single-Shot. Falls Item per Sub-Group gewuenscht: spaeter ueber `GroupName`-Probe.
- CategoryTag-Resolution via FFrame-Bytecode-Walk. Die Tab-Purity-Heuristik reicht.
- VEH-Filter feintunen (nur ein einmaliger first-chance bei Boot, kein Spam, kein Blocker).
- Pool-Recycling bei UI-Close. Pool 16 reicht fuer mehrere Visits, Crash-frei verifiziert.

## Risiken + Mitigation

| Risiko | Mitigation |
|---|---|
| UE-Editor 5.6 nicht oeffentlich verfuegbar | Workstream C verschiebt sich. A + B sind davon unabhaengig - wir koennen Multi-Item mit `.uasset`-Byte-Patching (wie heute) liefern. |
| Refactor bricht funktionale Identitaet | Klein-schrittweise: Datei nach Datei, jeweils Build+Deploy+Manueller-Smoke-Test. Vor jedem Schritt commit. |
| Multi-Item ueberlastet Pool | Cap heben auf 64 + Recycle-on-UI-Close (Detour auf `UR5BuildingPanelWidget::NativeDestruct` falls vorhanden). |
| Eigenes Mesh hat fehlende Refs (Material/Texture nicht gefunden) | Asset-Verifikation mit `CUE4Parse` Dump nach jedem Cook, vor Deploy. |
