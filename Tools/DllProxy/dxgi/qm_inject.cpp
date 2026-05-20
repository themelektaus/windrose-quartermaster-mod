// Quartermaster inject pipeline - impl. See qm_inject.hpp for the contract.

#define _CRT_SECURE_NO_WARNINGS
#include <windows.h>
#include <stdint.h>
#include <stdio.h>
#include <string.h>

#include "qm_ue.hpp"
#include "qm_state.hpp"
#include "qm_log.hpp"
#include "qm_config.hpp"
#include "qm_inject.hpp"
#include "qm_alloc.hpp"

// ============================================================================
// Module-level config.
// ============================================================================
const int kSpawnedPoolMax = 16;

// ============================================================================
// Module-private state.
// ============================================================================

// ---- Donor (captured once on first hit) -----------------------------------
static void*    g_donorItem            = nullptr;
static void*    g_donorSourceGroup     = nullptr;
static char     g_donorAssetName[128]  = "<?>";

// ---- Spawned widget pool --------------------------------------------------
// Each entry remembers which item the widget was customized for and the last
// target-group it was injected into. On re-entry we prefer reusing an entry
// whose widget belongs to the requested item and whose lastGroup differs from
// the current target-group - that's a widget that the previous (now-stale)
// group still references but the live UI no longer renders. Reusing it lets
// the pool size stay tiny (typically == g_injectableItemCount) regardless of
// how often the Build menu is reopened. Spawning a fresh widget every time
// would grow the pool unbounded and hit the kSpawnedPoolMax ceiling.
struct PoolEntry
{
    QmUE::UObject* widget;
    int            itemIdx;
    void*          lastGroup;
    long           reuseCount;  // diagnostic only
};
static PoolEntry        g_spawnedPool[16] = {};
static int              g_spawnedPoolCount    = 0;
static int              g_spawnedPoolNextSlot = 0;   // circular write cursor (used in force-fresh mode)
static QmUE::UClass*    g_itemWidgetClass     = nullptr;
static volatile LONG    g_spawnAttempts       = 0;
static volatile LONG    g_spawnSuccesses      = 0;
static volatile LONG    g_spawnReuses         = 0;

// ============================================================================
// FORCE-FRESH-SPAWN toggle (Iteration 2 - 2026-05-20).
// ============================================================================
// Observation: 2nd+ Build-menu visits cause item[0] (Painting) to take the
// pool-REUSE path (no SpawnObject UFunction call). Combined with the heavy
// FMalloc-TLS dependency of FMallocBinned3, that means the 64-byte bin pool
// never gets a real warm allocation in the detour-callback context - and our
// subsequent TArray-grow (GMalloc::Realloc) AVs at faultAddr=0x10 on all
// three fallback paths (hot/cold-align16/cold-align0).
//
// In Hit#2 (1st visit) item[0] was *freshly* spawned (pool empty), and the
// 64-byte pool came up warm enough that Realloc(nullptr,0) succeeded - the
// single observed difference between "works" and "fails" was that one extra
// real SpawnObject call before Realloc.
//
// Iteration 2 forces every inject to use the SpawnObject path - even when a
// pool widget for the same itemIdx is technically reusable. The pool becomes
// circular: when full, oldest entries are overwritten and their widgets are
// silently orphaned (UE GC reclaims them within seconds since no live UObject
// holds a reference). Memory cost: ~K spawned widgets per Build-menu visit
// until GC, bounded by frequency of Build-menu reopenings.
//
// If 6 sequential fresh SpawnObject calls per visit (1 real item[0] + 1 real
// item[1] + 5 sacrificial) still don't reach the 64-byte pool warm-up
// threshold, the TLS-warmup hypothesis is incorrect and we need a structural
// change (Option B: Replace-statt-Grow, or Option C: own Group).
static constexpr bool kForceFreshSpawn = true;

// ---- Override targets (one per InjectableItem) ----------------------------
// Index parallels g_injectableItems[]. Resolved lazily on first use, cached.
// We cap at a generous 64 to avoid VLAs; current item count is 2.
struct OverrideTarget
{
    bool         resolved;
    QmUE::FName  assetName;
    QmUE::FName  packageName;
};
static const int       kMaxOverrideTargets   = 64;
static OverrideTarget  g_overrideTargets[kMaxOverrideTargets] = {};
static volatile LONG   g_overrideLookupAttempts = 0;
static volatile LONG   g_overrideApplied      = 0;

// ---- Hit + inject counters ------------------------------------------------
static volatile LONG   g_hookHits             = 0;
static volatile LONG   g_foreignInjectsDone   = 0;
static volatile LONG   g_foreignAlreadyPresent = 0;
static volatile LONG   g_foreignSkippedCategory = 0;

// ============================================================================
// Public snapshot for crash handler + state-log line.
// ============================================================================
QmInjectSnapshot QmGetInjectSnapshot()
{
    QmInjectSnapshot s;
    s.hookHits                = g_hookHits;
    s.injectsDone             = g_foreignInjectsDone;
    s.alreadyPresent          = g_foreignAlreadyPresent;
    s.donorItem               = g_donorItem;
    s.donorSourceGroup        = g_donorSourceGroup;
    s.spawnedPoolCount        = g_spawnedPoolCount;
    s.spawnAttempts           = g_spawnAttempts;
    s.spawnSuccesses          = g_spawnSuccesses;
    s.spawnReuses             = g_spawnReuses;
    s.overrideApplied         = g_overrideApplied;
    s.overrideLookupAttempts  = g_overrideLookupAttempts;
    s.overridesResolvedCount  = QmCountOverridesResolved();
    s.skippedCategory         = g_foreignSkippedCategory;
    s.donorAssetName          = g_donorAssetName;
    return s;
}

long QmBumpHookHits()
{
    return InterlockedIncrement(&g_hookHits);
}

// ============================================================================
// Override-target FName resolution (KismetStringLibrary::Conv_StringToName).
// One target per item, resolved lazily.
// ============================================================================
bool QmIsOverrideResolved(int itemIdx)
{
    if (itemIdx < 0 || itemIdx >= g_injectableItemCount || itemIdx >= kMaxOverrideTargets)
        return false;
    return g_overrideTargets[itemIdx].resolved;
}

bool QmGetOverrideTarget(int itemIdx, QmUE::FName* pkgOut, QmUE::FName* assetOut)
{
    if (itemIdx < 0 || itemIdx >= g_injectableItemCount || itemIdx >= kMaxOverrideTargets)
        return false;
    if (!g_overrideTargets[itemIdx].resolved) return false;
    if (pkgOut)   *pkgOut   = g_overrideTargets[itemIdx].packageName;
    if (assetOut) *assetOut = g_overrideTargets[itemIdx].assetName;
    return true;
}

int QmCountOverridesResolved()
{
    int n = 0;
    int cap = g_injectableItemCount < kMaxOverrideTargets ? g_injectableItemCount : kMaxOverrideTargets;
    for (int i = 0; i < cap; ++i)
        if (g_overrideTargets[i].resolved) ++n;
    return n;
}

static bool ResolveOverrideTarget(int itemIdx)
{
    if (itemIdx < 0 || itemIdx >= g_injectableItemCount || itemIdx >= kMaxOverrideTargets)
        return false;
    OverrideTarget& tgt = g_overrideTargets[itemIdx];
    if (tgt.resolved) return true;

    const InjectableItem& item = g_injectableItems[itemIdx];

    long attempt = InterlockedIncrement(&g_overrideLookupAttempts);

    QmUE::FName pkgName = {0, 0};
    QmUE::FName assetName = {0, 0};

    if (!QmUE::FNameFromString(item.packagePathW, &pkgName))
    {
        if (attempt == 1 || (attempt % 5) == 0)
            QM_LOG_WARN("[Override] FNameFromString FAILED for Pkg='%ls' item='%s' (attempt#%ld)",
                item.packagePathW, item.name, attempt);
        return false;
    }
    if (!QmUE::FNameFromString(item.assetNameW, &assetName))
    {
        if (attempt == 1 || (attempt % 5) == 0)
            QM_LOG_WARN("[Override] FNameFromString FAILED for Asset='%ls' item='%s' (attempt#%ld)",
                item.assetNameW, item.name, attempt);
        return false;
    }

    tgt.resolved    = true;
    tgt.assetName   = assetName;
    tgt.packageName = pkgName;

    // Round-trip-verify so the log shows what the FName pool actually interned.
    char pkgBuf[256] = "<?>";
    char assetBuf[128] = "<?>";
    QmUE::ResolveFNameNarrow(pkgName,   pkgBuf,   sizeof(pkgBuf));
    QmUE::ResolveFNameNarrow(assetName, assetBuf, sizeof(assetBuf));
    QM_LOG_INFO("[Override] *** RESOLVED *** item[%d]='%s' Pkg='%s' (cmp=%d num=%u) Asset='%s' (cmp=%d num=%u)",
        itemIdx, item.name,
        pkgBuf,   pkgName.ComparisonIndex,   pkgName.Number,
        assetBuf, assetName.ComparisonIndex, assetName.Number);
    return true;
}

