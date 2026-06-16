// Runtime loot patcher for binary-only DataAssets:
//
// 1. UR5SegmentTreeData - multiplier on FR5DropLootData.Amount (Divi, Palms)
// 2. UR5DigVolumeConfig - multiplier on FR5DigVolumeLootData.Amount (Iron mines)
//
// These have their drops baked into binary DataAssets, NOT in the DA_LT_*
// JSON loot tables. JSON pak overrides cannot reach them.
// Loot-table min/max (UR5BLLootParams) is handled entirely by the pak.
//
// Lifecycle: QmLoot_Init() reads sidecars, QmLoot_OnProcessEvent() patches
// DataAssets during loading (before tree actors cache values).
#pragma once

bool QmLoot_Init();
void QmLoot_Heartbeat();
bool QmLoot_IsArmed();
void QmLoot_OnWorldChanged();

// ProcessEvent rider: aggressive early-scan during map loading so tree DataAssets
// are patched before actors spawn and cache their LootData. Call from Hook_ProcessEvent.
void QmLoot_OnProcessEvent(void* self, void* func, void* parms);
