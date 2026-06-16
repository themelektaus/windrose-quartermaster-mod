// Quartermaster runtime loot-table patcher. See qm_loot.hpp.

#define _CRT_SECURE_NO_WARNINGS
#include <windows.h>
#include <stdint.h>
#include <stdio.h>
#include <string.h>

#include "qm_ue.hpp"
#include "qm_log.hpp"
#include "qm_json.hpp"

namespace
{
    // Sidecar dir (same pattern as qm_weather.cpp).
    bool LocateSidecarDir(char* out, size_t outSz)
    {
        if (!out || outSz == 0) return false;
        HMODULE self = nullptr;
        if (!GetModuleHandleExA(
                GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS | GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT,
                reinterpret_cast<LPCSTR>(&LocateSidecarDir), &self) || !self)
            return false;
        char dllPath[MAX_PATH];
        DWORD n = GetModuleFileNameA(self, dllPath, sizeof(dllPath));
        if (n == 0 || n >= sizeof(dllPath)) return false;
        char* lastSep = strrchr(dllPath, '\\');
        if (!lastSep) return false;
        *lastSep = '\0';
        int w = snprintf(out, outSz, "%s\\Quartermaster", dllPath);
        return w > 0 && (size_t)w < outSz;
    }

    // ---- SDK offsets (Dumper-7, build 5.6.1-0+UE5-R5) ------------------------

    // TArray layout (x64): Data* @ +0, Count int32 @ +8, Max int32 @ +C
    constexpr size_t kOff_TArrayData    = 0x00;
    constexpr size_t kOff_TArrayCount   = 0x08;

    // --- (1) UR5BLLootParams : UR5JsonRuntimePDA (0x50 total) ----------------
    //   +0x40  TArray<FR5BLLootData> LootData
    constexpr size_t kOff_LootData      = 0x40;
    // FR5BLLootData (0x70 per element)
    //   +0x00  int32 min_0
    //   +0x04  int32 max_0
    constexpr size_t kSizeLootEntry     = 0x70;
    constexpr size_t kOff_EntryMin      = 0x00;
    constexpr size_t kOff_EntryMax      = 0x04;

    // --- (2) UR5SegmentTreeData : UDataAsset (0x04B0 total) ------------------
    //   +0x0118 TArray<FR5DropLootData> LootData (segment loot)
    //   +0x0130 TArray<FR5DropLootData> CollectLootSets (stump/collect loot)
    constexpr size_t kOff_TreeLootData    = 0x0118;
    constexpr size_t kOff_TreeCollectLoot = 0x0130;
    // FR5DropLootData (0x50 per element)
    //   +0x2C  FInt32Interval Amount (min int32 @ +0, max int32 @ +4)
    constexpr size_t kSizeDropLootEntry   = 0x50;
    constexpr size_t kOff_DropLootMin     = 0x2C;
    constexpr size_t kOff_DropLootMax     = 0x30;
    constexpr size_t kOff_DropLootUseCust = 0x28; // bool bUseCustomDistribution
    constexpr size_t kOff_DropLootCustArr = 0x38; // TArray<int32> CustomDistribution
    constexpr size_t kOff_DropLootStackByMin = 0x48; // FInt32Interval StackBy.min
    constexpr size_t kOff_DropLootStackByMax = 0x4C; // FInt32Interval StackBy.max

    // --- (3) UR5DigVolumeConfig : UDataAsset (0x0210 total) ------------------
    //   +0x0148 ER5DigVolumeLootDropType (uint8: 0=PerDeletedRock, 1=FixedWhole)
    //   +0x0150 FR5DigVolumeLootConfigPerDeletedRockAmount
    //           +0x00 int32 AmountOfRocks
    //           +0x08 TArray<FR5DigVolumeLootData>
    //   +0x0170 FR5DigVolumeLootConfigFixedAmountForWholeVolume
    //           +0x00 TArray<FR5DigVolumeLootDataWholeVolume>
    //           +0x10 TArray<FR5DigVolumeLootData>
    constexpr size_t kOff_DigDropType     = 0x0148;
    constexpr size_t kOff_DigPerRock      = 0x0150;
    constexpr size_t kOff_DigFixed        = 0x0170;
    // FR5DigVolumeLootData (0x38 per element)
    //   +0x28  FInt32Interval Amount (min @ +0, max @ +4)
    constexpr size_t kSizeDigLootEntry    = 0x38;
    constexpr size_t kOff_DigLootMin      = 0x28;
    constexpr size_t kOff_DigLootMax      = 0x2C;
    // FR5DigVolumeLootDataWholeVolume (0x30 per element)
    //   +0x00  float AmountInVolume
    constexpr size_t kSizeDigWholeEntry   = 0x30;
    constexpr size_t kOff_DigWholeAmount  = 0x00;

