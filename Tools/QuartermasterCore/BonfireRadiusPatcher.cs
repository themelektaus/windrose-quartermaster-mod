using System;
using System.Globalization;
using System.IO;
using UAssetAPI;
using UAssetAPI.UnrealTypes;
using UAssetAPI.ExportTypes;
using UAssetAPI.Unversioned;

namespace Windrose.Quartermaster.Core
{
    // Patches DA_BI_Utilities_BuildingCenterT01.uasset to scale the building-
    // center influence sphere (the "you can build here" zone around a placed
    // building center / bonfire). Vanilla ships two FloatProperties on the
    // PrimaryDataAsset export at byte offsets 117 / 121 inside the export's
    // RawExport.Data byte stream:
    //
    //     InfluenceRadius = 5000  (cm; ~50 m placement radius around the center)
    //     InfluenceHeight = 3000  (cm; ~30 m vertical extent)
    //
    // Both scale by the SAME user-supplied multiplier; the reference mod
    // ExtendedBonfireRadius_3x_P matches multiplier=3.0 (15000/9000). An
    // empirical ingame probe with multiplier=3.0 confirmed the build zone
    // around a placed building-center grows ~3x, so these two floats ARE
    // the gameplay trigger (the BP_BuildingBlock's ScenarioOverlapSphere
    // radius was a red herring - patching it alone did nothing).
    //
    // Why a raw-byte overwrite on RawExport.Data instead of a NormalExport
    // property edit (the path PickupBlueprintPatcher uses):
    //
    //   retoc to-legacy returns this asset as a RawExport. UAssetAPI walks
    //   the unversioned property stream successfully for the first 24
    //   properties (WorldActorType .. Description, ending at offset 320),
    //   but bails on the trailing CollisionApproximation property whose
    //   value is an R5CollisionApproximation struct with a custom C++
    //   Serialize() function (no FProperty schema, length-prefixed FVector
    //   array + bool + float). UAssetAPI surfaces the asset as a single
    //   RawExport so callers must work in raw bytes - which is exactly
    //   what we want here, because we only need to flip two 4-byte floats
    //   well before the unparseable tail starts.
    //
    //   The two target offsets are far below the CollisionApproximation
    //   start (117 < 320), so the patch leaves every byte involved in the
    //   custom-serialised tail untouched. The pat
    //   retoc to-zen then re-encodes the asset into IoStore chunk bytes;
    //   the result was verified ingame to be loadable and gameplay-active.
    //
    // Workflow:
    //   game IoStore (.ucas)
    //     -> retoc to-legacy     extract DA_BI_Utilities_BuildingCenterT01 as RawExport
    //     -> THIS CLASS          overwrite 8 bytes (2 LE floats) on RawExport.Data
    //     -> asset.Write         re-emit legacy uasset+uexp
    //     -> retoc to-zen        legacy -> IoStore triplet
    public sealed class BonfireRadiusPatcher
    {
        // Virtual asset path within the game's content tree. There's
        // exactly one building-center DataAsset with these properties in
        // Windrose 5.6; if the engine ever moves it, the composite builder
        // fails fast with a clear "filter didn't match" diagnostic.
        public const string AssetVirtualPath =
            "R5/Content/Gameplay/Building/BuildingUtilities/DA_BI_Utilities_BuildingCenterT01.uasset";

        // Filename-stem filter passed to retoc to-legacy --filter. retoc
        // walks the IoStore container tree and matches anywhere on the
        // basename without extension.
        public const string AssetFilterStem = "DA_BI_Utilities_BuildingCenterT01";

        // Vanilla baseline values. Empirically verified by reading the
        // RawExport.Data bytes at the offsets below; broken out as
        // constants so a future game patch could be handled by updating
        // just these four numbers.
        public const float VanillaInfluenceRadius = 5000f;
        public const float VanillaInfluenceHeight = 3000f;

        // Byte offsets of the two FloatProperty payloads inside the
        // export's RawExport.Data stream. Derived by walking the
        // unversioned property bitstream against R5BuildingItem's usmap
        // schema (24 byte FUnversionedHeader at the start, then the
        // serialized values one after another):
        //
        //   [10] @ 117..121  (4 B)  InfluenceRadius (FloatPropertyData) = 5000
        //   [11] @ 121..125  (4 B)  InfluenceHeight (FloatPropertyData) = 3000
        //
        // If a future game patch changes the upstream property ordering
        // or inserts/removes earlier properties, these offsets move and
        // the runtime vanilla-value check (below) will throw rather than
        // silently rewriting unrelated bytes.
        public const int InfluenceRadiusOffset = 117;
        public const int InfluenceHeightOffset = 121;

        // Allowed multiplier range. 1.0 == vanilla (no-op), upper bound
        // chosen to be slightly above the reference mod's 3.0 baseline -
        // anything past 5x would let a single bonfire cover most of a
        // small island, which trivialises base placement entirely.
        public const double MinMultiplier = 1.0;
        public const double MaxMultiplier = 5.0;

