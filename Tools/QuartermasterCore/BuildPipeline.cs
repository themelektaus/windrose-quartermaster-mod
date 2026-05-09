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
    public sealed class BuildPipeline
    {
        readonly WindrosePaths _paths;
        readonly StackPatcher _patcher;
        readonly LootPatcher _lootPatcher;
        readonly RepakResolver _repakResolver;

        public Action<string> Log;

        // When set, overrides the default ${ModRoot}/Builds output target.
        // The GUI sets this to Windrose's ~mods/ folder so a successful
        // build lands directly in the location the engine reads from. CLI
        // smoke tests leave this null so they keep landing in Builds/ and
        // never touch the live game install.
        public string OutputDir;

        // Optional source of the pre-baked Pickup-Radius mod triplet
        // (.pak/.ucas/.utoc). Called with one of "pak", "ucas", "utoc" --
        // returns a readable Stream containing that file's bytes, or null if
        // the asset is unavailable. When null, profiles asking for the
        // pickup-radius mod can't be built (the pipeline raises a clear
        // error). The Web layer wires this to the embedded resources in
        // Quartermaster.Web.dll; the CLI leaves it null since CLI smoke
        // tests don't need the Blueprint patch.
        public Func<string, Stream> PickupRadiusAssetProvider;

        public BuildPipeline(WindrosePaths paths)
        {
            if (paths == null) throw new ArgumentNullException("paths");
            _paths = paths;
            _patcher = new StackPatcher();
            _lootPatcher = new LootPatcher();
            _repakResolver = new RepakResolver(paths.ModRoot);
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
                bool pickupActive = profile.Globals != null
                                    && profile.Globals.PickupRadius != null
                                    && profile.Globals.PickupRadius.Doubled == true;
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

                string pickupPak = null, pickupUcas = null, pickupUtoc = null;
                if (pickupActive)
                {
                    Directory.CreateDirectory(outDir);
                    var pickupBase = Path.Combine(outDir,
                        "Quartermaster_" + safeName + "_PickupRadius_P");
                    pickupPak  = WritePickupAsset(pickupBase + ".pak",  "pak");
                    pickupUcas = WritePickupAsset(pickupBase + ".ucas", "ucas");
                    pickupUtoc = WritePickupAsset(pickupBase + ".utoc", "utoc");
                    LogLine("Pickup-radius triplet written next to the main pak");
                }

                return new BuildPipelineResult
                {
                    Profile = profile,
                    PatchResult = patchResult,
                    LootPatchResult = lootResult,
                    PakResult = pakResult,
                    PakPath = pakPath,
                    PickupPakPath = pickupPak,
                    PickupUcasPath = pickupUcas,
                    PickupUtocPath = pickupUtoc,
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

        // Streams the named extension ("pak"/"ucas"/"utoc") from the
        // injected provider and writes it to disk. Throws a clear error if
        // the provider isn't wired up at all (= GUI didn't pass one) or
        // doesn't carry the asset (= mismatched ship).
        string WritePickupAsset(string outPath, string extension)
        {
            if (PickupRadiusAssetProvider == null)
            {
                throw new InvalidOperationException(
                    "Profile requests the pickup-radius mod but no asset provider is wired up. "
                    + "This is a build-host configuration error -- only the GUI build path supports it.");
            }
            using (var src = PickupRadiusAssetProvider(extension))
            {
                if (src == null)
                {
                    throw new InvalidOperationException(
                        "Pickup-radius asset provider returned null for extension '" + extension + "'.");
                }
                using (var dst = File.Create(outPath))
                {
                    src.CopyTo(dst);
                }
            }
            return outPath;
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
        // Set when the profile asked for the pickup-radius patch. The
        // engine needs all three files together (Pak1 stub + IoStore data
        // + IoStore directory); ModsEndpoint groups them as one entry.
        public string PickupPakPath;
        public string PickupUcasPath;
        public string PickupUtocPath;
        public string TmpDir;
        public bool Success;
    }
}
