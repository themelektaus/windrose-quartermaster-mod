using System;
using System.IO;

namespace Windrose.Quartermaster.Core
{
    public sealed class WindrosePaths
    {
        public const string ModItemsPackagePath = "/Game/Quartermaster/";

        public string ModRoot;
        public string Sources;
        public string Vanilla;
        public string VanillaInventoryItems;
        public string VanillaLootTables;
        public string VanillaBuildingLimits;
        public string VanillaRecipeLists;
        public string VanillaRecipes;
        public string VanillaInventoryItemsCsv;
        public string VanillaBuildingItemsCsv;
        public string VanillaCrops;
        public string Builds;
        public string Profiles;
        public string BuildTmp;
        public string Tools;
        public string References;

        public string ProfileIconsDir(string profileId)
        {
            if (string.IsNullOrEmpty(profileId)) throw new ArgumentNullException("profileId");
            return Path.Combine(Profiles, profileId, "Icons");
        }

        public string ProfileShipMusicSlotDir(string profileId, string slotStem)
        {
            if (string.IsNullOrEmpty(profileId)) throw new ArgumentNullException("profileId");
            if (string.IsNullOrEmpty(slotStem)) throw new ArgumentNullException("slotStem");
            return Path.Combine(Profiles, profileId, "ShipMusic", slotStem);
        }

        public string ProfileShipMusicAddTrackDir(string profileId, string trackKey)
        {
            if (string.IsNullOrEmpty(profileId)) throw new ArgumentNullException("profileId");
            if (string.IsNullOrEmpty(trackKey))  throw new ArgumentNullException("trackKey");
            return Path.Combine(Profiles, profileId, "ShipMusicAdd", trackKey);
        }

        public string ProfileBonfireMusicDir(string profileId)
        {
            if (string.IsNullOrEmpty(profileId)) throw new ArgumentNullException("profileId");
            return Path.Combine(Profiles, profileId, "BonfireMusic");
        }

        public string ProfileBuildingAudioDir(string profileId, string buildingId)
        {
            if (string.IsNullOrEmpty(profileId)) throw new ArgumentNullException("profileId");
            if (string.IsNullOrEmpty(buildingId)) throw new ArgumentNullException("buildingId");
            return Path.Combine(Profiles, profileId, "BuildingAudio", buildingId);
        }

        public string ResolveProfileRelativeFolder(string profileId, string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return raw;
            if (!string.IsNullOrEmpty(profileId))
            {
                try
                {
                    var candidate = Path.Combine(Profiles, profileId, raw);
                    if (Directory.Exists(candidate))
                        return Path.GetFullPath(candidate);
                }
                catch
                {
                    // Malformed input throws from Path.*; fall through to raw return.
                }
            }
            return raw;
        }

        public string BinkAudioEncoderPath
        {
            get { return Path.Combine(Tools, "binkaudioenc.exe"); }
        }

        public string FfmpegPath
        {
            get { return Path.Combine(ModRoot, "ffmpeg.exe"); }
        }

        public string ShipMusicTemplateUasset
        {
            get { return Path.Combine(Tools, "Templates", "SoundWave_BinkInline.uasset"); }
        }
        public string ShipMusicTemplateUexp
        {
            get { return Path.Combine(Tools, "Templates", "SoundWave_BinkInline.uexp"); }
        }

        public string BuildingDefaultTexturesDir
        {
            get { return Path.Combine(Tools, "Templates", "DefaultTextures"); }
        }

        public string NativeDllDir
        {
            get { return ModRoot; }
        }

        static string s_globalNativeDllDir;

        public static void ConfigureNativeDllDir(string dir)
        {
            if (string.IsNullOrEmpty(dir)) return;
            s_globalNativeDllDir = Path.GetFullPath(dir);
        }

        public static string ResolveNativeDllDir()
        {
            return !string.IsNullOrEmpty(s_globalNativeDllDir)
                ? s_globalNativeDllDir
                : AppContext.BaseDirectory;
        }

        public static bool IsDevRepoRoot(string root)
        {
            return !string.IsNullOrEmpty(root)
                && File.Exists(Path.Combine(root, "Tools", "QuartermasterCore",
                                                  "QuartermasterCore.csproj"));
        }

        public static WindrosePaths FromModRoot(string modRoot)
        {
            if (string.IsNullOrEmpty(modRoot)) throw new ArgumentNullException("modRoot");
            modRoot = Path.GetFullPath(modRoot);
            ConfigureNativeDllDir(modRoot);
            var vanilla = Path.Combine(modRoot, "Sources", "Vanilla");
            var vanillaInv = Path.Combine(vanilla, "R5", "Plugins",
                "R5BusinessRules", "Content", "InventoryItems");
            var vanillaLoot = Path.Combine(vanilla, "R5", "Plugins",
                "R5BusinessRules", "Content", "LootTables");
            var vanillaBuildLimits = Path.Combine(vanilla, "R5", "Content",
                "Gameplay", "BuildingLimits");
            var vanillaRecipeLists = Path.Combine(vanilla, "R5", "Plugins",
                "R5BusinessRules", "Content", "RecipeLists");
            var vanillaRecipes = Path.Combine(vanilla, "R5", "Plugins",
                "R5BusinessRules", "Content", "Recipes");
            var vanillaInvItemsCsv = Path.Combine(vanilla, "R5", "Content",
                "Localization", "Data", "InventoryItems.csv");
            var vanillaBldgItemsCsv = Path.Combine(vanilla, "R5", "Content",
                "Localization", "Data", "BuildingItems.csv");
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
                VanillaBuildingItemsCsv = vanillaBldgItemsCsv,
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