// Apply the resolved override to a spawned widget. Returns true on success.
static bool ApplyOverrideToSpawned(QmUE::UObject* spawned, int itemIdx)
{
    if (!spawned) return false;
    if (itemIdx < 0 || itemIdx >= g_injectableItemCount) return false;
    OverrideTarget& tgt = g_overrideTargets[itemIdx];
    if (!tgt.resolved) return false;

    bool ok = false;
    __try
    {
        uint8_t* base = reinterpret_cast<uint8_t*>(spawned);
        *reinterpret_cast<QmUE::FName*>(base + ItemDataLayout::kPackageName) = tgt.packageName;
        *reinterpret_cast<QmUE::FName*>(base + ItemDataLayout::kAssetName)   = tgt.assetName;
        *reinterpret_cast<int32_t*>(base + ItemDataLayout::kWeakPtr)         = 0;
        *reinterpret_cast<int32_t*>(base + ItemDataLayout::kWeakPtr + 4)     = 0;
        ok = true;
    }
    __except (EXCEPTION_EXECUTE_HANDLER) { ok = false; }

    if (ok)
    {
        InterlockedIncrement(&g_overrideApplied);
        QM_LOG_INFO("[Override] APPLIED item[%d]='%s' -> spawned=0x%p (Pkg+Asset rewritten, WeakPtr zeroed)",
            itemIdx, g_injectableItems[itemIdx].name, spawned);
    }
    else
        QM_LOG_WARN("[Override] FAULT during write to spawned=0x%p item[%d]='%s'",
            spawned, itemIdx, g_injectableItems[itemIdx].name);
    return ok;
}

// ============================================================================
// Donor-class validation. The Build-Menu uses 'WBP_Building_Item_C' widgets in
// every decoration/utility group. Other widget classes (e.g. 'WBP_Shortcut_C'
// used in the bottom-row hotbar) live at recyclable UObject addresses - if the
// player builds with a custom item once and the donor's old WBP_Building_Item_C
// gets GC'd, the same address can be reused for a WBP_Shortcut_C the next time
// the build menu opens. Cloning that as a building-group item causes a
// class-mismatch crash a few frames later. Guard via class-name substring.
// ============================================================================
static const char* kExpectedDonorClassSubstr = "Building_Item";

static bool IsDonorClassValid(QmUE::UObject* candidate, char* clsNameOut, size_t clsNameOutSz)
{
    if (clsNameOut && clsNameOutSz) clsNameOut[0] = 0;
    if (!candidate) return false;

    QmUE::UClass* cls = nullptr;
    __try { cls = candidate->Class; }
    __except (EXCEPTION_EXECUTE_HANDLER) { cls = nullptr; }
    if (!cls) return false;

    char buf[128] = "<?>";
    bool resolved = false;
    __try { resolved = QmUE::ResolveFNameNarrow(cls->Name, buf, sizeof(buf)); }
    __except (EXCEPTION_EXECUTE_HANDLER) { resolved = false; }

    if (clsNameOut && clsNameOutSz) {
        strncpy(clsNameOut, buf, clsNameOutSz - 1);
        clsNameOut[clsNameOutSz - 1] = 0;
    }
    if (!resolved) return false;
    return strstr(buf, kExpectedDonorClassSubstr) != nullptr;
}

// ============================================================================
// Warm up FMalloc by triggering MULTIPLE SpawnObject calls. UE's allocator
// appears to require N successive UE-level allocation events (via reflection
// VM / SpawnObject path) on this thread before FMalloc::Realloc takes the hot
// path. At hit#2 (which worked) two SpawnObjects ran before Realloc (item[0]
// real-spawn + sacrificial). At hit#4+ (broken) only ONE ran (sacrificial
// only, item[0] came from pool-REUSE).
//
// Iteration 1 attempt: brute-force 5 sacrificial spawns per Realloc to push
// the SpawnObject count well above the threshold. Each spawned widget is
// INTENTIONALLY ORPHANED (not put into the pool). UE GC reclaims them.
// Cost per Realloc: ~5 UObjects (few KB each) until GC sweep.
//
// Returns count of successful spawns.
// ============================================================================
static volatile LONG g_warmupSpawns = 0;
static int WarmUpAllocatorViaSpawn(QmUE::UClass* cls, QmUE::UObject* outer, const char* contextTag, int count)
{
    if (!cls || !outer) return 0;
    if (count <= 0) count = 1;
    int ok = 0;
    QmUE::UObject* lastSpawn = nullptr;
    for (int i = 0; i < count; ++i)
    {
        QmUE::UObject* throwaway = nullptr;
        __try { throwaway = QmUE::SpawnObjectViaUFunction(cls, outer); }
        __except (EXCEPTION_EXECUTE_HANDLER) { throwaway = nullptr; }
        if (throwaway) { lastSpawn = throwaway; ok++; }
    }
    if (ok == 0)
    {
        QM_LOG_WARN("[Warmup] SpawnObjectViaUFunction returned null (ctx=%s cls=0x%p outer=0x%p req=%d)",
            contextTag ? contextTag : "?", cls, outer, count);
        return 0;
    }
    InterlockedExchangeAdd(&g_warmupSpawns, ok);
    QM_LOG_INFO("[Warmup] sacrificial spawn series ok=%d/%d lastWidget=0x%p (ctx=%s cls=0x%p outer=0x%p totalEver=%ld) - warming FMalloc TLS before TArray-grow",
        ok, count, lastSpawn, contextTag ? contextTag : "?", cls, outer, g_warmupSpawns);
    return ok;
}

// ============================================================================
// Warm up FMalloc by spamming AppendString (FName -> FString) lookups.
//
// EMPIRICAL DISCOVERY: at hit#2 (the only hit where Realloc Path 3 worked) the
// ClassDump diagnostic ran ~200 ResolveFNameNarrow calls between AllocWarmup
// and Realloc. At hit#4 the ClassDump was already done (atomic-once), so no
// AppendString activity preceded Realloc - and ALL 3 Realloc paths failed.
//
// Hypothesis: FMallocBinned3's per-thread per-pool TLS caches are populated
// lazily by real UE allocator activity. AppendString internally invokes
// TArray<TCHAR>::Reserve which goes through GMalloc->Realloc on the appropriate
// pool. Spamming long-name lookups warms the 64-byte (and adjacent) bin caches
// on this thread, so our subsequent Realloc(64,16) call hits hot TLS.
//
// We pull the source FName from the donor item (its Name is always a long
// WBP_Building_Item_C_<unique-id> string, well over the inline-string limit),
// which guarantees AppendString takes the heap-allocation path.
//
// Buffer is on the stack; if AppendString reallocs Data (when its initial size
// is exceeded), we leak the resulting heap buffer per call. That's fine - we
// WANT the heap allocation here, that's literally the warmup mechanism.
// ============================================================================
static volatile LONG g_warmupFNameRuns  = 0;
static volatile LONG g_warmupFNameCalls = 0;

static int WarmUpFMallocViaFNameLookups(QmUE::UObject* sourceObject, int iterations, const char* contextTag)
{
    if (!sourceObject) return 0;

    QmUE::FName fname = {0, 0};
    __try { fname = sourceObject->Name; }
    __except (EXCEPTION_EXECUTE_HANDLER) { fname = {0, 0}; }
    if (fname.ComparisonIndex == 0) return 0;

    int successCount = 0;
    char nameBuf[512];

    for (int i = 0; i < iterations; ++i)
    {
        nameBuf[0] = '\0';
        bool ok = false;
        __try
        {
            ok = QmUE::ResolveFNameNarrow(fname, nameBuf, sizeof(nameBuf));
        }
        __except (EXCEPTION_EXECUTE_HANDLER) { ok = false; }
        if (ok) successCount++;
        InterlockedIncrement(&g_warmupFNameCalls);
    }

    long runs = InterlockedIncrement(&g_warmupFNameRuns);
    QM_LOG_INFO("[Warmup] FName-lookup spam ok: %d/%d resolved (ctx=%s sourceObj=0x%p name='%s' run#%ld totalCalls=%ld) - warming FMalloc 64-byte pool TLS",
        successCount, iterations, contextTag ? contextTag : "?", sourceObject,
        nameBuf[0] ? nameBuf : "<?>", runs, g_warmupFNameCalls);
    return successCount;
}

// ============================================================================
// Pool reuse: find an existing widget for the same itemIdx whose lastGroup
// differs from currentGroup. That widget is owned by a previous (no-longer
// rendered) group - reusing it both avoids spawning N widgets per session and
// keeps the kSpawnedPoolMax ceiling out of reach. The widget's class is
// re-validated before reuse to guard against UE recycling the address.
//
// Returns the pool index of a reusable widget, or -1 if none qualifies.
// ============================================================================
static int FindReusablePoolEntry(int itemIdx, void* currentGroup)
{
    for (int i = 0; i < g_spawnedPoolCount; ++i)
    {
        PoolEntry& e = g_spawnedPool[i];
        if (!e.widget) continue;
        if (e.itemIdx != itemIdx) continue;
        if (e.lastGroup == currentGroup) continue;  // already in this group
        if (!IsDonorClassValid(e.widget, nullptr, 0)) continue;  // class stale
        return i;
    }
    return -1;
}

