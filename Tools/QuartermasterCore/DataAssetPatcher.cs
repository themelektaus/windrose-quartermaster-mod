using System;
using System.Collections.Generic;
using System.IO;
using UAssetAPI;
using UAssetAPI.ExportTypes;
using UAssetAPI.UnrealTypes;
using UAssetAPI.Unversioned;

namespace Windrose.Quartermaster.Core
{
    public sealed class DataAssetPatcher
    {
        public Action<string> Log;

        public DataAssetPatchResult Patch(
            string inputAssetPath,
            string outputAssetPath,
            string usmapPath,
            IReadOnlyDictionary<string, string> replacements,
            string newFolderName = null,
            bool requireAllHits = false)
        {
            if (string.IsNullOrEmpty(inputAssetPath))
                throw new ArgumentNullException("inputAssetPath");
            if (string.IsNullOrEmpty(outputAssetPath))
                throw new ArgumentNullException("outputAssetPath");
            if (string.IsNullOrEmpty(usmapPath))
                throw new ArgumentNullException("usmapPath");
            if (replacements == null || replacements.Count == 0)
                throw new ArgumentException("At least one replacement is required");
            if (!File.Exists(inputAssetPath))
                throw new FileNotFoundException("Legacy uasset not found: " + inputAssetPath);
            if (!File.Exists(usmapPath))
                throw new FileNotFoundException("Usmap not found: " + usmapPath);

            // UAssetAPI reads the .uexp sibling implicitly; it must sit next to the .uasset.
            var inUexpPath = Path.ChangeExtension(inputAssetPath, ".uexp");
            if (!File.Exists(inUexpPath))
            {
                throw new FileNotFoundException(
                    "Legacy uexp sibling not found: " + inUexpPath
                    + " - the patcher expects a uasset/uexp pair produced "
                    + "by `retoc to-legacy` (raw IoStore Zen packages cannot "
                    + "be patched directly).");
            }

            LogLine("Loading usmap: " + usmapPath);
            var mappings = new Usmap(usmapPath);
            LogLine("Loading uasset: " + inputAssetPath);
            var asset = new UAsset(inputAssetPath, UAssetIo.Ue, mappings);

            var perKeyHits = new Dictionary<string, int>(replacements.Count, StringComparer.Ordinal);
            foreach (var kvp in replacements)
            {
                perKeyHits[kvp.Key] = 0;
            }

            int totalRenamed = 0;
            var names = asset.GetNameMapIndexList();
            for (int i = 0; i < names.Count; i++)
            {
                var entry = names[i];
                if (entry == null || entry.Value == null) continue;

                if (replacements.TryGetValue(entry.Value, out var newValue))
                {
                    asset.SetNameReference(i, new FString(newValue, entry.Encoding));
                    LogLine("  NameMap[" + i + "]: " + entry.Value + " -> " + newValue);
                    perKeyHits[entry.Value] = perKeyHits[entry.Value] + 1;
                    totalRenamed++;
                }
            }

            // Exports cache an FName reference directly, so the NameMap rename above does not update ObjectName.
            int retargetedExports = 0;
            for (int i = 0; i < asset.Exports.Count; i++)
            {
                if (!(asset.Exports[i] is NormalExport ne)) continue;
                var on = ne.ObjectName;
                if (on == null || on.Value == null) continue;
                if (replacements.TryGetValue(on.Value.Value ?? string.Empty, out var newName))
                {
                    ne.ObjectName = FName.FromString(asset, newName);
                    LogLine("  Exports[" + i + "].ObjectName: " + on.Value.Value + " -> " + newName);
                    retargetedExports++;
                }
            }

            if (!string.IsNullOrEmpty(newFolderName))
            {
                LogLine("  FolderName: " + (asset.FolderName?.Value ?? "<null>") + " -> " + newFolderName);
                asset.FolderName = FString.FromString(newFolderName);
            }

            var missed = new List<string>();
            foreach (var kvp in perKeyHits)
            {
                if (kvp.Value == 0) missed.Add(kvp.Key);
            }
            if (missed.Count > 0)
            {
                LogLine("WARNING: " + missed.Count + " replacement key(s) found no NameMap match:");
                foreach (var m in missed) LogLine("  ! " + m);
                if (requireAllHits)
                {
                    throw new InvalidOperationException(
                        "Patch aborted: " + missed.Count + " replacement key(s) "
                        + "did not match any NameMap entry (requireAllHits=true). "
                        + "Missed keys: " + string.Join(", ", missed));
                }
            }

            var outDir = Path.GetDirectoryName(outputAssetPath);
            if (!string.IsNullOrEmpty(outDir)) Directory.CreateDirectory(outDir);
            LogLine("Writing patched uasset: " + outputAssetPath);
            asset.Write(outputAssetPath);

            var outUexp = Path.ChangeExtension(outputAssetPath, ".uexp");
            long outAssetBytes = File.Exists(outputAssetPath) ? new FileInfo(outputAssetPath).Length : 0;
            long outUexpBytes  = File.Exists(outUexp)         ? new FileInfo(outUexp).Length         : 0;

            return new DataAssetPatchResult
            {
                NameMapEntriesRenamed = totalRenamed,
                ExportsRetargeted = retargetedExports,
                ReplacementHits = perKeyHits,
                MissedReplacements = missed,
                OutputAssetPath = outputAssetPath,
                OutputUexpPath = outUexp,
                OutputAssetBytes = outAssetBytes,
                OutputUexpBytes = outUexpBytes,
            };
        }

        void LogLine(string msg) { if (Log != null) Log(msg); }
    }

    public sealed class DataAssetPatchResult
    {
        public int NameMapEntriesRenamed;
        public int ExportsRetargeted;
        public Dictionary<string, int> ReplacementHits;
        public List<string> MissedReplacements;
        public string OutputAssetPath;
        public string OutputUexpPath;
        public long OutputAssetBytes;
        public long OutputUexpBytes;
    }
}
