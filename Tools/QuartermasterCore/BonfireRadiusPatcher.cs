using System;
using System.Globalization;
using System.IO;
using UAssetAPI;
using UAssetAPI.UnrealTypes;
using UAssetAPI.ExportTypes;
using UAssetAPI.Unversioned;

namespace Windrose.Quartermaster.Core
{
    // Edits raw bytes because retoc returns this DataAsset as an unparseable
    // RawExport: its trailing R5CollisionApproximation property has a custom
    // C++ Serialize with no FProperty schema, so UAssetAPI cannot expose the
    // two target floats as NormalExport properties.
    public sealed class BonfireRadiusPatcher
    {
        public const string AssetVirtualPath =
            "R5/Content/Gameplay/Building/BuildingUtilities/DA_BI_Utilities_BuildingCenterT01.uasset";

        public const string AssetFilterStem = "DA_BI_Utilities_BuildingCenterT01";

        public const float VanillaInfluenceRadius = 5000f;
        public const float VanillaInfluenceHeight = 3000f;

        public const int InfluenceRadiusOffset = 117;
        public const int InfluenceHeightOffset = 121;

        public const double MinMultiplier = 1.0;
        public const double MaxMultiplier = 5.0;

        public Action<string> Log;

        public BonfireRadiusPatchResult Patch(
            string inputAssetPath, string outputAssetPath,
            string usmapPath, double multiplier)
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
            if (multiplier < MinMultiplier || multiplier > MaxMultiplier)
                throw new ArgumentOutOfRangeException("multiplier",
                    "Multiplier " + multiplier + " is outside ["
                    + MinMultiplier + ", " + MaxMultiplier
                    + "] - the GUI should have clamped this.");

            LogLine("Loading usmap: " + usmapPath);
            var mappings = new Usmap(usmapPath);

            LogLine("Loading uasset: " + inputAssetPath);
            var asset = new UAsset(inputAssetPath, EngineVersion.VER_UE5_6, mappings);

            RawExport raw = null;
            int rawIdx = -1;
            for (int i = 0; i < asset.Exports.Count; i++)
            {
                if (asset.Exports[i] is RawExport r)
                {
                    raw = r;
                    rawIdx = i;
                    break;
                }
            }
            if (raw == null)
            {
                throw new InvalidOperationException(
                    "No RawExport found in " + inputAssetPath
                    + " - vanilla DA_BI_Utilities_BuildingCenterT01 ships as a"
                    + " single RawExport; UAssetAPI's parser may have changed.");
            }

            var data = raw.Data;
            if (data == null || data.Length < InfluenceHeightOffset + 4)
            {
                throw new InvalidOperationException(
                    "Asset RawExport.Data is too small ("
                    + (data == null ? 0 : data.Length)
                    + " bytes) to hold the expected InfluenceRadius+Height layout"
                    + " (needs at least " + (InfluenceHeightOffset + 4) + " bytes).");
            }

            // BitConverter is little-endian on x86/x64, matching the zen float order.
            float vanillaR = BitConverter.ToSingle(data, InfluenceRadiusOffset);
            float vanillaH = BitConverter.ToSingle(data, InfluenceHeightOffset);
            if (Math.Abs(vanillaR - VanillaInfluenceRadius) > 0.001f
                || Math.Abs(vanillaH - VanillaInfluenceHeight) > 0.001f)
            {
                throw new InvalidOperationException(
                    "Vanilla InfluenceRadius/Height bytes don't match expectation: "
                    + "got " + vanillaR.ToString(CultureInfo.InvariantCulture)
                    + " / " + vanillaH.ToString(CultureInfo.InvariantCulture)
                    + " at offsets " + InfluenceRadiusOffset
                    + " / " + InfluenceHeightOffset
                    + " (expected " + VanillaInfluenceRadius
                    + " / " + VanillaInfluenceHeight + "). "
                    + "The vanilla asset's property layout may have changed - "
                    + "re-probe with .build-tmp/bonfire-inject and update the "
                    + "BonfireRadiusPatcher offset constants.");
            }

            float newR = (float)(VanillaInfluenceRadius * multiplier);
            float newH = (float)(VanillaInfluenceHeight * multiplier);

            var rBytes = BitConverter.GetBytes(newR);
            var hBytes = BitConverter.GetBytes(newH);
            Array.Copy(rBytes, 0, data, InfluenceRadiusOffset, 4);
            Array.Copy(hBytes, 0, data, InfluenceHeightOffset, 4);

            LogLine("BonfireRadius: InfluenceRadius "
                    + vanillaR.ToString("0", CultureInfo.InvariantCulture)
                    + " -> " + newR.ToString("0", CultureInfo.InvariantCulture)
                    + ", InfluenceHeight "
                    + vanillaH.ToString("0", CultureInfo.InvariantCulture)
                    + " -> " + newH.ToString("0", CultureInfo.InvariantCulture)
                    + " (multiplier=" + multiplier.ToString("0.##", CultureInfo.InvariantCulture)
                    + ")");

            LogLine("Writing: " + outputAssetPath);
            asset.Write(outputAssetPath);

            return new BonfireRadiusPatchResult
            {
                Multiplier = multiplier,
                RawExportIndex = rawIdx,
                VanillaInfluenceRadius = vanillaR,
                VanillaInfluenceHeight = vanillaH,
                EffectiveInfluenceRadius = newR,
                EffectiveInfluenceHeight = newH,
            };
        }

        void LogLine(string msg)
        {
            if (Log != null) Log(msg);
        }
    }

    public sealed class BonfireRadiusPatchResult
    {
        public double Multiplier;
        public int RawExportIndex;
        public float VanillaInfluenceRadius;
        public float VanillaInfluenceHeight;
        public float EffectiveInfluenceRadius;
        public float EffectiveInfluenceHeight;
    }
}
