using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;

namespace Windrose.Quartermaster.Core.BuildingCreator
{
    public sealed class RecipePatcher
    {
        public Action<string> Log;

        public RecipePatchResult Patch(
            string vanillaRecipeJsonPath,
            string outputDir,
            string buildingId,
            IList<(string ItemPath, int Count)> userRecipeCost,
            string displayName = null,
            string description = null)
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

            // BuildingIds already carry the "QmBldg_" prefix, so don't double-prefix.
            var outStem = "DA_RD_" + buildingId;
            var outFileName = outStem + ".json";
            var outAbs = Path.Combine(outputDir, outFileName);
            Directory.CreateDirectory(outputDir);

            // Must be unique across the loaded set (UE checks at GameplayTagsManager init).
            var newRecipeTag = "RecipeData.QM." + buildingId;

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

                        case "UIData":
                            writer.WritePropertyName("UIData");
                            WriteUiDataWithUserText(writer, prop.Value, displayName, description);
                            break;

                        default:
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

        // A plain string deserializes as FText.Base; the vanilla StringTableEntry shape would show the template's shared name instead.
        static void WriteUiDataWithUserText(Utf8JsonWriter writer, JsonElement uiData,
                                            string displayName, string description)
        {
            if (uiData.ValueKind != JsonValueKind.Object)
            {
                uiData.WriteTo(writer);
                return;
            }

            writer.WriteStartObject();
            foreach (var p in uiData.EnumerateObject())
            {
                if (p.NameEquals("Name") && !string.IsNullOrEmpty(displayName))
                {
                    writer.WriteString("Name", displayName);
                }
                else if ((p.NameEquals("Description") || p.NameEquals("RecipeDescription"))
                         && !string.IsNullOrEmpty(description))
                {
                    writer.WriteString(p.Name, description);
                }
                else
                {
                    p.WriteTo(writer);
                }
            }
            writer.WriteEndObject();
        }

        void LogLine(string msg) { if (Log != null) Log(msg); }
    }

    public sealed class RecipePatchResult
    {
        public string OutputJsonPath;
        public string OutputStem;
        public string NewRecipeTag;
        public int    RecipeCostRows;
        public bool   CostOverridden;
    }
}
