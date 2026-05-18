// Quartermaster injectable-item config - data table.
//
// To add a new item: append a row to g_injectableItems and ensure the asset
// is reachable on disk (mod pak in ~mods/ or vanilla asset).

#include "qm_config.hpp"

const InjectableItem g_injectableItems[] =
{
    // Item 0 - QmBedrl_01 (custom mod asset, shipped via QmBedrl_P.pak).
    // Uses vanilla bedroll bytes under a renamed package - the donor-clone +
    // SoftPath override makes the build slot point here, IoStore hydrates
    // from our mod pak.
    {
        /*.name=*/                   "QmBedrl_01",
        /*.className=*/              "R5BuildingItem",
        /*.assetName=*/              "DA_BI_QmBedrl_01",
        /*.packagePathW=*/           L"/Game/Gameplay/Building/BuildingDecoration/DA_BI_QmBedrl_01",
        /*.assetNameW=*/             L"DA_BI_QmBedrl_01",
        /*.targetCategorySubstring=*/"BuildingDecoration",
    },

    // Item 1 - DA_BI_WallTorch (vanilla asset, no mod pak required).
    // Test duplicate to verify multi-item injection works. The build slot
    // will show the real wall-torch icon and be buildable as a wall torch.
    // Exists in the BuildingDecoration tab in vanilla so the slot ends up
    // as a visible duplicate next to the original wall torch - intended.
    {
        /*.name=*/                   "WallTorch (vanilla test duplicate)",
        /*.className=*/              "R5BuildingItem",
        /*.assetName=*/              "DA_BI_WallTorch",
        /*.packagePathW=*/           L"/Game/Gameplay/Building/BuildingDecoration/DA_BI_WallTorch",
        /*.assetNameW=*/             L"DA_BI_WallTorch",
        /*.targetCategorySubstring=*/"BuildingDecoration",
    },
};

const int g_injectableItemCount = sizeof(g_injectableItems) / sizeof(g_injectableItems[0]);

// Currently all items share the BuildingDecoration tab. When we later have
// items targeting different tabs, this gate moves into the loop and the
// filter substring becomes per-item.
const char* const kTabPurityFilterSubstring = "BuildingDecoration";
