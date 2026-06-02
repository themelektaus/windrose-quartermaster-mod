# Ship Slots (Expanded Naval Tactics) - DONE

Slider-driven port of the **Expanded Naval Tactics** reference mod
(`References/Expanded Naval Tactics/`, Nexus mod 348), plus a savegame patcher
for EXISTING ships (the reference mod has no save patcher).

## What the reference mod changes

Plain pak (`ExpandedNavalTactics_MoreSlots_5_P.pak`), 12 R5BusinessRules JSON
files - `DA_ShipInventory_{Brig,Frigate,Ketch}{,_Stock,_Blackbeard,_Brethren}`.
Each file changes exactly 2 numbers:

| Slot | Path | Change |
|---|---|---|
| **Cargo** | `Inventory.Module.Default` -> `DA_BL_Slot_Chest` CountSlots | **doubled** (per ship: 20->40, 28->56, 52->104, ...) |
| **Combat Orders** | `Inventory.Module.Equipment` -> `DA_BL_Slot_ShipEquipment_CombatOrders` CountSlots | **1 -> 5** |

Cutter (cargo 50, combat 1), Merchant (cargo 50), Boat and the
`DA_InventoryShipDefault` template are deliberately left at vanilla. Our scope
matches the mod exactly via a Brig/Frigate/Ketch filename allowlist.

## Pak side (new ships) - `ShipSlotsPatcher`

Two profile knobs (`ShipSlotsGlobal`):
- `CargoMultiplier` (1.0-3.0, null/1.0 = vanilla): cargo = `round(vanillaBase * mult)`,
  away-from-zero, never below vanilla, capped at 200 cells. The mod's x2 is just
  multiplier 2.0.
- `CombatOrderSlots` (1-10, null/1 = vanilla): absolute count.

Reuses the dumped vanilla `Inventory/Ship` tree (already extracted by the
existing `playerInventory` manifest entry - the include prefix covers the whole
`Inventory/` subtree). Writes tabs+CRLF via `R5Json`. Wired into `BuildPipeline`
(field, ctor, invocation, `BuildPipelineResults.ShipSlotsResult`,
`HasShipSlotsConfiguration`, `BuildEndpoint` surfacing).

**Verified:** at `x2 / combat 5` the patcher output is **byte-identical** to the
reference mod across all 12 files (no missing, no extra files).

## Save side (existing ships) - `ShipSaveSlotsPatcher`

Ships live in the RocksDB save under the **`R5BLShip`** column family, one BSON
doc per ship (a character can own several). Same blueprint + live-array structure
the game cross-checks as the player jewelry case, so the surgery mirrors
`InventorySaveSlotsPatcher`: grow the live `Slots[]` array (clone an empty slot,
renumber index + SlotId), set the blueprint CountSlots, fix every enclosing doc's
int32 size prefix, rebuild the checkpoint ZIP. Two modules per ship are patched
(Default/cargo + Equipment/combat orders), one re-locating pass each because a
splice shifts later offsets.

**Idempotency / consistency with the pak:** each ship doc carries its source
template in `Inventory.InventoryParams`
(e.g. `.../DA_ShipInventory_Ketch_Stock.<...>`). We read that, look up the
**vanilla** cargo base from the matching dumped JSON, and target
`round(vanillaBase * mult)` - never the current value - so re-applying is a
no-op and the result equals what the pak writes for new ships. Unsupported ship
types (Cutter/Merchant/Boat) are reported and skipped.

Shared `InventorySaveSlotsPatcher.DiscoverCharacterDbFolders()` (extracted) keeps
the backup-dir exclusion (the "3 chars, 8 rows" fix) in one place.

**De-risked via write-roundtrip spike** on copies of the real save (live save
read-only): 3 ships across 2 characters patched x2 cargo + 5 combat
(28->56, 20->40, 28->56), reopened, **all persisted (live + blueprint)**,
checkpoint ZIP rebuilt, pre-patch backups written, re-patch = AlreadyMatches.
Only the in-game load is untested headless (same caveat as jewelry; the
algorithm mirrors the proven jewelry path).

## GUI

- **Basic tab** "Ship Slots" card: cargo multiplier slider (1-3, step .25) +
  combat-order slider (1-10). References `ship.png` (drop-in icon, not present).
- **Characters tab** generalised to "Save Patcher": existing ring/necklace
  character cards PLUS new ship cards (owner - name/type, current cargo/combat vs
  profile target, backup+patch button, shrink-blocking confirm). `/api/savegame/ships`
  (list) + `/api/savegame/ship-patch` (patch) on `SavegameEndpoint`.

## Files

New: `ShipSlotsPatcher.cs`, `ShipSaveSlotsPatcher.cs`.
Changed Core: `Profile.cs`, `WindrosePaths.cs`, `BuildPipeline.cs`,
`BuildPipeline.Resolvers.cs`, `BuildPipelineResults.cs`,
`InventorySaveSlotsPatcher.cs` (folder-discovery extract).
Changed Web: `BuildEndpoint.cs`, `SavegameEndpoint.cs`, `app.js`,
`tabs/characters.{html,js,css}`, `tabs/misc.{html,js}`.

## Testing notes

- Restart the dev server (pulls Core build + fresh wwwroot embed) for the cards
  to appear. Build the slider pak as usual and deploy to `~mods`.
- Save patch: close Windrose fully + turn OFF Steam Cloud Sync for Windrose, or
  the cloud reverts the patched save. The tool backs up each ship's value and
  rebuilds the checkpoint ZIP; making a copy of `SaveProfiles` first is still wise.

## Possible follow-ups

- Extend scope to Cutter/Merchant/Boat (add their families to `ShipFamilies`).
- A future cleanup could extract the shared BSON surgery primitives now duplicated
  between `InventorySaveSlotsPatcher` and `ShipSaveSlotsPatcher` into one helper
  (kept separate here to avoid regressing the shipped jewelry patcher).