    // --- (4) UR5GameplaySpawnerParams : UR5JsonRuntimeDA (0x0088 total) ------
    //   +0x0060  FFloatInterval RespawnInterval (float Min, float Max - seconds)
    constexpr size_t kOff_SpawnerRespawnMin = 0x0060;
    constexpr size_t kOff_SpawnerRespawnMax = 0x0064;

    // EObjectFlags used to reject CDOs/archetypes.
    constexpr uint32_t RF_ClassDefaultObject = 0x00000010;
    constexpr uint32_t RF_ArchetypeObject    = 0x00000020;

    // ---- Per-entry override -------------------------------------------------
    struct EntryOverride {
        int idx;
        int minVal;
        int maxVal;
    };

    // ---- Per-loot-table config ----------------------------------------------
    constexpr int kMaxTables  = 512;
    constexpr int kMaxEntries = 32;

    struct LootTableConfig {
        char assetName[128];
        EntryOverride entries[kMaxEntries];
        int entryCount;
        bool applied;
    };

    LootTableConfig g_tables[kMaxTables] = {};
    int  g_tableCount    = 0;
    bool g_armed         = false;
    bool g_allApplied    = false;

    // Multipliers for non-LootParams systems (read from __tree_mult / __digvolume_mult / __respawn_speed)
    float g_treeMult      = 1.0f;
    float g_digVolumeMult = 1.0f;
    float g_respawnSpeed  = 1.0f;

    // Per-UObject tracking: DataAssets persist across world changes, so we
    // track patched pointers to avoid cascading re-multiplication (3->9->27).
    constexpr int kMaxTracked = 256;
    void* g_patchedTreePtrs[kMaxTracked] = {};
    int   g_patchedTreeCount = 0;
    void* g_patchedDigPtrs[kMaxTracked]  = {};
    int   g_patchedDigCount  = 0;
    void* g_patchedSpawnerPtrs[kMaxTracked] = {};
    int   g_patchedSpawnerCount = 0;

    bool IsObjTracked(void* const* arr, int count, void* p)
    {
        for (int i = 0; i < count; ++i) if (arr[i] == p) return true;
        return false;
    }
    void TrackObj(void** arr, int& count, void* p)
    {
        if (count < kMaxTracked) arr[count++] = p;
    }

    // Early-scan: patch DataAssets DURING loading (via ProcessEvent rider)
    // so tree actors read our multiplied values when they spawn.
    // No convergence - scans whenever GObjects grows (new objects loaded).
    int g_earlyScanHWM = 0; // high-water mark: last scanned GObjects index

    // ---- JSON parsing -------------------------------------------------------
    // Format: { "AssetName": { "entryIdx": { "min": N, "max": N } }, ... }
    bool ParseSidecarFile(const char* path)
    {
        std::string raw;
        if (!QmJson::ReadWholeFile(path, raw) || raw.empty())
        {
            QM_LOG_WARN("[Loot] cannot read %s", path);
            return false;
        }
        QmJson::StripUtf8Bom(raw);

        QmJson::Parser jp(raw.c_str(), raw.size());

        if (!jp.expect('{')) { QM_LOG_WARN("[Loot] %s: expected top-level '{'", path); return false; }

        while (jp.ok && !jp.peek('}'))
        {
            // Key: asset name
            std::string assetName;
            if (!jp.parseString(assetName)) break;
            if (!jp.expect(':')) break;

            // Special keys: __tree_mult, __digvolume_mult -> float multiplier
            if (assetName.size() > 2 && assetName[0] == '_' && assetName[1] == '_')
            {
                double v = 0;
                if (jp.parseNumber(v))
                {
                    if (assetName == "__tree_mult")            g_treeMult      = (float)v;
                    else if (assetName == "__digvolume_mult") g_digVolumeMult = (float)v;
                    else if (assetName == "__respawn_speed")  g_respawnSpeed  = (float)v;
                    else QM_LOG_WARN("[Loot] unknown special key '%s' in %s", assetName.c_str(), path);
                }
                if (jp.peek(',')) ++jp.p;
                continue;
            }

            if (g_tableCount >= kMaxTables)
            {
                QM_LOG_WARN("[Loot] table limit (%d) reached, skipping rest of %s", kMaxTables, path);
                jp.skipValue();
                if (jp.peek(',')) { ++jp.p; continue; }
                break;
            }

            // Merge into existing table or create new
            LootTableConfig* tbl = nullptr;
            for (int t = 0; t < g_tableCount; ++t)
            {
                if (strcmp(g_tables[t].assetName, assetName.c_str()) == 0)
                {
                    tbl = &g_tables[t];
                    break;
                }
            }
            if (!tbl)
            {
                tbl = &g_tables[g_tableCount++];
                memset(tbl, 0, sizeof(*tbl));
                strncpy(tbl->assetName, assetName.c_str(), sizeof(tbl->assetName) - 1);
            }

            // Value: { "entryIdx": { "min": N, "max": N }, ... }
            if (!jp.expect('{')) break;

            while (jp.ok && !jp.peek('}'))
            {
                std::string idxStr;
                if (!jp.parseString(idxStr)) break;
                int idx = atoi(idxStr.c_str());

                if (!jp.expect(':')) break;
                if (!jp.expect('{')) break;

                int minVal = -1, maxVal = -1;
                while (jp.ok && !jp.peek('}'))
                {
                    std::string field;
                    if (!jp.parseString(field)) break;
                    if (!jp.expect(':')) break;
                    double v = 0;
                    if (!jp.parseNumber(v)) break;
                    if (field == "min") minVal = (int)v;
                    else if (field == "max") maxVal = (int)v;
                    if (jp.peek(',')) ++jp.p;
                }
                if (!jp.expect('}')) break;

                if (minVal >= 0 || maxVal >= 0)
                {
                    if (tbl->entryCount < kMaxEntries)
                    {
                        EntryOverride& e = tbl->entries[tbl->entryCount++];
                        e.idx    = idx;
                        e.minVal = minVal;
                        e.maxVal = maxVal;
                    }
                }

                if (jp.peek(',')) ++jp.p;
            }
            if (!jp.expect('}')) break;

            if (jp.peek(',')) ++jp.p;
        }
        // Tolerate missing closing '}' for robustness

        return true;
    }

