using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using UAssetAPI;
using UAssetAPI.UnrealTypes;
using UAssetAPI.PropertyTypes.Objects;
using UAssetAPI.PropertyTypes.Structs;
using UAssetAPI.ExportTypes;
using UAssetAPI.Unversioned;

namespace Windrose.Quartermaster.Core
{
    // Self-bakes vanilla DA_BI* DataAssets to "buildings basically never
    // collapse" by overwriting four floats inside the IntegritySettings
    // StructProperty directly in the Zen-format chunk bytes.
    //
    // Why direct zen-chunk patching instead of retoc's to-legacy + to-zen
    // round-trip:
    //
    //   Vanilla DA_BI* assets are cooked with UE5 unversioned-property
    //   encoding (compact schema indices, no FName tags). Pushing them
    //   through retoc to-zen re-emits them in versioned-property encoding,
    //   which mostly works EXCEPT for the trailing R5CollisionApproximation
    //   struct: that field uses a custom C++ Serialize() function (length-
    //   prefixed FVector array + bool + float, no FProperty stream) and
    //   the round-trip produces game-incompatible bytes. The game crashes
    //   at startup with a Serial size mismatch deep in
    //   R5BuildingItem::Serialize.
    //
    //   The fix: leave the zen chunks ALONE. Probe vanilla bytes via the
    //   to-legacy + UAssetAPI path (read-only, never round-trips) to find
    //   the IntegritySettings byte offsets and original 16-byte content,
    //   then locate that exact 16-byte pattern within the raw zen chunk
    //   from retoc unpack-raw and overwrite it in place with target
    //   values. The chunk's structure, sizes, hashes-of-hashes are all
    //   preserved - retoc pack-raw produces a triplet the game accepts.
    //
    // Workflow:
    //   game pakchunk0_s3-Windows.utoc
    //     -> retoc unpack-raw      raw zen chunks + manifest.json
    //     -> retoc to-legacy       legacy .uasset/.uexp (probe only)
    //     -> THIS CLASS            probe vanilla bytes, find in chunk, overwrite
    //     -> retoc pack-raw        output IoStore triplet (.pak/.ucas/.utoc)
    //
    // Byte layout of an unversioned IntegritySettings struct in this asset
    // family (verified across the full 787-asset set the BetterStructureSupport
    // reference mod ships - same layout, all 787 patched cleanly):
    //
    //     +0   2 bytes   inner FUnversionedHeader (single fragment, all 4 floats present)
    //     +2   4 bytes   BlockWeight                  (float, LE)
    //     +6   4 bytes   BlockMaxHorizontalLoad       (float, LE)
    //     +10  4 bytes   BlockMaxVerticalLoad         (float, LE)
    //     +14  4 bytes   BlockMinimumIntersectionExtent (float, LE)
    //                                                  = 18 bytes total
    //
    // Pattern uniqueness was empirically verified by a validation pass over
    // all 862 vanilla DA_BIs: every probed 16-byte vanilla value appears
    // exactly once in its zen chunk, so the find+overwrite is unambiguous.
    public sealed class BuildingStabilityPatcher
    {
        // retoc to-legacy --filter is substring matching on the asset
        // filename (without extension). "DA_BI" matches every DataAsset
        // in the BuildingItems / BuildingDecoration / BuildingFarming /
        // BuildingCrafts / BuildingUtilities / BuildingDockyard /
        // BuildingEmployees / BuildingPoi / POI families, ~862 assets in
        // Windrose 5.6. Of these, ~75 are filtered out by IsSupportedAssetPath
        // (non-placeable / special-physics / known-broken-when-patched) -
        // see that method's comment block for the rationale.
        public const string AssetFilterStem = "DA_BI";

