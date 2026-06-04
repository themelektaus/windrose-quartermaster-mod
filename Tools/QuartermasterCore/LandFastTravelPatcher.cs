using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using CUE4Parse.FileProvider;
using CUE4Parse.MappingsProvider;
using CUE4Parse.UE4.Assets;
using CUE4Parse.UE4.Assets.Objects;
using CUE4Parse.UE4.Assets.Objects.Properties;
using CUE4Parse.UE4.Assets.Objects.Unversioned;
using CUE4Parse.UE4.Assets.Readers;
using CUE4Parse.UE4.Readers;
using CUE4Parse.UE4.Versions;
using UAssetAPI;
using UAssetAPI.ExportTypes;
using UAssetAPI.UnrealTypes;
using Usmap = UAssetAPI.Unversioned.Usmap;

namespace Windrose.Quartermaster.Core
{
    // "Land Fast Travel": lets the fast-travel bell be placed inland instead of only
    // near the coast/water. Vanilla gates placement via the R5BuildingItem
    // CoastlineDistanceRange property (an FFloatRange). Widening it to
    // [-1e6, +1e6] (Exclusive bounds) makes the coast-proximity check always pass.
    //
    // Two fast-travel-bell DataAssets are patched, and they differ:
    //   - DA_BI_Utilities_FastTravel_Bell does NOT serialize CoastlineDistanceRange
    //     (inherits the restrictive class default), so we INSERT it.
    //   - DA_BI_Utilities_FastTravelBell_02 already serializes it (with a restrictive
    //     range), so we just widen its two bound floats.
    //
    // Why this is done PROGRAMMATICALLY from vanilla (no third-party asset shipped):
    // retoc to-legacy returns these DataAssets as UAssetAPI RawExports (the trailing
    // R5CollisionApproximation has a custom C++ Serialize with no FProperty schema,
    // which defeats UAssetAPI's unversioned reader). So we cannot add/edit the
    // property through UAssetAPI's structured API. Instead we:
    //   1) parse the cooked export with CUE4Parse (which DOES read this game's
    //      unversioned format, incl. the custom struct) to locate the exact byte
    //      offsets and the FUnversionedHeader fragment layout,
    //   2) splice the property's cooked bytes into the RawExport payload (inserting
    //      the FFloatRange value + flipping the unversioned header to mark the
    //      property present, or overwriting the two existing bound floats),
    //   3) let UAssetAPI.Write recompute the export SerialSize / summary offsets,
    //   4) re-parse the result with CUE4Parse and FAIL THE BUILD unless the property
    //      now reads back as [-1e6, +1e6] with every other property intact.
    //
    // The cooked FFloatRange value is a fixed 16-byte sequence learned from real
    // vanilla assets that ship a CoastlineDistanceRange (e.g. DA_BI_Dockyard_02):
    //   00 05            FFloatRange unversioned header (LowerBound, UpperBound present)
    //   80 05 01 <f32>   LowerBound: FFloatRangeBound (Type omitted via zero-mask =>
    //                    ERangeBoundTypes::Exclusive default) + Value float
    //   80 05 01 <f32>   UpperBound: same shape
    // It contains no FName references, so no name-table remapping is needed.
    public sealed class LandFastTravelPatcher
    {
        // retoc --filter stems for the two fast-travel-bell building-item DataAssets.
        public static readonly string[] AssetFilterStems =
        {
            "DA_BI_Utilities_FastTravel_Bell",
            "DA_BI_Utilities_FastTravelBell_02",
        };

        // Cooked virtual paths (forward-slash) of the same two DataAssets.
        public static readonly string[] AssetVirtualPaths =
        {
            "R5/Content/Gameplay/Building/BuildingUtilities/DA_BI_Utilities_FastTravel_Bell.uasset",
            "R5/Content/Gameplay/Building/BuildingUtilities/DA_BI_Utilities_FastTravelBell_02.uasset",
        };

        const string TargetProperty = "CoastlineDistanceRange";
        const float InlandLowerBound = -1000000f;
        const float InlandUpperBound =  1000000f;

        public Action<string> Log;

