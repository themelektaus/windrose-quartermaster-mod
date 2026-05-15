using System;
using System.IO;
using System.Linq;

namespace Windrose.Quartermaster.Core
{
    // Single source of truth for "which vanilla artifacts does Quartermaster
    // need extracted on disk before any patcher can run".
    //
    // Why a manifest:
    //   Every time the app grew a new feature that depended on a new pak
    //   subtree (Buyers needed RecipeLists+Recipes; Item Creator needed the
    //   InventoryItems.csv string-table; building stability needs the
    //   BuildingLimits DataAssets), we had to edit four places by hand
    //   (WindroseGameSecrets constant, WindrosePaths property, SetupRunner
    //   probe + ready-flag, VanillaDumper unpack call). Easy to forget one.
    //
    //   The manifest collapses those four places into one declarative
    //   entry. Adding a new required source is now: append one
    //   VanillaSourceManifestEntry, the dumper and the probe both pick it
    //   up automatically, and the setup overlay shows it as a new check
    //   row without any frontend changes.
    //
    // Backward compat: existing users whose Sources/Vanilla/ predates a
    // newly-added manifest entry will probe as not-ready and get the
    // setup overlay automatically on next app boot (because SetupStatus.
    // IsReady aggregates over every manifest entry). They click "Run setup"
    // and the dumper tops up just the missing subtrees.
    public sealed class VanillaSourceManifestEntry
    {
        // Stable string id used in JSON (frontend keys off this for icons
        // / per-row styling). Kept lowerCamelCase to match the rest of the
        // setup-status JSON shape.
        public string Key;

        // Human-readable label shown in the setup overlay.
        public string Label;

        // The "what is this for?" hint shown under the label in the overlay.
        // Kept short - the overlay is dense and the user scans it.
        public string Description;

        // Pak-internal include-prefix passed to repak's `-i` flag. For
        // single-file entries this is the full pak-relative path; repak's
        // prefix-match still works because the file IS its own prefix.
        public string PakIncludePath;

        // Resolver from WindrosePaths -> on-disk path the dumper writes
        // into and the probe checks. A delegate (vs. a plain string) so
        // we don't need to duplicate the path layout between this manifest
        // and WindrosePaths - the latter remains the only place that
        // knows how Sources/Vanilla/ is laid out under the mod root.
        public Func<WindrosePaths, string> DiskPath;

        // How to test "this artifact is present and looks intact". Either
        // we need at least one .json in the directory tree, or a specific
        // file must exist.
        public VanillaSourceProbeKind ProbeKind;
    }

    public enum VanillaSourceProbeKind
    {
        // Recursively scan the directory; OK if any .json file is present.
        // Suits subtree dumps where the file count grows over time and we
        // just want a "is the extraction non-empty" smoke check.
        DirectoryWithJsonFiles,

        // The exact file must exist. Suits single-file artifacts like the
        // InventoryItems string-table CSV.
        SingleFile,
    }

    public static class VanillaSourceManifest
    {
        // The canonical list of required vanilla sources. Order matters
        // only for UI rendering (overlay shows rows in declaration order)
        // and for dump-step log readability - the dumper iterates in
        // this order so the user sees a stable progression.
        //
        // To add a new required source:
        //   1. Add the pak include path as a const on WindroseGameSecrets.
        //   2. Add a WindrosePaths.Vanilla<X> property + initializer.
        //   3. Append a new entry to this array below. That's it.
        //      The probe + dumper + UI pick it up automatically.
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
                Description = "R5/Content/Localization/Data/InventoryItems.csv - baseline string-table that the Item Creator extends.",
                PakIncludePath = WindroseGameSecrets.InventoryItemsCsvPath,
                DiskPath = p => p.VanillaInventoryItemsCsv,
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
        };

        // Convenience: returns true when the on-disk artifact for one
        // manifest entry passes its probe. Used by both SetupRunner.Probe()
        // and VanillaDumper (for the per-step skip-if-already-present
        // log message) so the two stay in sync.
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
