using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Windrose.Quartermaster.Core
{
    // Patches R5BLRecipeData.CookingProcessDuration (top-level integer/float)
    // on every recipe whose family multiplier is non-null in
    // profile.Globals.ProductionTimes.
    //
    // Vanilla CookingProcessDuration distribution (counts from
    // Sources/Vanilla/.../Recipes/ on 5.6):
    //   1644 recipes -> 0    (trade-only, instant settlement; we skip these)
    //    390 recipes -> 1    (trivial-fast recipes; skipped by default)
    //    148 recipes -> 4200 (Trade Outpost NPC order wait, ~1h10)
    //     25 recipes -> 1800 (kiln/tannery/long-furnace runs)
    //     ...        -> mixed (0.5, 10, 30, 45, 90 etc.)
    //
    // The patcher only TOUCHES recipes whose family is active AND whose
    // vanilla duration is greater than zero. Vanilla=0 ("instant") recipes
    // are left at zero - multiplying by 0.5 still yields 0 anyway.
    //
    // Conflict-merge: the BuyerPatcher and SellerPatcher write their
    // recipe edits into the same tmpDir tree we write into. If a recipe's
    // output file already exists (because Buyer/Seller edited it), the
    // patcher loads THAT file as the baseline instead of vanilla, applies
    // the duration multiplier, and writes the merged result back. This
    // preserves the buyer/seller cost/result edits while ALSO patching
    // the duration.
    //
    // Output formatting matches vanilla: tab indent (size 1), CRLF line
    // endings, trailing CRLF.
    public sealed class CookingDurationPatcher
    {
        // In-pak prefix (relative to repak's root) where patched recipe
        // files land. Mirrors the layout BuyerPatcher uses.
        const string RecipesVanillaRoot = "R5/Plugins/R5BusinessRules/Content/Recipes";

        static readonly UTF8Encoding Utf8NoBom = new UTF8Encoding(false);

        // Per-family multiplier table. null = family inactive (skip every
        // recipe of that family).
        public sealed class FamilyMultipliers
        {
            public double? Smelting;
            public double? Kiln;
            public double? Tanning;
            public double? Milling;
            public double? BuildingBits;
            public double? Decoration;
            public double? ArmorWeapon;
            public double? TradeOutpost;
            public double? Other;

            public double? Get(RecipeFamily family)
            {
                switch (family)
                {
                    case RecipeFamily.Smelting:     return Smelting;
                    case RecipeFamily.Kiln:         return Kiln;
                    case RecipeFamily.Tanning:      return Tanning;
                    case RecipeFamily.Milling:      return Milling;
                    case RecipeFamily.BuildingBits: return BuildingBits;
                    case RecipeFamily.Decoration:   return Decoration;
                    case RecipeFamily.ArmorWeapon:  return ArmorWeapon;
                    case RecipeFamily.TradeOutpost: return TradeOutpost;
                    case RecipeFamily.Other:        return Other;
                    default: return null;
                }
            }

            public bool AnyActive()
            {
                return IsActive(Smelting) || IsActive(Kiln) || IsActive(Tanning)
                    || IsActive(Milling) || IsActive(BuildingBits)
                    || IsActive(Decoration) || IsActive(ArmorWeapon)
                    || IsActive(TradeOutpost) || IsActive(Other);
            }

            static bool IsActive(double? m)
            {
                return m.HasValue && m.Value > 0.0 && Math.Abs(m.Value - 1.0) > 1e-9;
            }
        }

        public CookingDurationPatchResult PatchToDirectory(
            string vanillaRecipesDir, string outDir, FamilyMultipliers families)
        {
            if (string.IsNullOrEmpty(vanillaRecipesDir)) throw new ArgumentNullException("vanillaRecipesDir");
            if (string.IsNullOrEmpty(outDir)) throw new ArgumentNullException("outDir");
            if (families == null) throw new ArgumentNullException("families");
            if (!Directory.Exists(vanillaRecipesDir)) throw new DirectoryNotFoundException(vanillaRecipesDir);

            Directory.CreateDirectory(outDir);
            var result = new CookingDurationPatchResult();
            if (!families.AnyActive()) return result;

            var vanillaRoot = Path.GetFullPath(vanillaRecipesDir);
            var recipesOutRoot = Path.Combine(outDir,
                RecipesVanillaRoot.Replace('/', Path.DirectorySeparatorChar));

            foreach (var vanillaPath in Directory.EnumerateFiles(
                vanillaRoot, "*.json", SearchOption.AllDirectories))
            {
                result.Scanned++;

                var stem = Path.GetFileNameWithoutExtension(vanillaPath);
                var rel = vanillaPath.Substring(vanillaRoot.Length).TrimStart(
                    Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                var outPath = Path.Combine(recipesOutRoot, rel);

                // If BuyerPatcher / SellerPatcher already wrote this file,
                // load it as the baseline so we preserve their edits.
                // Otherwise load vanilla.
                bool mergedWithTrade = File.Exists(outPath);
                var sourcePath = mergedWithTrade ? outPath : vanillaPath;

                JsonObject root;
                try
                {
                    var node = JsonNode.Parse(File.ReadAllText(sourcePath, Encoding.UTF8));
                    root = node as JsonObject;
                    if (root == null) { result.Skipped++; continue; }
                }
                catch (JsonException)
                {
                    result.Skipped++;
                    continue;
                }

                // Read vanilla duration. If the field is missing or zero,
                // skip - scaling 0 still gives 0 and patching it just to
                // re-emit the file would bloat the pak.
                if (!(root["CookingProcessDuration"] is JsonValue durVal))
                {
                    result.Skipped++;
                    continue;
                }

                double vanillaDur;
                if (!TryReadNumber(durVal, out vanillaDur))
                {
                    result.Skipped++;
                    continue;
                }

                if (vanillaDur <= 0.0)
                {
                    result.Skipped++;
                    continue;
                }

                // Classify the recipe and look up its family multiplier.
                var family = RecipeFamilyClassifier.Classify(root, stem);
                var mul = families.Get(family);
                if (!mul.HasValue || Math.Abs(mul.Value - 1.0) < 1e-9)
                {
                    result.SkippedFamilyInactive++;
                    continue;
                }

                var effective = vanillaDur * mul.Value;
                // Floor at 0.1s. Below that the engine treats it as
                // instant anyway; using exact zero would change semantics
                // (vanilla=0 means "instant trade settle", which the
                // gameplay code branches on).
                if (effective < 0.1) effective = 0.1;

                // Preserve original numeric shape when possible: if the
                // vanilla value was an integer, emit an integer; otherwise
                // emit a double rounded to one decimal so JSON output
                // stays compact and readable.
                JsonValue newVal;
                if (IsWholeNumber(vanillaDur) && IsWholeNumber(effective))
                    newVal = JsonValue.Create((long)Math.Round(effective));
                else
                    newVal = JsonValue.Create(Math.Round(effective, 1));

                root["CookingProcessDuration"] = newVal;

                Directory.CreateDirectory(Path.GetDirectoryName(outPath));
                File.WriteAllBytes(outPath, SerializeWithTabsAndCrlf(root));

                result.Written++;
                if (mergedWithTrade) result.MergedWithTrade++;
                result.PatchedRecipes.Add(new CookingDurationAssetResult
                {
                    Stem = stem,
                    Family = family,
                    Multiplier = mul.Value,
                    VanillaDuration = vanillaDur,
                    EffectiveDuration = effective,
                });

                // Family-level aggregates for the build response.
                CookingFamilySummary summary;
                if (!result.FamilySummaries.TryGetValue(family, out summary))
                {
                    summary = new CookingFamilySummary
                    {
                        Family = family,
                        Multiplier = mul.Value,
                    };
                    result.FamilySummaries[family] = summary;
                }
                summary.AssetCount++;
                summary.VanillaSum += vanillaDur;
                summary.EffectiveSum += effective;
            }

            return result;
        }

        static bool TryReadNumber(JsonValue v, out double n)
        {
            if (v.TryGetValue<long>(out var l)) { n = l; return true; }
            if (v.TryGetValue<double>(out var d)) { n = d; return true; }
            n = 0.0;
            return false;
        }

        static bool IsWholeNumber(double d)
        {
            return Math.Abs(d - Math.Round(d)) < 1e-9;
        }

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

    public sealed class CookingDurationPatchResult
    {
        public int Scanned;
        public int Written;
        public int Skipped;
        public int SkippedFamilyInactive;
        public int MergedWithTrade;
        public List<CookingDurationAssetResult> PatchedRecipes = new List<CookingDurationAssetResult>();
        public Dictionary<RecipeFamily, CookingFamilySummary> FamilySummaries
            = new Dictionary<RecipeFamily, CookingFamilySummary>();
    }

    public sealed class CookingDurationAssetResult
    {
        public string Stem;
        public RecipeFamily Family;
        public double Multiplier;
        public double VanillaDuration;
        public double EffectiveDuration;
    }

    public sealed class CookingFamilySummary
    {
        public RecipeFamily Family;
        public double Multiplier;
        public int AssetCount;
        public double VanillaSum;
        public double EffectiveSum;
    }
}
