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
    public sealed class LightingPatcher
    {
        public sealed class LightInfo
        {
            public string Stem;
            public string VirtualPath;
            public string DisplayName;
            // Verified against the asset's serialized value on load; mismatch throws.
            public float VanillaAttenuationRadius;
            public string Category;
        }

        public const string AttenuationRadiusPropertyName = "AttenuationRadius";

        public const double MinMultiplier = 0.1;
        public const double MaxMultiplier = 10.0;

        public static readonly List<LightInfo> Lights = new List<LightInfo>
        {
            new LightInfo
            {
                Stem = "BP_PointLight_Candle",
                VirtualPath = "R5/Content/Gameplay/Building/LightComponents/BP_PointLight_Candle.uasset",
                DisplayName = "Candle Lamp",
                VanillaAttenuationRadius = 300f,
                Category = "Lamp",
            },
            new LightInfo
            {
                Stem = "BP_PointLight_Lantern",
                VirtualPath = "R5/Content/Gameplay/Building/LightComponents/BP_PointLight_Lantern.uasset",
                DisplayName = "Standing Lantern",
                VanillaAttenuationRadius = 550f,
                Category = "Lamp",
            },
            new LightInfo
            {
                Stem = "BP_PointLight_Candelier",
                VirtualPath = "R5/Content/Gameplay/Building/LightComponents/BP_PointLight_Candelier.uasset",
                DisplayName = "Wall Lamp + Signal Fire",
                VanillaAttenuationRadius = 550f,
                Category = "Lamp",
            },
            new LightInfo
            {
                Stem = "BP_PointLight_TorchFire",
                VirtualPath = "R5/Content/Gameplay/Building/LightComponents/BP_PointLight_TorchFire.uasset",
                DisplayName = "Torch + Chandelier",
                VanillaAttenuationRadius = 800f,
                Category = "Fire",
            },
            new LightInfo
            {
                Stem = "BP_PointLight_WildFire",
                VirtualPath = "R5/Content/Gameplay/Building/LightComponents/BP_PointLight_WildFire.uasset",
                DisplayName = "Building Center Fire",
                VanillaAttenuationRadius = 1100f,
                Category = "Fire",
            },
            new LightInfo
            {
                Stem = "BP_BeltLanternLight",
                VirtualPath = "R5/Content/Gameplay/Character/Common/GameplayCue/Consumables/Lantern/BP_BeltLanternLight.uasset",
                DisplayName = "Belt Lantern",
                VanillaAttenuationRadius = 850f,
                Category = "Carried",
            },
        };

        public static LightInfo FindLight(string stem)
        {
            if (string.IsNullOrEmpty(stem)) return null;
            return Lights.FirstOrDefault(l =>
                string.Equals(l.Stem, stem, StringComparison.OrdinalIgnoreCase));
        }

        public Action<string> Log;

        // Walks all NormalExports: the property sits on the CDO for top-level
        // PointLight blueprints but on a sub-component export for BP_BeltLanternLight.
        public LightingPatchResult Patch(
            string inputAssetPath, string outputAssetPath,
            string usmapPath, double multiplier,
            LightInfo info)
        {
            if (string.IsNullOrEmpty(inputAssetPath))
                throw new ArgumentNullException("inputAssetPath");
            if (string.IsNullOrEmpty(outputAssetPath))
                throw new ArgumentNullException("outputAssetPath");
            if (string.IsNullOrEmpty(usmapPath))
                throw new ArgumentNullException("usmapPath");
            if (info == null)
                throw new ArgumentNullException("info");
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

            var propName = FName.FromString(asset, AttenuationRadiusPropertyName);

            NormalExport targetExport = null;
            int targetIndex = -1;
            FloatPropertyData targetProperty = null;
            for (int i = 0; i < asset.Exports.Count; i++)
            {
                if (asset.Exports[i] is NormalExport ne)
                {
                    var match = ne.Data.FirstOrDefault(p => p.Name == propName);
                    if (match != null)
                    {
                        if (!(match is FloatPropertyData fp))
                        {
                            throw new InvalidOperationException(
                                "Property '" + AttenuationRadiusPropertyName
                                + "' on " + ne.ObjectName + " has unexpected type "
                                + match.GetType().Name
                                + " - expected FloatPropertyData. Asset schema may have changed.");
                        }
                        targetExport = ne;
                        targetIndex = i;
                        targetProperty = fp;
                        break;
                    }
                }
            }
            if (targetProperty == null)
            {
                throw new InvalidOperationException(
                    "No NormalExport carrying '" + AttenuationRadiusPropertyName
                    + "' found in " + inputAssetPath
                    + " - the vanilla " + info.Stem + " CDO/sub-component layout "
                    + "may have changed. Re-probe with the .build-tmp probe script "
                    + "and update LightingPatcher.Lights.");
            }

            float vanillaValue = targetProperty.Value;
            if (Math.Abs(vanillaValue - info.VanillaAttenuationRadius) > 0.5f)
            {
                throw new InvalidOperationException(
                    "Vanilla " + info.Stem + ".AttenuationRadius mismatch: "
                    + "got " + vanillaValue.ToString(CultureInfo.InvariantCulture)
                    + " but expected " + info.VanillaAttenuationRadius.ToString(CultureInfo.InvariantCulture)
                    + ". Update LightingPatcher.Lights[" + info.Stem
                    + "].VanillaAttenuationRadius to match the new baseline.");
            }

            float newValue = (float)(info.VanillaAttenuationRadius * multiplier);
            targetProperty.Value = newValue;

            LogLine(info.Stem + ": AttenuationRadius "
                + vanillaValue.ToString("0.0", CultureInfo.InvariantCulture)
                + " -> " + newValue.ToString("0.0", CultureInfo.InvariantCulture)
                + " (multiplier=" + multiplier.ToString("0.##", CultureInfo.InvariantCulture)
                + ", export[" + targetIndex + "]=" + targetExport.ObjectName + ")");

            LogLine("Writing: " + outputAssetPath);
            asset.Write(outputAssetPath);

            return new LightingPatchResult
            {
                Stem = info.Stem,
                ExportIndex = targetIndex,
                Multiplier = multiplier,
                VanillaAttenuationRadius = vanillaValue,
                EffectiveAttenuationRadius = newValue,
            };
        }

        void LogLine(string msg)
        {
            if (Log != null) Log(msg);
        }
    }

    public sealed class LightingPatchResult
    {
        public string Stem;
        public int ExportIndex;
        public double Multiplier;
        public float VanillaAttenuationRadius;
        public float EffectiveAttenuationRadius;
    }
}
