// Quartermaster inject pipeline - impl. See qm_inject.hpp for the contract.

#define _CRT_SECURE_NO_WARNINGS
#include <windows.h>
#include <stdint.h>
#include <stdio.h>
#include <string.h>

#include "qm_ue.hpp"
#include "qm_state.hpp"
#include "qm_log.hpp"
#include "qm_inject.hpp"

// ============================================================================
// Module-level config (will move to runtime config in workstream B).
// ============================================================================
const wchar_t* const kOverridePackagePathW    = L"/Game/Gameplay/Building/BuildingDecoration/DA_BI_QmBedrl_01";
const wchar_t* const kOverrideAssetNameW      = L"DA_BI_QmBedrl_01";
const char*    const kOverrideAssetName       = "DA_BI_QmBedrl_01";
const char*    const kOverrideClassName       = "R5BuildingItem";
const char*    const kTargetGroupPathSubstring = "BuildingDecoration";
const int            kSpawnedPoolMax           = 16;

// ============================================================================
// Module-private state.
// ============================================================================

// ---- Donor (captured once on first hit) -----------------------------------
static void*    g_donorItem            = nullptr;
static void*    g_donorSourceGroup     = nullptr;
static char     g_donorAssetName[128]  = "<?>";

// ---- Spawned widget pool (one per inject) ---------------------------------
static QmUE::UObject*  g_spawnedPool[16] = {};
static int             g_spawnedPoolCount    = 0;
static QmUE::UClass*   g_itemWidgetClass     = nullptr;
static volatile LONG   g_spawnAttempts       = 0;
static volatile LONG   g_spawnSuccesses      = 0;

// ---- Override target (resolved on first need, cached thereafter) ----------
struct OverrideTarget
{
    bool         resolved;
    QmUE::FName  assetName;
    QmUE::FName  packageName;
};
static OverrideTarget  g_overrideTarget       = {};
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
    s.hookHits               = g_hookHits;
    s.injectsDone            = g_foreignInjectsDone;
    s.alreadyPresent         = g_foreignAlreadyPresent;
    s.donorItem              = g_donorItem;
    s.donorSourceGroup       = g_donorSourceGroup;
    s.spawnedPoolCount       = g_spawnedPoolCount;
    s.spawnAttempts          = g_spawnAttempts;
    s.spawnSuccesses         = g_spawnSuccesses;
    s.overrideApplied        = g_overrideApplied;
    s.overrideLookupAttempts = g_overrideLookupAttempts;
    s.overrideResolved       = g_overrideTarget.resolved;
    s.skippedCategory        = g_foreignSkippedCategory;
    s.donorAssetName         = g_donorAssetName;
    return s;
}

long QmBumpHookHits()
{
    return InterlockedIncrement(&g_hookHits);
}

// ============================================================================
// Override-target FName resolution (KismetStringLibrary::Conv_StringToName).
// ============================================================================
bool QmIsOverrideResolved()
{
    return g_overrideTarget.resolved;
}

bool QmGetOverrideTarget(QmUE::FName* pkgOut, QmUE::FName* assetOut)
{
    if (!g_overrideTarget.resolved) return false;
    if (pkgOut)   *pkgOut   = g_overrideTarget.packageName;
    if (assetOut) *assetOut = g_overrideTarget.assetName;
    return true;
}

static bool ResolveOverrideTarget()
{
    if (g_overrideTarget.resolved) return true;

    InterlockedIncrement(&g_overrideLookupAttempts);

    QmUE::FName pkgName = {0, 0};
    QmUE::FName assetName = {0, 0};

    if (!QmUE::FNameFromString(kOverridePackagePathW, &pkgName))
    {
        if (g_overrideLookupAttempts == 1 || (g_overrideLookupAttempts % 5) == 0)
            QM_LOG_WARN("[Override] FNameFromString FAILED for Pkg='%ls' (attempt#%ld) - using donor-clone fallback",
                kOverridePackagePathW, g_overrideLookupAttempts);
        return false;
    }
    if (!QmUE::FNameFromString(kOverrideAssetNameW, &assetName))
    {
        if (g_overrideLookupAttempts == 1 || (g_overrideLookupAttempts % 5) == 0)
            QM_LOG_WARN("[Override] FNameFromString FAILED for Asset='%ls' (attempt#%ld) - using donor-clone fallback",
                kOverrideAssetNameW, g_overrideLookupAttempts);
        return false;
    }

    g_overrideTarget.resolved    = true;
    g_overrideTarget.assetName   = assetName;
    g_overrideTarget.packageName = pkgName;

    // Round-trip-verify so the log shows what the FName pool actually interned.
    char pkgBuf[256] = "<?>";
    char assetBuf[128] = "<?>";
    QmUE::ResolveFNameNarrow(pkgName,   pkgBuf,   sizeof(pkgBuf));
    QmUE::ResolveFNameNarrow(assetName, assetBuf, sizeof(assetBuf));
    QM_LOG_INFO("[Override] *** RESOLVED *** target='%s' via FName-from-String Pkg='%s' (cmp=%d num=%u) Asset='%s' (cmp=%d num=%u)",
        kOverrideAssetName,
        pkgBuf,   pkgName.ComparisonIndex,   pkgName.Number,
        assetBuf, assetName.ComparisonIndex, assetName.Number);
    return true;
}