        public LandFastTravelPatchResult Patch(string stagingDir, string usmapPath)
        {
            if (string.IsNullOrEmpty(stagingDir)) throw new ArgumentNullException("stagingDir");
            if (string.IsNullOrEmpty(usmapPath)) throw new ArgumentNullException("usmapPath");
            if (!File.Exists(usmapPath))
                throw new FileNotFoundException("Usmap not found: " + usmapPath);

            int patched = 0;
            foreach (var virtualPath in AssetVirtualPaths)
            {
                var rel = virtualPath.Replace('/', Path.DirectorySeparatorChar);
                var assetPath = Path.Combine(stagingDir, rel);
                var stem = Path.GetFileNameWithoutExtension(virtualPath);

                // Sanity gate: the vanilla asset must have extracted here first.
                if (!File.Exists(assetPath))
                    throw new InvalidOperationException(
                        "Land Fast Travel: expected the vanilla bell asset at " + assetPath
                        + " after retoc to-legacy, but it is missing - the game container "
                        + "may have moved the asset (filter '" + stem + "').");

                PatchOne(assetPath, usmapPath, stem);
                patched++;
            }

            return new LandFastTravelPatchResult { AssetsReplaced = patched };
        }

        void PatchOne(string assetPath, string usmapPath, string stem)
        {
            // 1) Analyze the cooked export with CUE4Parse to find offsets + header layout.
            var layout = Analyze(assetPath, usmapPath);

            // 2) Splice the cooked bytes via UAssetAPI's RawExport payload.
            var asset = new UAsset(assetPath, UAssetIo.Ue, new Usmap(usmapPath));
            if (asset.Exports.Count != 1 || !(asset.Exports[0] is RawExport raw))
                throw new InvalidOperationException(
                    "Land Fast Travel: expected a single RawExport in " + stem
                    + " but found " + asset.Exports.Count + " export(s) of type "
                    + (asset.Exports.Count > 0 ? asset.Exports[0].GetType().Name : "<none>"));

            var D = raw.Data;
            var value = BuildCoastlineValue();

            if (layout.Present)
            {
                // Overwrite the existing FFloatRange value with the widened range.
                using var ms = new MemoryStream();
                ms.Write(D, 0, layout.ValueOffset);
                ms.Write(value, 0, value.Length);
                ms.Write(D, layout.ValueOffset + layout.ValueLength,
                         D.Length - (layout.ValueOffset + layout.ValueLength));
                raw.Data = ms.ToArray();
                LogLine("LandFastTravel: widened existing CoastlineDistanceRange in " + stem
                        + " -> [-1e6, +1e6]");
            }
            else
            {
                // Insert the value + flip the unversioned header to mark the property present.
                var newHeader = SplitHeaderToAddIndex(D, layout.HeaderLength,
                    layout.CoastSchemaIndex, layout.GapFragmentIndex);
                using var ms = new MemoryStream();
                ms.Write(newHeader, 0, newHeader.Length);                 // rewritten header
                ms.Write(D, layout.HeaderLength,
                         layout.InsertOffset - layout.HeaderLength);      // values before the property
                ms.Write(value, 0, value.Length);                        // inserted FFloatRange
                ms.Write(D, layout.InsertOffset, D.Length - layout.InsertOffset); // the rest
                raw.Data = ms.ToArray();
                LogLine("LandFastTravel: inserted CoastlineDistanceRange into " + stem
                        + " -> [-1e6, +1e6] (+" + (raw.Data.Length - D.Length) + " bytes)");
            }

            asset.Write(assetPath);

            // 3) Re-parse with CUE4Parse and fail the build unless the edit is correct.
            Verify(assetPath, usmapPath, stem);
        }

        // The fixed 16-byte cooked FFloatRange value with Exclusive bounds at +-1e6.
        static byte[] BuildCoastlineValue()
        {
            var lo = BitConverter.GetBytes(InlandLowerBound);
            var hi = BitConverter.GetBytes(InlandUpperBound);
            var b = new byte[16];
            b[0] = 0x00; b[1] = 0x05;                 // FFloatRange header
            b[2] = 0x80; b[3] = 0x05; b[4] = 0x01;    // LowerBound header + zero-mask (Type=Exclusive omitted)
            Array.Copy(lo, 0, b, 5, 4);
            b[9] = 0x80; b[10] = 0x05; b[11] = 0x01;  // UpperBound header + zero-mask
            Array.Copy(hi, 0, b, 12, 4);
            return b;
        }

        // -------- CUE4Parse analysis (read-only) --------

        sealed class BellLayout
        {
            public bool Present;
            public int CoastSchemaIndex;
            public int HeaderLength;     // bytes of the export's FUnversionedHeader
            public int InsertOffset;     // where to splice when absent (export-serial coords)
            public int GapFragmentIndex; // fragment whose skip-run covers CoastSchemaIndex (when absent)
            public int ValueOffset;      // existing value start (when present)
            public int ValueLength;      // existing value length (when present)
        }

