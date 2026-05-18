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

// ----- Module-level config (overridden later via runtime config) ------------
// Asset that replaces the donor's SoftPath on every spawned widget.
//
// kTargetGroupPathSubstring filters which Groups receive an inject. A group is
// a target iff its Items[0].PackageName contains this substring. Set to
// nullptr to disable filtering (legacy fan-out into every group).
extern const wchar_t* const kOverridePackagePathW;
extern const wchar_t* const kOverrideAssetNameW;
extern const char*    const kOverrideAssetName;     // log only
extern const char*    const kOverrideClassName;     // log only
extern const char*    const kTargetGroupPathSubstring;
extern const int            kSpawnedPoolMax;

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
    const char* status;    // "captured", "injected", "already-present",
                           // "skipped-same-group", "skipped-no-slack",
                           // "skipped-empty", "skipped-no-target",
                           // "skipped-category", "skipped-tab-impure",
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

// ----- Override resolution (FName-from-String, cached) ----------------------
// First call resolves and caches the FNames for kOverridePackagePathW +
// kOverrideAssetNameW. Subsequent calls are no-ops returning the cached state.
bool QmIsOverrideResolved();
bool QmGetOverrideTarget(QmUE::FName* pkgOut, QmUE::FName* assetOut);

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
    long  overrideApplied;
    long  overrideLookupAttempts;
    bool  overrideResolved;
    long  skippedCategory;
    const char* donorAssetName;
};

QmInjectSnapshot QmGetInjectSnapshot();

// Bump hook-hit counter. Returns the post-increment value. The counter lives
// in qm_inject so the crash snapshot can read it without coupling to qm_hook.
long QmBumpHookHits();
