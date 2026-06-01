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
    public sealed class PickupBlueprintPatcher
    {
        public const string AssetVirtualPath =
            "R5/Content/Gameplay/Character/Player/GameplayAbilities/Loot/GA_Loot_AutoPickup.uasset";

        public const string AssetFilterStem = "GA_Loot_AutoPickup";

        public Action<string> Log;

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
            var asset = new UAsset(inputAssetPath, UAssetIo.Ue, mappings);

            // The CDO is the "Default__<ClassName>_C" NormalExport; fall back to the first NormalExport.
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
                    + " - expected a Blueprint CDO export to patch.");
            }

            LogLine("CDO export [" + cdoIndex + "]: " + cdo.ObjectName
                    + " (existing properties: " + cdo.Data.Count + ")");

            // Vanilla CDO does not serialize MagnetRadius (uses the C++ class default), so the common path is ADD.
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
        public bool Added;
        public float? OldMagnetRadius; // null when Added (no prior value)
        public float NewMagnetRadius;
        public int FinalPropertyCount;
    }

    public sealed class PickupTripletResult
    {
        // PakPath is null when the main Pak1 is also being built (repak owns that on-disk path, not retoc).
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