// ============================================================================
// Spawn-or-reuse a widget for `itemIdx`. Reuse path: previously-spawned widget
// taken from the pool, override re-applied (idempotent). Spawn path: fresh
// UObject created via UFunction, ItemData cloned from donor, override applied.
// The currentGroup pointer is recorded on the pool entry so subsequent reuse
// can avoid placing the same widget into the same group twice.
// ============================================================================
static QmUE::UObject* SpawnOrReuseItemWithOverride(QmUE::UObject* donor, int itemIdx,
                                                   void* currentGroup, const char* contextTag)
{
    if (!donor) return nullptr;
    if (itemIdx < 0 || itemIdx >= g_injectableItemCount) return nullptr;
    const InjectableItem& item = g_injectableItems[itemIdx];

    // ----- Reuse path -----------------------------------------------------
    // Iteration 2: pool-REUSE is DISABLED when kForceFreshSpawn is true. The
    // SpawnObject UFunction call is the only path that appears to warm the
    // FMalloc 64-byte TLS pool enough that the subsequent TArray-grow Realloc
    // succeeds. Reuse skips that call (just hands back a stored pointer), so
    // 2nd+ visits fail at Realloc. See kForceFreshSpawn comment above.
    if (!kForceFreshSpawn)
    {
        int reuseIdx = FindReusablePoolEntry(itemIdx, currentGroup);
        if (reuseIdx >= 0)
        {
            PoolEntry& e = g_spawnedPool[reuseIdx];
            QmUE::UObject* reused = e.widget;
            void* oldGroup = e.lastGroup;
            e.lastGroup = currentGroup;
            e.reuseCount++;
            InterlockedIncrement(&g_spawnReuses);

            // Re-apply override (idempotent) in case anything mutated the
            // Pkg/Asset slots while the widget sat in the previous group.
            if (ResolveOverrideTarget(itemIdx))
                ApplyOverrideToSpawned(reused, itemIdx);

            QM_LOG_INFO("[Spawn] *** REUSE *** widget=0x%p ctx=%s item[%d]='%s' poolIdx=%d reuseCount=%ld oldGroup=0x%p newGroup=0x%p",
                reused, contextTag ? contextTag : "?", itemIdx, item.name,
                reuseIdx, e.reuseCount, oldGroup, currentGroup);
            return reused;
        }
    }

    // ----- Pool full guard (only matters for fresh spawns) ----------------
    // In force-fresh mode the pool acts as a circular buffer: when full, we
    // overwrite the oldest slot. The orphaned widget loses its only ref and
    // UE GC reclaims it within a few sweeps. In normal (reuse) mode we keep
    // the existing hard-fail.
    if (g_spawnedPoolCount >= kSpawnedPoolMax && !kForceFreshSpawn)
    {
        QM_LOG_WARN("[Spawn] pool full (%d) - refusing to spawn more (ctx=%s item='%s')",
            g_spawnedPoolCount, contextTag ? contextTag : "?", item.name);
        return nullptr;
    }

    // Stale-donor detection: the cached g_donorItem may point to recycled
    // memory now backing a different widget class (e.g. WBP_Shortcut_C). If so,
    // clear it so the next hit re-captures a fresh, valid donor. Spawning the
    // wrong class into a decoration group causes a class-mismatch crash.
    char itemClsName[128] = "<?>";
    if (!IsDonorClassValid(donor, itemClsName, sizeof(itemClsName)))
    {
        QM_LOG_WARN("[Spawn] STALE-DONOR detected: donor=0x%p class='%s' does not match '%s' - clearing g_donorItem (ctx=%s item='%s')",
            donor, itemClsName, kExpectedDonorClassSubstr,
            contextTag ? contextTag : "?", item.name);
        if (donor == g_donorItem) { g_donorItem = nullptr; g_donorSourceGroup = nullptr; }
        return nullptr;
    }

    QmUE::UClass* itemCls = nullptr;
    QmUE::UObject* outer = nullptr;
    __try { itemCls = donor->Class; outer = donor->Outer; }
    __except (EXCEPTION_EXECUTE_HANDLER) { itemCls = nullptr; outer = nullptr; }

    if (!itemCls)
    {
        QM_LOG_WARN("[Spawn] FAULT reading donor->Class/Outer for donor=0x%p (ctx=%s item='%s')",
            donor, contextTag ? contextTag : "?", item.name);
        return nullptr;
    }

    InterlockedIncrement(&g_spawnAttempts);
    g_itemWidgetClass = itemCls;

    QmUE::UObject* spawned = QmUE::SpawnObjectViaUFunction(itemCls, outer);
    if (!spawned)
    {
        QM_LOG_WARN("[Spawn] SpawnObjectViaUFunction returned null (Cls=%s @0x%p outer=0x%p ctx=%s item='%s')",
            itemClsName, itemCls, outer, contextTag ? contextTag : "?", item.name);
        return nullptr;
    }

    bool copyOK = false;
    __try
    {
        uint8_t* src = reinterpret_cast<uint8_t*>(donor)   + ItemDataLayout::kItemData;
        uint8_t* dst = reinterpret_cast<uint8_t*>(spawned) + ItemDataLayout::kItemData;
        memcpy(dst, src, ItemDataLayout::kSize);
        copyOK = true;
    }
    __except (EXCEPTION_EXECUTE_HANDLER) { copyOK = false; }

    if (!copyOK)
        QM_LOG_WARN("[Spawn] FAULT during ItemData memcpy donor=0x%p -> spawned=0x%p", donor, spawned);

    // Apply override immediately if resolved. Cheap (cached after first hit).
    if (ResolveOverrideTarget(itemIdx))
        ApplyOverrideToSpawned(spawned, itemIdx);

    // Pool insertion: in normal mode append to next free slot. In force-fresh
    // mode use circular write (overwrite oldest entry) so we never hit the
    // full-pool ceiling - the orphaned widget loses its only reference and
    // gets reclaimed by UE GC within a few sweeps.
    void* orphanedWidget = nullptr;
    if (kForceFreshSpawn)
    {
        int slot = g_spawnedPoolNextSlot;
        if (slot < 0 || slot >= kSpawnedPoolMax) slot = 0;
        orphanedWidget = g_spawnedPool[slot].widget;  // may be null on first wrap
        PoolEntry& e = g_spawnedPool[slot];
        e.widget     = spawned;
        e.itemIdx    = itemIdx;
        e.lastGroup  = currentGroup;
        e.reuseCount = 0;
        g_spawnedPoolNextSlot = (slot + 1) % kSpawnedPoolMax;
        if (g_spawnedPoolCount < kSpawnedPoolMax) g_spawnedPoolCount++;
    }
    else if (g_spawnedPoolCount < kSpawnedPoolMax)
    {
        PoolEntry& e = g_spawnedPool[g_spawnedPoolCount++];
        e.widget     = spawned;
        e.itemIdx    = itemIdx;
        e.lastGroup  = currentGroup;
        e.reuseCount = 0;
    }
    InterlockedIncrement(&g_spawnSuccesses);
    QM_LOG_INFO("[Spawn] *** SUCCESS *** %s spawned @ 0x%p (outer=0x%p) ItemData=%s ctx=%s item[%d]='%s' pool=%d group=0x%p%s",
        itemClsName, spawned, outer, copyOK ? "cloned-from-donor" : "FAULT",
        contextTag ? contextTag : "?", itemIdx, item.name, g_spawnedPoolCount, currentGroup,
        orphanedWidget ? " [forced-fresh, orphaned prior pool entry]" : "");

    return spawned;
}

// ============================================================================
// VARIANTE B: Custom group widget with DLL-static Items buffer.
// ============================================================================
// Strategy: instead of growing the existing group's Items TArray (which AVs
// inside FMallocBinned3 on 2nd+ tab visits due to per-thread TLS state), or
// spawning a parallel custom group (which renders double because UMG sub-
// slots are shared), we DIRECTLY OVERWRITE the vanilla group's Items.Data
// pointer with a DLL-static buffer that contains BOTH the existing vanilla
// items AND our custom items. No FMalloc::Realloc on our side, single group
// in the UI with all items mixed in.
//
// Step-by-step:
//   1. Read group0 = outer.Data[0] (the vanilla group we're hijacking)
//   2. Read its existing Items.Data array (N vanilla item pointers)
//   3. Pick next static buffer slot, copy N vanilla pointers into slots [0..N-1]
//   4. SpawnObjectViaUFunction for each of our custom items, write into [N..N+K-1]
//   5. Overwrite group0.Items.Data = static buffer, Num = N+K, Max = 16
//   6. Outer Result array unchanged - one group, with all items
//
// Trade-offs:
//   - The OLD vanilla Items.Data buffer is orphaned (FMalloc-allocated). Per-
//     visit memory leak of ~32-64 bytes. UE may eventually GC the original
//     reference path and free it, but we can't guarantee. Acceptable.
//   - On GC of the vanilla group, TArray::~TArray calls FMalloc::Free on our
//     static buffer. FMallocBinned3 should detect "address not in any bin" and
//     either silently ignore (release) or assert (debug). Same risk profile as
//     the previous custom-group approach which was observed stable under
//     stress-testing.
//   - If UE tries to grow Items[] at runtime (Realloc on our static buffer),
//     would be catastrophic. But BuildingBrushes tab is read-only display -
//     items aren't modified after CreateTabsData returns.
//
// To minimize double-free risk on rotation: rotate static buffer slots. With
// N slots and human-speed visits, GC reclaims the previous group before we
// wrap around. Each visit gets a fresh group instance from UE (different
// outer.Data[0] pointer each hit per logs).
//
// Backs off (returns 0, falls through to per-group inject) when:
//   - Outer Result array empty or unreadable
//   - Vanilla group0 unreadable
//   - Already swapped (Items.Data points into our static buffer pool) - prevents
//     duplicate injection on repeated calls within the same hit
// ============================================================================

// Per-buffer capacity = max combined items per group (vanilla + custom).
// Buffer slot count = how many in-flight groups can coexist before recycling.
// MUST match QmAlloc::Resolve()'s ReserveBuffers call (count=8, bytes=128).
constexpr int kCustomGroupItemMax     = 16;
constexpr int kCustomGroupBufferSlots = 8;