        // Classifies a relative DA_BI* asset path (e.g. "R5/Content/Gameplay/
        // Building/BuildingItems/DA_BI_Wall_Stone_T2.uasset") as either
        // safe-to-patch or must-be-excluded-from-the-output-triplet.
        //
        // BACKGROUND: an early self-bake attempt patched all 862 DA_BI*
        // DataAssets indiscriminately and the resulting mod crashed the game
        // on startup. The BetterStructureSupport reference mod's author had
        // empirically settled on a 787-asset subset; the 75 they omitted
        // turned out to be the trigger. The omitted set is a mix of:
        //   - non-placeable trader/NPC catalogue DataAssets in POI/TradePost
        //     and POI/Tortuga (they're DA_BI by filename, but they're trade
        //     inventories not building blocks)
        //   - dockyard ships and employee workers with special physics
        //   - POI dungeon decor (rotten barrels, skeleton beds, POI floor
        //     plates) that's pre-placed in handcrafted level geometry
        //   - the farming "plant stage" assets (Aloe_T02, Corn_T02, ...)
        //     which represent crop growth tiers, not buildings
        //   - the single special BuildingUtilities/BuildingCenterT01 asset
        //
        // Returns true if the asset should be patched + kept in the output
        // chunk set; false if its zen chunk should be dropped from the
        // manifest so retoc pack-raw doesn't include it in the triplet.
        public static bool IsSupportedAssetPath(string relativeAssetPath)
        {
            if (string.IsNullOrEmpty(relativeAssetPath)) return false;
            // Normalize path separators so this method works on both
            // Windows-style "\" paths from Directory.GetFiles and forward-
            // slash relative paths that callers may pass in.
            var p = relativeAssetPath.Replace('\\', '/');

            // Folder substrings that exclude the entire folder. All assets
            // here are non-buildable / non-physics-relevant in vanilla and
            // shipping a roundtripped+patched copy provoked the startup
            // crash described above.
            string[] excludedFolders = {
                "/BuildingDockyard/",
                "/BuildingEmployees/",
                "/BuildingPoi/",
                "/POI/Tortuga/",
                "/POI/TradePost/",
            };
            foreach (var folder in excludedFolders)
            {
                if (p.IndexOf(folder, StringComparison.OrdinalIgnoreCase) >= 0)
                    return false;
            }

            // Single named exclusion in BuildingUtilities: the foundation
            // center asset is special-cased by the engine and the reference
            // mod omits it.
            if (p.EndsWith("/BuildingUtilities/DA_BI_Utilities_BuildingCenterT01.uasset",
                    StringComparison.OrdinalIgnoreCase))
                return false;

            // BuildingFarming is mixed: keep flower beds + soil tiles
            // (those are real placeable building items), exclude plant
            // growth-stage assets (Aloe_T02, BushTomato_T02, Corn_T02, ...).
            // Pattern: kept items start with "DA_BI_Farming_GardenFlowerbed"
            // or "DA_BI_Farming_Soil"; the rest are crops.
            if (p.IndexOf("/BuildingFarming/", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                var fname = Path.GetFileName(p);
                return fname.StartsWith("DA_BI_Farming_GardenFlowerbed",
                            StringComparison.OrdinalIgnoreCase)
                    || fname.StartsWith("DA_BI_Farming_Soil",
                            StringComparison.OrdinalIgnoreCase);
            }

            return true;
        }

        // Target values applied to every IntegritySettings struct. These
        // match the BetterStructureSupport reference mod 1:1, so the
        // user-facing behaviour is identical to the previous reference-
        // adoption build path: BlockWeight=0 (gravity-less), Max*Load=1e7
        // (effectively unbreakable for any realistic stack), and
        // MinIntersectionExtent=0 (no overlap requirement).
        public const float TargetBlockWeight = 0.0f;
        public const float TargetBlockMaxHorizontalLoad = 10_000_000.0f;
        public const float TargetBlockMaxVerticalLoad = 10_000_000.0f;
        public const float TargetBlockMinimumIntersectionExtent = 0.0f;

        // Expected size in bytes of the IntegritySettings struct body
        // (inner header + 4 floats). Anything else is treated as a layout
        // change we don't understand - skipped with a warning rather than
        // mis-patched.
        const int ExpectedIntegritySize = 18;

        public Action<string> Log;

        // Orchestrates a complete patch pass:
        //   1. Walks vanillaLegacyDir for every DA_BI_*.uasset retoc to-legacy
        //      just produced.
        //   2. For each asset that IsSupportedAssetPath() approves, probes
        //      its UAssetAPI representation to find the IntegritySettings
        //      byte offset and reads the 16 vanilla bytes (4 little-endian
        //      floats).
        //   3. Looks up the asset's chunk ID via the manifest, opens the
        //      corresponding chunk file in chunksDir, locates the unique
        //      occurrence of the 16-byte vanilla pattern, and overwrites it
        //      with the target values.
        //   4. For excluded assets, drops their chunk file from chunksDir
        //      AND removes the chunk_paths entry from the manifest, so the
        //      subsequent retoc pack-raw produces a triplet with only the
        //      787 supported assets - exactly the set the reference mod
        //      ships and that we know is game-compatible.
        //   5. Writes the filtered manifest back to manifestPath.
        //
        // Returns one BuildingStabilityAssetResult per asset (patched +
        // skipped + excluded entries), so callers can roll up the totals
        // for the build response.
        public List<BuildingStabilityAssetResult> PatchChunks(
            string vanillaLegacyDir,
            string chunksDir,
            string manifestPath,
            string usmapPath)
        {
            if (string.IsNullOrEmpty(vanillaLegacyDir))
                throw new ArgumentNullException("vanillaLegacyDir");
            if (string.IsNullOrEmpty(chunksDir))
                throw new ArgumentNullException("chunksDir");
            if (string.IsNullOrEmpty(manifestPath))
                throw new ArgumentNullException("manifestPath");
            if (string.IsNullOrEmpty(usmapPath))
                throw new ArgumentNullException("usmapPath");
            if (!Directory.Exists(vanillaLegacyDir))
                throw new DirectoryNotFoundException("Vanilla legacy dir not found: " + vanillaLegacyDir);
            if (!Directory.Exists(chunksDir))
                throw new DirectoryNotFoundException("Chunks dir not found: " + chunksDir);
            if (!File.Exists(manifestPath))
                throw new FileNotFoundException("Manifest not found: " + manifestPath);
            if (!File.Exists(usmapPath))
                throw new FileNotFoundException("Usmap mappings not found: " + usmapPath);

            // Load manifest: chunk_paths maps chunkId -> "../../../R5/..."
            // We invert it into "R5/Content/..." -> chunkId for lookup.
            // Also keep the original JsonDocument around so we can write
            // back a filtered copy at the end.
            string manifestText = File.ReadAllText(manifestPath);
            var manifest = JsonDocument.Parse(manifestText);
            var chunkPathsEl = manifest.RootElement.GetProperty("chunk_paths");

            var pathToChunk = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in chunkPathsEl.EnumerateObject())
            {
                var chunkId = entry.Name;
                var rawPath = entry.Value.GetString() ?? "";
                var stripped = StripMountPrefix(rawPath);
                if (!string.IsNullOrEmpty(stripped))
                    pathToChunk[stripped] = chunkId;
            }

            var mappings = new Usmap(usmapPath);
            var results = new List<BuildingStabilityAssetResult>();
            var keepChunks = new HashSet<string>(StringComparer.Ordinal);
            int excluded = 0;

            var assetFiles = Directory.GetFiles(vanillaLegacyDir,
                "DA_BI_*.uasset", SearchOption.AllDirectories);

            foreach (var assetPath in assetFiles)
            {
                var relAssetPath = ToRelativeR5Path(assetPath, vanillaLegacyDir);

                if (!IsSupportedAssetPath(relAssetPath))
                {
                    // Excluded asset: drop its chunk from the output set.
                    // We don't add to keepChunks; the post-loop filter step
                    // will both delete the chunk file from chunksDir and
                    // omit it from the rewritten manifest.
                    excluded++;
                    results.Add(new BuildingStabilityAssetResult
                    {
                        AssetPath = assetPath,
                        RelativePath = relAssetPath,
                        Patched = false,
                        Reason = "excluded-by-skiplist",
                    });
                    continue;
                }

                BuildingStabilityAssetResult result;
                try
                {
                    result = PatchOneAsset(assetPath, relAssetPath, mappings,
                                          chunksDir, pathToChunk);
                }
                catch (Exception ex)
                {
                    result = new BuildingStabilityAssetResult
                    {
                        AssetPath = assetPath,
                        RelativePath = relAssetPath,
                        Patched = false,
                        Reason = "error: " + ex.Message,
                    };
                    LogLine("  error patching " + relAssetPath + ": " + ex.Message);
                }

                results.Add(result);

                // Even unpatched (no IntegritySettings) supported assets stay
                // in the output chunk set - they're not the source of the
                // 75-asset crash, so dropping them would diverge from the
                // reference-mod baseline unnecessarily.
                if (result.ChunkId != null)
                    keepChunks.Add(result.ChunkId);
            }

            // Manifest contains chunks for far more than just the 787 DA_BIs:
            // pakchunk0_s3 has ~15000 chunks (all of /Game/Gameplay/Building/
            // plus textures, materials, meshes for those assets). We want to
            // KEEP only the chunks we explicitly approved; everything else
            // gets dropped from both the manifest AND chunksDir so retoc
            // pack-raw produces a tight triplet with just the 787 patches.
            //
            // We walk the DISK chunks rather than manifest entries because
            // retoc unpack-raw can extract chunks that have no chunk_paths
            // entry (e.g. type 0x06 memory-mapped bulk data). Such orphan
            // chunks would still be picked up by pack-raw and bloat the
            // triplet by hundreds of KB.
            int chunksDropped = 0;
            foreach (var chunkFile in Directory.EnumerateFiles(chunksDir))
            {
                var chunkId = Path.GetFileName(chunkFile);
                if (keepChunks.Contains(chunkId)) continue;
                try { File.Delete(chunkFile); chunksDropped++; }
                catch (Exception ex)
                {
                    LogLine("  warn: failed to drop unused chunk " + chunkId + ": " + ex.Message);
                }
            }

            // Rewrite manifest.json with only the keep set. The format is
            // simple (mount_point, version, chunk_paths) so we hand-write
            // it rather than reflecting through JsonNode mutation.
            WriteFilteredManifest(manifest.RootElement, chunkPathsEl,
                                  keepChunks, manifestPath);

            LogLine("Stability: kept " + keepChunks.Count + " chunk(s), "
                    + "dropped " + chunksDropped + " unrelated chunk(s), "
                    + "excluded " + excluded + " non-supported asset(s)");

            return results;
        }

