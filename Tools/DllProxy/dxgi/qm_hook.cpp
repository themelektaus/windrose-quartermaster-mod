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

static void __fastcall Hook_LifecyclePreWarm(void* Context, void* Stack, void* Result)
{
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
    int  buildMenuFoundOnPass = 0;
    int  lifecycleFoundOnPass = 0;

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

        if (buildMenuFound && lifecycleFound)
        {
            QM_LOG_INFO("[UE] *** ALL HOOKS INSTALLED *** build-menu=pass#%d lifecycle=pass#%d - probe loop exiting",
                buildMenuFoundOnPass, lifecycleFoundOnPass);
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
    return (buildMenuFound || lifecycleFound) ? 0 : 1;
}