// Rotation cursor for picking which reserved buffer slot to use on the next
// ItemSwap. Buffers themselves live in QmAlloc - allocated by InnerMalloc
// (bypassing FMallocProxy) so FMallocBinned2's canary check accepts them on
// later Free/Realloc from the engine.
static volatile LONG     g_customGroupBufferNextSlot      = 0;

// Diagnostic counters.
static volatile LONG     g_itemSwapApplies                = 0;
static volatile LONG     g_itemSwapAlreadySwapped         = 0;
static volatile LONG     g_itemSwapSkipsNoOuter           = 0;
static volatile LONG     g_itemSwapSkipsNoItems           = 0;
static volatile LONG     g_itemSwapSkipsNoBuffer          = 0;
static volatile LONG     g_itemSwapSkipsVanillaOverflow   = 0;
static volatile LONG     g_itemSwapGcRestores             = 0;
static volatile LONG     g_itemSwapGcRestoreNotOurs       = 0;
static volatile LONG     g_itemSwapGcRestoreFaults        = 0;
static volatile LONG     g_itemSwapStandaloneSet          = 0;
static volatile LONG     g_itemSwapStandaloneFault        = 0;

// EObjectFlags::RF_Standalone keeps an UObject alive past GC. We set it on
// each group whose Items.Data we swapped, so the group widget survives garbage
// collection until we explicitly restore it.
constexpr uint32_t kRF_Standalone = 0x00000002u;

// Helper: is `ptr` one of our QmAlloc-reserved buffers?
static bool IsOurReservedBuffer(void* ptr)
{
    if (!ptr) return false;
    int n = QmAlloc::GetReservedBufferCount();
    for (int i = 0; i < n; ++i)
        if (QmAlloc::GetReservedBuffer(i) == ptr) return true;
    return false;
}

// ----- Per-slot bookkeeping for GC-safe cleanup --------------------------
// Each swap records (groupWidget, buffer) at the slot we used. Before reusing
// the slot (8 hits later, when the ring buffer wraps), we restore the previous
// tenant: null its Items header so its TArray dtor calls Free(null) instead of
// Free(staticBuffer), and clear RF_Standalone so GC can eventually reclaim it.
struct PrevSwapEntry
{
    void* groupWidget;
    void* ourBuffer;
    bool  inUse;
};
static PrevSwapEntry g_prevSwaps[kCustomGroupBufferSlots] = {};
static SRWLOCK       g_prevSwapsLock = SRWLOCK_INIT;

// Restore the previously-installed swap at this slot. Safe to call even if the
// group widget has been freed (SEH-guarded read+write). Idempotent.
static void RestorePrevSwapAtSlot(int slotIdx)
{
    if (slotIdx < 0 || slotIdx >= kCustomGroupBufferSlots) return;

    PrevSwapEntry entry = {};
    AcquireSRWLockExclusive(&g_prevSwapsLock);
    entry = g_prevSwaps[slotIdx];
    g_prevSwaps[slotIdx] = {};
    ReleaseSRWLockExclusive(&g_prevSwapsLock);

    if (!entry.inUse || !entry.groupWidget) return;

    void* gw          = entry.groupWidget;
    void* expectedBuf = entry.ourBuffer;
    bool  restoredItems    = false;
    bool  bufferStillOurs  = false;

    __try
    {
        QmUE::FTArrayHeader* items = reinterpret_cast<QmUE::FTArrayHeader*>(
            reinterpret_cast<uint8_t*>(gw) + kBuildingItemsOffset);
        if (items->Data == expectedBuf)
        {
            bufferStillOurs = true;
            items->Data = nullptr;
            items->Num  = 0;
            items->Max  = 0;
            QmUE::UObject* obj = reinterpret_cast<QmUE::UObject*>(gw);
            obj->Flags &= ~kRF_Standalone;
            restoredItems = true;
        }
    }
    __except (EXCEPTION_EXECUTE_HANDLER)
    {
        InterlockedIncrement(&g_itemSwapGcRestoreFaults);
        return;
    }

    if (restoredItems)
    {
        long n = InterlockedIncrement(&g_itemSwapGcRestores);
        if (n <= 5 || (n % 25) == 0)
            QM_LOG_INFO("[ItemSwap] cleanup slot=%d group=0x%p Items.Data %p->null, cleared RF_Standalone (restore#%ld)",
                slotIdx, gw, expectedBuf, n);
    }
    else if (!bufferStillOurs)
    {
        long n = InterlockedIncrement(&g_itemSwapGcRestoreNotOurs);
        if (n <= 5)
            QM_LOG_INFO("[ItemSwap] cleanup slot=%d group=0x%p Items.Data no longer ours (already moved/freed) - skipped",
                slotIdx, gw);
    }
}

