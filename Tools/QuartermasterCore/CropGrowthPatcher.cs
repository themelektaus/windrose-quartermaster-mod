using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using static Windrose.Quartermaster.Core.R5Json;

namespace Windrose.Quartermaster.Core
{
    public sealed class CropGrowthPatcher
    {
        const string CropsVanillaRoot = "R5/Plugins/R5BusinessRules/Content/Farming/Crops";

        public CropGrowthPatchResult PatchToDirectory(
            string vanillaCropsDir, string outDir, double multiplier)
        {
            if (string.IsNullOrEmpty(vanillaCropsDir)) throw new ArgumentNullException("vanillaCropsDir");
            if (string.IsNullOrEmpty(outDir)) throw new ArgumentNullException("outDir");
            if (!Directory.Exists(vanillaCropsDir)) throw new DirectoryNotFoundException(vanillaCropsDir);
            if (!(multiplier > 0.0))
                throw new ArgumentException("multiplier must be > 0", "multiplier");

            Directory.CreateDirectory(outDir);
            var result = new CropGrowthPatchResult { Multiplier = multiplier };

            if (Math.Abs(multiplier - 1.0) < 1e-9)
                return result;

            var rootFull = Path.GetFullPath(vanillaCropsDir);
            foreach (var path in Directory.EnumerateFiles(rootFull, "DA_Crop_*.json", SearchOption.AllDirectories))
            {
                result.Scanned++;
                JsonObject root;
                try
                {
                    var node = JsonNode.Parse(File.ReadAllText(path, Encoding.UTF8));
                    root = node as JsonObject;
                    if (root == null) { result.Skipped++; continue; }
                }
                catch (JsonException)
                {
                    result.Skipped++;
                    continue;
                }

                if (!(root["GrowthDuration"] is JsonValue gd))
                {
                    result.Skipped++;
                    continue;
                }

                // GrowthDuration is a 64-bit FTimespan tick count.
                long vanillaTicks;
                if (!gd.TryGetValue<long>(out vanillaTicks))
                {
                    double vanillaDouble;
                    if (!gd.TryGetValue<double>(out vanillaDouble))
                    {
                        result.Skipped++;
                        continue;
                    }
                    vanillaTicks = (long)Math.Round(vanillaDouble);
                }

                var newTicks = (long)Math.Round(vanillaTicks * multiplier);
                if (newTicks < 1) newTicks = 1;
                if (newTicks == vanillaTicks)
                {
                    result.Skipped++;
                    continue;
                }

                root["GrowthDuration"] = JsonValue.Create(newTicks);

                var stem = Path.GetFileNameWithoutExtension(path);
                var rel = path.Substring(rootFull.Length).TrimStart(
                    Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                var outPath = Path.Combine(outDir,
                    CropsVanillaRoot.Replace('/', Path.DirectorySeparatorChar),
                    rel);
                Directory.CreateDirectory(Path.GetDirectoryName(outPath));
                File.WriteAllBytes(outPath, SerializeWithTabsAndCrlf(root));

                result.Written++;
                result.PatchedCrops.Add(new CropGrowthAssetResult
                {
                    Stem = stem,
                    VanillaTicks = vanillaTicks,
                    EffectiveTicks = newTicks,
                });
            }

            return result;
        }

    }

    public sealed class CropGrowthPatchResult
    {
        public double Multiplier;
        public int Scanned;
        public int Written;
        public int Skipped;
        public List<CropGrowthAssetResult> PatchedCrops = new List<CropGrowthAssetResult>();
    }

    public sealed class CropGrowthAssetResult
    {
        public string Stem;
        public long VanillaTicks;
        public long EffectiveTicks;
    }
}