    // ---- GObjects scan + patch ----------------------------------------------
    bool IsCdoOrArchetype(const QmUE::UObject* o)
    {
        return o && (o->Flags & (RF_ClassDefaultObject | RF_ArchetypeObject)) != 0;
    }

    // Try to patch one LootTableConfig against a matched UObject.
    // Returns true if all entries were written successfully.
    bool PatchLootTable(LootTableConfig& tbl, QmUE::UObject* obj)
    {
        __try
        {
            uint8_t* base = reinterpret_cast<uint8_t*>(obj);

            // Read TArray<FR5BLLootData> at kOff_LootData
            uint8_t* dataPtr = *reinterpret_cast<uint8_t**>(base + kOff_LootData + kOff_TArrayData);
            int32_t  count   = *reinterpret_cast<int32_t*>(base + kOff_LootData + kOff_TArrayCount);

            if (!dataPtr || count <= 0 || count > 256)
            {
                QM_LOG_WARN("[Loot] %s: TArray invalid (data=0x%p count=%d) - skip",
                    tbl.assetName, dataPtr, count);
                return false;
            }

            int written = 0;
            for (int e = 0; e < tbl.entryCount; ++e)
            {
                const EntryOverride& ovr = tbl.entries[e];
                if (ovr.idx < 0 || ovr.idx >= count)
                {
                    QM_LOG_WARN("[Loot] %s: entry idx %d out of range (count=%d) - skip entry",
                        tbl.assetName, ovr.idx, count);
                    continue;
                }

                uint8_t* entry = dataPtr + (size_t)ovr.idx * kSizeLootEntry;

                int32_t oldMin = *reinterpret_cast<int32_t*>(entry + kOff_EntryMin);
                int32_t oldMax = *reinterpret_cast<int32_t*>(entry + kOff_EntryMax);

                if (ovr.minVal >= 0) *reinterpret_cast<int32_t*>(entry + kOff_EntryMin) = ovr.minVal;
                if (ovr.maxVal >= 0) *reinterpret_cast<int32_t*>(entry + kOff_EntryMax) = ovr.maxVal;

                int32_t newMin = *reinterpret_cast<int32_t*>(entry + kOff_EntryMin);
                int32_t newMax = *reinterpret_cast<int32_t*>(entry + kOff_EntryMax);

                QM_LOG_INFO("[Loot] %s[%d]: min %d->%d, max %d->%d",
                    tbl.assetName, ovr.idx, oldMin, newMin, oldMax, newMax);
                ++written;
            }

            return written > 0;
        }
        __except (EXCEPTION_EXECUTE_HANDLER)
        {
            QM_LOG_WARN("[Loot] %s: SEH fault during patch - skip", tbl.assetName);
            return false;
        }
    }

