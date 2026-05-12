namespace Windrose.Quartermaster.Core
{
    // Shared constants used by the dump + icon pipelines.
    // The AES key is the public game key (NOT a secret - it's used by
    // every Windrose modding tool, e.g. WindrosePlus' IniConfigParser).
    public static class WindroseGameSecrets
    {
        public const string AesKey =
            "0x5F430BF9FEF2B0B91B7C79C313BDAF291BA076A1DAB5045974186333AA16CFAE";

        // Pak-internal prefix the dumper extracts (item definitions).
        public const string InventoryItemsPath =
            "R5/Plugins/R5BusinessRules/Content/InventoryItems";

        // Pak-internal prefix the dumper extracts (loot tables for mobs,
        // chests, foliage, ships, etc - referenced by InventoryItems but
        // shipped as a sibling tree).
        public const string LootTablesPath =
            "R5/Plugins/R5BusinessRules/Content/LootTables";

        // Pak-internal prefix the dumper extracts (DataAssets controlling
        // build-amount limits; currently only DA_BuildLimits_FastTravel
        // matters for us - governs the cap on placeable bells / signal
        // fires). Lives in the *base game* content tree, NOT under the
        // R5BusinessRules plugin.
        public const string BuildingLimitsPath =
            "R5/Content/Gameplay/BuildingLimits";

        // Specific JSON file (relative to a vanilla extract root) for the
        // fast-travel bell limits asset. Three R5BuildingAmountLimit entries:
        //   [0] Bell variant 1 (DA_BI_Utilities_FastTravel_Bell)   default 10
        //   [1] Bell variant 2 (DA_BI_Utilities_FastTravelBell_02) default 10
        //   [2] Signal fire     (DA_BI_SignalFireT01)              default  3
        public const string FastTravelLimitsRelPath =
            "R5/Content/Gameplay/BuildingLimits/DA_BuildLimits_FastTravel.json";

        // Pak-internal prefix the dumper extracts (R5BLRecipeList JSONs:
        // the per-NPC trade rosters - what each trader buys from / sells to
        // the player - plus crafting-station rosters like Furnace_T01).
        // Needed by the Buyers tab (PlayerSells variants).
        public const string RecipeListsPath =
            "R5/Plugins/R5BusinessRules/Content/RecipeLists";

        // Pak-internal prefix the dumper extracts (R5BLRecipeData JSONs:
        // the individual trade / craft entries with Cost + Result + optional
        // CraftRequirement). Every entry in a RecipeList is a reference into
        // this tree, so we need both folders to render a Buyer.
        public const string RecipesPath =
            "R5/Plugins/R5BusinessRules/Content/Recipes";

        // Pak-internal file path the dumper extracts (the InventoryItems
        // string-table). Custom items synthesized via the Item Creator tab
        // reference this table for their FText ItemName / ItemDescription
        // keys; the patcher emits an extended copy of this CSV alongside
        // the new InventoryItem JSONs so the engine resolves the lookups
        // at runtime. Lives in the base R5 content tree (NOT under the
        // R5BusinessRules plugin), same as BuildingLimits.
        public const string InventoryItemsCsvPath =
            "R5/Content/Localization/Data/InventoryItems.csv";
    }
}
