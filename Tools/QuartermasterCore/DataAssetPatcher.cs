using System;
using System.Collections.Generic;
using System.IO;
using UAssetAPI;
using UAssetAPI.ExportTypes;
using UAssetAPI.UnrealTypes;
using UAssetAPI.Unversioned;

namespace Windrose.Quartermaster.Core
{
    // Renames FName entries inside a Legacy .uasset/.uexp pair so a cloned
    // Vanilla DataAsset (e.g. DA_BI_Paintings_HighLands_02) ends up pointing
    // at user-supplied mesh/texture/locale refs while inheriting every other
    // property (snap points, collision profile, FX, sound, crafting recipe).
    //
    // Workflow context:
    //   game IoStore (.ucas)
    //     -> retoc to-legacy --filter <stem>  (Zen package -> Legacy .uasset)
    //     -> THIS CLASS                       (rename FName strings)
    //     -> retoc to-zen                     (Legacy -> IoStore triplet)
    //
    // The patcher is agnostic to *which* DA it is patching. The caller hands
    // it a Dictionary<string, string> Replacements where every key that
    // appears in the NameMap gets rewritten to its mapped value. Three flavor
    // hints for callers:
    //
    //   * Asset stem names live in the NameMap verbatim
    //     ("SM_Paintings_HighLands_02" - no slashes, no extension).
    //   * Package paths live there too, with the leading slash
    //     ("/Game/Environment/.../SM_Paintings_HighLands_02").
    //   * Replace BOTH so the engine's soft-object-path resolver picks up
    //     the new asset under its new package path.
    //
    // Same Patcher contract as ShipMusicPatcher/PickupBlueprintPatcher:
    // a single Patch() entry point, an injectable Log delegate, and a
    // structured result object so the orchestrator can report counts.
    public sealed class DataAssetPatcher
    {
        public Action<string> Log;

        // Patches the asset at inputAssetPath into outputAssetPath (UAssetAPI
        // emits the matching .uexp sibling alongside). usmapPath is required
        // for UE5 unversioned property layouts; without it UAssetAPI can't
        // safely round-trip an asset that uses unversioned serialization.
        //
        // Replacements is a dictionary of "old NameMap string" -> "new value".
        // If the same string appears multiple times in the NameMap (rare but
        // legal for distinct FName.Number variants) ALL occurrences get
        // renamed. Missing keys (i.e. a replacement that finds no NameMap
        // entry) are NOT an error by default - the result object reports
        // counts so the caller can decide whether to fail; if RequireAllHits
        // is true, the patcher throws when any replacement key found zero
        // matches.
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

            // The .uexp sibling is read implicitly by UAssetAPI when the
            // ctor opens the .uasset. If it lives at a non-default path the
            // caller is responsible for File.Copy()-ing both into the same
            // directory before calling us (mirrors ShipMusicPatcher).
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
            var asset = new UAsset(inputAssetPath, EngineVersion.VER_UE5_6, mappings);

            // Step 1: Rename NameMap entries that match a replacement key.
            // Build per-key hit counters so we can report which replacements
            // were dead-letters (typo in the caller's table, or the vanilla
            // asset's NameMap layout has shifted).
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

            // Step 2: Defensive export retargeting. If any NormalExport's
            // ObjectName still resolves to one of the old keys (typically
            // the asset's own self-name), point it at the renamed entry.
            // The NameMap rename in step 1 already shifts FName resolution,
            // but exports also cache an FName reference directly and we
            // want to make sure ObjectName.ToString() reflects the new
            // value (matters for asset-registry lookups).
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

            // Step 3: FolderName overrides where the engine thinks this
            // package lives (matters for cooked-load path resolution). Caller
            // passes the new "/Game/<...>/<DAStem>" prefix without extension;
            // if null we leave FolderName as-is (uncommon but legal).
            if (!string.IsNullOrEmpty(newFolderName))
            {
                LogLine("  FolderName: " + (asset.FolderName?.Value ?? "<null>") + " -> " + newFolderName);
                asset.FolderName = FString.FromString(newFolderName);
            }

            // Step 4: Sanity check. If a replacement key produced no hits,
            // the caller's table is wrong or the asset's NameMap doesn't
            // contain that exact string. Default behavior is "log + continue"
            // (many replacement tables intentionally include keys that may
            // or may not be present, e.g. optional FX refs). The optional
            // requireAllHits flag turns this into a hard fail for callers
            // that want strict validation.
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

            // Step 5: Write the patched asset out. UAssetAPI emits both
            // .uasset and .uexp at outputAssetPath (the .uexp sibling path
            // is derived from outputAssetPath's directory + stem).
            var outDir = Path.GetDirectoryName(outputAssetPath);
            if (!string.IsNullOrEmpty(outDir)) Directory.CreateDirectory(outDir);
            LogLine("Writing patched uasset: " + outputAssetPath);
            asset.Write(outputAssetPath);

            // Report sizes so the orchestrator can sanity-check that the
            // .uexp came out at a reasonable size (a write that nukes the
            // export body shows up as a 0-byte .uexp).
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

    // Per-call result object. Mirrors the shape of the other patcher results
    // (ShipMusicPatchResult etc) so a future BuildPipeline orchestrator can
    // collect them uniformly.
    public sealed class DataAssetPatchResult
    {
        public int NameMapEntriesRenamed;
        public int ExportsRetargeted;
        // Per-key counter: how many NameMap entries matched each replacement
        // key. Useful for diagnostics when a vanilla asset's layout shifts.
        public Dictionary<string, int> ReplacementHits;
        // Subset of replacement keys whose hit-count was 0. Always populated
        // (empty list if every key found at least one match).
        public List<string> MissedReplacements;
        // Output paths (absolute).
        public string OutputAssetPath;
        public string OutputUexpPath;
        // Output file sizes (bytes). Both should be > 0 for a sane patch.
        public long OutputAssetBytes;
        public long OutputUexpBytes;
    }
}
