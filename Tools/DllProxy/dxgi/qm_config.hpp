// Quartermaster injectable-item config
// -------------------------------------
// Declarative list of items we want to add to the build-mode UI. Each entry
// is resolved to an FName-pair at runtime via FNameFromString and written
// onto a spawned widget's SoftPath (Plan B+ override path). The asset itself
// must exist on disk - either via a mod pak in ~mods/ or as a vanilla asset.
//
// To add a new item: append a row to g_injectableItems in qm_config.cpp.
// To remove: comment out or delete the row. No other code changes needed.

#pragma once

#include "qm_ue.hpp"

// One declarative row. Wide strings are used for FNameFromString (Conv_StringToName
// takes FString = wide), narrow strings are kept for log readability.
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

extern const InjectableItem  g_injectableItems[];
extern const int             g_injectableItemCount;

// Tab-purity gate: if non-null, a hit's group result must consist entirely of
// groups whose first item's package path contains this substring. Skips mixed
// tabs (e.g. "Aufbewahrung+Betten" which mixes Decoration with Utilities).
// Currently assumes all items share one tab; later we can per-item.
extern const char* const kTabPurityFilterSubstring;