// ============================================================================
// TryItemSwapInVanillaGroup
//
// Overwrites the vanilla group[0]'s Items.Data with a static buffer that
// contains [N vanilla items] + [K our custom items]. Single group in the UI,
// all items in one row.
// ============================================================================
static int TryItemSwapInVanillaGroup(void* Result, ForeignFanoutReport* fanout)
{
    if (!Result) return 0;
    if (!g_donorItem) return 0;

    // ----- Read outer Result, locate vanilla group[0] ---------------------
    QmUE::FTArrayHeader outerHdr = {};
    if (SafeReadTArrayHeader(Result, &outerHdr) != 0) return 0;
    if (!outerHdr.Data || outerHdr.Num < 1)
    {
        long n = InterlockedIncrement(&g_itemSwapSkipsNoOuter);
        if (n <= 3)
            QM_LOG_WARN("[ItemSwap] outer Result empty/unreadable (Num=%d Max=%d) - cannot swap (skip#%ld)",
                outerHdr.Num, outerHdr.Max, n);
        return 0;
    }

    void* group0 = nullptr;
    __try { group0 = reinterpret_cast<void**>(outerHdr.Data)[0]; }
    __except (EXCEPTION_EXECUTE_HANDLER) { return 0; }
    if (!group0) return 0;

    // ----- Read vanilla Items TArray header ------------------------------
    QmUE::FTArrayHeader* vanillaItems = reinterpret_cast<QmUE::FTArrayHeader*>(
        reinterpret_cast<uint8_t*>(group0) + kBuildingItemsOffset);

    void* vanillaData = nullptr;
    int32_t vanillaNum = 0;
    int32_t vanillaMax = 0;
    __try
    {
        vanillaData = vanillaItems->Data;
        vanillaNum  = vanillaItems->Num;
        vanillaMax  = vanillaItems->Max;
    }
    __except (EXCEPTION_EXECUTE_HANDLER) { return 0; }

    // ----- Already swapped this hit? Idempotency check -------------------
    if (IsOurReservedBuffer(vanillaData))
    {
        long n = InterlockedIncrement(&g_itemSwapAlreadySwapped);
        if (n <= 3 || (n % 50) == 0)
            QM_LOG_INFO("[ItemSwap] vanilla group0=0x%p already swapped (Items.Data=0x%p in our pool, Num=%d) - skip#%ld",
                group0, vanillaData, vanillaNum, n);
        return 0;
    }

    // ----- Sanity check: vanilla item count fits in our buffer -----------
    if (vanillaNum < 0 || vanillaNum > kCustomGroupItemMax - g_injectableItemCount)
    {
        long n = InterlockedIncrement(&g_itemSwapSkipsVanillaOverflow);
        if (n <= 3)
            QM_LOG_WARN("[ItemSwap] vanilla group0=0x%p has Num=%d, can't fit + %d custom into buffer cap=%d (skip#%ld)",
                group0, vanillaNum, g_injectableItemCount, kCustomGroupItemMax, n);
        return 0;
    }

    // ----- Buffer availability gate --------------------------------------
    // If QmAlloc didn't pre-reserve any buffers (InnerMalloc shim not found,
    // or ReserveBuffers came up empty) we have NO fallback. Bail silently
    // here so the game keeps running without our items.
    const int reservedCount = QmAlloc::GetReservedBufferCount();
    if (reservedCount <= 0 ||
        QmAlloc::GetReservedBufferSize() < kCustomGroupItemMax * sizeof(void*))
    {
        long n = InterlockedIncrement(&g_itemSwapSkipsNoBuffer);
        if (n == 1 || (n % 100) == 0)
            QM_LOG_WARN("[ItemSwap] no reserved buffers (count=%d, size=%zu) - ItemSwap disabled (skip#%ld). See GAME_UPDATE_RECOVERY.md.",
                reservedCount, QmAlloc::GetReservedBufferSize(), n);
        return 0;
    }

    // ----- Pick next slot (rotation, bounded by what was actually reserved) --
    const int activeSlots = (reservedCount < kCustomGroupBufferSlots)
                          ?  reservedCount : kCustomGroupBufferSlots;
    LONG slotRaw = InterlockedIncrement(&g_customGroupBufferNextSlot) - 1;
    int  slotIdx = static_cast<int>(slotRaw % activeSlots);
    if (slotIdx < 0) slotIdx = 0;

    // ----- GC safety: cleanup previous tenant of this slot ---------------
    // If a prior swap parked on this slot, restore its Items header (null it)
    // and clear RF_Standalone so GC can reclaim that group safely.
    RestorePrevSwapAtSlot(slotIdx);

    // ----- Claim the reserved buffer for this slot -----------------------
    void** itemsBuffer = static_cast<void**>(QmAlloc::GetReservedBuffer(slotIdx));
    if (!itemsBuffer)
    {
        QM_LOG_WARN("[ItemSwap] reserved buffer for slot=%d is null - aborting (reservedCount=%d)",
            slotIdx, reservedCount);
        return 0;
    }

    // ----- Copy vanilla item pointers + spawn customs into buffer --------
    // Buffer is either a fresh GMalloc-reserved block, a reused one from a
    // prior swap, or a DLL-static. None of them carry vanilla content - so
    // always memcpy the vanilla pointers from the (still-valid) vanillaData.
    bool fillOk = true;
    int  vanillaCopied = 0;
    __try
    {
        memset(itemsBuffer, 0, kCustomGroupItemMax * sizeof(void*));
        for (int i = 0; i < vanillaNum; ++i)
        {
            itemsBuffer[i] = reinterpret_cast<void**>(vanillaData)[i];
            vanillaCopied++;
        }
        fillOk = true;
    }
    __except (EXCEPTION_EXECUTE_HANDLER) { fillOk = false; }
    if (!fillOk)
    {
        QM_LOG_WARN("[ItemSwap] FAULT copying vanilla pointers from group0=0x%p Items.Data=0x%p Num=%d (copied=%d) - aborting",
            group0, vanillaData, vanillaNum, vanillaCopied);
        return 0;
    }

    // ----- Spawn our custom items, append after vanilla ------------------
    int customInjected = 0;
    for (int itemIdx = 0; itemIdx < g_injectableItemCount; ++itemIdx)
    {
        int writeSlot = vanillaCopied + customInjected;
        if (writeSlot >= kCustomGroupItemMax) break;

        QmUE::UObject* widget = SpawnOrReuseItemWithOverride(
            reinterpret_cast<QmUE::UObject*>(g_donorItem), itemIdx, reinterpret_cast<QmUE::UObject*>(group0), "item-swap");
        if (!widget)
        {
            if (fanout) { fanout->total++; fanout->skipped++; }
            continue;
        }
        bool writeOk = false;
        __try
        {
            itemsBuffer[writeSlot] = widget;
            writeOk = true;
        }
        __except (EXCEPTION_EXECUTE_HANDLER) { writeOk = false; }
        if (!writeOk)
        {
            if (fanout) { fanout->total++; fanout->faulted++; }
            continue;
        }
        customInjected++;
        if (fanout) { fanout->total++; fanout->injected++; }
        InterlockedIncrement(&g_foreignInjectsDone);
    }

    if (customInjected == 0)
    {
        long n = InterlockedIncrement(&g_itemSwapSkipsNoItems);
        QM_LOG_WARN("[ItemSwap] no custom items spawned for group0=0x%p - leaving vanilla untouched (skip#%ld)",
            group0, n);
        return 0;
    }

    int totalNum = vanillaCopied + customInjected;

    // ----- Atomically swap vanilla Items.Data -> our static buffer -------
    void* oldData = vanillaData;
    bool  swapOk  = false;
    __try
    {
        vanillaItems->Data = itemsBuffer;
        vanillaItems->Num  = totalNum;
        vanillaItems->Max  = kCustomGroupItemMax;
        swapOk = true;
    }
    __except (EXCEPTION_EXECUTE_HANDLER) { swapOk = false; }
    if (!swapOk)
    {
        QM_LOG_ERROR("[ItemSwap] FAULT writing new Items TArray header at group0=0x%p (slot=%d buffer=0x%p) - vanilla state may be inconsistent",
            group0, slotIdx, itemsBuffer);
        return 0;
    }

    // ----- Pin group widget against GC -----------------------------------
    // Set RF_Standalone so UE's garbage collector won't reclaim this group
    // widget while its Items.Data points to our static buffer. Without this,
    // ~TArray<UWidget*> at GC time would call FMalloc::Free(staticBuffer) and
    // FMallocBinned3 crashes on the unrecognized .data segment pointer. The
    // pin gets released when this slot's buffer is reused (RestorePrevSwapAtSlot
    // clears the flag after nulling Items).
    bool standaloneSet = false;
    __try
    {
        QmUE::UObject* obj = reinterpret_cast<QmUE::UObject*>(group0);
        obj->Flags |= kRF_Standalone;
        standaloneSet = true;
    }
    __except (EXCEPTION_EXECUTE_HANDLER) { standaloneSet = false; }
    if (standaloneSet)
        InterlockedIncrement(&g_itemSwapStandaloneSet);
    else
        InterlockedIncrement(&g_itemSwapStandaloneFault);

    // Record entry so RestorePrevSwapAtSlot can clean it up when the slot is
    // reused on the (kCustomGroupBufferSlots)-th future swap.
    AcquireSRWLockExclusive(&g_prevSwapsLock);
    g_prevSwaps[slotIdx].groupWidget = group0;
    g_prevSwaps[slotIdx].ourBuffer   = itemsBuffer;
    g_prevSwaps[slotIdx].inUse       = true;
    ReleaseSRWLockExclusive(&g_prevSwapsLock);

    long applyNum = InterlockedIncrement(&g_itemSwapApplies);
    QM_LOG_INFO("[ItemSwap] *** SUCCESS *** apply#%ld group0=0x%p Items.Data %p->%p (vanilla=%d copied + custom=%d injected = %d total; vanillaOldMax=%d newMax=%d; slot=%d; RF_Standalone=%s)",
        applyNum, group0, oldData, itemsBuffer,
        vanillaCopied, customInjected, totalNum,
        vanillaMax, kCustomGroupItemMax, slotIdx,
        standaloneSet ? "set" : "FAULT");

    return customInjected;
}

// ============================================================================
// Hook params reader - CategoryTag from GetBuildingGroupsByCategoryTag.
//
// Params layout (Dumper-7 R5_parameters.hpp):
//   struct R5HFSM_BuildingPanel_GetBuildingGroupsByCategoryTag {
//     FGameplayTag             CategoryTag;     // 0x0000(0x0008) Const,Parm,Out,Reference
//     const UR5BuildingBrush*  SelectedBrush;   // 0x0008(0x0008) Const,Parm
//     TArray<UR5BuildingGroupWidget*> ReturnValue; // 0x0010(0x0010) Parm,Out,Return
//   };
//
// CategoryTag is ReferenceParm - the slot at paramsBase+0x00 holds a pointer.
// We try deref first, then value-style fallback.
// ============================================================================
bool ReadCategoryTagFromHookParams(void* Result, QmUE::FGameplayTag* tagOut, bool* viaReferenceOut)
{
    if (!Result || !tagOut) return false;
    tagOut->ComparisonIndex = 0;
    tagOut->Number = 0;
    if (viaReferenceOut) *viaReferenceOut = false;

    uint8_t* paramsBase = reinterpret_cast<uint8_t*>(Result) - 0x10;

    // 1) Reference-style: slot holds FGameplayTag*.
    QmUE::FGameplayTag* refPtr = nullptr;
    __try { refPtr = *reinterpret_cast<QmUE::FGameplayTag**>(paramsBase + 0x00); }
    __except (EXCEPTION_EXECUTE_HANDLER) {}
    if (refPtr)
    {
        QmUE::FGameplayTag refTag = {};
        bool ok = false;
        __try { refTag = *refPtr; ok = true; }
        __except (EXCEPTION_EXECUTE_HANDLER) {}
        if (ok && !refTag.IsNone())
        {
            *tagOut = refTag;
            if (viaReferenceOut) *viaReferenceOut = true;
            return true;
        }
    }

    // 2) Value-style fallback: slot holds FGameplayTag inline.
    QmUE::FGameplayTag valTag = {};
    __try { valTag = *reinterpret_cast<QmUE::FGameplayTag*>(paramsBase + 0x00); }
    __except (EXCEPTION_EXECUTE_HANDLER) {}
    if (!valTag.IsNone())
    {
        *tagOut = valTag;
        return true;
    }
    return false;
}

// ============================================================================
// Group category probe + classification.
// ============================================================================
void ProbeGroupCategory(void* group, GroupCategoryProbe* out)
{
    if (!out) return;
    out->firstItem = nullptr;
    out->pkgName[0] = '\0';
    out->tagName[0] = '\0';
    out->hasItems = false;
    out->pkgValid = false;
    if (!group) return;

    QmUE::FTArrayHeader itemsHdr = {};
    if (SafeReadTArrayHeader(
        reinterpret_cast<uint8_t*>(group) + kBuildingItemsOffset, &itemsHdr) != 0) return;
    if (!itemsHdr.Data || itemsHdr.Num < 1) return;
    out->hasItems = true;

    void* firstItem = nullptr;
    QmUE::FName pkgName = {};
    int32_t weakIdx = 0;
    __try
    {
        firstItem = reinterpret_cast<void**>(itemsHdr.Data)[0];
        if (firstItem)
        {
            uint8_t* w = reinterpret_cast<uint8_t*>(firstItem);
            pkgName = *reinterpret_cast<QmUE::FName*>(w + ItemDataLayout::kPackageName);
            weakIdx = *reinterpret_cast<int32_t*>(w + ItemDataLayout::kWeakPtr);
        }
    }
    __except (EXCEPTION_EXECUTE_HANDLER) { return; }
    if (!firstItem) return;
    out->firstItem = firstItem;

    if (!pkgName.IsNone())
    {
        if (QmUE::ResolveFNameNarrow(pkgName, out->pkgName, sizeof(out->pkgName))
            && out->pkgName[0])
            out->pkgValid = true;
    }

    // Try to hydrate the BuildingItemTag if the soft-ref is already resolved.
    if (weakIdx > 0)
    {
        QmUE::UObject* hydrated = ResolveWeakObjectPtr(weakIdx);
        if (hydrated)
        {
            __try
            {
                QmUE::FName tag = *reinterpret_cast<QmUE::FName*>(
                    reinterpret_cast<uint8_t*>(hydrated) + kBuildingItemTagOffset);
                if (!tag.IsNone())
                    QmUE::ResolveFNameNarrow(tag, out->tagName, sizeof(out->tagName));
            }
            __except (EXCEPTION_EXECUTE_HANDLER) { out->tagName[0] = '\0'; }
        }
    }
}

