using System;
using System.IO;
using System.Linq;

namespace Windrose.Quartermaster.Core
{
    public sealed class VanillaSourceManifestEntry
    {
        // Stable string id; the frontend keys off this for per-row styling.
        public string Key;

        public string Label;

        public string Description;

        // repak -i include-prefix. For single-file entries this is the full pak-relative path (the file is its own prefix).
        public string PakIncludePath;

        public Func<WindrosePaths, string> DiskPath;

        public VanillaSourceProbeKind ProbeKind;
    }

    public enum VanillaSourceProbeKind
    {
        DirectoryWithJsonFiles,

        SingleFile,
    }

    public static class VanillaSourceManifest
    {
        public static readonly VanillaSourceManifestEntry[] Entries = new[]
        {
            new VanillaSourceManifestEntry
            {
                Key = "inventoryItems",
                Label = "Item definitions",
                Description = "R5BusinessRules/Content/InventoryItems - read by every patcher (Item Creator clones, Buyer lookups, Loot Table refs).",
                PakIncludePath = WindroseGameSecrets.InventoryItemsPath,
                DiskPath = p => p.VanillaInventoryItems,
                ProbeKind = VanillaSourceProbeKind.DirectoryWithJsonFiles,
            },
            new VanillaSourceManifestEntry
            {
                Key = "lootTables",
                Label = "Loot tables",
                Description = "R5BusinessRules/Content/LootTables - drop pools for mobs / containers / foliage.",
                PakIncludePath = WindroseGameSecrets.LootTablesPath,
                DiskPath = p => p.VanillaLootTables,
                ProbeKind = VanillaSourceProbeKind.DirectoryWithJsonFiles,
            },
            new VanillaSourceManifestEntry
            {
                Key = "buildingLimits",
                Label = "Building limits",
                Description = "R5/Content/Gameplay/BuildingLimits - DataAssets for fast-travel bell / signal-fire caps.",
                PakIncludePath = WindroseGameSecrets.BuildingLimitsPath,
                DiskPath = p => p.VanillaBuildingLimits,
                ProbeKind = VanillaSourceProbeKind.DirectoryWithJsonFiles,
            },
            new VanillaSourceManifestEntry
            {
                Key = "playerInventory",
                Label = "Player inventory blueprint",
                Description = "R5BusinessRules/Content/Inventory - DA_PlayerInventoryParams (ring / necklace equipment slot counts). Needed by the Equipment Slots slider.",
                PakIncludePath = WindroseGameSecrets.PlayerInventoryPath,
                DiskPath = p => p.VanillaPlayerInventory,
                ProbeKind = VanillaSourceProbeKind.DirectoryWithJsonFiles,
            },
            new VanillaSourceManifestEntry
            {
                Key = "recipeLists",
                Label = "Recipe lists (NPC trade rosters)",
                Description = "R5BusinessRules/Content/RecipeLists - per-NPC PlayerBuys/PlayerSells rosters. Needed by the Buyers tab.",
                PakIncludePath = WindroseGameSecrets.RecipeListsPath,
                DiskPath = p => p.VanillaRecipeLists,
                ProbeKind = VanillaSourceProbeKind.DirectoryWithJsonFiles,
            },
            new VanillaSourceManifestEntry
            {
                Key = "recipes",
                Label = "Recipe entries",
                Description = "R5BusinessRules/Content/Recipes - the individual Cost+Result records that RecipeLists reference.",
                PakIncludePath = WindroseGameSecrets.RecipesPath,
                DiskPath = p => p.VanillaRecipes,
                ProbeKind = VanillaSourceProbeKind.DirectoryWithJsonFiles,
            },
            new VanillaSourceManifestEntry
            {
                Key = "inventoryItemsCsv",
                Label = "Item localization (CSV string-table)",
                Description = "R5/Content/Localization/Data/InventoryItems.csv - vanilla string-table reference. Kept available for diagnostics and template inspection (custom items now emit plain-string FText inline, no CSV synthesis).",
                PakIncludePath = WindroseGameSecrets.InventoryItemsCsvPath,
                DiskPath = p => p.VanillaInventoryItemsCsv,
                ProbeKind = VanillaSourceProbeKind.SingleFile,
            },
            new VanillaSourceManifestEntry
            {
                Key = "buildingItemsCsv",
                Label = "Building localization (CSV string-table)",
                Description = "R5/Content/Localization/Data/BuildingItems.csv - vanilla string-table reference. Kept available for diagnostics (custom buildings now emit plain-string FText inline via the binary FText.Base rewrite, no CSV synthesis).",
                PakIncludePath = WindroseGameSecrets.BuildingItemsCsvPath,
                DiskPath = p => p.VanillaBuildingItemsCsv,
                ProbeKind = VanillaSourceProbeKind.SingleFile,
            },
            new VanillaSourceManifestEntry
            {
                Key = "crops",
                Label = "Crop growth definitions",
                Description = "R5BusinessRules/Content/Farming/Crops - per-crop DA_Crop_*.json DataAssets with GrowthDuration. Needed by the Stations tab's crop-growth slider.",
                PakIncludePath = WindroseGameSecrets.FarmingCropsPath,
                DiskPath = p => p.VanillaCrops,
                ProbeKind = VanillaSourceProbeKind.DirectoryWithJsonFiles,
            },
            new VanillaSourceManifestEntry
            {
                Key = "aiSpawners",
                Label = "AI spawner configs",
                Description = "R5/Content/Gameplay/Actor/SpawnPoints/A2_Spawners - R5GameplaySpawnerParams / VariantPreset .json with RespawnInterval + Amount. Needed by the NPC Spawns tab.",
                PakIncludePath = WindroseGameSecrets.AiSpawnersPath,
                DiskPath = p => p.VanillaAiSpawners,
                ProbeKind = VanillaSourceProbeKind.DirectoryWithJsonFiles,
            },
        };

        public static bool Probe(VanillaSourceManifestEntry entry, WindrosePaths paths)
        {
            if (entry == null) throw new ArgumentNullException("entry");
            if (paths == null) throw new ArgumentNullException("paths");
            var diskPath = entry.DiskPath(paths);
            switch (entry.ProbeKind)
            {
                case VanillaSourceProbeKind.DirectoryWithJsonFiles:
                    return Directory.Exists(diskPath) &&
                           Directory.EnumerateFiles(diskPath, "*.json", SearchOption.AllDirectories).Any();
                case VanillaSourceProbeKind.SingleFile:
                    return File.Exists(diskPath);
                default:
                    throw new InvalidOperationException(
                        "unknown probe kind: " + entry.ProbeKind);
            }
        }
    }
}
