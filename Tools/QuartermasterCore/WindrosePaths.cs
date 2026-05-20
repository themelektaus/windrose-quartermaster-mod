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
        // The vanilla R5/Content/Localization/Data/BuildingItems.csv
        // string-table. Same role as VanillaInventoryItemsCsv but for
        // building DAs: BuildingPatcher rewrites the FText keys in
        // each cloned DA's export body to per-building keys, and the
        // build pipeline appends matching rows to the extended copy of
        // this CSV so in-game display names / tooltips render the
        // user-supplied text instead of the vanilla fallback.
        public string VanillaBuildingItemsCsv;
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

        // Per-profile per-slot folder for user-uploaded ship-music WAVs.
        // The ship-music upload endpoint stores the single file as
        // audio.wav under this dir; the ShipMusicPatcher reads it back
        // at build time and runs binkaudioenc.exe + template splice on
        // it. slotStem is the vanilla SWAV stem (e.g.
        // "SWAV_Shanti_DrunkenSailor") and also serves as a tampering
        // safeguard - the endpoint validates it against
        // ShipMusicSlots.ByStem before touching disk.
        public string ProfileShipMusicSlotDir(string profileId, string slotStem)
        {
            if (string.IsNullOrEmpty(profileId)) throw new ArgumentNullException("profileId");
            if (string.IsNullOrEmpty(slotStem)) throw new ArgumentNullException("slotStem");
            return Path.Combine(Profiles, profileId, "ShipMusic", slotStem);
        }

        // Absolute path to the in-tree Bink Audio encoder CLI. We ship
        // it next to repak.exe / retoc.exe under Tools/ so it travels
        // with the published app. Source under Tools/BinkAudioEnc/.
        public string BinkAudioEncoderPath
        {
            get { return Path.Combine(Tools, "binkaudioenc.exe"); }
        }

        // Absolute path to ffmpeg.exe at the workspace root. Not shipped
        // with the repo (gitignored); FfmpegResolver downloads it on
        // first use from BtbN/FFmpeg-Builds (LGPL variant, ~190 MB ZIP)
        // and extracts only ffmpeg.exe here. Used by the audio
        // preprocessor to transcode arbitrary user-uploaded audio
        // (mp3/ogg/flac/m4a/aac/opus/wav) into the 44.1 kHz stereo
        // 16-bit PCM WAV the Bink encoder accepts.
        public string FfmpegPath
        {
            get { return Path.Combine(ModRoot, "ffmpeg.exe"); }
        }

        // Absolute path to the pre-cooked ForceInline USoundWave
        // template the ship-music patcher splices Bink Audio bytes
        // into. .uasset + .uexp pair; ForceInline cooks have no .ubulk
        // sidecar. Cooked once by hand from a 5-second 44.1 kHz stereo
        // PCM WAV (References/AudioEncoder project).
        public string ShipMusicTemplateUasset
        {
            get { return Path.Combine(Tools, "Templates", "SoundWave_BinkInline.uasset"); }
        }
        public string ShipMusicTemplateUexp
        {
            get { return Path.Combine(Tools, "Templates", "SoundWave_BinkInline.uexp"); }
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
            // BuildingItems string-table CSV (same Localization/Data tree
            // as InventoryItems.csv but for buildings).
            var vanillaBldgItemsCsv = Path.Combine(vanilla, "R5", "Content",
                "Localization", "Data", "BuildingItems.csv");
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