    // Scan GObjects for UR5BLLootParams instances and try to match+patch.
    // scanFrom: start index in GObjects (0 = full scan, >0 = incremental).
    void ScanAndPatch(int scanFrom = 0)
    {
        if (!QmUE::IsReady()) return;
        QmUE::TUObjectArray* arr = QmUE::GetGObjects();
        const QmUE::int32 total = arr->Num();

        int matched = 0, patched = 0;
        char clsBuf[128], nameBuf[128];

        for (QmUE::int32 i = (QmUE::int32)scanFrom; i < total; ++i)
        {
            QmUE::UObject* obj = arr->GetByIndex(i);
            if (!obj || !obj->Class) continue;

            // Match class: UR5BLLootParams -> FName "R5BLLootParams"
            if (!QmUE::ResolveFNameNarrow(obj->Class->Name, clsBuf, sizeof(clsBuf))) continue;
            if (strcmp(clsBuf, "R5BLLootParams") != 0) continue;

            // Skip CDO/archetype
            if (IsCdoOrArchetype(obj)) continue;

            // Resolve object name (e.g. "DA_LT_Mineral_Iron_01")
            if (!QmUE::ResolveFNameNarrow(obj->Name, nameBuf, sizeof(nameBuf))) continue;

            // Match against our override list
            for (int t = 0; t < g_tableCount; ++t)
            {
                LootTableConfig& tbl = g_tables[t];
                if (tbl.applied) continue;
                if (strcmp(tbl.assetName, nameBuf) != 0) continue;

                ++matched;
                if (PatchLootTable(tbl, obj))
                {
                    tbl.applied = true;
                    ++patched;
                }
            }
        }

        // Check if all tables are applied
        bool allDone = true;
        for (int t = 0; t < g_tableCount; ++t)
        {
            if (!g_tables[t].applied) { allDone = false; break; }
        }

        if (matched > 0 || patched > 0)
        {
            int pending = 0;
            for (int t = 0; t < g_tableCount; ++t)
                if (!g_tables[t].applied) ++pending;
            QM_LOG_INFO("[Loot] scan: %d matched, %d patched, %d pending",
                matched, patched, pending);
        }

        if (allDone && !g_allApplied)
        {
            g_allApplied = true;
            QM_LOG_INFO("[Loot] *** ALL %d table(s) patched ***", g_tableCount);
        }
    }

    // Helper: multiply all entries in a TArray<FR5DropLootData> by mult.
    // Handles BOTH Amount (FInt32Interval) AND CustomDistribution (TArray<int32>)
    // because when bUseCustomDistribution is true the game ignores Amount entirely.
    int MultiplyDropLootArray(uint8_t* base, size_t arrayOffset, float mult,
        const char* label, const char* arrayName)
    {
        uint8_t* dataPtr = *reinterpret_cast<uint8_t**>(base + arrayOffset + kOff_TArrayData);
        int32_t  count   = *reinterpret_cast<int32_t*>(base + arrayOffset + kOff_TArrayCount);
        if (!dataPtr || count <= 0 || count > 128) return 0;

        int changed = 0;
        for (int e = 0; e < count; ++e)
        {
            uint8_t* entry = dataPtr + (size_t)e * kSizeDropLootEntry;

            // Read bUseCustomDistribution flag
            bool useCustom = *reinterpret_cast<bool*>(entry + kOff_DropLootUseCust);

            // (A) Always multiply Amount min/max (belt-and-suspenders)
            int32_t* pMin = reinterpret_cast<int32_t*>(entry + kOff_DropLootMin);
            int32_t* pMax = reinterpret_cast<int32_t*>(entry + kOff_DropLootMax);
            int32_t oldMin = *pMin, oldMax = *pMax;
            int32_t newMin = (int32_t)(oldMin * mult + 0.5f);
            int32_t newMax = (int32_t)(oldMax * mult + 0.5f);
            if (newMin < 1 && oldMin >= 1) newMin = 1;
            if (newMax < 1 && oldMax >= 1) newMax = 1;
            if (newMin != oldMin || newMax != oldMax)
            {
                *pMin = newMin;
                *pMax = newMax;
                ++changed;
            }

            // (B) If bUseCustomDistribution, ALSO multiply each entry in CustomDistribution
            int custChanged = 0;
            if (useCustom)
            {
                uint8_t* cdBase = entry + kOff_DropLootCustArr;
                int32_t* cdData = *reinterpret_cast<int32_t**>(cdBase + kOff_TArrayData);
                int32_t  cdCnt  = *reinterpret_cast<int32_t*>(cdBase + kOff_TArrayCount);
                if (cdData && cdCnt > 0 && cdCnt <= 512)
                {
                    for (int d = 0; d < cdCnt; ++d)
                    {
                        int32_t oldVal = cdData[d];
                        int32_t newVal = (int32_t)(oldVal * mult + 0.5f);
                        if (newVal < 1 && oldVal >= 1) newVal = 1;
                        if (newVal != oldVal)
                        {
                            cdData[d] = newVal;
                            ++custChanged;
                        }
                    }
                    ++changed;
                }
            }

            // (C) Also multiply StackBy (controls per-pickup grouping)
            int32_t* pSBMin = reinterpret_cast<int32_t*>(entry + kOff_DropLootStackByMin);
            int32_t* pSBMax = reinterpret_cast<int32_t*>(entry + kOff_DropLootStackByMax);
            int32_t oldSBMin = *pSBMin, oldSBMax = *pSBMax;
            int32_t newSBMin = (int32_t)(oldSBMin * mult + 0.5f);
            int32_t newSBMax = (int32_t)(oldSBMax * mult + 0.5f);
            if (newSBMin < 1 && oldSBMin >= 1) newSBMin = 1;
            if (newSBMax < 1 && oldSBMax >= 1) newSBMax = 1;
            if (newSBMin != oldSBMin || newSBMax != oldSBMax)
            {
                *pSBMin = newSBMin;
                *pSBMax = newSBMax;
            }

            QM_LOG_INFO("[Loot] %s %s[%d]: Amount %d-%d -> %d-%d, StackBy %d-%d -> %d-%d%s%s",
                label, arrayName, e, oldMin, oldMax, newMin, newMax,
                oldSBMin, oldSBMax, newSBMin, newSBMax,
                useCustom ? " [CustomDist ACTIVE" : "",
                useCustom ? (custChanged > 0 ?
                    ", entries multiplied]" : ", 0 entries changed]") : "");
        }
        return changed;
    }