// Apply the resolved override to a spawned widget. Returns true on success.
static bool ApplyOverrideToSpawned(QmUE::UObject* spawned)
{
    if (!spawned || !g_overrideTarget.resolved) return false;

    bool ok = false;
    __try
    {
        uint8_t* base = reinterpret_cast<uint8_t*>(spawned);
        *reinterpret_cast<QmUE::FName*>(base + ItemDataLayout::kPackageName) = g_overrideTarget.packageName;
        *reinterpret_cast<QmUE::FName*>(base + ItemDataLayout::kAssetName)   = g_overrideTarget.assetName;
        *reinterpret_cast<int32_t*>(base + ItemDataLayout::kWeakPtr)         = 0;
        *reinterpret_cast<int32_t*>(base + ItemDataLayout::kWeakPtr + 4)     = 0;
        ok = true;
    }
    __except (EXCEPTION_EXECUTE_HANDLER) { ok = false; }

    if (ok)
    {
        InterlockedIncrement(&g_overrideApplied);
        QM_LOG_INFO("[Override] APPLIED to spawned=0x%p (Pkg+Asset rewritten, WeakPtr zeroed) -> SoftRef will re-resolve", spawned);
    }
    else
        QM_LOG_WARN("[Override] FAULT during write to spawned=0x%p", spawned);
    return ok;
}

// ============================================================================
// Fresh-widget spawn with override applied.
// ============================================================================
static QmUE::UObject* SpawnFreshItemWithOverride(QmUE::UObject* donor, const char* contextTag)
{
    if (!donor) return nullptr;

    if (g_spawnedPoolCount >= kSpawnedPoolMax)
    {
        QM_LOG_WARN("[Spawn] pool full (%d) - refusing to spawn more (ctx=%s)",
            g_spawnedPoolCount, contextTag ? contextTag : "?");
        return nullptr;
    }

    QmUE::UClass* itemCls = nullptr;
    QmUE::UObject* outer = nullptr;
    __try { itemCls = donor->Class; outer = donor->Outer; }
    __except (EXCEPTION_EXECUTE_HANDLER) { itemCls = nullptr; outer = nullptr; }

    if (!itemCls)
    {
        QM_LOG_WARN("[Spawn] FAULT reading donor->Class for donor=0x%p (ctx=%s)",
            donor, contextTag ? contextTag : "?");
        return nullptr;
    }

    char itemClsName[128] = "<?>";
    __try { QmUE::ResolveFNameNarrow(itemCls->Name, itemClsName, sizeof(itemClsName)); }
    __except (EXCEPTION_EXECUTE_HANDLER) {}

    InterlockedIncrement(&g_spawnAttempts);
    g_itemWidgetClass = itemCls;

    QmUE::UObject* spawned = QmUE::SpawnObjectViaUFunction(itemCls, outer);
    if (!spawned)
    {
        QM_LOG_WARN("[Spawn] SpawnObjectViaUFunction returned null (Cls=%s @0x%p outer=0x%p ctx=%s)",
            itemClsName, itemCls, outer, contextTag ? contextTag : "?");
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
    if (ResolveOverrideTarget())
        ApplyOverrideToSpawned(spawned);

    if (g_spawnedPoolCount < kSpawnedPoolMax)
        g_spawnedPool[g_spawnedPoolCount++] = spawned;
    InterlockedIncrement(&g_spawnSuccesses);
    QM_LOG_INFO("[Spawn] *** SUCCESS *** %s spawned @ 0x%p (outer=0x%p) ItemData=%s ctx=%s pool=%d",
        itemClsName, spawned, outer, copyOK ? "cloned-from-donor" : "FAULT",
        contextTag ? contextTag : "?", g_spawnedPoolCount);

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
    if (!kTargetGroupPathSubstring) return true;   // disabled => match all
    if (!probe.pkgValid) return false;             // no path => can't classify
    return strstr(probe.pkgName, kTargetGroupPathSubstring) != nullptr;
}

int ClassifyTabPurity(void* Result)
{
    if (!Result) return -1;
    if (!kTargetGroupPathSubstring) return 1;   // filter disabled -> treat as pure

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
        if (!probe.pkgValid) continue;   // can't classify this group - skip
        totalProbed++;
        if (GroupMatchesTargetCategory(probe)) matched++;
    }
    if (totalProbed <= 0) return -1;
    return (matched == totalProbed) ? 1 : 0;
}

