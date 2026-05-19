// Quartermaster UFunction hook + UE probe loop - impl. See qm_hook.hpp.

#define _CRT_SECURE_NO_WARNINGS
#include <windows.h>
#include <stdint.h>
#include <stdio.h>
#include <string.h>

#include "minhook/include/MinHook.h"
#include "qm_ue.hpp"
#include "qm_state.hpp"
#include "qm_log.hpp"
#include "qm_config.hpp"
#include "qm_inject.hpp"
#include "qm_diag.hpp"

// ============================================================================
// Detour.
// ============================================================================
static QmUE::FNativeFuncPtr g_origGetBuildingGroups = nullptr;

static void __fastcall Hook_GetBuildingGroupsByCategoryTag(void* Context, void* Stack, void* Result)
{
    long n = QmBumpHookHits();

    // Logging policy:
    //   hits 1..3  : everything (header + inputs + diag + soft-paths + each item)
    //   hits 4..10 : header + per-hit inject
    //   later      : every 200th hit a ping
    const bool logHeader = (n <= 10) || (n % 200 == 0);
#if QM_DIAG
    const bool logDeep   = (n <= 3);
#endif

    if (logHeader)
    {
        char ctxCls[128] = { 0 };
        TryResolveContextClassName(reinterpret_cast<QmUE::UObject*>(Context), ctxCls, sizeof(ctxCls));
        QM_LOG_DEBUG("[Hook] GetBuildingGroupsByCategoryTag hit #%ld TID=%lu Ctx=0x%p Cls='%s' Stack=0x%p Result=0x%p",
            n, GetCurrentThreadId(), Context, ctxCls[0] ? ctxCls : "<?>", Stack, Result);
        DiagInspectInputs(Result, Stack);
    }

    // Plan A diagnostic - log the resolved CategoryTag per hit (cheap helper,
    // ReferenceParm-aware). Used to filter by tag once the read path works.
    {
        QmUE::FGameplayTag catTag = {};
        bool viaRef = false;
        bool ok = ReadCategoryTagFromHookParams(Result, &catTag, &viaRef);
        char catStr[256] = "<none>";
        if (ok)
        {
            if (!QmUE::ResolveFNameNarrow(catTag, catStr, sizeof(catStr)))
                snprintf(catStr, sizeof(catStr), "<unresolved cmp=%d num=%u>", catTag.ComparisonIndex, catTag.Number);
        }
        if (logHeader || n <= 30)
            QM_LOG_INFO("[Cat] hit#%ld CategoryTag='%s' (via=%s)",
                n, catStr, ok ? (viaRef ? "ref" : "val") : "none");
    }

    // Forward to original.
    if (g_origGetBuildingGroups)
    {
        __try { g_origGetBuildingGroups(Context, Stack, Result); }
        __except (EXCEPTION_EXECUTE_HANDLER)
        {
            if (logHeader)
                QM_LOG_ERROR("[Hook] *** EXCEPTION inside original GetBuildingGroupsByCategoryTag ***");
        }
    }

    // Plan B+ - log a per-group category probe + tab purity classification
    // for the first few hits so we can verify the all-decoration heuristic.
    if (logHeader)
    {
        QmUE::FTArrayHeader grpHdr = {};
        if (SafeReadTArrayHeader(Result, &grpHdr) == 0 && grpHdr.Data && grpHdr.Num > 0)
        {
            int matchCount = 0, probeCount = 0;
            for (int g = 0; g < grpHdr.Num; ++g)
            {
                void* gp = nullptr;
                __try { gp = reinterpret_cast<void**>(grpHdr.Data)[g]; }
                __except (EXCEPTION_EXECUTE_HANDLER) { continue; }
                if (!gp) continue;
                GroupCategoryProbe probe = {};
                ProbeGroupCategory(gp, &probe);
                const bool match = GroupMatchesTargetCategory(probe);
                if (probe.pkgValid) { probeCount++; if (match) matchCount++; }
                QM_LOG_DEBUG("[Group] hit#%ld G%d=0x%p match=%d pkg='%s' tag='%s'",
                    n, g, gp, match ? 1 : 0,
                    probe.pkgValid ? probe.pkgName : "<unresolved>",
                    probe.tagName[0] ? probe.tagName : "<unhydrated>");
            }
            const int purity = ClassifyTabPurity(Result);
            QM_LOG_DEBUG("[Tab] hit#%ld purity=%s (matched=%d / probed=%d / total=%d) -> %s",
                n,
                purity == 1 ? "pure-decoration" : purity == 0 ? "mixed/other" : "indeterminate",
                matchCount, probeCount, grpHdr.Num,
                purity == 1 ? "INJECT-ALLOWED" : "INJECT-SKIPPED");
        }
    }

    ForeignInjectReport fi = {};
    ForeignFanoutReport ff = {};
    int fiRc = CaptureOrInjectForeignItem(Result, &fi, &ff);

    QmInjectSnapshot snap = QmGetInjectSnapshot();

    if (fiRc == -2)
    {
        if (logHeader)
            QM_LOG_WARN("[Foreign] hit#%ld FAULT during capture-or-inject", n);
    }
    else if (fi.status && strcmp(fi.status, "captured") == 0)
    {
        QM_LOG_INFO("[Foreign] hit#%ld CAPTURED donor item=0x%p Asset='%s' from sourceGroup=0x%p",
            n, fi.donorItem, snap.donorAssetName, snap.donorSourceGroup);
        if (ff.total > 0)
            QM_LOG_INFO("[Foreign] hit#%ld FANOUT injected=%d skipped=%d faulted=%d - donor visible from first menu open",
                n, ff.injected, ff.skipped, ff.faulted);
    }
    else if (fi.status && strcmp(fi.status, "injected") == 0)
    {
        const char* itemName = (fi.itemIdx >= 0 && fi.itemIdx < g_injectableItemCount)
            ? g_injectableItems[fi.itemIdx].name : "<?>";
        if (logHeader)
        {
            QM_LOG_DEBUG("[Foreign] hit#%ld INJECTED item[%d]='%s' donor=0x%p -> targetGroup=0x%p slot[%d], Items.Num: %d -> %d (Max=%d) [total=%ld, fanout: t=%d i=%d s=%d f=%d]",
                n, fi.itemIdx, itemName, fi.donorItem, fi.targetGroup, fi.newNum - 1,
                fi.oldNum, fi.newNum, fi.max, snap.injectsDone,
                ff.total, ff.injected, ff.skipped, ff.faulted);
        }
        else if (snap.injectsDone <= 50 || snap.injectsDone % 25 == 0)
        {
            QM_LOG_TRACE("[Foreign] hit#%ld inject#%ld item[%d]='%s' -> targetGroup=0x%p Items %d->%d",
                n, snap.injectsDone, fi.itemIdx, itemName, fi.targetGroup, fi.oldNum, fi.newNum);
        }
    }
    else if (fi.status && strcmp(fi.status, "already-present") == 0)
    {
        if (snap.alreadyPresent <= 5 || logHeader)
            QM_LOG_TRACE("[Foreign] hit#%ld already-present (donor in Items, skip) targetGroup=0x%p Items.Num=%d [skips=%ld]",
                n, fi.targetGroup, fi.oldNum, snap.alreadyPresent);
    }
    else if (fi.status && strcmp(fi.status, "skipped-tab-impure") == 0)
    {
        if (logHeader)
            QM_LOG_INFO("[Foreign] hit#%ld TAB-IMPURE - mixed/other tab, skipping inject (donor stays available)", n);
    }
    else if (fi.status && (n <= 12 || (n % 100 == 0)))
    {
        QM_LOG_TRACE("[Foreign] hit#%ld %s targetGroup=0x%p Items.Num=%d Max=%d",
            n, fi.status, fi.targetGroup, fi.oldNum, fi.max);
    }

#if QM_DIAG
    if (logHeader)
        DiagInspectGroupResult(Result, logDeep);
    if (n <= 3)
        DiagInspectFirstGroupSoftPaths(Result);
#endif

    if (n == 1)
    {
        QM_LOG_INFO("[Hook] *** PHASE 2a SUCCESS *** GetBuildingGroupsByCategoryTag is reachable from our detour");
        QM_LOG_INFO("[Hook] active - %d injectable item(s) configured (workstream B - multi-item):", g_injectableItemCount);
        for (int i = 0; i < g_injectableItemCount; ++i)
        {
            const InjectableItem& it = g_injectableItems[i];
            QM_LOG_INFO("[Hook]   item[%d] '%s' -> %s::%s (target='%s')",
                i, it.name, it.className, it.assetName,
                it.targetCategorySubstring ? it.targetCategorySubstring : "<match-all>");
        }
        QM_LOG_INFO("[Hook] tab-purity-gate: %s (ALL groups in result must match - skips mixed tabs)",
            kTabPurityFilterSubstring ? kTabPurityFilterSubstring : "<disabled>");
        QM_LOG_INFO("[Hook] inject-policy: single-shot per item (each item produces at most one slot per hit, in first matching group)");
        QM_LOG_INFO("[Hook] spawn-policy: spawn-or-reuse per inject (pool cap=%d - reuses prior widgets whose lastGroup differs from current)",
            kSpawnedPoolMax);
    }

    if (n == 1 || (n % 50 == 0))
    {
        QM_LOG_DEBUG("[Spawn] state: pool=%d (attempts=%ld successes=%ld reuses=%ld) donor=0x%p overrides={resolved=%d/%d applied=%ld attempts=%ld} cat-skips=%ld",
            snap.spawnedPoolCount, snap.spawnAttempts, snap.spawnSuccesses, snap.spawnReuses, snap.donorItem,
            snap.overridesResolvedCount, g_injectableItemCount,
            snap.overrideApplied, snap.overrideLookupAttempts, snap.skippedCategory);
    }
}