    // (2) Scan UR5SegmentTreeData UObjects and multiply drop amounts.
    // Uses per-object tracking: DataAssets persist across world changes.
    // scanFrom: start index in GObjects (0 = full scan, >0 = incremental).
    void ScanAndPatchTrees(int scanFrom = 0)
    {
        if (g_treeMult == 1.0f) return;
        if (!QmUE::IsReady()) return;

        QmUE::TUObjectArray* arr = QmUE::GetGObjects();
        const QmUE::int32 total = arr->Num();

        int found = 0, patched = 0;
        char clsBuf[128], nameBuf[128];

        for (QmUE::int32 i = (QmUE::int32)scanFrom; i < total; ++i)
        {
            QmUE::UObject* obj = arr->GetByIndex(i);
            if (!obj || !obj->Class) continue;
            if (!QmUE::ResolveFNameNarrow(obj->Class->Name, clsBuf, sizeof(clsBuf))) continue;
            if (strcmp(clsBuf, "R5SegmentTreeData") != 0) continue;
            if (IsCdoOrArchetype(obj)) continue;
            if (IsObjTracked(g_patchedTreePtrs, g_patchedTreeCount, obj)) continue;

            QmUE::ResolveFNameNarrow(obj->Name, nameBuf, sizeof(nameBuf));
            ++found;

            __try
            {
                uint8_t* base = reinterpret_cast<uint8_t*>(obj);
                int c = 0;
                c += MultiplyDropLootArray(base, kOff_TreeLootData, g_treeMult,
                    nameBuf, "LootData");
                c += MultiplyDropLootArray(base, kOff_TreeCollectLoot, g_treeMult,
                    nameBuf, "CollectLoot");
                if (c > 0) ++patched;
            }
            __except (EXCEPTION_EXECUTE_HANDLER)
            {
                QM_LOG_WARN("[Loot] tree %s: SEH fault - skip", nameBuf);
            }

            TrackObj(g_patchedTreePtrs, g_patchedTreeCount, obj);
        }

        if (found > 0)
        {
            QM_LOG_INFO("[Loot] tree scan: %d new DataAsset(s), %d patched (x%.2f), %d tracked total",
                found, patched, (double)g_treeMult, g_patchedTreeCount);
        }
    }

    // Helper: multiply all entries in a TArray<FR5DigVolumeLootData> by mult.
    int MultiplyDigLootArray(uint8_t* arrayBase, float mult,
        const char* label, const char* arrayName)
    {
        uint8_t* dataPtr = *reinterpret_cast<uint8_t**>(arrayBase + kOff_TArrayData);
        int32_t  count   = *reinterpret_cast<int32_t*>(arrayBase + kOff_TArrayCount);
        if (!dataPtr || count <= 0 || count > 128) return 0;

        int changed = 0;
        for (int e = 0; e < count; ++e)
        {
            uint8_t* entry = dataPtr + (size_t)e * kSizeDigLootEntry;
            int32_t* pMin = reinterpret_cast<int32_t*>(entry + kOff_DigLootMin);
            int32_t* pMax = reinterpret_cast<int32_t*>(entry + kOff_DigLootMax);
            int32_t oldMin = *pMin, oldMax = *pMax;
            int32_t newMin = (int32_t)(oldMin * mult + 0.5f);
            int32_t newMax = (int32_t)(oldMax * mult + 0.5f);
            if (newMin < 1 && oldMin >= 1) newMin = 1;
            if (newMax < 1 && oldMax >= 1) newMax = 1;
            if (newMin != oldMin || newMax != oldMax)
            {
                *pMin = newMin;
                *pMax = newMax;
                QM_LOG_INFO("[Loot] %s %s[%d]: %d-%d -> %d-%d",
                    label, arrayName, e, oldMin, oldMax, newMin, newMax);
                ++changed;
            }
        }
        return changed;
    }