bool GroupMatchesTargetCategory(const GroupCategoryProbe& probe)
{
    // Tab-purity uses the shared filter substring (all items share one tab
    // in this iteration). If the filter is null we fall back to match-all.
    if (!kTabPurityFilterSubstring) return true;
    if (!probe.pkgValid) return false;
    return strstr(probe.pkgName, kTabPurityFilterSubstring) != nullptr;
}

// Per-item match: does this group pass item.targetCategorySubstring?
static bool GroupMatchesItemTarget(const GroupCategoryProbe& probe, const InjectableItem& item)
{
    if (!item.targetCategorySubstring) return true;  // match-all
    if (!probe.pkgValid) return false;
    return strstr(probe.pkgName, item.targetCategorySubstring) != nullptr;
}

int ClassifyTabPurity(void* Result)
{
    if (!Result) return -1;
    if (!kTabPurityFilterSubstring) return 1;

    QmUE::FTArrayHeader grpHdr = {};
    if (SafeReadTArrayHeader(Result, &grpHdr) != 0) return -1;
    if (!grpHdr.Data || grpHdr.Num < 1) return -1;

    int matched = 0;
    int totalProbed = 0;
    for (int g = 0; g < grpHdr.Num; ++g)
    {
        void* gp = nullptr;
        __try { gp = reinterpret_cast<void**>(grpHdr.Data)[g]; }
        __except (EXCEPTION_EXECUTE_HANDLER) { continue; }
        if (!gp) continue;

        GroupCategoryProbe probe = {};
        ProbeGroupCategory(gp, &probe);
        if (!probe.pkgValid) continue;
        totalProbed++;
        if (GroupMatchesTargetCategory(probe)) matched++;
    }
    if (totalProbed <= 0) return -1;
    return (matched == totalProbed) ? 1 : 0;
}

// ============================================================================
// One-shot diagnostic: dump the group widget's UClass hierarchy and ALL its
// UFunctions. We need this to find a Blueprint-level "AddItem" / "BindItems"
// function we can call via ProcessEvent - that's UE's own TArray-grow path
// which works reliably (avoids our FMalloc::Realloc cold-path problem).
//
// Fires once per game session, gated by an atomic CAS. Walks group->Class +
// all SuperStruct ancestors. Logs every UFunction with name, ExecFn, Flags,
// and parameter count (from StructSize / ParamsSize for native funcs).
// ============================================================================
static volatile LONG g_groupClassDumpDone = 0;

static void DumpClassUFunctions(QmUE::UStruct* cls, const char* contextTag)
{
    if (!cls) return;
    char clsName[128] = "<?>";
    __try { QmUE::ResolveFNameNarrow(reinterpret_cast<QmUE::UObject*>(cls)->Name, clsName, sizeof(clsName)); }
    __except (EXCEPTION_EXECUTE_HANDLER) { strncpy_s(clsName, sizeof(clsName), "<fault>", _TRUNCATE); }

    QM_LOG_INFO("[ClassDump] >>> [%s] class '%s' @0x%p - enumerating UFunctions (super-chain follows)",
        contextTag, clsName, cls);

    QmUE::UStruct* curr = cls;
    int depth = 0;
    int totalFuncs = 0;
    while (curr && depth < 20)
    {
        char currName[128] = "<?>";
        __try { QmUE::ResolveFNameNarrow(reinterpret_cast<QmUE::UObject*>(curr)->Name, currName, sizeof(currName)); }
        __except (EXCEPTION_EXECUTE_HANDLER) { strncpy_s(currName, sizeof(currName), "<fault>", _TRUNCATE); break; }

        QmUE::UField* field = nullptr;
        __try { field = curr->Children; }
        __except (EXCEPTION_EXECUTE_HANDLER) { field = nullptr; }

        int funcIdx = 0;
        int sanityWalk = 0;
        while (field && sanityWalk < 1024)
        {
            QmUE::UField* nextField = nullptr;
            QmUE::UClass* fieldCls = nullptr;
            QmUE::FName fname = {0,0};
            __try { fieldCls = field->Class; nextField = field->Next; fname = field->Name; }
            __except (EXCEPTION_EXECUTE_HANDLER) { break; }

            if (fieldCls)
            {
                uint64_t castFlags = 0;
                __try { castFlags = fieldCls->CastFlags; }
                __except (EXCEPTION_EXECUTE_HANDLER) { castFlags = 0; }

                if ((castFlags & QmUE::CASTFLAG_Function) != 0)
                {
                    char fnName[128] = "<?>";
                    QmUE::ResolveFNameNarrow(fname, fnName, sizeof(fnName));
                    QmUE::UFunction* fn = reinterpret_cast<QmUE::UFunction*>(field);
                    int32_t paramsSize = 0;
                    __try { paramsSize = fn->StructSize; } __except (EXCEPTION_EXECUTE_HANDLER) {}
                    QM_LOG_INFO("[ClassDump]   [%s] fn[%d] '%s' ExecFn=0x%p Flags=0x%08X ParamsSize=%d",
                        currName, funcIdx++, fnName, (void*)fn->ExecFunction, fn->FunctionFlags, paramsSize);
                    totalFuncs++;
                }
            }
            field = nextField;
            sanityWalk++;
        }

        QmUE::UStruct* super = nullptr;
        __try { super = curr->SuperStruct; }
        __except (EXCEPTION_EXECUTE_HANDLER) { super = nullptr; }
        if (super == curr) break;  // safety: cycle detection
        curr = super;
        depth++;
    }
    QM_LOG_INFO("[ClassDump] <<< [%s] '%s' total UFunctions in chain: %d (depth=%d)",
        contextTag, clsName, totalFuncs, depth);
}

static void DumpGroupAndItemClassesOnce(void* group)
{
    if (!group) return;
    if (InterlockedCompareExchange(&g_groupClassDumpDone, 1, 0) != 0) return;

    QmUE::UClass* groupCls = nullptr;
    __try { groupCls = reinterpret_cast<QmUE::UObject*>(group)->Class; }
    __except (EXCEPTION_EXECUTE_HANDLER) { groupCls = nullptr; }
    if (groupCls) DumpClassUFunctions(groupCls, "GroupWidget");

    // Also dump item widget class - we may need an item-level function too
    // (e.g. "Init", "SetData") for further work.
    if (g_itemWidgetClass) DumpClassUFunctions(g_itemWidgetClass, "ItemWidget");

    // And the panel class - useful to check for an "AddItemToGroup" hook
    QmUE::UClass* panelCls = QmUE::FindClassByName("R5HFSM_BuildingPanel");
    if (panelCls) DumpClassUFunctions(panelCls, "Panel");
}

