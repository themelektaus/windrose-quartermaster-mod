using System;
using System.IO;
using System.Text;

namespace Windrose.Quartermaster.Core
{
    // Orchestrates the full build for a given Profile:
    //   Vanilla/  --(StackPatcher)-->  .build-tmp/<profile-id>/  --(PakBuilder)-->  Builds/<name>_P.pak
    //
    // The temp directory is wiped before patching and (by default) deleted
    // after a successful pack -- callers can opt into keepTemp=true for
    // post-mortem debugging.
    //
    // Pickup-radius is a separate IoStore mod triplet built alongside the
    // main pak: we shell out to retoc to convert the vanilla
    // GA_Loot_AutoPickup Blueprint to Legacy, patch MagnetRadius via
    // UAssetAPI, then re-pack as IoStore. UE5 mounts our _P-suffixed
    // triplet at higher mount priority than vanilla, so the patched
    // value wins at runtime. See PickupTripletBuilder for the gritty
    // details.
    public sealed class BuildPipeline
    {
        readonly WindrosePaths _paths;
        readonly StackPatcher _patcher;
        readonly LootPatcher _lootPatcher;
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
        // ONLY for builds that activate the pickup-radius patch -- retoc's
        // to-legacy step needs the vanilla IoStore container as input.
        // The GUI wires this to SteamLocator.FindVanillaPaksDir; CLI smoke
        // tests leave it null since CLI builds never enable pickup-radius.
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
                    + " -- run Dump-WindroseVanilla.ps1 first to extract it from the game pak");

            var safeName = SanitizeForFileName(profile.Name);
            var pakName = "Quartermaster_" + safeName + "_P.pak";
            var outDir = !string.IsNullOrEmpty(OutputDir) ? OutputDir : _paths.Builds;
            var outPakPath = Path.Combine(outDir, pakName);
            var tmpDir = Path.Combine(_paths.BuildTmp, profile.Id);

            try
            {
                // Wipe the temp dir before patching: a stale tree from a
                // previous run could otherwise leak files into the new pak.
                if (Directory.Exists(tmpDir)) Directory.Delete(tmpDir, true);

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

                int totalWritten = patchResult.Written + (lootResult != null ? lootResult.Written : 0);
                double pickupMultiplier = ResolvePickupMultiplier(profile);
                bool pickupActive = pickupMultiplier > 0.0 && Math.Abs(pickupMultiplier - 1.0) > 1e-9;
                if (totalWritten == 0 && !pickupActive)
                {
                    throw new InvalidOperationException(
                        "Profile produces no changes -- nothing to pack. "
                        + "Adjust globals or add per-item / per-loot-table overrides.");
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
                    pakResult = builder.Build(tmpDir, outPakPath, overwrite: true);
                    pakPath = outPakPath;

                    LogLine("Pak built: " + outPakPath
                            + " (" + Math.Round(pakResult.SizeBytes / 1024.0, 1) + " KB, "
                            + pakResult.FileCount + " files)");
                }
                else
                {
                    LogLine("No item / loot changes -- main pak skipped (pickup-radius-only build).");
                }

                PickupTripletResult pickupResult = null;
                if (pickupActive)
                {
                    pickupResult = BuildPickupTriplet(profile, safeName, outDir, tmpDir, pickupMultiplier);
                }

                return new BuildPipelineResult
                {
                    Profile = profile,
                    PatchResult = patchResult,
                    LootPatchResult = lootResult,
                    PakResult = pakResult,
                    PakPath = pakPath,
                    PickupResult = pickupResult,
                    PickupMultiplier = pickupActive ? (double?)pickupMultiplier : null,
                    TmpDir = tmpDir,
                    Success = true,
                };
            }
            finally
            {
                if (!keepTemp && Directory.Exists(tmpDir))
                {
                    try { Directory.Delete(tmpDir, true); }
                    catch (Exception ex)
                    {
                        // Failure to clean up is annoying but not fatal --
                        // surface it in the log so the user can clean by hand.
                        LogLine("Warning: temp dir cleanup failed: " + ex.Message);
                    }
                }
            }
        }

        // Builds the IoStore triplet for the pickup-radius mod and writes
        // it next to the main pak as Quartermaster_<name>_PickupRadius_P.{pak,ucas,utoc}.
        // Surfaces a clear error if the GUI didn't supply a paks-dir
        // locator -- a CLI build with pickup-radius enabled is currently
        // unsupported (CLI doesn't talk to the Steam install).
        PickupTripletResult BuildPickupTriplet(
            Profile profile, string safeName, string outDir,
            string tmpDir, double multiplier)
        {
            if (GamePaksDirProvider == null)
            {
                throw new InvalidOperationException(
                    "Profile requests the pickup-radius mod but no GamePaksDirProvider is wired up. "
                    + "This is a build-host configuration error -- only the GUI build path "
                    + "can locate the live game's Paks directory.");
            }
            var gamePaksDir = GamePaksDirProvider();
            if (string.IsNullOrEmpty(gamePaksDir) || !Directory.Exists(gamePaksDir))
            {
                throw new InvalidOperationException(
                    "Pickup-radius patch needs the live game's Paks directory but the locator "
                    + "returned an invalid path: " + (gamePaksDir ?? "<null>"));
            }
            var usmapPath = UsmapLocator.Find(_paths.ModRoot);

            LogLine("Resolving retoc.exe...");
            _retocResolver.Log = Log;
            var retocExe = _retocResolver.Resolve();

            Directory.CreateDirectory(outDir);
            var pickupBaseName = "Quartermaster_" + safeName + "_PickupRadius_P.pak";
            var outPakPath = Path.Combine(outDir, pickupBaseName);
            var pickupTmp = Path.Combine(tmpDir, "_pickup");
            float magnetRadius = (float)(VanillaMagnetRadius * multiplier);

            LogLine("Building pickup-radius triplet (multiplier=" + multiplier
                    + ", MagnetRadius=" + magnetRadius + "cm) -> " + outPakPath);

            var triplet = new PickupTripletBuilder { Log = Log };
            var result = triplet.Build(new PickupTripletRequest
            {
                RetocExe = retocExe,
                GamePaksDir = gamePaksDir,
                UsmapPath = usmapPath,
                OutputPakPath = outPakPath,
                MagnetRadius = magnetRadius,
                TempDir = pickupTmp,
                Overwrite = true,
            });

            LogLine("Pickup triplet written: "
                    + Path.GetFileName(result.PakPath) + " ("
                    + result.PakSize + " B) + "
                    + Path.GetFileName(result.UcasPath) + " ("
                    + result.UcasSize + " B) + "
                    + Path.GetFileName(result.UtocPath) + " ("
                    + result.UtocSize + " B)");
            return result;
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

        // True when the profile actually configures the loot domain --
        // either via a per-bucket multiplier or per-LT override. Lets the
        // pipeline skip the loot patch step entirely for stack-only
        // profiles (e.g. all 11 builtins).
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
        public PakBuildResult PakResult;          // null if pickup-only build (no item/loot changes)
        public string PakPath;                    // null if pickup-only build
        // The freshly built pickup-radius IoStore triplet, or null if the
        // profile didn't request a pickup mod (or set multiplier == 1.0).
        public PickupTripletResult PickupResult;
        // The user-facing scalar that produced the triplet (e.g. 2.0,
        // 1.5, ...). null when no pickup triplet was built.
        public double? PickupMultiplier;
        public string TmpDir;
        public bool Success;
    }
}