// ============================================================================
// Hook install.
// ============================================================================
static bool g_groupsHookInstalled = false;
static bool InstallGetBuildingGroupsHook(QmUE::UFunction* target)
{
    if (g_groupsHookInstalled) return true;
    if (!target || !target->ExecFunction)
    {
        QM_LOG_ERROR("[Hook] cannot install - target or ExecFunction is null");
        return false;
    }

    LPVOID execAddr = reinterpret_cast<LPVOID>(target->ExecFunction);
    MH_STATUS st = MH_CreateHook(execAddr,
        reinterpret_cast<LPVOID>(&Hook_GetBuildingGroupsByCategoryTag),
        reinterpret_cast<LPVOID*>(&g_origGetBuildingGroups));
    if (st != MH_OK)
    {
        QM_LOG_ERROR("[Hook] MH_CreateHook(GetBuildingGroupsByCategoryTag @ 0x%p) FAILED: %s",
            execAddr, MH_StatusToString(st));
        return false;
    }

    st = MH_EnableHook(execAddr);
    if (st != MH_OK)
    {
        QM_LOG_ERROR("[Hook] MH_EnableHook(GetBuildingGroupsByCategoryTag @ 0x%p) FAILED: %s",
            execAddr, MH_StatusToString(st));
        return false;
    }

    g_groupsHookInstalled = true;
    QM_LOG_INFO("[Hook] *** INSTALLED *** GetBuildingGroupsByCategoryTag ExecFn=0x%p detour=0x%p trampoline=0x%p",
        execAddr, (void*)&Hook_GetBuildingGroupsByCategoryTag, (void*)g_origGetBuildingGroups);
    QM_LOG_INFO("[Hook] Now open Build mode (B-key) to trigger the function and verify the hook fires");
    return true;
}

