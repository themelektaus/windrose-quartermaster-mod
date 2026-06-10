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
#include "qm_alloc.hpp"
#include "qm_weather.hpp"
#include "qm_killxp.hpp"
#include "qm_shanty.hpp"
#include "qm_modtab.hpp"

// ============================================================================
// Detour.
// ============================================================================
static QmUE::FNativeFuncPtr g_origGetBuildingGroups = nullptr;

// CreateTabsData diagnostic - want to know:
//   * Does it run on the SAME thread as GetBuildingGroupsByCategoryTag?
//   * Does it run BEFORE GetBuildingGroupsByCategoryTag (within ms)?
//   * Does CreateTabsData itself call FMallocBinned3 allocator (warming TLS)?
// If yes to all 3: we have a clean place to insert allocator-warmup before the
// fragile Realloc in InjectIntoGroup. Hook is read-only - forwards original
// unchanged.
static QmUE::FNativeFuncPtr g_origCreateTabsData = nullptr;
static volatile LONG g_createTabsDataHits = 0;
static volatile LONG g_lastCreateTabsDataTick = 0;   // GetTickCount() of last call
static volatile LONG g_lastCreateTabsDataTID  = 0;

static void __fastcall Hook_CreateTabsData(void* Context, void* Stack, void* Result)
{
    long n = InterlockedIncrement(&g_createTabsDataHits);
    DWORD tid = GetCurrentThreadId();
    DWORD tick = GetTickCount();
    InterlockedExchange(&g_lastCreateTabsDataTick, static_cast<LONG>(tick));
    InterlockedExchange(&g_lastCreateTabsDataTID,  static_cast<LONG>(tid));

    if (n <= 10 || (n % 50) == 0)
        QM_LOG_DEBUG("[CreateTabs] hit#%ld pre  TID=%lu Ctx=0x%p Stack=0x%p Result=0x%p tick=%lu",
            n, tid, Context, Stack, Result, tick);

    if (g_origCreateTabsData)
    {
        __try { g_origCreateTabsData(Context, Stack, Result); }
        __except (EXCEPTION_EXECUTE_HANDLER)
        {
            QM_LOG_ERROR("[CreateTabs] hit#%ld *** EXCEPTION inside original CreateTabsData ***", n);
        }
    }

    if (n <= 10 || (n % 50) == 0)
        QM_LOG_DEBUG("[CreateTabs] hit#%ld post TID=%lu (post-tick=%lu, dt=%lums)",
            n, tid, GetTickCount(), GetTickCount() - tick);
}

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

    // ---- Allocator-warmup diagnostic ----------------------------------------
    // Log how long ago CreateTabsData last fired (on which thread). The hypothesis:
    // CreateTabsData allocates via FMalloc (it builds TArrays), warming the
    // FMallocBinned3 TLS state. If GetBuildingGroupsByCategoryTag runs within the
    // same thread + a few ms of CreateTabsData, the TLS should still be warm and
    // Realloc should succeed. If they're far apart (or different threads), the
    // TLS is cold and Realloc faults.
    {
        DWORD nowTick = GetTickCount();
        DWORD myTid   = GetCurrentThreadId();
        LONG  ctTick  = InterlockedCompareExchange(&g_lastCreateTabsDataTick, 0, 0);
        LONG  ctTid   = InterlockedCompareExchange(&g_lastCreateTabsDataTID,  0, 0);
        LONG  ctHits  = InterlockedCompareExchange(&g_createTabsDataHits,     0, 0);
        if (n <= 20 || (n % 50) == 0)
            QM_LOG_DEBUG("[AllocWarmup] hit#%ld GetBuildingGroups TID=%lu  CreateTabs lastTID=%ld lastTick=%lu (hits=%ld, dt=%ldms %s)",
                n, myTid, ctTid, static_cast<unsigned long>(ctTick), ctHits,
                static_cast<long>(nowTick) - ctTick,
                (ctTid == static_cast<LONG>(myTid)) ? "same-thread" : "DIFF-THREAD");
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
    else if (fi.status && strcmp(fi.status, "item-swapped") == 0)
    {
        if (logHeader || (snap.injectsDone <= 30) || (snap.injectsDone % 25 == 0))
        {
            QM_LOG_INFO("[Foreign] hit#%ld ITEM-SWAP injected=%d custom item(s) into vanilla group via static buffer [total injects=%ld, fanout: t=%d i=%d s=%d f=%d]",
                n, fi.newNum, snap.injectsDone,
                ff.total, ff.injected, ff.skipped, ff.faulted);
        }
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
// CreateTabsData diagnostic hook (read-only). Logs hit + TID + tick so we can
// see whether GetBuildingGroupsByCategoryTag runs same-thread soon-after - the
// premise behind allocator-warmup via CreateTabsData.
// ============================================================================
static bool g_createTabsHookInstalled = false;
static bool InstallCreateTabsDataHook(QmUE::UFunction* target)
{
    if (g_createTabsHookInstalled) return true;
    if (!target || !target->ExecFunction)
    {
        QM_LOG_ERROR("[CreateTabs] cannot install - target or ExecFunction is null");
        return false;
    }
    LPVOID execAddr = reinterpret_cast<LPVOID>(target->ExecFunction);
    MH_STATUS st = MH_CreateHook(execAddr,
        reinterpret_cast<LPVOID>(&Hook_CreateTabsData),
        reinterpret_cast<LPVOID*>(&g_origCreateTabsData));
    if (st != MH_OK)
    {
        QM_LOG_ERROR("[CreateTabs] MH_CreateHook(CreateTabsData @ 0x%p) FAILED: %s",
            execAddr, MH_StatusToString(st));
        return false;
    }
    st = MH_EnableHook(execAddr);
    if (st != MH_OK)
    {
        QM_LOG_ERROR("[CreateTabs] MH_EnableHook(CreateTabsData @ 0x%p) FAILED: %s",
            execAddr, MH_StatusToString(st));
        return false;
    }
    g_createTabsHookInstalled = true;
    QM_LOG_INFO("[CreateTabs] *** INSTALLED *** CreateTabsData ExecFn=0x%p detour=0x%p trampoline=0x%p (diag-only, forwards original)",
        execAddr, (void*)&Hook_CreateTabsData, (void*)g_origCreateTabsData);
    return true;
}

// ============================================================================
// Savegame-load-time pre-warm hook
// ----------------------------------------------------------------------------
// The premise. Placed custom buildings render invisible after savegame load
// (until the first build action retroactively hydrates all instances). Root
// cause: the saved TSoftObjectPtr<UR5BuildingItem> references our mod-pak
// DA_BI_QmBldg_* paths (emitted by the patcher under WindrosePaths.Mod-
// ItemsPackagePath on the C# side) which the AssetManager filter
// rejected at boot, so the actor deserializer's TryLoad returns null and the
// mesh-component attach silently fails. Once ANY code path (e.g. our
// build-menu inject's FNameFromString) pokes the PackageStore for the same
// path, all in-world instances pop in simultaneously - they share the now-
// cached resolution.
//
// What we tried first (worker-thread polling + LoadAsset_Blocking): the
// worker-thread path crashed on map-transition because UE5 GC runs in the
// game thread and asserts `Illegal call to StaticFindObjectFast() while
// garbage collecting!` if any other thread calls into the asset subsystem
// concurrently. Removed - see the long comment block in QmUeProbeThreadEntry
// below for the full story.
//
// What works (this code). We hook a UFunction whose ExecFunction we detour
// via MinHook, which means the body runs ON THE GAME THREAD (ProcessEvent
// dispatches in-thread) and CAN'T overlap with GC (GC also runs in the game
// thread - the two are mutually exclusive by construction). Inside the
// handler we run QmInject_PreWarmBuildingPackages() periodically (see gating
// below), then forward to the original.
//
// Two-stage gating:
//   1. TIME THROTTLE (first gate, cheap). The ExecFunction of any Blueprint
//      implementable event is a SHARED bytecode dispatcher - hooking it
//      intercepts every BP event in the whole game (~525 Hz). The throttle
//      compares GetTickCount() against the last fire and rejects 99.998% of
//      hits in ~30ns. PreWarm runs at most once every QM_PREWARM_MIN_INTERVAL_MS.
//   2. GAMEPLAY-MAP GATE (second gate, after the throttle window expires).
//      ResolveContextMapPackage walks Context->Outer up to the UWorld's
//      package (e.g. "/Game/Maps/GenlandiaMulty"). If the package name is
//      blacklisted (Lobby / Entrance / Transition / MainMenu / FrontEnd /
//      TitleScreen), the hit is forwarded without firing PreWarm; the
//      throttle stays set so we naturally re-check after another window.
//
// History note: an earlier revision used a Context.Class filter
// (R5GameMode-or-derived) as the first gate. That fixed the 525 Hz spam but
// broke savegame pop-in: R5GameMode::BeginPlay fires exactly once per
// persistent-level activation and World-Partition sublevel mounts bring in
// actors whose Context.Class != R5GameMode. The class filter rejected every
// sublevel-mount opportunity to re-prime the PackageStore cache, so placed
// buildings stayed invisible until the player ran a build action. The
// time-throttle approach naturally heartbeats every 5s as long as ANY BP
// event keeps firing in a gameplay map, covering sublevel mounts and
// save/load events with no class-specific tuning.
//
// The target. We don't know up-front which UFunction is the most reliable
// fire-once-after-savegame-load anchor in this specific game build, so we
// probe a list of candidates and install on the first hit. Candidates are
// ordered most-likely-to-fire-once-cleanly first; per-Tick UFunctions sit at
// the bottom as a fallback (gameplay-map gate + world-change reset make
// them well-behaved).
// ============================================================================
static QmUE::FNativeFuncPtr g_origLifecycleFunc      = nullptr;
// Pre-warm gating via TIME THROTTLE rather than class filter.
//
// History: an earlier iteration used a Context.Class filter ("must be R5GameMode
// or derived") as the first gate. That fixed the 525 Hz dispatcher spam but
// broke the savegame pop-in: R5GameMode::BeginPlay fires exactly once per
// persistent-level activation, and World-Partition sublevel mounts (e.g.
// LS_Genlandia) bring in actors whose Context.Class != R5GameMode - so the
// class filter rejected every sublevel-mount opportunity to re-prime the
// PackageStore cache and placed buildings stayed invisible.
//
// Couldn't fix via a periodic worker thread either: LoadAsset_Blocking from
// any non-game-thread crashes UE5 with `Illegal call to StaticFindObjectFast()
// while garbage collecting!` on map-load GC (see qm_inject.cpp's long
// disabled-pre-warm-thread comment block).
//
// The fix: time throttle as FIRST gate. The hook still fires ~525 Hz across
// all BlueprintEvents (shared bytecode dispatcher) but the time check rejects
// 99.998% of hits in ~30ns - no Outer walk, no allocation, no class resolve.
// Real PreWarm work runs at most once every QM_PREWARM_MIN_INTERVAL_MS, from
// the game thread (same GC-safety as before). Heartbeat naturally catches
// sublevel mounts and save/load events because SOME BP event always fires
// shortly after they happen, and the next throttle-window-expiry triggers
// PreWarm.
//
// Threading: g_lastPreWarmTickMs is updated via InterlockedCompareExchange so
// concurrent hits across worker threads (rare but possible during streaming)
// don't double-fire within a single window.
static const DWORD          QM_PREWARM_MIN_INTERVAL_MS = 5000;
static volatile LONG        g_lastPreWarmTickMs       = 0;
// Total hits where the throttle gate let us through (= called PreWarm or
// at least tried to). Diagnostic only.
static volatile LONG        g_lifecycleHookHits      = 0;
// Throttled-fast-path counter - rough indication that the hook is alive even
// when no PreWarm is firing. Read-only diagnostic, no flow control depends
// on it. Atomic increment cost: ~10ns @ 525 Hz = 5us/sec total - negligible.
static volatile LONG        g_lifecycleThrottledHits = 0;
static const char*          g_lifecycleHookTargetDesc = nullptr;
// Tracks the World UObject of the most recent PreWarm fire. Diagnostic only;
// logs a "world changed" line when a fire lands on a different world than the
// previous one (e.g. sublevel mount on different UWorld pointer in World-
// Partition titles).
static QmUE::UObject* volatile g_lifecycleLastWorld  = nullptr;

// ----- Map-name gate ---------------------------------------------------------
// The hook target may fire in maps where we don't want pre-warm to run yet
// (Lobby, EntranceHall, Transition, MainMenu, etc.). Pre-warm should only fire
// when the player has actually entered a gameplay map.
//
// IMPORTANT: /Engine/Transient is treated as "gameplay" here on purpose.
// World-Partition titles like Windrose route some actor BeginPlay events
// through actors whose Outer chain doesn't quite reach the real UWorld
// package - they resolve to /Engine/Transient. Empirically these fires are
// the ones that actually rescue placed savegame buildings from invisibility;
// excluding them broke the fix. The per-world fire counter caps the cost.
//
// The gate is a NEGATIVE blacklist (only known-bad maps are skipped). Cooked
// gameplay maps under /Game/Maps/ and the /Engine/Transient placeholder both
// pass.
static bool IsNonGameplayMap(const char* mapPkg)
{
    // Empty / unresolved map name -> let it through. World-Partition fires
    // and many actor-BeginPlay events arrive with an empty Outer chain
    // (Context->Outer doesn't reach UWorld in 5 hops). These are the fires
    // that move the needle for the savegame pop-in symptom, so they must
    // not be skipped. The per-world fire counter ensures we don't run
    // pre-warm hundreds of times per second even on a hot tick path.
    if (!mapPkg || !mapPkg[0]) return false;
    // Negative blacklist for menu/transition maps.
    if (strstr(mapPkg, "EntranceHall")) return true;
    if (strstr(mapPkg, "Entrance"))     return true;
    if (strstr(mapPkg, "Lobby"))        return true;
    if (strstr(mapPkg, "Transition"))   return true;
    if (strstr(mapPkg, "MainMenu"))     return true;
    if (strstr(mapPkg, "FrontEnd"))     return true;
    if (strstr(mapPkg, "TitleScreen"))  return true;
    return false;
}

// Best-effort extraction of "which map am I in?" from a GameMode/PlayerController/
// Character context. The Outer chain for any actor is:
//   Actor -> ULevel (PersistentLevel) -> UWorld -> UPackage (e.g. "/Game/Maps/GenlandiaMulty")
// We walk up to 4 hops looking for the package whose name starts with "/Game/".
// SEH-guarded throughout - any null/garbage in the chain falls back to a
// zero-length result, which IsNonGameplayMap treats as "skip".
static bool ResolveContextMapPackage(QmUE::UObject* ctx, char* outBuf, int outCap, QmUE::UObject** outWorld)
{
    if (outBuf && outCap > 0) outBuf[0] = '\0';
    if (outWorld) *outWorld = nullptr;
    if (!ctx || !outBuf || outCap <= 0) return false;

    QmUE::UObject* cur = ctx;
    QmUE::UObject* foundWorld = nullptr;
    for (int hop = 0; hop < 5; ++hop)
    {
        QmUE::UObject* outer = nullptr;
        __try { outer = cur->Outer; }
        __except (EXCEPTION_EXECUTE_HANDLER) { return false; }
        if (!outer) break;

        // Check class-name to detect when we reach the UWorld in the chain.
        char clsName[64] = {0};
        TryResolveContextClassName(outer, clsName, sizeof(clsName));
        if (strcmp(clsName, "World") == 0)
            foundWorld = outer;

        // Try to read the outer's own name. If it starts with "/Game/" we've
        // hit the map package and we're done.
        char nameBuf[256] = {0};
        bool ok = false;
        __try { ok = QmUE::ResolveFNameNarrow(outer->Name, nameBuf, sizeof(nameBuf)); }
        __except (EXCEPTION_EXECUTE_HANDLER) { ok = false; }
        if (ok && nameBuf[0] == '/')
        {
            strncpy(outBuf, nameBuf, outCap - 1);
            outBuf[outCap - 1] = '\0';
            if (outWorld) *outWorld = foundWorld;
            return true;
        }

        cur = outer;
    }

    if (outWorld) *outWorld = foundWorld;
    return false;
}

// FFrame layout (UE5): the FOutputDevice header occupies 0x10, then the script frame:
//   +0x10 UFunction* Node    (the executing function)
//   +0x18 UObject*   Object  (the target object - equals the Context arg)
//   +0x28 uint8*     Locals  (the packed param block, == a ProcessEvent Parms buffer)
static constexpr size_t kFFrameNodeOff   = 0x10;
static constexpr size_t kFFrameObjectOff = 0x18;
static constexpr size_t kFFrameLocalsOff = 0x28;
static volatile LONG    g_piLayoutLogged = 0;

// Shared ProcessInternal-rider dispatch. Every pure Blueprint UFunction's ExecFunction IS
// UObject::ProcessInternal, so the lifecycle hook below (which sits on ReceiveBeginPlay's
// ExecFunction) is in fact the single MinHook allowed at ProcessInternal - MinHook rejects a
// second hook on the same address. Modules that need to observe BP-internal calls (the
// mod-settings-tab recon: CookTabs / TabsGroup.SetData / OnTabsStateChanged, which bypass
// ProcessEvent) therefore ride this one detour. Called BEFORE the lifecycle throttle so riders
// see EVERY dispatch, not just the heartbeat fires. Caller wraps this in SEH; Stack is the
// FFrame. Never alters dispatch.
static void DispatchProcessInternalRiders(void* Context, void* Stack)
{
    // Zero cost for non-modtab deploys: the lifecycle detour also fires for item/weather
    // users, so bail on a cached bool before touching the FFrame. (Currently modtab is the
    // only rider; add others above this gate or widen the condition when they appear.)
    if (!Stack || !QmModTab_ReconArmed()) return;
    QmUE::UFunction* func = *reinterpret_cast<QmUE::UFunction**>(
        reinterpret_cast<uint8_t*>(Stack) + kFFrameNodeOff);
    void* locals = *reinterpret_cast<void**>(
        reinterpret_cast<uint8_t*>(Stack) + kFFrameLocalsOff);

    // One-time layout confirmation: Stack.Object must equal the Context arg, which validates
    // our FFrame offsets empirically before we trust Node/Locals.
    if (InterlockedCompareExchange(&g_piLayoutLogged, 1, 0) == 0)
    {
        QmUE::UObject* sObj = *reinterpret_cast<QmUE::UObject**>(
            reinterpret_cast<uint8_t*>(Stack) + kFFrameObjectOff);
        QM_LOG_INFO("[PI] first dispatch: context=0x%p Stack.Object=0x%p Stack.Node=0x%p Stack.Locals=0x%p (Object==context: %s)",
            Context, (void*)sObj, (void*)func, locals,
            (sObj == reinterpret_cast<QmUE::UObject*>(Context)) ? "yes" : "NO(layout mismatch)");
    }

    QmModTab_OnProcessInternal(reinterpret_cast<QmUE::UObject*>(Context), func, locals);
}

static void __fastcall Hook_LifecyclePreWarm(void* Context, void* Stack, void* Result)
{
    // ProcessInternal riders (mod-settings-tab recon): this detour sits on the shared
    // ProcessInternal dispatcher, so observe BP-internal calls here, BEFORE the throttle gate
    // below drops the vast majority of hits. SEH-guarded; never alters dispatch.
    __try { DispatchProcessInternalRiders(Context, Stack); }
    __except (EXCEPTION_EXECUTE_HANDLER) {}

    // ---- FIRST GATE: time throttle ----------------------------------------
    // The ExecFunction we hook is a shared bytecode dispatcher (every Blueprint
    // implementable event routes through it), so this detour fires ~525 Hz in
    // a busy gameplay map. We reject 99.998% of hits in ~30ns - just a tick
    // read and an unsigned diff. No Outer walk, no class resolve, no alloc.
    // Real PreWarm work runs at most every QM_PREWARM_MIN_INTERVAL_MS (5s).
    DWORD now  = GetTickCount();
    DWORD last = (DWORD)g_lastPreWarmTickMs;
    if (last != 0 && (DWORD)(now - last) < QM_PREWARM_MIN_INTERVAL_MS)
    {
        InterlockedIncrement(&g_lifecycleThrottledHits);
        if (g_origLifecycleFunc)
        {
            __try { g_origLifecycleFunc(Context, Stack, Result); }
            __except (EXCEPTION_EXECUTE_HANDLER) { /* swallow */ }
        }
        return;
    }

    // Throttle window expired. Atomically claim the next fire slot so that if
    // two threads race past the read above only one proceeds with the work.
    LONG observed = InterlockedCompareExchange(&g_lastPreWarmTickMs,
                                               (LONG)now, (LONG)last);
    if ((DWORD)observed != last)
    {
        // Lost race - some other thread fired first within nanoseconds.
        InterlockedIncrement(&g_lifecycleThrottledHits);
        if (g_origLifecycleFunc)
        {
            __try { g_origLifecycleFunc(Context, Stack, Result); }
            __except (EXCEPTION_EXECUTE_HANDLER) { /* swallow */ }
        }
        return;
    }

    LONG nHits     = InterlockedIncrement(&g_lifecycleHookHits);
    LONG throttled = InterlockedExchange(&g_lifecycleThrottledHits, 0);

    // ---- SECOND GATE: gameplay map check ----------------------------------
    // Now safe to do the expensive Outer walk (rate-limited by the throttle to
    // ~12 calls/min). Resolve the current map package and World pointer.
    char mapPkg[256] = {0};
    QmUE::UObject* world = nullptr;
    bool gotMap = ResolveContextMapPackage(reinterpret_cast<QmUE::UObject*>(Context),
                                           mapPkg, sizeof(mapPkg), &world);

    // World-change diagnostic. The pointer comparison is informational - the
    // time throttle subsumes the per-world fire-count gating that earlier
    // revisions used. Useful for understanding sublevel mount timing in logs.
    if (world)
    {
        QmUE::UObject* prev = reinterpret_cast<QmUE::UObject*>(
            InterlockedExchangePointer(reinterpret_cast<void* volatile*>(&g_lifecycleLastWorld), world));
        if (prev && prev != world)
        {
            QM_LOG_INFO("[PreWarm] world changed since last fire (prev=0x%p new=0x%p map='%s')",
                prev, world, gotMap ? mapPkg : "<unknown>");
        }
    }

    const bool nonGameplay = IsNonGameplayMap(gotMap ? mapPkg : "");
    if (nonGameplay)
    {
        // Lobby/Entrance/Transition/MainMenu/FrontEnd/TitleScreen: skip
        // PreWarm. The lastPreWarmTickMs is already set so we'll naturally
        // re-check in another QM_PREWARM_MIN_INTERVAL_MS - good behavior for
        // when the player transitions Lobby -> gameplay map (next 5s window
        // catches the first gameplay-map BP event).
        if (nHits <= 10 || (nHits % 100) == 0)
            QM_LOG_TRACE("[PreWarm] hit#%ld map='%s' is non-gameplay - skip pre-warm, forwarding "
                         "(throttled %ld hits since previous fire)",
                nHits, gotMap ? mapPkg : "<unresolved>", throttled);
    }
    else
    {
        // Gameplay map (or empty/Transient): fire PreWarm. Heartbeat mode -
        // this runs every QM_PREWARM_MIN_INTERVAL_MS as long as BP events
        // are happening in a gameplay map, which naturally covers initial
        // map load, sublevel mounts, and save/load events.
        char ctxCls[64] = {0};
        TryResolveContextClassName(reinterpret_cast<QmUE::UObject*>(Context),
                                   ctxCls, sizeof(ctxCls));
        QM_LOG_INFO("[PreWarm] *** FIRE #%ld *** via %s (TID=%lu ctxCls='%s' map='%s' world=0x%p "
                    "throttled=%ld since last fire) - running building DA pre-warm now",
            nHits,
            g_lifecycleHookTargetDesc ? g_lifecycleHookTargetDesc : "<unknown>",
            GetCurrentThreadId(),
            ctxCls[0] ? ctxCls : "<?>",
            gotMap ? mapPkg : "<unresolved>", world, throttled);

        __try { QmInject_PreWarmBuildingPackages(); }
        __except (EXCEPTION_EXECUTE_HANDLER)
        {
            QM_LOG_ERROR("[PreWarm] *** EXCEPTION inside pre-warm sweep - lifecycle hook caught fault, "
                         "forwarding original anyway");
        }

        // Weather PoC heartbeat (Option B). Same game-thread + gameplay-map
        // gating as pre-warm; pins the live R5N_WeatherComponent::CheatWeatherID
        // to the qm_weather.txt sentinel value. No-op unless the sentinel armed.
        __try { QmWeather_Heartbeat(); }
        __except (EXCEPTION_EXECUTE_HANDLER)
        {
            QM_LOG_ERROR("[Weather] *** EXCEPTION inside weather heartbeat - lifecycle hook caught fault");
        }
    }

    if (g_origLifecycleFunc)
    {
        __try { g_origLifecycleFunc(Context, Stack, Result); }
        __except (EXCEPTION_EXECUTE_HANDLER)
        {
            if (nHits <= 5)
                QM_LOG_ERROR("[PreWarm] *** EXCEPTION inside original lifecycle function (hit#%ld)", nHits);
        }
    }
}

static bool g_lifecycleHookInstalled = false;
static bool InstallLifecyclePreWarmHook(QmUE::UFunction* target, const char* desc)
{
    if (g_lifecycleHookInstalled) return true;
    if (!target || !target->ExecFunction)
    {
        QM_LOG_ERROR("[PreWarm] cannot install lifecycle hook - target or ExecFunction is null");
        return false;
    }

    LPVOID execAddr = reinterpret_cast<LPVOID>(target->ExecFunction);
    MH_STATUS st = MH_CreateHook(execAddr,
        reinterpret_cast<LPVOID>(&Hook_LifecyclePreWarm),
        reinterpret_cast<LPVOID*>(&g_origLifecycleFunc));
    if (st != MH_OK)
    {
        QM_LOG_ERROR("[PreWarm] MH_CreateHook(%s @ 0x%p) FAILED: %s",
            desc, execAddr, MH_StatusToString(st));
        return false;
    }
    st = MH_EnableHook(execAddr);
    if (st != MH_OK)
    {
        QM_LOG_ERROR("[PreWarm] MH_EnableHook(%s @ 0x%p) FAILED: %s",
            desc, execAddr, MH_StatusToString(st));
        return false;
    }
    g_lifecycleHookInstalled = true;
    g_lifecycleHookTargetDesc = desc;
    QM_LOG_INFO("[PreWarm] *** INSTALLED *** lifecycle hook %s ExecFn=0x%p detour=0x%p trampoline=0x%p "
                "(gate: time throttle at %lu ms between fires; gameplay-map filter on each fire)",
        desc, execAddr, (void*)&Hook_LifecyclePreWarm, (void*)g_origLifecycleFunc,
        (unsigned long)QM_PREWARM_MIN_INTERVAL_MS);
    return true;
}

// Probe-list of (className, funcName, desc). Searched top-to-bottom, first
// match wins. Order them most-likely-fire-once first; per-Tick fallbacks last
// because the latch makes them safe but they're noisier.
//
// BlueprintImplementableEvents are exposed as UFunctions on the BP-derived
// class only if the BP overrides them. So BP_R5GameMode_C::ReceiveBeginPlay
// exists iff the BP has a custom BeginPlay graph - which it almost always
// does in a shipped game.
struct LifecycleTarget
{
    const char* className;
    const char* funcName;
    const char* desc;
};
static const LifecycleTarget kLifecycleTargets[] =
{
    // BP-override BeginPlay - highest confidence, fires exactly once per map load
    {"BP_R5GameMode_C",         "ReceiveBeginPlay", "BP_R5GameMode_C::ReceiveBeginPlay"},
    {"BP_R5PlayerController_C", "ReceiveBeginPlay", "BP_R5PlayerController_C::ReceiveBeginPlay"},
    {"BP_R5PlayerCharacter_C",  "ReceiveBeginPlay", "BP_R5PlayerCharacter_C::ReceiveBeginPlay"},
    // Native BlueprintNativeEvent - if the BP doesn't override, the native UFunction may still exist
    {"R5GameMode",              "ReceiveBeginPlay", "R5GameMode::ReceiveBeginPlay"},
    {"R5PlayerController",      "ReceiveBeginPlay", "R5PlayerController::ReceiveBeginPlay"},
    {"R5PlayerCharacter",       "ReceiveBeginPlay", "R5PlayerCharacter::ReceiveBeginPlay"},
    {"R5Character",             "ReceiveBeginPlay", "R5Character::ReceiveBeginPlay"},
    // Tick fallbacks - per-frame but latch makes per-Tick safe
    {"BP_R5GameMode_C",         "ReceiveTick",      "BP_R5GameMode_C::ReceiveTick"},
    {"BP_R5PlayerController_C", "ReceiveTick",      "BP_R5PlayerController_C::ReceiveTick"},
    {"BP_R5PlayerCharacter_C",  "ReceiveTick",      "BP_R5PlayerCharacter_C::ReceiveTick"},
};
static constexpr int kLifecycleTargetCount = sizeof(kLifecycleTargets) / sizeof(kLifecycleTargets[0]);

static bool TryProbeLifecycleHook(int passNumber)
{
    if (g_lifecycleHookInstalled) return true;

    for (int i = 0; i < kLifecycleTargetCount; ++i)
    {
        const LifecycleTarget& t = kLifecycleTargets[i];
        QmUE::UClass* cls = QmUE::FindClassByName(t.className);
        if (!cls) continue;
        QmUE::UFunction* fn = QmUE::FindFunctionOnClass(cls, t.funcName);
        if (!fn || !fn->ExecFunction)
        {
            if (passNumber <= 3 || (passNumber % 30) == 0)
                QM_LOG_TRACE("[PreWarm] probe#%d candidate %d/%d: %s -> class FOUND but function missing (BP-override probably absent)",
                    passNumber, i + 1, kLifecycleTargetCount, t.desc);
            continue;
        }
        QM_LOG_INFO("[PreWarm] probe#%d candidate %d/%d HIT: %s class=0x%p fn=0x%p ExecFn=0x%p Flags=0x%08X",
            passNumber, i + 1, kLifecycleTargetCount, t.desc, cls, fn,
            (void*)fn->ExecFunction, fn->FunctionFlags);
        if (InstallLifecyclePreWarmHook(fn, t.desc))
            return true;
    }
    if (passNumber <= 3 || (passNumber % 30) == 0)
        QM_LOG_DEBUG("[PreWarm] probe#%d: no lifecycle target yet (tried %d candidates) - "
                     "will retry in 2s. Class registration happens as the map BP loads.",
            passNumber, kLifecycleTargetCount);
    return false;
}

// ============================================================================
// Stage-2 item-use detection RECON: hook R5ConsumeAbility::EventReceived.
// ----------------------------------------------------------------------------
// Goal of stage 2 (Docs/PLAN-WeatherControl-WIP.md): when the player USES a
// consumable, identify WHICH item it was so a future "Weather Control" can
// trigger a weather change. EventReceived is the native handler the consume
// ability runs when its montage / GAS event lands; signature is
//   EventReceived(FGameplayTag EventTag, FGameplayEventData EventData)
// (R5_classes.hpp:16310). We hook its ExecFunction (game-thread dispatch, same
// pattern as the other hooks) and log every candidate item-identity field so a
// SINGLE in-game test answers the two open unknowns: (1) does this ExecFunction
// thunk fire at all on item use, and (2) which field carries the consumed
// inventory-item DA (so we can match our custom item by name next).
//
// Fields logged:
//   - Context  = the UR5ConsumeAbility instance (class + own name)
//   - Context->Params_0 @ 0x3C0 = UR5ConsumeAbilityData (e.g.
//     DA_ConsumableAbilityData_SpawnerBoar). NOTE: a boar-whistle CLONE shares
//     this asset, so Params_0 alone is NOT a per-item discriminator - hence we
//     need the consumed-item DA below.
//   - EventTag (the GAS event)
//   - EventData.Instigator / Target / OptionalObject / OptionalObject2.
//     OptionalObject (FGameplayEventData @ +0x18, GameplayAbilities_structs.hpp:
//     1010) is the prime candidate for the consumed item DA - confirm in-game.
//
// Params come from the FFrame: when EventReceived is dispatched via
// ProcessEvent, FFrame.Locals (@ Stack+0x28, same layout DiagInspectInputs
// uses) points at the flat {EventTag@0x00, EventData@0x08} parms buffer (the
// Code==null path). All reads SEH-guarded. RECON ONLY - forwards the original
// unchanged, touches no weather state.
// ============================================================================
static QmUE::FNativeFuncPtr g_origConsumeEventReceived = nullptr;
static QmUE::FNativeFuncPtr g_origConsumeOnMontageEnd  = nullptr;
static QmUE::FNativeFuncPtr g_origConsumeFinishAbility  = nullptr;
static volatile LONG        g_consumeHits             = 0;

// R5ConsumeAbility::Params_0 (UR5ConsumeAbilityData*) - R5_classes.hpp:16301
static constexpr size_t kOffConsumeParams0 = 0x3C0;

// Which UR5ConsumeAbility UFunction a detour belongs to. EventReceived is the
// activation entry (fires for BP-driven food/bandage via the script thunk, but
// NOT for the purely-native spawner whistle); OnMontageEnd / FinishAbility are
// completion callbacks bound as montage-task dynamic delegates - those DO route
// through ProcessEvent for the whistle, so they are our spawner-whistle hook.
enum class ConsumeFnKind { EventReceived = 0, OnMontageEnd = 1, FinishAbility = 2 };

static void LogConsumeObjIdentity(const char* label, QmUE::UObject* obj, long n)
{
    if (!obj)
    {
        QM_LOG_INFO("[Consume] hit#%ld   %-24s = null", n, label);
        return;
    }
    char cls[128] = { 0 }, name[192] = { 0 };
    TryResolveContextClassName(obj, cls, sizeof(cls));
    __try { QmUE::ResolveFNameNarrow(obj->Name, name, sizeof(name)); }
    __except (EXCEPTION_EXECUTE_HANDLER) {}
    QM_LOG_INFO("[Consume] hit#%ld   %-24s = 0x%p Cls='%s' Name='%s'",
        n, label, obj, cls[0] ? cls : "<?>", name[0] ? name : "<?>");
}

// Shared detour core for all three UR5ConsumeAbility UFunctions. Reads the
// Params_0 (ConsumableData) discriminator + (for the tag-carrying functions)
// the EventTag, logs the identity (throttled), then routes to the right weather
// trigger: the spend-tag-gated path for EventReceived (food/bandage, e.g. the
// rum-bottle weather item), or the completion path for OnMontageEnd/FinishAbility
// (a purely-native consume whose entry never reaches the EventReceived thunk).
// SEH-guarded; POD locals only (no C++ destructors) so __try is legal here.
static void ConsumeHitCore(ConsumeFnKind kind, const char* fnName, void* Context, void* Stack)
{
    long n = InterlockedIncrement(&g_consumeHits);

    // Consume events are user-driven and rare; log full identity for the first
    // 50 hits, then a thin heartbeat, so a spammy caller (if any) can't flood.
    const bool verbose = (n <= 50) || (n % 25 == 0);

    QmUE::UObject* ability = reinterpret_cast<QmUE::UObject*>(Context);

    // ---- read the discriminators on EVERY hit (cheap; needed for the trigger) ----
    // Context->Params_0 -> the R5ConsumeAbilityData (e.g. DA_ConsumableAbilityData_Bandages_T01).
    QmUE::UObject* params0 = nullptr;
    char params0Name[192] = { 0 };
    if (ability)
    {
        __try { params0 = *reinterpret_cast<QmUE::UObject**>(
                    reinterpret_cast<uint8_t*>(ability) + kOffConsumeParams0); }
        __except (EXCEPTION_EXECUTE_HANDLER) { params0 = nullptr; }
        if (params0)
        {
            __try { QmUE::ResolveFNameNarrow(params0->Name, params0Name, sizeof(params0Name)); }
            __except (EXCEPTION_EXECUTE_HANDLER) { params0Name[0] = '\0'; }
        }
    }

    // EventReceived + OnMontageEnd carry (FGameplayTag EventTag, FGameplayEventData
    // EventData); FinishAbility takes no params, so skip the FFrame read for it.
    const bool hasTagParam = (kind == ConsumeFnKind::EventReceived ||
                              kind == ConsumeFnKind::OnMontageEnd);
    void* locals = nullptr;
    if (hasTagParam && Stack)
    {
        __try { locals = *reinterpret_cast<void**>(reinterpret_cast<uint8_t*>(Stack) + 0x28); }
        __except (EXCEPTION_EXECUTE_HANDLER) { locals = nullptr; }
    }
    char evTagStr[200] = { 0 };
    if (locals)
    {
        QmUE::FName evTag = {};
        __try { evTag = *reinterpret_cast<QmUE::FName*>(reinterpret_cast<uint8_t*>(locals) + 0x00); }
        __except (EXCEPTION_EXECUTE_HANDLER) {}
        if (!evTag.IsNone() && !QmUE::ResolveFNameNarrow(evTag, evTagStr, sizeof(evTagStr)))
            snprintf(evTagStr, sizeof(evTagStr), "<cmp=%d num=%u>", evTag.ComparisonIndex, evTag.Number);
    }

    if (verbose)
    {
        QM_LOG_INFO("[Consume] *** %s hit#%ld *** TID=%lu Ctx=0x%p Stack=0x%p",
            fnName, n, GetCurrentThreadId(), Context, Stack);
        LogConsumeObjIdentity("ability", ability, n);
        LogConsumeObjIdentity("Params_0(abilityData)", params0, n);
        if (hasTagParam)
            QM_LOG_INFO("[Consume] hit#%ld   EventTag                  = '%s'", n, evTagStr[0] ? evTagStr : "<none>");

        if (locals)
        {
            uint8_t* p = reinterpret_cast<uint8_t*>(locals);
            // EventData @ +0x08: Instigator@+0x08 Target@+0x10 OptionalObject@+0x18 OptionalObject2@+0x20
            QmUE::UObject *instig = nullptr, *target = nullptr, *opt1 = nullptr, *opt2 = nullptr;
            __try {
                instig = *reinterpret_cast<QmUE::UObject**>(p + 0x08 + 0x08);
                target = *reinterpret_cast<QmUE::UObject**>(p + 0x08 + 0x10);
                opt1   = *reinterpret_cast<QmUE::UObject**>(p + 0x08 + 0x18);
                opt2   = *reinterpret_cast<QmUE::UObject**>(p + 0x08 + 0x20);
            } __except (EXCEPTION_EXECUTE_HANDLER) {}
            LogConsumeObjIdentity("EventData.Instigator", instig, n);
            LogConsumeObjIdentity("EventData.Target", target, n);
            LogConsumeObjIdentity("EventData.OptionalObject", opt1, n);
            LogConsumeObjIdentity("EventData.OptionalObject2", opt2, n);
        }
        else if (hasTagParam)
        {
            QM_LOG_INFO("[Consume] hit#%ld   FFrame.Locals null - params unreadable via Locals (Code!=null path?)", n);
        }
    }

    // ---- weather trigger -----------------------------------------------------
    // EventReceived: spend-tag-gated (food/bandage path, proven in stage 2b - this
    // is how the rum-bottle weather item fires). OnMontageEnd / FinishAbility:
    // completion path, substring match only, which catches a purely-native consume
    // whose entry EventReceived never hits this thunk. The weather module owns the
    // match + write + debounce; we just feed it the discriminators.
    if (params0Name[0])
    {
        int applied = -1;
        if (kind == ConsumeFnKind::EventReceived)
        {
            if (evTagStr[0]) applied = QmWeather_TryConsumableTrigger(params0Name, evTagStr);
        }
        else
        {
            applied = QmWeather_TryConsumableTriggerOnComplete(params0Name, fnName);
        }
        if (applied >= 0)
            QM_LOG_INFO("[Consume] hit#%ld   -> WEATHER TRIGGERED (id=%d) by '%s' via %s", n, applied, params0Name, fnName);
    }
}

static void __fastcall Hook_ConsumeEventReceived(void* Context, void* Stack, void* Result)
{
    ConsumeHitCore(ConsumeFnKind::EventReceived, "EventReceived", Context, Stack);
    if (g_origConsumeEventReceived)
    {
        __try { g_origConsumeEventReceived(Context, Stack, Result); }
        __except (EXCEPTION_EXECUTE_HANDLER)
        { QM_LOG_ERROR("[Consume] *** EXCEPTION inside original EventReceived ***"); }
    }
}

static void __fastcall Hook_ConsumeOnMontageEnd(void* Context, void* Stack, void* Result)
{
    ConsumeHitCore(ConsumeFnKind::OnMontageEnd, "OnMontageEnd", Context, Stack);
    if (g_origConsumeOnMontageEnd)
    {
        __try { g_origConsumeOnMontageEnd(Context, Stack, Result); }
        __except (EXCEPTION_EXECUTE_HANDLER)
        { QM_LOG_ERROR("[Consume] *** EXCEPTION inside original OnMontageEnd ***"); }
    }
}

static void __fastcall Hook_ConsumeFinishAbility(void* Context, void* Stack, void* Result)
{
    ConsumeHitCore(ConsumeFnKind::FinishAbility, "FinishAbility", Context, Stack);
    if (g_origConsumeFinishAbility)
    {
        __try { g_origConsumeFinishAbility(Context, Stack, Result); }
        __except (EXCEPTION_EXECUTE_HANDLER)
        { QM_LOG_ERROR("[Consume] *** EXCEPTION inside original FinishAbility ***"); }
    }
}

static bool g_consumeHookInstalled = false;

// Generic single-UFunction exec-thunk hook installer. Returns true on success.
static bool InstallConsumeFnHook(QmUE::UClass* cls, const char* fnName,
                                 LPVOID detour, QmUE::FNativeFuncPtr* origOut)
{
    QmUE::UFunction* fn = QmUE::FindFunctionOnClass(cls, fnName);
    if (!fn || !fn->ExecFunction)
    {
        QM_LOG_WARN("[Consume] R5ConsumeAbility::%s missing/no-exec - skipped", fnName);
        return false;
    }
    LPVOID execAddr = reinterpret_cast<LPVOID>(fn->ExecFunction);
    MH_STATUS st = MH_CreateHook(execAddr, detour, reinterpret_cast<LPVOID*>(origOut));
    if (st != MH_OK)
    {
        QM_LOG_ERROR("[Consume] MH_CreateHook(%s @ 0x%p) FAILED: %s", fnName, execAddr, MH_StatusToString(st));
        return false;
    }
    st = MH_EnableHook(execAddr);
    if (st != MH_OK)
    {
        QM_LOG_ERROR("[Consume] MH_EnableHook(%s @ 0x%p) FAILED: %s", fnName, execAddr, MH_StatusToString(st));
        return false;
    }
    QM_LOG_INFO("[Consume] *** INSTALLED *** R5ConsumeAbility::%s ExecFn=0x%p detour=0x%p trampoline=0x%p Flags=0x%08X",
        fnName, execAddr, detour, (void*)*origOut, fn->FunctionFlags);
    return true;
}

// Probe for the native R5ConsumeAbility class and hook its three UFunctions.
// EventReceived (REQUIRED) catches BP-driven food/bandage; OnMontageEnd +
// FinishAbility (best-effort) catch the purely-native spawner whistle, whose
// entry EventReceived runs natively and never hits the script thunk. Native
// classes register early, so this typically installs on pass#1.
static bool TryProbeConsumeHook(int passNumber)
{
    if (g_consumeHookInstalled) return true;
    QmUE::UClass* cls = QmUE::FindClassByName("R5ConsumeAbility");
    if (!cls)
    {
        if (passNumber <= 3 || (passNumber % 30) == 0)
            QM_LOG_TRACE("[Consume] probe#%d R5ConsumeAbility not in GObjects yet", passNumber);
        return false;
    }
    QmUE::UFunction* evFn = QmUE::FindFunctionOnClass(cls, "EventReceived");
    if (!evFn || !evFn->ExecFunction)
    {
        if (passNumber <= 3 || (passNumber % 30) == 0)
            QM_LOG_TRACE("[Consume] probe#%d R5ConsumeAbility found but EventReceived missing/no-exec", passNumber);
        return false;
    }
    QM_LOG_INFO("[Consume] probe#%d HIT: R5ConsumeAbility cls=0x%p - installing EventReceived/OnMontageEnd/FinishAbility hooks",
        passNumber, cls);

    // EventReceived is the required hook; the loop must keep retrying until it
    // installs. The two completion hooks are best-effort (whistle path).
    if (!InstallConsumeFnHook(cls, "EventReceived", reinterpret_cast<LPVOID>(&Hook_ConsumeEventReceived), &g_origConsumeEventReceived))
        return false;

    InstallConsumeFnHook(cls, "OnMontageEnd",  reinterpret_cast<LPVOID>(&Hook_ConsumeOnMontageEnd),  &g_origConsumeOnMontageEnd);
    InstallConsumeFnHook(cls, "FinishAbility", reinterpret_cast<LPVOID>(&Hook_ConsumeFinishAbility), &g_origConsumeFinishAbility);

    g_consumeHookInstalled = true;
    if (QmWeather_TriggerArmed())
        QM_LOG_INFO("[Consume] weather trigger ARMED - food/bandage via spend-tag (EventReceived), spawner whistle via completion (OnMontageEnd/FinishAbility); substring='match' decides (see [Weather] *** TRIGGER ***)");
    else
        QM_LOG_INFO("[Consume] recon mode (no qm_weather_trigger.txt) - logs the consumed item identity on each consumable use");
    return true;
}

// ============================================================================
// Stage 2c-1c: global ProcessEvent net-hook for the purely-native spawner whistle
// ----------------------------------------------------------------------------
// In-game 2026-06-06 proved the spawner whistle (GA_SpawnerConsumableAbility_C, an
// EMPTY native UR5ConsumeAbility subclass) dispatches NONE of its three UFunctions
// (EventReceived/OnMontageEnd/FinishAbility) through the per-UFunction script-VM
// exec thunks we detour - zero [Consume] hits despite a confirmed whistle use.
// ProcessEvent is the ONE central dispatcher every script-routed UFunction passes
// through, so a single detour on it catches whatever the whistle DOES route via
// the VM (montage / anim-notify / ability-task dynamic delegates, gameplay events,
// BlueprintCallable spawns) - without us having to guess the function a 3rd time.
//
// Two jobs, both kept cheap (PE fires 10k-100k times/frame):
//   (1) FUNCTIONAL: when `self` derives from UR5ConsumeAbility, read its Params_0
//       (ConsumableData @ 0x3C0) and feed the name to the SAME completion-trigger
//       the thunk path uses (substring + 1.5s debounce live in qm_weather). ANY
//       PE dispatch on the spawner ability during a use -> storm. The debounce is
//       shared with the thunk path, so food/bandage can't double-fire, and the
//       "SpawnerBoar" substring means non-spawner consumables never match.
//   (2) RECON: log distinct PE calls whose owning class name matches a spawner/
//       montage/consumable keyword (rate-limited per UFunction). If (1) never fires
//       (PE never targets the ability object), this still reveals the real
//       chokepoint so the next iteration can wire it precisely.
//
// Cost control: a direct-mapped class memo (keyed by UClass*) caches the per-class
// verdict so the expensive name-resolve + super-chain walk runs ONCE per class,
// never per call; after warmup the hot path is a deref + array lookup. Installed
// ONLY when the weather trigger is armed, so non-weather users pay nothing.
// SEH-guarded; always forwards the original ProcessEvent.
// ============================================================================
static QmUE::ProcessEventFn g_origProcessEvent    = nullptr;
static QmUE::UClass*        g_consumeAbilityClass  = nullptr;   // UR5ConsumeAbility (ancestry test)
static volatile LONG        g_peHookInstalled      = 0;
static volatile LONG        g_peAbilityHits        = 0;         // PE calls on a consume-ability (diagnostic)

static constexpr uint8_t PE_VALID   = 0x80;
static constexpr uint8_t PE_ABILITY = 0x01;   // self derives from UR5ConsumeAbility
static constexpr uint8_t PE_RECON   = 0x02;   // class name matches a recon keyword

struct PeClassMemo { void* cls; volatile uint8_t verdict; };
static const uint32_t kPeClassCacheMask = (1u << 15) - 1;       // 32768 slots * ~16B = 512KB
static PeClassMemo     g_peClassCache[kPeClassCacheMask + 1];

struct PeFuncMemo { void* fn; volatile ULONGLONG tick; };
static const uint32_t kPeFuncCacheMask = (1u << 13) - 1;        // 8192 slots
static PeFuncMemo      g_peFuncCache[kPeFuncCacheMask + 1];

// Pointer walk up the SuperStruct chain (no string work). Bounded as a cycle guard.
static bool ClassDerivesFromConsumeAbility(QmUE::UClass* cls)
{
    if (!g_consumeAbilityClass || !cls) return false;
    QmUE::UStruct* s = cls;
    for (int i = 0; i < 32 && s; ++i)
    {
        if (s == static_cast<QmUE::UStruct*>(g_consumeAbilityClass)) return true;
        s = s->SuperStruct;
    }
    return false;
}

// Case-sensitive (UE class names are PascalCase, keywords match the real casing).
static bool ClassNameIsReconInteresting(QmUE::UClass* cls)
{
    char nm[160] = { 0 };
    __try { if (!QmUE::ResolveFNameNarrow(cls->Name, nm, sizeof(nm)) || !nm[0]) return false; }
    __except (EXCEPTION_EXECUTE_HANDLER) { return false; }
    static const char* const kKeywords[] = {
        "Spawn", "Boar", "Whistle", "Montage", "Consum", "Cooldown", "Notify"
    };
    for (const char* k : kKeywords)
        if (strstr(nm, k)) return true;
    return false;
}

// Memoized per-class verdict. Direct-mapped; collisions just recompute. Races are
// benign (worst case: one extra recompute, or one mis-flagged call that the
// substring/SEH guards absorb).
static uint8_t PeClassVerdict(QmUE::UClass* cls)
{
    PeClassMemo& slot = g_peClassCache[(((uintptr_t)cls) >> 4) & kPeClassCacheMask];
    if (slot.cls == cls && (slot.verdict & PE_VALID))
        return slot.verdict;
    uint8_t v = PE_VALID;
    if (ClassDerivesFromConsumeAbility(cls)) v |= PE_ABILITY;
    if (ClassNameIsReconInteresting(cls))    v |= PE_RECON;
    // Publish order matters: invalidate, set key, then publish the verdict last.
    // Under MSVC /volatile:ms (x64 default) the volatile `verdict` writes carry
    // release semantics, so the `cls` store can't float past the final publish.
    slot.verdict = 0;          // invalidate while we publish
    slot.cls     = cls;
    slot.verdict = v;          // publish complete verdict
    return v;
}

// Recon log for an "interesting" class, rate-limited per UFunction (<= 1 / 2s) so a
// busy scene can't flood and a whistle use still emits fresh, timestamped lines.
static void PeReconLog(QmUE::UObject* self, QmUE::UClass* cls, QmUE::UFunction* func)
{
    PeFuncMemo& slot = g_peFuncCache[(((uintptr_t)func) >> 4) & kPeFuncCacheMask];
    ULONGLONG now = GetTickCount64();
    if (slot.fn == func && slot.tick != 0 && (now - slot.tick) < 2000) return;
    slot.fn = func; slot.tick = now;

    char clsNm[160] = { 0 }, fnNm[160] = { 0 };
    QmUE::ResolveFNameNarrow(cls->Name,  clsNm, sizeof(clsNm));
    QmUE::ResolveFNameNarrow(func->Name, fnNm,  sizeof(fnNm));
    QM_LOG_INFO("[PE-recon] %s::%s  self=0x%p", clsNm[0] ? clsNm : "?", fnNm[0] ? fnNm : "?", self);
}

static void __fastcall Hook_ProcessEvent(QmUE::UObject* self, QmUE::UFunction* func, void* parms)
{
    bool suppress = false;   // Always-Shanties may veto forwarding a helm-leave ServerDisableShanty
    __try
    {
        QmUE::UClass* cls = (self && func) ? self->Class : nullptr;
        if (cls)
        {
            uint8_t v = PeClassVerdict(cls);

            if (v & PE_ABILITY)
            {
                // FUNCTIONAL: this is a consume-ability instance. Read its Params_0
                // (ConsumableData) and hand it to the shared completion-trigger.
                long h = InterlockedIncrement(&g_peAbilityHits);
                QmUE::UObject* params0 = *reinterpret_cast<QmUE::UObject**>(
                    reinterpret_cast<uint8_t*>(self) + kOffConsumeParams0);
                char p0[192] = { 0 };
                if (params0)
                    QmUE::ResolveFNameNarrow(params0->Name, p0, sizeof(p0));
                if (p0[0])
                {
                    char fnNm[128] = { 0 }, via[160];
                    QmUE::ResolveFNameNarrow(func->Name, fnNm, sizeof(fnNm));
                    snprintf(via, sizeof(via), "PE:%s", fnNm[0] ? fnNm : "?");
                    int applied = QmWeather_TryConsumableTriggerOnComplete(p0, via);
                    if (applied >= 0)
                        QM_LOG_INFO("[PE] *** weather triggered (id=%d) *** via %s Params_0='%s'", applied, via, p0);
                    else if (h <= 40)   // first hits: show what PE-on-ability looks like even when no match
                        QM_LOG_INFO("[PE] ability-call#%ld %s Params_0='%s' (no trigger)", h, via, p0);
                }
            }

            if (v & PE_RECON)
                PeReconLog(self, cls, func);
        }

        // XP-for-kills: kill detection + seed-free XP grant. No-op unless its
        // sentinel armed; SEH-guarded internally. Independent of the weather
        // verdict above (its own per-UFunction memo), so it sees every dispatch.
        QmKillXp_OnProcessEvent(self, func, parms);

        // Always-Shanties: keep the crew shanty playing after you leave the helm. No-op
        // unless its sentinel armed; SEH-guarded internally. Its own per-UFunction memo,
        // independent of the verdicts above, so it sees every dispatch. Returns true ONLY
        // for a helm-leave ServerDisableShanty, asking us to drop the original dispatch so
        // the disable never runs (the shanty keeps playing).
        suppress = QmShanty_OnProcessEvent(self, func, parms);

        // NOTE: the mod-settings-tab recon does NOT ride this ProcessEvent hook. Its targets
        // (CookTabs/SetData/OnTabsStateChanged) are Blueprint-internal calls that bypass
        // ProcessEvent (recon-confirmed). It rides the shared ProcessInternal detour instead
        // (DispatchProcessInternalRiders, called from Hook_LifecyclePreWarm).
    }
    __except (EXCEPTION_EXECUTE_HANDLER) {}

    if (!suppress && g_origProcessEvent)
        g_origProcessEvent(self, func, parms);
}

// Install the global ProcessEvent detour. Requires GObjects ready + ProcessEvent
// resolved (both true once we're in the probe loop). Idempotent.
static bool InstallProcessEventHook()
{
    if (InterlockedCompareExchange(&g_peHookInstalled, 1, 0) != 0) return true;

    void* pe = reinterpret_cast<void*>(QmUE::GetProcessEventFn());
    if (!pe)
    {
        QM_LOG_WARN("[PE] ProcessEvent unresolved - global net-hook skipped");
        InterlockedExchange(&g_peHookInstalled, 0);
        return false;
    }
    // For the ancestry test. If null, the FUNCTIONAL path stays off but recon runs.
    g_consumeAbilityClass = QmUE::FindClassByName("R5ConsumeAbility");

    MH_STATUS st = MH_CreateHook(pe, reinterpret_cast<LPVOID>(&Hook_ProcessEvent),
                                 reinterpret_cast<LPVOID*>(&g_origProcessEvent));
    if (st != MH_OK)
    {
        QM_LOG_ERROR("[PE] MH_CreateHook(ProcessEvent @ 0x%p) FAILED: %s", pe, MH_StatusToString(st));
        InterlockedExchange(&g_peHookInstalled, 0);
        return false;
    }
    st = MH_EnableHook(pe);
    if (st != MH_OK)
    {
        QM_LOG_ERROR("[PE] MH_EnableHook(ProcessEvent @ 0x%p) FAILED: %s", pe, MH_StatusToString(st));
        InterlockedExchange(&g_peHookInstalled, 0);
        return false;
    }
    QM_LOG_INFO("[PE] *** INSTALLED *** global ProcessEvent net-hook @ 0x%p (R5ConsumeAbility cls=0x%p) - "
                "functional spawner trigger + spawner/montage recon active",
        pe, g_consumeAbilityClass);
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

    // Diagnostic hook on CreateTabsData (read-only, forwards original). Used
    // to verify the allocator-warmup-via-CreateTabsData hypothesis.
    UFunction* ctd = FindFunctionOnClass(panelClass, "CreateTabsData");
    if (ctd)
    {
        QM_LOG_INFO("[CreateTabs] target found: 0x%p ExecFn=0x%p Flags=0x%08X",
            ctd, (void*)ctd->ExecFunction, ctd->FunctionFlags);
        InstallCreateTabsDataHook(ctd);
    }
    else
    {
        QM_LOG_WARN("[CreateTabs] target NOT FOUND on R5HFSM_BuildingPanel - diagnostic skipped");
    }

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
    // 100k threshold guarantees native class registration has finished on a
    // client. On a dedicated server without connected players, NumElements
    // stabilizes around ~95-100k (no per-player UObjects). Hypothesis:
    // once a player connects, additional UObjects load and push us past
    // the threshold. So we wait effectively forever (kInitMaxAttempts very
    // high) instead of giving up - the probe-loop is one Sleep(500) thread,
    // costs nothing while idle. If the server boots without any player ever
    // connecting, nothing bad happens - we just never enter Phase 2.
    const int kInitMaxAttempts = 14400; // 14400 * 500ms = 2 hours
    int lastReported = 0;
    bool initOK      = false;
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
            if (n > 100000)
            {
                initOK = true; initAttempts = attempt + 1;
                break;
            }
        }
        Sleep(500);
    }

    if (!initOK)
    {
        QM_LOG_ERROR("[UE] init NEVER reached threshold (>100k) within %d attempts - aborting probe",
            kInitMaxAttempts);
        return 1;
    }
    QM_LOG_INFO("[UE] init accepted on attempt#%d (>100k)", initAttempts);

    QmUE::TUObjectArray* arr = QmUE::GetGObjects();
    QM_LOG_INFO("[UE] init reached threshold on attempt#%d - GObjects.Num=%d", initAttempts, arr->Num());

    // Resolve GMalloc + InnerMalloc + reserve our ItemSwap buffer pool. Done
    // after GObjects-ready so the engine has fully initialized its allocator.
    // On failure the ItemSwap path stays disabled and items don't show - no
    // crash-prone fallback. See GAME_UPDATE_RECOVERY.md for what to do.
    if (!QmAlloc::Resolve(QmUE::GetImageBase()))
    {
        QM_LOG_WARN("[Alloc] GMalloc resolution failed - ItemSwap disabled, items will not appear in build menu");
    }

    // ------------------------------------------------------------------------
    // Savegame pre-warm: DISABLED.
    // ------------------------------------------------------------------------
    // Background: we tried sync-loading each Building-DA package via
    // UKismetSystemLibrary::LoadAsset_Blocking from a worker thread so the
    // IoStore PackageStore would have resolved entries before any savegame
    // could attempt to deserialize a placed custom building. Two problems made
    // this approach unworkable:
    //
    //   1. LoadAsset_Blocking returned nullptr unconditionally - even for a
    //      known-good vanilla DA (DA_BI_Bedroll_01) and even after 2min of
    //      polling. The R5.log showed the corresponding
    //          "Object Keine.DA_BI_xxx konnte nicht gefunden werden"
    //      warnings, which means the underlying StaticLoadObject path can't
    //      resolve a TSoftObjectPtr built solely from PackageName+AssetName
    //      FNames; the engine expects either a fully-resolved Outer or the
    //      AssetManager-registered PrimaryAssetId, neither of which we have
    //      from outside the engine.
    //
    //   2. *** WORSE: it crashed the game on map transition. ***
    //      Map-load triggers UE5 garbage collection. Our worker thread called
    //      LoadAsset_Blocking concurrently, which internally hit
    //      StaticFindObjectFast(), and the engine fatally asserts:
    //          "Illegal call to StaticFindObjectFast() while garbage
    //           collecting!"
    //          [UObjectGlobals.cpp Line 459]
    //      Fixing this would require marshaling the call back to the game
    //      thread (no public UE API for that from a foreign DLL) and gating
    //      on IsGarbageCollecting() (also not safely readable from a worker
    //      thread). Without those, ANY off-thread UFunction call into the
    //      asset-loading subsystem is a latent crash waiting to happen
    //      whenever GC fires.
    //
    // The proper fix needs a different hook: intercept the savegame
    // deserialization itself (e.g. FSoftObjectPath::TryLoad or the
    // SaveGame UFunction that materializes placed actors) and either patch
    // the path or register the package via the IoStore PackageStore directly.
    // That is significantly more work than this DLL is currently set up for;
    // for now the "build menu inject" path remains the primary workaround
    // and the savegame pop-in is a known cosmetic issue (one build action
    // hydrates all custom buildings retroactively).
    //
    // See git log for the worker-thread implementation that lived here and
    // the inject log timestamp 2026-05-21 12:24 / R5.log timestamp
    // 10.24.41:396 for the crash evidence.

    // Phase 2+3: probe loop. In each pass try BOTH:
    //   - Build-menu hook (R5HFSM_BuildingPanel + GetBuildingGroupsByCategoryTag).
    //     Once installed, the per-pass call becomes a cheap is-installed guard.
    //   - Lifecycle hook (BP_R5GameMode_C::ReceiveBeginPlay + fallbacks).
    //     Once installed, ditto.
    //
    // The two targets register at different times: build-menu can be installed
    // as soon as the player first opens the Build UI (the panel class only
    // exists in GObjects after it has been touched once). Lifecycle target's
    // BP class is registered when the player transitions out of the Lobby into
    // the actual gameplay map - typically several minutes after DLL load. So
    // we use a generous timeout that covers main-menu idle + scenario load.
    //
    // The loop exits as soon as BOTH hooks are installed (the common success
    // path) or kProbeMaxAttempts is reached (degraded - log loud warning).
    const int kProbeMaxAttempts = 900;    // 900 * 2s = 30 min - covers main-menu idle + map transitions
#if QM_DIAG
    bool firstPass = true;
#endif
    bool buildMenuFound = false;
    bool lifecycleFound = false;
    bool consumeFound   = false;
    // Global ProcessEvent net-hook is only needed (and only paid for) when the
    // weather trigger OR the kill-XP/shanty recon is armed. If none is armed, treat
    // it as already done so the probe loop's exit condition isn't held open by it.
    bool peNetDone      = !(QmWeather_TriggerArmed() || QmKillXp_ReconArmed() || QmShanty_ReconArmed());
    // Mod-settings-tab recon (CookTabs/SetData/OnTabsStateChanged) bypasses ProcessEvent; it
    // rides the lifecycle ProcessInternal detour instead (see DispatchProcessInternalRiders),
    // so there's no separate install gate - lifecycleFound below guarantees its coverage.
    int  buildMenuFoundOnPass = 0;
    int  lifecycleFoundOnPass = 0;
    int  consumeFoundOnPass   = 0;
    int  peNetDoneOnPass      = 0;

    for (int p = 0; p < kProbeMaxAttempts; ++p)
    {
        if (!buildMenuFound && UE_ProbePass(p + 1))
        {
            buildMenuFound = true;
            buildMenuFoundOnPass = p + 1;
        }
        if (!lifecycleFound && TryProbeLifecycleHook(p + 1))
        {
            lifecycleFound = true;
            lifecycleFoundOnPass = p + 1;
            QM_LOG_INFO("[PreWarm] lifecycle hook installed on probe pass#%d - savegame pop-in fix is now armed; "
                        "next BeginPlay/Tick will trigger pre-warm of %d DA package(s)",
                p + 1, g_injectableItemCount);
        }
        // Stage-2 item-use detection recon (native class -> installs ~pass#1).
        if (!consumeFound && TryProbeConsumeHook(p + 1))
        {
            consumeFound = true;
            consumeFoundOnPass = p + 1;
            QM_LOG_INFO("[Consume] hook installed on probe pass#%d - using any consumable will now log its item identity",
                p + 1);
        }
        // Stage-2c global ProcessEvent net-hook (when weather trigger OR kill-XP
        // recon is armed). Same prerequisites as the consume hook (GObjects ready
        // + ProcessEvent resolved). Kill-XP doesn't need the consume class, so it
        // can install the net-hook even if the consume probe hasn't landed yet.
        if (!peNetDone && (consumeFound || QmKillXp_ReconArmed() || QmShanty_ReconArmed()) && InstallProcessEventHook())
        {
            peNetDone = true;
            peNetDoneOnPass = p + 1;
        }
        if (buildMenuFound && lifecycleFound && consumeFound && peNetDone)
        {
            QM_LOG_INFO("[UE] *** ALL HOOKS INSTALLED *** build-menu=pass#%d lifecycle=pass#%d consume=pass#%d pe-net=%s - probe loop exiting",
                buildMenuFoundOnPass, lifecycleFoundOnPass, consumeFoundOnPass,
                peNetDoneOnPass ? "installed" : "skipped(not armed)");
            return 0;
        }

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

    // Loop timed out. Report whichever hooks didn't install.
    if (!buildMenuFound)
    {
        QM_LOG_ERROR("[UE] *** TIMEOUT *** GetBuildingGroupsByCategoryTag never found via Children walk - build-menu inject disabled");
#if QM_DIAG
        int hits = DiagFindUFunctionsByName("GetBuildingGroupsByCategoryTag", 5);
        QM_LOG_DEBUG("[UE] final diag: %d direct-name UFunction hits in GObjects", hits);
#endif
    }
    if (!lifecycleFound)
    {
        QM_LOG_WARN("[PreWarm] *** TIMEOUT *** no lifecycle UFunction found in %d attempts - "
                    "savegame pop-in fix NOT installed; build menu inject %s, but "
                    "placed custom buildings will be invisible after savegame load until first build action",
            kProbeMaxAttempts, buildMenuFound ? "still works" : "ALSO disabled");
    }
    if (!consumeFound)
    {
        QM_LOG_WARN("[Consume] *** TIMEOUT *** R5ConsumeAbility::EventReceived never resolved in %d attempts - "
                    "item-use detection recon NOT installed (class name drift after a game update?)",
            kProbeMaxAttempts);
    }
    return (buildMenuFound || lifecycleFound || consumeFound) ? 0 : 1;
}
