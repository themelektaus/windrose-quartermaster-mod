using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UAssetAPI;
using UAssetAPI.UnrealTypes;
using UAssetAPI.Unversioned;
using UAssetAPI.ExportTypes;
using UAssetAPI.PropertyTypes.Objects;

namespace Windrose.Quartermaster.Core
{
    // "Better Crop Overlap": shrinks the planting collision footprint of every
    // seed/crop so they can be placed closer together (overlapping).
    //
    // Mechanism (derived from VANILLA, generalises the reference mod's effect):
    // each plantable crop is a BP_Farming_* actor whose CapsuleComponent carries
    // a CapsuleRadius float (the horizontal footprint that blocks placement when
    // two crops would intersect). The reference mod flattened every crop to a
    // fixed radius; we instead apply a single MULTIPLIER (< 1.0) to the freshly
    // extracted vanilla CapsuleRadius, so each crop keeps its relative size and
    // there is no drift if the game retunes the base radii. CapsuleHalfHeight
    // (the vertical extent) is left untouched - only the horizontal radius gates
    // crop-to-crop overlap.
    //
    // The crop actors live alongside non-plantable "Dummy"/"Stage" growth-visual
    // variants that do NOT serialise a CapsuleRadius. We detect the plantable
    // ones precisely by "has a CapsuleRadius FloatProperty" (new crops are
    // covered automatically) and PRUNE the rest from the staging tree so the
    // packed triplet ships only the patched assets - never an unmodified actor
    // that would needlessly override vanilla.
    public sealed class CropOverlapPatcher
    {
        // Path-substring filter for `retoc to-legacy`: pulls only the farming
        // actor blueprints (the DataAssets live under .../BuildingFarming, a
        // different path, so they are not extracted).
        public const string AssetFilter = "Building/Actors/Farming";

        // Staging subtree we own: only files beneath here are patched/pruned.
        static readonly string FarmingActorsDir =
            Path.Combine("R5", "Content", "Gameplay", "Building", "Actors", "Farming");

        const string CapsuleRadiusProperty = "CapsuleRadius";

        public Action<string> Log;

        // Scales CapsuleRadius on every plantable crop in the staging tree by
        // `multiplier`, then deletes the non-plantable farming actors so only the
        // patched assets get packed. `stagingDir` is the composite legacy root.
        public CropOverlapPatchResult Patch(string stagingDir, string usmapPath, double multiplier)
        {
            if (string.IsNullOrEmpty(stagingDir)) throw new ArgumentNullException("stagingDir");
            if (string.IsNullOrEmpty(usmapPath)) throw new ArgumentNullException("usmapPath");
            if (!File.Exists(usmapPath))
                throw new FileNotFoundException("Usmap not found: " + usmapPath);
            if (!(multiplier > 0.0))
                throw new ArgumentOutOfRangeException("multiplier", "Crop overlap multiplier must be > 0.");

            var farmingRoot = Path.Combine(stagingDir, FarmingActorsDir);
            if (!Directory.Exists(farmingRoot))
                throw new InvalidOperationException(
                    "Crop Overlap: expected farming actors under " + farmingRoot
                    + " after retoc to-legacy, but the folder is missing - the game "
                    + "container may have moved them (filter '" + AssetFilter + "').");

            var mappings = new Usmap(usmapPath);
            int cropsPatched = 0;
            int valuesScaled = 0;
            int pruned = 0;

            foreach (var assetPath in Directory.GetFiles(
                         farmingRoot, "BP_Farming_*.uasset", SearchOption.AllDirectories))
            {
                int scaled = ScaleAsset(assetPath, mappings, multiplier);
                if (scaled > 0)
                {
                    cropsPatched++;
                    valuesScaled += scaled;
                }
                else
                {
                    // No planting capsule (Dummy/Stage growth-visual variant):
                    // drop it so we don't ship an unmodified vanilla override.
                    DeleteAssetPair(assetPath);
                    pruned++;
                }
            }

            if (valuesScaled == 0)
                throw new InvalidOperationException(
                    "Crop Overlap: found no '" + CapsuleRadiusProperty + "' float on any "
                    + "BP_Farming actor under " + farmingRoot + " - the crop layout changed; "
                    + "the patch was not applied to avoid shipping wrong assets.");

            LogLine("Crop Overlap: scaled " + valuesScaled + " CapsuleRadius value(s) across "
                    + cropsPatched + " crop(s) by " + multiplier.ToString("0.0##") + "x (from vanilla); "
                    + "pruned " + pruned + " non-plantable variant(s)");
            return new CropOverlapPatchResult
            {
                CropsPatched = cropsPatched,
                ValuesScaled = valuesScaled,
                VariantsPruned = pruned,
            };
        }

        // Multiplies every CapsuleRadius float in the asset by `multiplier` and
        // rewrites it. Returns the number of scaled floats (0 = not a plantable crop).
        int ScaleAsset(string assetPath, Usmap mappings, double multiplier)
        {
            var asset = new UAsset(assetPath, UAssetIo.Ue, mappings);

            int scaled = 0;
            foreach (var exp in asset.Exports.OfType<NormalExport>())
                foreach (var p in exp.Data)
                    if (p is FloatPropertyData fp
                        && p.Name != null && p.Name.ToString() == CapsuleRadiusProperty)
                    {
                        fp.Value = (float)(fp.Value * multiplier);
                        scaled++;
                    }

            if (scaled > 0)
            {
                asset.Write(assetPath);
                LogLine("Crop Overlap: " + Path.GetFileNameWithoutExtension(assetPath)
                        + " - scaled " + scaled + " CapsuleRadius value(s)");
            }
            return scaled;
        }

        static void DeleteAssetPair(string assetPath)
        {
            if (File.Exists(assetPath)) File.Delete(assetPath);
            var uexp = Path.ChangeExtension(assetPath, ".uexp");
            if (File.Exists(uexp)) File.Delete(uexp);
        }

        void LogLine(string msg)
        {
            if (Log != null) Log(msg);
        }
    }

    public sealed class CropOverlapPatchResult
    {
        public int CropsPatched;
        public int ValuesScaled;
        public int VariantsPruned;
    }
}