// ============================================================================
// UE probe pass - find R5HFSM_BuildingPanel + GetBuildingGroupsByCategoryTag.
// ============================================================================
static bool UE_ProbePass(int passNumber)
{
    using namespace QmUE;

    UClass* panelClass = FindClassByName("R5HFSM_BuildingPanel");
    if (!panelClass)
    {
        QM_LOG_DEBUG("[UE] probe#%d R5HFSM_BuildingPanel NOT FOUND in GObjects", passNumber);
        return false;
    }

    int totalFields = 0;
    int funcCount = 0;
    UField* field = panelClass->Children;
    while (field)
    {
        totalFields++;
        if (field->Class && (field->Class->CastFlags & CASTFLAG_Function) != 0)
            funcCount++;
        field = field->Next;
    }

    UFunction* target = FindFunctionOnClass(panelClass, "GetBuildingGroupsByCategoryTag");

    QM_LOG_INFO("[UE] probe#%d panelClass=0x%p (idx=%d) Children=0x%p fields=%d funcs=%d target=%s",
        passNumber, panelClass, panelClass->Index,
        (void*)panelClass->Children, totalFields, funcCount,
        target ? "FOUND" : "missing");

    if (!target) return false;

    QM_LOG_INFO("[UE] *** GO *** UFunction GetBuildingGroupsByCategoryTag = 0x%p ExecFn=0x%p Flags=0x%08X",
        target, (void*)target->ExecFunction, target->FunctionFlags);

#if QM_DIAG
    // List all the panel's UFunctions - good for spotting newly-added
    // BlueprintCallable functions after game updates.
    char nameBuf[256];
    field = panelClass->Children;
    int idx = 0;
    while (field)
    {
        if (field->Class && (field->Class->CastFlags & CASTFLAG_Function) != 0)
        {
            if (ResolveFNameNarrow(field->Name, nameBuf, sizeof(nameBuf)))
            {
                UFunction* fn = reinterpret_cast<UFunction*>(field);
                QM_LOG_DEBUG("[UE]   fn[%d] = '%s' ExecFn=0x%p Flags=0x%08X",
                    idx++, nameBuf, (void*)fn->ExecFunction, fn->FunctionFlags);
            }
        }
        field = field->Next;
    }
#endif

    InstallGetBuildingGroupsHook(target);

    // Verify GameplayStatics/SpawnObject chain. Item-class lookup is lazy
    // (resolved from donor->Class at hit#1) because WBP_Building_Item_C only
    // gets registered after the player enters build mode.
    QmUE::UClass*    gsCls = QmUE::FindClassByName("GameplayStatics");
    QmUE::UFunction* sof   = gsCls ? QmUE::FindFunctionOnClass(gsCls, "SpawnObject") : nullptr;
    QmUE::UObject*   gsCDO = QmUE::GetClassDefaultObject(gsCls);

    QM_LOG_INFO("[Spawn] probe: GameplayStatics=0x%p SpawnObject=0x%p CDO=0x%p (item-class resolved lazily from donor at hit#1)",
        gsCls, sof, gsCDO);

    if (gsCls && sof && gsCDO)
        QM_LOG_INFO("[Spawn] *** READY *** SpawnObject UFunction reachable - spawn will fire on first donor capture");
    else
        QM_LOG_WARN("[Spawn] *** NOT READY *** SpawnObject UFunction unavailable - inject will use donor-fallback");

    return true;
}

