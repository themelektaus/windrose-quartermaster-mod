using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using UAssetAPI;
using UAssetAPI.UnrealTypes;
using UAssetAPI.PropertyTypes.Objects;
using UAssetAPI.PropertyTypes.Structs;
using UAssetAPI.ExportTypes;
using UAssetAPI.Unversioned;

namespace Windrose.Quartermaster.Core
{
    // Patches the AimingData.ReloadTime FloatProperty on every BatteryData
    // entry of every ship's BatteryManagerParams DataAsset, multiplying it
    // by a user-supplied scalar < 1.0 to shorten cannon reload times.
    //
    // The property structure is deeper than the other cooldown patches:
    //
    //   Default__DA_BatteryManagerParams_*  (NormalExport, R5BatteryManagerData)
    //   + BatteryDataArray (ArrayProperty of R5BatteryData)
    //     [* per battery, typically Port + Starboard for a Cutter/Brig/...]
    //     + AimingData (StructProperty, R5AimingData)
    //       + ReloadTime (FloatProperty, seconds; vanilla 10)
    //
    // All batteries on a given ship get the SAME multiplier. The patcher
    // does NOT touch the ShotDelay inside the per-caliber ammo params
    // (R5CannonAmmoParams.ProjectileData.LogicData.LauncherParams.ShotDelay) -
    // that's the small inter-shot pause within a salvo, not the cooldown
    // between salvos. Touching ShotDelay would only matter for rapid-fire
    // builds and is out of scope here.
    //
    // Workflow context (mirrors PickaxeRangePatcher):
    //   game IoStore (.ucas)
    //     -> retoc to-legacy   (Zen package -> Legacy .uasset+.uexp)
    //     -> THIS CLASS        (multiply ReloadTime on every battery)
    //     -> retoc to-zen      (Legacy -> IoStore triplet)
    public sealed class ShipCannonPatcher
    {
        public const double MinMultiplier = 0.05;
        public const double MaxMultiplier = 1.0;

        public const string BatteryDataArrayProp = "BatteryDataArray";
        public const string AimingDataProp       = "AimingData";
        public const string ReloadTimeProp       = "ReloadTime";

        // Filename stem -> virtual asset path. Covers every shipping hull
        // variant with a BatteryManagerParams in 5.6.
        public static readonly Dictionary<string, string> HullAssets =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                { "DA_BatteryManagerParams_Cutter_01",
                  "R5/Content/Gameplay/Water/Character/DamageModelContent/Ships/CannonParams/Cutter_01/DA_BatteryManagerParams_Cutter_01.uasset" },
                { "DA_BatteryManagerParams_Cutter_Black_01",
                  "R5/Content/Gameplay/Water/Character/DamageModelContent/Ships/CannonParams/Cutter_Black_01/DA_BatteryManagerParams_Cutter_Black_01.uasset" },
                { "DA_BatteryManagerParams_Brig_01",
                  "R5/Content/Gameplay/Water/Character/DamageModelContent/Ships/CannonParams/Brig_01/DA_BatteryManagerParams_Brig_01.uasset" },
                { "DA_BatteryManagerParams_Frigate_01",
                  "R5/Content/Gameplay/Water/Character/DamageModelContent/Ships/CannonParams/Frigate_01/DA_BatteryManagerParams_Frigate_01.uasset" },
                { "DA_BatteryManagerParams_Frigate_TheHunchbackPiligrim",
                  "R5/Content/Gameplay/Water/Character/DamageModelContent/Ships/CannonParams/Frigate_01/DA_BatteryManagerParams_Frigate_TheHunchbackPiligrim.uasset" },
                { "DA_BatteryManagerParams_Ketch",
                  "R5/Content/Gameplay/Water/Character/DamageModelContent/Ships/CannonParams/Ketch/DA_BatteryManagerParams_Ketch.uasset" },
                { "DA_BatteryManagerParams_Cutter_01_Aggressive",
                  "R5/Content/Gameplay/Water/Character/DamageModelContent/Ships/CannonParams/PvE/Cutter_01_Aggressive/DA_BatteryManagerParams_Cutter_01_Aggressive.uasset" },
                { "DA_BatteryManagerParams_Cutter_01_Passive",
                  "R5/Content/Gameplay/Water/Character/DamageModelContent/Ships/CannonParams/PvE/Cutter_01_Passive/DA_BatteryManagerParams_Cutter_01_Passive.uasset" },
            };

