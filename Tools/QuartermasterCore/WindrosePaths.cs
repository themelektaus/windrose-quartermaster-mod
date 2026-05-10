using System;
using System.IO;

namespace Windrose.Quartermaster.Core
{
    // Resolves the standard Windrose mod-folder layout from a known root.
    // The root is wherever the .ps1 build scripts (and now the GUI) live --
    // i.e. the directory that contains Sources/, Builds/, Profiles/ etc.
    public sealed class WindrosePaths
    {
        public string ModRoot;
        public string Sources;
        public string Vanilla;
        public string VanillaInventoryItems;
        public string VanillaLootTables;
        public string VanillaBuildingLimits;
        public string Builds;
        public string Profiles;
        public string BuildTmp;
        public string Tools;
        // Folder containing reference mods we adopt 1:1 (.pak/.ucas/.utoc
        // triplets). Currently only used for BetterStructureSupport, whose
        // 787 patched DA_BI assets we extract via retoc to-legacy and merge
        // into our composite IoStore output. Reference mods are kept
        // verbatim because vanilla DataAssets cannot be parsed by UAssetAPI
        // (RawExport fallback); the mod-cooked variants serialize their
        // properties in full and re-pack cleanly.
        public string References;

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
            return new WindrosePaths
            {
                ModRoot = modRoot,
                Sources = Path.Combine(modRoot, "Sources"),
                Vanilla = vanilla,
                VanillaInventoryItems = vanillaInv,
                VanillaLootTables = vanillaLoot,
                VanillaBuildingLimits = vanillaBuildLimits,
                Builds = Path.Combine(modRoot, "Builds"),
                Profiles = Path.Combine(modRoot, "Profiles"),
                BuildTmp = Path.Combine(modRoot, ".build-tmp"),
                Tools = Path.Combine(modRoot, "Tools"),
                References = Path.Combine(modRoot, "References"),
            };
        }
    }
}
