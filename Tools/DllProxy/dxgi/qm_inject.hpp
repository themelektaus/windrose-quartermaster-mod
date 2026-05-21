// Quartermaster inject pipeline
// -----------------------------
// Capture donor item once, then for each successful inject spawn a *fresh*
// UR5BuildingItemWidget via UGameplayStatics::SpawnObject, memcpy the
// donor ItemData, rewrite PackageName/AssetName via
// KismetStringLibrary::Conv_StringToName, zero the WeakPtr (IoStore re-hydrates
// to our path on next render), and append into the target Group's items array.
//
// Per-inject (not shared) widgets: UE expects 1 widget = 1 owning group. The
// previous design reused one widget across many groups, which crashed once
// groups recycled (game thinks freed widget is still live).

#pragma once

#include <stdint.h>
#include "qm_ue.hpp"

// ----- Module-level config --------------------------------------------------
// Item table is in qm_config.{hpp,cpp}. The spawn pool cap stays here because
// it is intimately tied to the inject pipeline.
extern const int kSpawnedPoolMax;

// ----- Per-call inject reporting --------------------------------------------
// Filled by InjectIntoGroup / CaptureOrInjectForeignItem and consumed by the
// hook for log formatting.
struct ForeignInjectReport
{
    void* targetGroup;
    void* donorItem;
    int   oldNum;
    int   newNum;
    int   max;
    int   itemIdx;         // which InjectableItem (-1 if N/A: capture/empty)
    const char* status;    // "captured", "injected", "item-swapped",
                           // "already-present", "skipped-same-group",
                           // "skipped-no-slack", "skipped-empty",
                           // "skipped-no-target", "skipped-category",
                           // "skipped-tab-impure", "skipped-bad-item",
                           // nullptr if FAULT
};

struct ForeignFanoutReport
{
    int total;
    int injected;
    int skipped;
    int faulted;
};

// ----- Group category probe -------------------------------------------------
// Read group's first item, resolve its package name and (if hydrated) the
// underlying UR5BuildingItem::BuildingItemTag. All reads SEH-guarded; on fault
// the probe leaves fields in their default-empty state.
struct GroupCategoryProbe
{
    void* firstItem;       // Items[0] widget pointer (or null on empty)
    char  pkgName[256];    // resolved package path string ("" on fault)
    char  tagName[128];    // hydrated BuildingItemTag string ("" if unhydrated/fault)
    bool  hasItems;        // true if BuildingItems TArray had >= 1 entry
    bool  pkgValid;        // true if pkgName resolved to a non-empty string
};

void ProbeGroupCategory(void* group, GroupCategoryProbe* out);
bool GroupMatchesTargetCategory(const GroupCategoryProbe& probe);

// Tab-purity classification (Plan B+):
//   1 = pure target tab (every group matches),
//   0 = mixed/other tab (at least one non-target group),
//  -1 = indeterminate (no groups / fault).
int ClassifyTabPurity(void* Result);

// ----- Override resolution (FName-from-String, cached per item) ------------
// One override target per InjectableItem. Lazy-resolved on first use, cached
// thereafter. itemIdx is into g_injectableItems[].
bool QmIsOverrideResolved(int itemIdx);
bool QmGetOverrideTarget(int itemIdx, QmUE::FName* pkgOut, QmUE::FName* assetOut);
int  QmCountOverridesResolved();   // for state-log line

// ----- Hook param reader (used unconditionally by the hook) ----------------
// Resolves CategoryTag from the GetBuildingGroupsByCategoryTag param block.
// Tries ReferenceParm first (pointer deref) then value-style as fallback.
// Returns true if a non-None tag was resolved; viaReferenceOut signals path.
bool ReadCategoryTagFromHookParams(void* Result, QmUE::FGameplayTag* tagOut, bool* viaReferenceOut);

// ----- Per-hit pipeline entry point -----------------------------------------
// Returns 0 on success (captured OR at least one inject), -1 on skip/empty,
// -2 on SEH fault. The fanout report holds aggregate per-group totals.
int CaptureOrInjectForeignItem(void* Result, ForeignInjectReport* out,
                               ForeignFanoutReport* fanout);

// ----- Snapshot for crash diagnostics + state-log line ---------------------
struct QmInjectSnapshot
{
    long  hookHits;
    long  injectsDone;
    long  alreadyPresent;
    void* donorItem;
    void* donorSourceGroup;
    int   spawnedPoolCount;
    long  spawnAttempts;
    long  spawnSuccesses;
    long  spawnReuses;
    long  overrideApplied;
    long  overrideLookupAttempts;
    int   overridesResolvedCount;     // how many items have their FName-pair cached
    long  skippedCategory;
    const char* donorAssetName;
};

QmInjectSnapshot QmGetInjectSnapshot();

// Bump hook-hit counter. Returns the post-increment value. The counter lives
// in qm_inject so the crash snapshot can read it without coupling to qm_hook.
long QmBumpHookHits();

// ----- Savegame pre-warm (DLL init time, gated by canary poll) -------------
// Sync-load every Building-DA package from qm_items.json via
// UKismetSystemLibrary::LoadAsset_Blocking so the IoStore PackageStore has
// resolved entries before any savegame can attempt to deserialize a placed
// custom building. Without this, the actor deserializer can't resolve the
// saved SoftObjectPath (the AssetManager-filter rejected our mod pak at
// boot - same root cause as the Phase B5 BuildingMenu inject) and the
// placed building renders empty until the player opens the Build menu AND
// builds a new item, which finally triggers the lookup.
//
// IMPORTANT: LoadAsset_Blocking returns nullptr if called too early - even
// for known-good vanilla DAs - because the async-loading subsystem /
// PackageStore isn't fully initialized until after UR5GameInstance::Init
// runs (which happens ~250ms AFTER our ProbeThread reaches the post-Alloc
// stage). We can't synchronously wait for GameInstance::Init from the
// ProbeThread, so instead we POLL: at fixed intervals, try LoadAsset on a
// known vanilla DA. As soon as that returns non-null, the subsystem is
// ready and we run the full pre-warm immediately.
//
// QmInject_TryCanaryLoad probes a known-good vanilla DA. Returns true if
// the load succeeded (asset-subsystem ready), false otherwise. Safe to call
// repeatedly - LoadAsset_Blocking is idempotent.
//
// QmInject_PreWarmBuildingPackages does the actual per-item LoadAsset
// sweep. Internal static flag prevents double-execution if called from
// multiple paths (ProbeThread, build-menu hook, etc).
//
// Logs a per-item OK/FAIL line plus an aggregate summary. Failures do not
// abort the DLL - the BuildingMenu inject still runs and the next build
// action will eventually resolve the path.
bool QmInject_TryCanaryLoad();
void QmInject_PreWarmBuildingPackages();

// Returns true once QmInject_PreWarmBuildingPackages has run with all mod DAs
// (and the canary) returning non-null UObjects. The flag is process-permanent
// - the IoStore PackageStore caches the resolution for the rest of the
// process lifetime, so re-running pre-warm on subsequent map transitions
// is pure noise. qm_hook.cpp's Hook_LifecyclePreWarm checks this before
// touching the latch to avoid burning CPU on every gameplay BeginPlay.
bool QmInject_PreWarmIsComplete();
