using System;
using System.Globalization;
using System.IO;
using System.Linq;
using UAssetAPI;
using UAssetAPI.PropertyTypes.Objects;
using UAssetAPI.PropertyTypes.Structs;
using UAssetAPI.ExportTypes;
using UAssetAPI.Unversioned;

namespace Windrose.Quartermaster.Core
{
    // Scales the Soul Harvest [F] ability radius: the inline MaxDistance float nested in
    // DistanceData (struct R5AS_EnvironmentRequestDistanceData) on the shared TargetParams
    // DataAsset. Vanilla 500 units = 5 m (100 units = 1 m). A single asset, referenced by
    // BOTH the Base and Advanced Souldrinker tiers, so one edit covers both weapons.
    //
    // Unlike the Soul Harvest cooldown/damage (CurveTable rows in CT_Weapon_GE_Values), the
    // radius is a plain reflected FloatProperty - patched in place via UAssetAPI like
    // PickaxeRangePatcher, just one struct level deeper (inside DistanceData).
    public sealed class SoulHarvestRadiusPatcher
    {
        public const string AssetStem =
            "DA_Wpn_TwoHand_Souldrinker_Base_SoulHarvest_TargetParams";
        public const string VirtualPath =
            "R5/Content/Gameplay/ItemsLogic/Weapon/Wpn_TwoHand/Greatsword_Souldrinker_Base/SoulHarvestAbility/DA_Wpn_TwoHand_Souldrinker_Base_SoulHarvest_TargetParams.uasset";

        public const string DistanceDataPropertyName = "DistanceData";
        public const string MaxDistancePropertyName = "MaxDistance";

        // Safety backstop only ("the GUI should have clamped this"); the slider is the real limit.
        public const double MinMultiplier = 0.01;
        public const double MaxMultiplier = 10.0;

        public Action<string> Log;

        public CooldownPatchResult Patch(
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
            var asset = new UAsset(inputAssetPath, UAssetIo.Ue, mappings);

            FloatPropertyData maxDistance = null;
            int exportIndex = -1;
            for (int i = 0; i < asset.Exports.Count && maxDistance == null; i++)
            {
                if (!(asset.Exports[i] is NormalExport ne)) continue;
                foreach (var p in ne.Data)
                {
                    if (p is StructPropertyData sp
                        && string.Equals(sp.Name?.Value?.Value, DistanceDataPropertyName,
                                         StringComparison.Ordinal)
                        && sp.Value != null)
                    {
                        maxDistance = sp.Value.FirstOrDefault(c =>
                            c is FloatPropertyData
                            && string.Equals(c.Name?.Value?.Value, MaxDistancePropertyName,
                                             StringComparison.Ordinal)) as FloatPropertyData;
                        if (maxDistance != null) { exportIndex = i; break; }
                    }
                }
            }
            if (maxDistance == null)
                throw new InvalidOperationException(
                    "No " + DistanceDataPropertyName + "." + MaxDistancePropertyName
                    + " FloatProperty found in " + inputAssetPath
                    + " - the TargetParams schema may have changed (game update?); refusing to patch.");

            float vanillaValue = maxDistance.Value;
            if (float.IsNaN(vanillaValue) || float.IsInfinity(vanillaValue)
                || vanillaValue <= 0f || vanillaValue > 1000000f)
                throw new InvalidOperationException(
                    "Soul Harvest radius (" + MaxDistancePropertyName + ") value "
                    + vanillaValue.ToString(CultureInfo.InvariantCulture)
                    + " is not a plausible distance in " + inputAssetPath
                    + " - refusing to patch.");

            float newValue = (float)(vanillaValue * multiplier);
            maxDistance.Value = newValue;
            LogLine("Updated " + DistanceDataPropertyName + "." + MaxDistancePropertyName + ": "
                + vanillaValue.ToString("0.0000", CultureInfo.InvariantCulture) + " -> "
                + newValue.ToString("0.0000", CultureInfo.InvariantCulture)
                + " (multiplier=" + multiplier.ToString("0.##", CultureInfo.InvariantCulture) + ")");

            LogLine("Writing: " + outputAssetPath);
            asset.Write(outputAssetPath);

            return new CooldownPatchResult
            {
                AssetStem = Path.GetFileNameWithoutExtension(inputAssetPath),
                ExportIndex = exportIndex,
                Multiplier = multiplier,
                VanillaValue = vanillaValue,
                EffectiveValue = newValue,
                Shape = CooldownPatchShape.SoulHarvestRadius,
            };
        }

        void LogLine(string msg)
        {
            if (Log != null) Log(msg);
        }
    }
}