        public Action<string> Log;

        // Patches the asset at inputAssetPath in-place when
        // inputAssetPath == outputAssetPath, or copies + patches when they
        // differ. usmapPath is required for UAssetAPI to recognise the
        // unversioned property layout (even though we operate on raw bytes,
        // loading the asset without mappings throws). The vanilla check
        // ensures we never silently overwrite unrelated bytes if the
        // upstream layout has drifted.
        public BonfireRadiusPatchResult Patch(
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

            // Find the RawExport that carries the DA payload. Vanilla
            // ships this DataAsset with exactly one export, but be defensive
            // and pick the first RawExport regardless of position.
            RawExport raw = null;
            int rawIdx = -1;
            for (int i = 0; i < asset.Exports.Count; i++)
            {
                if (asset.Exports[i] is RawExport r)
                {
                    raw = r;
                    rawIdx = i;
                    break;
                }
            }
            if (raw == null)
            {
                throw new InvalidOperationException(
                    "No RawExport found in " + inputAssetPath
                    + " - vanilla DA_BI_Utilities_BuildingCenterT01 ships as a"
                    + " single RawExport; UAssetAPI's parser may have changed.");
            }

            var data = raw.Data;
            if (data == null || data.Length < InfluenceHeightOffset + 4)
            {
                throw new InvalidOperationException(
                    "Asset RawExport.Data is too small ("
                    + (data == null ? 0 : data.Length)
                    + " bytes) to hold the expected InfluenceRadius+Height layout"
                    + " (needs at least " + (InfluenceHeightOffset + 4) + " bytes).");
            }

            // Read + verify vanilla values. BitConverter is little-endian
            // on x86/x64, matching the zen-encoded float byte order.
            float vanillaR = BitConverter.ToSingle(data, InfluenceRadiusOffset);
            float vanillaH = BitConverter.ToSingle(data, InfluenceHeightOffset);
            if (Math.Abs(vanillaR - VanillaInfluenceRadius) > 0.001f
                || Math.Abs(vanillaH - VanillaInfluenceHeight) > 0.001f)
            {
                throw new InvalidOperationException(
                    "Vanilla InfluenceRadius/Height bytes don't match expectation: "
                    + "got " + vanillaR.ToString(CultureInfo.InvariantCulture)
                    + " / " + vanillaH.ToString(CultureInfo.InvariantCulture)
                    + " at offsets " + InfluenceRadiusOffset
                    + " / " + InfluenceHeightOffset
                    + " (expected " + VanillaInfluenceRadius
                    + " / " + VanillaInfluenceHeight + "). "
                    + "The vanilla asset's property layout may have changed - "
                    + "re-probe with .build-tmp/bonfire-inject and update the "
                    + "BonfireRadiusPatcher offset constants.");
            }

            float newR = (float)(VanillaInfluenceRadius * multiplier);
            float newH = (float)(VanillaInfluenceHeight * multiplier);

            // Overwrite the two 4-byte little-endian floats in place. The
            // bytes around them (header, other properties, CollisionApproximation
            // tail) are preserved verbatim.
            var rBytes = BitConverter.GetBytes(newR);
            var hBytes = BitConverter.GetBytes(newH);
            Array.Copy(rBytes, 0, data, InfluenceRadiusOffset, 4);
            Array.Copy(hBytes, 0, data, InfluenceHeightOffset, 4);

            LogLine("BonfireRadius: InfluenceRadius "
                    + vanillaR.ToString("0", CultureInfo.InvariantCulture)
                    + " -> " + newR.ToString("0", CultureInfo.InvariantCulture)
                    + ", InfluenceHeight "
                    + vanillaH.ToString("0", CultureInfo.InvariantCulture)
                    + " -> " + newH.ToString("0", CultureInfo.InvariantCulture)
                    + " (multiplier=" + multiplier.ToString("0.##", CultureInfo.InvariantCulture)
                    + ")");

            LogLine("Writing: " + outputAssetPath);
            asset.Write(outputAssetPath);

            return new BonfireRadiusPatchResult
            {
                Multiplier = multiplier,
                RawExportIndex = rawIdx,
                VanillaInfluenceRadius = vanillaR,
                VanillaInfluenceHeight = vanillaH,
                EffectiveInfluenceRadius = newR,
                EffectiveInfluenceHeight = newH,
            };
        }

        void LogLine(string msg)
        {
            if (Log != null) Log(msg);
        }
    }

    // Per-asset patch outcome - the build pipeline aggregates this into a
    // higher-level BonfireRadiusResult that also carries the published
    // triplet paths.
    public sealed class BonfireRadiusPatchResult
    {
        public double Multiplier;
        public int RawExportIndex;
        public float VanillaInfluenceRadius;
        public float VanillaInfluenceHeight;
        public float EffectiveInfluenceRadius;
        public float EffectiveInfluenceHeight;
    }
}