        BellLayout Analyze(string assetPath, string usmapPath)
        {
            var versions = new VersionContainer(UAssetCue.Game);
            var uexpPath = Path.ChangeExtension(assetPath, ".uexp");
            var uassetBytes = File.ReadAllBytes(assetPath);
            var uexpBytes = File.ReadAllBytes(uexpPath);
            var pkg = new Package(
                new FByteArchive(assetPath, uassetBytes, versions),
                new FByteArchive(uexpPath, uexpBytes, versions),
                (FArchive)null, (FArchive)null,
                UAssetCue.MappingsProvider(usmapPath),
                useLazySerialization: false);

            var export = pkg.ExportMap[0];
            var classObj = pkg.ExportsLazy[0].Value.Class;
            if (classObj == null)
                throw new InvalidOperationException("LandFastTravel: cannot resolve export class");
            var clazz = classObj.Name.ToString();
            if (!pkg.Mappings.Types.TryGetValue(clazz, out var struc) || struc == null)
                throw new InvalidOperationException("LandFastTravel: no usmap schema for class " + clazz);

            int coastIndex = SchemaIndexOf(struc, TargetProperty);
            if (coastIndex < 0)
                throw new InvalidOperationException(
                    "LandFastTravel: schema for " + clazz + " has no property " + TargetProperty);

            // Drive the unversioned parse manually, tracking byte positions.
            var ar = new FAssetArchive(new FByteArchive(uexpPath, uexpBytes, versions), pkg,
                                       (int)uassetBytes.Length);
            ar.SeekAbsolute(export.SerialOffset, SeekOrigin.Begin);
            long start = ar.Position;
            var header = new FUnversionedHeader(ar);
            int headerLen = (int)(ar.Position - start);

            var layout = new BellLayout
            {
                CoastSchemaIndex = coastIndex,
                HeaderLength = headerLen,
                InsertOffset = -1,
            };

            if (header.HasValues)
            {
                using var it = new FIterator(header);
                do
                {
                    var (val, isNonZero) = it.Current;
                    long before = ar.Position;
                    struc.TryGetValue(val, out var info);
                    if (layout.InsertOffset < 0 && val > coastIndex)
                        layout.InsertOffset = (int)(before - start);
                    if (isNonZero)
                    {
                        new FPropertyTag(ar, info, ReadType.NORMAL);
                        if (val == coastIndex)
                        {
                            layout.Present = true;
                            layout.ValueOffset = (int)(before - start);
                            layout.ValueLength = (int)(ar.Position - before);
                        }
                    }
                    else
                    {
                        new FPropertyTag(ar, info, ReadType.ZERO);
                    }
                } while (it.MoveNext());
            }

            if (!layout.Present)
            {
                if (layout.InsertOffset < 0)
                    throw new InvalidOperationException(
                        "LandFastTravel: no property after " + TargetProperty
                        + " to anchor the insert in this asset.");
                layout.GapFragmentIndex = FindGapFragment(header, coastIndex);
            }
            return layout;
        }

        static int SchemaIndexOf(Struct struc, string name)
        {
            // Schemas for this game stay well under a few hundred flattened properties.
            for (int i = 0; i < 4096; i++)
            {
                if (struc.TryGetValue(i, out var info) && info != null
                    && string.Equals(info.Name, name, StringComparison.Ordinal))
                    return i;
            }
            return -1;
        }

        // Index (into header.Fragments) of the fragment whose skip-run covers `targetIndex`
        // (i.e. the property is currently absent and skipped by that fragment).
        static int FindGapFragment(FUnversionedHeader header, int targetIndex)
        {
            int pos = 0;
            for (int f = 0; f < header.Fragments.Count; f++)
            {
                var frag = header.Fragments[f];
                int skipStart = pos;
                int skipEnd = pos + frag.SkipNum;       // [skipStart, skipEnd) are skipped
                if (targetIndex >= skipStart && targetIndex < skipEnd)
                    return f;
                pos = skipEnd + frag.ValueNum;          // advance past the value run
            }
            throw new InvalidOperationException(
                "LandFastTravel: target index " + targetIndex
                + " is not inside any header skip-run (asset layout changed?).");
        }