        // Patches one asset: probe vanilla bytes via UAssetAPI, find the
        // unique 16-byte pattern in the zen chunk, overwrite with target
        // values. Returns a result regardless of whether the patch landed -
        // the caller decides what to do with skip/error outcomes.
        BuildingStabilityAssetResult PatchOneAsset(
            string assetPath,
            string relAssetPath,
            Usmap mappings,
            string chunksDir,
            Dictionary<string, string> pathToChunk)
        {
            if (!pathToChunk.TryGetValue(relAssetPath, out var chunkId))
            {
                // Vanilla legacy extract has an asset retoc unpack-raw
                // didn't include in the manifest. Shouldn't happen because
                // both come from the same pakchunk0_s3 container, but log
                // so we'd notice if it ever did.
                LogLine("  warn: no chunk mapping for " + relAssetPath);
                return new BuildingStabilityAssetResult
                {
                    AssetPath = assetPath,
                    RelativePath = relAssetPath,
                    Patched = false,
                    Reason = "no-chunk-mapping",
                };
            }

            var vanillaBytes = ProbeIntegrityBytes(assetPath, mappings);
            if (vanillaBytes == null)
            {
                // Asset legitimately lacks IntegritySettings. Keep its
                // chunk in the output (unpatched) so we don't perturb the
                // package graph - the reference mod also ships these
                // asset chunks unchanged.
                return new BuildingStabilityAssetResult
                {
                    AssetPath = assetPath,
                    RelativePath = relAssetPath,
                    Patched = false,
                    Reason = "no-integrity-settings",
                    ChunkId = chunkId,
                };
            }

            var chunkFile = Path.Combine(chunksDir, chunkId);
            if (!File.Exists(chunkFile))
            {
                LogLine("  warn: chunk file missing: " + chunkId + " for " + relAssetPath);
                return new BuildingStabilityAssetResult
                {
                    AssetPath = assetPath,
                    RelativePath = relAssetPath,
                    Patched = false,
                    Reason = "chunk-file-missing",
                };
            }

            var chunkBytes = File.ReadAllBytes(chunkFile);
            int hitOffset = FindUnique(chunkBytes, vanillaBytes);
            if (hitOffset < 0)
            {
                // Pattern not found OR not unique. Both are anomalies.
                // -1 = no match (asset's vanilla bytes don't appear in the
                //      chunk we mapped it to - manifest mismatch?)
                // -2 = multiple matches (ambiguous, refuse to guess which)
                int hits = CountOccurrences(chunkBytes, vanillaBytes);
                LogLine("  warn: " + relAssetPath + " - chunk has " + hits + " match(es), expected 1");
                return new BuildingStabilityAssetResult
                {
                    AssetPath = assetPath,
                    RelativePath = relAssetPath,
                    Patched = false,
                    Reason = "pattern-match-count=" + hits,
                    ChunkId = chunkId,
                };
            }

            // Capture original values for diagnostics, then overwrite the
            // 16 bytes (4 little-endian floats) in place.
            float oldWeight = BitConverter.ToSingle(chunkBytes, hitOffset);
            float oldHLoad  = BitConverter.ToSingle(chunkBytes, hitOffset + 4);
            float oldVLoad  = BitConverter.ToSingle(chunkBytes, hitOffset + 8);
            float oldMinExt = BitConverter.ToSingle(chunkBytes, hitOffset + 12);

            WriteFloatLE(chunkBytes, hitOffset,      TargetBlockWeight);
            WriteFloatLE(chunkBytes, hitOffset + 4,  TargetBlockMaxHorizontalLoad);
            WriteFloatLE(chunkBytes, hitOffset + 8,  TargetBlockMaxVerticalLoad);
            WriteFloatLE(chunkBytes, hitOffset + 12, TargetBlockMinimumIntersectionExtent);

            File.WriteAllBytes(chunkFile, chunkBytes);

            return new BuildingStabilityAssetResult
            {
                AssetPath = assetPath,
                RelativePath = relAssetPath,
                Patched = true,
                ChunkId = chunkId,
                IntegrityOffsetInChunk = hitOffset,
                OldBlockWeight = oldWeight,
                OldBlockMaxHorizontalLoad = oldHLoad,
                OldBlockMaxVerticalLoad = oldVLoad,
                OldBlockMinimumIntersectionExtent = oldMinExt,
            };
        }