    // (3) Scan UR5DigVolumeConfig UObjects and multiply drop amounts.
    // Uses per-object tracking: DataAssets persist across world changes.
    // scanFrom: start index in GObjects (0 = full scan, >0 = incremental).
    void ScanAndPatchDigVolumes(int scanFrom = 0)
    {
        if (g_digVolumeMult == 1.0f) return;
        if (!QmUE::IsReady()) return;

        QmUE::TUObjectArray* arr = QmUE::GetGObjects();
        const QmUE::int32 total = arr->Num();

        int found = 0, patched = 0;
        char clsBuf[128], nameBuf[128];

        for (QmUE::int32 i = (QmUE::int32)scanFrom; i < total; ++i)
        {
            QmUE::UObject* obj = arr->GetByIndex(i);
            if (!obj || !obj->Class) continue;
            if (!QmUE::ResolveFNameNarrow(obj->Class->Name, clsBuf, sizeof(clsBuf))) continue;
            if (strcmp(clsBuf, "R5DigVolumeConfig") != 0) continue;
            if (IsCdoOrArchetype(obj)) continue;
            if (IsObjTracked(g_patchedDigPtrs, g_patchedDigCount, obj)) continue;

            QmUE::ResolveFNameNarrow(obj->Name, nameBuf, sizeof(nameBuf));
            ++found;

            __try
            {
                uint8_t* base = reinterpret_cast<uint8_t*>(obj);
                uint8_t dropType = *reinterpret_cast<uint8_t*>(base + kOff_DigDropType);
                int c = 0;

                if (dropType == 0) // PerDeletedRockAmount
                {
                    // TArray<FR5DigVolumeLootData> @ kOff_DigPerRock + 0x08
                    c += MultiplyDigLootArray(
                        base + kOff_DigPerRock + 0x08,
                        g_digVolumeMult, nameBuf, "PerRock");
                }
                else if (dropType == 1) // FixedAmountForWholeVolume
                {
                    // GuaranteedLoot: TArray<FR5DigVolumeLootDataWholeVolume> @ kOff_DigFixed + 0x00
                    uint8_t* gPtr = *reinterpret_cast<uint8_t**>(base + kOff_DigFixed + kOff_TArrayData);
                    int32_t  gCnt = *reinterpret_cast<int32_t*>(base + kOff_DigFixed + kOff_TArrayCount);
                    if (gPtr && gCnt > 0 && gCnt <= 64)
                    {
                        for (int e = 0; e < gCnt; ++e)
                        {
                            uint8_t* entry = gPtr + (size_t)e * kSizeDigWholeEntry;
                            float* pAmt = reinterpret_cast<float*>(entry + kOff_DigWholeAmount);
                            float oldAmt = *pAmt;
                            *pAmt = oldAmt * g_digVolumeMult;
                            if (*pAmt != oldAmt)
                            {
                                QM_LOG_INFO("[Loot] %s Guaranteed[%d]: %.1f -> %.1f",
                                    nameBuf, e, oldAmt, *pAmt);
                                ++c;
                            }
                        }
                    }
                    // ChanceLoot: TArray<FR5DigVolumeLootData> @ kOff_DigFixed + 0x10
                    c += MultiplyDigLootArray(
                        base + kOff_DigFixed + 0x10,
                        g_digVolumeMult, nameBuf, "Chance");
                }

                if (c > 0) ++patched;
            }
            __except (EXCEPTION_EXECUTE_HANDLER)
            {
                QM_LOG_WARN("[Loot] digvol %s: SEH fault - skip", nameBuf);
            }

            TrackObj(g_patchedDigPtrs, g_patchedDigCount, obj);
        }

        if (found > 0)
        {
            QM_LOG_INFO("[Loot] digvolume scan: %d new config(s), %d patched (x%.2f), %d tracked total",
                found, patched, (double)g_digVolumeMult, g_patchedDigCount);
        }
    }