        // Rewrites the FUnversionedHeader so `targetIndex` becomes a present (non-zero)
        // value, by splitting the single gap fragment into skip/value/skip. Returns the
        // new header bytes; the body is spliced separately.
        static byte[] SplitHeaderToAddIndex(byte[] data, int headerLen, int targetIndex, int gapFragmentIndex)
        {
            // Re-read the fragments from the raw bytes (2 bytes each, little-endian).
            int fragCount = -1;
            var fragments = new List<ushort>();
            for (int i = 0; i + 1 < headerLen; i += 2)
            {
                ushort packed = (ushort)(data[i] | (data[i + 1] << 8));
                fragments.Add(packed);
                if ((packed & FFragment.IsLastMask) != 0) { fragCount = fragments.Count; break; }
            }
            if (fragCount < 0 || fragCount != fragments.Count)
                throw new InvalidOperationException("LandFastTravel: malformed unversioned header.");

            // Walk to the gap fragment to recover its skip-run start.
            int pos = 0;
            for (int f = 0; f < gapFragmentIndex; f++)
            {
                var fr = new FFragment(fragments[f]);
                pos += fr.SkipNum + fr.ValueNum;
            }
            var gap = new FFragment(fragments[gapFragmentIndex]);
            if (gap.HasAnyZeroes)
                throw new InvalidOperationException(
                    "LandFastTravel: gap fragment uses a zero-mask; insertion not supported for this layout.");

            int skipStart = pos;
            int skipBefore = targetIndex - skipStart;               // absent props before target
            int skipAfter  = (skipStart + gap.SkipNum) - (targetIndex + 1); // absent props after target
            if (skipBefore < 0 || skipAfter < 0 || skipBefore > FFragment.SkipMax || skipAfter > FFragment.SkipMax)
                throw new InvalidOperationException("LandFastTravel: cannot split header gap fragment cleanly.");

            // First new fragment: skip up to target, present exactly 1 value (the target).
            ushort f1 = (ushort)((uint)skipBefore | (1u << FFragment.ValueNumShift));
            // Second new fragment: skip the remainder, then the gap fragment's original value run,
            // preserving its IsLast flag.
            ushort f2 = (ushort)((uint)skipAfter
                                 | ((uint)gap.ValueNum << FFragment.ValueNumShift)
                                 | (gap.IsLast ? FFragment.IsLastMask : 0u));

            var outFrags = new List<ushort>(fragments.Count + 1);
            for (int f = 0; f < fragments.Count; f++)
            {
                if (f == gapFragmentIndex) { outFrags.Add(f1); outFrags.Add(f2); }
                else outFrags.Add(fragments[f]);
            }

            var bytes = new byte[outFrags.Count * 2];
            for (int i = 0; i < outFrags.Count; i++)
            {
                bytes[i * 2] = (byte)(outFrags[i] & 0xFF);
                bytes[i * 2 + 1] = (byte)(outFrags[i] >> 8);
            }
            return bytes;
        }

        // -------- CUE4Parse verification (fail the build on any mismatch) --------

        void Verify(string assetPath, string usmapPath, string stem)
        {
            var pkg = UAssetCue.LoadStandalone(assetPath, usmapPath);
            var exp = pkg.ExportsLazy[0].Value;
            var coast = exp.Properties.FirstOrDefault(p => p.Name.Text == TargetProperty);
            if (coast == null)
                throw new InvalidOperationException(
                    "LandFastTravel: post-patch verification failed - " + TargetProperty
                    + " missing in " + stem + ".");

            var gv = coast.Tag?.GenericValue;
            if (gv is FScriptStruct fs) gv = fs.StructType;
            if (!(gv is FStructFallback fr))
                throw new InvalidOperationException(
                    "LandFastTravel: post-patch verification failed - " + TargetProperty
                    + " is not a struct in " + stem + ".");

            float lo = BoundValue(fr, "LowerBound");
            float hi = BoundValue(fr, "UpperBound");
            if (Math.Abs(lo - InlandLowerBound) > 1f || Math.Abs(hi - InlandUpperBound) > 1f)
                throw new InvalidOperationException(
                    "LandFastTravel: post-patch verification failed - " + stem
                    + " CoastlineDistanceRange = [" + lo + ", " + hi + "], expected [-1e6, +1e6].");

            LogLine("LandFastTravel: verified " + stem + " CoastlineDistanceRange = ["
                    + lo + ", " + hi + "] (CUE4Parse re-parse)");
        }

        static float BoundValue(FStructFallback range, string boundName)
        {
            var b = range.Properties.FirstOrDefault(p => p.Name.Text == boundName);
            var gv = b?.Tag?.GenericValue;
            if (gv is FScriptStruct fs) gv = fs.StructType;
            if (gv is FStructFallback bs)
            {
                var v = bs.Properties.FirstOrDefault(p => p.Name.Text == "Value");
                if (v?.Tag?.GenericValue is float f) return f;
            }
            throw new InvalidOperationException("LandFastTravel: cannot read " + boundName + ".Value");
        }

        void LogLine(string msg) { if (Log != null) Log(msg); }
    }

    public sealed class LandFastTravelPatchResult
    {
        public int AssetsReplaced;
    }
}
