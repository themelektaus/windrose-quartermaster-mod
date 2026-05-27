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
    // Patches the AttenuationRadius FloatProperty on one or more vanilla
    // light-source Blueprints so torches, candles, lanterns and bonfires
    // illuminate a larger (or smaller) sphere ingame. Exposes the multiplier
    // as a per-light slider plus an overall slider on the Lighting tab.
    //
    // Workflow context (mirrors PickaxeRangePatcher):
    //   game IoStore (.ucas)
    //     -> retoc to-legacy   (Zen package -> Legacy .uasset+.uexp)
    //     -> THIS CLASS        (multiply AttenuationRadius on the export
    //                            that carries the PointLight CDO defaults)
    //     -> retoc to-zen      (Legacy -> IoStore triplet)
    //
    // Why we ship a fixed registry of supported lights:
    //   - The 7 PointLight Blueprints under
    //     /Game/Gameplay/Building/LightComponents/ + the belt-lantern under
    //     /Game/Gameplay/Character/Common/... are the only blueprint-based
    //     light sources in vanilla Windrose 5.6; other light sources are
    //     spawned inline by Niagara FX or hardcoded C++ components which
    //     can't be patched via DataAsset overrides.
    //   - BP_PointLight_CampFire's CDO doesn't serialize AttenuationRadius
    //     (it inherits the parent R5BalancedPointLightComponent's C++
    //     default), so we can't update-in-place there. We skip CampFire
    //     entirely rather than ADD a property whose C++ default baseline
    //     is unknown (worst case we'd SHRINK the vanilla radius).
    //   - BP_BeltLanternLight carries AttenuationRadius on the
    //     PointLight_GEN_VARIABLE export (not the CDO). The patcher's
    //     export-search walks every NormalExport for the property so this
    //     "just works" - same code path as the CDO-based lights.
    public sealed class LightingPatcher
    {
        // Description of one supported light source.
        public sealed class LightInfo
        {
            // Filename stem (passed to retoc to-legacy --filter).
            public string Stem;
            // Full virtual path under R5/Content (used to locate the
            // freshly extracted legacy uasset in the staging dir).
            public string VirtualPath;
            // Human-readable label for the GUI.
            public string DisplayName;
            // Vanilla AttenuationRadius in cm. The patcher verifies the
            // asset's serialized value matches this on load; a mismatch
            // throws so we catch upstream layout drift instead of silently
            // rewriting unrelated floats.
            public float VanillaAttenuationRadius;
            // Optional short category string the GUI groups by (e.g.
            // "Fire", "Lantern"). Display-only, no behavioral impact.
            public string Category;
        }

        // Property name on the PointLight CDO / sub-component export.
        public const string AttenuationRadiusPropertyName = "AttenuationRadius";

        // Allowed multiplier range. 1.0 = vanilla (no-op, the build won't
        // ship the asset). Upper bound matches the GUI slider's cap
        // (10x = enough to push lanterns from 5 m -> 55 m which already
        // overlaps half the map; anything past that is silly).
        public const double MinMultiplier = 0.1;
        public const double MaxMultiplier = 10.0;

        // Registry of supported lights. Order is the GUI display order.
        // Vanilla AttenuationRadius values were empirically read out of
        // the live 5.6 cooked containers via UAssetAPI; the patcher's
        // verify-on-load step will catch any drift from these baselines.
        //
        // DisplayName reflects the set of placed actor blueprints that
        // actually reference this light component (reverse-engineered
        // from the Sources/Vanilla actor BP refs), NOT the component's
        // own filename. Example: BP_PointLight_Candelier is used by
        // SignalFireT01 and wall/hook lamps, while placed Chandeliers
        // reference BP_PointLight_TorchFire. Users adjust based on what
        // gets brighter ingame, so we label accordingly.
        //
        // Light components NOT in this list (and why):
        //   - BP_PointLight_CampFire     used only by FireplaceT04. Has
        //                                no serialized AttenuationRadius
        //                                (C++ default on the parent
        //                                R5BalancedPointLightComponent),
        //                                so no in-place edit is possible -
        //                                fireplaces cannot be adjusted.
        public static readonly List<LightInfo> Lights = new List<LightInfo>
        {
            new LightInfo
            {
                Stem = "BP_PointLight_Candle",
                VirtualPath = "R5/Content/Gameplay/Building/LightComponents/BP_PointLight_Candle.uasset",
                // Used by BP_BuildingBlock_Lamp_01..04, BP_LampT04_01,
                // BP_TableLampT04_01 (the small candle-style lamps).
                DisplayName = "Candle Lamp",
                VanillaAttenuationRadius = 300f,
                Category = "Lamp",
            },
            new LightInfo
            {
                Stem = "BP_PointLight_Lantern",
                VirtualPath = "R5/Content/Gameplay/Building/LightComponents/BP_PointLight_Lantern.uasset",
                // Used by BP_BuildingBlock_Lamp_05/06 and the table
                // lantern variants BP_TableLampT04_02/03.
                DisplayName = "Standing Lantern",
                VanillaAttenuationRadius = 550f,
                Category = "Lamp",
            },
            new LightInfo
            {
                Stem = "BP_PointLight_Candelier",
                VirtualPath = "R5/Content/Gameplay/Building/LightComponents/BP_PointLight_Candelier.uasset",
                // Used by BP_SignalFireT01 (!) plus BP_LampHookT02_01,
                // BP_LampT04_02..04 and BP_WallLampT04_01..03. The name
                // "Candelier" is misleading - actual Chandeliers use
                // BP_PointLight_TorchFire instead.
                DisplayName = "Wall Lamp + Signal Fire",
                VanillaAttenuationRadius = 550f,
                Category = "Lamp",
            },
            new LightInfo
            {
                Stem = "BP_PointLight_TorchFire",
                VirtualPath = "R5/Content/Gameplay/Building/LightComponents/BP_PointLight_TorchFire.uasset",
                // Used by floor/wall torches AND every Chandelier variant
                // (BP_BuildingBlock_Chandelier[_02], BP_ChandelierT02_01,
                // BP_ChandelierT04_01..04). Patching this slider lights
                // up both groups together.
                DisplayName = "Torch + Chandelier",
                VanillaAttenuationRadius = 800f,
                Category = "Fire",
            },
            new LightInfo
            {
                Stem = "BP_PointLight_WildFire",
                VirtualPath = "R5/Content/Gameplay/Building/LightComponents/BP_PointLight_WildFire.uasset",
                // Used only by BP_BuildingBlock_BuildingCenterT01 - the
                // central anchor fire of a base. Not the same as the
                // SignalFireT01 (which uses BP_PointLight_Candelier).
                DisplayName = "Building Center Fire",
                VanillaAttenuationRadius = 1100f,
                Category = "Fire",
            },
            new LightInfo
            {
                Stem = "BP_BeltLanternLight",
                VirtualPath = "R5/Content/Gameplay/Character/Common/GameplayCue/Consumables/Lantern/BP_BeltLanternLight.uasset",
                // Carried by the player when the belt-lantern consumable
                // is equipped. Stand-alone, doesn't affect any placed
                // building actor.
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

        // Patches the asset in-place (input == output is fine). Walks all
        // NormalExports to find AttenuationRadius - the property lives on
        // the CDO for top-level PointLight Blueprints, but on a
        // SCS-component sub-export (PointLight_GEN_VARIABLE) for
        // BP_BeltLanternLight. PickaxeRangePatcher uses the same scan
        // pattern for the same reason.
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

    // Per-asset patch outcome. The build pipeline aggregates these into a
    // higher-level LightingResult that also carries the published triplet
    // paths.
    public sealed class LightingPatchResult
    {
        // Filename stem (e.g. "BP_PointLight_WildFire") so result rendering
        // can attribute each entry to its light without re-parsing the path.
        public string Stem;
        public int ExportIndex;
        public double Multiplier;
        public float VanillaAttenuationRadius;
        public float EffectiveAttenuationRadius;
    }
}
