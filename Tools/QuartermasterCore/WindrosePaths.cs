using System;
using System.IO;

namespace Windrose.Quartermaster.Core
{
    // Resolves the standard Windrose mod-folder layout from a known root.
    // The root is wherever the .ps1 build scripts (and now the GUI) live,
    // i.e. the directory that contains Sources/, Builds/, Profiles/ etc.
    public sealed class WindrosePaths
    {
        public string ModRoot;
        public string Sources;
        public string Vanilla;
        public string VanillaInventoryItems;
        public string VanillaLootTables;
        public string VanillaBuildingLimits;
        // RecipeLists/ holds R5BLRecipeList JSONs - each lists which Recipe
        // entries belong to one trader (PlayerBuys / PlayerSells) or crafting
        // station (Furnace, Kiln, ...). Recipes/ holds R5BLRecipeData JSONs -
        // the actual Cost+Result+CraftRequirement entries the lists reference.
        // Both are needed for the Buyers tab to resolve every recipe ref.
        public string VanillaRecipeLists;
        public string VanillaRecipes;
        // The vanilla R5/Content/Localization/Data/InventoryItems.csv
        // string-table. The Item Creator patcher reads this as its
        // baseline, appends new <Id>_ItemName / <Id>_ItemDescription
        // rows for every CustomItem, and ships the extended copy in the
        // mod pak so the engine resolves the FText lookups the new
        // InventoryItem JSONs contain.
        public string VanillaInventoryItemsCsv;
        // Farming/Crops/ holds R5BLCropParams JSONs - one per crop type
        // (Aloe, Banana, BlackBean, ...). Read by CropGrowthPatcher to
        // multiply each crop's GrowthDuration (FTimespan ticks) by the
        // user-chosen factor for the "Faster crop growth" feature.
        public string VanillaCrops;
        public string Builds;
        public string Profiles;
        public string BuildTmp;
        public string Tools;
        // Folder that historically held reference mods adopted 1:1 (the
        // BetterStructureSupport_P triplet). The build pipeline no longer
        // reads from here - the building-stability feature now self-bakes
        // every vanilla DA_BI* DataAsset via byte-level patching. The
        // path is kept on the WindrosePaths struct so older bundled mods
        // (or future reference-adoption features) have a known landing
        // spot, but no current code path depends on its contents.
        public string References;

        // Returns the per-profile Icons folder where the GUI stores
        // user-uploaded PNGs (Profiles/<profileId>/Icons/). Used by the
        // upload endpoint to land bytes and by IconBakerPatcher to read
        // them at build time. The folder is created lazily by the
        // endpoint - this method is purely a path computation.
        public string ProfileIconsDir(string profileId)
        {
            if (string.IsNullOrEmpty(profileId)) throw new ArgumentNullException("profileId");
            return Path.Combine(Profiles, profileId, "Icons");
        }

        public static WindrosePaths FromModRoot(string modRoot)
        {
            if (string.IsNullOrEmpty(modRoot)) throw new ArgumentNullException("modRoot");
            modRoot = Path.GetFullPath(modRoot);
            var vanilla = Path.Combine(modRoot, "Sources", "Vanilla");
            // Match the in-pak directory layout exactly so output trees can be
            // re-packed without path massaging.
            var vanillaInv = Path.Combine(vanilla, "R5", "Plugins",
                "R5BusinessRules", "Content", "InventoryItems");
            var vanillaLoot = Path.Combine(vanilla, "R5", "Plugins",
                "R5BusinessRules", "Content", "LootTables");
            // BuildingLimits lives in the base R5 content tree (NOT under
            // the R5BusinessRules plugin), and contains tiny config JSONs
            // like DA_BuildLimits_FastTravel.json (~10 entries total).
            var vanillaBuildLimits = Path.Combine(vanilla, "R5", "Content",
                "Gameplay", "BuildingLimits");
            // RecipeLists + Recipes both live under R5BusinessRules/Content/
            // (same plugin tree as InventoryItems / LootTables).
            var vanillaRecipeLists = Path.Combine(vanilla, "R5", "Plugins",
                "R5BusinessRules", "Content", "RecipeLists");
            var vanillaRecipes = Path.Combine(vanilla, "R5", "Plugins",
                "R5BusinessRules", "Content", "Recipes");
            // InventoryItems string-table CSV (base R5 content tree, not
            // under the R5BusinessRules plugin).
            var vanillaInvItemsCsv = Path.Combine(vanilla, "R5", "Content",
                "Localization", "Data", "InventoryItems.csv");
            // Farming/Crops/ lives under the R5BusinessRules plugin tree,
            // same level as InventoryItems / LootTables / Recipes.
            var vanillaCrops = Path.Combine(vanilla, "R5", "Plugins",
                "R5BusinessRules", "Content", "Farming", "Crops");
            return new WindrosePaths
            {
                ModRoot = modRoot,
                Sources = Path.Combine(modRoot, "Sources"),
                Vanilla = vanilla,
                VanillaInventoryItems = vanillaInv,
                VanillaLootTables = vanillaLoot,
                VanillaBuildingLimits = vanillaBuildLimits,
                VanillaRecipeLists = vanillaRecipeLists,
                VanillaRecipes = vanillaRecipes,
                VanillaInventoryItemsCsv = vanillaInvItemsCsv,
                VanillaCrops = vanillaCrops,
                Builds = Path.Combine(modRoot, "Builds"),
                Profiles = Path.Combine(modRoot, "Profiles"),
                BuildTmp = Path.Combine(modRoot, ".build-tmp"),
                Tools = Path.Combine(modRoot, "Tools"),
                References = Path.Combine(modRoot, "References"),
            };
        }
    }
}