// ============================================================================
// Inject one item into a single group. Spawns a fresh widget, applies the
// per-item override, appends into the items array.
// ============================================================================
static int InjectIntoGroup(void* group, int itemIdx, ForeignInjectReport* out)
{
    if (out) { out->targetGroup = group; out->donorItem = nullptr;
               out->oldNum = out->newNum = out->max = -1; out->status = nullptr; out->itemIdx = itemIdx; }
    if (!group) { if (out) out->status = "skipped-empty"; return -1; }
    if (itemIdx < 0 || itemIdx >= g_injectableItemCount) {
        if (out) out->status = "skipped-bad-item"; return -1;
    }

    if (!g_donorItem) { if (out) out->status = "skipped-no-target"; return -1; }

    // Don't list anything in the donor's own group on capture-hit (would
    // duplicate right next to the original slot). Stale-after-tab-recycle
    // pointer compare is safe: at worst it's a false match against a freed
    // address, never a deref.
    if (group == g_donorSourceGroup) { if (out) out->status = "skipped-same-group"; return -1; }

    // One-shot diagnostic: dump group/item/panel class UFunctions so we can
    // find a Blueprint-level "AddItem" / "BindItems" we can call via
    // ProcessEvent (UE's own TArray-grow path - bypasses our FMalloc cold
    // path that AVs on 2nd+ tab-visit).
    DumpGroupAndItemClassesOnce(group);

    const InjectableItem& item = g_injectableItems[itemIdx];

    // Per-item category-targeting: only inject into groups whose first item's
    // package path matches this item's target. Skip everything else.
    GroupCategoryProbe probe = {};
    ProbeGroupCategory(group, &probe);
    if (!GroupMatchesItemTarget(probe, item))
    {
        InterlockedIncrement(&g_foreignSkippedCategory);
        if (out) out->status = "skipped-category";
        return -1;
    }

    QmUE::FTArrayHeader* itemsArr = reinterpret_cast<QmUE::FTArrayHeader*>(
        reinterpret_cast<uint8_t*>(group) + kBuildingItemsOffset);

    QmUE::FTArrayHeader itemsHdr = {};
    if (SafeReadTArrayHeader(itemsArr, &itemsHdr) != 0) return -2;
    if (out) { out->oldNum = itemsHdr.Num; out->max = itemsHdr.Max; }

    if (!itemsHdr.Data)              { if (out) out->status = "skipped-empty";    return -1; }

    // ----- Slack gate / TArray grow path -----------------------------------
    // If the group's Items TArray has no slack (Num == Max), grow it via UE's
    // own FMemory allocator so the destructor's GMalloc->Free remains valid.
    // Multi-item profiles need this because the vanilla buffer is usually
    // sized just-fits (e.g. Brush_Pier_01 group spawns with Num=3 Max=4).
    //
    // IMPORTANT: We use Malloc + manual memcpy instead of Realloc because UE5
    // TArrays often use TInlineAllocator<N> for small fixed-size buckets - the
    // initial Data ptr is then an inline buffer INSIDE the group object itself,
    // NOT a GMalloc allocation. Calling GMalloc->Realloc(InlineBufferPtr, ...)
    // returns a fresh buffer but does NOT copy the original contents (since
    // GMalloc doesn't recognize the foreign pointer). UE then iterates the new
    // buffer's garbage entries -> AV.
    //
    // We use QmAlloc::Realloc(oldData, ...) (= hot-realloc) first, then fall
    // back to Realloc(nullptr,...) (= cold-realloc-as-malloc). The cold path
    // AVs on this build's FMallocProxy with non-zero alignment - both calls
    // pass `0` (= natural alignment) to skip the proxy's wstring-deref bug.
    //
    // This legacy path is NOT the primary inject route (BuildingBrushes uses
    // ItemSwap with reserved buffers instead) but is kept for completeness
    // in case a future item targets a category that needs a real TArray grow.
    //
    // Realloc + memcpy + never-free-original is safe in both cases:
    //   - Original was inline: TInlineAllocator destructor checks Data !=
    //     &InlineBuffer and only calls SecondaryAllocator.Free on heap ptrs;
    //     since we set Data to a fresh GMalloc ptr, that Free path is valid.
    //     Inline buffer stays in the object - no leak, no crash.
    //   - Original was heap: we leak <= 32 bytes per group grow. Acceptable.
    //
    // Failure path is silent: if QmAlloc isn't resolved, or Malloc returned
    // null, we fall back to the original skipped-no-slack behaviour. Better
    // than crashing.
    if (itemsHdr.Num >= itemsHdr.Max)
    {
        if (!QmAlloc::IsResolved())
        {
            if (out) out->status = "skipped-no-slack";
            return -1;
        }
        const int32_t oldMax = itemsHdr.Max;
        // Grow by a small amount per call. Most build-menu groups stay small
        // (<= 8 items), so a +4 bump is enough headroom for all currently
        // injectable items even with multiple custom builds per profile.
        const int32_t newMax = oldMax + 4;
        const size_t  newBytes = static_cast<size_t>(newMax) * sizeof(void*);
        void* const oldData = itemsHdr.Data;
        const int32_t oldNum = itemsHdr.Num;

        // ----- Stack-temp backup of existing item pointers ------------------
        // Copy the existing entries onto our stack before any allocator call.
        // After Realloc we restore from this temp - that way the new buffer
        // always has correct contents regardless of whether the allocator
        // preserved them. Cheap (16 ptrs = 128 bytes stack) and gives us
        // defense-in-depth: even if the allocator's "preserve content" path
        // is broken in this UE build, our stack temp wins.
        constexpr int kMaxBackupSlots = 16;
        void* tempBuf[kMaxBackupSlots] = {};
        const int copyCount = (oldNum > kMaxBackupSlots) ? kMaxBackupSlots : oldNum;
        bool backupOk = false;
        __try
        {
            memcpy(tempBuf, oldData, static_cast<size_t>(copyCount) * sizeof(void*));
            backupOk = true;
        }
        __except (EXCEPTION_EXECUTE_HANDLER) { backupOk = false; }
        if (!backupOk)
        {
            QM_LOG_ERROR("[Realloc] group=0x%p item[%d]: FAULT reading oldData=0x%p num=%d - skipping",
                group, itemIdx, oldData, oldNum);
            if (out) out->status = "skipped-bad-old-data";
            return -1;
        }

        // ----- FMalloc warm-up BEFORE allocation call -----------------------
        // Observed bug: on 2nd+ tab-open, item[0] takes the pool-REUSE path
        // (no SpawnObject), then item[1] needs grow. Without a recent UE-VM
        // allocation event the allocator's TLS / inner-pointer cache can be
        // cold and Realloc faults via call-to-null. Trigger one sacrificial
        // SpawnObject to warm it. Orphaned widget - UE GC reclaims it.
        QmUE::UClass*  warmupCls   = g_itemWidgetClass;
        QmUE::UObject* warmupOuter = nullptr;
        if (g_donorItem)
        {
            __try { warmupOuter = reinterpret_cast<QmUE::UObject*>(g_donorItem)->Outer; }
            __except (EXCEPTION_EXECUTE_HANDLER) { warmupOuter = nullptr; }
        }
        if (warmupCls && warmupOuter)
        {
            // Iteration 1: 5 sacrificial spawns. Hit#2 had 2 SpawnObjects
            // (real + sacrificial) and worked, Hit#4+ had only 1 (sacrificial
            // alone via pool-REUSE) and failed - threshold is somewhere between
            // 1 and 2. Use 5 as safety margin against numerical-saturation TLS
            // init in FMallocBinned3.
            WarmUpAllocatorViaSpawn(warmupCls, warmupOuter, "tarray-grow", 5);
        }

        // ----- FMalloc 64-byte pool warm-up via AppendString spam -----------
        // EMPIRICAL: at hit#2 the ClassDump diagnostic accidentally ran ~200
        // FName->FString resolutions between AllocWarmup and Realloc, which
        // got us a working Path 3. At hit#4 (no ClassDump) all 3 paths failed.
        // Hypothesis: FMallocBinned3's per-thread per-pool TLS caches need
        // recent real allocator activity for the specific pool we hit.
        // Long-FName lookups force AppendString to invoke TArray<TCHAR>::
        // Reserve which goes through GMalloc->Realloc - the exact code path
        // we need to warm. 50 iterations matches the ClassDump's ballpark.
        if (g_donorItem)
        {
            WarmUpFMallocViaFNameLookups(reinterpret_cast<QmUE::UObject*>(g_donorItem), 50, "tarray-grow");
        }

        // ----- Allocation: hot-path first, cold fallback --------------------
        // Path 1 (hot, UE's own TArray::Add path): Realloc(oldData, newBytes,
        //   0). UE knows oldData's bin from the FMalloc-internal header so
        //   it allocates + copies + frees in one go.
        // Path 2 (cold): Realloc(nullptr, newBytes, 0).
        //
        // CRITICAL: Alignment MUST be 0 (= natural). Offline analysis of the
        // GMalloc vtable[4] proxy showed that a non-zero alignment gets routed
        // into a wide-string init function and dereferenced as wchar*, causing
        // an AV with faultAddr matching the alignment value. With Alignment=0,
        // the wrapper takes a fast-path skip and the inner FMallocBinned2 uses
        // its default alignment (16) anyway.
        void* newData = nullptr;
        const char* pathTaken = "?";

        newData = QmAlloc::Realloc(oldData, newBytes, 0);
        if (newData) pathTaken = "hot-realloc(oldData,0)";

        if (!newData)
        {
            newData = QmAlloc::Realloc(nullptr, newBytes, 0);
            if (newData) pathTaken = "cold-realloc(nullptr,0)";
        }
        if (!newData)
        {
            QM_LOG_WARN("[Realloc] group=0x%p item[%d]: all 3 FMemory paths returned null - skipping (newBytes=%zu oldData=0x%p)",
                group, itemIdx, newBytes, oldData);
            if (out) out->status = "skipped-malloc-failed";
            return -1;
        }

        // ----- Restore content from stack-temp ------------------------------
        // Hot-path Realloc already preserved content, but we overwrite from
        // tempBuf anyway as defense - if the allocator path was actually
        // cold (paths 2 or 3) the new buffer is uninitialised. Either way
        // tempBuf has the correct pointer values from before the call.
        __try
        {
            memcpy(newData, tempBuf, static_cast<size_t>(copyCount) * sizeof(void*));
            // Zero trailing slots (defense - UE shouldn't read past Num but
            // some bounds-relaxed paths walk to Max during tear-down).
            memset(reinterpret_cast<uint8_t*>(newData) + static_cast<size_t>(copyCount) * sizeof(void*),
                   0, static_cast<size_t>(newMax - copyCount) * sizeof(void*));
            itemsArr->Data = newData;
            itemsArr->Max  = newMax;
        }
        __except (EXCEPTION_EXECUTE_HANDLER)
        {
            QM_LOG_ERROR("[Realloc] *** EXCEPTION restoring content newData=0x%p num=%d newMax=%d",
                newData, copyCount, newMax);
            return -2;
        }
        itemsHdr.Data = newData;
        itemsHdr.Max  = newMax;
        if (out) out->max = newMax;
        QM_LOG_INFO("[Realloc] grew group items: Num=%d Max=%d -> %d (group=0x%p item[%d] oldData=0x%p newData=0x%p, restored=%d ptr(s), path=%s)",
            itemsHdr.Num, oldMax, newMax, group, itemIdx, oldData, newData, copyCount, pathTaken);
    }

    // Spawn-or-reuse AFTER the slack/empty gates so we don't allocate widgets
    // that won't be used. The pool prefers reusing a previously-spawned widget
    // for the same item whose lastGroup differs from this one - that's a
    // widget the previous (now-stale) group still references but the live UI
    // no longer renders. Falling back to fresh spawn only when no reuse fits.
    QmUE::UObject* injectWidget = SpawnOrReuseItemWithOverride(
        reinterpret_cast<QmUE::UObject*>(g_donorItem), itemIdx, group, "inject");
    if (!injectWidget) { if (out) out->status = "skipped-no-target"; return -1; }
    if (out) out->donorItem = injectWidget;

    __try
    {
        reinterpret_cast<void**>(itemsHdr.Data)[itemsHdr.Num] = injectWidget;
        itemsArr->Num = itemsHdr.Num + 1;
    }
    __except (EXCEPTION_EXECUTE_HANDLER) { return -2; }

    InterlockedIncrement(&g_foreignInjectsDone);
    if (out) { out->newNum = itemsHdr.Num + 1; out->status = "injected"; }
    return 0;
}

