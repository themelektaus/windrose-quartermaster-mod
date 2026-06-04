using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UAssetAPI;
using UAssetAPI.UnrealTypes;
using UAssetAPI.Unversioned;
using UAssetAPI.ExportTypes;
using UAssetAPI.PropertyTypes.Objects;
using UAssetAPI.PropertyTypes.Structs;

namespace Windrose.Quartermaster.Core
{
    // "Extended Ship Pickup Radius": widens the overlap volumes that decide how
    // close you must sail to collect floating sea loot / interact with ships.
    //
    // Mechanism (derived from VANILLA, generalises the reference mod's effect):
    // the interaction-zone DataAssets carry an OverlapData struct whose
    // NewShapeData array holds the collision shapes (spheres + capsules). Each
    // shape has a Radius (and, for capsules, a HalfHeight) in cm. The reference
    // mod scales those up so the pickup volume reaches farther. We do the same as
    // a single multiplier applied to EVERY Radius/HalfHeight under OverlapData,
    // computed from the freshly-extracted vanilla value (so there is no drift if
    // the game retunes the base radii).
    //
    // Why UAssetAPI (not a byte overwrite): the values are FloatProperties nested
    // several struct/array levels deep inside a cooked, unversioned DataAsset;
    // UAssetAPI resolves them via the usmap and reserialises cleanly, exactly the
    // way the reference mod was produced.
    public sealed class ShipPickupPatcher
    {
        // The interaction-zone DataAssets (sea-loot pickup sphere + per-ship-type
        // zones). retoc --filter stems and the legacy paths to-legacy produces.
        public static readonly string[] AssetFilterStems =
        {
            "DA_InteractShipParams",
            "DA_InteractZoneBrig",
            "DA_InteractZoneFrigate",
            "DA_InteractZoneKetch",
        };

        static readonly string[] AssetVirtualPaths =
        {
            "R5/Content/Gameplay/Interaction/Params/Ability/DA_InteractShipParams.uasset",
            "R5/Content/Gameplay/Interaction/Params/Ship/DA_InteractZoneBrig.uasset",
            "R5/Content/Gameplay/Interaction/Params/Ship/DA_InteractZoneFrigate.uasset",
            "R5/Content/Gameplay/Interaction/Params/Ship/DA_InteractZoneKetch.uasset",
        };

        // The struct that holds the collision shapes; we only scale floats beneath it
        // so unrelated Radius/HalfHeight floats elsewhere in the asset are left alone.
        const string OverlapProperty = "OverlapData";

        // Float leaves we scale (sphere radius + capsule radius/half-height).
        static readonly HashSet<string> ScaledFloats =
            new HashSet<string>(StringComparer.Ordinal) { "Radius", "HalfHeight" };

        public Action<string> Log;

        // Scales the just-extracted vanilla interaction-zone assets in the IoStore
        // staging tree by `multiplier`. `stagingDir` is the composite legacy root
        // retoc to-legacy populated; `usmapPath` is the unversioned property map.
        public ShipPickupPatchResult Patch(string stagingDir, string usmapPath, double multiplier)
        {
            if (string.IsNullOrEmpty(stagingDir)) throw new ArgumentNullException("stagingDir");
            if (string.IsNullOrEmpty(usmapPath)) throw new ArgumentNullException("usmapPath");
            if (!File.Exists(usmapPath))
                throw new FileNotFoundException("Usmap not found: " + usmapPath);
            if (!(multiplier > 0.0))
                throw new ArgumentOutOfRangeException("multiplier", "Ship pickup multiplier must be > 0.");

            var mappings = new Usmap(usmapPath);
            int assetsPatched = 0;
            int valuesScaled = 0;

            foreach (var virtualPath in AssetVirtualPaths)
            {
                var assetPath = Path.Combine(stagingDir,
                    virtualPath.Replace('/', Path.DirectorySeparatorChar));
                if (!File.Exists(assetPath))
                    throw new InvalidOperationException(
                        "Ship Pickup: expected the vanilla interaction asset at " + assetPath
                        + " after retoc to-legacy, but it is missing - the game container may "
                        + "have moved it (filter '" + Path.GetFileNameWithoutExtension(virtualPath) + "').");
                if (!File.Exists(Path.ChangeExtension(assetPath, ".uexp")))
                    throw new FileNotFoundException(
                        "Ship Pickup: legacy uexp sibling not found for " + assetPath
                        + " - expected a uasset/uexp pair from `retoc to-legacy`.");

                int scaled = ScaleAsset(assetPath, mappings, multiplier);
                if (scaled > 0) assetsPatched++;
                valuesScaled += scaled;
            }

            if (valuesScaled == 0)
                throw new InvalidOperationException(
                    "Ship Pickup: found no overlap-shape Radius/HalfHeight floats under '"
                    + OverlapProperty + "' in any interaction asset - the zone layout changed; "
                    + "the patch was not applied to avoid shipping a wrong asset.");

            LogLine("Ship Pickup: scaled " + valuesScaled + " overlap-shape value(s) across "
                    + assetsPatched + " asset(s) by " + multiplier.ToString("0.0#") + "x (from vanilla)");
            return new ShipPickupPatchResult
            {
                AssetsPatched = assetsPatched,
                ValuesScaled = valuesScaled,
            };
        }

        // Multiplies every Radius/HalfHeight float beneath OverlapData by `multiplier`
        // and rewrites the asset. Returns the number of scaled floats.
        int ScaleAsset(string assetPath, Usmap mappings, double multiplier)
        {
            var asset = new UAsset(assetPath, UAssetIo.Ue, mappings);

            var overlaps = asset.Exports.OfType<NormalExport>()
                .SelectMany(e => e.Data)
                .Where(p => p.Name != null && p.Name.ToString() == OverlapProperty)
                .ToList();
            if (overlaps.Count == 0)
                throw new InvalidOperationException(
                    "Ship Pickup: no '" + OverlapProperty + "' property in "
                    + Path.GetFileName(assetPath) + " - the zone layout changed.");

            int scaled = 0;
            foreach (var overlap in overlaps)
                foreach (var f in FindScalableFloats(new[] { overlap }))
                {
                    f.Value = (float)(f.Value * multiplier);
                    scaled++;
                }

            if (scaled > 0) asset.Write(assetPath);
            LogLine("Ship Pickup: " + Path.GetFileNameWithoutExtension(assetPath)
                    + " - scaled " + scaled + " shape value(s)");
            return scaled;
        }

        // Walks a property subtree (structs + arrays) yielding every FloatProperty
        // named Radius/HalfHeight, regardless of how deep UAssetAPI nests the shapes.
        static IEnumerable<FloatPropertyData> FindScalableFloats(IEnumerable<PropertyData> props)
        {
            if (props == null) yield break;
            foreach (var p in props)
            {
                if (p is FloatPropertyData fp
                    && p.Name != null && ScaledFloats.Contains(p.Name.ToString()))
                    yield return fp;
                else if (p is StructPropertyData sp)
                    foreach (var c in FindScalableFloats(sp.Value)) yield return c;
                else if (p is ArrayPropertyData ap)
                    foreach (var c in FindScalableFloats(ap.Value)) yield return c;
            }
        }

        void LogLine(string msg)
        {
            if (Log != null) Log(msg);
        }
    }

    public sealed class ShipPickupPatchResult
    {
        public int AssetsPatched;
        public int ValuesScaled;
    }
}
