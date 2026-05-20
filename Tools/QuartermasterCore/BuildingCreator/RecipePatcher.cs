using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;

namespace Windrose.Quartermaster.Core.BuildingCreator
{
    // Clones a vanilla recipe JSON (e.g. DA_RD_BuildObject_Deco_Paintings_T02.json)
    // into the build-tmp staging tree under a per-building stem, rewriting:
    //
    //   * RecipeCost  : replaced by the user-edited cost list (Item + Count)
    //                   when the profile supplies one. Empty list / null
    //                   on the profile = keep the vanilla defaults.
    //   * RecipeTag   : forced to a per-building unique tag
    //                   ("RecipeData.QM.<BuildingId>") so we don't collide
    //                   with the vanilla recipe still in the game.
    //
    // Everything else (CraftRequirement, ComfortRequirements, UIData,
    // CookingProcessDuration, ...) gets cloned through verbatim - we don't
    // need to touch them, the vanilla parent has perfectly fine defaults.
    //
    // Output lands at
    //   <stagingDir>/R5/Plugins/R5BusinessRules/Content/Recipes/Building/
    //     Items/Decorations/DA_RD_Qm<BuildingId>.json
    // which gets picked up by the existing repak.exe legacy-pak step
    // (same pipeline path the BuildingItems.csv synthesis already uses
    // - JSON/CSV/ini all ride in the legacy pak, not the IoStore composite).
    public sealed class RecipePatcher
    {
        public Action<string> Log;

        public RecipePatchResult Patch(
            string vanillaRecipeJsonPath,
            string outputDir,
            string buildingId,
            IList<(string ItemPath, int Count)> userRecipeCost)
        {
            if (string.IsNullOrEmpty(vanillaRecipeJsonPath))
                throw new ArgumentNullException("vanillaRecipeJsonPath");
            if (string.IsNullOrEmpty(outputDir))
                throw new ArgumentNullException("outputDir");
            if (string.IsNullOrEmpty(buildingId))
                throw new ArgumentNullException("buildingId");
            if (!File.Exists(vanillaRecipeJsonPath))
                throw new FileNotFoundException(
                    "Vanilla recipe JSON not found: " + vanillaRecipeJsonPath
                    + " (run Setup to extract the R5BusinessRules recipes).");

            LogLine("Reading vanilla recipe: " + vanillaRecipeJsonPath);
            var src = File.ReadAllText(vanillaRecipeJsonPath);
            using var doc = JsonDocument.Parse(src);
            var root = doc.RootElement;

            // Output file naming + per-building stem. Mirrors the
            // DataAssetPatcher's pattern (DA_BI_Qm<BuildingId>) so the
            // file naming is predictable across both asset types.
            var outStem = "DA_RD_Qm" + buildingId;
            var outFileName = outStem + ".json";
            var outAbs = Path.Combine(outputDir, outFileName);
            Directory.CreateDirectory(outputDir);

            // Per-building RecipeTag. Vanilla tags follow
            // "RecipeData.Deco.<Family>.T<Tier>.<Variant>" - we mirror the
            // prefix but namespace under .QM so a future vanilla-tag rename
            // doesn't collide. Tag must be unique across the loaded set
            // (UE checks at GameplayTagsManager init); per-building id
            // gives us that guarantee for free.
            var newRecipeTag = "RecipeData.QM." + buildingId;

            // We rebuild the JSON object by walking the source's root
            // properties. JsonDocument is read-only so we write through
            // a Utf8JsonWriter; this preserves field order and saves a
            // round-trip through a mutable POCO.
            var costEntries = userRecipeCost;
            bool costOverridden = costEntries != null;  // null = keep vanilla; empty list = explicit free

            int newCostRows = 0;
            int keptVanillaRows = 0;

            using var ms = new MemoryStream();
            using (var writer = new Utf8JsonWriter(ms, new JsonWriterOptions
            {
                Indented = true,
                Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            }))
            {
                writer.WriteStartObject();
                foreach (var prop in root.EnumerateObject())
                {
                    switch (prop.Name)
                    {
                        case "RecipeCost":
                            writer.WritePropertyName("RecipeCost");
                            if (costOverridden)
                            {
                                writer.WriteStartArray();
                                foreach (var (itemPath, count) in costEntries)
                                {
                                    if (string.IsNullOrWhiteSpace(itemPath)) continue;
                                    var c = count < 0 ? 0 : count;
                                    writer.WriteStartObject();
                                    writer.WriteString("Item", itemPath);
                                    writer.WriteNumber("Count", c);
                                    writer.WriteEndObject();
                                    newCostRows++;
                                }
                                writer.WriteEndArray();
                            }
                            else
                            {
                                // Vanilla pass-through.
                                if (prop.Value.ValueKind == JsonValueKind.Array)
                                    keptVanillaRows = prop.Value.GetArrayLength();
                                prop.Value.WriteTo(writer);
                            }
                            break;

                        case "RecipeTag":
                            writer.WritePropertyName("RecipeTag");
                            writer.WriteStartObject();
                            writer.WriteString("TagName", newRecipeTag);
                            writer.WriteEndObject();
                            break;

                        default:
                            // Verbatim clone of every other top-level
                            // field (CraftRequirement, UIData, etc).
                            prop.WriteTo(writer);
                            break;
                    }
                }
                writer.WriteEndObject();
            }

            File.WriteAllBytes(outAbs, ms.ToArray());

            LogLine("Wrote recipe: " + outAbs);
            if (costOverridden)
                LogLine("  RecipeCost: " + newCostRows + " user row(s)");
            else
                LogLine("  RecipeCost: " + keptVanillaRows + " vanilla row(s) (no user override)");
            LogLine("  RecipeTag : " + newRecipeTag);

            return new RecipePatchResult
            {
                OutputJsonPath  = outAbs,
                OutputStem      = outStem,
                NewRecipeTag    = newRecipeTag,
                RecipeCostRows  = costOverridden ? newCostRows : keptVanillaRows,
                CostOverridden  = costOverridden,
            };
        }

