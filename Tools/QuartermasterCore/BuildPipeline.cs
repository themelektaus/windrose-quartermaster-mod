using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Windrose.Quartermaster.Core.BuildingCreator;
using Windrose.Quartermaster.Core.Deploy;

namespace Windrose.Quartermaster.Core
{
    public sealed partial class BuildPipeline
    {
        readonly WindrosePaths _paths;
        readonly StackPatcher _patcher;
        readonly LootPatcher _lootPatcher;
        readonly BellLimitsPatcher _bellPatcher;
        readonly InventorySlotsPatcher _invSlotsPatcher;
        readonly ShipSlotsPatcher _shipSlotsPatcher;
        readonly BuyerPatcher _buyerPatcher;
        readonly SellerPatcher _sellerPatcher;
        readonly ItemCreatorPatcher _itemCreatorPatcher;
        readonly CropGrowthPatcher _cropGrowthPatcher;
        readonly CookingDurationPatcher _cookingDurationPatcher;
        readonly NpcSpawnPatcher _npcSpawnPatcher;
        readonly RepakResolver _repakResolver;
        readonly RetocResolver _retocResolver;
        readonly BuildingPatcher _buildingPatcher;
        readonly RecipePatcher _recipePatcher;
        readonly BlueprintPatcher _blueprintPatcher;
        readonly BuildingAudioStager _audioStager;

        public Action<string> Log;

        public string OutputDir;

        public Func<string> GamePaksDirProvider;

        public VanillaBuildingTemplateCatalog BuildingTemplateCatalog;

        public const string StabilityContainerFilename = "pakchunk0_s3-Windows.utoc";

        public const string RawCompanionSuffix = "_Raw_P";

        public const float VanillaMagnetRadius = 400f;

