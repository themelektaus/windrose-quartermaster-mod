# Plan: More Ring & Necklace Equipment Slots (slider + existing-character patch)

Status: **Umgesetzt** (Commit `feat(equipment-slots): ...`). Aufgegriffen aus
einer Reference-Mod ("More Rings and Necklace Slots", Nexus 350, by Baradrim)
plus dem User-Wunsch, bestehende Characters ohne externen Savegame-Patcher
abzudecken.

## Was die Reference-Mod macht

Genau **eine** Datei, zwei geaenderte Ints - kein IoStore, plain `_P.pak`:

- `R5/Plugins/R5BusinessRules/Content/Inventory/DA_PlayerInventoryParams.json`
- Jewelry-Modul (`Inventory.Module.Jewelry`) -> `Slots[]`:
  - `DA_BL_Slot_Equipment_Ring`     `CountSlots` 1 -> 4
  - `DA_BL_Slot_Equipment_Necklace` `CountSlots` 1 -> 2

Vanilla = tabs + CRLF. Es gibt 11x `"CountSlots": 1` in der Datei -> **immer per
SlotParams-Pfad ankern, nie per Array-Index**.

## Warum ein neuer Character noetig ist (WARNING.txt)

Windrose-Saves sind **RocksDB** unter
`%LOCALAPPDATA%\R5\Saved\SaveProfiles\<steamid>\RocksDB_v2\<version>\Players\<id>`.
Die Inventar-Struktur (Slot-Anzahl) wird **bei Char-Erstellung in den Save
gebacken** und beim Laden von dort wiederhergestellt; das Data-Asset ist nur die
Vorlage bei Erstellung. Die reine Pak-Mod wirkt daher nur bei neuen Characters.

## Slider (Pak, neue Characters)

`InventorySlotsPatcher` (Muster wie `BellLimitsPatcher`): Vanilla-JSON parsen,
Jewelry-Slot per SlotParams-Marker klassifizieren, `CountSlots` setzen, tabs+CRLF
via `R5Json.SerializeWithTabsAndCrlf` schreiben. Verdrahtung:
`Globals.EquipmentSlots {RingSlots, NecklaceSlots}`, Vanilla-Source-Dump
`playerInventory`, `WindrosePaths.VanillaPlayerInventory`, Pipeline-Invocation +
Build-Summary, Slider-Card "Equipment Slots" im Misc-Tab (1-3 / 1-3).

## Savegame-Patcher (bestehende Characters) - C#-Port

Vorlage: `DeveloperBlue/windrose-equipment-slots-patcher` (Python/rocksdict).
Portiert nach C# (`InventorySaveSlotsPatcher` + `CheckpointZipBuilder`).

### RocksDB-Mauer (entscheidend)

Das Spiel schreibt **`format_version=6`-SSTs mit rocksdb 10.4.2** (siehe
`OPTIONS-*` im DB-Ordner), NoCompression, Column-Families `default`,
`R5LargeObjects`, `R5BLPlayer`, `R5BLShip`, `R5BLBuilding`,
`R5BLActor_BuildingBlock`. Das einzige gepflegte .NET-Binding auf NuGet
(`RocksDbSharp` 6.2.2, native rocksdb 6.2.2 von 2019) kann `format_version=6`
**nicht** lesen. Loesung: Paket **`RocksDB` (Curiosity), Version 10.10.1.649** -
bundelt native librocksdb **10.10.1** (> 10.4.2), oeffnet die Save problemlos.
Native `rocksdb.dll` (win-x64) fliesst transitiv in den Web/App-Output.

### BSON-Chirurgie

Jeder Character ist ein BSON-Dokument in der `R5BLPlayer`-CF. Das Jewelry-Modul
hat **zwei** Sichten, die das Spiel beim Laden gegeneinander prueft:

- `ModuleParams.Slots` = Blueprint (`CountSlots` pro Slot-Typ)
- `Slots` = Live-Array (ein Eintrag pro physischem Slot: `SlotId`, `SlotParams`,
  `ItemsStack`)

Nur den Blueprint zu aendern reicht nicht - das Spiel sieht weniger Live-Slots
als der Blueprint behauptet und schreibt den Blueprint zurueck. Also:

