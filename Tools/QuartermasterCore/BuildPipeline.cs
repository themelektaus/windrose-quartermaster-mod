using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Windrose.Quartermaster.Core
{
    // Orchestrates the full build for a given Profile:
    //   Vanilla/  --(StackPatcher)-->  .build-tmp/<profile-id>/  --(PakBuilder)-->  Builds/<name>_P.pak
    //
    // The temp directory is wiped before patching and (by default) deleted
    // after a successful pack - callers can opt into keepTemp=true for
    // post-mortem debugging.
    //
    // The build can produce up to TWO IoStore triplets next to the main pak:
    //
    //   1. Composite triplet  Quartermaster_<name>_P.{ucas,utoc}  (+ stub .pak
    //      OR full .pak from the main pakbuild). Built via retoc to-legacy +
    //      AfterExtract patches + retoc to-zen by IoStoreCompositeBuilder.
    //      Carries the Pickup and NoSmoke features.
    //
    //   2. Raw companion  Quartermaster_<name>_Raw_P.{pak,ucas,utoc} -
    //      a SECOND mount-point separate from the composite, holding
    //      features that can't go through retoc to-zen. Two paths
    //      contribute here:
    //        - Stability: retoc unpack-raw + byte-level patches on raw
    //          zen chunks + retoc pack-raw -> .ucas + .utoc. Lives here
    //          because to-zen produces game-incompatible output for the
    //          vanilla DA_BI* class (R5CollisionApproximation uses a
    //          custom C++ Serialize() that breaks under unversioned <->
    //          versioned round-tripping).
    //        - Minimap: a vanilla R5/Config/DefaultR5MapSettings.ini
    //          extracted via AES-keyed repak unpack, scaled by a
    //          user-supplied multiplier, re-packed via repak pack -> .pak.
    //          Lives here because the .ini lands in the PakFile subsystem
    //          (not IoStore) and retoc to-zen silently drops non-asset
    //          files. The PakFile + IoStore backends mount independently
    //          under the same basename so both can coexist in one container.
    //
    //      Layout is adaptive:
    //        - Stability + Minimap -> real .pak (minimap INI) + real .ucas/.utoc (stability)
    //        - Stability only      -> 347-byte stub .pak + real .ucas/.utoc
    //        - Minimap only        -> real .pak ONLY (no .ucas/.utoc emitted)
    //
    // Composite sources today:
    //   - Pickup:  vanilla GA_Loot_AutoPickup Blueprint extracted via
    //              retoc to-legacy and patched in-place via UAssetAPI to
    //              add a serialized MagnetRadius FloatProperty.
    //   - NoSmoke: self-bakes vanilla Niagara assets (FX_Bonefire/Campfire/
    //              Furnace/Kiln) and patches every EmitterHandle.bIsEnabled
    //              to false to silence the smoke / flame particle systems.
    //              Three independent toggles map to three asset groups.
    //   - Bonfire: scales the two influence floats on
    //              DA_BI_Utilities_BuildingCenterT01.uasset by a user
    //              multiplier (vanilla 5000/3000 -> 5000*M/3000*M), so the
    //              "you can build here" zone around a placed building
    //              center grows linearly. Single-asset byte patch on
    //              RawExport.Data inside UAssetAPI, then re-emitted as
    //              legacy uasset+uexp for the composite's to-zen step.
    //
    // The composite triplet shares the basename of the main pak so UE5
    // mounts them as one logical mod. The raw companion has its own
    // basename (..._Raw_P) and gets recognised as a companion by
    // ModsEndpoint so it aggregates back into a single logical mod in
    // the UI.
    public sealed class BuildPipeline
    {
        readonly WindrosePaths _paths;
        readonly StackPatcher _patcher;
        readonly LootPatcher _lootPatcher;
        readonly BellLimitsPatcher _bellPatcher;
        readonly BuyerPatcher _buyerPatcher;
        readonly SellerPatcher _sellerPatcher;
        readonly ItemCreatorPatcher _itemCreatorPatcher;
        readonly CropGrowthPatcher _cropGrowthPatcher;
        readonly CookingDurationPatcher _cookingDurationPatcher;
        readonly RepakResolver _repakResolver;
        readonly RetocResolver _retocResolver;

        public Action<string> Log;

        // When set, overrides the default ${ModRoot}/Builds output target.
        // The GUI sets this to Windrose's ~mods/ folder so a successful
        // build lands directly in the location the engine reads from. CLI
        // smoke tests leave this null so they keep landing in Builds/ and
        // never touch the live game install.
        public string OutputDir;

        // Optional locator for the live game's Paks/ directory. Required
        // for builds that activate any IoStore feature (pickup-radius,
        // no-smoke, or building-stability). retoc's to-legacy + unpack-raw
        // both read directly from the game's IoStore containers. The GUI
        // wires this to SteamLocator.FindVanillaPaksDir; CLI smoke tests
        // leave it null since CLI builds never enable IoStore features.
        public Func<string> GamePaksDirProvider;

        // Filename of the game container that ships the DA_BI* DataAssets
        // (the ones BuildingStabilityPatcher operates on). retoc unpack-raw
        // takes a specific .utoc file rather than a Paks directory, so we
        // need to know which container to point it at. pakchunk0_s3 has
        // been stable across Windrose 5.6 patches; if it ever moves we'll
        // see "no DA_BI assets in chunk manifest" in the build log.
        public const string StabilityContainerFilename = "pakchunk0_s3-Windows.utoc";

        // Suffix appended to the safe profile name to derive the raw
        // companion triplet's basename. The "_P" terminator makes UE5
        // recognise it as a mod container at mount time. ModsEndpoint uses
        // the same constant to aggregate the raw companion back into the
        // parent mod's list entry.
        //
        // "Raw" because the contained payloads bypass retoc's to-zen step
        // (stability bytes-patches raw zen chunks, minimap ships a loose
        // config .ini through the PakFile subsystem) - the basename is a
        // single landing pad for whichever subset of those features the
        // profile activates.
        public const string RawCompanionSuffix = "_Raw_P";

        // Vanilla MagnetRadius value (cm) the patcher multiplies against
        // to derive the patched value. 400cm = 4m is the Windrose 5.6
        // baseline; broken out as a constant so a future game patch
        // could be handled without touching call sites everywhere.
        public const float VanillaMagnetRadius = 400f;

        public BuildPipeline(WindrosePaths paths)
        {
            if (paths == null) throw new ArgumentNullException("paths");
            _paths = paths;
            _patcher = new StackPatcher();
            _lootPatcher = new LootPatcher();
            _bellPatcher = new BellLimitsPatcher();
            _buyerPatcher = new BuyerPatcher();
            _sellerPatcher = new SellerPatcher();
            _itemCreatorPatcher = new ItemCreatorPatcher();
            _cropGrowthPatcher = new CropGrowthPatcher();
            _cookingDurationPatcher = new CookingDurationPatcher();
            _repakResolver = new RepakResolver(paths.ModRoot);
            _retocResolver = new RetocResolver(paths.ModRoot);
        }

        public BuildPipelineResult Build(Profile profile, bool keepTemp = false)
        {
            if (profile == null) throw new ArgumentNullException("profile");
            if (string.IsNullOrEmpty(profile.Id)) throw new ArgumentException("Profile.Id is required");
            if (string.IsNullOrEmpty(profile.Name)) throw new ArgumentException("Profile.Name is required");
            if (!Directory.Exists(_paths.Vanilla))
                throw new DirectoryNotFoundException(
                    "Vanilla source not found: " + _paths.Vanilla
                    + " - run Dump-WindroseVanilla.ps1 first to extract it from the game pak");

            var safeName = SanitizeForFileName(profile.Name);
            var pakName = "Quartermaster_" + safeName + "_P.pak";
            var outDir = !string.IsNullOrEmpty(OutputDir) ? OutputDir : _paths.Builds;
            var outPakPath = Path.Combine(outDir, pakName);
            // Base name (no extension) shared by the main pak and the
            // pickup-radius IoStore companions (.ucas/.utoc). UE5 mounts
            // .pak/.ucas/.utoc with a matching basename as one logical
            // container, so consolidating under one prefix lets us ship
            // a single mod that combines JSON patches AND the patched
            // Blueprint - instead of the two separate "main" + "_PickupRadius"
            // mods we used to produce.
            var sharedBaseName = "Quartermaster_" + safeName + "_P";
            var sharedUcasPath = Path.Combine(outDir, sharedBaseName + ".ucas");
            var sharedUtocPath = Path.Combine(outDir, sharedBaseName + ".utoc");

            // Raw companion lives in a SEPARATE triplet (see class header
            // for why). Same out-dir, distinct basename so UE mounts both as
            // independent containers but ModsEndpoint aggregates them
            // back into one logical "Quartermaster_<name>" row in the
            // mods list. Carries whichever subset of {stability, minimap}
            // the profile activates - see BuildRawCompanion for layout.
            var rawBaseName = "Quartermaster_" + safeName + RawCompanionSuffix;
            var rawPakPath  = Path.Combine(outDir, rawBaseName + ".pak");
            var rawUcasPath = Path.Combine(outDir, rawBaseName + ".ucas");
            var rawUtocPath = Path.Combine(outDir, rawBaseName + ".utoc");

            var tmpDir = Path.Combine(_paths.BuildTmp, profile.Id);

            try
            {
                // Wipe the temp dir before patching: a stale tree from a
                // previous run could otherwise leak files into the new pak.
                if (Directory.Exists(tmpDir)) Directory.Delete(tmpDir, true);

                // Pre-clear stale outputs at the target paths. Without this,
                // a previous build that produced .ucas/.utoc would leave
                // them lingering when the user disables pickup-radius for
                // the next build - and stale IoStore companions would
                // still get mounted by the engine. Done before any work
                // so a half-finished build leaves the user no worse off.
                if (Directory.Exists(outDir))
                {
                    foreach (var p in new[]
                    {
                        outPakPath, sharedUcasPath, sharedUtocPath,
                        rawPakPath, rawUcasPath, rawUtocPath,
                    })
                    {
                        if (File.Exists(p)) File.Delete(p);
                    }
                }

                // The two patchers write into the SAME temp directory but
                // into disjoint subtrees (InventoryItems/ vs LootTables/),
                // so they can't collide. After both run, repak packs the
                // merged tree as a single .pak.
                // StackPatcher writes into the InventoryItems subtree to
                // mirror the in-pak path (and to avoid scanning LootTables/
                // for MaxCountInSlot that's never going to be there).
                var tmpInvDir = Path.Combine(tmpDir, "R5", "Plugins",
                    "R5BusinessRules", "Content", "InventoryItems");
                LogLine("Patching vanilla items -> " + tmpInvDir);
                var patchResult = _patcher.PatchToDirectory(_paths.VanillaInventoryItems, tmpInvDir, profile);
                LogLine("Patched items: " + patchResult.Written
                        + " (" + patchResult.Promoted + " promoted, "
                        + patchResult.Overridden + " overridden, "
                        + patchResult.Capped + " capped)");

                LootPatchResult lootResult = null;
                bool lootActive = HasLootConfiguration(profile);
                if (lootActive)
                {
                    var tmpLootDir = Path.Combine(tmpDir, "R5", "Plugins",
                        "R5BusinessRules", "Content", "LootTables");
                    LogLine("Patching loot tables -> " + tmpLootDir);
                    lootResult = _lootPatcher.PatchToDirectory(
                        _paths.VanillaLootTables, tmpLootDir, profile);
                    LogLine("Patched loot: " + lootResult.Written
                            + " (" + lootResult.MultiplierApplied + " multiplied, "
                            + lootResult.Edited + " edited, "
                            + lootResult.Removed + " removed-from, "
                            + lootResult.Added + " appended-to)");
                    foreach (var w in lootResult.Warnings) LogLine("  warn: " + w);
                }

                // Fast-travel-bell + signal-fire caps. Tiny JSON config
                // patch (~700 B input, one file) - ships inside the main
                // Pak1 alongside the item / loot patches, no IoStore step.
                BellLimitsPatchResult bellResult = null;
                if (HasBellLimitsConfiguration(profile))
                {
                    LogLine("Patching fast-travel build limits");
                    var bell = profile.Globals.FastTravelBells;
                    bellResult = _bellPatcher.PatchToDirectory(
                        _paths.VanillaBuildingLimits, tmpDir,
                        bell.BellCap, bell.SignalFireCap);
                    if (bellResult.Skipped)
                    {
                        LogLine("  skipped (resolved caps match vanilla 10/3 - nothing to do)");
                    }
                    else if (bellResult.Written)
                    {
                        LogLine("  bells " + bellResult.BellCap + " (vanilla 10), signal-fires "
                                + bellResult.SignalFireCap + " (vanilla 3) - "
                                + bellResult.BellsPatched + " bell + "
                                + bellResult.SignalFiresPatched + " signal-fire entries patched");
                    }
                    if (bellResult.Unmatched != null && bellResult.Unmatched.Count > 0)
                    {
                        foreach (var u in bellResult.Unmatched)
                            LogLine("  warn: unrecognised BuildLimits entry left at vanilla cap: " + u);
                    }
                }

                // Buyer trade-list edits (PlayerSells side). Writes into the
                // same tmpDir tree but under disjoint subpaths (Recipes/ +
                // RecipeLists/) so it composes cleanly with the other
                // patchers' output for a single repak invocation.
                BuyerPatchResult buyerResult = null;
                if (HasBuyerConfiguration(profile))
                {
                    LogLine("Patching buyer trade lists (PlayerSells)");
                    buyerResult = _buyerPatcher.PatchToDirectory(
                        _paths.VanillaRecipeLists, _paths.VanillaRecipes, tmpDir, profile);
                    LogLine("Patched buyer recipes: "
                            + buyerResult.RecipesEdited + " edited, "
                            + buyerResult.RecipesAdded + " added; "
                            + "lists: " + buyerResult.ListsWritten + " written ("
                            + buyerResult.RefsAdded + " refs appended, "
                            + buyerResult.RefsRemoved + " refs removed)");
                    foreach (var w in buyerResult.Warnings) LogLine("  warn: " + w);
                }

                // Seller trade-list edits (PlayerBuys side). Independent of
                // Buyer* but writes into the SAME tmpDir tree - both patchers
                // produce disjoint files (PlayerSells/* vs PlayerBuys/*
                // RecipeLists, vanilla recipes never overlap between the two
                // tabs because *_Sell and *_Buy basenames are distinct, and
                // custom recipes use disjoint prefixes "QM_Custom_*" vs
                // "QM_SCustom_*"). The Custom/ folder is shared and the two
                // sets coexist cleanly.
                SellerPatchResult sellerResult = null;
                if (HasSellerConfiguration(profile))
                {
                    LogLine("Patching seller trade lists (PlayerBuys)");
                    sellerResult = _sellerPatcher.PatchToDirectory(
                        _paths.VanillaRecipeLists, _paths.VanillaRecipes, tmpDir, profile);
                    LogLine("Patched seller recipes: "
                            + sellerResult.RecipesEdited + " edited, "
                            + sellerResult.RecipesAdded + " added; "
                            + "lists: " + sellerResult.ListsWritten + " written ("
                            + sellerResult.RefsAdded + " refs appended, "
                            + sellerResult.RefsRemoved + " refs removed)");
                    foreach (var w in sellerResult.Warnings) LogLine("  warn: " + w);
                }

                // Crop-growth + cooking-duration patches. Both ship JSON
                // DataAssets inside the same main pak as the other JSON
                // patchers (no IoStore step needed).
                //
                // CropGrowth runs independently: writes new DA_Crop_*.json
                // files under Farming/Crops/ with a scaled GrowthDuration.
                //
                // CookingDuration runs AFTER Buyer/Seller so it can merge
                // its CookingProcessDuration edit into a recipe file the
                // trade patchers already touched (preserving both edits).
                CropGrowthPatchResult cropGrowthResult = null;
                double cropGrowthMul = ResolveCropGrowthMultiplier(profile);
                bool cropGrowthActive = cropGrowthMul > 0.0 && Math.Abs(cropGrowthMul - 1.0) > 1e-9;
                if (cropGrowthActive)
                {
                    LogLine("Patching crop growth (" + cropGrowthMul.ToString("0.##") + "x)");
                    cropGrowthResult = _cropGrowthPatcher.PatchToDirectory(
                        _paths.VanillaCrops, tmpDir, cropGrowthMul);
                    LogLine("Patched crops: " + cropGrowthResult.Written
                            + " written (" + cropGrowthResult.Scanned + " scanned, "
                            + cropGrowthResult.Skipped + " skipped)");
                }

                CookingDurationPatchResult cookingDurationResult = null;
                var cookingFamilies = ResolveCookingFamilies(profile);
                bool cookingDurationActive = cookingFamilies != null && cookingFamilies.AnyActive();
                if (cookingDurationActive)
                {
                    LogLine("Patching recipe cooking durations");
                    cookingDurationResult = _cookingDurationPatcher.PatchToDirectory(
                        _paths.VanillaRecipes, tmpDir, cookingFamilies);
                    LogLine("Patched recipes: " + cookingDurationResult.Written
                            + " written (" + cookingDurationResult.MergedWithTrade
                            + " merged with trade edits, "
                            + cookingDurationResult.Scanned + " scanned, "
                            + cookingDurationResult.SkippedFamilyInactive
                            + " family-inactive, "
                            + cookingDurationResult.Skipped + " skipped)");
                }

                // Resolve which CustomItems will get a baked custom icon
                // BEFORE running the item-creator patcher: the patcher
                // needs to know whether to point each item's ItemTexture
                // at the synthesized Custom/T_QmCustomIcon_<id> asset
                // (only valid if the PNG actually exists on disk and
                // will get baked) or fall back to the template icon.
                // Doing it in this order keeps the JSON-on-disk side
                // and the texture-on-disk side in lockstep.
                var iconBakeJobs = ResolveIconBakeJobs(profile);
                var bakeableItemIds = new HashSet<string>(StringComparer.Ordinal);
                foreach (var j in iconBakeJobs) bakeableItemIds.Add(j.ItemId);

                // Item Creator: user-defined custom items synthesized from
                // vanilla templates. Lands under InventoryItems/Custom/ +
                // emits an extended copy of InventoryItems.csv (the FText
                // string-table the synthesized JSONs reference).
                ItemCreatorPatchResult itemCreatorResult = null;
                if (HasCustomItemsConfiguration(profile))
                {
                    LogLine("Synthesizing custom items");
                    itemCreatorResult = _itemCreatorPatcher.PatchToDirectory(
                        _paths.VanillaInventoryItems, _paths.VanillaInventoryItemsCsv,
                        tmpDir, profile, bakeableItemIds);
                    LogLine("Custom items: " + itemCreatorResult.ItemsWritten
                            + " written, " + itemCreatorResult.CsvRowsAppended
                            + " CSV rows appended");
                    foreach (var w in itemCreatorResult.Warnings) LogLine("  warn: " + w);
                }

                int totalWritten = patchResult.Written
                    + (lootResult != null ? lootResult.Written : 0)
                    + (bellResult != null && bellResult.Written ? 1 : 0)
                    + (buyerResult != null
                        ? buyerResult.RecipesEdited + buyerResult.RecipesAdded + buyerResult.ListsWritten
                        : 0)
                    + (sellerResult != null
                        ? sellerResult.RecipesEdited + sellerResult.RecipesAdded + sellerResult.ListsWritten
                        : 0)
                    + (itemCreatorResult != null
                        ? itemCreatorResult.ItemsWritten + (itemCreatorResult.CsvWritten ? 1 : 0)
                        : 0)
                    + (cropGrowthResult != null ? cropGrowthResult.Written : 0)
                    + (cookingDurationResult != null ? cookingDurationResult.Written : 0);
                double pickupMultiplier = ResolvePickupMultiplier(profile);
                bool pickupActive = pickupMultiplier > 0.0 && Math.Abs(pickupMultiplier - 1.0) > 1e-9;
                bool stabilityActive = ResolveStabilityEnabled(profile);
                var noSmokeCategories = ResolveNoSmokeCategories(profile);
                bool noSmokeActive = noSmokeCategories.Count > 0;
                double minimapMultiplier = ResolveMinimapMultiplier(profile);
                bool minimapActive = minimapMultiplier > 0.0 && Math.Abs(minimapMultiplier - 1.0) > 1e-9;
                double bonfireMultiplier = ResolveBonfireMultiplier(profile);
                bool bonfireActive = bonfireMultiplier > 0.0 && Math.Abs(bonfireMultiplier - 1.0) > 1e-9;
                double pickaxeMultiplier = ResolvePickaxeRangeMultiplier(profile);
                bool pickaxeActive = pickaxeMultiplier > 0.0 && Math.Abs(pickaxeMultiplier - 1.0) > 1e-9;
                var cooldownJobs = ResolveCooldownJobs(profile);
                bool cooldownsActive = cooldownJobs.Count > 0;
                var shipMusicJobs = ResolveShipMusicJobs(profile);
                bool shipMusicActive = shipMusicJobs.Count > 0;
                // iconBakeJobs was resolved earlier (before ItemCreator);
                // re-derive activity flag from the same list so the
                // composite path knows whether to add the icons source.
                bool iconsActive = iconBakeJobs.Count > 0;
                bool ioStoreActive = pickupActive || stabilityActive || noSmokeActive || minimapActive || bonfireActive || pickaxeActive || cooldownsActive || shipMusicActive || iconsActive;
                if (totalWritten == 0 && !ioStoreActive)
                {
                    throw new InvalidOperationException(
                        "Profile produces no changes - nothing to pack. "
                        + "Adjust globals or add per-item / per-loot-table overrides.");
                }

                // Build IoStore composite triplet FIRST (into a staging
                // dir), so that when repak runs afterwards it overwrites
                // the tiny .pak stub retoc produces with the real Pak1
                // content. The .ucas / .utoc are then copied to outDir
                // under the shared basename, sharing the prefix with the
                // main pak.
                //
                // For IoStore-only builds (no item/loot/bell changes),
                // repak is skipped entirely and we copy all three triplet
                // files (including the stub .pak) to outDir - the engine
                // still needs the .pak as the IoStore container's marker.
                PickupTripletResult pickupResult = null;
                NoSmokeResult noSmokeResult = null;
                BonfireRadiusResult bonfireResult = null;
                PickaxeRangeResult pickaxeResult = null;
                CooldownsResult cooldownsResult = null;
                ShipMusicResult shipMusicResult = null;
                List<IconBakerPatcher.BakeResult> iconBakeResults = null;
                bool compositeActive = pickupActive || noSmokeActive || bonfireActive || pickaxeActive || cooldownsActive || shipMusicActive || iconsActive;
                if (compositeActive)
                {
                    var compositeResult = BuildIoStoreComposite(
                        profile, outDir, pickupMultiplier, pickupActive,
                        noSmokeCategories,
                        bonfireMultiplier, bonfireActive,
                        pickaxeMultiplier, pickaxeActive,
                        cooldownJobs,
                        shipMusicJobs,
                        iconBakeJobs,
                        sharedBaseName, mainPakWillBeBuilt: totalWritten > 0);
                    pickupResult = compositeResult.Pickup;
                    noSmokeResult = compositeResult.NoSmoke;
                    bonfireResult = compositeResult.Bonfire;
                    pickaxeResult = compositeResult.PickaxeRange;
                    cooldownsResult = compositeResult.Cooldowns;
                    shipMusicResult = compositeResult.ShipMusic;
                    iconBakeResults = compositeResult.Icons;
                }

                // Raw companion is independent of the composite (see
                // class header). Built only when at least one of its
                // member features (stability / minimap) is on; goes to
                // its own _Raw_P basename. ModsEndpoint recognises the
                // suffix and treats both containers as one logical mod.
                BuildingStabilityResult stabilityResult = null;
                MinimapRangeResult minimapResult = null;
                if (stabilityActive || minimapActive)
                {
                    var rawOut = BuildRawCompanion(profile, outDir, rawBaseName,
                                                   stabilityActive, minimapActive, minimapMultiplier);
                    stabilityResult = rawOut.Stability;
                    minimapResult = rawOut.Minimap;
                }

                PakBuildResult pakResult = null;
                string pakPath = null;
                if (totalWritten > 0)
                {
                    LogLine("Resolving repak.exe...");
                    _repakResolver.Log = Log;
                    var repakExe = _repakResolver.Resolve();

                    LogLine("Packing -> " + outPakPath);
                    Directory.CreateDirectory(outDir);
                    var builder = new PakBuilder(repakExe);
                    builder.Log = Log;
                    // repak builds tmpDir recursively. The pickup-triplet
                    // staging is in tmpDir/_pickup-out/ which would leak
                    // its retoc artefacts into the main pak. Skip the
                    // staging tree when building.
                    pakResult = builder.Build(tmpDir, outPakPath, overwrite: true);
                    pakPath = outPakPath;

                    LogLine("Pak built: " + outPakPath
                            + " (" + Math.Round(pakResult.SizeBytes / 1024.0, 1) + " KB, "
                            + pakResult.FileCount + " files)");
                }
                else if (!ioStoreActive)
                {
                    LogLine("No item / loot changes - main pak skipped (IoStore-only build).");
                }

                return new BuildPipelineResult
                {
                    Profile = profile,
                    PatchResult = patchResult,
                    LootPatchResult = lootResult,
                    BellLimitsResult = bellResult,
                    BuyerPatchResult = buyerResult,
                    SellerPatchResult = sellerResult,
                    ItemCreatorResult = itemCreatorResult,
                    PakResult = pakResult,
                    PakPath = pakPath,
                    PickupResult = pickupResult,
                    PickupMultiplier = pickupActive ? (double?)pickupMultiplier : null,
                    StabilityResult = stabilityResult,
                    NoSmokeResult = noSmokeResult,
                    MinimapResult = minimapResult,
                    BonfireResult = bonfireResult,
                    PickaxeRangeResult = pickaxeResult,
                    CooldownsResult = cooldownsResult,
                    ShipMusicResult = shipMusicResult,
                    CropGrowthResult = cropGrowthResult,
                    CookingDurationResult = cookingDurationResult,
                    TmpDir = tmpDir,
                    Success = true,
                };
            }
            finally
            {
                if (!keepTemp)
                {
                    foreach (var dir in new[]
                    {
                        tmpDir,
                        Path.Combine(_paths.BuildTmp, profile.Id + "__iostore"),
                        Path.Combine(_paths.BuildTmp, profile.Id + "__raw"),
                    })
                    {
                        if (!Directory.Exists(dir)) continue;
                        try { Directory.Delete(dir, true); }
                        catch (Exception ex)
                        {
                            // Failure to clean up is annoying but not fatal,
                            // surface it in the log so the user can clean by hand.
                            LogLine("Warning: temp dir cleanup failed for " + dir + ": " + ex.Message);
                        }
                    }
                }
            }
        }

        // Builds the IoStore composite triplet (.ucas/.utoc + .pak stub)
        // for whichever IoStore features the profile activated, and
        // publishes the result to outDir under the shared basename
        // Quartermaster_<safeName>_P.*.
        //
        // Publishing rules:
        //   - Always copy .ucas + .utoc (the IoStore payload).
        //   - Copy the .pak only when no main pak is being built; if
        //     mainPakWillBeBuilt=true, the subsequent repak step writes
        //     the real Pak1 content at the same path and the stub would
        //     just be overwritten anyway.
        //
        // The retoc work happens in <_paths.BuildTmp>/<profileId>__iostore/
        // - a SIBLING of the JSON-patch tmpDir, NOT a child. If we put it
        // inside tmpDir, repak's recursive scan would sweep the retoc
        // artefacts into the main pak.
        //
        // Surfaces a clear error if the GUI didn't supply a paks-dir
        // locator - CLI builds can't enable IoStore features today.
        BuildIoStoreCompositeOutput BuildIoStoreComposite(
            Profile profile, string outDir,
            double pickupMultiplier, bool pickupActive,
            List<NoSmokeCategory> noSmokeCategories,
            double bonfireMultiplier, bool bonfireActive,
            double pickaxeMultiplier, bool pickaxeActive,
            List<CooldownJob> cooldownJobs,
            List<ShipMusicJob> shipMusicJobs,
            List<IconBakerPatcher.BakeJob> iconBakeJobs,
            string sharedBaseName, bool mainPakWillBeBuilt)
        {
            if (GamePaksDirProvider == null)
            {
                throw new InvalidOperationException(
                    "Profile requests an IoStore feature but no GamePaksDirProvider is wired up. "
                    + "This is a build-host configuration error - only the GUI build path "
                    + "can locate the live game's Paks directory.");
            }
            var gamePaksDir = GamePaksDirProvider();
            if (string.IsNullOrEmpty(gamePaksDir) || !Directory.Exists(gamePaksDir))
            {
                throw new InvalidOperationException(
                    "IoStore features need the live game's Paks directory but the locator "
                    + "returned an invalid path: " + (gamePaksDir ?? "<null>"));
            }

            LogLine("Resolving retoc.exe...");
            _retocResolver.Log = Log;
            var retocExe = _retocResolver.Resolve();

            Directory.CreateDirectory(outDir);

            // IoStore work area: sibling of tmpDir (so repak's recursive
            // scan doesn't leak retoc artefacts into the main pak). Wiped
            // at the start of each build the same way tmpDir is.
            var iostoreRoot = Path.Combine(_paths.BuildTmp, profile.Id + "__iostore");
            if (Directory.Exists(iostoreRoot)) Directory.Delete(iostoreRoot, true);
            Directory.CreateDirectory(iostoreRoot);
            var stagingBase = Path.Combine(iostoreRoot, "out", sharedBaseName);
            var legacyTmp = Path.Combine(iostoreRoot, "legacy");

            // Compose the source list. Order matters only for log
            // readability - retoc deals with the merged tree at the end.
            var sources = new List<IoStoreCompositeSource>();
            PickupBlueprintPatchResult pickupPatchResult = null;
            float magnetRadius = 0f;

            if (pickupActive)
            {
                magnetRadius = (float)(VanillaMagnetRadius * pickupMultiplier);
                var usmapPath = UsmapLocator.Find(_paths.ModRoot);
                LogLine("Pickup source: vanilla "
                        + PickupBlueprintPatcher.AssetFilterStem
                        + " (multiplier=" + pickupMultiplier
                        + ", MagnetRadius=" + magnetRadius + "cm)");
                sources.Add(new IoStoreCompositeSource
                {
                    Name = "pickup",
                    InputDir = gamePaksDir,
                    Filter = PickupBlueprintPatcher.AssetFilterStem,
                    AfterExtract = stagingDir =>
                    {
                        var legacyAssetPath = Path.Combine(stagingDir,
                            PickupBlueprintPatcher.AssetVirtualPath
                                .Replace('/', Path.DirectorySeparatorChar));
                        if (!File.Exists(legacyAssetPath))
                        {
                            throw new InvalidOperationException(
                                "retoc to-legacy did not produce the expected pickup asset at "
                                + legacyAssetPath
                                + " - the game container may have moved the asset, or "
                                + "the filter '" + PickupBlueprintPatcher.AssetFilterStem
                                + "' is wrong.");
                        }
                        var patcher = new PickupBlueprintPatcher { Log = Log };
                        pickupPatchResult = patcher.Patch(
                            legacyAssetPath, legacyAssetPath, usmapPath, magnetRadius);
                    },
                });
            }

            BonfireRadiusPatchResult bonfirePatchResult = null;
            if (bonfireActive)
            {
                // Vanilla DA_BI_Utilities_BuildingCenterT01 ships with
                // serialized InfluenceRadius=5000 + InfluenceHeight=3000
                // floats inside its RawExport.Data; we overwrite both with
                // user*5000 and user*3000 directly in the byte stream so
                // the patch never has to interpret the trailing
                // R5CollisionApproximation custom-Serialize tail.
                var usmapPath = UsmapLocator.Find(_paths.ModRoot);
                LogLine("Bonfire source: vanilla "
                        + BonfireRadiusPatcher.AssetFilterStem
                        + " (multiplier=" + bonfireMultiplier
                        + ", InfluenceRadius=" + (BonfireRadiusPatcher.VanillaInfluenceRadius * bonfireMultiplier)
                        + "cm, InfluenceHeight=" + (BonfireRadiusPatcher.VanillaInfluenceHeight * bonfireMultiplier) + "cm)");
                sources.Add(new IoStoreCompositeSource
                {
                    Name = "bonfire",
                    InputDir = gamePaksDir,
                    Filter = BonfireRadiusPatcher.AssetFilterStem,
                    AfterExtract = stagingDir =>
                    {
                        var legacyAssetPath = Path.Combine(stagingDir,
                            BonfireRadiusPatcher.AssetVirtualPath
                                .Replace('/', Path.DirectorySeparatorChar));
                        if (!File.Exists(legacyAssetPath))
                        {
                            throw new InvalidOperationException(
                                "retoc to-legacy did not produce the expected bonfire asset at "
                                + legacyAssetPath
                                + " - the game container may have moved the asset, or "
                                + "the filter '" + BonfireRadiusPatcher.AssetFilterStem
                                + "' is wrong.");
                        }
                        var patcher = new BonfireRadiusPatcher { Log = Log };
                        bonfirePatchResult = patcher.Patch(
                            legacyAssetPath, legacyAssetPath, usmapPath, bonfireMultiplier);
                    },
                });
            }

            // Pickaxe-range: patches TraceScaleModifier on every pickaxe tier
            // (4 InstanceParams DataAssets). Each tier rides as an individual
            // source so retoc to-legacy's --filter targets just that file -
            // grouping all four under one source would also work (retoc OR-
            // matches repeated --filter flags) but separating them keeps the
            // build log self-explanatory: one log line per tier showing the
            // before/after TraceScaleModifier value.
            var pickaxePatchResults = new List<PickaxeRangePatchResult>();
            if (pickaxeActive)
            {
                var usmapPath = UsmapLocator.Find(_paths.ModRoot);
                LogLine("PickaxeRange source: vanilla pickaxe InstanceParams"
                        + " (multiplier=" + pickaxeMultiplier.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture)
                        + ", " + PickaxeRangePatcher.TierAssets.Count + " tier"
                        + (PickaxeRangePatcher.TierAssets.Count == 1 ? "" : "s") + ")");

                // One filter per tier; one AfterExtract callback per source.
                // Capturing the stem+vpath in a local prevents the lambda
                // from closing over the loop variable's identity (subtle
                // bug-trap if foreach captures the same slot in older C#).
                foreach (var kv in PickaxeRangePatcher.TierAssets)
                {
                    var stem = kv.Key;
                    var virtualPath = kv.Value;
                    sources.Add(new IoStoreCompositeSource
                    {
                        Name = "pickaxe:" + stem,
                        InputDir = gamePaksDir,
                        Filter = stem,
                        AfterExtract = stagingDir =>
                        {
                            var legacyAssetPath = Path.Combine(stagingDir,
                                virtualPath.Replace('/', Path.DirectorySeparatorChar));
                            if (!File.Exists(legacyAssetPath))
                            {
                                throw new InvalidOperationException(
                                    "retoc to-legacy did not produce the expected pickaxe asset at "
                                    + legacyAssetPath
                                    + " - the game container may have moved the asset, or "
                                    + "the filter '" + stem + "' is wrong.");
                            }
                            var patcher = new PickaxeRangePatcher { Log = Log };
                            var r = patcher.Patch(
                                legacyAssetPath, legacyAssetPath, usmapPath, pickaxeMultiplier);
                            pickaxePatchResults.Add(r);
                        },
                    });
                }
            }

            // Cooldowns: one IoStoreCompositeSource per asset job. Each job
            // carries its family, asset stem/virtual-path, multiplier, and
            // which patch shape to apply (ScalableFloat duration vs top-level
            // Magnitude vs PassiveReload vs ShipCannon battery walk).
            // Separating one source per asset keeps the build log
            // self-explanatory and matches the per-tier layout of pickaxe.
            var cooldownPatchResults = new List<CooldownJobResult>();
            if (cooldownJobs != null && cooldownJobs.Count > 0)
            {
                var usmapPath = UsmapLocator.Find(_paths.ModRoot);
                LogLine("Cooldowns source: " + cooldownJobs.Count + " asset"
                        + (cooldownJobs.Count == 1 ? "" : "s")
                        + " across " + CountCooldownFamilies(cooldownJobs) + " famil"
                        + (CountCooldownFamilies(cooldownJobs) == 1 ? "y" : "ies"));
                foreach (var job in cooldownJobs)
                {
                    var localJob = job;
                    sources.Add(new IoStoreCompositeSource
                    {
                        Name = "cooldown:" + localJob.AssetStem,
                        InputDir = gamePaksDir,
                        Filter = localJob.AssetStem,
                        AfterExtract = stagingDir =>
                        {
                            var legacyAssetPath = Path.Combine(stagingDir,
                                localJob.VirtualPath.Replace('/', Path.DirectorySeparatorChar));
                            if (!File.Exists(legacyAssetPath))
                            {
                                throw new InvalidOperationException(
                                    "retoc to-legacy did not produce the expected cooldown asset at "
                                    + legacyAssetPath
                                    + " - the game container may have moved the asset, or "
                                    + "the filter '" + localJob.AssetStem + "' is wrong.");
                            }
                            var r = RunCooldownJob(localJob, legacyAssetPath, usmapPath);
                            cooldownPatchResults.Add(r);
                        },
                    });
                }
            }

            // Ship music: each replaced shanty slot is a "pre-staged"
            // source - we don't run retoc to-legacy at all because the
            // user's UE5-Editor-cooked SoundWave triplet already IS the
            // legacy bytes. The AfterExtract callback copies the user's
            // uasset+uexp+ubulk into the staging tree and re-writes the
            // NameMap entries so the engine resolves the file under the
            // vanilla slot's asset path.
            var shipMusicPatchResults = new List<ShipMusicPatchResult>();
            if (shipMusicJobs != null && shipMusicJobs.Count > 0)
            {
                var usmapPath = UsmapLocator.Find(_paths.ModRoot);
                var encoderPath = _paths.BinkAudioEncoderPath;
                var templateUassetPath = _paths.ShipMusicTemplateUasset;
                var templateUexpPath = _paths.ShipMusicTemplateUexp;
                if (!File.Exists(encoderPath))
                    throw new FileNotFoundException(
                        "Bink Audio encoder not found at " + encoderPath
                        + " - ship-music slots cannot be built without it.");
                if (!File.Exists(templateUassetPath) || !File.Exists(templateUexpPath))
                    throw new FileNotFoundException(
                        "Ship-music template missing under " + Path.GetDirectoryName(templateUassetPath)
                        + " - expected SoundWave_BinkInline.uasset + .uexp.");
                LogLine("ShipMusic source: " + shipMusicJobs.Count + " custom shanty"
                        + (shipMusicJobs.Count == 1 ? "" : "s"));
                foreach (var job in shipMusicJobs)
                {
                    var localJob = job;
                    sources.Add(new IoStoreCompositeSource
                    {
                        Name = "ship-music:" + localJob.Slot.Stem,
                        // InputDir intentionally null - this source is
                        // pre-staged via the callback below; the builder
                        // skips retoc to-legacy entirely for it.
                        InputDir = null,
                        AfterExtract = stagingDir =>
                        {
                            var patcher = new ShipMusicPatcher { Log = Log };
                            var r = patcher.PatchFromWav(
                                localJob.UserWavPath,
                                templateUassetPath,
                                templateUexpPath,
                                encoderPath,
                                stagingDir,
                                localJob.Slot,
                                usmapPath);
                            r.OriginalFilename = localJob.OriginalFilename;
                            shipMusicPatchResults.Add(r);
                        },
                    });
                }
            }

            NoSmokeResult noSmokeOut = null;
            if (noSmokeCategories != null && noSmokeCategories.Count > 0)
            {
                // Self-bake: collect every Niagara asset in the active
                // categories, pull them in a single retoc to-legacy call
                // (multi --filter), then patch each one's NiagaraSystem
                // export to disable every emitter handle. The composite
                // builder picks them up from the shared staging tree
                // along with any other sources.
                var usmapPath = UsmapLocator.Find(_paths.ModRoot);
                var assetPaths = new List<string>();
                var filterStems = new List<string>();
                foreach (var cat in noSmokeCategories)
                {
                    string[] virtualPaths;
                    if (!NoSmokePatcher.CategoryAssets.TryGetValue(cat, out virtualPaths))
                        continue;
                    foreach (var vp in virtualPaths)
                    {
                        assetPaths.Add(vp);
                        filterStems.Add(Path.GetFileNameWithoutExtension(vp));
                    }
                }
                LogLine("NoSmoke source: vanilla Niagara FX ("
                        + string.Join(", ", noSmokeCategories)
                        + " -> " + assetPaths.Count + " asset"
                        + (assetPaths.Count == 1 ? "" : "s") + ")");
                var perAssetResults = new List<NoSmokeAssetResult>();
                sources.Add(new IoStoreCompositeSource
                {
                    Name = "no-smoke",
                    InputDir = gamePaksDir,
                    Filters = filterStems,
                    AfterExtract = stagingDir =>
                    {
                        var patcher = new NoSmokePatcher { Log = Log };
                        for (int i = 0; i < assetPaths.Count; i++)
                        {
                            var legacyAssetPath = Path.Combine(stagingDir,
                                assetPaths[i].Replace('/', Path.DirectorySeparatorChar));
                            if (!File.Exists(legacyAssetPath))
                            {
                                throw new InvalidOperationException(
                                    "retoc to-legacy did not produce the expected NoSmoke asset at "
                                    + legacyAssetPath
                                    + " - the game container may have moved the asset, or "
                                    + "the filter '" + filterStems[i] + "' is wrong.");
                            }
                            var pr = patcher.Patch(legacyAssetPath, usmapPath);
                            perAssetResults.Add(new NoSmokeAssetResult
                            {
                                AssetPath = assetPaths[i],
                                FlippedHandles = pr.FlippedHandles,
                                TotalHandles = pr.TotalHandles,
                            });
                        }
                    },
                });
                noSmokeOut = new NoSmokeResult
                {
                    Categories = new List<NoSmokeCategory>(noSmokeCategories),
                    AssetResults = perAssetResults,
                };
            }

            // Icon-bake source: pulls the vanilla Piastre Texture2D as a
            // template, then synthesises a fresh T_QmCustomIcon_<id> for
            // every CustomItem with an uploaded PNG. UAssetAPI rewrites
            // the FName slots so the new asset lives at .../Custom/<id>
            // (a brand-new asset path, not an override of the Piastre).
            // The template files themselves are removed from staging
            // afterwards so to-zen doesn't repackage vanilla bytes.
            List<IconBakerPatcher.BakeResult> iconResults = null;
            if (iconBakeJobs != null && iconBakeJobs.Count > 0)
            {
                LogLine("Icons source: " + iconBakeJobs.Count
                        + " custom icon" + (iconBakeJobs.Count == 1 ? "" : "s")
                        + " (template " + IconBakerPatcher.TemplateAssetStem + ")");
                sources.Add(new IoStoreCompositeSource
                {
                    Name = "icons",
                    InputDir = gamePaksDir,
                    Filter = IconBakerPatcher.TemplateAssetStem,
                    AfterExtract = stagingDir =>
                    {
                        var baker = new IconBakerPatcher { Log = Log };
                        iconResults = baker.Bake(stagingDir, iconBakeJobs);
                        // Remove the unmodified template from staging so
                        // to-zen doesn't ship a duplicate of vanilla
                        // (which would then OVERRIDE the vanilla Piastre,
                        // exactly what we don't want).
                        baker.RemoveTemplateFromStaging(stagingDir);
                        foreach (var r in iconResults)
                        {
                            LogLine("  baked " + r.ItemId
                                    + " (PNG=" + r.PngBytesIn + " B, uexp=" + r.UexpBytesOut + " B)"
                                    + " -> " + r.ItemTextureRef);
                        }
                    },
                });
            }

            LogLine("Building IoStore composite triplet -> staging ("
                    + sources.Count + " source" + (sources.Count == 1 ? "" : "s") + ")");

            var builder = new IoStoreCompositeBuilder { Log = Log };
            var compositeResult = builder.Build(new IoStoreCompositeRequest
            {
                RetocExe = retocExe,
                OutputBasePath = stagingBase,
                TempDir = legacyTmp,
                Overwrite = true,
                Sources = sources,
            });

            // Publish to outDir under the shared basename. .ucas + .utoc
            // always, .pak only if the main pipeline isn't going to write
            // a real Pak1 there afterwards.
            var finalPak  = Path.Combine(outDir, sharedBaseName + ".pak");
            var finalUcas = Path.Combine(outDir, sharedBaseName + ".ucas");
            var finalUtoc = Path.Combine(outDir, sharedBaseName + ".utoc");
            File.Copy(compositeResult.UcasPath, finalUcas, true);
            File.Copy(compositeResult.UtocPath, finalUtoc, true);
            if (!mainPakWillBeBuilt)
            {
                File.Copy(compositeResult.PakPath, finalPak, true);
            }

            LogLine("IoStore composite published: "
                    + sharedBaseName + ".{ucas,utoc}"
                    + (mainPakWillBeBuilt ? "" : ",pak")
                    + " -> " + outDir
                    + " (.ucas=" + compositeResult.UcasSize + " B, "
                    + ".utoc=" + compositeResult.UtocSize + " B"
                    + (mainPakWillBeBuilt ? "" : ", .pak=" + compositeResult.PakSize + " B")
                    + ")");

            PickupTripletResult pickupOut = null;
            if (pickupActive)
            {
                pickupOut = new PickupTripletResult
                {
                    PakPath  = mainPakWillBeBuilt ? null : finalPak,
                    UcasPath = finalUcas,
                    UtocPath = finalUtoc,
                    PakSize  = mainPakWillBeBuilt ? 0 : compositeResult.PakSize,
                    UcasSize = compositeResult.UcasSize,
                    UtocSize = compositeResult.UtocSize,
                    MagnetRadius = magnetRadius,
                    PatchResult = pickupPatchResult,
                    LegacyTempDir = compositeResult.StagingDir,
                };
            }

            BonfireRadiusResult bonfireOut = null;
            if (bonfireActive)
            {
                bonfireOut = new BonfireRadiusResult
                {
                    Enabled = true,
                    Multiplier = bonfireMultiplier,
                    Patch = bonfirePatchResult,
                    UcasPath = finalUcas,
                    UtocPath = finalUtoc,
                    PakPath = mainPakWillBeBuilt ? null : finalPak,
                };
            }

            PickaxeRangeResult pickaxeOut = null;
            if (pickaxeActive)
            {
                pickaxeOut = new PickaxeRangeResult
                {
                    Enabled = true,
                    Multiplier = pickaxeMultiplier,
                    AssetResults = pickaxePatchResults,
                    UcasPath = finalUcas,
                    UtocPath = finalUtoc,
                    PakPath = mainPakWillBeBuilt ? null : finalPak,
                };
            }

            CooldownsResult cooldownsOut = null;
            if (cooldownPatchResults.Count > 0)
            {
                cooldownsOut = new CooldownsResult
                {
                    Enabled = true,
                    JobResults = cooldownPatchResults,
                    UcasPath = finalUcas,
                    UtocPath = finalUtoc,
                    PakPath = mainPakWillBeBuilt ? null : finalPak,
                };
            }

            ShipMusicResult shipMusicOut = null;
            if (shipMusicPatchResults.Count > 0)
            {
                shipMusicOut = new ShipMusicResult
                {
                    Enabled = true,
                    SlotResults = shipMusicPatchResults,
                    UcasPath = finalUcas,
                    UtocPath = finalUtoc,
                    PakPath = mainPakWillBeBuilt ? null : finalPak,
                };
            }

            return new BuildIoStoreCompositeOutput
            {
                Pickup = pickupOut,
                NoSmoke = noSmokeOut,
                Bonfire = bonfireOut,
                PickaxeRange = pickaxeOut,
                Cooldowns = cooldownsOut,
                ShipMusic = shipMusicOut,
                Icons = iconResults,
            };
        }

        // Internal carrier so BuildIoStoreComposite can return all
        // feature-specific result objects to the caller without a tuple.
        sealed class BuildIoStoreCompositeOutput
        {
            public PickupTripletResult Pickup;
            public NoSmokeResult NoSmoke;
            public BonfireRadiusResult Bonfire;
            public PickaxeRangeResult PickaxeRange;
            public CooldownsResult Cooldowns;
            public ShipMusicResult ShipMusic;
            public List<IconBakerPatcher.BakeResult> Icons;
        }

        // Walks profile.CustomItems and produces one IconBakerPatcher.BakeJob
        // per item that has an uploaded PNG (IconPath set + the file
        // exists on disk under Profiles/<profileId>/Icons/). Items with
        // a configured-but-missing PNG surface a build-log warning and
        // get skipped (the baked-asset reference would be a dangling
        // pointer otherwise, which the engine renders as a broken
        // checkered icon ingame).
        List<IconBakerPatcher.BakeJob> ResolveIconBakeJobs(Profile profile)
        {
            var jobs = new List<IconBakerPatcher.BakeJob>();
            if (profile == null || profile.CustomItems == null) return jobs;

            var iconsDir = _paths.ProfileIconsDir(profile.Id);
            foreach (var ci in profile.CustomItems)
            {
                if (ci == null) continue;
                if (string.IsNullOrWhiteSpace(ci.Id)) continue;
                if (string.IsNullOrWhiteSpace(ci.IconPath)) continue;

                // IconPath is a basename only (the upload endpoint enforces
                // "<itemId>.png"); rebuild the absolute path here.
                var absPath = Path.Combine(iconsDir, ci.IconPath);
                if (!File.Exists(absPath))
                {
                    LogLine("  warn: custom item '" + ci.Id + "' references icon '"
                            + ci.IconPath + "' but the file is missing at "
                            + absPath + " - skipping bake (item ships with template icon).");
                    continue;
                }
                jobs.Add(new IconBakerPatcher.BakeJob
                {
                    ItemId = ci.Id,
                    PngPath = absPath,
                });
            }
            return jobs;
        }

        // Builds the raw companion under <rawBaseName>.{pak[,ucas,utoc]},
        // carrying whichever subset of {stability, minimap} the profile
        // activated. Adaptive layout - emitted files depend on inputs:
        //
        //   stability && minimap  -> .pak (minimap INI) + .ucas/.utoc (stability)
        //   stability only        -> 347-byte stub .pak + .ucas/.utoc
        //   minimap only          -> .pak (minimap INI) ONLY, no .ucas/.utoc
        //
        // The raw companion is INDEPENDENT of the composite triplet
        // because (a) the retoc paths it uses for stability (unpack-raw
        // + pack-raw) produce IoStore container bytes incompatible with
        // the composite's to-zen output, and (b) minimap ships a loose
        // .ini file in the PakFile subsystem which to-zen would silently
        // drop. See class header for the full reasoning.
        BuildRawCompanionOutput BuildRawCompanion(
            Profile profile, string outDir, string rawBaseName,
            bool stabilityActive, bool minimapActive, double minimapMultiplier)
        {
            if (!stabilityActive && !minimapActive)
            {
                throw new InvalidOperationException(
                    "BuildRawCompanion called with no active feature - this is a "
                    + "programmer error; the caller should have skipped the call.");
            }

            Directory.CreateDirectory(outDir);

            // Raw work area. Sibling of tmpDir + iostore dir so a single
            // keepTemp=false run can wipe all three independently.
            var rawRoot = Path.Combine(_paths.BuildTmp, profile.Id + "__raw");
            if (Directory.Exists(rawRoot)) Directory.Delete(rawRoot, true);
            Directory.CreateDirectory(rawRoot);

            // Resolve outputs eagerly so cleanup paths can always reach them.
            var finalPak  = Path.Combine(outDir, rawBaseName + ".pak");
            var finalUcas = Path.Combine(outDir, rawBaseName + ".ucas");
            var finalUtoc = Path.Combine(outDir, rawBaseName + ".utoc");

            // 1. STABILITY: when active, builds the .ucas/.utoc payload
            //    via retoc unpack-raw + byte-patch + retoc pack-raw, plus
            //    an empty stub .pak to use IF minimap isn't going to
            //    supply a real one. The .pak path here is a temporary
            //    location inside rawRoot; the final pak copy is decided
            //    after step 2 runs.
            BuildingStabilityResult stabilityResult = null;
            string srcUcas = null, srcUtoc = null, stubPak = null;
            if (stabilityActive)
            {
                stabilityResult = BuildStabilityInsideRawRoot(
                    profile, rawRoot, rawBaseName,
                    out srcUcas, out srcUtoc, out stubPak);
            }

            // 2. MINIMAP: when active, lazy-extracts the vanilla
            //    DefaultR5MapSettings.ini, scales the four reveal-range
            //    floats by `multiplier`, and repaks the result into a
            //    real .pak. This .pak displaces any stub from step 1.
            MinimapRangeResult minimapResult = null;
            string srcRealPak = null;
            if (minimapActive)
            {
                minimapResult = BuildMinimapPakInsideRawRoot(
                    profile, rawRoot, rawBaseName, minimapMultiplier,
                    out srcRealPak);
            }

            // 3. PUBLISH: copy the resolved files into outDir under the
            //    raw basename. Adaptive: stub .pak is used only when no
            //    real minimap pak exists; .ucas/.utoc are emitted only
            //    when stability provided them.
            var publishedPak = srcRealPak ?? stubPak;
            if (publishedPak == null)
            {
                // Both features somehow produced nothing - shouldn't
                // happen because we required at least one active above.
                throw new InvalidOperationException(
                    "Raw companion produced no .pak - internal pipeline error.");
            }
            File.Copy(publishedPak, finalPak, true);
            long finalPakSize = new FileInfo(finalPak).Length;

            long finalUcasSize = 0, finalUtocSize = 0;
            if (srcUcas != null && srcUtoc != null)
            {
                File.Copy(srcUcas, finalUcas, true);
                File.Copy(srcUtoc, finalUtoc, true);
                finalUcasSize = new FileInfo(finalUcas).Length;
                finalUtocSize = new FileInfo(finalUtoc).Length;
            }

            // Reflect the published paths onto the per-feature results
            // so callers see "where on disk did my feature land".
            if (stabilityResult != null)
            {
                stabilityResult.PakPath  = finalPak;
                stabilityResult.UcasPath = finalUcas;
                stabilityResult.UtocPath = finalUtoc;
                stabilityResult.PakSize  = finalPakSize;
                stabilityResult.UcasSize = finalUcasSize;
                stabilityResult.UtocSize = finalUtocSize;
            }
            if (minimapResult != null)
            {
                minimapResult.PakPath = finalPak;
                minimapResult.PakSize = finalPakSize;
            }

            // Single combined log line so the build log stays readable
            // regardless of which subset is active.
            var emittedFiles = ".pak"
                + (finalUcasSize > 0 ? ",ucas" : "")
                + (finalUtocSize > 0 ? ",utoc" : "");
            LogLine("Raw companion published: " + rawBaseName + ".{" + emittedFiles
                    + "} -> " + outDir
                    + " (.pak=" + finalPakSize + " B"
                    + (finalUcasSize > 0 ? ", .ucas=" + finalUcasSize + " B" : "")
                    + (finalUtocSize > 0 ? ", .utoc=" + finalUtocSize + " B" : "")
                    + ")");

            return new BuildRawCompanionOutput
            {
                Stability = stabilityResult,
                Minimap = minimapResult,
            };
        }

        // Inner stability builder: runs the retoc unpack-raw + to-legacy
        // + BuildingStabilityPatcher + pack-raw pipeline inside rawRoot,
        // and produces the source paths for .pak/.ucas/.utoc that the
        // caller then publishes. Splits the pak source: srcStubPak is
        // the 347-byte empty marker; the caller may opt to use a real
        // pak instead (when minimap is also active).
        BuildingStabilityResult BuildStabilityInsideRawRoot(
            Profile profile, string rawRoot, string rawBaseName,
            out string srcUcas, out string srcUtoc, out string srcStubPak)
        {
            if (GamePaksDirProvider == null)
            {
                throw new InvalidOperationException(
                    "Profile requests building-stability but no GamePaksDirProvider is wired up. "
                    + "This is a build-host configuration error - only the GUI build path "
                    + "can locate the live game's Paks directory.");
            }
            var gamePaksDir = GamePaksDirProvider();
            if (string.IsNullOrEmpty(gamePaksDir) || !Directory.Exists(gamePaksDir))
            {
                throw new InvalidOperationException(
                    "Building-stability needs the live game's Paks directory but the locator "
                    + "returned an invalid path: " + (gamePaksDir ?? "<null>"));
            }
            var stabUtocSrc = Path.Combine(gamePaksDir, StabilityContainerFilename);
            if (!File.Exists(stabUtocSrc))
            {
                throw new FileNotFoundException(
                    "Building-stability needs the vanilla container "
                    + StabilityContainerFilename + " in the game Paks dir but it wasn't found: "
                    + stabUtocSrc + " - has the game been patched? Check the actual chunk numbering.");
            }

            LogLine("Resolving retoc.exe...");
            _retocResolver.Log = Log;
            var retocExe = _retocResolver.Resolve();
            var usmapPath = UsmapLocator.Find(_paths.ModRoot);

            var rawDir    = Path.Combine(rawRoot, "stability-raw");
            var legacyDir = Path.Combine(rawRoot, "stability-legacy");
            var outBase   = Path.Combine(rawRoot, "stability-out", rawBaseName);
            Directory.CreateDirectory(Path.GetDirectoryName(outBase));

            // Step 1: unpack-raw the container that ships DA_BIs into
            // <rawDir>/chunks + <rawDir>/manifest.json. Includes EVERY
            // chunk in pakchunk0_s3 (assets + their dependency textures,
            // materials, meshes) - we filter down to just the 787 DA_BI
            // chunks in step 3.
            LogLine("retoc unpack-raw: " + StabilityContainerFilename + " -> " + rawDir);
            RunRetoc(retocExe, new[] { "unpack-raw", stabUtocSrc, rawDir });

            var chunksDir    = Path.Combine(rawDir, "chunks");
            var manifestPath = Path.Combine(rawDir, "manifest.json");
            if (!Directory.Exists(chunksDir) || !File.Exists(manifestPath))
            {
                throw new InvalidOperationException(
                    "retoc unpack-raw produced unexpected layout under " + rawDir
                    + " - expected chunks/ directory + manifest.json sibling.");
            }

            // Step 2: pull the 862 vanilla DA_BIs as legacy uasset/uexp
            // pairs into a separate staging dir. These are used ONLY to
            // probe IntegritySettings byte patterns; we never round-trip
            // them through to-zen (that's the path that crashes the game).
            LogLine("retoc to-legacy --filter " + BuildingStabilityPatcher.AssetFilterStem
                    + " -> " + legacyDir);
            RunRetoc(retocExe, new[]
            {
                "to-legacy", gamePaksDir, legacyDir, "--version", "UE5_6",
                "--filter", BuildingStabilityPatcher.AssetFilterStem,
            });

            // Step 3: byte-patch every supported DA_BI* chunk + drop the
            // 75 excluded assets from the chunk set and the manifest.
            LogLine("Stability: patching IntegritySettings in zen chunks");
            var patcher = new BuildingStabilityPatcher { Log = Log };
            var assetResults = patcher.PatchChunks(
                legacyDir, chunksDir, manifestPath, usmapPath);

            int patched = 0, skipped = 0, excluded = 0;
            foreach (var r in assetResults)
            {
                if (r.Patched) patched++;
                else if (r.Reason == "excluded-by-skiplist") excluded++;
                else skipped++;
            }
            LogLine("Stability: patched=" + patched + ", skipped=" + skipped
                    + ", excluded=" + excluded);

            // Step 4: re-pack the (now filtered + patched) chunk set into
            // an IoStore container. pack-raw produces ONLY .ucas + .utoc
            // (it doesn't emit a .pak marker like to-zen does). UE5 needs
            // the .pak as a "this is an IoStore mod" marker at mount time,
            // so we generate the marker separately in step 5.
            LogLine("retoc pack-raw: " + rawDir + " -> " + outBase + ".utoc");
            RunRetoc(retocExe, new[] { "pack-raw", rawDir, outBase + ".utoc" });

            srcUcas = outBase + ".ucas";
            srcUtoc = outBase + ".utoc";
            if (!File.Exists(srcUcas) || !File.Exists(srcUtoc))
            {
                throw new InvalidOperationException(
                    "retoc pack-raw reported success but .ucas/.utoc missing under " + outBase);
            }

            // Step 5: synthesise the empty pak marker via retoc to-zen on
            // an empty input dir. to-zen always emits a 347-byte stub .pak
            // alongside its real .ucas/.utoc; we discard the latter and
            // reuse only the .pak. The stub is deterministic + content-
            // independent (UE accepts it as a generic IoStore mod marker
            // pointing at any same-version companion .ucas/.utoc), so
            // bytes from an "empty container" stub work just as well as
            // bytes from a "full container" stub.
            //
            // If the caller goes on to build a real minimap pak, it'll
            // displace this stub; the stub itself is only published when
            // minimap is inactive.
            var stubInputDir = Path.Combine(rawRoot, "stub-input");
            var stubOutDir   = Path.Combine(rawRoot, "stub-out");
            Directory.CreateDirectory(stubInputDir);
            Directory.CreateDirectory(stubOutDir);
            var stubUtocPath = Path.Combine(stubOutDir, "stub.utoc");
            LogLine("retoc to-zen (stub pak only): " + stubInputDir + " -> " + stubUtocPath);
            RunRetoc(retocExe, new[]
            {
                "to-zen", "--version", "UE5_6", stubInputDir, stubUtocPath,
            });
            srcStubPak = Path.Combine(stubOutDir, "stub.pak");
            if (!File.Exists(srcStubPak))
            {
                throw new InvalidOperationException(
                    "retoc to-zen (empty stub) did not produce a .pak at " + srcStubPak);
            }

            return new BuildingStabilityResult
            {
                Enabled = true,
                AssetResults = assetResults,
                // Final paths/sizes are filled in by the caller after
                // publishing.
            };
        }

        // Inner minimap builder: ensures the vanilla INI is in cache,
        // runs MinimapRangePatcher to scale the four reveal-range fields,
        // and invokes repak.exe to pack a single-file V8B .pak under
        // rawRoot. Returns the source pak path through srcRealPak so
        // the caller can publish it.
        MinimapRangeResult BuildMinimapPakInsideRawRoot(
            Profile profile, string rawRoot, string rawBaseName, double multiplier,
            out string srcRealPak)
        {
            // Lazy-extract or hit the cache for the vanilla baseline.
            // The extractor logs whether it had to run repak unpack.
            var configExtractor = new VanillaConfigExtractor(_paths) { Log = Log };
            var vanillaIniPath = configExtractor.EnsureMapSettings();

            // Stage the patched INI under the in-pak path the game
            // expects (R5/Config/DefaultR5MapSettings.ini).
            var minimapStageRoot = Path.Combine(rawRoot, "minimap-stage");
            var stagedIni = Path.Combine(minimapStageRoot,
                "R5", "Config", "DefaultR5MapSettings.ini");

            var patcher = new MinimapRangePatcher { Log = Log };
            var minimapPatch = patcher.PatchToFile(vanillaIniPath, stagedIni, multiplier);

            LogLine("Resolving repak.exe...");
            _repakResolver.Log = Log;
            var repakExe = _repakResolver.Resolve();

            var pakOutDir = Path.Combine(rawRoot, "minimap-out");
            Directory.CreateDirectory(pakOutDir);
            srcRealPak = Path.Combine(pakOutDir, rawBaseName + ".pak");

            var builder = new PakBuilder(repakExe) { Log = Log };
            builder.Build(minimapStageRoot, srcRealPak, overwrite: true);

            return new MinimapRangeResult
            {
                Enabled = true,
                Multiplier = multiplier,
                Patch = minimapPatch,
                // PakPath / PakSize are filled in by the caller after
                // publishing (they refer to the final outDir path, not
                // the staging path).
            };
        }

        // Internal carrier so BuildRawCompanion can return both feature
        // result objects from one method without inventing a tuple type.
        sealed class BuildRawCompanionOutput
        {
            public BuildingStabilityResult Stability;
            public MinimapRangeResult Minimap;
        }

        // Direct retoc invocation for the stability flow (which uses
        // unpack-raw + pack-raw instead of going through the
        // IoStoreCompositeBuilder). Same process semantics as the
        // builder's RunRetoc - stdout/stderr captured, non-zero exit
        // raises an InvalidOperationException with the stderr text.
        void RunRetoc(string retocExe, string[] args)
        {
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = retocExe,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            foreach (var a in args) psi.ArgumentList.Add(a);

            WineHelper.ApplyWine(psi);
            var proc = System.Diagnostics.Process.Start(psi);
            var stdout = proc.StandardOutput.ReadToEnd();
            var stderr = proc.StandardError.ReadToEnd();
            proc.WaitForExit();

            if (proc.ExitCode != 0)
            {
                throw new InvalidOperationException(
                    "retoc " + args[0] + " failed (exit " + proc.ExitCode + ")\n"
                    + (string.IsNullOrEmpty(stderr) ? stdout : stderr));
            }
        }

        // Resolves the effective multiplier the build should use, with the
        // "no pickup mod" sentinel (0 or 1.0) collapsed to 1.0. Centralized
        // here so the readiness check, the activation flag, and the actual
        // patch all see the same number.
        static double ResolvePickupMultiplier(Profile profile)
        {
            if (profile.Globals == null || profile.Globals.PickupRadius == null) return 1.0;
            var pr = profile.Globals.PickupRadius;
            if (pr.Multiplier.HasValue) return pr.Multiplier.Value;
            return 1.0;
        }

        // True when the profile asks to enable the building-stability
        // single-toggle. False (or null/missing) means no stability
        // assets ship for this profile.
        static bool ResolveStabilityEnabled(Profile profile)
        {
            var bs = profile.Globals != null ? profile.Globals.BuildingStability : null;
            if (bs == null) return false;
            return bs.Enabled.GetValueOrDefault(false);
        }

        // Resolves the effective minimap-range multiplier. 1.0 (default)
        // means "no minimap pak ships" - same null/1.0 collapse pattern as
        // pickup-radius, so the readiness check, the activation flag, and
        // the patcher all see the same number.
        static double ResolveMinimapMultiplier(Profile profile)
        {
            if (profile.Globals == null || profile.Globals.MinimapRange == null) return 1.0;
            var mr = profile.Globals.MinimapRange;
            if (mr.Multiplier.HasValue) return mr.Multiplier.Value;
            return 1.0;
        }

        // Resolves the effective bonfire / building-center influence
        // multiplier. 1.0 (default) means "no bonfire patch ships" - same
        // null/1.0 collapse pattern as pickup-radius and minimap.
        static double ResolveBonfireMultiplier(Profile profile)
        {
            if (profile.Globals == null || profile.Globals.BonfireRadius == null) return 1.0;
            var br = profile.Globals.BonfireRadius;
            if (br.Multiplier.HasValue) return br.Multiplier.Value;
            return 1.0;
        }

        // Resolves the effective pickaxe-range multiplier (applied to each
        // tier's TraceScaleModifier). 1.0 (default) means "no pickaxe patch
        // ships" - same null/1.0 collapse pattern as the other multiplier
        // globals.
        static double ResolvePickaxeRangeMultiplier(Profile profile)
        {
            if (profile.Globals == null || profile.Globals.PickaxeRange == null) return 1.0;
            var pr = profile.Globals.PickaxeRange;
            if (pr.Multiplier.HasValue) return pr.Multiplier.Value;
            return 1.0;
        }

        // Resolves the effective crop-growth multiplier (applied to every
        // DA_Crop_*.json's GrowthDuration). 1.0 = no crop-growth patch
        // ships.
        static double ResolveCropGrowthMultiplier(Profile profile)
        {
            var pt = profile.Globals != null ? profile.Globals.ProductionTimes : null;
            if (pt == null) return 1.0;
            if (pt.CropGrowthMultiplier.HasValue) return pt.CropGrowthMultiplier.Value;
            return 1.0;
        }

        // Resolves the active cooking-duration family multipliers. Returns
        // a populated FamilyMultipliers struct even when the user has only
        // some families active (per-family null collapse happens inside
        // the patcher). Returns null only when the whole ProductionTimes
        // block is absent (so the build can skip the patcher entirely
        // for stack/loot/cooldown-only profiles).
        static CookingDurationPatcher.FamilyMultipliers ResolveCookingFamilies(Profile profile)
        {
            var pt = profile.Globals != null ? profile.Globals.ProductionTimes : null;
            if (pt == null) return null;
            return new CookingDurationPatcher.FamilyMultipliers
            {
                Smelting     = pt.SmeltingMultiplier,
                Kiln         = pt.KilnMultiplier,
                Tanning      = pt.TanningMultiplier,
                Milling      = pt.MillingMultiplier,
                BuildingBits = pt.BuildingBitsMultiplier,
                Decoration   = pt.DecorationMultiplier,
                ArmorWeapon  = pt.ArmorWeaponMultiplier,
                TradeOutpost = pt.TradeOutpostMultiplier,
                Other        = pt.OtherMultiplier,
            };
        }

        // Resolves the active set of cooldown patch jobs. Each entry pairs
        // an asset (stem + virtual path) with the multiplier to apply and
        // the patch shape to use. 8 family multipliers fan out into 1..N
        // jobs per family (Elixir = 1 asset, ShipRepairKit = 2, RangedReload
        // = ~20, ShipCannon = ~8). Returns an empty list when every family
        // is null or at 1.0 (vanilla).
        static List<CooldownJob> ResolveCooldownJobs(Profile profile)
        {
            var jobs = new List<CooldownJob>();
            var cd = profile.Globals != null ? profile.Globals.Cooldowns : null;
            if (cd == null) return jobs;

            if (HasCooldownMultiplier(cd.ElixirMultiplier))
            {
                AddScalableFloatJobs(jobs, CooldownsPatcher.ElixirAssets,
                    cd.ElixirMultiplier.Value, "elixir");
            }
            if (HasCooldownMultiplier(cd.MedicineMultiplier))
            {
                AddTopLevelMagnitudeJobs(jobs, CooldownsPatcher.MedicineAssets,
                    cd.MedicineMultiplier.Value, "medicine");
            }
            if (HasCooldownMultiplier(cd.RecallMultiplier))
            {
                AddTopLevelMagnitudeJobs(jobs, CooldownsPatcher.RecallAssets,
                    cd.RecallMultiplier.Value, "recall");
            }
            if (HasCooldownMultiplier(cd.ShipRepairKitMultiplier))
            {
                AddScalableFloatJobs(jobs, CooldownsPatcher.ShipRepairKitAssets,
                    cd.ShipRepairKitMultiplier.Value, "ship-repair-kit");
            }
            if (HasCooldownMultiplier(cd.BoarWhistleMultiplier))
            {
                AddScalableFloatJobs(jobs, CooldownsPatcher.BoarWhistleAssets,
                    cd.BoarWhistleMultiplier.Value, "boar-whistle");
            }
            if (HasCooldownMultiplier(cd.ShipSummonMultiplier))
            {
                AddScalableFloatJobs(jobs, CooldownsPatcher.ShipSummonAssets,
                    cd.ShipSummonMultiplier.Value, "ship-summon");
            }
            if (HasCooldownMultiplier(cd.RangedReloadMultiplier))
            {
                foreach (var kv in RangedReloadPatcher.WeaponAssets)
                {
                    jobs.Add(new CooldownJob
                    {
                        Family = "ranged-reload",
                        AssetStem = kv.Key,
                        VirtualPath = kv.Value,
                        Multiplier = cd.RangedReloadMultiplier.Value,
                        Shape = CooldownJobShape.RangedReload,
                    });
                }
            }
            if (HasCooldownMultiplier(cd.ShipCannonMultiplier))
            {
                foreach (var kv in ShipCannonPatcher.HullAssets)
                {
                    jobs.Add(new CooldownJob
                    {
                        Family = "ship-cannon",
                        AssetStem = kv.Key,
                        VirtualPath = kv.Value,
                        Multiplier = cd.ShipCannonMultiplier.Value,
                        Shape = CooldownJobShape.ShipCannon,
                    });
                }
            }
            return jobs;
        }

        // A multiplier counts as "active" when it's set AND not vanilla.
        // Same 1e-9 epsilon as the other multiplier globals.
        static bool HasCooldownMultiplier(double? m)
        {
            return m.HasValue && Math.Abs(m.Value - 1.0) > 1e-9;
        }

        // Resolves the active ship-music replacement slots. Walks the
        // profile's per-slot dict, validates each entry against the
        // ShipMusicSlots catalog (rejecting tampered profiles with
        // unknown stems), and checks that the audio.wav file actually
        // exists on disk. Missing WAVs are logged and skipped - the
        // slot falls back to vanilla. Empty list = no ship-music
        // source contributes to the IoStore composite.
        List<ShipMusicJob> ResolveShipMusicJobs(Profile profile)
        {
            var jobs = new List<ShipMusicJob>();
            var sm = profile.Globals != null ? profile.Globals.ShipMusic : null;
            if (sm == null || sm.Songs == null || sm.Songs.Count == 0) return jobs;
            foreach (var kv in sm.Songs)
            {
                var stem = kv.Key;
                var ov = kv.Value;
                if (ov == null) continue;
                if (!ShipMusicSlots.ByStem.TryGetValue(stem, out var slot))
                {
                    LogLine("ShipMusic: skipping unknown slot stem '"
                            + stem + "' (not in vanilla catalog)");
                    continue;
                }
                var slotDir = _paths.ProfileShipMusicSlotDir(profile.Id, stem);
                var userWav = Path.Combine(slotDir, "audio.wav");
                if (!File.Exists(userWav))
                {
                    LogLine("ShipMusic: slot '" + stem
                            + "' is configured but its audio.wav is missing in "
                            + slotDir + " - falling back to vanilla.");
                    continue;
                }
                jobs.Add(new ShipMusicJob
                {
                    Slot = slot,
                    UserWavPath = userWav,
                    OriginalFilename = ov.OriginalFilename,
                });
            }
            return jobs;
        }

        static void AddScalableFloatJobs(List<CooldownJob> jobs,
            Dictionary<string, string> assets, double multiplier, string family)
        {
            foreach (var kv in assets)
            {
                jobs.Add(new CooldownJob
                {
                    Family = family,
                    AssetStem = kv.Key,
                    VirtualPath = kv.Value,
                    Multiplier = multiplier,
                    Shape = CooldownJobShape.ScalableFloatDuration,
                });
            }
        }

        static void AddTopLevelMagnitudeJobs(List<CooldownJob> jobs,
            Dictionary<string, string> assets, double multiplier, string family)
        {
            foreach (var kv in assets)
            {
                jobs.Add(new CooldownJob
                {
                    Family = family,
                    AssetStem = kv.Key,
                    VirtualPath = kv.Value,
                    Multiplier = multiplier,
                    Shape = CooldownJobShape.TopLevelMagnitude,
                });
            }
        }

        // Diagnostic helper for the build log.
        static int CountCooldownFamilies(List<CooldownJob> jobs)
        {
            var set = new HashSet<string>(StringComparer.Ordinal);
            foreach (var j in jobs) if (j != null && j.Family != null) set.Add(j.Family);
            return set.Count;
        }

        // Dispatches a single cooldown job to the right patcher and returns
        // a uniform result envelope. Encapsulates the per-shape patcher
        // construction so the source-loop stays compact.
        CooldownJobResult RunCooldownJob(CooldownJob job, string legacyAssetPath, string usmapPath)
        {
            switch (job.Shape)
            {
                case CooldownJobShape.ScalableFloatDuration:
                {
                    var patcher = new CooldownsPatcher { Log = Log };
                    var r = patcher.PatchScalableFloatDuration(
                        legacyAssetPath, legacyAssetPath, usmapPath, job.Multiplier);
                    return new CooldownJobResult
                    {
                        Family = job.Family,
                        AssetStem = r.AssetStem,
                        Multiplier = job.Multiplier,
                        VanillaValue = r.VanillaValue,
                        EffectiveValue = r.EffectiveValue,
                        BatteryCount = 0,
                        PatchedBatteryCount = 0,
                    };
                }
                case CooldownJobShape.TopLevelMagnitude:
                {
                    var patcher = new CooldownsPatcher { Log = Log };
                    var r = patcher.PatchTopLevelMagnitude(
                        legacyAssetPath, legacyAssetPath, usmapPath, job.Multiplier);
                    return new CooldownJobResult
                    {
                        Family = job.Family,
                        AssetStem = r.AssetStem,
                        Multiplier = job.Multiplier,
                        VanillaValue = r.VanillaValue,
                        EffectiveValue = r.EffectiveValue,
                        BatteryCount = 0,
                        PatchedBatteryCount = 0,
                    };
                }
                case CooldownJobShape.RangedReload:
                {
                    var patcher = new RangedReloadPatcher { Log = Log };
                    var r = patcher.Patch(
                        legacyAssetPath, legacyAssetPath, usmapPath, job.Multiplier);
                    return new CooldownJobResult
                    {
                        Family = job.Family,
                        AssetStem = r.AssetStem,
                        Multiplier = job.Multiplier,
                        VanillaValue = r.VanillaReloadTime,
                        EffectiveValue = r.EffectiveReloadTime,
                        BatteryCount = 0,
                        PatchedBatteryCount = 0,
                    };
                }
                case CooldownJobShape.ShipCannon:
                {
                    var patcher = new ShipCannonPatcher { Log = Log };
                    var r = patcher.Patch(
                        legacyAssetPath, legacyAssetPath, usmapPath, job.Multiplier);
                    return new CooldownJobResult
                    {
                        Family = job.Family,
                        AssetStem = r.AssetStem,
                        Multiplier = job.Multiplier,
                        VanillaValue = r.VanillaReloadTime,
                        EffectiveValue = r.EffectiveReloadTime,
                        BatteryCount = r.BatteryCount,
                        PatchedBatteryCount = r.PatchedCount,
                    };
                }
                default:
                    throw new InvalidOperationException(
                        "Unknown CooldownJobShape: " + job.Shape);
            }
        }

        // Returns the list of NoSmoke categories the profile has actively
        // enabled (in declaration order: Campfire, Furnace, Kiln). Empty
        // list means no NoSmoke source contributes to the IoStore composite.
        // Categories with null/false flags are omitted.
        static List<NoSmokeCategory> ResolveNoSmokeCategories(Profile profile)
        {
            var result = new List<NoSmokeCategory>();
            var ns = profile.Globals != null ? profile.Globals.NoSmoke : null;
            if (ns == null) return result;
            if (ns.Campfire.GetValueOrDefault(false)) result.Add(NoSmokeCategory.Campfire);
            if (ns.Furnace.GetValueOrDefault(false))  result.Add(NoSmokeCategory.Furnace);
            if (ns.Kiln.GetValueOrDefault(false))     result.Add(NoSmokeCategory.Kiln);
            return result;
        }

        // True when the profile actually configures the loot domain,
        // either via a per-bucket multiplier or per-LT override. Lets the
        // pipeline skip the loot patch step entirely for stack-only
        // profiles.
        static bool HasLootConfiguration(Profile profile)
        {
            if (profile.LootOverrides != null && profile.LootOverrides.Count > 0) return true;
            var loot = profile.Globals != null ? profile.Globals.Loot : null;
            if (loot == null || loot.ByCategory == null) return false;
            foreach (var kv in loot.ByCategory)
            {
                if (kv.Value != 1.0) return true;
            }
            return false;
        }

        // True when the profile defines at least one CustomItem with a
        // non-empty Id + TemplateId. Lets the pipeline skip the patcher
        // entirely for profiles that haven't touched the Item Creator
        // tab (the common case).
        static bool HasCustomItemsConfiguration(Profile profile)
        {
            var customs = profile.CustomItems;
            if (customs == null || customs.Count == 0) return false;
            foreach (var c in customs)
            {
                if (c == null) continue;
                if (!string.IsNullOrWhiteSpace(c.Id) && !string.IsNullOrWhiteSpace(c.TemplateId))
                    return true;
            }
            return false;
        }

        // True when the profile has any buyer (PlayerSells) trade-list edit
        // - either a recipe override (vanilla edit or synthesized custom) or
        // a per-list add/remove of recipe refs. Lets the pipeline skip the
        // BuyerPatcher entirely for stack/loot-only profiles.
        static bool HasBuyerConfiguration(Profile profile)
        {
            if (profile.BuyerRecipes != null && profile.BuyerRecipes.Count > 0) return true;
            if (profile.BuyerLists != null)
            {
                foreach (var kv in profile.BuyerLists)
                {
                    var v = kv.Value;
                    if (v == null) continue;
                    if (v.AddedRecipeIds != null && v.AddedRecipeIds.Count > 0) return true;
                    if (v.RemovedRecipeIds != null && v.RemovedRecipeIds.Count > 0) return true;
                }
            }
            return false;
        }

        // Same test for the seller side (PlayerBuys lists).
        static bool HasSellerConfiguration(Profile profile)
        {
            if (profile.SellerRecipes != null && profile.SellerRecipes.Count > 0) return true;
            if (profile.SellerLists != null)
            {
                foreach (var kv in profile.SellerLists)
                {
                    var v = kv.Value;
                    if (v == null) continue;
                    if (v.AddedRecipeIds != null && v.AddedRecipeIds.Count > 0) return true;
                    if (v.RemovedRecipeIds != null && v.RemovedRecipeIds.Count > 0) return true;
                }
            }
            return false;
        }

        // True when the profile asks for at least one bell-or-signal-fire
        // cap that differs from vanilla. Lets the pipeline skip the patch
        // step (and its file-existence check on VanillaBuildingLimits)
        // for the common case where no bell config is set.
        static bool HasBellLimitsConfiguration(Profile profile)
        {
            var b = profile.Globals != null ? profile.Globals.FastTravelBells : null;
            if (b == null) return false;
            if (b.BellCap.HasValue && b.BellCap.Value != BellLimitsPatcher.VanillaBellCap)
                return true;
            if (b.SignalFireCap.HasValue && b.SignalFireCap.Value != BellLimitsPatcher.VanillaSignalFireCap)
                return true;
            return false;
        }

        // Profile.Name -> filename component for "Quartermaster_<name>_P.pak".
        // Stay strict (alnum + dash + underscore) so the pak filename works
        // on every Windows / Linux server config the user might drop it on.
        // Spaces collapse to dashes; other chars are dropped.
        public static string SanitizeForFileName(string name)
        {
            if (string.IsNullOrEmpty(name)) return "Untitled";
            var sb = new StringBuilder(name.Length);
            foreach (var c in name)
            {
                if (char.IsLetterOrDigit(c) || c == '-' || c == '_') sb.Append(c);
                else if (c == ' ') sb.Append('-');
            }
            // Collapse runs of dashes / underscores to a single one for tidiness.
            var raw = sb.ToString();
            if (string.IsNullOrEmpty(raw)) return "Untitled";
            var collapsed = new StringBuilder(raw.Length);
            char prev = '\0';
            foreach (var c in raw)
            {
                if ((c == '-' || c == '_') && c == prev) continue;
                collapsed.Append(c);
                prev = c;
            }
            return collapsed.ToString().Trim('-', '_');
        }

        void LogLine(string msg)
        {
            if (Log != null) Log(msg);
        }
    }

    public sealed class BuildPipelineResult
    {
        public Profile Profile;
        public PatchResult PatchResult;
        public LootPatchResult LootPatchResult;   // null if profile has no loot config
        public BellLimitsPatchResult BellLimitsResult; // null if profile has no bell config
        public BuyerPatchResult BuyerPatchResult; // null if profile has no buyer (PlayerSells) edits
        public SellerPatchResult SellerPatchResult; // null if profile has no seller (PlayerBuys) edits
        // Item Creator (custom items synthesized from vanilla templates).
        // null when the profile has no CustomItems. When non-null,
        // ItemsWritten counts the new JSONs that landed under the
        // Custom/ subfolder; CsvRowsAppended counts the (ItemName +
        // ItemDescription) string-table rows appended to the modded
        // InventoryItems.csv.
        public ItemCreatorPatchResult ItemCreatorResult;
        public PakBuildResult PakResult;          // null if pickup-only build (no item/loot changes)
        public string PakPath;                    // null if pickup-only build
        // The freshly built pickup-radius IoStore triplet, or null if the
        // profile didn't request a pickup mod (or set multiplier == 1.0).
        public PickupTripletResult PickupResult;
        // The user-facing scalar that produced the triplet (e.g. 2.0,
        // 1.5, ...). null when no pickup triplet was built.
        public double? PickupMultiplier;
        // Building-stability inclusion result. null when the profile
        // didn't enable the toggle. When non-null, the .ucas/.utoc
        // already shipped under PickupResult's paths (or the standalone
        // shared basename if pickup was off and stability was the only
        // IoStore source).
        public BuildingStabilityResult StabilityResult;
        // NoSmoke inclusion result. null when no NoSmoke category was
        // active. When non-null, lists which categories were enabled and
        // per-asset patch counts (handles flipped from enabled to
        // disabled). The .ucas/.utoc payload is part of the same shared
        // IoStore triplet as Pickup / Stability.
        public NoSmokeResult NoSmokeResult;
        // Minimap-range inclusion result. null when the profile didn't
        // configure a multiplier or set it to 1.0 (vanilla). When non-
        // null, carries the effective scaled values and the .pak the
        // ini-patch was shipped in (shared with stability's pak when
        // both features are active).
        public MinimapRangeResult MinimapResult;
        // Bonfire / building-center influence-radius inclusion result.
        // null when the profile didn't configure a multiplier or set it
        // to 1.0 (vanilla). When non-null, the patched DA_BI_Utilities_
        // BuildingCenterT01 is part of the same shared IoStore triplet
        // as Pickup / NoSmoke (sharedBaseName.ucas/utoc).
        public BonfireRadiusResult BonfireResult;
        // Pickaxe-range inclusion result. null when the profile didn't
        // configure a multiplier or set it to 1.0 (vanilla). When non-
        // null, carries one PickaxeRangePatchResult per pickaxe tier
        // patched (currently 4) plus the published triplet paths the
        // patched DataAssets ride inside (sharedBaseName.ucas/utoc).
        public PickaxeRangeResult PickaxeRangeResult;
        // Cooldown patches inclusion result. null when no cooldown family
        // was activated (every multiplier null or 1.0). When non-null,
        // carries one CooldownJobResult per patched asset (1..N per
        // activated family) plus the shared triplet paths.
        public CooldownsResult CooldownsResult;
        // Ship-music slot replacement result. null when the profile didn't
        // configure any custom shanties (or all configured slots were
        // missing their on-disk triplets). When non-null, carries one
        // ShipMusicPatchResult per replaced slot plus the shared triplet
        // paths.
        public ShipMusicResult ShipMusicResult;
        // Crop-growth patch inclusion result. null when the profile didn't
        // configure a CropGrowthMultiplier or set it to 1.0 (vanilla). When
        // non-null, carries the per-crop ticks before/after for the build
        // log. The patched DA_Crop_*.json files ride in the main pak
        // alongside the other JSON-patcher output.
        public CropGrowthPatchResult CropGrowthResult;
        // Recipe cooking-duration patch result. null when no family
        // multiplier was set (or all are 1.0). When non-null, carries
        // the per-recipe patches grouped by family - lets the build
        // response render "DONE - smelting: 0.5x; 10 recipes; ~4200 -> ~2100"
        // style lines.
        public CookingDurationPatchResult CookingDurationResult;
        public string TmpDir;
        public bool Success;
    }

    // Standalone summary of "stability got included in this build". Kept
    // separate from PickupTripletResult so the caller can report the two
    // features independently in the response payload.
    //
    // AssetResults is populated with one entry per DA_BI_*.uasset the
    // patcher saw. Patched=true entries are the assets whose
    // IntegritySettings floats were overwritten; Patched=false entries
    // are skipped assets (no IntegritySettings property, etc.). The
    // single-toggle UI only cares about Enabled, but downstream callers
    // (build response / log) can roll up the counts.
    public sealed class BuildingStabilityResult
    {
        public bool Enabled;
        public List<BuildingStabilityAssetResult> AssetResults;
        // Paths to the published stability companion triplet under outDir.
        // The stability triplet always ships as a full triplet (pak + ucas
        // + utoc), independent of the main pak / composite triplet.
        public string PakPath;
        public string UcasPath;
        public string UtocPath;
        public long PakSize;
        public long UcasSize;
        public long UtocSize;
    }

    // Standalone summary of "no-smoke patches got included in this build".
    // Carries the active categories plus per-asset patch counts so the
    // build response can attribute totals back to the user-visible
    // toggles ("3 campfires patched, 11+8 emitter handles silenced", ...).
    public sealed class NoSmokeResult
    {
        public List<NoSmokeCategory> Categories;
        public List<NoSmokeAssetResult> AssetResults;
    }

    public sealed class NoSmokeAssetResult
    {
        // Virtual content path of the patched asset (e.g.
        // "R5/Content/FX/Particles/Environment/Fire/FX_Bonefire_Center.uasset").
        public string AssetPath;
        // Total EmitterHandles encountered on the asset's NiagaraSystem.
        public int TotalHandles;
        // Subset of TotalHandles that had bIsEnabled flipped from true to
        // false. Handles already at false are not counted.
        public int FlippedHandles;
    }

    // Standalone summary of "minimap-range got included in this build".
    // The .pak path here is the raw-companion .pak (shared with stability
    // when both are active); the loose ini lives at
    // R5/Config/DefaultR5MapSettings.ini inside that pak. Vanilla baseline
    // values + effective scaled values surface in the Patch member so
    // callers can render "37 -> 74" style summaries without recomputing.
    public sealed class MinimapRangeResult
    {
        public bool Enabled;
        public double Multiplier;
        public MinimapRangePatchResult Patch;
        public string PakPath;
        public long PakSize;
    }

    // Standalone summary of "bonfire-radius got included in this build".
    // The patched DataAsset rides inside the SAME IoStore triplet as
    // Pickup / NoSmoke (sharedBaseName.ucas/utoc), so PakPath here is
    // null when a main Pak1 is also being built (it would be redundant
    // with the main pak path) and points to the stub .pak otherwise.
    // Patch carries the vanilla + effective influence values for the
    // build-response log line.
    public sealed class BonfireRadiusResult
    {
        public bool Enabled;
        public double Multiplier;
        public BonfireRadiusPatchResult Patch;
        public string PakPath;
        public string UcasPath;
        public string UtocPath;
    }

    // Standalone summary of "pickaxe-range got included in this build".
    // The patched InstanceParams DataAssets ride inside the SAME IoStore
    // triplet as Pickup / Bonfire / NoSmoke (sharedBaseName.ucas/utoc), so
    // PakPath here mirrors Bonfire's semantics (null when a main Pak1 is
    // also being built, stub .pak otherwise).
    //
    // AssetResults holds one entry per pickaxe tier (4 in 5.6) with each
    // tier's vanilla + effective TraceScaleModifier - lets the build log
    // render a "T00:1.00->1.40, T01:1.00->1.40, ..." line per tier.
    public sealed class PickaxeRangeResult
    {
        public bool Enabled;
        public double Multiplier;
        public List<PickaxeRangePatchResult> AssetResults;
        public string PakPath;
        public string UcasPath;
        public string UtocPath;
    }

    // Discriminator for cooldown jobs - tells the dispatcher which patcher
    // to invoke for a given asset. Each shape corresponds to one of the
    // three patcher classes (CooldownsPatcher with two methods, plus
    // RangedReloadPatcher and ShipCannonPatcher).
    public enum CooldownJobShape
    {
        ScalableFloatDuration,
        TopLevelMagnitude,
        RangedReload,
        ShipCannon,
    }

    // A single cooldown patch to apply: which asset, which multiplier,
    // which property shape. ResolveCooldownJobs() fans the 8 family
    // multipliers out into 1..N of these per family.
    public sealed class CooldownJob
    {
        // Human-readable family id ("elixir", "medicine", "ranged-reload",
        // "ship-cannon", ...). Used to group per-asset results back into
        // family-level summaries for the build response.
        public string Family;
        public string AssetStem;
        public string VirtualPath;
        public double Multiplier;
        public CooldownJobShape Shape;
    }

    // Per-asset cooldown patch outcome. Uniform envelope across all four
    // patch shapes so the build response can render a single table without
    // branching on Shape. BatteryCount/PatchedBatteryCount carry extra
    // diagnostic for ShipCannon (zero for the other shapes).
    public sealed class CooldownJobResult
    {
        public string Family;
        public string AssetStem;
        public double Multiplier;
        public float VanillaValue;
        public float EffectiveValue;
        public int BatteryCount;
        public int PatchedBatteryCount;
    }

    // Standalone summary of "cooldown patches got included in this build".
    // The patched DataAssets ride inside the SAME IoStore triplet as
    // Pickup / Bonfire / Pickaxe / NoSmoke (sharedBaseName.ucas/utoc), so
    // PakPath mirrors the same semantics as the other composite results
    // (null when a main Pak1 is also being built, stub .pak otherwise).
    public sealed class CooldownsResult
    {
        public bool Enabled;
        public List<CooldownJobResult> JobResults;
        public string PakPath;
        public string UcasPath;
        public string UtocPath;
    }

    // One scheduled ship-music slot replacement. The pipeline's
    // ResolveShipMusicJobs() fans the profile's Songs dict out into
    // one of these per replaced shanty, each pointing at the on-disk
    // user .wav. The patcher runs binkaudioenc.exe on it and splices
    // the resulting Bink Audio bytes into a fresh copy of the
    // SoundWave_BinkInline template.
    public sealed class ShipMusicJob
    {
        public ShipMusicSlots.SlotInfo Slot;
        public string UserWavPath;
        public string OriginalFilename;
    }

    // Standalone summary of "ship-music slots got replaced in this
    // build". The patched SoundWaves ride inside the SAME IoStore
    // triplet as Pickup / Bonfire / Pickaxe / NoSmoke / Cooldowns
    // (sharedBaseName.ucas/utoc), so PakPath mirrors the same
    // semantics as the other composite results (null when a main
    // Pak1 is also being built, stub .pak otherwise).
    public sealed class ShipMusicResult
    {
        public bool Enabled;
        public List<ShipMusicPatchResult> SlotResults;
        public string PakPath;
        public string UcasPath;
        public string UtocPath;
    }
}