        public Action<string> Log;

        public ShipCannonPatchResult Patch(
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

            NormalExport target = null;
            int targetIndex = -1;
            for (int i = 0; i < asset.Exports.Count; i++)
            {
                if (asset.Exports[i] is NormalExport ne)
                {
                    target = ne;
                    targetIndex = i;
                    break;
                }
            }
            if (target == null)
            {
                throw new InvalidOperationException(
                    "No NormalExport found in " + inputAssetPath
                    + " - expected an R5BatteryManagerData DataAsset.");
            }

            var arrayName = FName.FromString(asset, BatteryDataArrayProp);
            var batteryArray = target.Data.OfType<ArrayPropertyData>()
                .FirstOrDefault(p => p.Name == arrayName);
            if (batteryArray == null || batteryArray.Value == null)
            {
                throw new InvalidOperationException(
                    "No BatteryDataArray ArrayProperty on " + target.ObjectName
                    + " in " + inputAssetPath + ".");
            }

            var aimingName = FName.FromString(asset, AimingDataProp);
            var reloadName = FName.FromString(asset, ReloadTimeProp);

            int batteries = 0;
            int patched = 0;
            float firstVanilla = 0f;
            float firstEffective = 0f;
            foreach (var item in batteryArray.Value)
            {
                var battery = item as StructPropertyData;
                if (battery == null || battery.Value == null) continue;
                batteries++;

                var aiming = battery.Value.OfType<StructPropertyData>()
                    .FirstOrDefault(p => p.Name == aimingName);
                if (aiming == null || aiming.Value == null) continue;

                var reloadProp = aiming.Value.OfType<FloatPropertyData>()
                    .FirstOrDefault(p => p.Name == reloadName);
                if (reloadProp == null) continue;

                float vanillaValue = reloadProp.Value;
                float newValue = (float)(vanillaValue * multiplier);
                reloadProp.Value = newValue;
                if (patched == 0)
                {
                    firstVanilla = vanillaValue;
                    firstEffective = newValue;
                }
                patched++;
            }

            if (patched == 0)
            {
                throw new InvalidOperationException(
                    "BatteryDataArray on " + target.ObjectName + " in "
                    + inputAssetPath + " had " + batteries
                    + " entries, but none carried AimingData.ReloadTime.");
            }

            LogLine("Updated " + patched + "/" + batteries
                + " battery AimingData.ReloadTime values: "
                + firstVanilla.ToString("0.0000", CultureInfo.InvariantCulture)
                + " -> " + firstEffective.ToString("0.0000", CultureInfo.InvariantCulture)
                + " (multiplier=" + multiplier.ToString("0.##", CultureInfo.InvariantCulture) + ")");

            LogLine("Writing: " + outputAssetPath);
            asset.Write(outputAssetPath);

            return new ShipCannonPatchResult
            {
                AssetStem = Path.GetFileNameWithoutExtension(inputAssetPath),
                ExportIndex = targetIndex,
                Multiplier = multiplier,
                BatteryCount = batteries,
                PatchedCount = patched,
                VanillaReloadTime = firstVanilla,
                EffectiveReloadTime = firstEffective,
            };
        }

        void LogLine(string msg)
        {
            if (Log != null) Log(msg);
        }
    }

    public sealed class ShipCannonPatchResult
    {
        public string AssetStem;
        public int ExportIndex;
        public double Multiplier;
        public int BatteryCount;
        public int PatchedCount;
        // Sample values from the first patched battery; all batteries on
        // a given ship share the same vanilla ReloadTime in 5.6, so a
        // single sample is representative.
        public float VanillaReloadTime;
        public float EffectiveReloadTime;
    }
}