// ============================================================================
// Probe thread entry: wait for GObjects, then probe in a loop.
// ============================================================================
DWORD WINAPI QmUeProbeThreadEntry(LPVOID /*lpParam*/)
{
    QM_LOG_INFO("[UE] ProbeThread start (TID: %lu)", GetCurrentThreadId());

    HMODULE exeMod = GetModuleHandleA(NULL);
    QM_LOG_INFO("[UE] EXE base = 0x%p", exeMod);
    QM_LOG_DEBUG("[UE] expected GObjects @ 0x%p",     (void*)((uintptr_t)exeMod + QmUE::OFFSET_GObjects));
    QM_LOG_DEBUG("[UE] expected AppendString @ 0x%p", (void*)((uintptr_t)exeMod + QmUE::OFFSET_AppendString));

    // Phase 1: wait until GObjects is allocated and reasonably populated.
    // 100k threshold guarantees native class registration has finished.
    const int kInitMaxAttempts = 300;     // 300 * 500ms = 2.5 min
    int lastReported = 0;
    bool initOK = false;
    int initAttempts = 0;
    for (int attempt = 0; attempt < kInitMaxAttempts; ++attempt)
    {
        if (QmUE::Init())
        {
            QmUE::TUObjectArray* arr = QmUE::GetGObjects();
            int n = arr->Num();
            if (n >= lastReported + 50000 || (!initOK && n > 0))
            {
                QM_LOG_DEBUG("[UE] init progress attempt#%d GObjects.Num=%d NumChunks=%d",
                    attempt + 1, n, arr->NumChunks);
                lastReported = n;
            }
            if (n > 100000) { initOK = true; initAttempts = attempt + 1; break; }
        }
        Sleep(500);
    }

    if (!initOK)
    {
        QM_LOG_ERROR("[UE] init NEVER reached 100000 objects - aborting probe");
        return 1;
    }

    QmUE::TUObjectArray* arr = QmUE::GetGObjects();
    QM_LOG_INFO("[UE] init reached threshold on attempt#%d - GObjects.Num=%d", initAttempts, arr->Num());

    // Phase 2: probe loop. Try every 2s until we find the function or time out.
    const int kProbeMaxAttempts = 150;    // 150 * 2s = 5 min
#if QM_DIAG
    bool firstPass = true;
#endif
    bool found = false;
    for (int p = 0; p < kProbeMaxAttempts; ++p)
    {
        if (UE_ProbePass(p + 1)) { found = true; break; }
#if QM_DIAG
        if (firstPass)
        {
            firstPass = false;
            QmUE::UClass* panelClass = QmUE::FindClassByName("R5HFSM_BuildingPanel");
            if (panelClass) DiagDumpClassBytes(panelClass, "R5HFSM_BuildingPanel");
            int hits = DiagFindUFunctionsByName("GetBuildingGroupsByCategoryTag", 5);
            QM_LOG_DEBUG("[UE] diag: %d UFunction(s) named 'GetBuildingGroupsByCategoryTag' in GObjects", hits);
        }
#endif
        Sleep(2000);
    }

    if (!found)
    {
        QM_LOG_ERROR("[UE] *** TIMEOUT *** GetBuildingGroupsByCategoryTag never found via Children walk");
#if QM_DIAG
        int hits = DiagFindUFunctionsByName("GetBuildingGroupsByCategoryTag", 5);
        QM_LOG_DEBUG("[UE] final diag: %d direct-name UFunction hits in GObjects", hits);
#endif
    }
    return found ? 0 : 1;
}
