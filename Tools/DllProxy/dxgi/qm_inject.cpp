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
static QmUE::UClass*    g_itemWidgetClass     = nullptr;
static volatile LONG    g_spawnAttempts       = 0;
static volatile LONG    g_spawnSuccesses      = 0;
static volatile LONG    g_spawnReuses         = 0;

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

    // ----- Pool full guard (only matters for fresh spawns) ----------------
    if (g_spawnedPoolCount >= kSpawnedPoolMax)
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

    if (g_spawnedPoolCount < kSpawnedPoolMax)
    {
        PoolEntry& e = g_spawnedPool[g_spawnedPoolCount++];
        e.widget     = spawned;
        e.itemIdx    = itemIdx;
        e.lastGroup  = currentGroup;
        e.reuseCount = 0;
    }
    InterlockedIncrement(&g_spawnSuccesses);
    QM_LOG_INFO("[Spawn] *** SUCCESS *** %s spawned @ 0x%p (outer=0x%p) ItemData=%s ctx=%s item[%d]='%s' pool=%d group=0x%p",
        itemClsName, spawned, outer, copyOK ? "cloned-from-donor" : "FAULT",
        contextTag ? contextTag : "?", itemIdx, item.name, g_spawnedPoolCount, currentGroup);

    return spawned;
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
    if (itemsHdr.Num >= itemsHdr.Max){ if (out) out->status = "skipped-no-slack"; return -1; }

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
