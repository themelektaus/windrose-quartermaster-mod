using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Windrose.Quartermaster.Core
{
    // Patches R5BLCropParams.GrowthDuration (UE FTimespan; 1 tick = 100 ns)
    // on every DA_Crop_*.json under R5BusinessRules/Content/Farming/Crops.
    //
    // Reference: the "Faster crop growth" mod ships every crop with
    // GrowthDuration = 9_000_000_000 ticks (~15 min). Most vanilla crops
    // already sit at that value, but Grape and Pineapple are much faster
    // (80_000_000 ticks = ~8 s) - the multiplier scales the vanilla value
    // proportionally so a 0.5x slider halves whatever the crop's own
    // GrowthDuration is.
    //
    // The patcher writes into a parallel directory tree mirroring the
    // in-pak layout so it can be co-packed with the BuyerPatcher /
    // StackPatcher output by repak.
    //
    // Output formatting matches vanilla: tab indent (size 1), CRLF line
    // endings, trailing CRLF. Only the GrowthDuration value changes -
    // the surrounding JSON object is preserved in-place.
    public sealed class CropGrowthPatcher
    {
        // In-pak prefix (relative to repak's root) where the patched
        // DA_Crop_*.json files land. Mirrors WindroseGameSecrets.
        // FarmingCropsPath so the output tree can be repak'd as-is.
        const string CropsVanillaRoot = "R5/Plugins/R5BusinessRules/Content/Farming/Crops";

        // No-BOM UTF-8; CRLF handled inline by the JsonWriter options.
        static readonly UTF8Encoding Utf8NoBom = new UTF8Encoding(false);

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

            // Multiplier ~= 1.0 -> no patch (caller already null-collapsed,
            // but keep a safety check so an accidental 1.0 doesn't bloat
            // the pak with vanilla-identical files).
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

                // GrowthDuration is a 64-bit FTimespan tick count, written
                // as a plain JSON integer. Read as long, multiply, clamp
                // to a sensible minimum (1 tick is the engine floor).
                long vanillaTicks;
                if (!gd.TryGetValue<long>(out vanillaTicks))
                {
                    // Could be serialised as a JSON number with a decimal
                    // point on a rounded vanilla value; fall back to double
                    // and round.
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

        // Tab-indent (size 1), CRLF line endings, trailing CRLF - matches
        // the format every other patcher emits so all patched JSONs share
        // one canonical shape inside the pak.
        static byte[] SerializeWithTabsAndCrlf(JsonObject root)
        {
            using var ms = new MemoryStream();
            var writerOptions = new JsonWriterOptions
            {
                Indented = true,
                IndentCharacter = '\t',
                IndentSize = 1,
                NewLine = "\r\n",
                Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            };
            using (var writer = new Utf8JsonWriter(ms, writerOptions))
            {
                root.WriteTo(writer);
            }
            ms.WriteByte((byte)'\r');
            ms.WriteByte((byte)'\n');
            return ms.ToArray();
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