    // (4) Scan UR5GameplaySpawnerParams UObjects and divide RespawnInterval.
    // Speed > 1 = faster respawn = interval divided by speed.
    // Uses per-object tracking: DataAssets persist across world changes.
    // scanFrom: start index in GObjects (0 = full scan, >0 = incremental).
    void ScanAndPatchSpawnerParams(int scanFrom = 0)
    {
        if (g_respawnSpeed == 1.0f) return;
        if (!QmUE::IsReady()) return;

        QmUE::TUObjectArray* arr = QmUE::GetGObjects();
        const QmUE::int32 total = arr->Num();

        int found = 0, patched = 0;
        char clsBuf[128], nameBuf[128];

        for (QmUE::int32 i = (QmUE::int32)scanFrom; i < total; ++i)
        {
            QmUE::UObject* obj = arr->GetByIndex(i);
            if (!obj || !obj->Class) continue;
            if (!QmUE::ResolveFNameNarrow(obj->Class->Name, clsBuf, sizeof(clsBuf))) continue;
            if (strcmp(clsBuf, "R5GameplaySpawnerParams") != 0) continue;
            if (IsCdoOrArchetype(obj)) continue;
            if (IsObjTracked(g_patchedSpawnerPtrs, g_patchedSpawnerCount, obj)) continue;

            QmUE::ResolveFNameNarrow(obj->Name, nameBuf, sizeof(nameBuf));

            // Only patch resource spawners (DA_ResSpawner_*).
            // NPC/enemy/chest/garrison spawners are handled by the pak-based NPC Spawn Patcher.
            if (strncmp(nameBuf, "DA_ResSpawner_", 14) != 0)
            {
                TrackObj(g_patchedSpawnerPtrs, g_patchedSpawnerCount, obj);
                continue;
            }

            ++found;

            __try
            {
                uint8_t* base = reinterpret_cast<uint8_t*>(obj);
                float* pMin = reinterpret_cast<float*>(base + kOff_SpawnerRespawnMin);
                float* pMax = reinterpret_cast<float*>(base + kOff_SpawnerRespawnMax);
                float oldMin = *pMin, oldMax = *pMax;

                // Divide interval by speed (speed 2.0 = half the wait time)
                float newMin = oldMin / g_respawnSpeed;
                float newMax = oldMax / g_respawnSpeed;
                if (newMin < 1.0f && oldMin >= 1.0f) newMin = 1.0f;
                if (newMax < 1.0f && oldMax >= 1.0f) newMax = 1.0f;

                if (newMin != oldMin || newMax != oldMax)
                {
                    *pMin = newMin;
                    *pMax = newMax;
                    QM_LOG_INFO("[Loot] spawner %s: RespawnInterval %.0f-%.0f -> %.0f-%.0f sec (speed x%.2f)",
                        nameBuf, oldMin, oldMax, newMin, newMax, (double)g_respawnSpeed);
                    ++patched;
                }
            }
            __except (EXCEPTION_EXECUTE_HANDLER)
            {
                QM_LOG_WARN("[Loot] spawner %s: SEH fault - skip", nameBuf);
            }

            TrackObj(g_patchedSpawnerPtrs, g_patchedSpawnerCount, obj);
        }

        if (found > 0)
        {
            QM_LOG_INFO("[Loot] spawner scan: %d new param(s), %d patched (speed x%.2f), %d tracked total",
                found, patched, (double)g_respawnSpeed, g_patchedSpawnerCount);
        }
    }
}

bool QmLoot_Init()
{
    g_tableCount   = 0;
    g_armed        = false;
    g_allApplied   = false;
    g_treeMult      = 1.0f;
    g_digVolumeMult = 1.0f;
    g_respawnSpeed  = 1.0f;
    g_patchedTreeCount    = 0;
    g_patchedDigCount     = 0;
    g_patchedSpawnerCount = 0;
    memset(g_patchedTreePtrs, 0, sizeof(g_patchedTreePtrs));
    memset(g_patchedDigPtrs, 0, sizeof(g_patchedDigPtrs));
    memset(g_patchedSpawnerPtrs, 0, sizeof(g_patchedSpawnerPtrs));
    g_earlyScanHWM = 0;

    char dir[MAX_PATH];
    if (!LocateSidecarDir(dir, sizeof(dir)))
    {
        QM_LOG_WARN("[Loot] cannot locate DLL dir - loot module disabled");
        return false;
    }

    // Glob qm_loot_*.json (one per deployed profile).
    char pattern[MAX_PATH];
    if (snprintf(pattern, sizeof(pattern), "%s\\qm_loot_*.json", dir) <= 0)
        return false;

    WIN32_FIND_DATAA fd = {};
    HANDLE h = FindFirstFileA(pattern, &fd);
    int files = 0;
    if (h != INVALID_HANDLE_VALUE)
    {
        do
        {
            if (fd.dwFileAttributes & FILE_ATTRIBUTE_DIRECTORY) continue;
            char full[MAX_PATH];
            int w = snprintf(full, sizeof(full), "%s\\%s", dir, fd.cFileName);
            if (w <= 0 || (size_t)w >= sizeof(full)) continue;
            if (ParseSidecarFile(full))
                ++files;
        } while (FindNextFileA(h, &fd));
        FindClose(h);
    }

    g_armed = (g_tableCount > 0 || g_treeMult != 1.0f || g_digVolumeMult != 1.0f || g_respawnSpeed != 1.0f);
    if (g_armed)
    {
        QM_LOG_INFO("[Loot] *** ARMED *** %d table(s), tree x%.2f, digvol x%.2f, respawn speed x%.2f from %d file(s)",
            g_tableCount, (double)g_treeMult, (double)g_digVolumeMult, (double)g_respawnSpeed, files);
        for (int t = 0; t < g_tableCount; ++t)
        {
            QM_LOG_INFO("[Loot]   [%d] '%s' (%d entries)", t, g_tables[t].assetName, g_tables[t].entryCount);
        }
    }
    else
    {
        QM_LOG_INFO("[Loot] no qm_loot_*.json sidecars (or none with overrides) - loot module idle");
    }

    return g_armed;
}

