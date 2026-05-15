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
    // Patches a pickaxe InstanceParams DataAsset (legacy .uasset+.uexp after
    // retoc to-legacy) by multiplying its TraceScaleModifier FloatProperty
    // with a user-supplied scalar. The engine multiplies every weapon trace
    // shape (cylinder radius, box extents, location offset) by this single
    // top-level scalar at hit-resolution time, so growing it is enough to
    // give the player extra reach when chopping nodes - no need to touch
    // the deeply nested per-section trace entries (variant A of the
    // UE4SS "Pickaxe Range" reference mod).
    //
    // Workflow context (mirrors PickupBlueprintPatcher):
    //   game IoStore (.ucas)
    //     -> retoc to-legacy   (Zen package -> Legacy .uasset+.uexp)
    //     -> THIS CLASS        (multiply TraceScaleModifier on the main export)
    //     -> retoc to-zen      (Legacy -> IoStore triplet)
    //
    // Four assets are patched per build (one per pickaxe tier). The build
    // pipeline runs one PickaxeRangePatcher.Patch() call per asset; each
    // call is independent and produces its own PickaxeRangePatchResult.
    public sealed class PickaxeRangePatcher
    {
        // Filename stem -> virtual asset path. Filename stem is what we pass
        // to retoc to-legacy --filter; virtual path tells the AfterExtract
        // callback where to find the freshly written legacy file on disk.
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

        // Property name inside the DataAsset's main export. Cooked
        // InstanceParams DataAssets ship this as a serialized FloatProperty;
        // when it's missing in vanilla (engine falls back to the C++ class
        // default) we ADD it instead of editing in place.
        public const string TraceScaleModifierPropertyName = "TraceScaleModifier";

        // C++ class default used as the baseline when the property is not
        // serialized in the vanilla asset. Empirically all four pickaxe
        // tiers DO serialize this value, but the fallback keeps the patcher
        // safe against future engine reshuffles that could drop it.
        public const float DefaultTraceScaleModifier = 1.0f;

        // Allowed multiplier range. 1.0 = vanilla (no-op, the build won't
        // ship the asset). Upper bound chosen generously: 3.0x already gives
        // ~1.5-2x effective reach when combined with the trace shape's
        // natural extent, beyond which traces start clipping into terrain.
        public const double MinMultiplier = 1.0;
        public const double MaxMultiplier = 3.0;

        public Action<string> Log;

        // Patches the asset in-place (input == output is fine). Returns a
        // PickaxeRangePatchResult carrying vanilla + effective values so the
        // build response can render a "1.00 -> 1.40" style summary.
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

            // Find the export carrying the InstanceParams payload. UE5.6
            // DataAssets may ship sub-component exports (ability tasks,
            // gameplay-effect components) alongside the Default__DA_*_C CDO -
            // the first NormalExport is therefore NOT necessarily the CDO.
            // Locate the right export by TraceScaleModifier presence; if no
            // export carries it (vanilla relies on the C++ class default),
            // fall back to the first NormalExport and ADD the property there.
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
                // Property is not serialized anywhere - fall back to the first
                // NormalExport so we can ADD it. This mirrors the original
                // behavior for assets that omit TraceScaleModifier entirely.
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
                // Property exists but has the wrong type - bail loudly rather
                // than corrupt the asset. Means the schema changed upstream.
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

    // Per-asset patch outcome. The build pipeline aggregates one of these
    // per pickaxe tier into a higher-level PickaxeRangeResult that also
    // carries the published triplet paths.
    public sealed class PickaxeRangePatchResult
    {
        // Filename stem (e.g. "DA_MeleeWpn_Pickaxe_T02_Regular_InstanceParams")
        // so result rendering can attribute each entry to its tier without
        // re-parsing the full asset path.
        public string AssetStem;
        public int ExportIndex;
        public double Multiplier;
        public float VanillaTraceScaleModifier;
        public float EffectiveTraceScaleModifier;
        // True when the patcher had to ADD a missing FloatProperty (vanilla
        // relied on the C++ default). False when it edited a serialized
        // value in place. Useful for diagnostics if the upstream schema
        // ever shifts.
        public bool Added;
    }
}
