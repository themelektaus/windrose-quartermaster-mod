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
    public sealed class BuildingStabilityPatcher
    {
        public const string AssetFilterStem = "DA_BI";

        public static bool IsSupportedAssetPath(string relativeAssetPath)
        {
            if (string.IsNullOrEmpty(relativeAssetPath)) return false;
            var p = relativeAssetPath.Replace('\\', '/');

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

            if (p.EndsWith("/BuildingUtilities/DA_BI_Utilities_BuildingCenterT01.uasset",
                    StringComparison.OrdinalIgnoreCase))
                return false;

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

        public const float TargetBlockWeight = 0.0f;
        public const float TargetBlockMaxHorizontalLoad = 10_000_000.0f;
        public const float TargetBlockMaxVerticalLoad = 10_000_000.0f;
        public const float TargetBlockMinimumIntersectionExtent = 0.0f;

        const int ExpectedIntegritySize = 18;

        public Action<string> Log;

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

                if (result.ChunkId != null)
                    keepChunks.Add(result.ChunkId);
            }

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

            WriteFilteredManifest(manifest.RootElement, chunkPathsEl,
                                  keepChunks, manifestPath);

            LogLine("Stability: kept " + keepChunks.Count + " chunk(s), "
                    + "dropped " + chunksDropped + " unrelated chunk(s), "
                    + "excluded " + excluded + " non-supported asset(s)");

            return results;
        }

        BuildingStabilityAssetResult PatchOneAsset(
            string assetPath,
            string relAssetPath,
            Usmap mappings,
            string chunksDir,
            Dictionary<string, string> pathToChunk)
        {
            if (!pathToChunk.TryGetValue(relAssetPath, out var chunkId))
            {
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

        static string StripMountPrefix(string rawPath)
        {
            if (string.IsNullOrEmpty(rawPath)) return rawPath;
            var p = rawPath;
            while (p.StartsWith("../") || p.StartsWith("..\\"))
                p = p.Substring(3);
            return p.Replace('\\', '/');
        }

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
            // Target bytes are little-endian; reverse on a big-endian host.
            if (!BitConverter.IsLittleEndian) Array.Reverse(bytes);
            Array.Copy(bytes, 0, buf, offset, 4);
        }

        void LogLine(string msg)
        {
            if (Log != null) Log(msg);
        }
    }

    public sealed class BuildingStabilityAssetResult
    {
        public string AssetPath;
        public string RelativePath;
        public bool Patched;
        public string Reason;
        public string ChunkId;
        public int IntegrityOffsetInChunk;
        public float OldBlockWeight;
        public float OldBlockMaxHorizontalLoad;
        public float OldBlockMaxVerticalLoad;
        public float OldBlockMinimumIntersectionExtent;
    }
}
