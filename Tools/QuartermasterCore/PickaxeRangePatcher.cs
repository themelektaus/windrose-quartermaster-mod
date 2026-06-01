using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using UAssetAPI;
using UAssetAPI.UnrealTypes;
using UAssetAPI.PropertyTypes.Objects;
using UAssetAPI.ExportTypes;
using UAssetAPI.Unversioned;

namespace Windrose.Quartermaster.Core
{
    public sealed class PickaxeRangePatcher
    {
        public static readonly Dictionary<string, string> TierAssets =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                {
                    "DA_MeleeWpn_Pickaxe_T00_Stone_InstanceParams",
                    "R5/Content/Gameplay/ItemsLogic/Weapon/Wpn_MainHand/Pickaxe_T00_Stone/MeleeWpn/DA_MeleeWpn_Pickaxe_T00_Stone_InstanceParams.uasset"
                },
                {
                    "DA_MeleeWpn_Pickaxe_T01_Crude_InstanceParams",
                    "R5/Content/Gameplay/ItemsLogic/Weapon/Wpn_MainHand/Pickaxe_T01_Crude/MeleeWpn/DA_MeleeWpn_Pickaxe_T01_Crude_InstanceParams.uasset"
                },
                {
                    "DA_MeleeWpn_Pickaxe_T02_Regular_InstanceParams",
                    "R5/Content/Gameplay/ItemsLogic/Weapon/Wpn_MainHand/Pickaxe_T02_Regular/MeleeWpn/DA_MeleeWpn_Pickaxe_T02_Regular_InstanceParams.uasset"
                },
                {
                    "DA_MeleeWpn_Pickaxe_T03_Reliable_InstanceParams",
                    "R5/Content/Gameplay/ItemsLogic/Weapon/Wpn_MainHand/Pickaxe_T03_Reliable/MeleeWpn/DA_MeleeWpn_Pickaxe_T03_Reliable_InstanceParams.uasset"
                },
            };

        public const string TraceScaleModifierPropertyName = "TraceScaleModifier";

        // Baseline used when the property is not serialized in vanilla (engine uses the C++ class default).
        public const float DefaultTraceScaleModifier = 1.0f;

        public const double MinMultiplier = 1.0;
        public const double MaxMultiplier = 3.0;

        public Action<string> Log;

        public PickaxeRangePatchResult Patch(
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

            // The CDO is not necessarily the first NormalExport; locate it by TraceScaleModifier presence.
            var propName = FName.FromString(asset, TraceScaleModifierPropertyName);
            NormalExport target = null;
            int targetIndex = -1;
            UAssetAPI.PropertyTypes.Objects.PropertyData existing = null;
            for (int i = 0; i < asset.Exports.Count; i++)
            {
                if (asset.Exports[i] is NormalExport ne)
                {
                    var match = ne.Data.FirstOrDefault(p => p.Name == propName);
                    if (match != null)
                    {
                        target = ne;
                        targetIndex = i;
                        existing = match;
                        break;
                    }
                }
            }
            if (target == null)
            {
                for (int i = 0; i < asset.Exports.Count; i++)
                {
                    if (asset.Exports[i] is NormalExport ne)
                    {
                        target = ne;
                        targetIndex = i;
                        break;
                    }
                }
            }
            if (target == null)
            {
                throw new InvalidOperationException(
                    "No NormalExport found in " + inputAssetPath
                    + " - expected an InstanceParams DataAsset export to patch.");
            }
            float vanillaValue;
            bool added;
            if (existing is FloatPropertyData existingFloat)
            {
                vanillaValue = existingFloat.Value;
                added = false;
            }
            else if (existing != null)
            {
                throw new InvalidOperationException(
                    "Property '" + TraceScaleModifierPropertyName
                    + "' on " + target.ObjectName + " has unexpected type "
                    + existing.GetType().Name
                    + " - expected FloatPropertyData. Asset schema may have changed.");
            }
            else
            {
                vanillaValue = DefaultTraceScaleModifier;
                added = true;
            }

            float newValue = (float)(vanillaValue * multiplier);
            if (added)
            {
                target.Data.Add(new FloatPropertyData(propName) { Value = newValue });
                LogLine("Added " + TraceScaleModifierPropertyName
                    + " FloatProperty = " + newValue.ToString("0.0000", CultureInfo.InvariantCulture)
                    + " (vanilla missing, assumed class-default "
                    + vanillaValue.ToString("0.0000", CultureInfo.InvariantCulture) + ")");
            }
            else
            {
                ((FloatPropertyData)existing).Value = newValue;
                LogLine("Updated " + TraceScaleModifierPropertyName + ": "
                    + vanillaValue.ToString("0.0000", CultureInfo.InvariantCulture)
                    + " -> " + newValue.ToString("0.0000", CultureInfo.InvariantCulture)
                    + " (multiplier=" + multiplier.ToString("0.##", CultureInfo.InvariantCulture) + ")");
            }

            LogLine("Writing: " + outputAssetPath);
            asset.Write(outputAssetPath);

            return new PickaxeRangePatchResult
            {
                AssetStem = Path.GetFileNameWithoutExtension(inputAssetPath),
                ExportIndex = targetIndex,
                Multiplier = multiplier,
                VanillaTraceScaleModifier = vanillaValue,
                EffectiveTraceScaleModifier = newValue,
                Added = added,
            };
        }

        void LogLine(string msg)
        {
            if (Log != null) Log(msg);
        }
    }

    public sealed class PickaxeRangePatchResult
    {
        public string AssetStem;
        public int ExportIndex;
        public double Multiplier;
        public float VanillaTraceScaleModifier;
        public float EffectiveTraceScaleModifier;
        // True = property was added (vanilla relied on C++ default); false = edited in place.
        public bool Added;
    }
}
