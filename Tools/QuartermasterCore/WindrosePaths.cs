using System;
using System.IO;

namespace Windrose.Quartermaster.Core
{
    // Resolves the standard Windrose mod-folder layout from a known root.
    // The root is wherever the .ps1 build scripts (and now the GUI) live,
    // i.e. the directory that contains Sources/, Builds/, Profiles/ etc.
    public sealed class WindrosePaths
    {
        // UE virtual package directory under which Quartermaster emits ALL
        // mod-pak assets (cloned DAs, cloned MIs, staged user meshes, default
        // textures, etc.). This is purely an OUTPUT convention controlled by
        // the build pipeline - the user's UE-editor cooked-folder source path
        // can be anywhere on disk, but the patcher always restages and
        // self-path-normalizes everything into this single mod-side prefix so
        // the runtime injector only ever has one place to look. Trailing
        // slash is intentional: callers concatenate stems directly.
        //
        // Centralized here (instead of repeated as a string literal across
        // BuildingPatcher / GameDeployer / FlamePresetCatalog) so a future
        // relocation needs a single edit + a rebuild of the C# tooling. The
        // shipped DefaultTextures + any user-cooked asset are FolderName-
        // normalized to this prefix at staging time (see
        // BuildingPatcher.NormalizeAssetSelfPath), so no UE-editor recook
        // is needed. The dxgi DLL also needs no rebuild - it reads the
        // packagePath as an opaque string from qm_items_<profile>.json,
        // which the deployer regenerates per build.
        public const string ModItemsPackagePath = "/Game/Quartermaster/";

        // Vanilla string-table identifiers (= filename stem of the CSV under
        // R5/Content/Localization/Data/). The Windrose game registers each
        // CSV at boot under a StringTable whose TableId equals the stem -
        // any FText property with HistoryType=StringTableEntry referencing
        // that TableId then resolves its Key against the CSV at runtime.
        public const string VanillaInventoryItemsTableId = "InventoryItems";
        public const string VanillaBuildingItemsTableId  = "BuildingItems";

        // Per-profile StringTable scheme. Each profile gets its own CSV
        // file inside the pak (= its own StringTable registration at boot)
        // so that two profiles with custom items / buildings never collide
        // on the shared "InventoryItems"/"BuildingItems" path. Without this
        // the alphabetically-later pak's CSV overrides the earlier one and
        // the loser's buildings/items resolve to <MISSING_STRING>.
        //
        // Short-id form: first 8 hex chars of the profile GUID after
        // dash-stripping (e.g. profile "6895e2b9-c2d2-..." -> "6895e2b9").
        // Matches the existing QmBldg_<8hex> id scheme so the suffix stays
        // short enough that the cloned building DA's FName NameMap entry
        // for the rewritten TableId fits the byte budget of a UAssetAPI
        // re-serialize round-trip without ballooning the export header.
        public static string ShortProfileId(string profileId)
        {
            if (string.IsNullOrEmpty(profileId)) throw new ArgumentNullException("profileId");
            var stripped = profileId.Replace("-", "");
            return stripped.Length >= 8 ? stripped.Substring(0, 8) : stripped;
        }

        // TableId for the per-profile InventoryItems string-table. Used as
        // the FText.TableId on every custom item's UI JSON AND as the
        // filename stem of the per-profile CSV in the legacy pak.
        public static string InventoryItemsTableIdFor(string profileId)
        {
            return VanillaInventoryItemsTableId + "_" + ShortProfileId(profileId);
        }
        public static string BuildingItemsTableIdFor(string profileId)
        {
            return VanillaBuildingItemsTableId + "_" + ShortProfileId(profileId);
        }

        // Pak-internal path the per-profile CSV lands at. Folder is the same
        // R5/Content/Localization/Data/ tree as the vanilla CSVs - the
        // Windrose loader scans that folder at boot, so dropping a new file
        // there is enough for the registration to fire. Filename stem must
        // equal the TableId.
        public static string InventoryItemsCsvPakPathFor(string profileId)
        {
            return "R5/Content/Localization/Data/" + InventoryItemsTableIdFor(profileId) + ".csv";
        }
        public static string BuildingItemsCsvPakPathFor(string profileId)
        {
            return "R5/Content/Localization/Data/" + BuildingItemsTableIdFor(profileId) + ".csv";
        }

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

