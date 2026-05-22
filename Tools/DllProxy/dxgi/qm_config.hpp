// Quartermaster injectable-item config
// -------------------------------------
// Declarative list of items we want to add to the build-mode UI. Each entry
// is resolved to an FName-pair at runtime via FNameFromString and written
// onto a spawned widget's SoftPath (Plan B+ override path). The asset itself
// must exist on disk - either via a mod pak in ~mods/ or as a vanilla asset.
//
// Source of truth: `qm_items_<profile>.json` files sitting next to dxgi.dll
// in the game's `R5/Binaries/Win64/` folder, one file per deployed profile.
// The DLL scans the directory for `qm_items_*.json` at startup and merges
// every match into one item list - so multiple Quartermaster profiles
// deployed side-by-side compose naturally. The Quartermaster GUI ("Build"
// button) writes the per-profile file; deleting a pak in the mods tab
// deletes the matching JSON too. No DLL rebuild is required to change the
// item list. If no JSON files are present (or all are malformed) the DLL
// stays loaded but injects nothing.

#pragma once

#include "qm_ue.hpp"

// One declarative row. Wide strings are used for FNameFromString (Conv_StringToName
// takes FString = wide), narrow strings are kept for log readability. Pointers
// are owned by qm_config.cpp's internal string storage; do not free.
struct InjectableItem
{
    const char*    name;                      // log-display name
    const char*    className;                 // expected donor class, always "R5BuildingItem"
    const char*    assetName;                 // narrow: "DA_BI_QmBedrl_01"
    const wchar_t* packagePathW;              // wide: L"/Game/Gameplay/Building/BuildingDecoration/DA_BI_QmBedrl_01"
    const wchar_t* assetNameW;                // wide: L"DA_BI_QmBedrl_01"
    const char*    targetCategorySubstring;   // group-filter substring on first item's package path,
                                              // nullptr = match every group (legacy fan-out)
};

// Runtime-loaded view. After QmConfigLoad() these point at heap-backed storage
// owned by qm_config.cpp; before/after Load they may be (nullptr, 0, nullptr).
// Item accesses like g_injectableItems[i].name work identically to the previous
// const-array form because pointer indexing is array indexing.
extern const InjectableItem* g_injectableItems;
extern int                   g_injectableItemCount;

// Tab-purity gate: if non-null, a hit's group result must consist entirely of
// groups whose first item's package path contains this substring. Skips mixed
// tabs (e.g. "Aufbewahrung+Betten" which mixes Decoration with Utilities).
// Currently assumes all items share one tab; later we can per-item.
extern const char* kTabPurityFilterSubstring;

// Scan the directory containing this DLL for qm_items_*.json files, parse
// each, and merge their items into one list. Safe to call multiple times
// (later calls clear and re-merge). Returns true on success (including the
// "no files matched -> empty list" no-op case); false only when one or
// more files hit a hard parse error. Errors are logged via QM_LOG_*.
bool QmConfigLoad();

// Optional explicit shutdown - releases the internal vectors. Not required
// for correctness; DLL unload reclaims everything anyway.
void QmConfigUnload();