1. Blueprint-`CountSlots` (Ring/Necklace) setzen (keine Groessenaenderung).
2. Live-Array neu bauen: leeren Slot der gleichen Art als Template klonen
   (nie einen gefuellten - das wuerde Items duplizieren), Element-Index-Namen
   und `SlotId` neu nummerieren, Reihenfolge Ring..Necklace..Backpack..Rest.
3. Splice ins Dokument + Delta auf **jeden** umschliessenden Sub-Doc-/Array-
   Groessenpraefix (`AncestorChain`) addieren. Invariante: `root size == len`.

Shrink mit ausgeruestetem Item in einem wegfallenden Slot -> blockiert, ausser
`force` (dann wird das Item verworfen; pre-patch Backup deckt das ab).

### Checkpoint-ZIP

Das Spiel restauriert die Live-DB bei jedem Laden aus
`RocksDB_v2_Backups/<type>/<id>/<id>_<version>_Latest.zip`. Nach dem DB-Write
muss dieses ZIP neu gebaut werden, sonst revertiert der naechste Start.
`CheckpointZipBuilder` portiert `checkpoint_zip.py`: SST -> `shared_checksum/`
mit `_s<session.identity>_<size>`, Blob mit `_<crc32c>_<size>`,
MANIFEST/CURRENT/OPTIONS/.log -> `private/1/`, `Checkpoint/meta/1` (crc32c je
Datei, erste zwei Sequenz-Zeilen + AdditionalRecordFiles aus dem alten ZIP
uebernommen).

### Sicherheit / Caveats

- Pre-patch Backup `<id>.value.pre-patch.bak` (nie ueberschrieben).
- Patch-Ordner muss unter dem echten SaveProfiles-Root liegen (kein beliebiger Pfad).
- **Steam Cloud Sync muss aus** sein, sonst ueberschreibt Steam die gepatchte
  Save beim Start (UI-Status weist darauf hin).
- Spiel muss vollstaendig geschlossen sein (sonst RocksDB-Lock).
- Der In-Game-Load ist headless **nicht** testbar - der Algorithmus spiegelt das
  bewaehrte Python-Tool; der User verifiziert einmal im Spiel.

## Reproduzierbare Recon

```bash
KEY=0x5F43...CFAE
PAKS="E:/Games/steamapps/common/Windrose/R5/Content/Paks"
# Vanilla-Asset
./repak.exe -a $KEY get "$PAKS/pakchunk0-Windows.pak" \
  "R5/Plugins/R5BusinessRules/Content/Inventory/DA_PlayerInventoryParams.json" > van.json
# Mod-Asset (zum Vergleich)
./repak.exe get "References/More Rings and Necklace Slots/2Necklace_4Ring_P.pak" \
  "R5/Plugins/R5BusinessRules/Content/Inventory/DA_PlayerInventoryParams.json" > mod.json
# Save-Format pruefen
cat "$LOCALAPPDATA/R5/Saved/SaveProfiles/<steamid>/RocksDB_v2/<ver>/Players/<id>/OPTIONS-"* | grep -iE "rocksdb_version|format_version"
```

## Verifikation (umgesetzt)

- Pak: Ring 3 / Necklace 2 -> Diff zur Vanilla = exakt die zwei `CountSlots`-
  Zeilen, tabs+CRLF identisch; Vanilla 1/1 -> Skipped.
- Save: auf einer **Kopie** der echten Save (Char "Testinger"): 1/1 -> 3/2
  persistiert ueber DB-Reopen, Blueprint + Live beide aktualisiert, Checkpoint-
  ZIP neu gebaut (meta-Sequenznummern erhalten), Backup geschrieben, erneutes
  3/2 = No-op. RocksDB open/read/write + BSON-Splice vorab per Spike gegen die
  echte Save bewiesen.

## Out of Scope

- Andere Equipment-Slots (Head/Torso/etc.) - dieselbe Technik, aber kein
  konkreter Bedarf.
- Die "+g" Hand-Slot-Variante des Python-Tools (zwei Handschuhe).
