namespace Windrose.Quartermaster.Core
{
    public static class WindroseGameSecrets
    {
        // Public game key shared by every Windrose modding tool; not a secret.
        public const string AesKey =
            "0x5F430BF9FEF2B0B91B7C79C313BDAF291BA076A1DAB5045974186333AA16CFAE";

        public const string InventoryItemsPath =
            "R5/Plugins/R5BusinessRules/Content/InventoryItems";

        public const string LootTablesPath =
            "R5/Plugins/R5BusinessRules/Content/LootTables";

        public const string BuildingLimitsPath =
            "R5/Content/Gameplay/BuildingLimits";

        public const string FastTravelLimitsRelPath =
            "R5/Content/Gameplay/BuildingLimits/DA_BuildLimits_FastTravel.json";

        public const string RecipeListsPath =
            "R5/Plugins/R5BusinessRules/Content/RecipeLists";

        public const string RecipesPath =
            "R5/Plugins/R5BusinessRules/Content/Recipes";

        public const string InventoryItemsCsvPath =
            "R5/Content/Localization/Data/InventoryItems.csv";

        public const string BuildingItemsCsvPath =
            "R5/Content/Localization/Data/BuildingItems.csv";

        public const string FarmingCropsPath =
            "R5/Plugins/R5BusinessRules/Content/Farming/Crops";

        // R5JsonRuntimeDA spawner configs (R5GameplaySpawnerParams +
        // R5GameplaySpawnerVariantPreset) read at runtime as raw .json. Shipped
        // in the legacy pakchunk0 .pak (not the .ucas), so repak extracts them.
        public const string AiSpawnersPath =
            "R5/Content/Gameplay/Actor/SpawnPoints/A2_Spawners";

        // R5CannonParams configs read at runtime as raw .json (shipped in the
        // legacy pakchunk0 .pak, so repak extracts them). DA_Cannon_* are the
        // PLAYER cannons; DA_AI_Cannon_* are the enemy/NPC variants. The cannon
        // reload feature patches only DA_Cannon_* so enemy ships stay vanilla.
        public const string CannonParamsPath =
            "R5/Content/Gameplay/Water/Character/Guns/Cannons";

        public const string PlayerInventoryPath =
            "R5/Plugins/R5BusinessRules/Content/Inventory";

        public const string PlayerInventoryParamsRelPath =
            "R5/Plugins/R5BusinessRules/Content/Inventory/DA_PlayerInventoryParams.json";
    }
}
