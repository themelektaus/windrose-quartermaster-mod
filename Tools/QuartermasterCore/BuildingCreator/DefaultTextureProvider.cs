using System;
using System.Collections.Generic;
using System.IO;

namespace Windrose.Quartermaster.Core.BuildingCreator
{
    // Shipped VT default textures the MI clones can reference instead of a user-picked custom. Staged into the per-build tree once so the clones always resolve them under the mod's output namespace.
    public static class DefaultTextureProvider
    {
        // Order is the UI dropdown order.
        public static readonly string[] Stems = new[]
        {
            "T_White",
            "T_NormalFlat",
            "T_MTRMDefault",
            "T_MTRMGlass",
            "T_MTRMZero",
            "T_MTRMOne"
        };

        static readonly string[] Extensions = new[] { ".uasset", ".uexp", ".ubulk" };

        // Skip-if-exists so a user-cooked override with the same stem wins. Missing files are non-fatal (logged), so a fresh checkout without the binary triplets doesn't silently ship a pak with no defaults.
        public static int StageInto(WindrosePaths paths, string stagingItemsDir, string usmapPath, Action<string> log)
        {
            if (paths == null) throw new ArgumentNullException("paths");
            if (string.IsNullOrEmpty(stagingItemsDir)) throw new ArgumentNullException("stagingItemsDir");
            Directory.CreateDirectory(stagingItemsDir);

            var srcDir = paths.BuildingDefaultTexturesDir;
            if (!Directory.Exists(srcDir))
            {
                if (log != null) log("  warn: default-textures folder missing: " + srcDir
                    + " - buildings that reference " + string.Join(" / ", Stems) + " may render broken textures");
                return 0;
            }

            int copied = 0;
            int skipped = 0;
            int missing = 0;
            // The .uasset header drives the package-resolution check, so FolderName normalization runs once per stem.
            var freshStems = new List<string>();
            foreach (var stem in Stems)
            {
                bool stemFreshlyCopied = false;
                foreach (var ext in Extensions)
                {
                    var srcFile = Path.Combine(srcDir, stem + ext);
                    if (!File.Exists(srcFile))
                    {
                        if (string.Equals(ext, ".ubulk", StringComparison.OrdinalIgnoreCase))
                        {
                            // .ubulk is optional - absent when the texture has no bulk-data.
                            continue;
                        }
                        if (log != null) log("  warn: default-texture file missing: " + srcFile);
                        missing++;
                        continue;
                    }

                    var dstFile = Path.Combine(stagingItemsDir, stem + ext);
                    if (File.Exists(dstFile))
                    {
                        // Pre-existing staged file (user-cooked override) wins.
                        skipped++;
                        continue;
                    }
                    File.Copy(srcFile, dstFile, overwrite: false);
                    copied++;
                    if (string.Equals(ext, ".uasset", StringComparison.OrdinalIgnoreCase))
                        stemFreshlyCopied = true;
                }
                if (stemFreshlyCopied) freshStems.Add(stem);
            }

            // Without this, the iostore loader fails to resolve the texture (chunk path vs FolderName mismatch) and the MI falls back to the vanilla parent's texture.
            int normalized = 0;
            int normalizeFailures = 0;
            if (!string.IsNullOrEmpty(usmapPath) && File.Exists(usmapPath))
            {
                foreach (var stem in freshStems)
                {
                    var stagedAsset = Path.Combine(stagingItemsDir, stem + ".uasset");
                    if (BuildingPatcher.NormalizeAssetSelfPath(stagedAsset, usmapPath, log, out var err))
                        normalized++;
                    else if (err != null)
                        normalizeFailures++;
                }
            }
            else
            {
                if (log != null) log("  warn: default-texture FolderName normalize SKIPPED (usmapPath missing or not found) - "
                    + string.Join(" / ", Stems) + " may not load in-game; "
                    + "MIs will fall back to the vanilla parent's textures");
            }

            if (log != null)
            {
                if (copied + skipped + missing == 0)
                {
                    log("  (default textures: nothing staged)");
                }
                else
                {
                    log("  default textures: " + copied + " copied"
                        + (skipped > 0 ? ", " + skipped + " pre-existing (user-cooked override)" : "")
                        + (missing > 0 ? ", " + missing + " file(s) MISSING from " + srcDir : "")
                        + (normalized > 0 ? ", " + normalized + " self-path normalized to " + WindrosePaths.ModItemsPackagePath + "<stem>" : "")
                        + (normalizeFailures > 0 ? ", " + normalizeFailures + " normalize FAILURE(s)" : "")
                        + " - " + string.Join(", ", Stems));
                }
            }
            return copied;
        }

        public static IReadOnlyList<string> GetStems() => Stems;
    }
}