        // Loads the asset via UAssetAPI, walks the property stream until it
        // finds IntegritySettings, and returns the 16 raw vanilla bytes
        // (4 LE floats: BlockWeight, BlockMaxHorizontalLoad,
        // BlockMaxVerticalLoad, BlockMinimumIntersectionExtent).
        //
        // The DA_BI* family splits into two serialization shapes:
        //
        //   RawExport (~99%): the typical case. The asset's tail
        //     R5CollisionApproximation struct uses a custom C++
        //     Serialize() function that UAssetAPI can't decode, so
        //     the export comes through as a raw byte buffer. We
        //     manually run the property reader over rawExp.Data to
        //     locate IntegritySettings and read the 16 bytes from
        //     the appropriate offset.
        //
        //   NormalExport (~1%): a small subset (currently 8 assets:
        //     a few StairsT04 / FloorT04 / WallCornerT04 / DoorWayT04
        //     variants) lacks R5CollisionApproximation entirely, so
        //     the property reader walks to the end and returns a
        //     fully decoded NormalExport. For these we read the
        //     typed float values directly and synthesize the 16-byte
        //     pattern from them.
        //
        // Returns null if the asset has no IntegritySettings property
        // (legitimate for some special-case DA_BIs).
        byte[] ProbeIntegrityBytes(string assetPath, Usmap mappings)
        {
            var asset = new UAsset(assetPath, EngineVersion.VER_UE5_6, mappings);
            if (asset.Exports.Count == 0) return null;

            var firstExp = asset.Exports[0];
            if (firstExp is RawExport rawExp)
            {
                return ProbeRaw(asset, rawExp);
            }
            if (firstExp is NormalExport ne)
            {
                return ProbeNormal(ne);
            }
            return null;
        }

