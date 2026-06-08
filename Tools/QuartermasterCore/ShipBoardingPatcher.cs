using System;
using System.Collections.Generic;
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
    // "Easy Boarding": loosens the ship-boarding restrictions, but as four
    // independent multipliers instead of the reference mod's fixed absolutes.
    // Every value lives in the inline TargetSelection struct
    // (R5BoardingTargetSelectionData) of each ship's BoardingParams DataAsset:
    //
    //   Range  -> MaxBoardingDistance     (per-ship baseline distance, scaled)
    //   Aim    -> BoardingSweepRadius      (100 baseline, scaled; the real
    //            "aim slightly around the ship" forgiveness - the mod's
    //            BoardSectorAngle bump is a no-op because HalfBoardSectorAngleCos,
    //            the actual runtime threshold, is left unchanged, so we don't
    //            touch the sector at all)
    //   Angle  -> MainAxisDifferenceAngle (degrees, scaled) AND its cosine
    //            MainAxisDifferenceAngleCos is RECOMPUTED. The cosine is the
    //            actual runtime threshold (dot >= cos); the degrees field is
    //            informational, so both must move together to take effect.
    //   Speed  -> MaxClosingSpeedKnots     (max relative speed at which boarding
    //            can still be initiated)
    //
    // PLAYER SHIPS ONLY / PER-FIELD-PRESENT INVARIANT: only the four ships the
    // reference mod patches (Brig/Cutter/Frigate/Ketch) are touched; the small
    // ShallowBoat is deliberately left vanilla. Properties are UNVERSIONED, so a
    // field is only serialized when it differs from the C++ class default
    // (e.g. MaxClosingSpeedKnots is absent on Cutter). Each field is patched only
    // where it is present; absent fields are skipped, never created - matching
    // exactly what the proven reference mod does.
    //
    // Like Pickaxe/Cannon-Damage this rides the IoStore composite path (cooked
    // uasset): retoc to-legacy extracts vanilla, this patches it in place, retoc
    // to-zen packs it back.
    public sealed class ShipBoardingPatcher
    {
        // Player ship boarding params, stem -> virtual (legacy) path.
        public static readonly Dictionary<string, string> Ships =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                { "DA_Brig_BoardingParams",    "R5/Content/Gameplay/Water/Character/Ability/Boarding/Params/DA_Brig_BoardingParams.uasset" },
                { "DA_Cutter_BoardingParams",  "R5/Content/Gameplay/Water/Character/Ability/Boarding/Params/DA_Cutter_BoardingParams.uasset" },
                { "DA_Frigate_BoardingParams", "R5/Content/Gameplay/Water/Character/Ability/Boarding/Params/DA_Frigate_BoardingParams.uasset" },
                { "DA_Ketch_BoardingParams",   "R5/Content/Gameplay/Water/Character/Ability/Boarding/Params/DA_Ketch_BoardingParams.uasset" },
            };

        public const string TargetSelectionPropertyName = "TargetSelection";
        public const string MaxBoardingDistanceName = "MaxBoardingDistance";
        public const string BoardingSweepRadiusName = "BoardingSweepRadius";
        public const string MainAxisDifferenceAngleName = "MainAxisDifferenceAngle";
        public const string MainAxisDifferenceAngleCosName = "MainAxisDifferenceAngleCos";
        public const string MaxClosingSpeedKnotsName = "MaxClosingSpeedKnots";

        // Per-property safety backstops ("the GUI should have clamped this"); the
        // sliders are the real limits. These mirror the cooldowns.html slider mins/maxes.
        public const double RangeMin = 1.0, RangeMax = 5.0;
        public const double AimMin   = 1.0, AimMax   = 5.0;
        public const double AngleMin = 1.0, AngleMax = 2.0;
        public const double SpeedMin = 1.0, SpeedMax = 3.0;

        // The recomputed MainAxisDifferenceAngle is clamped below 90 deg so its
        // cosine stays positive (at >=90 deg the dot-product threshold would
        // collapse / flip sign and boarding alignment would break).
        public const float MaxAngleDegrees = 89.0f;

        public Action<string> Log;

        public ShipBoardingPatchResult Patch(
            string inputAssetPath, string outputAssetPath, string usmapPath,
            double rangeMultiplier, double aimMultiplier,
            double angleMultiplier, double speedMultiplier)
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
            ValidateRange("range", rangeMultiplier, RangeMin, RangeMax);
            ValidateRange("aim",   aimMultiplier,   AimMin,   AimMax);
            ValidateRange("angle", angleMultiplier, AngleMin, AngleMax);
            ValidateRange("speed", speedMultiplier, SpeedMin, SpeedMax);

            var mappings = new Usmap(usmapPath);
            var asset = new UAsset(inputAssetPath, UAssetIo.Ue, mappings);

            StructPropertyData targetSel = null;
            int exportIndex = -1;
            for (int i = 0; i < asset.Exports.Count && targetSel == null; i++)
            {
                if (!(asset.Exports[i] is NormalExport ne)) continue;
                var match = ne.Data.FirstOrDefault(p =>
                    p is StructPropertyData
                    && string.Equals(p.Name?.Value?.Value, TargetSelectionPropertyName,
                                     StringComparison.Ordinal)) as StructPropertyData;
                if (match != null && match.Value != null) { targetSel = match; exportIndex = i; }
            }
            if (targetSel == null)
                throw new InvalidOperationException(
                    "No '" + TargetSelectionPropertyName + "' struct found in " + inputAssetPath
                    + " - the boarding params schema may have changed (game update?); refusing to patch.");

            var result = new ShipBoardingPatchResult
            {
                AssetStem = Path.GetFileNameWithoutExtension(inputAssetPath),
                ExportIndex = exportIndex,
                RangeMultiplier = rangeMultiplier,
                AimMultiplier = aimMultiplier,
                AngleMultiplier = angleMultiplier,
                SpeedMultiplier = speedMultiplier,
            };

            // Only touch a field when its multiplier is active (!= 1.0). This keeps
            // untouched properties byte-identical and avoids float drift (notably
            // the angle cosine recompute) when a slider is at vanilla.
            if (IsActive(rangeMultiplier))
            {
                var fp = FindFloat(targetSel, MaxBoardingDistanceName);
                if (fp != null)
                {
                    result.VanillaRange = fp.Value;
                    result.EffectiveRange = (float)(fp.Value * rangeMultiplier);
                    fp.Value = result.EffectiveRange;
                    result.RangePatched = true;
                    LogField(result.AssetStem, "range", MaxBoardingDistanceName,
                        result.VanillaRange, result.EffectiveRange, rangeMultiplier);
                }
                else LogSkip(result.AssetStem, "range", MaxBoardingDistanceName);
            }

            if (IsActive(aimMultiplier))
            {
                var fp = FindFloat(targetSel, BoardingSweepRadiusName);
                if (fp != null)
                {
                    result.VanillaAim = fp.Value;
                    result.EffectiveAim = (float)(fp.Value * aimMultiplier);
                    fp.Value = result.EffectiveAim;
                    result.AimPatched = true;
                    LogField(result.AssetStem, "aim", BoardingSweepRadiusName,
                        result.VanillaAim, result.EffectiveAim, aimMultiplier);
                }
                else LogSkip(result.AssetStem, "aim", BoardingSweepRadiusName);
            }

            if (IsActive(speedMultiplier))
            {
                var fp = FindFloat(targetSel, MaxClosingSpeedKnotsName);
                if (fp != null)
                {
                    result.VanillaSpeed = fp.Value;
                    result.EffectiveSpeed = (float)(fp.Value * speedMultiplier);
                    fp.Value = result.EffectiveSpeed;
                    result.SpeedPatched = true;
                    LogField(result.AssetStem, "speed", MaxClosingSpeedKnotsName,
                        result.VanillaSpeed, result.EffectiveSpeed, speedMultiplier);
                }
                else LogSkip(result.AssetStem, "speed", MaxClosingSpeedKnotsName);
            }

            if (IsActive(angleMultiplier))
            {
                var angle = FindFloat(targetSel, MainAxisDifferenceAngleName);
                if (angle != null)
                {
                    float vanillaDeg = angle.Value;
                    float newDeg = (float)(vanillaDeg * angleMultiplier);
                    if (newDeg > MaxAngleDegrees) newDeg = MaxAngleDegrees;
                    angle.Value = newDeg;
                    result.VanillaAngle = vanillaDeg;
                    result.EffectiveAngle = newDeg;
                    result.AnglePatched = true;

                    // Recompute the cosine threshold so the looser angle takes effect.
                    var cos = FindFloat(targetSel, MainAxisDifferenceAngleCosName);
                    if (cos != null)
                    {
                        cos.Value = (float)Math.Cos(newDeg * Math.PI / 180.0);
                        result.AngleCosRecomputed = true;
                    }
                    LogField(result.AssetStem, "angle", MainAxisDifferenceAngleName,
                        vanillaDeg, newDeg, angleMultiplier);
                    if (!result.AngleCosRecomputed)
                        LogLine("Ship boarding [" + result.AssetStem + "]: warning - "
                            + MainAxisDifferenceAngleName + " scaled but "
                            + MainAxisDifferenceAngleCosName + " absent; angle change may be inert.");
                }
                else LogSkip(result.AssetStem, "angle", MainAxisDifferenceAngleName);
            }

            result.PropertiesScaled =
                (result.RangePatched ? 1 : 0) + (result.AimPatched ? 1 : 0)
                + (result.AnglePatched ? 1 : 0) + (result.SpeedPatched ? 1 : 0);

            // A ship legitimately may not carry a targeted field (e.g. speed on
            // Cutter), so PropertiesScaled == 0 here is a valid skip, not an error -
            // it just means this ship had none of the active fields serialized.
            asset.Write(outputAssetPath);
            return result;
        }

        static bool IsActive(double multiplier) => Math.Abs(multiplier - 1.0) > 1e-9;

        static FloatPropertyData FindFloat(StructPropertyData container, string fieldName)
        {
            return container.Value.FirstOrDefault(c =>
                c is FloatPropertyData
                && string.Equals(c.Name?.Value?.Value, fieldName, StringComparison.Ordinal))
                as FloatPropertyData;
        }

        static void ValidateRange(string label, double value, double min, double max)
        {
            if (value < min || value > max)
                throw new ArgumentOutOfRangeException(label + "Multiplier",
                    label + " multiplier " + value.ToString(CultureInfo.InvariantCulture)
                    + " is outside [" + min + ", " + max
                    + "] - the GUI should have clamped this.");
        }

        void LogField(string stem, string label, string field, float vanilla, float scaled, double mult)
        {
            LogLine("Ship boarding [" + stem + "]: " + label + " (" + field + ") "
                + vanilla.ToString("0.####", CultureInfo.InvariantCulture) + " -> "
                + scaled.ToString("0.####", CultureInfo.InvariantCulture)
                + " (x" + mult.ToString("0.##", CultureInfo.InvariantCulture) + ")");
        }

        void LogSkip(string stem, string label, string field)
        {
            LogLine("Ship boarding [" + stem + "]: " + label + " ('" + field
                + "' not serialized on this ship) - skipped");
        }

        void LogLine(string msg)
        {
            if (Log != null) Log(msg);
        }
    }

    public sealed class ShipBoardingPatchResult
    {
        public string AssetStem;
        public int ExportIndex;
        public double RangeMultiplier;
        public double AimMultiplier;
        public double AngleMultiplier;
        public double SpeedMultiplier;
        public bool RangePatched;
        public bool AimPatched;
        public bool AnglePatched;
        public bool SpeedPatched;
        public bool AngleCosRecomputed;
        public int PropertiesScaled;
        // Samples are only meaningful when the matching *Patched flag is true.
        public float VanillaRange, EffectiveRange;
        public float VanillaAim, EffectiveAim;
        public float VanillaAngle, EffectiveAngle;   // degrees
        public float VanillaSpeed, EffectiveSpeed;   // knots
    }
}
