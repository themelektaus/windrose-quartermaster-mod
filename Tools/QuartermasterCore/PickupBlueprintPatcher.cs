using System;
using System.IO;
using System.Linq;
using UAssetAPI;
using UAssetAPI.UnrealTypes;
using UAssetAPI.PropertyTypes.Objects;
using UAssetAPI.ExportTypes;
using UAssetAPI.Unversioned;

namespace Windrose.Quartermaster.Core
{
    // Patches a UE5 Legacy .uasset+.uexp pair (the GA_Loot_AutoPickup
    // Blueprint CDO after retoc to-legacy conversion) by adding (or
    // updating) a serialized "MagnetRadius" FloatProperty on the
    // ClassDefaultObject. The vanilla CDO ships with MagnetRadius coming
    // from the C++ class default (NOT serialized in the export bytes), so
    // for any non-default value we have to ADD the property -- the engine
    // then reads our value at CDO-load time and overrides the C++ default.
    //
    // Workflow context:
    //   game IoStore (.ucas)
    //     -> retoc to-legacy   (Zen package -> Legacy .uasset+.uexp)
    //     -> THIS CLASS        (add FloatProperty MagnetRadius=<value>)
    //     -> retoc to-zen      (Legacy -> IoStore triplet .pak/.ucas/.utoc)
    //
    // The path gymnastics (in/out paths, usmap loader) live here -- the
    // higher-level orchestrator (BuildPipeline.BuildIoStoreComposite)
    // just calls Patch() with the in/out paths and the magnet-radius value.
    public sealed class PickupBlueprintPatcher
    {
        // Virtual asset path within the game's content tree. Hardcoded
        // because there's exactly one Blueprint with the MagnetRadius
        // property in Windrose 5.6 -- if the engine ever moves it, the
        // composite builder fails fast with a clear "filter didn't match"
        // diagnostic and we update this constant.
        public const string AssetVirtualPath =
            "R5/Content/Gameplay/Character/Player/GameplayAbilities/Loot/GA_Loot_AutoPickup.uasset";

        // Filename-stem filter passed to retoc to-legacy --filter. Just
        // the basename (no path component, no extension); retoc walks
        // the IoStore container tree and matches anywhere.
        public const string AssetFilterStem = "GA_Loot_AutoPickup";

        public Action<string> Log;

        // Patches the asset in-place when inputAssetPath == outputAssetPath,
        // or copies + patches when they differ. usmapPath is required (UE5
        // unversioned properties need the .usmap to know the layout).
        // Returns the actual property count after the patch (so the caller
        // can verify "we now have N+1 properties").
        public PickupBlueprintPatchResult Patch(
            string inputAssetPath, string outputAssetPath,
            string usmapPath, float magnetRadius)
        {
            if (string.IsNullOrEmpty(inputAssetPath))
                throw new ArgumentNullException("inputAssetPath");
            if (string.IsNullOrEmpty(outputAssetPath))
                throw new ArgumentNullException("outputAssetPath");
            if (string.IsNullOrEmpty(usmapPath))
                throw new ArgumentNullException("usmapPath");
            if (!File.Exists(inputAssetPath))
                throw new FileNotFoundException("Legacy uasset not found: " + inputAssetPath);
            if (!File.Exists(usmapPath))
                throw new FileNotFoundException("Usmap mappings not found: " + usmapPath);

            LogLine("Loading usmap: " + usmapPath);
            var mappings = new Usmap(usmapPath);

            LogLine("Loading uasset: " + inputAssetPath);
            var asset = new UAsset(inputAssetPath, EngineVersion.VER_UE5_6, mappings);

            // Find the ClassDefaultObject export. Blueprint cooking always
            // emits a "Default__<ClassName>_C" NormalExport alongside the
            // ClassExport itself; we want the former (the value carrier).
            // Fallback: first NormalExport.
            NormalExport cdo = null;
            int cdoIndex = -1;
            for (int i = 0; i < asset.Exports.Count; i++)
            {
                var ne = asset.Exports[i] as NormalExport;
                if (ne == null) continue;
                if (ne.ObjectName.ToString().StartsWith("Default__", StringComparison.Ordinal))
                {
                    cdo = ne;
                    cdoIndex = i;
                    break;
                }
            }
            if (cdo == null)
            {
                for (int i = 0; i < asset.Exports.Count; i++)
                {
                    var ne = asset.Exports[i] as NormalExport;
                    if (ne == null) continue;
                    cdo = ne;
                    cdoIndex = i;
                    break;
                }
            }
            if (cdo == null)
            {
                throw new InvalidOperationException(
                    "No NormalExport found in " + inputAssetPath
                    + " -- expected a Blueprint CDO export to patch.");
            }

            LogLine("CDO export [" + cdoIndex + "]: " + cdo.ObjectName
                    + " (existing properties: " + cdo.Data.Count + ")");

            // Set or add the property. FName.FromString registers the name
            // in the asset's NameMap if it isn't there yet (which is the
            // common case here -- vanilla CDO doesn't carry MagnetRadius).
            var magnetName = FName.FromString(asset, "MagnetRadius");
            var existing = cdo.Data.FirstOrDefault(p => p.Name == magnetName);
            bool added;
            float oldValue = 0f;
            if (existing is FloatPropertyData existingFloat)
            {
                oldValue = existingFloat.Value;
                existingFloat.Value = magnetRadius;
                added = false;
                LogLine("Updated MagnetRadius: " + oldValue + " -> " + magnetRadius);
            }
            else
            {
                cdo.Data.Add(new FloatPropertyData(magnetName) { Value = magnetRadius });
                added = true;
                LogLine("Added MagnetRadius FloatProperty = " + magnetRadius);
            }

            LogLine("Writing: " + outputAssetPath);
            asset.Write(outputAssetPath);

            return new PickupBlueprintPatchResult
            {
                CdoIndex = cdoIndex,
                CdoName = cdo.ObjectName.ToString(),
                Added = added,
                OldMagnetRadius = added ? (float?)null : oldValue,
                NewMagnetRadius = magnetRadius,
                FinalPropertyCount = cdo.Data.Count,
            };
        }

        void LogLine(string msg)
        {
            if (Log != null) Log(msg);
        }
    }

    public sealed class PickupBlueprintPatchResult
    {
        public int CdoIndex;
        public string CdoName;
        public bool Added;            // true = property newly added; false = pre-existing value updated
        public float? OldMagnetRadius;// null if the property was added (no prior value)
        public float NewMagnetRadius;
        public int FinalPropertyCount;
    }

    // Per-feature triplet result -- BuildPipeline emits one of these for
    // the pickup-radius portion of the IoStore composite. Even though all
    // IoStore content now ships in ONE shared triplet (sharedBaseName.*),
    // the result keeps separate UCAS/UTOC paths for the pickup feature so
    // the build response can attribute file sizes / paths back to the
    // user-visible "Pickup" axis. PakPath is null when the main Pak1 is
    // also being built (because then the same path on disk is owned by
    // repak's Pak1 output, not the retoc IoStore stub).
    public sealed class PickupTripletResult
    {
        public string PakPath;
        public string UcasPath;
        public string UtocPath;
        public long PakSize;
        public long UcasSize;
        public long UtocSize;
        public float MagnetRadius;
        public PickupBlueprintPatchResult PatchResult;
        public string LegacyTempDir;
    }
}