        byte[] ProbeRaw(UAsset asset, RawExport rawExp)
        {
            using var ms = new MemoryStream(rawExp.Data);
            using var br = new AssetBinaryReader(ms, asset);

            var className = rawExp.GetExportClassType();
            var ancestry = new AncestryInfo();
            ancestry.SetAsParent(className, null);

            FUnversionedHeader header;
            try { header = new FUnversionedHeader(br); }
            catch { return null; }

            while (header.HasValues())
            {
                long beforePos = ms.Position;
                PropertyData prop;
                try
                {
                    prop = MainSerializer.Read(br, ancestry, className, null, header, true);
                }
                catch
                {
                    // Hit the unparseable tail (R5CollisionApproximation)
                    // before IntegritySettings was found. The asset
                    // legitimately doesn't serialize it - return null so
                    // the caller skips without erroring.
                    return null;
                }
                if (prop == null) return null;

                var pname = prop.Name != null && prop.Name.Value != null
                    ? prop.Name.Value.Value as string
                    : null;
                if (pname == "IntegritySettings")
                {
                    int size = (int)(ms.Position - beforePos);
                    if (size != ExpectedIntegritySize)
                    {
                        // Layout change - surface as hard error rather
                        // than read potentially-wrong bytes from a
                        // mis-sized struct.
                        throw new InvalidOperationException(
                            "IntegritySettings size " + size + " != expected "
                            + ExpectedIntegritySize + " - the game's serialization "
                            + "format may have changed.");
                    }
                    var vanilla = new byte[16];
                    Array.Copy(rawExp.Data, (int)beforePos + 2, vanilla, 0, 16);
                    return vanilla;
                }
            }
            return null;
        }