void QmLoot_Heartbeat()
{
    // Intentionally empty. All scanning is now handled by the incremental
    // early-scan in QmLoot_OnProcessEvent (O(delta) per call, no full scans).
    // The old heartbeat ran 4 full GObjects iterations (~2.4M string compares)
    // every 5 seconds on the game thread, causing visible frame stutters.
}

void QmLoot_OnWorldChanged()
{
    if (!g_armed) return;

    // DataAssets (SegmentTreeData, DigVolumeConfig) persist across world changes.
    // Their tracking is NOT reset - they keep our multiplied values.
    // Only reset LootParams tables (written with absolute values, no cascading risk).
    g_allApplied = false;
    for (int t = 0; t < g_tableCount; ++t)
        g_tables[t].applied = false;

    // Force early-scan on next PE call by resetting the high-water mark.
    // Per-object tracking prevents re-multiplication of already-patched assets.
    g_earlyScanHWM = 0;

    QM_LOG_INFO("[Loot] world changed - re-scanning (tree %d, digvol %d, spawner %d tracked)",
        g_patchedTreeCount, g_patchedDigCount, g_patchedSpawnerCount);
}

// ProcessEvent rider: incremental DataAsset scan.
// Tree actors cache their LootData at spawn time; the regular heartbeat
// (gameplay-map gated) fires too late. ProcessEvent runs during loading
// screens (UI, input), so we piggyback a DataAsset scan here.
//
// Incremental: we track a high-water mark (HWM) of the last scanned
// GObjects index. Each PE call only scans objects [HWM, current_count) -
// the NEW objects since the last scan. During gameplay this is typically
// 0-10 objects (particles, FX), so the cost is negligible. During loading
// we catch DataAssets within microseconds of them appearing in GObjects.
// No throttle needed because work is proportional to new objects only.
void QmLoot_OnProcessEvent(void* /*self*/, void* /*func*/, void* /*parms*/)
{
    if (!g_armed || !QmUE::IsReady()) return;

    // Fast path: single int read + comparison.
    QmUE::TUObjectArray* arr = QmUE::GetGObjects();
    int currentNum = arr->Num();
    if (currentNum <= g_earlyScanHWM) return;

    int scanFrom = g_earlyScanHWM;
    g_earlyScanHWM = currentNum;

    __try
    {
        // LootParams tables (absolute values, one-shot)
        if (g_tableCount > 0 && !g_allApplied)
            ScanAndPatch(scanFrom);

        int prevT = g_patchedTreeCount;
        int prevD = g_patchedDigCount;
        int prevS = g_patchedSpawnerCount;
        ScanAndPatchTrees(scanFrom);
        ScanAndPatchDigVolumes(scanFrom);
        ScanAndPatchSpawnerParams(scanFrom);
        int newT = g_patchedTreeCount    - prevT;
        int newD = g_patchedDigCount     - prevD;
        int newS = g_patchedSpawnerCount - prevS;

        if (newT > 0 || newD > 0 || newS > 0)
        {
            QM_LOG_INFO("[Loot] early-scan: %d tree(s), %d digvol(s), %d spawner(s) patched (range %d..%d)",
                newT, newD, newS, scanFrom, currentNum);
        }
    }
    __except (EXCEPTION_EXECUTE_HANDLER)
    {
        QM_LOG_WARN("[Loot] early-scan SEH fault (range %d..%d)", scanFrom, currentNum);
    }
}

bool QmLoot_IsArmed() { return g_armed; }
