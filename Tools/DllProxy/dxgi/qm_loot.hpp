// Runtime loot patcher. Four mechanisms, belt-and-suspenders:
//
// 1. UR5BLLootParams  - per-entry min/max overrides (MineralNodes, Mobs, etc.)
// 2. UR5SegmentTreeData - multiplier on FR5DropLootData.Amount (Divi, Palms)
// 3. UR5DigVolumeConfig - multiplier on FR5DigVolumeLootData.Amount (Iron mines)
// 4. UR5GameplaySpawnerParams - respawn speed (divides RespawnInterval)
//
// Trees (2) and DigVolumes (3) have their drops baked into binary DataAssets,
// NOT in the DA_LT_* JSON loot tables. JSON pak overrides only reach (1).
//
// Lifecycle: QmLoot_Init() reads sidecars, QmLoot_OnProcessEvent() patches
// DataAssets during loading (before tree actors cache values),
// QmLoot_Heartbeat() patches on gameplay-map as backup.
#pragma once

bool QmLoot_Init();
void QmLoot_Heartbeat();
bool QmLoot_IsArmed();
void QmLoot_OnWorldChanged();

// ProcessEvent rider: aggressive early-scan during map loading so tree DataAssets
// are patched before actors spawn and cache their LootData. Call from Hook_ProcessEvent.
void QmLoot_OnProcessEvent(void* self, void* func, void* parms);
