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
    // IoStore content (pickup-radius patch + enhanced building stability)
    // is built into a SHARED triplet that ships alongside the main Pak1.
    // The IoStoreCompositeBuilder takes one or more "sources" - each
    // source contributes Legacy assets to a unified staging tree - and
    // produces one .ucas / .utoc / .pak-stub set with everything merged.
    //
    // Two source kinds today:
    //   1. Pickup: vanilla GA_Loot_AutoPickup Blueprint extracted via
    //      retoc to-legacy and then patched in-place via UAssetAPI to
    //      add a serialized MagnetRadius FloatProperty.
    //   2. Stability: the BetterStructureSupport reference mod's 787
    //      pre-cooked DA_BI* DataAssets adopted 1:1 (single toggle, no
    //      patch - vanilla DA_BI cannot be UAssetAPI-parsed).
    //
    // Both share the basename of the main pak so UE5 mounts them as one
    // logical mod (.pak Pak1 + .ucas/.utoc IoStore companions).
    //
    // A third source - NoSmoke - self-bakes vanilla Niagara assets
    // (FX_Bonefire/Campfire/Furnace/Kiln) and patches every
    // EmitterHandle.bIsEnabled to false to silence the smoke / flame
    // particle systems. Three independent toggles map to three asset
    // groups (Campfire / Furnace / Kiln); active toggles' assets are
    // pulled in a single to-legacy call (multi --filter) and patched in
    // the AfterExtract callback.
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
        // for builds that activate any IoStore feature (pickup-radius or
        // building-stability) - retoc's to-legacy step needs the vanilla
        // IoStore container as input AND the game's global.utoc to resolve
        // ScriptObjects, even for the stability source which only adopts
        // bytes from a reference mod. The GUI wires this to
        // SteamLocator.FindVanillaPaksDir; CLI smoke tests leave it null
        // since CLI builds never enable IoStore features.
        public Func<string> GamePaksDirProvider;

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
                    foreach (var p in new[] { outPakPath, sharedUcasPath, sharedUtocPath })
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
                bool ioStoreActive = pickupActive || stabilityActive || noSmokeActive;
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
                BuildingStabilityResult stabilityResult = null;
                NoSmokeResult noSmokeResult = null;
                if (ioStoreActive)
                {
                    var compositeResult = BuildIoStoreComposite(
                        profile, outDir, pickupMultiplier, pickupActive, stabilityActive,
                        noSmokeCategories,
                        sharedBaseName, mainPakWillBeBuilt: totalWritten > 0);
                    pickupResult = compositeResult.Pickup;
                    stabilityResult = compositeResult.Stability;
                    noSmokeResult = compositeResult.NoSmoke;
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
            double pickupMultiplier, bool pickupActive, bool stabilityActive,
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

            BuildingStabilityResult stabilityOut = null;
            if (stabilityActive)
            {
                // Reference-mod adoption: copy mod triplet AND the game's
                // global.{ucas,utoc} into a tmp dir, point retoc at that.
                var refsDir = Path.Combine(iostoreRoot, "stability-refs");
                Directory.CreateDirectory(refsDir);
                var modPak = Path.Combine(_paths.References, StabilityReferenceFileName + ".pak");
                if (!File.Exists(modPak))
                {
                    throw new FileNotFoundException(
                        "Building-stability reference mod missing: "
                        + modPak
                        + " - expected the BetterStructureSupport_P triplet under "
                        + _paths.References);
                }
                foreach (var ext in new[] { ".pak", ".ucas", ".utoc" })
                {
                    File.Copy(
                        Path.Combine(_paths.References, StabilityReferenceFileName + ext),
                        Path.Combine(refsDir, StabilityReferenceFileName + ext),
                        true);
                }
                foreach (var f in new[] { "global.ucas", "global.utoc" })
                {
                    var src = Path.Combine(gamePaksDir, f);
                    if (!File.Exists(src))
                    {
                        throw new FileNotFoundException(
                            "Game's " + f + " not found in " + gamePaksDir
                            + " - needed for ScriptObjects resolution during reference-mod extraction.");
                    }
                    File.Copy(src, Path.Combine(refsDir, f), true);
                }
                LogLine("Stability source: reference mod "
                        + StabilityReferenceFileName + " (787 DA_BI assets, adopted 1:1)");
                sources.Add(new IoStoreCompositeSource
                {
                    Name = "stability",
                    InputDir = refsDir,
                    Filter = null,
                    AfterExtract = null,
                });
                stabilityOut = new BuildingStabilityResult { Enabled = true };
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
                Stability = stabilityOut,
                NoSmoke = noSmokeOut,
            };
        }

        // Basename (no extension) of the BetterStructureSupport reference
        // mod triplet under <_paths.References>. Bundled in the repo to
        // keep the build offline-capable.
        const string StabilityReferenceFileName = "BetterStructureSupport_P";

        // Internal carrier so BuildIoStoreComposite can return all
        // feature-specific result objects to the caller without a tuple.
        sealed class BuildIoStoreCompositeOutput
        {
            public PickupTripletResult Pickup;
            public BuildingStabilityResult Stability;
            public NoSmokeResult NoSmoke;
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
        public string TmpDir;
        public bool Success;
    }

    // Standalone summary of "stability got included in this build". Kept
    // separate from PickupTripletResult so the caller can report the two
    // features independently in the response payload.
    public sealed class BuildingStabilityResult
    {
        public bool Enabled;
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
}