        byte[] ProbeNormal(NormalExport ne)
        {
            StructPropertyData integ = null;
            foreach (var prop in ne.Data)
            {
                if (prop is StructPropertyData sd
                    && sd.Name != null && sd.Name.Value != null
                    && (sd.Name.Value.Value as string) == "IntegritySettings")
                {
                    integ = sd;
                    break;
                }
            }
            if (integ == null || integ.Value == null) return null;

            float w = 0, h = 0, v = 0, m = 0;
            bool hw = false, hh = false, hv = false, hm = false;
            foreach (var sub in integ.Value)
            {
                var fpd = sub as FloatPropertyData;
                if (fpd == null || fpd.Name == null || fpd.Name.Value == null) continue;
                var nm = fpd.Name.Value.Value as string;
                switch (nm)
                {
                    case "BlockWeight":                    w = fpd.Value; hw = true; break;
                    case "BlockMaxHorizontalLoad":         h = fpd.Value; hh = true; break;
                    case "BlockMaxVerticalLoad":           v = fpd.Value; hv = true; break;
                    case "BlockMinimumIntersectionExtent": m = fpd.Value; hm = true; break;
                }
            }
            if (!hw || !hh || !hv || !hm) return null;

            var bytes = new byte[16];
            BitConverter.GetBytes(w).CopyTo(bytes, 0);
            BitConverter.GetBytes(h).CopyTo(bytes, 4);
            BitConverter.GetBytes(v).CopyTo(bytes, 8);
            BitConverter.GetBytes(m).CopyTo(bytes, 12);
            return bytes;
        }

        // Finds the unique occurrence of `pattern` in `data`. Returns the
        // byte offset of the match, or -1 if the pattern doesn't appear or
        // appears more than once. Refusing to guess on multi-match avoids
        // patching the wrong location in chunks that happen to contain
        // similar float sequences elsewhere.
        static int FindUnique(byte[] data, byte[] pattern)
        {
            int firstHit = -1;
            int max = data.Length - pattern.Length;
            for (int i = 0; i <= max; i++)
            {
                bool match = true;
                for (int j = 0; j < pattern.Length; j++)
                {
                    if (data[i + j] != pattern[j]) { match = false; break; }
                }
                if (!match) continue;
                if (firstHit >= 0) return -1;   // ambiguous
                firstHit = i;
            }
            return firstHit;
        }

        // Diagnostic helper: counts occurrences for the log message when
        // FindUnique returns -1, so the user can tell "no match" (0)
        // apart from "ambiguous" (2+).
        static int CountOccurrences(byte[] data, byte[] pattern)
        {
            int hits = 0;
            int max = data.Length - pattern.Length;
            for (int i = 0; i <= max; i++)
            {
                bool match = true;
                for (int j = 0; j < pattern.Length; j++)
                {
                    if (data[i + j] != pattern[j]) { match = false; break; }
                }
                if (match) hits++;
            }
            return hits;
        }

