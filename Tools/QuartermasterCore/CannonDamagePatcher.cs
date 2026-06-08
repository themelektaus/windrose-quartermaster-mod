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
    // Scales the PLAYER ship cannons' projectile damage by a flat multiplier, the
    // same edit the "Cannon Damage" reference mod ships: every DamageInterval
    // (min+max) inside the DamageGEData array of the cannonball damage params is
    // multiplied. Each caliber's DA carries three R5DamageGEData entries (hull /
    // shared / sail damage); all of them are scaled so the boost is uniform.
    //
    // PLAYER-ONLY INVARIANT: only DA_Ship_DamageParams_Cannonball_{12,24,36} are
    // patched. The DA_Ship_DamageParams_Cannonball_AI_* variants drive enemy/NPC
    // ships and are deliberately never touched, so the slider can't buff enemies.
    // The invariant is structural: PlayerAssets lists only the player stems, and
    // each is extracted/patched by its exact stem (the "_AI_" infix means the AI
    // assets never match a player filter).
    //
    // Unlike reload/range (loose R5CannonParams .json -> legacy pak) the damage
    // lives in cooked uassets, so this rides the IoStore composite path: retoc
    // to-legacy extracts the vanilla uasset, this patches it in place, retoc
    // to-zen packs it back.
    public sealed class CannonDamagePatcher
    {
        // Player cannonball damage params, stem -> virtual (legacy) path.
        public static readonly Dictionary<string, string> PlayerAssets =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                {
                    "DA_Ship_DamageParams_Cannonball_12",
                    "R5/Content/Gameplay/Water/Character/Guns/Damage/DA_Ship_DamageParams_Cannonball_12.uasset"
                },
                {
                    "DA_Ship_DamageParams_Cannonball_24",
                    "R5/Content/Gameplay/Water/Character/Guns/Damage/DA_Ship_DamageParams_Cannonball_24.uasset"
                },
                {
                    "DA_Ship_DamageParams_Cannonball_36",
                    "R5/Content/Gameplay/Water/Character/Guns/Damage/DA_Ship_DamageParams_Cannonball_36.uasset"
                },
            };

        public const string DamageArrayPropertyName = "DamageGEData";
        public const string DamageIntervalPropertyName = "DamageInterval";

        public const double MinMultiplier = 1.0;
        public const double MaxMultiplier = 10.0;

        public Action<string> Log;

        public CannonDamagePatchResult Patch(
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

            var mappings = new Usmap(usmapPath);
            var asset = new UAsset(inputAssetPath, UAssetIo.Ue, mappings);

            // Locate the export holding the DamageGEData array (the params CDO).
            var arrayName = FName.FromString(asset, DamageArrayPropertyName);
            ArrayPropertyData damageArray = null;
            foreach (var ex in asset.Exports)
            {
                if (ex is NormalExport ne)
                {
                    var match = ne.Data.FirstOrDefault(p => p.Name == arrayName) as ArrayPropertyData;
                    if (match != null) { damageArray = match; break; }
                }
            }
            if (damageArray == null)
                throw new InvalidOperationException(
                    "No '" + DamageArrayPropertyName + "' array found in " + inputAssetPath
                    + " - the cannonball damage params schema may have changed.");

            var result = new CannonDamagePatchResult
            {
                AssetStem = Path.GetFileNameWithoutExtension(inputAssetPath),
                Multiplier = multiplier,
            };

            foreach (var element in damageArray.Value)
            {
                if (!(element is StructPropertyData entry)) continue;
                var interval = entry.Value.FirstOrDefault(
                    p => p.Name.ToString() == DamageIntervalPropertyName) as StructPropertyData;
                if (interval == null) continue;

                // FFloatInterval carries two floats (min, max). Scale every float
                // child so the edit doesn't hinge on exact field casing.
                foreach (var fld in interval.Value)
                {
                    if (fld is FloatPropertyData fp)
                    {
                        float vanilla = fp.Value;
                        float scaled = (float)(vanilla * multiplier);
                        fp.Value = scaled;
                        result.IntervalsScaled++;
                        if (result.IntervalsScaled == 1)
                        {
                            result.SampleVanillaDamage = vanilla;
                            result.SampleEffectiveDamage = scaled;
                        }
                    }
                }
            }

            if (result.IntervalsScaled == 0)
                throw new InvalidOperationException(
                    "Found '" + DamageArrayPropertyName + "' in " + inputAssetPath
                    + " but no '" + DamageIntervalPropertyName
                    + "' float fields to scale - schema may have changed.");

            LogLine("Cannon damage [" + result.AssetStem + "]: scaled "
                + result.IntervalsScaled + " damage value(s) by "
                + multiplier.ToString("0.##", CultureInfo.InvariantCulture) + "x (sample "
                + result.SampleVanillaDamage.ToString("0", CultureInfo.InvariantCulture) + " -> "
                + result.SampleEffectiveDamage.ToString("0", CultureInfo.InvariantCulture) + ")");

            asset.Write(outputAssetPath);
            return result;
        }

        void LogLine(string msg)
        {
            if (Log != null) Log(msg);
        }
    }

    public sealed class CannonDamagePatchResult
    {
        public string AssetStem;
        public double Multiplier;
        public int IntervalsScaled;       // count of float damage values touched
        public float SampleVanillaDamage;
        public float SampleEffectiveDamage;
    }
}
