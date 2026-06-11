// Quartermaster "item grant" - spawning items into the player inventory via the
// native scenario task R5ScenarioTask_AddReward (Reward TArray<FR5BLItemsStackData>
// @0x118), the sibling of the proven R5ScenarioTask_AddExp construct grant in
// qm_killxp.cpp (same base-class gates: state@0xC0, owner@0xC8, Outer@0x20).
//
// Current stage: click-driven recon dump. Two unknowns block the actual grant and
// both are only observable in a live process:
//   1. AddReward's Execute entry - probed by locating the proven AddExp Execute RVA
//      in the AddExp CDO vtable and reading the same slot from the AddReward CDO.
//   2. What a VALID populated Reward array looks like (soft-ptr layout, live donor
//      instances from loaded scenario assets, R5BLInventoryItem PDA census).

#pragma once

// Logs the full recon picture (game thread; bounded output; SEH-guarded throughout).
// Wired to the ModTab "add_item_test" button command.
void QmItemGrant_ReconDump();
