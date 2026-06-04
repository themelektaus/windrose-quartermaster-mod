using System;
using System.Collections.Generic;
using System.IO;

namespace Windrose.Quartermaster.Core
{
    // "Land Fast Travel" reference mod: lets the fast-travel bell be placed inland
    // instead of only near the coast/water. Vanilla restricts placement via the
    // R5BuildingItem.CoastlineDistanceRange property, which the two fast-travel-bell
    // DataAssets do NOT serialize (they inherit the restrictive class-default). The
    // mod overrides both assets, adding CoastlineDistanceRange = [-1e6, +1e6]
    // (Exclusive bounds), which makes the coast-proximity gate always pass.
    //
    // Why prebuilt assets instead of patching vanilla in place (like
    // BonfireRadiusPatcher): retoc to-legacy returns this DataAsset as an
    // unparseable RawExport (its trailing R5CollisionApproximation has a custom C++
    // Serialize with no FProperty schema). Because vanilla never serializes
    // CoastlineDistanceRange there is no in-place float to flip, and injecting a
    // brand-new unversioned property tag into a RawExport blob is fragile. So we
    // ship the two prebuilt override DataAssets (vanilla + the single added
    // property; every other value identical to vanilla / the class default), staged
    // into the IoStore composite and repacked by retoc to-zen alongside the other
    // patched assets. The prebuilt files live under WindrosePaths.LandFastTravelTemplateDir.
    public sealed class LandFastTravelPatcher
    {
        // retoc --filter stems for the two fast-travel-bell building-item DataAssets.
        public static readonly string[] AssetFilterStems =
        {
            "DA_BI_Utilities_FastTravel_Bell",
            "DA_BI_Utilities_FastTravelBell_02",
        };

        // Cooked virtual paths (forward-slash) of the same two DataAssets.
        public static readonly string[] AssetVirtualPaths =
        {
            "R5/Content/Gameplay/Building/BuildingUtilities/DA_BI_Utilities_FastTravel_Bell.uasset",
            "R5/Content/Gameplay/Building/BuildingUtilities/DA_BI_Utilities_FastTravelBell_02.uasset",
        };

        public Action<string> Log;

        // Replaces the (just-extracted vanilla) bell DataAssets in the IoStore
        // staging tree with the prebuilt inland-placement overrides. `templateDir`
        // holds the 4 prebuilt files (.uasset + .uexp per asset); `stagingDir` is the
        // composite legacy root that retoc to-legacy populated with the vanilla
        // bells (existence-checked here as a game-version sanity gate before we
        // overwrite them - if a game update moved the asset, the override would
        // otherwise silently target a dead path).
        public LandFastTravelPatchResult StageInto(string stagingDir, string templateDir)
        {
            if (string.IsNullOrEmpty(stagingDir)) throw new ArgumentNullException("stagingDir");
            if (string.IsNullOrEmpty(templateDir)) throw new ArgumentNullException("templateDir");
            if (!Directory.Exists(templateDir))
                throw new DirectoryNotFoundException(
                    "Land Fast Travel template dir not found: " + templateDir
                    + " - the prebuilt override DataAssets are missing from the build.");

            int replaced = 0;
            foreach (var virtualPath in AssetVirtualPaths)
            {
                var relUasset = virtualPath.Replace('/', Path.DirectorySeparatorChar);
                var relUexp   = Path.ChangeExtension(relUasset, ".uexp");
                var baseName  = Path.GetFileName(virtualPath);            // *.uasset

                var srcUasset = Path.Combine(templateDir, baseName);
                var srcUexp   = Path.Combine(templateDir, Path.ChangeExtension(baseName, ".uexp"));
                if (!File.Exists(srcUasset) || !File.Exists(srcUexp))
                    throw new FileNotFoundException(
                        "Land Fast Travel prebuilt override missing: "
                        + srcUasset + " / " + srcUexp);

                var dstUasset = Path.Combine(stagingDir, relUasset);
                var dstUexp   = Path.Combine(stagingDir, relUexp);

                // Sanity gate: the vanilla asset must have extracted here first.
                if (!File.Exists(dstUasset))
                    throw new InvalidOperationException(
                        "Land Fast Travel: expected the vanilla bell asset at " + dstUasset
                        + " after retoc to-legacy, but it is missing - the game container "
                        + "may have moved the asset (filter '"
                        + Path.GetFileNameWithoutExtension(baseName)
                        + "'); the prebuilt override cannot be placed.");

                Directory.CreateDirectory(Path.GetDirectoryName(dstUasset));
                File.Copy(srcUasset, dstUasset, true);
                File.Copy(srcUexp, dstUexp, true);
                replaced++;
                LogLine("LandFastTravel: inland-placement override staged for "
                        + Path.GetFileNameWithoutExtension(baseName));
            }

            return new LandFastTravelPatchResult { AssetsReplaced = replaced };
        }

        void LogLine(string msg)
        {
            if (Log != null) Log(msg);
        }
    }

    public sealed class LandFastTravelPatchResult
    {
        public int AssetsReplaced;
    }
}
