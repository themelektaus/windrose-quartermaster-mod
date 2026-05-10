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
    }
}