        public BuildPipeline(WindrosePaths paths)
        {
            if (paths == null) throw new ArgumentNullException("paths");
            _paths = paths;
            _patcher = new StackPatcher();
            _lootPatcher = new LootPatcher();
            _bellPatcher = new BellLimitsPatcher();
            _invSlotsPatcher = new InventorySlotsPatcher();
            _shipSlotsPatcher = new ShipSlotsPatcher();
            _buyerPatcher = new BuyerPatcher();
            _sellerPatcher = new SellerPatcher();
            _itemCreatorPatcher = new ItemCreatorPatcher();
            _cropGrowthPatcher = new CropGrowthPatcher();
            _cookingDurationPatcher = new CookingDurationPatcher();
            _npcSpawnPatcher = new NpcSpawnPatcher();
            _repakResolver = new RepakResolver(paths.ModRoot);
            _retocResolver = new RetocResolver(paths.ModRoot);
            _buildingPatcher = new BuildingPatcher();
            _recipePatcher = new RecipePatcher();
            _blueprintPatcher = new BlueprintPatcher();
            _audioStager = new BuildingAudioStager();
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
            var sharedBaseName = "Quartermaster_" + safeName + "_P";
            var sharedUcasPath = Path.Combine(outDir, sharedBaseName + ".ucas");
            var sharedUtocPath = Path.Combine(outDir, sharedBaseName + ".utoc");

            var rawBaseName = "Quartermaster_" + safeName + RawCompanionSuffix;
            var rawPakPath  = Path.Combine(outDir, rawBaseName + ".pak");
            var rawUcasPath = Path.Combine(outDir, rawBaseName + ".ucas");
            var rawUtocPath = Path.Combine(outDir, rawBaseName + ".utoc");

            var tmpDir = Path.Combine(_paths.BuildTmp, profile.Id);

            try
            {
                if (Directory.Exists(tmpDir)) Directory.Delete(tmpDir, true);

                // Pre-clear stale outputs so disabling a feature doesn't leave
                // its old IoStore companions to be mounted by the engine.
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

                InventorySlotsPatchResult invSlotsResult = null;
                if (HasEquipmentSlotsConfiguration(profile))
                {
                    LogLine("Patching player inventory slots (ring / necklace)");
                    var eq = profile.Globals.EquipmentSlots;
                    invSlotsResult = _invSlotsPatcher.PatchToDirectory(
                        _paths.VanillaPlayerInventory, tmpDir,
                        eq.RingSlots, eq.NecklaceSlots);
                    if (invSlotsResult.Skipped)
                    {
                        LogLine("  skipped (resolved counts match vanilla 1/1 - nothing to do)");
                    }
                    else if (invSlotsResult.Written)
                    {
                        LogLine("  ring " + invSlotsResult.RingSlots + " (vanilla 1), necklace "
                                + invSlotsResult.NecklaceSlots + " (vanilla 1) - "
                                + (invSlotsResult.RingPatched ? "ring " : "")
                                + (invSlotsResult.NecklacePatched ? "necklace " : "")
                                + "patched. NOTE: only affects NEW characters; existing "
                                + "characters need the save patcher.");
                    }
                }

                ShipSlotsPatchResult shipSlotsResult = null;
                if (HasShipSlotsConfiguration(profile))
                {
                    LogLine("Patching ship inventory slots (cargo / combat orders)");
                    var sh = profile.Globals.ShipSlots;
                    shipSlotsResult = _shipSlotsPatcher.PatchToDirectory(
                        _paths.VanillaShipInventory, tmpDir,
                        sh.CargoMultiplier, sh.CombatOrderSlots);
                    if (shipSlotsResult.Skipped)
                    {
                        LogLine("  skipped (resolved values match vanilla - nothing to do)");
                    }
                    else if (shipSlotsResult.Written)
                    {
                        LogLine("  cargo x" + shipSlotsResult.CargoMultiplier
                                + ", combat orders " + shipSlotsResult.CombatOrderSlots
                                + " - " + shipSlotsResult.FilesWritten + " ship file(s) patched. "
                                + "NOTE: only affects NEW ships; existing ships need the save patcher.");
                    }
                }

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

                // CookingDuration must run after Buyer/Seller to merge its edit
                // into a recipe file the trade patchers may already have touched.
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

                NpcSpawnPatchResult npcSpawnResult = null;
                if (HasNpcSpawnConfiguration(profile))
                {
                    var tmpSpawnDir = Path.Combine(tmpDir, "R5", "Content",
                        "Gameplay", "Actor", "SpawnPoints", "A2_Spawners");
                    LogLine("Patching NPC spawners -> " + tmpSpawnDir);
                    npcSpawnResult = _npcSpawnPatcher.PatchToDirectory(
                        _paths.VanillaAiSpawners, tmpSpawnDir, profile);
                    LogLine("Patched NPC spawners: " + npcSpawnResult.Written
                            + " written (" + npcSpawnResult.RespawnChanged + " respawn, "
                            + npcSpawnResult.CountChanged + " amount blocks, "
                            + npcSpawnResult.OverriddenFiles + " per-spawner override(s); "
                            + npcSpawnResult.Scanned + " scanned)");
                    foreach (var w in npcSpawnResult.Warnings) LogLine("  warn: " + w);
                }

                // Resolve bake jobs before the item-creator patcher: it needs to
                // know whether each item's ItemTexture points at a baked icon.
                var iconBakeJobs = ResolveIconBakeJobs(profile);
                var bakeableItemIds = new HashSet<string>(StringComparer.Ordinal);
                foreach (var j in iconBakeJobs) bakeableItemIds.Add(j.ItemId);

                ItemCreatorPatchResult itemCreatorResult = null;
                if (HasCustomItemsConfiguration(profile))
                {
                    LogLine("Synthesizing custom items");
                    itemCreatorResult = _itemCreatorPatcher.PatchToDirectory(
                        _paths.VanillaInventoryItems, _paths.VanillaInventoryItemsCsv,
                        tmpDir, profile, bakeableItemIds);
                    LogLine("Custom items: " + itemCreatorResult.ItemsWritten
                            + " JSON(s) written (display text ships inline as plain-string FText)");
                    foreach (var w in itemCreatorResult.Warnings) LogLine("  warn: " + w);
                }

                // Building assets MUST end up in the IoStore composite triplet,
                // not the legacy .pak: new asset paths are only resolvable via
                // the IoStore global index. The patcher call is deferred into
                // BuildIoStoreComposite as a pre-staged source.
                List<BuildingPatchResult> buildingResults = null;
                bool buildingsActive = HasCustomBuildingsConfiguration(profile);

                int totalWritten = patchResult.Written
                    + (lootResult != null ? lootResult.Written : 0)
                    + (bellResult != null && bellResult.Written ? 1 : 0)
                    + (invSlotsResult != null && invSlotsResult.Written ? 1 : 0)
                    + (shipSlotsResult != null && shipSlotsResult.Written ? shipSlotsResult.FilesWritten : 0)
                    + (buyerResult != null
                        ? buyerResult.RecipesEdited + buyerResult.RecipesAdded + buyerResult.ListsWritten
                        : 0)
                    + (sellerResult != null
                        ? sellerResult.RecipesEdited + sellerResult.RecipesAdded + sellerResult.ListsWritten
                        : 0)
                    + (itemCreatorResult != null
                        ? itemCreatorResult.ItemsWritten
                        : 0)
                    + (cropGrowthResult != null ? cropGrowthResult.Written : 0)
                    + (cookingDurationResult != null ? cookingDurationResult.Written : 0)
                    + (npcSpawnResult != null ? npcSpawnResult.Written : 0)
                    + CountBuildableBuildings(profile);
                double pickupMultiplier = ResolvePickupMultiplier(profile);
                bool pickupActive = pickupMultiplier > 0.0 && Math.Abs(pickupMultiplier - 1.0) > 1e-9;
                bool stabilityActive = ResolveStabilityEnabled(profile);
                var noSmokeCategories = ResolveNoSmokeCategories(profile);
                bool noSmokeActive = noSmokeCategories.Count > 0;
                double minimapMultiplier = ResolveMinimapMultiplier(profile);
                bool minimapActive = minimapMultiplier > 0.0 && Math.Abs(minimapMultiplier - 1.0) > 1e-9;
                bool noFogActive = ResolveNoFogEnabled(profile);
                bool persistentLootActive = ResolvePersistentLootEnabled(profile);
                bool keepStatusActive = ResolveKeepStatusEnabled(profile);
                bool landFastTravelActive = ResolveLandFastTravelEnabled(profile);
                double bonfireMultiplier = ResolveBonfireMultiplier(profile);
                bool bonfireActive = bonfireMultiplier > 0.0 && Math.Abs(bonfireMultiplier - 1.0) > 1e-9;
                double pickaxeMultiplier = ResolvePickaxeRangeMultiplier(profile);
                bool pickaxeActive = pickaxeMultiplier > 0.0 && Math.Abs(pickaxeMultiplier - 1.0) > 1e-9;
                var cooldownJobs = ResolveCooldownJobs(profile);
                bool cooldownsActive = cooldownJobs.Count > 0;
                var shipMusicJobs = ResolveShipMusicJobs(profile);
                bool shipMusicActive = shipMusicJobs.Count > 0;
                var bonfireMusicJob = ResolveBonfireMusicJob(profile);
                bool bonfireMusicActive = bonfireMusicJob != null;
                var shipMusicAddJobs = ResolveShipMusicAddJobs(profile);
                bool shipMusicAddActive = shipMusicAddJobs.Count > 0;
                var shipMusicExcludedIndices = ResolveShipMusicExcludedIndices(profile);
                bool shipMusicExcludesActive = shipMusicExcludedIndices.Count > 0;
                bool shipMusicDaActive = shipMusicAddActive || shipMusicExcludesActive;
                var lightingJobs = ResolveLightingJobs(profile);
                bool lightingActive = lightingJobs.Count > 0;
                var shipSpeedJobs = ResolveShipSpeedJobs(profile);
                bool shipSpeedActive = shipSpeedJobs.Count > 0;
                bool iconsActive = iconBakeJobs.Count > 0;
                bool ioStoreActive = pickupActive || stabilityActive || noSmokeActive || minimapActive || noFogActive || persistentLootActive || keepStatusActive || landFastTravelActive || bonfireActive || pickaxeActive || cooldownsActive || shipMusicActive || shipMusicDaActive || bonfireMusicActive || lightingActive || shipSpeedActive || iconsActive || buildingsActive;
                if (totalWritten == 0 && !ioStoreActive)
                {
                    // Surface which fields are missing when all buildings were
                    // skeleton-filtered, instead of a generic "no changes".
                    var skeleton = DescribeSkeletonBuildings(profile);
                    if (skeleton != null)
                    {
                        throw new InvalidOperationException(
                            "Profile has custom building(s) but required field(s) are empty:\n"
                            + skeleton
                            + "\nFill the missing field(s) in the Buildings tab and Save, then Build again.");
                    }
                    throw new InvalidOperationException("Profile produces no changes - nothing to pack.");
                }

                // Build the IoStore composite first, so a later repak overwrites
                // retoc's .pak stub with the real Pak1 content.
                PickupTripletResult pickupResult = null;
                NoSmokeResult noSmokeResult = null;
                BonfireRadiusResult bonfireResult = null;
                LandFastTravelResult landFastTravelResult = null;
                NoFogResult noFogResult = null;
                PersistentLootResult persistentLootResult = null;
                KeepStatusResult keepStatusResult = null;
                PickaxeRangeResult pickaxeResult = null;
                CooldownsResult cooldownsResult = null;
                ShipMusicResult shipMusicResult = null;
                ShipMusicAddResult shipMusicAddResult = null;
                BonfireMusicResult bonfireMusicResult = null;
                LightingResult lightingResult = null;
                ShipSpeedResult shipSpeedResult = null;
                List<IconBakerPatcher.BakeResult> iconBakeResults = null;
                bool compositeActive = pickupActive || noSmokeActive || bonfireActive || landFastTravelActive || noFogActive || persistentLootActive || keepStatusActive || pickaxeActive || cooldownsActive || shipMusicActive || shipMusicDaActive || bonfireMusicActive || lightingActive || shipSpeedActive || iconsActive || buildingsActive;
                if (compositeActive)
                {
                    var compositeResult = BuildIoStoreComposite(
                        profile, outDir, pickupMultiplier, pickupActive,
                        noSmokeCategories,
                        bonfireMultiplier, bonfireActive,
                        landFastTravelActive,
                        noFogActive,
                        persistentLootActive,
                        keepStatusActive,
                        pickaxeMultiplier, pickaxeActive,
                        cooldownJobs,
                        shipMusicJobs,
                        shipMusicAddJobs,
                        shipMusicExcludedIndices,
                        bonfireMusicJob,
                        lightingJobs,
                        shipSpeedJobs,
                        iconBakeJobs,
                        buildingsActive,
                        sharedBaseName, mainPakWillBeBuilt: totalWritten > 0);
                    pickupResult = compositeResult.Pickup;
                    noSmokeResult = compositeResult.NoSmoke;
                    bonfireResult = compositeResult.Bonfire;
                    landFastTravelResult = compositeResult.LandFastTravel;
                    noFogResult = compositeResult.NoFog;
                    persistentLootResult = compositeResult.PersistentLoot;
                    keepStatusResult = compositeResult.KeepStatus;
                    pickaxeResult = compositeResult.PickaxeRange;
                    cooldownsResult = compositeResult.Cooldowns;
                    shipMusicResult = compositeResult.ShipMusic;
                    shipMusicAddResult = compositeResult.ShipMusicAdd;
                    bonfireMusicResult = compositeResult.BonfireMusic;
                    lightingResult = compositeResult.Lighting;
                    shipSpeedResult = compositeResult.ShipSpeed;
                    iconBakeResults = compositeResult.Icons;
                    buildingResults = compositeResult.Buildings;
                }

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
                // totalWritten is only predictive; verify real files exist
                // before repak (it refuses to pack an empty source folder).
                bool tmpHasFiles = totalWritten > 0
                    && Directory.Exists(tmpDir)
                    && Directory.EnumerateFiles(tmpDir, "*", SearchOption.AllDirectories).Any();
                if (tmpHasFiles)
                {
                    LogLine("Resolving repak.exe...");
                    _repakResolver.Log = Log;
                    var repakExe = _repakResolver.Resolve();

                    LogLine("Packing -> " + outPakPath);
                    Directory.CreateDirectory(outDir);
                    var builder = new PakBuilder(repakExe);
                    builder.Log = Log;
                    pakResult = builder.Build(tmpDir, outPakPath, overwrite: true);
                    pakPath = outPakPath;

                    LogLine("Pak built: " + outPakPath
                            + " (" + Math.Round(pakResult.SizeBytes / 1024.0, 1) + " KB, "
                            + pakResult.FileCount + " files)");
                }
                else if (totalWritten > 0)
                {
                    LogLine("No legacy-pak content produced (predictive counters were optimistic) - main pak skipped.");
                }
                else if (!ioStoreActive)
                {
                    LogLine("No item / loot changes - main pak skipped (IoStore-only build).");
                }

                // Only touch the game folder when the buildings feature was
                // used - never inject the DLL for a stack/loot-only profile.
                int buildingsCount = buildingResults != null ? buildingResults.Count : 0;
                if (buildingsCount > 0)
                {
                    LogLine("Deploying DLL + qm_items_" + safeName + ".json to game Binaries/Win64");
                    var deployer = new GameDeployer(_paths.ModRoot);
                    deployer.Log = Log;
                    // EnsureDllInstalled returns false on non-Windows; skip the
                    // JSON write too so no orphaned config is left in Win64.
                    if (deployer.EnsureDllInstalled())
                    {
                        deployer.WriteItemsJson(safeName, buildingResults);
                    }
                }
                else
                {
                    // No buildings now: if the DLL was previously deployed,
                    // delete this profile's JSON so it stops injecting stale
                    // items. Never touch the game folder if no DLL exists.
                    try
                    {
                        var deployer = new GameDeployer(_paths.ModRoot);
                        deployer.Log = Log;
                        if (File.Exists(deployer.TargetDllPath()))
                        {
                            // Empty list deletes the per-profile JSON.
                            deployer.WriteItemsJson(safeName, new List<BuildingPatchResult>());
                            deployer.RemoveDllIfNoProfilesLeft();
                        }
                    }
                    catch (Exception ex)
                    {
                        LogLine("Warning: skipped per-profile JSON cleanup (game folder lookup failed): " + ex.Message);
                    }
                }

                return new BuildPipelineResult
                {
                    Profile = profile,
                    PatchResult = patchResult,
                    LootPatchResult = lootResult,
                    BellLimitsResult = bellResult,
                    EquipmentSlotsResult = invSlotsResult,
                    ShipSlotsResult = shipSlotsResult,
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
                    NoFogResult = noFogResult,
                    PersistentLootResult = persistentLootResult,
                    KeepStatusResult = keepStatusResult,
                    LandFastTravelResult = landFastTravelResult,
                    BonfireResult = bonfireResult,
                    PickaxeRangeResult = pickaxeResult,
                    CooldownsResult = cooldownsResult,
                    ShipMusicResult = shipMusicResult,
                    ShipMusicAddResult = shipMusicAddResult,
                    BonfireMusicResult = bonfireMusicResult,
                    LightingResult = lightingResult,
                    ShipSpeedResult = shipSpeedResult,
                    CropGrowthResult = cropGrowthResult,
                    CookingDurationResult = cookingDurationResult,
                    NpcSpawnResult = npcSpawnResult,
                    BuildingResults = buildingResults,
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
                        Path.Combine(_paths.BuildTmp, profile.Id + "__buildings"),
                    })
                    {
                        if (!Directory.Exists(dir)) continue;
                        try { Directory.Delete(dir, true); }
                        catch (Exception ex)
                        {
                            LogLine("Warning: temp dir cleanup failed for " + dir + ": " + ex.Message);
                        }
                    }
                }
            }
        }

        BuildIoStoreCompositeOutput BuildIoStoreComposite(
            Profile profile, string outDir,
            double pickupMultiplier, bool pickupActive,
            List<NoSmokeCategory> noSmokeCategories,
            double bonfireMultiplier, bool bonfireActive,
            bool landFastTravelActive,
            bool noFogActive,
            bool persistentLootActive,
            bool keepStatusActive,
            double pickaxeMultiplier, bool pickaxeActive,
            List<CooldownJob> cooldownJobs,
            List<ShipMusicJob> shipMusicJobs,
            List<ShipMusicAddJob> shipMusicAddJobs,
            IReadOnlyCollection<int> shipMusicExcludedIndices,
            BonfireMusicJob bonfireMusicJob,
            List<LightingJob> lightingJobs,
            List<ShipSpeedJob> shipSpeedJobs,
            List<IconBakerPatcher.BakeJob> iconBakeJobs,
            bool buildingsActive,
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

            // Sibling of tmpDir so repak's recursive scan can't sweep retoc
            // artefacts into the main pak.
            var iostoreRoot = Path.Combine(_paths.BuildTmp, profile.Id + "__iostore");
            if (Directory.Exists(iostoreRoot)) Directory.Delete(iostoreRoot, true);
            Directory.CreateDirectory(iostoreRoot);
            var stagingBase = Path.Combine(iostoreRoot, "out", sharedBaseName);
            var legacyTmp = Path.Combine(iostoreRoot, "legacy");

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

            LandFastTravelPatchResult landFastTravelPatch = null;
            if (landFastTravelActive)
            {
                var usmapPath = UsmapLocator.Find(_paths.ModRoot);
                LogLine("LandFastTravel source: vanilla fast-travel bells -> widen "
                        + "CoastlineDistanceRange to [-1e6,+1e6] (" + LandFastTravelPatcher.AssetFilterStems.Length
                        + " DataAssets, derived from vanilla)");
                sources.Add(new IoStoreCompositeSource
                {
                    Name = "land-fast-travel",
                    InputDir = gamePaksDir,
                    Filters = new List<string>(LandFastTravelPatcher.AssetFilterStems),
                    // Extract the vanilla bells (version sanity gate) then inject/widen
                    // the CoastlineDistanceRange property in place before the shared to-zen.
                    AfterExtract = stagingDir =>
                    {
                        var patcher = new LandFastTravelPatcher { Log = Log };
                        landFastTravelPatch = patcher.Patch(stagingDir, usmapPath);
                    },
                });
            }

            NoFogPatchResult noFogPatch = null;
            if (noFogActive)
            {
                var usmapPath = UsmapLocator.Find(_paths.ModRoot);
                LogLine("NoFog source: vanilla M_Map -> repoint fog RenderTarget import "
                        + "RT_MapFog -> RT_MapFog_d (derived from vanilla)");
                sources.Add(new IoStoreCompositeSource
                {
                    Name = "no-fog",
                    InputDir = gamePaksDir,
                    Filter = NoFogPatcher.AssetFilterStem,
                    // Extract vanilla M_Map (version sanity gate) then rename the fog
                    // RenderTarget import in place and drop the filter's collateral
                    // (e.g. M_MapFog_Brush) before the shared to-zen.
                    AfterExtract = stagingDir =>
                    {
                        var patcher = new NoFogPatcher { Log = Log };
                        noFogPatch = patcher.Patch(stagingDir, usmapPath);
                    },
                });
            }

            PersistentLootPatchResult persistentLootPatch = null;
            if (persistentLootActive)
            {
                var usmapPath = UsmapLocator.Find(_paths.ModRoot);
                LogLine("Persistent Loot source: vanilla GA_SpawnPosthumousContainer (land + ship) -> "
                        + "repoint PosthumousContainerClass to BP_Storage_DecorBag_02 (derived from vanilla)");
                sources.Add(new IoStoreCompositeSource
                {
                    Name = "persistent-loot",
                    InputDir = gamePaksDir,
                    Filter = PersistentLootPatcher.AssetFilterStem,
                    // Extract the vanilla ability (version sanity gate) then repoint the
                    // death-container soft class in place and drop the filter's collateral
                    // (the _Ship variant) before the shared to-zen.
                    AfterExtract = stagingDir =>
                    {
                        var patcher = new PersistentLootPatcher { Log = Log };
                        persistentLootPatch = patcher.Patch(stagingDir, usmapPath);
                    },
                });
            }

            KeepStatusPatchResult keepStatusPatch = null;
            if (keepStatusActive)
            {
                var usmapPath = UsmapLocator.Find(_paths.ModRoot);
                LogLine("Keep Status source: vanilla DA_Hero_ActorDeathParams -> swap broad "
                        + "GAS.Effect.Status removal for curated sub-prefixes "
                        + "(food/elixir/comfort kept on death, derived from vanilla)");
                sources.Add(new IoStoreCompositeSource
                {
                    Name = "keep-status",
                    InputDir = gamePaksDir,
                    Filter = KeepStatusPatcher.AssetFilterStem,
                    // Extract the vanilla death-params asset (version sanity gate) then
                    // rewrite the RemoveEffectWithTagPrefix tag container in place before
                    // the shared to-zen.
                    AfterExtract = stagingDir =>
                    {
                        var patcher = new KeepStatusPatcher { Log = Log };
                        keepStatusPatch = patcher.Patch(stagingDir, usmapPath);
                    },
                });
            }

            var pickaxePatchResults = new List<PickaxeRangePatchResult>();
            if (pickaxeActive)
            {
                var usmapPath = UsmapLocator.Find(_paths.ModRoot);
                LogLine("PickaxeRange source: vanilla pickaxe InstanceParams"
                        + " (multiplier=" + pickaxeMultiplier.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture)
                        + ", " + PickaxeRangePatcher.TierAssets.Count + " tier"
                        + (PickaxeRangePatcher.TierAssets.Count == 1 ? "" : "s") + ")");

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

            var lightingPatchResults = new List<LightingPatchResult>();
            if (lightingJobs != null && lightingJobs.Count > 0)
            {
                var usmapPath = UsmapLocator.Find(_paths.ModRoot);
                LogLine("Lighting source: " + lightingJobs.Count + " light"
                        + (lightingJobs.Count == 1 ? "" : "s")
                        + " (vanilla AttenuationRadius scaled per-light)");
                foreach (var job in lightingJobs)
                {
                    var localJob = job;
                    sources.Add(new IoStoreCompositeSource
                    {
                        Name = "lighting:" + localJob.Info.Stem,
                        InputDir = gamePaksDir,
                        Filter = localJob.Info.Stem,
                        AfterExtract = stagingDir =>
                        {
                            var legacyAssetPath = Path.Combine(stagingDir,
                                localJob.Info.VirtualPath.Replace('/', Path.DirectorySeparatorChar));
                            if (!File.Exists(legacyAssetPath))
                            {
                                throw new InvalidOperationException(
                                    "retoc to-legacy did not produce the expected lighting asset at "
                                    + legacyAssetPath
                                    + " - the game container may have moved the asset, or "
                                    + "the filter '" + localJob.Info.Stem + "' is wrong.");
                            }
                            var patcher = new LightingPatcher { Log = Log };
                            var r = patcher.Patch(
                                legacyAssetPath, legacyAssetPath, usmapPath,
                                localJob.Multiplier, localJob.Info);
                            lightingPatchResults.Add(r);
                        },
                    });
                }
            }

            var shipSpeedPatchResults = new List<ShipSpeedPatchResult>();
            if (shipSpeedJobs != null && shipSpeedJobs.Count > 0)
            {
                LogLine("Faster Ships source: " + shipSpeedJobs.Count + " motor curve"
                        + (shipSpeedJobs.Count == 1 ? "" : "s")
                        + " (vanilla FRichCurve key values scaled per-curve)");
                var localJobs = shipSpeedJobs;
                var keepStems = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var filterStems = new List<string>();
                foreach (var j in localJobs)
                {
                    keepStems.Add(j.Info.Stem);
                    filterStems.Add(j.Info.Stem);
                }
                // One source, one to-legacy call: retoc OR-matches all the curve
                // stems at once into the shared staging tree.
                sources.Add(new IoStoreCompositeSource
                {
                    Name = "shipspeed",
                    InputDir = gamePaksDir,
                    Filters = filterStems,
                    AfterExtract = stagingDir =>
                    {
                        var patcher = new ShipSpeedPatcher { Log = Log };
                        foreach (var j in localJobs)
                        {
                            var r = patcher.PatchCurve(stagingDir, j.Multiplier, j.Info);
                            shipSpeedPatchResults.Add(r);
                        }
                        // Substring filters (e.g. "CRV_BrigMotor") also drag in the
                        // faction siblings (_BlackBeard/_Brethren); drop any catalog
                        // curve we did not patch so to-zen ships only the intended ones.
                        patcher.RemoveCollateral(stagingDir, keepStems);
                    },
                });
            }

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
                        // null InputDir = pre-staged via callback; builder skips retoc to-legacy.
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

                // Slots whose volume differs from the 0.45 "vanilla unchanged"
                // baseline get all 4 cue variants overwritten to the absolute value.
                var volJobs = new List<ShipMusicJob>();
                const double VanillaVoicePlayerVolume = 0.45;
                foreach (var j in shipMusicJobs)
                {
                    if (Math.Abs(j.UserVolume - VanillaVoicePlayerVolume) > 0.001) volJobs.Add(j);
                }
                if (volJobs.Count > 0)
                {
                    // ShipMusicSlots.All is positional; CUE index is position+1 (1-based).
                    var volByCueIdx = new Dictionary<int, double>();
                    var filterStems = new List<string>();
                    foreach (var j in volJobs)
                    {
                        int pos0 = ShipMusicSlots.All.ToList().IndexOf(j.Slot);
                        if (pos0 < 0)
                        {
                            LogLine("ShipMusic-override-volume: slot '" + j.Slot.Stem
                                + "' not in catalog - skipping volume override");
                            continue;
                        }
                        int cueIdx = pos0 + 1;
                        volByCueIdx[cueIdx] = j.UserVolume;
                        string n = cueIdx.ToString("00", System.Globalization.CultureInfo.InvariantCulture);
                        filterStems.Add("CUE_Shanti_" + n + "_Large_VoicePlayer");
                        filterStems.Add("CUE_Shanti_" + n + "_Medium_VoicePlayer");
                        filterStems.Add("CUE_Shanti_" + n + "_Small_VoicePlayer");
                        filterStems.Add("CUE_Shanti_" + n + "_VoiceNoPlayer");
                    }

                    if (volByCueIdx.Count > 0)
                    {
                        LogLine("ShipMusic-override-volume source: "
                            + volByCueIdx.Count + " slot"
                            + (volByCueIdx.Count == 1 ? "" : "s")
                            + " with non-default volume (4 cue variants each = "
                            + filterStems.Count + " files)");

                        var volByCueIdxLocal = volByCueIdx;
                        sources.Add(new IoStoreCompositeSource
                        {
                            Name = "ship-music-override-volume",
                            InputDir = gamePaksDir,
                            Filters = filterStems,
                            AfterExtract = stagingDir =>
                            {
                                var patcher = new ShipMusicOverrideCuePatcher { Log = Log };
                                foreach (var kv in volByCueIdxLocal)
                                {
                                    int cueIdx = kv.Key;
                                    double userVol = kv.Value;
                                    string n = cueIdx.ToString("00",
                                        System.Globalization.CultureInfo.InvariantCulture);
                                    foreach (var f in ShipMusicAddPipelineHelper.Flavors)
                                    {
                                        string stem = f == "NoPlayer"
                                            ? "CUE_Shanti_" + n + "_VoiceNoPlayer"
                                            : "CUE_Shanti_" + n + "_" + f + "_VoicePlayer";
                                        string rel = ShipMusicAddCueCloner.CueRelDir(f) + "/"
                                                   + stem + ".uasset";
                                        string abs = Path.Combine(stagingDir,
                                            rel.Replace('/', Path.DirectorySeparatorChar));
                                        if (!File.Exists(abs))
                                        {
                                            throw new InvalidOperationException(
                                                "ShipMusic-override-volume: vanilla cue missing in staging: "
                                                + abs + " - retoc filter for "
                                                + stem + " did not match");
                                        }
                                        patcher.Patch(abs, abs, usmapPath, userVol);
                                    }
                                }
                            },
                        });
                    }
                }
            }

            ShipMusicPatchResult bonfireMusicPatchResult = null;
            if (bonfireMusicJob != null)
            {
                var usmapPath = UsmapLocator.Find(_paths.ModRoot);
                var encoderPath = _paths.BinkAudioEncoderPath;
                var templateUassetPath = _paths.ShipMusicTemplateUasset;
                var templateUexpPath = _paths.ShipMusicTemplateUexp;
                if (!File.Exists(encoderPath))
                    throw new FileNotFoundException(
                        "Bink Audio encoder not found at " + encoderPath
                        + " - bonfire-music ('The Hearth') cannot be built without it.");
                if (!File.Exists(templateUassetPath) || !File.Exists(templateUexpPath))
                    throw new FileNotFoundException(
                        "Ship-music template missing under " + Path.GetDirectoryName(templateUassetPath)
                        + " - expected SoundWave_BinkInline.uasset + .uexp"
                        + " (also required for the bonfire-music swap).");

                if (bonfireMusicJob.IsSynthesizedSilence)
                {
                    LogLine("BonfireMusic source: muting vanilla 'The Hearth' "
                            + "(no upload, volume=0) - synthesizing silence SWAV.");
                }
                else
                {
                    LogLine("BonfireMusic source: 1 custom hearth theme"
                            + (string.IsNullOrEmpty(bonfireMusicJob.OriginalFilename)
                                ? ""
                                : " ('" + bonfireMusicJob.OriginalFilename + "')")
                            + " @ volume=" + bonfireMusicJob.UserVolume.ToString("0.00",
                                System.Globalization.CultureInfo.InvariantCulture));
                }
                var slot = BonfireMusicSlot.ToSlotInfo();
                var localJob = bonfireMusicJob;
                var localSlot = slot;
                var localPaths = _paths;
                sources.Add(new IoStoreCompositeSource
                {
                    Name = "bonfire-music",
                    // null InputDir = pre-staged via callback; builder skips retoc to-legacy.
                    InputDir = null,
                    AfterExtract = stagingDir =>
                    {
                        string wavForEncode = null;
                        string tempWav = null;
                        try
                        {
                            if (localJob.IsSynthesizedSilence)
                            {
                                wavForEncode = AudioPreprocessor.GenerateSilenceAsync(
                                    localPaths,
                                    4.0,
                                    Log).GetAwaiter().GetResult();
                                tempWav = wavForEncode;
                            }
                            else
                            {
                                wavForEncode = AudioPreprocessor.ApplyGainAsync(
                                    localPaths,
                                    localJob.UserWavPath,
                                    localJob.UserVolume,
                                    Log).GetAwaiter().GetResult();
                                if (!string.Equals(wavForEncode, localJob.UserWavPath,
                                        StringComparison.OrdinalIgnoreCase))
                                {
                                    tempWav = wavForEncode;
                                }
                            }

                            var patcher = new ShipMusicPatcher { Log = Log };
                            var r = patcher.PatchFromWav(
                                wavForEncode,
                                templateUassetPath,
                                templateUexpPath,
                                encoderPath,
                                stagingDir,
                                localSlot,
                                usmapPath);
                            r.OriginalFilename = localJob.IsSynthesizedSilence
                                ? "(muted)"
                                : localJob.OriginalFilename;
                            bonfireMusicPatchResult = r;
                        }
                        finally
                        {
                            if (tempWav != null)
                            {
                                try { File.Delete(tempWav); }
                                catch { }
                            }
                        }
                    },
                });
            }

            // ADD and EXCLUDE both rewrite the same DA_<ShipType>_AudioParams,
            // so they share one source to avoid clobbering each other.
            var shipMusicAddTrackResults = new List<ShipMusicAddTrackResult>();
            bool hasShipMusicAdd = shipMusicAddJobs != null && shipMusicAddJobs.Count > 0;
            bool hasShipMusicExcludes = shipMusicExcludedIndices != null && shipMusicExcludedIndices.Count > 0;
            if (hasShipMusicAdd || hasShipMusicExcludes)
            {
                var usmapPath = UsmapLocator.Find(_paths.ModRoot);
                var encoderPath = _paths.BinkAudioEncoderPath;
                var templateUassetPath = _paths.ShipMusicTemplateUasset;
                var templateUexpPath = _paths.ShipMusicTemplateUexp;
                if (hasShipMusicAdd && !File.Exists(encoderPath))
                    throw new FileNotFoundException(
                        "Bink Audio encoder not found at " + encoderPath
                        + " - ship-music-add tracks cannot be built without it.");
                if (hasShipMusicAdd && (!File.Exists(templateUassetPath) || !File.Exists(templateUexpPath)))
                    throw new FileNotFoundException(
                        "Ship-music template missing under " + Path.GetDirectoryName(templateUassetPath)
                        + " - expected SoundWave_BinkInline.uasset + .uexp.");

                if (hasShipMusicAdd)
                {
                    LogLine("ShipMusicAdd source: " + shipMusicAddJobs.Count + " added track"
                            + (shipMusicAddJobs.Count == 1 ? "" : "s")
                            + " (slot indices "
                            + string.Join(", ", shipMusicAddJobs.Select(j => j.NewIndex)) + ")");
                }
                if (hasShipMusicExcludes)
                {
                    LogLine("ShipMusicAdd source: " + shipMusicExcludedIndices.Count
                            + " vanilla shanty slot"
                            + (shipMusicExcludedIndices.Count == 1 ? "" : "s")
                            + " excluded (0-based indices: "
                            + string.Join(", ", shipMusicExcludedIndices.OrderBy(i => i)) + ")");
                }

                if (hasShipMusicAdd) foreach (var job in shipMusicAddJobs)
                {
                    shipMusicAddTrackResults.Add(new ShipMusicAddTrackResult
                    {
                        TrackKey = job.TrackKey,
                        NewIndex = job.NewIndex,
                        Title = job.Title,
                        OriginalFilename = job.OriginalFilename,
                    });
                }

                if (hasShipMusicAdd) for (int i = 0; i < shipMusicAddJobs.Count; i++)
                {
                    var localJob = shipMusicAddJobs[i];
                    var localTrackResult = shipMusicAddTrackResults[i];
                    var swavStem = "SWAV_Shanti_" + localJob.TrackKey;
                    var swavVirtualPath = "R5/Content/Audio/Game/Music/Shanti/SWAV/" + swavStem + ".uasset";
                    var fakeSlot = new ShipMusicSlots.SlotInfo
                    {
                        Stem = swavStem,
                        VirtualUassetPath = swavVirtualPath,
                        Title = localJob.Title ?? localJob.TrackKey,
                    };
                    sources.Add(new IoStoreCompositeSource
                    {
                        Name = "ship-music-add-swav:" + localJob.TrackKey,
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
                                fakeSlot,
                                usmapPath);
                            localTrackResult.SwavStem = swavStem;
                            localTrackResult.SwavVirtualPath = swavVirtualPath;
                            localTrackResult.BinkBytes = r.BinkBytes;
                            localTrackResult.DurationSeconds = r.DurationSeconds.GetValueOrDefault(0f);
                            localTrackResult.SampleRate = r.SampleRate.GetValueOrDefault(0);
                            localTrackResult.Channels = r.NumChannels.GetValueOrDefault(0);
                        },
                    });
                }

                bool hasAddLocal = hasShipMusicAdd;
                bool hasExclLocal = hasShipMusicExcludes;
                var excludedSet = hasExclLocal
                    ? new HashSet<int>(shipMusicExcludedIndices)
                    : new HashSet<int>();
                sources.Add(new IoStoreCompositeSource
                {
                    Name = "ship-music-add-cues-das",
                    InputDir = gamePaksDir,
                    Filters = ShipMusicAddPipelineHelper.Filters(includeCueTemplates: hasAddLocal).ToList(),
                    AfterExtract = stagingDir =>
                    {
                        var cueCloner = new ShipMusicAddCueCloner { Log = Log };
                        var daPatcher = new ShipMusicAddDaPatcher { Log = Log };

                        var vanillaCues = new Dictionary<string, string>();
                        if (hasAddLocal) foreach (var f in ShipMusicAddPipelineHelper.Flavors)
                        {
                            var rel = ShipMusicAddPipelineHelper.VanillaCueRelPath(f)
                                        .Replace('/', Path.DirectorySeparatorChar);
                            var abs = Path.Combine(stagingDir, rel);
                            if (!File.Exists(abs))
                                throw new InvalidOperationException(
                                    "ship-music-add: vanilla cue template missing in staging: " + abs
                                    + " - retoc filter for "
                                    + ShipMusicAddCueCloner.VanillaCueStem(f) + " did not match");
                            vanillaCues[f] = abs;
                        }

                        var daAbs = new Dictionary<string, string>();
                        foreach (var da in ShipMusicAddPipelineHelper.DaStems)
                        {
                            var rel = ShipMusicAddPipelineHelper.DaRelPath(da)
                                        .Replace('/', Path.DirectorySeparatorChar);
                            var abs = Path.Combine(stagingDir, rel);
                            if (!File.Exists(abs))
                                throw new InvalidOperationException(
                                    "ship-music-add: vanilla DA missing in staging: " + abs
                                    + " - retoc filter for " + da + " did not match");
                            daAbs[da] = abs;
                        }

                        if (hasAddLocal) for (int i = 0; i < shipMusicAddJobs.Count; i++)
                        {
                            var job = shipMusicAddJobs[i];
                            var trackRes = shipMusicAddTrackResults[i];

                            // Prefer the SWAV source's value; fall back to a fresh
                            // WavInfo read since composite sources may run out of order.
                            float audioDur = trackRes.DurationSeconds;
                            if (audioDur <= 0f)
                            {
                                var info = WavInfo.Read(job.UserWavPath);
                                audioDur = info.DurationSeconds;
                            }
                            if (audioDur <= 0f)
                                throw new InvalidOperationException(
                                    "ship-music-add: could not determine audio duration "
                                    + "for track '" + job.TrackKey + "' (path: "
                                    + job.UserWavPath + ") - cue cloner needs a positive "
                                    + "duration to write SoundCue.Duration.");

                            var createdCues = new List<string>(4);
                            foreach (var flavor in ShipMusicAddPipelineHelper.Flavors)
                            {
                                var newRel = ShipMusicAddPipelineHelper.NewCueRelPath(flavor, job.NewIndex)
                                                .Replace('/', Path.DirectorySeparatorChar);
                                var newAbs = Path.Combine(stagingDir, newRel);
                                var cr = cueCloner.Clone(
                                    inputUassetPath:  vanillaCues[flavor],
                                    outputUassetPath: newAbs,
                                    usmapPath:        usmapPath,
                                    flavor:           flavor,
                                    newIndex:         job.NewIndex,
                                    newSwavStem:      job.TrackKey,
                                    audioDurationSec: audioDur,
                                    userVolumeAbsolute: job.UserVolume);
                                createdCues.Add(cr.NewCueStem);
                            }
                            trackRes.CueStemsCreated = createdCues;
                        }

                        // Delete vanilla cue templates so they don't ship in the mod-pak.
                        foreach (var kv in vanillaCues)
                        {
                            var uassetAbs = kv.Value;
                            var uexpAbs = Path.ChangeExtension(uassetAbs, ".uexp");
                            if (File.Exists(uassetAbs)) File.Delete(uassetAbs);
                            if (File.Exists(uexpAbs))   File.Delete(uexpAbs);
                        }

                        foreach (var da in ShipMusicAddPipelineHelper.DaStems)
                        {
                            var voiceFlavor = ShipMusicAddPipelineHelper.VoiceFlavorForDa(da);
                            var slotRefs = hasAddLocal
                                ? shipMusicAddJobs
                                    .Select(j => ShipMusicAddPipelineHelper.BuildSlotRef(voiceFlavor, j.NewIndex))
                                    .ToList()
                                : new List<ShipMusicAddSlotRef>();
                            daPatcher.Patch(daAbs[da], daAbs[da], usmapPath, excludedSet, slotRefs);
                        }
                    },
                });
            }

            NoSmokeResult noSmokeOut = null;
            if (noSmokeCategories != null && noSmokeCategories.Count > 0)
            {
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
                        // Remove the template so to-zen doesn't ship it and override the vanilla asset.
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

            List<BuildingPatchResult> buildingResults = null;
            if (buildingsActive)
            {
                var usmapPath = UsmapLocator.Find(_paths.ModRoot);
                var buildingTmp = Path.Combine(_paths.BuildTmp, profile.Id + "__buildings");
                LogLine("Buildings source: "
                        + CountBuildableBuildings(profile)
                        + " custom building(s)");
                sources.Add(new IoStoreCompositeSource
                {
                    Name = "buildings",
                    // null InputDir = pre-staged; patcher writes directly into stagingDir.
                    InputDir = null,
                    AfterExtract = stagingDir =>
                    {
                        if (Directory.Exists(buildingTmp)) Directory.Delete(buildingTmp, true);
                        Directory.CreateDirectory(buildingTmp);

                        var stagingItemsDir = Path.Combine(stagingDir,
                            "R5", "Content", "Quartermaster", "Items");

                        _buildingPatcher.Log = Log;
                        _buildingPatcher.RetocExe = retocExe;
                        _buildingPatcher.UsmapPath = usmapPath;
                        _buildingPatcher.VanillaPaksDir = gamePaksDir;
                        _buildingPatcher.AesKey = WindroseGameSecrets.AesKey;
                        _buildingPatcher.TempDir = buildingTmp;

                        _blueprintPatcher.Log = Log;
                        _blueprintPatcher.RetocExe = retocExe;
                        _blueprintPatcher.UsmapPath = usmapPath;
                        _blueprintPatcher.VanillaPaksDir = gamePaksDir;
                        _blueprintPatcher.AesKey = WindroseGameSecrets.AesKey;
                        _blueprintPatcher.TempDir = buildingTmp;

                        _audioStager.Log = Log;
                        _audioStager.RetocExe = retocExe;
                        _audioStager.UsmapPath = usmapPath;
                        _audioStager.VanillaPaksDir = gamePaksDir;
                        _audioStager.AesKey = WindroseGameSecrets.AesKey;
                        _audioStager.TempDir = buildingTmp;
                        _audioStager.BinkEncoderPath    = _paths.BinkAudioEncoderPath;
                        _audioStager.SwavTemplateUasset = _paths.ShipMusicTemplateUasset;
                        _audioStager.SwavTemplateUexp   = _paths.ShipMusicTemplateUexp;

                        // Pre-flight: hard-fail upfront when a building wants Audio
                        // but the encoder/templates are missing, rather than later.
                        bool hasBuildingAudio = false;
                        if (profile.CustomBuildings != null)
                        {
                            foreach (var bb in profile.CustomBuildings)
                            {
                                if (bb == null || string.IsNullOrWhiteSpace(bb.Id)) continue;
                                var pp = ComponentPresetCatalog.Resolve(bb.ComponentPresetId);
                                if (pp == null || pp.Kind != ComponentPresetKind.Audio) continue;
                                var aDir = _paths.ProfileBuildingAudioDir(profile.Id, bb.Id);
                                if (File.Exists(Path.Combine(aDir, "audio.wav")))
                                {
                                    hasBuildingAudio = true;
                                    break;
                                }
                            }
                        }
                        if (hasBuildingAudio)
                        {
                            if (!File.Exists(_paths.BinkAudioEncoderPath))
                                throw new FileNotFoundException(
                                    "Bink Audio encoder not found at " + _paths.BinkAudioEncoderPath
                                    + " - buildings with the Audio component preset and a user-uploaded"
                                    + " audio file cannot be built without it.");
                            if (!File.Exists(_paths.ShipMusicTemplateUasset)
                                || !File.Exists(_paths.ShipMusicTemplateUexp))
                                throw new FileNotFoundException(
                                    "SoundWave template missing under "
                                    + Path.GetDirectoryName(_paths.ShipMusicTemplateUasset)
                                    + " - expected SoundWave_BinkInline.uasset + .uexp"
                                    + " (used by the Building Audio stager).");
                        }

                        LogLine("Buildings: staging shared default textures");
                        DefaultTextureProvider.StageInto(_paths, stagingItemsDir, usmapPath, Log);

                        var stagedComponentBuildings =
                            new Dictionary<string, BlueprintStageResult>(StringComparer.OrdinalIgnoreCase);

                        buildingResults = new List<BuildingPatchResult>();
                        // Two buildings sharing a MeshStem would clobber each
                        // other's slot refs in staging; first owner wins.
                        var meshOwner = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                        foreach (var b in profile.CustomBuildings)
                        {
                            if (b == null) continue;
                            if (string.IsNullOrWhiteSpace(b.Id)) continue;
                            if (string.IsNullOrWhiteSpace(b.TemplateId)) continue;
                            if (string.IsNullOrWhiteSpace(b.CookedFolderPath)) continue;
                            if (string.IsNullOrWhiteSpace(b.MeshStem)) continue;
                            if (string.IsNullOrWhiteSpace(b.ResolveAssetPrefix())) continue;

                            if (meshOwner.TryGetValue(b.MeshStem, out var firstOwner))
                            {
                                LogLine("  warn: building '" + b.Id + "' uses MeshStem '"
                                    + b.MeshStem + "' which was already claimed by building '"
                                    + firstOwner + "'. Two buildings cannot share the same"
                                    + " mesh file - rename or re-cook one of them. Skipping '"
                                    + b.Id + "' to keep '" + firstOwner + "' intact.");
                                continue;
                            }
                            meshOwner[b.MeshStem] = b.Id;

                            var template = ResolveBuildingTemplate(b.TemplateId);
                            if (template == null)
                            {
                                LogLine("  warn: unknown templateId='" + b.TemplateId
                                        + "' for building id='" + b.Id + "' - skipping");
                                continue;
                            }

                            // A component preset overrides the user-picked template
                            // with the preset's source-DA refs.
                            var bldComponentPreset = ComponentPresetCatalog.Resolve(b.ComponentPresetId);

                            // Flame requires a socket (skipped without one); Audio's
                            // socket is optional. Only the first socket is used.
                            StaticMeshSocketReader.Socket bldComponentSocket = null;
                            if (bldComponentPreset != null)
                            {
                                try
                                {
                                    var resolvedCookedFolder = _paths.ResolveProfileRelativeFolder(profile.Id, b.CookedFolderPath);
                                    var userMeshFile = Path.Combine(resolvedCookedFolder, b.MeshStem + ".uasset");
                                    var reader = new StaticMeshSocketReader
                                    {
                                        UsmapPath = usmapPath,
                                        Log       = Log,
                                    };
                                    bldComponentSocket = reader.FindFirst(userMeshFile);
                                }
                                catch (Exception ex)
                                {
                                    LogLine("  warn: socket read failed for mesh '" + b.MeshStem
                                        + "': " + ex.Message + " - component preset will use vanilla BP positions");
                                    bldComponentSocket = null;
                                }

                                bool requiresSocket = bldComponentPreset.Kind == ComponentPresetKind.Flame;
                                if (bldComponentSocket == null && requiresSocket)
                                {
                                    LogLine("  [" + bldComponentPreset.Kind + "] preset '" + bldComponentPreset.Id
                                        + "' set but mesh '" + b.MeshStem
                                        + "' has no sockets - skipping preset for this building"
                                        + " (add at least one socket to the mesh to enable flame placement)");
                                    bldComponentPreset = null;
                                }
                                else
                                {
                                    var socketDesc = bldComponentSocket != null
                                        ? "socket '" + (bldComponentSocket.Name ?? "<noname>") + "'"
                                        : "vanilla BP positions (no socket on mesh)";
                                    LogLine("  [" + bldComponentPreset.Kind + "] preset '" + bldComponentPreset.Id
                                        + "' active for '" + b.Id + "' - using " + socketDesc
                                        + ", swapping template '" + template.Id
                                        + "' with source DA '" + bldComponentPreset.SourceVanillaDaStem + "'");
                                    template = bldComponentPreset.ApplyTo(template);
                                }
                            }

                            BuildingInputs inputs;
                            try
                            {
                                inputs = BuildBuildingInputs(b, template, usmapPath, _paths, profile.Id, Log);
                            }
                            catch (Exception ex)
                            {
                                LogLine("  warn: failed to build inputs for '" + b.Id + "': " + ex.Message + " - skipping");
                                continue;
                            }

                            // Runs before the BP-staging try block so audio errors
                            // propagate instead of being swallowed by its catch.
                            BuildingAudioStageResult bldAudioStage = null;
                            if (bldComponentPreset != null
                                && bldComponentPreset.Kind == ComponentPresetKind.Audio)
                            {
                                var audioDir = _paths.ProfileBuildingAudioDir(profile.Id, b.Id);
                                var audioWav = Path.Combine(audioDir, "audio.wav");
                                if (File.Exists(audioWav))
                                {
                                    bldAudioStage = _audioStager.Stage(
                                        b.Id, audioWav, stagingItemsDir,
                                        rangeMeters: b.AudioRangeMeters,
                                        volume:      b.AudioVolume);
                                    LogLine("  [Audio] user audio staged for '" + b.Id + "' -> "
                                        + bldAudioStage.CueStem + " (loop "
                                        + bldAudioStage.DurationSeconds.ToString("0.##",
                                            System.Globalization.CultureInfo.InvariantCulture)
                                        + "s, range="
                                        + bldAudioStage.RangeMeters.ToString("0.##",
                                            System.Globalization.CultureInfo.InvariantCulture)
                                        + "m, volume="
                                        + bldAudioStage.Volume.ToString("0.##",
                                            System.Globalization.CultureInfo.InvariantCulture)
                                        + " abs)");
                                }
                                else
                                {
                                    LogLine("  [Audio] no user audio for '" + b.Id
                                        + "' - keeping vanilla Tick-Tack loop");
                                }
                            }

                            // Must run before the DA patcher: it populates
                            // inputs.ExtraDaNameMapRewrites from the cloned BP stem.
                            BlueprintStageResult bldBpStage = null;
                            if (bldComponentPreset != null)
                            {
                                try
                                {
                                    var userMeshStem = inputs.MeshStem;
                                    var userMeshPath = WindrosePaths.ModItemsPackagePath + userMeshStem;

                                    bldBpStage = _blueprintPatcher.Stage(
                                        bldComponentPreset, b.Id, userMeshStem, userMeshPath, stagingItemsDir,
                                        bldComponentSocket, bldAudioStage);
                                    stagedComponentBuildings[b.Id] = bldBpStage;
                                    foreach (var w in bldBpStage.Warnings ?? new List<string>())
                                        LogLine("  warn: component BP '" + b.Id + "': " + w);

                                    // Redirect the cloned DA's ItemClass FName entries
                                    // to this building's per-building BP clone.
                                    inputs.ExtraDaNameMapRewrites = new Dictionary<string, string>(StringComparer.Ordinal);
                                    if (!string.IsNullOrEmpty(bldComponentPreset.SourceVanillaItemClassPath))
                                    {
                                        inputs.ExtraDaNameMapRewrites[bldComponentPreset.SourceVanillaItemClassPath]
                                            = ComponentPresetCatalog.ComponentPreset.ClonedPackagePathFor(bldComponentPreset, b.Id);
                                    }
                                    if (!string.IsNullOrEmpty(bldComponentPreset.SourceVanillaItemClassStem))
                                    {
                                        inputs.ExtraDaNameMapRewrites[bldComponentPreset.SourceVanillaItemClassStem + "_C"]
                                            = bldBpStage.ClonedBpStem + "_C";
                                    }
                                }
                                catch (Exception ex)
                                {
                                    LogLine("  warn: component BP staging failed for '" + b.Id
                                        + "': " + ex.Message
                                        + " - building will spawn under vanilla BP class (no preset FX)");
                                }
                            }

                            LogLine("Patching building '" + b.Id + "' (template=" + template.Id + ")");
                            var result = _buildingPatcher.Patch(template, inputs, stagingItemsDir, profile.Id);
                            buildingResults.Add(result);
                            foreach (var w in result.Warnings) LogLine("  warn: " + w);

                            if (bldComponentPreset != null && bldBpStage != null)
                            {
                                LogLine("  [" + bldComponentPreset.Kind + "] DA ItemClass redirected via NameMap -> "
                                    + bldBpStage.ClonedClassPath
                                    + " (preset '" + bldComponentPreset.Id + "')");
                            }
                            else if (bldComponentPreset != null)
                            {
                                result.Warnings.Add(
                                    bldComponentPreset.Kind + " preset '" + bldComponentPreset.Id
                                    + "' BP staging failed for '" + b.Id
                                    + "' - building will spawn under vanilla BP class instead of cloned BP.");
                                LogLine("  warn: " + result.Warnings[^1]);
                            }

                            if (!string.IsNullOrEmpty(template.VanillaRecipeJsonPath))
                            {
                                try
                                {
                                    var vanillaRecipeAbs = Path.Combine(_paths.Vanilla, template.VanillaRecipeJsonPath);
                                    var legacyStagingDir = Path.Combine(_paths.BuildTmp, profile.Id);
                                    var recipesOutDir = Path.Combine(legacyStagingDir,
                                        "R5", "Plugins", "R5BusinessRules", "Content",
                                        "Recipes", "Building", "Items", "Decorations");
                                    _recipePatcher.Log = Log;
                                    var costList = ToTupleList(b.RecipeCost);
                                    var rp = _recipePatcher.Patch(
                                        vanillaRecipeAbs,
                                        recipesOutDir,
                                        b.Id,
                                        costList,
                                        b.Name,
                                        b.Description);
                                    result.OutputRecipeJsonPath = rp.OutputJsonPath;
                                    result.OutputRecipeStem     = rp.OutputStem;
                                    result.NewRecipeTag         = rp.NewRecipeTag;
                                    result.RecipeCostRows       = rp.RecipeCostRows;
                                    result.RecipeCostOverridden = rp.CostOverridden;
                                }
                                catch (Exception ex)
                                {
                                    // Non-fatal: building ships but becomes uncraftable.
                                    result.Warnings.Add(
                                        "Recipe patch failed for '" + b.Id + "': " + ex.Message);
                                    LogLine("  warn: " + result.Warnings[^1]);
                                }
                            }
                        }
                        LogLine("Patched buildings: " + buildingResults.Count + " written");
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

            LandFastTravelResult landFastTravelOut = null;
            if (landFastTravelActive)
            {
                landFastTravelOut = new LandFastTravelResult
                {
                    Enabled = true,
                    AssetsReplaced = landFastTravelPatch != null ? landFastTravelPatch.AssetsReplaced : 0,
                    UcasPath = finalUcas,
                    UtocPath = finalUtoc,
                    PakPath = mainPakWillBeBuilt ? null : finalPak,
                };
            }

            NoFogResult noFogOut = null;
            if (noFogActive)
            {
                noFogOut = new NoFogResult
                {
                    Enabled = true,
                    AssetsReplaced = noFogPatch != null ? noFogPatch.AssetsReplaced : 0,
                    UcasPath = finalUcas,
                    UtocPath = finalUtoc,
                    PakPath = mainPakWillBeBuilt ? null : finalPak,
                };
            }

            PersistentLootResult persistentLootOut = null;
            if (persistentLootActive)
            {
                persistentLootOut = new PersistentLootResult
                {
                    Enabled = true,
                    AssetsReplaced = persistentLootPatch != null ? persistentLootPatch.AssetsReplaced : 0,
                    UcasPath = finalUcas,
                    UtocPath = finalUtoc,
                    PakPath = mainPakWillBeBuilt ? null : finalPak,
                };
            }

            KeepStatusResult keepStatusOut = null;
            if (keepStatusActive)
            {
                keepStatusOut = new KeepStatusResult
                {
                    Enabled = true,
                    AssetsReplaced = keepStatusPatch != null ? keepStatusPatch.AssetsReplaced : 0,
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

            ShipMusicAddResult shipMusicAddOut = null;
            bool hasShipMusicAddOut = shipMusicAddTrackResults.Count > 0
                                   || (shipMusicExcludedIndices != null && shipMusicExcludedIndices.Count > 0);
            if (hasShipMusicAddOut)
            {
                shipMusicAddOut = new ShipMusicAddResult
                {
                    Enabled = true,
                    TrackResults = shipMusicAddTrackResults,
                    ExcludedSlotIndices = shipMusicExcludedIndices == null
                        ? new List<int>()
                        : shipMusicExcludedIndices.OrderBy(i => i).ToList(),
                    UcasPath = finalUcas,
                    UtocPath = finalUtoc,
                    PakPath = mainPakWillBeBuilt ? null : finalPak,
                };
            }

            BonfireMusicResult bonfireMusicOut = null;
            if (bonfireMusicPatchResult != null)
            {
                bonfireMusicOut = new BonfireMusicResult
                {
                    Enabled = true,
                    SlotResult = bonfireMusicPatchResult,
                    UcasPath = finalUcas,
                    UtocPath = finalUtoc,
                    PakPath = mainPakWillBeBuilt ? null : finalPak,
                };
            }

            LightingResult lightingOut = null;
            if (lightingPatchResults.Count > 0)
            {
                double overall = ResolveLightingOverallMultiplier(profile);
                lightingOut = new LightingResult
                {
                    Enabled = true,
                    OverallMultiplier = overall,
                    AssetResults = lightingPatchResults,
                    UcasPath = finalUcas,
                    UtocPath = finalUtoc,
                    PakPath = mainPakWillBeBuilt ? null : finalPak,
                };
            }

            ShipSpeedResult shipSpeedOut = null;
            if (shipSpeedPatchResults.Count > 0)
            {
                double overall = ResolveShipSpeedOverallMultiplier(profile);
                shipSpeedOut = new ShipSpeedResult
                {
                    Enabled = true,
                    OverallMultiplier = overall,
                    AssetResults = shipSpeedPatchResults,
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
                LandFastTravel = landFastTravelOut,
                NoFog = noFogOut,
                PersistentLoot = persistentLootOut,
                KeepStatus = keepStatusOut,
                PickaxeRange = pickaxeOut,
                Cooldowns = cooldownsOut,
                ShipMusic = shipMusicOut,
                ShipMusicAdd = shipMusicAddOut,
                BonfireMusic = bonfireMusicOut,
                Lighting = lightingOut,
                ShipSpeed = shipSpeedOut,
                Icons = iconResults,
                Buildings = buildingResults,
            };
        }

        sealed class BuildIoStoreCompositeOutput
        {
            public PickupTripletResult Pickup;
            public NoSmokeResult NoSmoke;
            public BonfireRadiusResult Bonfire;
            public LandFastTravelResult LandFastTravel;
            public NoFogResult NoFog;
            public PersistentLootResult PersistentLoot;
            public KeepStatusResult KeepStatus;
            public PickaxeRangeResult PickaxeRange;
            public CooldownsResult Cooldowns;
            public ShipMusicResult ShipMusic;
            public ShipMusicAddResult ShipMusicAdd;
            public BonfireMusicResult BonfireMusic;
            public LightingResult Lighting;
            public ShipSpeedResult ShipSpeed;
            public List<IconBakerPatcher.BakeResult> Icons;
            public List<BuildingPatchResult> Buildings;
        }

        static int CountBuildableBuildings(Profile profile)
        {
            if (profile.CustomBuildings == null) return 0;
            int n = 0;
            foreach (var b in profile.CustomBuildings)
            {
                if (b == null) continue;
                if (string.IsNullOrWhiteSpace(b.Id)) continue;
                if (string.IsNullOrWhiteSpace(b.TemplateId)) continue;
                if (string.IsNullOrWhiteSpace(b.CookedFolderPath)) continue;
                if (string.IsNullOrWhiteSpace(b.MeshStem)) continue;
                if (string.IsNullOrWhiteSpace(b.ResolveAssetPrefix())) continue;
                n++;
            }
            return n;
        }

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

                // IconPath is a basename only; rebuild the absolute path.
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

            var rawRoot = Path.Combine(_paths.BuildTmp, profile.Id + "__raw");
            if (Directory.Exists(rawRoot)) Directory.Delete(rawRoot, true);
            Directory.CreateDirectory(rawRoot);

            var finalPak  = Path.Combine(outDir, rawBaseName + ".pak");
            var finalUcas = Path.Combine(outDir, rawBaseName + ".ucas");
            var finalUtoc = Path.Combine(outDir, rawBaseName + ".utoc");

            BuildingStabilityResult stabilityResult = null;
            string srcUcas = null, srcUtoc = null, stubPak = null;
            if (stabilityActive)
            {
                stabilityResult = BuildStabilityInsideRawRoot(
                    profile, rawRoot, rawBaseName,
                    out srcUcas, out srcUtoc, out stubPak);
            }

            // The map-settings real .pak (minimap range) displaces any stub from
            // the stability step.
            MinimapRangeResult minimapResult = null;
            string srcRealPak = null;
            if (minimapActive)
            {
                var mapOut = BuildMapSettingsPakInsideRawRoot(
                    profile, rawRoot, rawBaseName, minimapMultiplier,
                    out srcRealPak);
                minimapResult = mapOut.Minimap;
            }

            // Stub .pak is used only when no real minimap pak exists.
            var publishedPak = srcRealPak ?? stubPak;
            if (publishedPak == null)
            {
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

            // These legacy pairs only probe byte patterns; never round-trip them
            // through to-zen (that output crashes the game for this class).
            LogLine("retoc to-legacy --filter " + BuildingStabilityPatcher.AssetFilterStem
                    + " -> " + legacyDir);
            RunRetoc(retocExe, new[]
            {
                "to-legacy", gamePaksDir, legacyDir, "--version", "UE5_6",
                "--filter", BuildingStabilityPatcher.AssetFilterStem,
            });

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

            // pack-raw emits only .ucas/.utoc; UE5 needs a .pak mount marker,
            // synthesized separately below.
            LogLine("retoc pack-raw: " + rawDir + " -> " + outBase + ".utoc");
            RunRetoc(retocExe, new[] { "pack-raw", rawDir, outBase + ".utoc" });

            srcUcas = outBase + ".ucas";
            srcUtoc = outBase + ".utoc";
            if (!File.Exists(srcUcas) || !File.Exists(srcUtoc))
            {
                throw new InvalidOperationException(
                    "retoc pack-raw reported success but .ucas/.utoc missing under " + outBase);
            }

            // Synthesize the .pak marker via to-zen on an empty dir; its stub
            // .pak works as a generic marker for any same-version companion.
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
            };
        }

        // Builds the single DefaultR5MapSettings.ini pak that carries the minimap
        // range edit. (No-fog used to ride this same INI by flipping bFogEnabled,
        // but that disabled the fog subsystem and crashed the game on world load;
        // no-fog now ships as an M_Map material override via the IoStore composite
        // instead - see NoFogPatcher.)
        MapSettingsPakOutput BuildMapSettingsPakInsideRawRoot(
            Profile profile, string rawRoot, string rawBaseName, double multiplier,
            out string srcRealPak)
        {
            var configExtractor = new VanillaConfigExtractor(_paths) { Log = Log };
            var vanillaIniPath = configExtractor.EnsureMapSettings();

            var minimapStageRoot = Path.Combine(rawRoot, "minimap-stage");
            var stagedIni = Path.Combine(minimapStageRoot,
                "R5", "Config", "DefaultR5MapSettings.ini");

            var patcher = new MinimapRangePatcher { Log = Log };
            var mapPatch = patcher.PatchToFile(vanillaIniPath, stagedIni, multiplier);

            LogLine("Resolving repak.exe...");
            _repakResolver.Log = Log;
            var repakExe = _repakResolver.Resolve();

            var pakOutDir = Path.Combine(rawRoot, "minimap-out");
            Directory.CreateDirectory(pakOutDir);
            srcRealPak = Path.Combine(pakOutDir, rawBaseName + ".pak");

            var builder = new PakBuilder(repakExe) { Log = Log };
            builder.Build(minimapStageRoot, srcRealPak, overwrite: true);

            return new MapSettingsPakOutput
            {
                Minimap = new MinimapRangeResult
                {
                    Enabled = true,
                    Multiplier = multiplier,
                    Patch = mapPatch,
                },
            };
        }

        sealed class MapSettingsPakOutput
        {
            public MinimapRangeResult Minimap;
        }

        sealed class BuildRawCompanionOutput
        {
            public BuildingStabilityResult Stability;
            public MinimapRangeResult Minimap;
        }

        void RunRetoc(string retocExe, string[] args)
        {
            var r = ToolProcess.RunCapture(retocExe, args);
            if (r.ExitCode != 0)
            {
                throw new InvalidOperationException(
                    "retoc " + args[0] + " failed (exit " + r.ExitCode + ")\n" + r.ErrOrOut);
            }
        }

        void LogLine(string msg)
        {
            if (Log != null) Log(msg);
        }
    }
}