        // Strips retoc's manifest mount-point prefix ("../../../") to yield
        // the canonical "R5/Content/..." path we use as the dictionary key.
        // Defensive: handles both forward and back slash variants since
        // retoc has been observed to mix them on Windows.
        static string StripMountPrefix(string rawPath)
        {
            if (string.IsNullOrEmpty(rawPath)) return rawPath;
            var p = rawPath;
            while (p.StartsWith("../") || p.StartsWith("..\\"))
                p = p.Substring(3);
            return p.Replace('\\', '/');
        }

        // Maps "C:\tmp\stab-staging\R5\Content\Gameplay\..." -> "R5/Content/Gameplay/..."
        // so the result matches the post-strip-mount-prefix keys in pathToChunk.
        static string ToRelativeR5Path(string assetPath, string rootDir)
        {
            var rel = assetPath;
            if (rel.StartsWith(rootDir, StringComparison.OrdinalIgnoreCase))
            {
                rel = rel.Substring(rootDir.Length)
                         .TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            }
            return rel.Replace('\\', '/');
        }

        // Writes manifest.json containing only the chunk_paths entries
        // whose chunkId is in keepChunks. Preserves mount_point + version
        // from the original. Stable iteration order so successive builds
        // produce byte-identical manifests when nothing changed (the
        // ordered traversal of keepChunks comes from the original chunk
        // enumeration).
        static void WriteFilteredManifest(JsonElement rootEl, JsonElement chunkPathsEl,
                                          HashSet<string> keepChunks, string manifestPath)
        {
            var sb = new StringBuilder();
            sb.Append('{');
            bool firstRoot = true;
            foreach (var prop in rootEl.EnumerateObject())
            {
                if (!firstRoot) sb.Append(',');
                firstRoot = false;
                sb.Append(JsonEncodedText.Encode(prop.Name).EncodedUtf8Bytes.Length == 0
                    ? "\"" + prop.Name + "\""
                    : "\"" + prop.Name + "\"");
                sb.Append(':');
                if (prop.Name == "chunk_paths")
                {
                    sb.Append('{');
                    bool firstChunk = true;
                    foreach (var entry in chunkPathsEl.EnumerateObject())
                    {
                        if (!keepChunks.Contains(entry.Name)) continue;
                        if (!firstChunk) sb.Append(',');
                        firstChunk = false;
                        sb.Append('"').Append(entry.Name).Append('"');
                        sb.Append(':');
                        sb.Append(JsonSerializer.Serialize(entry.Value.GetString() ?? ""));
                    }
                    sb.Append('}');
                }
                else
                {
                    sb.Append(prop.Value.GetRawText());
                }
            }
            sb.Append('}');
            File.WriteAllText(manifestPath, sb.ToString());
        }

        static void WriteFloatLE(byte[] buf, int offset, float value)
        {
            var bytes = BitConverter.GetBytes(value);
            // BitConverter is host-endian; UE5 cooked assets are
            // little-endian. Reverse if we ever run on a BE host.
            if (!BitConverter.IsLittleEndian) Array.Reverse(bytes);
            Array.Copy(bytes, 0, buf, offset, 4);
        }

        void LogLine(string msg)
        {
            if (Log != null) Log(msg);
        }
    }

    // Per-asset patch outcome. Patched=true means the 16-byte
    // IntegritySettings pattern was located + overwritten in the
    // corresponding zen chunk. Patched=false splits into:
    //   - excluded-by-skiplist:  IsSupportedAssetPath rejected; chunk dropped
    //   - no-integrity-settings: legitimate (asset lacks the property);
    //                            chunk kept unpatched
    //   - chunk-file-missing / no-chunk-mapping / pattern-match-count=N:
    //                            anomaly worth logging
    public sealed class BuildingStabilityAssetResult
    {
        public string AssetPath;            // absolute path of legacy uasset (probe-only)
        public string RelativePath;         // canonical "R5/Content/..." key
        public bool Patched;
        public string Reason;               // populated when Patched=false
        public string ChunkId;              // zen chunk id (16-char hex); null when no mapping
        public int IntegrityOffsetInChunk;  // byte offset of the 16-byte pattern; valid when Patched=true
        public float OldBlockWeight;
        public float OldBlockMaxHorizontalLoad;
        public float OldBlockMaxVerticalLoad;
        public float OldBlockMinimumIntersectionExtent;
    }
}
