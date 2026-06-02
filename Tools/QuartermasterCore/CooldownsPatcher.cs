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
    public sealed class CooldownsPatcher
    {
        public const double MinMultiplier = 0.1;
        public const double MaxMultiplier = 3.0;

        public const string DurationMagnitudeProp     = "DurationMagnitude";
        public const string ScalableFloatMagnitudeProp = "ScalableFloatMagnitude";
        public const string ValueProp                 = "Value";
        public const string MagnitudeProp             = "Magnitude";

        public static readonly Dictionary<string, string> ElixirAssets =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                {
                    "GE_Cooldown_Elixir",
                    "R5/Content/Gameplay/ItemsLogic/Consumables/Elixir/GE_Cooldown_Elixir.uasset"
                },
            };

        public static readonly Dictionary<string, string> MedicineAssets =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                {
                    "BP_Calc_ConsCdBonus_Medicine",
                    "R5/Content/Gameplay/ItemsLogic/Consumables/Shared/ConsCdBonus/BP_Calc_ConsCdBonus_Medicine.uasset"
                },
            };

        public static readonly Dictionary<string, string> RecallAssets =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                {
                    "BP_Calc_ConsCdBonus_Recall",
                    "R5/Content/Gameplay/ItemsLogic/Consumables/Shared/ConsCdBonus/BP_Calc_ConsCdBonus_Recall.uasset"
                },
            };

        public static readonly Dictionary<string, string> ShipRepairKitAssets =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                {
                    "GE_Ship_Cooldown_RepairKit",
                    "R5/Content/Gameplay/Water/Character/Ability/Equip/ConsumableGE/GE_Ship_Cooldown_RepairKit.uasset"
                },
                {
                    "GE_Ship_Cooldown_RepairKit_Small",
                    "R5/Content/Gameplay/Water/Character/Ability/Equip/ConsumableGE/GE_Ship_Cooldown_RepairKit_Small.uasset"
                },
            };

        public static readonly Dictionary<string, string> BoarWhistleAssets =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                {
                    "GE_SpawnerCooldown",
                    "R5/Content/Gameplay/Character/Common/GameplayAbilities/UseConsumable/GE_SpawnerCooldown.uasset"
                },
            };

        public static readonly Dictionary<string, string> ShipSummonAssets =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                {
                    "GE_ShipSummon_Cooldown",
                    "R5/Content/Gameplay/Character/Player/GameplayAbilities/Summon/GE_ShipSummon_Cooldown.uasset"
                },
            };

        public Action<string> Log;

        public CooldownPatchResult PatchScalableFloatDuration(
            string inputAssetPath, string outputAssetPath,
            string usmapPath, double multiplier)
        {
            ValidateArgs(inputAssetPath, outputAssetPath, usmapPath, multiplier);

            LogLine("Loading usmap: " + usmapPath);
            var mappings = new Usmap(usmapPath);
            LogLine("Loading uasset: " + inputAssetPath);
            var asset = new UAsset(inputAssetPath, UAssetIo.Ue, mappings);

            var durationName = FName.FromString(asset, DurationMagnitudeProp);
            var (target, targetIndex, duration) = FindExportWithStruct(
                asset, durationName, inputAssetPath,
                "DurationMagnitude StructProperty",
                "expected a GameplayEffect with a duration magnitude");

            var scalableName = FName.FromString(asset, ScalableFloatMagnitudeProp);
            var scalable = duration.Value.OfType<StructPropertyData>()
                .FirstOrDefault(p => p.Name == scalableName);
            if (scalable == null || scalable.Value == null)
            {
                throw new InvalidOperationException(
                    "No ScalableFloatMagnitude struct inside DurationMagnitude on "
                    + target.ObjectName + " in " + inputAssetPath
                    + " - the GE may use a different MagnitudeCalculationType.");
            }

            var valueName = FName.FromString(asset, ValueProp);
            var valueProp = scalable.Value.OfType<FloatPropertyData>()
                .FirstOrDefault(p => p.Name == valueName);
            if (valueProp == null)
            {
                throw new InvalidOperationException(
                    "No Value FloatProperty inside ScalableFloatMagnitude on "
                    + target.ObjectName + " in " + inputAssetPath + ".");
            }

            float vanillaValue = valueProp.Value;
            float newValue = (float)(vanillaValue * multiplier);
            valueProp.Value = newValue;
            LogLine("Updated DurationMagnitude.ScalableFloatMagnitude.Value: "
                + vanillaValue.ToString("0.0000", CultureInfo.InvariantCulture)
                + " -> " + newValue.ToString("0.0000", CultureInfo.InvariantCulture)
                + " (multiplier=" + multiplier.ToString("0.##", CultureInfo.InvariantCulture) + ")");

            LogLine("Writing: " + outputAssetPath);
            asset.Write(outputAssetPath);

            return new CooldownPatchResult
            {
                AssetStem = Path.GetFileNameWithoutExtension(inputAssetPath),
                ExportIndex = targetIndex,
                Multiplier = multiplier,
                VanillaValue = vanillaValue,
                EffectiveValue = newValue,
                Shape = CooldownPatchShape.ScalableFloatDuration,
            };
        }

        public CooldownPatchResult PatchTopLevelMagnitude(
            string inputAssetPath, string outputAssetPath,
            string usmapPath, double multiplier)
        {
            ValidateArgs(inputAssetPath, outputAssetPath, usmapPath, multiplier);

            LogLine("Loading usmap: " + usmapPath);
            var mappings = new Usmap(usmapPath);
            LogLine("Loading uasset: " + inputAssetPath);
            var asset = new UAsset(inputAssetPath, UAssetIo.Ue, mappings);

            var magName = FName.FromString(asset, MagnitudeProp);
            var (_, targetIndex, magProp) = FindExportWithFloat(
                asset, magName, inputAssetPath,
                "Magnitude FloatProperty",
                "expected an R5ModMagCalc_SimpleAttributeBased BP_Calc");

            float vanillaValue = magProp.Value;
            float newValue = (float)(vanillaValue * multiplier);
            magProp.Value = newValue;
            LogLine("Updated Magnitude: "
                + vanillaValue.ToString("0.0000", CultureInfo.InvariantCulture)
                + " -> " + newValue.ToString("0.0000", CultureInfo.InvariantCulture)
                + " (multiplier=" + multiplier.ToString("0.##", CultureInfo.InvariantCulture) + ")");

            LogLine("Writing: " + outputAssetPath);
            asset.Write(outputAssetPath);

            return new CooldownPatchResult
            {
                AssetStem = Path.GetFileNameWithoutExtension(inputAssetPath),
                ExportIndex = targetIndex,
                Multiplier = multiplier,
                VanillaValue = vanillaValue,
                EffectiveValue = newValue,
                Shape = CooldownPatchShape.TopLevelMagnitude,
            };
        }

        // Locate by property presence, not export index: GameplayEffects ship component exports alongside the CDO, so the first NormalExport is often not the one we want.
        static (NormalExport target, int index, StructPropertyData prop) FindExportWithStruct(
            UAsset asset, FName propName, string inputAssetPath,
            string what, string hint)
        {
            for (int i = 0; i < asset.Exports.Count; i++)
            {
                if (asset.Exports[i] is NormalExport ne)
                {
                    var match = ne.Data.OfType<StructPropertyData>()
                        .FirstOrDefault(p => p.Name == propName && p.Value != null);
                    if (match != null)
                    {
                        return (ne, i, match);
                    }
                }
            }
            throw new InvalidOperationException(
                "No " + what + " found in any NormalExport of "
                + inputAssetPath + " - " + hint + ".");
        }

        static (NormalExport target, int index, FloatPropertyData prop) FindExportWithFloat(
            UAsset asset, FName propName, string inputAssetPath,
            string what, string hint)
        {
            for (int i = 0; i < asset.Exports.Count; i++)
            {
                if (asset.Exports[i] is NormalExport ne)
                {
                    var match = ne.Data.OfType<FloatPropertyData>()
                        .FirstOrDefault(p => p.Name == propName);
                    if (match != null)
                    {
                        return (ne, i, match);
                    }
                }
            }
            throw new InvalidOperationException(
                "No " + what + " found in any NormalExport of "
                + inputAssetPath + " - " + hint + ".");
        }

        static void ValidateArgs(string input, string output, string usmap, double multiplier)
        {
            if (string.IsNullOrEmpty(input))
                throw new ArgumentNullException("inputAssetPath");
            if (string.IsNullOrEmpty(output))
                throw new ArgumentNullException("outputAssetPath");
            if (string.IsNullOrEmpty(usmap))
                throw new ArgumentNullException("usmapPath");
            if (!File.Exists(input))
                throw new FileNotFoundException("Legacy uasset not found: " + input);
            if (!File.Exists(usmap))
                throw new FileNotFoundException("Usmap mappings not found: " + usmap);
            if (multiplier < MinMultiplier || multiplier > MaxMultiplier)
                throw new ArgumentOutOfRangeException("multiplier",
                    "Multiplier " + multiplier + " is outside ["
                    + MinMultiplier + ", " + MaxMultiplier
                    + "] - the GUI should have clamped this.");
        }

        void LogLine(string msg)
        {
            if (Log != null) Log(msg);
        }
    }

    public enum CooldownPatchShape
    {
        ScalableFloatDuration,
        TopLevelMagnitude,
        WeaponAbilityCurve,
    }

    public sealed class CooldownPatchResult
    {
        public string AssetStem;
        public int ExportIndex;
        public double Multiplier;
        public float VanillaValue;
        public float EffectiveValue;
        public CooldownPatchShape Shape;
    }
}