// ============================================================================
// Per-hit pipeline entry point.
// Capture (once) -> tab-purity gate -> per-item single-shot inject.
// ============================================================================
int CaptureOrInjectForeignItem(void* Result, ForeignInjectReport* out,
                               ForeignFanoutReport* fanout)
{
    if (out) { out->targetGroup = nullptr; out->donorItem = nullptr;
               out->oldNum = out->newNum = out->max = -1; out->status = nullptr; out->itemIdx = -1; }
    if (fanout) { fanout->total = fanout->injected = fanout->skipped = fanout->faulted = 0; }
    if (!Result) { if (out) out->status = "skipped-empty"; return -1; }

    QmUE::FTArrayHeader grpHdr = {};
    if (SafeReadTArrayHeader(Result, &grpHdr) != 0) return -2;
    if (!grpHdr.Data || grpHdr.Num < 1) { if (out) out->status = "skipped-empty"; return -1; }

    void* group0 = nullptr;
    __try { group0 = reinterpret_cast<void**>(grpHdr.Data)[0]; }
    __except (EXCEPTION_EXECUTE_HANDLER) { return -2; }
    if (!group0) { if (out) out->status = "skipped-empty"; return -1; }
    if (out) out->targetGroup = group0;

    // ----- Stale-donor invalidation ---------------------------------------
    // If we have a cached donor but its memory has been recycled by UE to back
    // a different widget class (e.g. WBP_Shortcut_C from the hotbar after the
    // player built+placed our custom item), clear it so the capture phase
    // below re-acquires a fresh, valid WBP_Building_Item_C from group0.
    if (g_donorItem)
    {
        char staleClsName[128] = "<?>";
        if (!IsDonorClassValid(reinterpret_cast<QmUE::UObject*>(g_donorItem),
                               staleClsName, sizeof(staleClsName)))
        {
            QM_LOG_WARN("[Capture] STALE-DONOR detected at re-entry: g_donorItem=0x%p class='%s' (expected substring '%s') - re-capturing",
                g_donorItem, staleClsName, kExpectedDonorClassSubstr);
            g_donorItem = nullptr;
            g_donorSourceGroup = nullptr;
            strcpy(g_donorAssetName, "<?>");
        }
    }

    // ----- Capture phase ---------------------------------------------------
    bool capturedThisCall = false;
    if (!g_donorItem)
    {
        QmUE::FTArrayHeader itemsHdr = {};
        if (SafeReadTArrayHeader(
            reinterpret_cast<uint8_t*>(group0) + kBuildingItemsOffset, &itemsHdr) != 0)
            return -2;
        if (out) { out->oldNum = itemsHdr.Num; out->max = itemsHdr.Max; }
        if (!itemsHdr.Data || itemsHdr.Num < 1) { if (out) out->status = "skipped-empty"; return -1; }

        void* firstItem = nullptr;
        QmUE::FName assetName = {};
        __try
        {
            firstItem = reinterpret_cast<void**>(itemsHdr.Data)[0];
            if (firstItem)
                assetName = *reinterpret_cast<QmUE::FName*>(
                    reinterpret_cast<uint8_t*>(firstItem) + ItemDataLayout::kAssetName);
        }
        __except (EXCEPTION_EXECUTE_HANDLER) { return -2; }
        if (!firstItem) { if (out) out->status = "skipped-empty"; return -1; }

        // Class-check the candidate before we commit it. Capturing the wrong
        // class once would lead to a class-mismatch crash on the next inject.
        char captureClsName[128] = "<?>";
        if (!IsDonorClassValid(reinterpret_cast<QmUE::UObject*>(firstItem),
                               captureClsName, sizeof(captureClsName)))
        {
            QM_LOG_WARN("[Capture] REJECTED candidate firstItem=0x%p class='%s' (expected substring '%s')",
                firstItem, captureClsName, kExpectedDonorClassSubstr);
            if (out) out->status = "skipped-bad-class";
            return -1;
        }

        g_donorItem = firstItem;
        g_donorSourceGroup = group0;
        if (!assetName.IsNone())
        {
            if (!QmUE::ResolveFNameNarrow(assetName, g_donorAssetName, sizeof(g_donorAssetName)))
                snprintf(g_donorAssetName, sizeof(g_donorAssetName), "<unresolved cmp=%d num=%u>",
                    assetName.ComparisonIndex, assetName.Number);
        }
        else strcpy(g_donorAssetName, "<None>");

        // No pre-spawn here: spawning happens lazily inside InjectIntoGroup,
        // one fresh widget per inject (each group gets its own).

        QM_LOG_INFO("[Capture] donor accepted: donor=0x%p class='%s' assetName='%s' sourceGroup=0x%p",
            g_donorItem, captureClsName, g_donorAssetName, g_donorSourceGroup);

        if (out) { out->donorItem = firstItem; out->status = "captured"; }
        capturedThisCall = true;
    }

    // ----- Tab-purity guard (Plan B+): only inject if EVERY group in this
    // result is part of the same target tab. Mixed tabs (e.g. "Aufbewahrung
    // +Betten" which mixes Bedroll-decoration with StickBasket-utilities)
    // get skipped.
    int purity = ClassifyTabPurity(Result);
    if (purity == 0)
    {
        if (out && (out->status == nullptr ||
            strcmp(out->status, "captured") != 0))
            out->status = "skipped-tab-impure";
        return capturedThisCall ? 0 : -1;
    }
    // purity == -1 (no groups / fault) and purity == 1 (pure) both fall through.

    // ----- VARIANTE C: item-swap-in-vanilla-group (primary path) ----------
    // When a tab-purity filter is configured and this tab matches (purity==1
    // or indeterminate), overwrite the vanilla group[0]'s Items.Data pointer
    // with a DLL-static buffer that contains [N vanilla items] + [K custom
    // items]. Single group in the UI with all items mixed in.
    //
    // This avoids:
    //   - The FMallocBinned3 Realloc cold-path AV (we never grow the existing
    //     TArray, we replace its Data buffer wholesale)
    //   - The double-group UMG render bug (we don't spawn a parallel group,
    //     so no shared UMG sub-slots between two groups)
    //
    // If swap succeeds (customInjected > 0), skip the legacy per-group loop
    // entirely. On any failure (empty outer, vanilla overflow, no items
    // produced), fall through to the legacy per-group loop.
    if (kTabPurityFilterSubstring && fanout)
    {
        int swapInjected = TryItemSwapInVanillaGroup(Result, fanout);
        if (swapInjected > 0)
        {
            if (out && (out->status == nullptr || strcmp(out->status, "captured") != 0))
            {
                out->status      = "item-swapped";
                out->itemIdx     = -1;
                out->newNum      = swapInjected;
                out->oldNum      = 0;
                out->max         = kCustomGroupItemMax;
                out->targetGroup = nullptr;  // overwrote vanilla in-place
                out->donorItem   = nullptr;
            }
            return 0;
        }
        // swapInjected == 0: skip happened (logged inside) - fall through
        // to legacy path so injects into other slack-bearing groups still work.
    }

    // ----- Inject phase: per item, walk groups, inject into the FIRST
    // matching one. Each item produces at most one slot per hit.
    if (fanout)
    {
        for (int itemIdx = 0; itemIdx < g_injectableItemCount; ++itemIdx)
        {
            bool itemPlaced = false;
            for (int g = 0; g < grpHdr.Num; ++g)
            {
                void* gp = nullptr;
                __try { gp = reinterpret_cast<void**>(grpHdr.Data)[g]; }
                __except (EXCEPTION_EXECUTE_HANDLER) { fanout->faulted++; continue; }
                if (!gp) { fanout->skipped++; continue; }

                ForeignInjectReport sub = {};
                int rc = InjectIntoGroup(gp, itemIdx, &sub);
                fanout->total++;
                if (rc == 0)       fanout->injected++;
                else if (rc == -2) fanout->faulted++;
                else               fanout->skipped++;

                if (rc == 0)
                {
                    if (out && (out->status == nullptr ||
                        strcmp(out->status, "captured") != 0))
                    {
                        // Latest successful inject wins for `out` summary;
                        // the hook logs fanout aggregates separately.
                        *out = sub;
                    }
                    itemPlaced = true;
                    break;  // this item is placed, move to next item
                }
            }
            (void)itemPlaced;  // future: track per-item miss for diagnostics
        }
    }

    return capturedThisCall ? 0 : (fanout && fanout->injected > 0 ? 0 : -1);
}
