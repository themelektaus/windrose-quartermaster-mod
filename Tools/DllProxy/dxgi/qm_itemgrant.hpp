// Quartermaster "item grant" - spawning items into the player inventory via the
// native scenario task R5ScenarioTask_AddReward (Reward TArray<FR5BLItemsStackData>
// @0x118), the sibling of the proven R5ScenarioTask_AddExp construct grant in
// qm_killxp.cpp (same base-class gates: state@0xC0, owner@0xC8, Outer@0x20).
//
// Recon (live, 2026-06-11) proved the surface: Execute is virtual and shares its
// vtable slot with the proven AddExp Execute; no live AddReward donors carry data,
// so the Reward array is SYNTHESIZED - the soft ptr gets only the two
// FTopLevelAssetPath FNames of an already-loaded R5BLInventoryItem PDA (the at-rest
// state of any asset-serialized soft ptr; Execute resolves it like every quest
// reward). No FName creation, no donor required.

#pragma once

// Fires the grant for `argument` = "<AssetName>" or "<AssetName>:<Count>" (the PDA
// asset name, e.g. DA_CID_Alchemy_Bandages_T01; count defaults to 1, clamped 1..999).
// The named PDA must already be loaded (it is matched against live GObjects) - unless
// packagePath names its mounted package (custom mod-pak items, catalog field 3): then
// a miss triggers one sync load (LoadAsset_Blocking) before giving up.
// An EMPTY argument runs the recon dump instead. Game thread only (PE-hook click
// dispatch); re-entrancy-guarded + SEH-guarded; a fault leaves the save untouched.
// Wired to the ModTab "add_item_test" / "add_selected_item" button commands.
void QmItemGrant_Fire(const char* argument, const char* packagePath = nullptr);

// Logs the full recon picture (game thread; bounded output; SEH-guarded throughout).
void QmItemGrant_ReconDump();