        // Reads the user-facing default RecipeCost rows from a vanilla
        // recipe JSON. Used by the Buildings endpoint's inspect-recipe
        // handler to pre-fill the cost editor when the user picks a
        // template. Skips the full JSON-rewrite path - just an array
        // walk + projection.
        public static List<(string ItemPath, int Count)> ReadDefaultRecipeCost(
            string vanillaRecipeJsonPath)
        {
            var result = new List<(string, int)>();
            if (string.IsNullOrEmpty(vanillaRecipeJsonPath)) return result;
            if (!File.Exists(vanillaRecipeJsonPath)) return result;

            using var doc = JsonDocument.Parse(File.ReadAllText(vanillaRecipeJsonPath));
            if (!doc.RootElement.TryGetProperty("RecipeCost", out var arr)) return result;
            if (arr.ValueKind != JsonValueKind.Array) return result;
            foreach (var row in arr.EnumerateArray())
            {
                if (row.ValueKind != JsonValueKind.Object) continue;
                string item = "";
                int count = 0;
                if (row.TryGetProperty("Item", out var itemEl)
                    && itemEl.ValueKind == JsonValueKind.String)
                    item = itemEl.GetString() ?? "";
                if (row.TryGetProperty("Count", out var cEl)
                    && cEl.ValueKind == JsonValueKind.Number)
                    count = cEl.GetInt32();
                if (!string.IsNullOrEmpty(item))
                    result.Add((item, count));
            }
            return result;
        }

        // Reads the vanilla RecipeTag string for diagnostics surface.
        public static string ReadVanillaRecipeTag(string vanillaRecipeJsonPath)
        {
            if (string.IsNullOrEmpty(vanillaRecipeJsonPath)) return "";
            if (!File.Exists(vanillaRecipeJsonPath)) return "";
            using var doc = JsonDocument.Parse(File.ReadAllText(vanillaRecipeJsonPath));
            if (!doc.RootElement.TryGetProperty("RecipeTag", out var tagEl)) return "";
            if (tagEl.ValueKind != JsonValueKind.Object) return "";
            if (!tagEl.TryGetProperty("TagName", out var name)) return "";
            if (name.ValueKind != JsonValueKind.String) return "";
            return name.GetString() ?? "";
        }

        void LogLine(string msg) { if (Log != null) Log(msg); }
    }

    public sealed class RecipePatchResult
    {
        public string OutputJsonPath;
        public string OutputStem;      // "DA_RD_Qm<BuildingId>"
        public string NewRecipeTag;    // "RecipeData.QM.<BuildingId>"
        public int    RecipeCostRows;
        public bool   CostOverridden;  // true = user-supplied list, false = vanilla pass-through
    }
}
