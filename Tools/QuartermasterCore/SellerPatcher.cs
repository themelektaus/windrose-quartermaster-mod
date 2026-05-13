using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Windrose.Quartermaster.Core
{
    // Reads vanilla R5BLRecipeList + R5BLRecipeData JSONs and writes a
    // parallel directory containing patched PlayerBuys lists + the
    // recipes they reference. Structurally a mirror of BuyerPatcher; the
    // important differences:
    //
    //   * filters / consumes Profile.SellerRecipes + Profile.SellerLists
    //     (vs Buyer*)
    //   * Cost/Result mapping is SWAPPED on disk - on the seller side
    //     RecipeResult is the item the NPC delivers (= override.ItemPath)
    //     and RecipeCost is the currency (= override.PayItemPath). The
    //     SellersEndpoint does the same swap on the read side, so the
    //     profile JSON shape stays uniform between buyer and seller tabs.
    //   * synthesized custom recipes use the "QM_SCustom_*" prefix so the
    //     seller and buyer custom namespaces stay disjoint
    //   * the custom-recipe template is picked from a vanilla *_Buy
    //     recipe so the RecipeTag stays in the PlayerBuy namespace
    //
    // Output dir is the SAME tree BuyerPatcher writes into - both patchers
    // produce disjoint subpaths under Recipes/ (vanilla edits land in
    // their original folder, customs land in Custom/<id>.json) and
    // disjoint files under RecipeLists/ (PlayerSells vs PlayerBuys), so
    // they compose cleanly into one pak.
    //
    // After applying the override, the patcher deep-compares against
    // vanilla and skips writing files that turned out identical (idempotent
    // edits do not bloat the pak).
    //
    // Output formatting matches vanilla: tab indent (size 1), CRLF line
    // endings, trailing CRLF.
    public sealed class SellerPatcher
    {
        const string RecipesPathPrefix = "/R5BusinessRules/Recipes/";
        const string CustomRecipesFolder = "Custom";
        const string CustomRecipeIdPrefix = "QM_SCustom_";

        const string RecipeListsVanillaRoot = "R5/Plugins/R5BusinessRules/Content/RecipeLists";
        const string RecipesVanillaRoot     = "R5/Plugins/R5BusinessRules/Content/Recipes";

        static readonly UTF8Encoding Utf8NoBom = new UTF8Encoding(false);

        public SellerPatchResult PatchToDirectory(
            string vanillaRecipeListsDir,
            string vanillaRecipesDir,
            string outDir,
            Profile profile)
        {
            if (string.IsNullOrEmpty(vanillaRecipeListsDir)) throw new ArgumentNullException("vanillaRecipeListsDir");
            if (string.IsNullOrEmpty(vanillaRecipesDir))     throw new ArgumentNullException("vanillaRecipesDir");
            if (string.IsNullOrEmpty(outDir))                throw new ArgumentNullException("outDir");
            if (profile == null)                              throw new ArgumentNullException("profile");
            if (!Directory.Exists(vanillaRecipeListsDir))     throw new DirectoryNotFoundException(vanillaRecipeListsDir);
            if (!Directory.Exists(vanillaRecipesDir))         throw new DirectoryNotFoundException(vanillaRecipesDir);

            Directory.CreateDirectory(outDir);

            var result = new SellerPatchResult();
            var listOverrides = profile.SellerLists ?? new Dictionary<string, SellerListOverride>(0);
            var recipeOverrides = profile.SellerRecipes ?? new Dictionary<string, SellerRecipeOverride>(0);

            var recipeMap = BuildVanillaRecipeMap(vanillaRecipesDir);

            // Custom-recipe template: a vanilla *_Buy recipe so the
            // synthesized RecipeTag follows the PlayerBuy naming
            // convention and any field the engine looks at lives at the
            // expected key.
            JsonObject customTemplate = null;
            if (HasAnyCustomRecipe(recipeOverrides))
            {
                customTemplate = LoadCustomRecipeTemplate(recipeMap, result);
                if (customTemplate == null)
                {
                    result.Warnings.Add(
                        "No vanilla PlayerBuys recipe found to clone as a custom template - "
                        + "synthesized seller recipes will be skipped.");
                }
            }

            // 1. Recipe-level edits + synthesis.
            foreach (var kv in recipeOverrides)
            {
                result.RecipesScanned++;
                var recipeId = kv.Key;
                var ovr = kv.Value;
                if (string.IsNullOrEmpty(recipeId) || ovr == null) continue;

                if (ovr.IsCustom)
                {
                    if (customTemplate == null) continue;
                    if (!IsCustomRecipeId(recipeId))
                    {
                        result.Warnings.Add("Custom seller recipe id '" + recipeId
                            + "' must start with '" + CustomRecipeIdPrefix
                            + "' - skipping.");
                        continue;
                    }
                    if (string.IsNullOrEmpty(ovr.ItemPath)
                        || string.IsNullOrEmpty(ovr.PayItemPath)
                        || !ovr.ItemCount.HasValue
                        || !ovr.PayCount.HasValue)
                    {
                        result.Warnings.Add("Custom seller recipe '" + recipeId
                            + "' is missing required fields - skipping.");
                        continue;
                    }
                    WriteCustomRecipe(outDir, recipeId, ovr, customTemplate, result);
                }
                else
                {
                    if (!recipeMap.TryGetValue(recipeId, out var pair))
                    {
                        result.Warnings.Add("Edited seller recipe '" + recipeId
                            + "' not found in vanilla recipes - skipping.");
                        continue;
                    }
                    PatchVanillaRecipe(vanillaRecipesDir, outDir, recipeId, pair.JsonPath, ovr, result);
                }
            }

            // 2. RecipeList-level edits.
            foreach (var kv in listOverrides)
            {
                result.ListsScanned++;
                var listId = kv.Key;
                var ovr = kv.Value;
                if (string.IsNullOrEmpty(listId) || ovr == null) continue;
                if ((ovr.AddedRecipeIds == null || ovr.AddedRecipeIds.Count == 0)
                    && (ovr.RemovedRecipeIds == null || ovr.RemovedRecipeIds.Count == 0))
                    continue;

                PatchRecipeList(vanillaRecipeListsDir, outDir, listId, ovr, recipeMap, result);
            }

            return result;
        }

        Dictionary<string, RecipePathPair> BuildVanillaRecipeMap(string vanillaRecipesDir)
        {
            var map = new Dictionary<string, RecipePathPair>(StringComparer.OrdinalIgnoreCase);
            var rootFull = Path.GetFullPath(vanillaRecipesDir);
            foreach (var path in Directory.EnumerateFiles(rootFull, "*.json", SearchOption.AllDirectories))
            {
                var basename = Path.GetFileNameWithoutExtension(path);
                if (string.IsNullOrEmpty(basename)) continue;
                if (map.ContainsKey(basename)) continue;

                var rel = path.Substring(rootFull.Length).TrimStart(
                    Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                var relForward = rel.Replace(Path.DirectorySeparatorChar, '/')
                                    .Replace(Path.AltDirectorySeparatorChar, '/');
                if (relForward.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                    relForward = relForward.Substring(0, relForward.Length - 5);
                var assetPath = RecipesPathPrefix + relForward + "." + basename;
                map[basename] = new RecipePathPair { AssetPath = assetPath, JsonPath = path };
            }
            return map;
        }

        // Prefer *_Buy recipes so the template's RecipeTag follows the
        // PlayerBuy convention. Falls back to any recipe with the right
        // $type if no _Buy file is present (vanilla always ships some;
        // fallback is defensive).
        JsonObject LoadCustomRecipeTemplate(
            Dictionary<string, RecipePathPair> recipeMap,
            SellerPatchResult result)
        {
            var candidate = recipeMap
                .Where(kv => kv.Key.IndexOf("_Buy", StringComparison.OrdinalIgnoreCase) >= 0)
                .Select(kv => kv.Value.JsonPath)
                .FirstOrDefault()
                ?? recipeMap.Values.Select(p => p.JsonPath).FirstOrDefault();
            if (candidate == null) return null;

            try
            {
                var node = JsonNode.Parse(File.ReadAllText(candidate, Encoding.UTF8));
                return node as JsonObject;
            }
            catch (Exception ex)
            {
                result.Warnings.Add("Failed to load custom-recipe template '"
                    + candidate + "': " + ex.Message);
                return null;
            }
        }

        // Writes Recipes/Custom/<id>.json based on the template. The four
        // trade fields are written WITH THE SWAP - the override stores
        // ItemPath = what the NPC sells (= RecipeResult on disk),
        // PayItemPath = what the player pays (= RecipeCost on disk).
        void WriteCustomRecipe(string outDir, string recipeId,
            SellerRecipeOverride ovr, JsonObject template, SellerPatchResult result)
        {
            var root = (JsonObject)template.DeepClone();

            // Seller-side swap: ItemPath (the NPC's delivery) -> RecipeResult,
            // PayItemPath (the player's payment) -> RecipeCost. Exactly the
            // mirror of BuyerPatcher.
            SetTradeField(root, "RecipeResult", ovr.ItemPath,    ovr.ItemCount.Value);
            SetTradeField(root, "RecipeCost",   ovr.PayItemPath, ovr.PayCount.Value);

            root["CraftRequirement"] = string.IsNullOrEmpty(ovr.CraftRequirement)
                ? "None"
                : ovr.CraftRequirement;

            // Unique RecipeTag in the PlayerBuy namespace so each clone
            // counts as a distinct trade.
            if (root["RecipeTag"] is JsonObject tagObj)
            {
                tagObj["TagName"] = "RecipeData.Trade.PlayerBuy.QmSCustom." + recipeId;
            }
            else
            {
                root["RecipeTag"] = new JsonObject
                {
                    ["TagName"] = "RecipeData.Trade.PlayerBuy.QmSCustom." + recipeId,
                };
            }

            // Wipe the carried-over UIData label / image so the engine
            // falls back to the item's own icon.
            if (root["UIData"] is JsonObject uiObj)
            {
                uiObj["Name"] = string.Empty;
                uiObj["Image"] = "None";
            }

            var outPath = Path.Combine(outDir, "R5", "Plugins", "R5BusinessRules", "Content",
                                       "Recipes", CustomRecipesFolder, recipeId + ".json");
            Directory.CreateDirectory(Path.GetDirectoryName(outPath));
            File.WriteAllBytes(outPath, SerializeWithTabsAndCrlf(root));

            result.RecipesAdded++;
            result.WrittenRecipes.Add("Custom/" + recipeId);
        }

        void PatchVanillaRecipe(string vanillaRecipesDir, string outDir,
            string recipeId, string vanillaJsonPath, SellerRecipeOverride ovr,
            SellerPatchResult result)
        {
            JsonObject root;
            try
            {
                var node = JsonNode.Parse(File.ReadAllText(vanillaJsonPath, Encoding.UTF8));
                root = node as JsonObject;
                if (root == null) { result.NoSchema++; return; }
            }
            catch (JsonException)
            {
                result.NoSchema++;
                return;
            }

            var before = root.DeepClone();

            // SWAP vs BuyerPatcher: ItemPath -> RecipeResult, PayItemPath -> RecipeCost.
            if (!string.IsNullOrEmpty(ovr.ItemPath) || ovr.ItemCount.HasValue)
                UpdateTradeField(root, "RecipeResult", ovr.ItemPath, ovr.ItemCount);
            if (!string.IsNullOrEmpty(ovr.PayItemPath) || ovr.PayCount.HasValue)
                UpdateTradeField(root, "RecipeCost", ovr.PayItemPath, ovr.PayCount);

            if (ovr.CraftRequirement != null)
            {
                root["CraftRequirement"] = ovr.CraftRequirement;
            }

            if (DeepEquals(before, root))
            {
                result.UnchangedSkip++;
                return;
            }

            var rel = vanillaJsonPath.Substring(Path.GetFullPath(vanillaRecipesDir).Length).TrimStart(
                Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var outPath = Path.Combine(outDir, "R5", "Plugins", "R5BusinessRules", "Content",
                                       "Recipes", rel);
            Directory.CreateDirectory(Path.GetDirectoryName(outPath));
            File.WriteAllBytes(outPath, SerializeWithTabsAndCrlf(root));

            result.RecipesEdited++;
            result.WrittenRecipes.Add(recipeId);
        }

        void PatchRecipeList(string vanillaRecipeListsDir, string outDir,
            string listId, SellerListOverride ovr,
            Dictionary<string, RecipePathPair> recipeMap,
            SellerPatchResult result)
        {
            var vanillaListPath = Path.Combine(vanillaRecipeListsDir,
                listId.Replace('/', Path.DirectorySeparatorChar) + ".json");
            if (!File.Exists(vanillaListPath))
            {
                result.Warnings.Add("RecipeList not found: " + listId
                    + " (looked at " + vanillaListPath + ")");
                return;
            }

            JsonObject root;
            try
            {
                var node = JsonNode.Parse(File.ReadAllText(vanillaListPath, Encoding.UTF8));
                root = node as JsonObject;
                if (root == null) { result.NoSchema++; return; }
            }
            catch (JsonException)
            {
                result.NoSchema++;
                return;
            }

            if (!(root["RecipeList"] is JsonArray vanillaRefs))
            {
                result.NoSchema++;
                return;
            }

            var removed = ovr.RemovedRecipeIds != null
                ? new HashSet<string>(ovr.RemovedRecipeIds, StringComparer.OrdinalIgnoreCase)
                : null;
            var newArr = new JsonArray();
            foreach (var refNode in vanillaRefs)
            {
                if (!(refNode is JsonValue refVal)) continue;
                var refStr = refVal.GetValue<string>();
                if (string.IsNullOrEmpty(refStr)) continue;
                var basename = AssetPathToBasename(refStr);
                if (removed != null && removed.Contains(basename))
                {
                    result.RefsRemoved++;
                    continue;
                }
                newArr.Add(refStr);
            }

            if (ovr.AddedRecipeIds != null)
            {
                foreach (var id in ovr.AddedRecipeIds)
                {
                    if (string.IsNullOrEmpty(id)) continue;
                    var assetPath = ResolveAddedRecipeAssetPath(id, recipeMap);
                    if (assetPath == null)
                    {
                        result.Warnings.Add("Added seller recipe id '" + id
                            + "' (in list '" + listId + "') could not be resolved - skipping ref.");
                        continue;
                    }
                    newArr.Add(assetPath);
                    result.RefsAdded++;
                }
            }

            if (DeepEquals(vanillaRefs, newArr))
            {
                result.UnchangedSkip++;
                return;
            }

            root["RecipeList"] = newArr;

            var outPath = Path.Combine(outDir, "R5", "Plugins", "R5BusinessRules", "Content",
                                       "RecipeLists", listId.Replace('/', Path.DirectorySeparatorChar) + ".json");
            Directory.CreateDirectory(Path.GetDirectoryName(outPath));
            File.WriteAllBytes(outPath, SerializeWithTabsAndCrlf(root));

            result.ListsWritten++;
            result.WrittenLists.Add(listId);
        }

        static string ResolveAddedRecipeAssetPath(string id,
            Dictionary<string, RecipePathPair> recipeMap)
        {
            if (IsCustomRecipeId(id))
            {
                return RecipesPathPrefix + CustomRecipesFolder + "/" + id + "." + id;
            }
            if (recipeMap.TryGetValue(id, out var pair))
                return pair.AssetPath;
            return null;
        }

        static void SetTradeField(JsonObject root, string key, string itemPath, int count)
        {
            root[key] = new JsonArray(
                new JsonObject
                {
                    ["Item"] = itemPath,
                    ["Count"] = count,
                });
        }

        static void UpdateTradeField(JsonObject root, string key, string itemPath, int? count)
        {
            if (!(root[key] is JsonArray arr) || arr.Count == 0)
            {
                if (string.IsNullOrEmpty(itemPath) || !count.HasValue) return;
                root[key] = new JsonArray(
                    new JsonObject
                    {
                        ["Item"] = itemPath,
                        ["Count"] = count.Value,
                    });
                return;
            }
            if (!(arr[0] is JsonObject obj)) return;
            if (!string.IsNullOrEmpty(itemPath)) obj["Item"] = itemPath;
            if (count.HasValue) obj["Count"] = count.Value;
        }

        static bool IsCustomRecipeId(string id)
        {
            return !string.IsNullOrEmpty(id)
                && id.StartsWith(CustomRecipeIdPrefix, StringComparison.OrdinalIgnoreCase);
        }

        static bool HasAnyCustomRecipe(Dictionary<string, SellerRecipeOverride> recipes)
        {
            if (recipes == null) return false;
            foreach (var kv in recipes)
                if (kv.Value != null && kv.Value.IsCustom) return true;
            return false;
        }

        static string AssetPathToBasename(string assetPath)
        {
            if (string.IsNullOrEmpty(assetPath)) return assetPath;
            var s = assetPath;
            var dot = s.LastIndexOf('.');
            var slash = s.LastIndexOf('/');
            var cut = Math.Max(dot, slash);
            return cut >= 0 && cut < s.Length - 1 ? s.Substring(cut + 1) : s;
        }

        static bool DeepEquals(JsonNode a, JsonNode b)
        {
            if (a == null && b == null) return true;
            if (a == null || b == null) return false;

            if (a is JsonObject oa && b is JsonObject ob)
            {
                if (oa.Count != ob.Count) return false;
                using var ea = oa.GetEnumerator();
                using var eb = ob.GetEnumerator();
                while (ea.MoveNext() && eb.MoveNext())
                {
                    if (ea.Current.Key != eb.Current.Key) return false;
                    if (!DeepEquals(ea.Current.Value, eb.Current.Value)) return false;
                }
                return true;
            }
            if (a is JsonArray aa && b is JsonArray ab)
            {
                if (aa.Count != ab.Count) return false;
                for (int i = 0; i < aa.Count; i++)
                {
                    if (!DeepEquals(aa[i], ab[i])) return false;
                }
                return true;
            }
            if (a is JsonValue va && b is JsonValue vb)
            {
                return va.ToJsonString() == vb.ToJsonString();
            }
            return false;
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

        sealed class RecipePathPair
        {
            public string AssetPath;
            public string JsonPath;
        }
    }

    public sealed class SellerPatchResult
    {
        public int RecipesScanned;
        public int RecipesEdited;
        public int RecipesAdded;
        public int ListsScanned;
        public int ListsWritten;
        public int RefsAdded;
        public int RefsRemoved;
        public int UnchangedSkip;
        public int NoSchema;

        public List<string> WrittenRecipes = new List<string>();
        public List<string> WrittenLists   = new List<string>();
        public List<string> Warnings       = new List<string>();
    }
}