// ============================================================================
// Inject single group + capture-or-inject pipeline entry.
// ============================================================================
static int InjectIntoGroup(void* group, ForeignInjectReport* out)
{
    if (out) { out->targetGroup = group; out->donorItem = nullptr;
               out->oldNum = out->newNum = out->max = -1; out->status = nullptr; }
    if (!group) { if (out) out->status = "skipped-empty"; return -1; }

    if (!g_donorItem) { if (out) out->status = "skipped-no-target"; return -1; }

    // Don't list anything in the donor's own group on capture-hit (would
    // duplicate right next to the original slot). Stale-after-tab-recycle
    // pointer compare is safe: at worst it's a false match against a freed
    // address, never a deref.
    if (group == g_donorSourceGroup) { if (out) out->status = "skipped-same-group"; return -1; }

    // Category-targeting (Plan B): only inject into groups whose first item's
    // package path matches our target category. Skip everything else.
    if (kTargetGroupPathSubstring)
    {
        GroupCategoryProbe probe = {};
        ProbeGroupCategory(group, &probe);
        if (!GroupMatchesTargetCategory(probe))
        {
            InterlockedIncrement(&g_foreignSkippedCategory);
            if (out) out->status = "skipped-category";
            return -1;
        }
    }

    QmUE::FTArrayHeader* itemsArr = reinterpret_cast<QmUE::FTArrayHeader*>(
        reinterpret_cast<uint8_t*>(group) + kBuildingItemsOffset);

    QmUE::FTArrayHeader itemsHdr = {};
    if (SafeReadTArrayHeader(itemsArr, &itemsHdr) != 0) return -2;
    if (out) { out->oldNum = itemsHdr.Num; out->max = itemsHdr.Max; }

    if (!itemsHdr.Data)              { if (out) out->status = "skipped-empty";    return -1; }
    if (itemsHdr.Num >= itemsHdr.Max){ if (out) out->status = "skipped-no-slack"; return -1; }

    // Spawn AFTER the slack/empty gates so we don't allocate widgets that won't
    // be used. Each successful inject consumes one pool slot.
    QmUE::UObject* injectWidget = SpawnFreshItemWithOverride(
        reinterpret_cast<QmUE::UObject*>(g_donorItem), "inject");
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

int CaptureOrInjectForeignItem(void* Result, ForeignInjectReport* out,
                               ForeignFanoutReport* fanout)
{
    if (out) { out->targetGroup = nullptr; out->donorItem = nullptr;
               out->oldNum = out->newNum = out->max = -1; out->status = nullptr; }
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

        if (out) { out->donorItem = firstItem; out->status = "captured"; }
        capturedThisCall = true;
    }

    // ----- Tab-purity guard (Plan B+): only inject if EVERY group in this
    // result is a decoration group. Mixed tabs (e.g. "Aufbewahrung+Betten",
    // which mixes Bedroll-decoration with StickBasket-utilities) get skipped.
    int purity = ClassifyTabPurity(Result);
    if (purity == 0)
    {
        if (out && (out->status == nullptr ||
            strcmp(out->status, "captured") != 0))
            out->status = "skipped-tab-impure";
        return capturedThisCall ? 0 : -1;
    }
    // purity == -1 (no groups / fault) and purity == 1 (pure) both fall through;
    // pure proceeds to inject, fault is already handled by the inject-loop's
    // per-group SEH guards.

    // ----- Inject phase: walk groups, inject into the FIRST matching one ---
    // Stop after the first successful inject per hit - we want the donor to
    // appear exactly once, not once per matching decoration subgroup.
    if (fanout)
    {
        for (int g = 0; g < grpHdr.Num; ++g)
        {
            void* gp = nullptr;
            __try { gp = reinterpret_cast<void**>(grpHdr.Data)[g]; }
            __except (EXCEPTION_EXECUTE_HANDLER) { fanout->faulted++; continue; }
            if (!gp) { fanout->skipped++; continue; }

            ForeignInjectReport sub = {};
            int rc = InjectIntoGroup(gp, &sub);
            fanout->total++;
            if (rc == 0)       fanout->injected++;
            else if (rc == -2) fanout->faulted++;
            else               fanout->skipped++;

            if (rc == 0)
            {
                if (out && (out->status == nullptr ||
                    strcmp(out->status, "captured") != 0))
                {
                    *out = sub;
                }
                break; // single-inject policy: one slot per hit, done
            }
        }
    }

    return capturedThisCall ? 0 : (fanout && fanout->injected > 0 ? 0 : -1);
}
