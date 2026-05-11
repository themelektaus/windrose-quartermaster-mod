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

                int totalWritten = patchResult.Written
                    + (lootResult != null ? lootResult.Written : 0)
                    + (bellResult != null && bellResult.Written ? 1 : 0);
                double pickupMultiplier = ResolvePickupMultiplier(profile);
                bool pickupActive = pickupMultiplier > 0.0 && Math.Abs(pickupMultiplier - 1.0) > 1e-9;
                bool stabilityActive = ResolveStabilityEnabled(profile);
                var noSmokeCategories = ResolveNoSmokeCategories(profile);
                bool noSmokeActive = noSmokeCategories.Count > 0;
                double minimapMultiplier = ResolveMinimapMultiplier(profile);
                bool minimapActive = minimapMultiplier > 0.0 && Math.Abs(minimapMultiplier - 1.0) > 1e-9;
                bool ioStoreActive = pickupActive || stabilityActive || noSmokeActive || minimapActive;
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
                bool compositeActive = pickupActive || noSmokeActive;
                if (compositeActive)
                {
                    var compositeResult = BuildIoStoreComposite(
                        profile, outDir, pickupMultiplier, pickupActive,
                        noSmokeCategories,
                        sharedBaseName, mainPakWillBeBuilt: totalWritten > 0);
                    pickupResult = compositeResult.Pickup;
                    noSmokeResult = compositeResult.NoSmoke;
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
                    PakResult = pakResult,
                    PakPath = pakPath,
                    PickupResult = pickupResult,
                    PickupMultiplier = pickupActive ? (double?)pickupMultiplier : null,
                    StabilityResult = stabilityResult,
                    NoSmokeResult = noSmokeResult,
                    MinimapResult = minimapResult,
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

            return new BuildIoStoreCompositeOutput
            {
                Pickup = pickupOut,
                NoSmoke = noSmokeOut,
            };
        }

        // Internal carrier so BuildIoStoreComposite can return all
        // feature-specific result objects to the caller without a tuple.
        sealed class BuildIoStoreCompositeOutput
        {
            public PickupTripletResult Pickup;
            public NoSmokeResult NoSmoke;
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
}
