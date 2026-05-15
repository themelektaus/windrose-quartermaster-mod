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
    // Patches GameplayEffect / BP_Calc DataAssets to shorten consumable
    // cooldowns. Mirrors the "Faster Player Consumable Cooldown" + "Faster
    // Ship Consumable Cooldown" reference mods (5 assets) plus two additional
    // cooldowns the reference mods do not touch (boar whistle, ship summon).
    //
    // Two distinct property shapes:
    //
    //   A) GameplayEffect with ScalableFloatMagnitude:
    //        Default__GE_*_C
    //        + DurationMagnitude (StructProperty, GameplayEffectModifierMagnitude)
    //          + ScalableFloatMagnitude (StructProperty, ScalableFloat)
    //            + Value (FloatProperty)
    //      Used for: Elixir, Ship Repair Kit (regular + small), Boar Whistle,
    //                Ship Summon.
    //
    //   B) BP_Calc (R5ModMagCalc_SimpleAttributeBased) with top-level Magnitude:
    //        Default__BP_Calc_*_C
    //        + Magnitude (FloatProperty)
    //      Used for: Medicine, Recall (driven by GE_Cooldown_Medicine /
    //                GE_Cooldown_Potion_Recall via CustomCalculationClass,
    //                so patching the BP_Calc Magnitude scales the resolved
    //                cooldown without touching the GEs themselves).
    //
    // Workflow context (mirrors PickaxeRangePatcher):
    //   game IoStore (.ucas)
    //     -> retoc to-legacy   (Zen package -> Legacy .uasset+.uexp)
    //     -> THIS CLASS        (multiply the targeted FloatProperty)
    //     -> retoc to-zen      (Legacy -> IoStore triplet)
    //
    // The composite builder runs one Patch() call per asset; each call is
    // independent and produces its own CooldownPatchResult.
    public sealed class CooldownsPatcher
    {
        // Multiplier clamps. 1.0 = vanilla; the GUI hides the asset from
        // the build when the user leaves it at 1.0. Lower bound prevents
        // dividing by something close to zero in the rare future case
        // someone wires the multiplier in reverse; 0.05 = 20x faster which
        // is faster than any reasonable use case.
        public const double MinMultiplier = 0.05;
        public const double MaxMultiplier = 1.0;

        // Property names. Constants both for readability and so a future
        // engine rename only touches one place.
        public const string DurationMagnitudeProp     = "DurationMagnitude";
        public const string ScalableFloatMagnitudeProp = "ScalableFloatMagnitude";
        public const string ValueProp                 = "Value";
        public const string MagnitudeProp             = "Magnitude";

        // Filename stem -> virtual asset path mappings, grouped by family.
        // Filename stem is what we pass to retoc to-legacy --filter; virtual
        // path tells the AfterExtract callback where to find the freshly
        // written legacy file on disk.

        // Family: ELIXIR (GameplayEffect, ScalableFloat shape).
        public static readonly Dictionary<string, string> ElixirAssets =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                {
                    "GE_Cooldown_Elixir",
                    "R5/Content/Gameplay/ItemsLogic/Consumables/Elixir/GE_Cooldown_Elixir.uasset"
                },
            };

        // Family: MEDICINE (BP_Calc, top-level Magnitude).
        public static readonly Dictionary<string, string> MedicineAssets =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                {
                    "BP_Calc_ConsCdBonus_Medicine",
                    "R5/Content/Gameplay/ItemsLogic/Consumables/Shared/ConsCdBonus/BP_Calc_ConsCdBonus_Medicine.uasset"
                },
            };

        // Family: RECALL (BP_Calc, top-level Magnitude).
        public static readonly Dictionary<string, string> RecallAssets =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                {
                    "BP_Calc_ConsCdBonus_Recall",
                    "R5/Content/Gameplay/ItemsLogic/Consumables/Shared/ConsCdBonus/BP_Calc_ConsCdBonus_Recall.uasset"
                },
            };

        // Family: SHIP REPAIR KIT (regular + small variant, both GameplayEffect).
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

        // Family: BOAR WHISTLE (player consumable - GameplayEffect, ScalableFloat
        // with Value=1.0 multiplied against a CurveTable lookup).
        public static readonly Dictionary<string, string> BoarWhistleAssets =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                {
                    "GE_SpawnerCooldown",
                    "R5/Content/Gameplay/Character/Common/GameplayAbilities/UseConsumable/GE_SpawnerCooldown.uasset"
                },
            };

        // Family: SHIP SUMMON (player ability - GameplayEffect, ScalableFloat).
        public static readonly Dictionary<string, string> ShipSummonAssets =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                {
                    "GE_ShipSummon_Cooldown",
                    "R5/Content/Gameplay/Character/Player/GameplayAbilities/Summon/GE_ShipSummon_Cooldown.uasset"
                },
            };

        public Action<string> Log;

        // Patches a GameplayEffect asset by multiplying
        // DurationMagnitude.ScalableFloatMagnitude.Value with the supplied
        // multiplier. Used for Elixir / Ship Repair Kit / Boar Whistle /
        // Ship Summon. Input/output paths may be the same path (in-place).
        public CooldownPatchResult PatchScalableFloatDuration(
            string inputAssetPath, string outputAssetPath,
            string usmapPath, double multiplier)
        {
            ValidateArgs(inputAssetPath, outputAssetPath, usmapPath, multiplier);

            LogLine("Loading usmap: " + usmapPath);
            var mappings = new Usmap(usmapPath);
            LogLine("Loading uasset: " + inputAssetPath);
            var asset = new UAsset(inputAssetPath, EngineVersion.VER_UE5_6, mappings);

            var (target, targetIndex) = FindMainExport(asset, inputAssetPath);

            // DurationMagnitude (StructProperty, GameplayEffectModifierMagnitude)
            var durationName = FName.FromString(asset, DurationMagnitudeProp);
            var duration = target.Data.OfType<StructPropertyData>()
                .FirstOrDefault(p => p.Name == durationName);
            if (duration == null || duration.Value == null)
            {
                throw new InvalidOperationException(
                    "No DurationMagnitude StructProperty found on "
                    + target.ObjectName + " in " + inputAssetPath
                    + " - expected a GameplayEffect with a duration magnitude.");
            }

            // ScalableFloatMagnitude (StructProperty, ScalableFloat)
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

            // Value (FloatProperty)
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

        // Patches a BP_Calc asset by multiplying its top-level Magnitude
        // FloatProperty. Used for Medicine / Recall.
        public CooldownPatchResult PatchTopLevelMagnitude(
            string inputAssetPath, string outputAssetPath,
            string usmapPath, double multiplier)
        {
            ValidateArgs(inputAssetPath, outputAssetPath, usmapPath, multiplier);

            LogLine("Loading usmap: " + usmapPath);
            var mappings = new Usmap(usmapPath);
            LogLine("Loading uasset: " + inputAssetPath);
            var asset = new UAsset(inputAssetPath, EngineVersion.VER_UE5_6, mappings);

            var (target, targetIndex) = FindMainExport(asset, inputAssetPath);

            var magName = FName.FromString(asset, MagnitudeProp);
            var magProp = target.Data.OfType<FloatPropertyData>()
                .FirstOrDefault(p => p.Name == magName);
            if (magProp == null)
            {
                throw new InvalidOperationException(
                    "No Magnitude FloatProperty on " + target.ObjectName
                    + " in " + inputAssetPath
                    + " - expected an R5ModMagCalc_SimpleAttributeBased BP_Calc.");
            }

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

        // Locates the export carrying the patched property. Both GE and
        // BP_Calc DataAssets ship as a single NormalExport (the Default__
        // CDO); picking the first NormalExport is robust against minor
        // index reshuffles.
        static (NormalExport target, int index) FindMainExport(UAsset asset, string inputAssetPath)
        {
            for (int i = 0; i < asset.Exports.Count; i++)
            {
                if (asset.Exports[i] is NormalExport ne)
                {
                    return (ne, i);
                }
            }
            throw new InvalidOperationException(
                "No NormalExport found in " + inputAssetPath
                + " - expected a GameplayEffect or BP_Calc CDO export.");
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

    // Discriminator for diagnostics. Tells callers which property shape was
    // patched on a given asset so the build response can render the right
    // label ("Magnitude" vs "DurationMagnitude.ScalableFloatMagnitude.Value").
    public enum CooldownPatchShape
    {
        ScalableFloatDuration,
        TopLevelMagnitude,
    }

    // Per-asset patch outcome. The build pipeline aggregates these into a
    // higher-level CooldownsResult that groups results by family and
    // carries the published triplet paths.
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