        // Resolves a user-supplied folder string (e.g. CustomBuilding.Cooked-
        // FolderPath = "MyPainting") to an absolute filesystem path,
        // preferring a profile-relative location.
        //
        // Lookup order:
        //   1. If `<Profiles>/<profileId>/<raw>` exists as a directory,
        //      return its absolute path. This makes users able to drop
        //      a "MyPainting" cooked folder next to the profile JSON and
        //      reference it with just the folder name in the GUI.
        //   2. Otherwise return `raw` as-is. The caller is responsible
        //      for whatever fallback semantics make sense for them
        //      (most call sites either treat absolute paths verbatim,
        //      or surface a "Folder does not exist" error).
        //
        // The stored CustomBuilding.CookedFolderPath value is NEVER
        // rewritten by this helper - what the user typed stays in the
        // profile JSON. This method only computes a usable absolute
        // path on the fly at consumption time.
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
                    // Path.Combine / GetFullPath can throw on
                    // pathologically malformed input (invalid chars on
                    // Windows etc.). Fall through to the raw return so
                    // the caller's existing error handling kicks in.
                }
            }
            return raw;
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

        // Folder holding the shared VT default textures (canonical
        // stem list: DefaultTextureProvider.Stems) that the Building
        // Creator's MI clones reference when the user doesn't pick a
        // custom texture for a slot's Albedo / Normal / MTRM param. Each
        // texture lives as .uasset + .uexp + .ubulk triplet under
        // this folder; the build pipeline copies them into the
        // staging tree once per build (regardless of which buildings
        // reference them) so the cloned MIs always resolve. The
        // GUI surfaces the stems as an "always available" group
        // in the per-slot texture dropdowns so the user can pick
        // them without having to cook them into their own project.
        public string BuildingDefaultTexturesDir
        {
            get { return Path.Combine(Tools, "Templates", "DefaultTextures"); }
        }

        // Folder where lazily-extracted / lazily-downloaded native sidecar
        // DLLs land (oodle-data-shared.dll from the OodleUE GitHub release,
        // Detex.dll from an embedded resource in CUE4Parse-Conversion).
        // Defaults to ModRoot, which means:
        //   - dev runs: lands in the workspace root (gitignored)
        //   - deployed runs: lands in <exe-dir>/QuartermasterData/ alongside
        //     dxgi.dll / *.usmap / Profiles / Icons - so a portable install
        //     (USB stick) carries the natives with the rest of the state
        //     instead of leaving them stranded next to the EXE.
        public string NativeDllDir
        {
            get { return ModRoot; }
        }

        // Static fallback the EnsureOodle / EnsureDetex sites consult when
        // they have no WindrosePaths in scope (they are static methods or
        // CUE4Parse-style instance classes with only PaksDir / AesKey
        // fields, no full paths struct). FromModRoot sets this to ModRoot
        // automatically so the typical Web / CLI startup primes it; the
        // AppContext.BaseDirectory fallback keeps callers safe even if they
        // skip the configure step (e.g. unit tests, ad-hoc tooling).
        static string s_globalNativeDllDir;

        // Override the global native-DLL dir explicitly. The Web layer also
        // calls this from CreateWebApp belt-and-braces - the FromModRoot
        // auto-set uses the same value, but an explicit call documents the
        // intent at the entry point.
        public static void ConfigureNativeDllDir(string dir)
        {
            if (string.IsNullOrEmpty(dir)) return;
            s_globalNativeDllDir = Path.GetFullPath(dir);
        }

        // Resolves the directory native sidecar DLLs should be cached in.
        // Falls back to AppContext.BaseDirectory (= original behavior) when
        // no caller has primed the path yet, so we never crash on a missing
        // configure call.
        public static string ResolveNativeDllDir()
        {
            return !string.IsNullOrEmpty(s_globalNativeDllDir)
                ? s_globalNativeDllDir
                : AppContext.BaseDirectory;
        }

        public static WindrosePaths FromModRoot(string modRoot)
        {
            if (string.IsNullOrEmpty(modRoot)) throw new ArgumentNullException("modRoot");
            modRoot = Path.GetFullPath(modRoot);
            // Prime the static native-DLL dir so subsequent EnsureOodle /
            // EnsureDetex sites land their downloads in the data root the
            // rest of the app uses. ConfigureNativeDllDir is idempotent and
            // tolerates repeated calls with the same value (every endpoint
            // builds its own WindrosePaths instance, so this fires often).
            ConfigureNativeDllDir(modRoot);
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
