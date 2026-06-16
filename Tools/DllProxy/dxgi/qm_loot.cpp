// Quartermaster runtime loot patcher (binary DataAssets). See qm_loot.hpp.

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

    // --- (1) UR5SegmentTreeData : UDataAsset (0x04B0 total) ------------------
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

    // EObjectFlags used to reject CDOs/archetypes.
    constexpr uint32_t RF_ClassDefaultObject = 0x00000010;
    constexpr uint32_t RF_ArchetypeObject    = 0x00000020;

    bool g_armed         = false;

    // Multipliers read from sidecar JSON (__tree_mult / __digvolume_mult)
    float g_treeMult      = 1.0f;
    float g_digVolumeMult = 1.0f;

    // Per-UObject tracking: DataAssets persist across world changes, so we
    // track patched pointers to avoid cascading re-multiplication (3->9->27).
    constexpr int kMaxTracked = 1024;
    void* g_patchedTreePtrs[kMaxTracked] = {};
    int   g_patchedTreeCount = 0;
    void* g_patchedDigPtrs[kMaxTracked]  = {};
    int   g_patchedDigCount  = 0;

    bool IsObjTracked(void* const* arr, int count, void* p)
    {
        for (int i = 0; i < count; ++i) if (arr[i] == p) return true;
        return false;
    }
    bool TrackObj(void** arr, int& count, void* p)
    {
        if (count < kMaxTracked) { arr[count++] = p; return true; }
        QM_LOG_WARN("[Loot] tracking overflow (%d) - cascading risk!", kMaxTracked);
        return false;
    }

    // Early-scan: patch DataAssets DURING loading (via ProcessEvent rider)
    // so tree actors read our multiplied values when they spawn.
    // No convergence - scans whenever GObjects grows (new objects loaded).
    int g_earlyScanHWM = 0; // high-water mark: last scanned GObjects index

    // ---- JSON parsing -------------------------------------------------------
    // Format: { "__tree_mult": 2.0, "__digvolume_mult": 2.0 }
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
            std::string key;
            if (!jp.parseString(key)) break;
            if (!jp.expect(':')) break;

            double v = 0;
            if (key == "__tree_mult" && jp.parseNumber(v))
                g_treeMult = (float)v;
            else if (key == "__digvolume_mult" && jp.parseNumber(v))
                g_digVolumeMult = (float)v;
            else
                jp.skipValue();

            if (jp.peek(',')) ++jp.p;
        }

        return true;
    }

    // ---- GObjects scan + patch ----------------------------------------------
    bool IsCdoOrArchetype(const QmUE::UObject* o)
    {
        return o && (o->Flags & (RF_ClassDefaultObject | RF_ArchetypeObject)) != 0;
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

    // (1) Scan UR5SegmentTreeData UObjects and multiply drop amounts.
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

    // (2) Scan UR5DigVolumeConfig UObjects and multiply drop amounts.
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

}

bool QmLoot_Init()
{
    g_armed         = false;
    g_treeMult      = 1.0f;
    g_digVolumeMult = 1.0f;
    g_patchedTreeCount    = 0;
    g_patchedDigCount     = 0;
    memset(g_patchedTreePtrs, 0, sizeof(g_patchedTreePtrs));
    memset(g_patchedDigPtrs, 0, sizeof(g_patchedDigPtrs));
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

    g_armed = (g_treeMult != 1.0f || g_digVolumeMult != 1.0f);
    if (g_armed)
    {
        QM_LOG_INFO("[Loot] *** ARMED *** tree x%.2f, digvol x%.2f from %d file(s)",
            (double)g_treeMult, (double)g_digVolumeMult, files);
    }
    else
    {
        QM_LOG_INFO("[Loot] no qm_loot_*.json sidecars (or no multipliers) - loot module idle");
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
    // Force early-scan on next PE call by resetting the high-water mark.
    // Per-object tracking prevents re-multiplication of already-patched assets.
    g_earlyScanHWM = 0;

    QM_LOG_INFO("[Loot] world changed - re-scanning (tree %d, digvol %d tracked)",
        g_patchedTreeCount, g_patchedDigCount);
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
        int prevT = g_patchedTreeCount;
        int prevD = g_patchedDigCount;
        ScanAndPatchTrees(scanFrom);
        ScanAndPatchDigVolumes(scanFrom);
        int newT = g_patchedTreeCount - prevT;
        int newD = g_patchedDigCount  - prevD;

        if (newT > 0 || newD > 0)
        {
            QM_LOG_INFO("[Loot] early-scan: %d tree(s), %d digvol(s) patched (range %d..%d)",
                newT, newD, scanFrom, currentNum);
        }
    }
    __except (EXCEPTION_EXECUTE_HANDLER)
    {
        QM_LOG_WARN("[Loot] early-scan SEH fault (range %d..%d)", scanFrom, currentNum);
    }
}

bool QmLoot_IsArmed() { return g_armed; }
