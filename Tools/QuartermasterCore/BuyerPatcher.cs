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
    // parallel directory containing:
    //   * the RecipeLists that had refs added/removed (BuyerLists override)
    //   * the Recipes that were edited (BuyerRecipes with IsCustom=false)
    //   * brand-new "QM_Custom_*" Recipes (BuyerRecipes with IsCustom=true)
    //     placed at Recipes/Custom/QM_Custom_<id>.json
    //
    // Output dir mirrors the in-pak layout
    // (R5/Plugins/R5BusinessRules/Content/{RecipeLists,Recipes}/...) so it
    // can be co-packed with StackPatcher / LootPatcher output by repak.
    //
    // Resolver rule per BuyerRecipes entry:
    //   1. IsCustom=true              -> synthesize a fresh JSON cloned from
    //                                     an internal template, set the 4
    //                                     trade fields + a RecipeTag derived
    //                                     from the custom id
    //   2. IsCustom=false             -> load the vanilla recipe matching
    //                                     basename, patch the 4 fields
    //                                     in-place, write
    //
    // Resolver rule per BuyerLists entry:
    //   * iterate vanilla refs, drop any whose basename is in
    //     RemovedRecipeIds
    //   * append entries for each id in AddedRecipeIds:
    //       "QM_Custom_*"           -> /R5BusinessRules/Recipes/Custom/<id>.<id>
    //       any other vanilla base  -> looked up via the basename->path map
    //                                  built at start (skipped with a
    //                                  warning if not found)
    //
    // After applying the override, the patcher deep-compares against vanilla
    // and skips writing files that turned out identical (idempotent edits
    // do not bloat the pak).
    //
    // Output formatting matches vanilla: tab indent (size 1), CRLF line
    // endings, trailing CRLF. Field order is preserved by patching the
    // existing JsonNode tree in-place (only the leaves change) so the
    // sequence the engine expects is never disturbed.
    public sealed class BuyerPatcher
    {
        // Asset paths referencing recipes/items use these prefixes.
        const string RecipesPathPrefix = "/R5BusinessRules/Recipes/";

        // Synthetic recipes get filed here so they never collide with
        // vanilla refs. Frontend already enforces the "QM_Custom_" prefix
        // on the recipeId so this folder only ever holds our own output.
        const string CustomRecipesFolder = "Custom";
        const string CustomRecipeIdPrefix = "QM_Custom_";

        // The repak-mountable in-pak tree mirrors these subpaths under the
        // output dir. We strip these prefixes when computing keys / refs.
        const string RecipeListsVanillaRoot = "R5/Plugins/R5BusinessRules/Content/RecipeLists";
        const string RecipesVanillaRoot     = "R5/Plugins/R5BusinessRules/Content/Recipes";

        // No-BOM UTF-8; line endings handled manually because we pin
        // CRLF to match vanilla regardless of host platform.
        static readonly UTF8Encoding Utf8NoBom = new UTF8Encoding(false);

        public BuyerPatchResult PatchToDirectory(
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

            var result = new BuyerPatchResult();
            var listOverrides = profile.BuyerLists ?? new Dictionary<string, BuyerListOverride>(0);
            var recipeOverrides = profile.BuyerRecipes ?? new Dictionary<string, BuyerRecipeOverride>(0);

            // Build a basename->(assetPath, jsonPath) lookup over the
            // vanilla recipes once. The patcher needs it for both:
            //   * resolving AddedRecipeIds to a full asset path when
            //     rewriting a RecipeList, AND
            //   * locating the on-disk file for IsCustom=false recipe edits.
            // Skipping the scan when no buyer config is set is the caller's
            // job (see BuildPipeline.HasBuyerConfiguration).
            var recipeMap = BuildVanillaRecipeMap(vanillaRecipesDir);

            // Pick one vanilla PlayerSells recipe as the template for any
            // custom recipes we have to synthesize. Snapshotting the actual
            // game shape (with all the obscure fields the engine expects)
            // is far more robust than hand-rolling a "minimal" JSON that
            // happens to validate today but breaks at the next content patch.
            JsonObject customTemplate = null;
            if (HasAnyCustomRecipe(recipeOverrides))
            {
                customTemplate = LoadCustomRecipeTemplate(recipeMap, result);
                if (customTemplate == null)
                {
                    // The user asked to add a custom recipe but vanilla
                    // shipped no PlayerSells template - leave a warning
                    // and skip the custom output. Other patcher work
                    // (vanilla edits, list mods that reference vanilla
                    // recipes only) is still emitted.
                    result.Warnings.Add(
                        "No vanilla PlayerSells recipe found to clone as a custom template - "
                        + "synthesized recipes will be skipped.");
                }
            }

            // 1. Recipe-level edits + custom recipe synthesis. Each entry
            //    in BuyerRecipes lives in its OWN output file (Recipes/...),
            //    independent of which RecipeLists eventually reference it.
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
                        result.Warnings.Add("Custom recipe id '" + recipeId
                            + "' must start with '" + CustomRecipeIdPrefix
                            + "' - skipping.");
                        continue;
                    }
                    if (string.IsNullOrEmpty(ovr.ItemPath)
                        || string.IsNullOrEmpty(ovr.PayItemPath)
                        || !ovr.ItemCount.HasValue
                        || !ovr.PayCount.HasValue)
                    {
                        result.Warnings.Add("Custom recipe '" + recipeId
                            + "' is missing required fields - skipping.");
                        continue;
                    }
                    WriteCustomRecipe(outDir, recipeId, ovr, customTemplate, result);
                }
                else
                {
                    string assetPath, jsonPath;
                    if (!recipeMap.TryGetValue(recipeId, out var pair))
                    {
                        result.Warnings.Add("Edited recipe '" + recipeId
                            + "' not found in vanilla recipes - skipping.");
                        continue;
                    }
                    assetPath = pair.AssetPath;
                    jsonPath  = pair.JsonPath;
                    PatchVanillaRecipe(vanillaRecipesDir, outDir, recipeId, jsonPath, ovr, result);
                }
            }

            // 2. RecipeList-level edits. Each entry in BuyerLists rewrites
            //    exactly one RecipeList JSON; non-modified vanilla refs are
            //    preserved bit-for-bit.
            foreach (var kv in listOverrides)
            {
                result.ListsScanned++;
                var listId = kv.Key;
                var ovr = kv.Value;
                if (string.IsNullOrEmpty(listId) || ovr == null) continue;
                if (ovr.RecipeOrder == null
                    && (ovr.AddedRecipeIds == null || ovr.AddedRecipeIds.Count == 0)
                    && (ovr.RemovedRecipeIds == null || ovr.RemovedRecipeIds.Count == 0))
                    continue;

                PatchRecipeList(vanillaRecipeListsDir, outDir, listId, ovr, recipeMap, result);
            }

            return result;
        }

        // Walk the vanilla recipes tree once and build a basename ->
        // (assetPath, on-disk-path) map. The basename is what BuyerRecipes
        // / BuyerLists key against, so we need O(1) resolution at patch
        // time. Duplicate basenames across folders would clash - the
        // current vanilla tree doesn't have any so we just warn and keep
        // the first occurrence.
        Dictionary<string, RecipePathPair> BuildVanillaRecipeMap(string vanillaRecipesDir)
        {
            var map = new Dictionary<string, RecipePathPair>(StringComparer.OrdinalIgnoreCase);
            var rootFull = Path.GetFullPath(vanillaRecipesDir);
            foreach (var path in Directory.EnumerateFiles(rootFull, "*.json", SearchOption.AllDirectories))
            {
                var basename = Path.GetFileNameWithoutExtension(path);
                if (string.IsNullOrEmpty(basename)) continue;
                if (map.ContainsKey(basename)) continue;

                // The asset path mirrors the on-disk subpath under Recipes/.
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

        // Snapshot a vanilla PlayerSells recipe to use as the structural
        // template for custom recipes. Loads the first match found in the
        // vanilla recipe map; the four trade fields will be overwritten
        // per-recipe anyway, so the choice of source doesn't matter as
        // long as it has the right $type.
        JsonObject LoadCustomRecipeTemplate(
            Dictionary<string, RecipePathPair> recipeMap,
            BuyerPatchResult result)
        {
            // Stable picking order: any "*_Sell" recipe wins over any
            // other so the template's RecipeTag follows the PlayerSell
            // naming convention.
            var candidate = recipeMap
                .Where(kv => kv.Key.IndexOf("_Sell", StringComparison.OrdinalIgnoreCase) >= 0)
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

        // Writes a fresh Recipes/Custom/<id>.json based on the template.
        // The template's structure is preserved exactly; we only overwrite
        // the trade quartet + RecipeTag (so the tag stays unique per
        // recipe, otherwise the engine would dedupe them at runtime).
        void WriteCustomRecipe(string outDir, string recipeId,
            BuyerRecipeOverride ovr, JsonObject template, BuyerPatchResult result)
        {
            // DeepClone so multiple custom recipes share the same template
            // but never accidentally share mutated leaves.
            var root = (JsonObject)template.DeepClone();

            SetTradeField(root, "RecipeCost",   ovr.ItemPath,    ovr.ItemCount.Value);
            SetTradeField(root, "RecipeResult", ovr.PayItemPath, ovr.PayCount.Value);

            // Reputation gate: empty/null override -> "None" (vanilla
            // PlayerSells recipes ship with "None" anyway, so this matches
            // the default). Non-empty -> the dropdown asset path the user
            // picked (the frontend stores the full
            // /R5BusinessRules/.../DA_Requirement_<faction>_<n>.<...> form).
            root["CraftRequirement"] = string.IsNullOrEmpty(ovr.CraftRequirement)
                ? "None"
                : ovr.CraftRequirement;

            // Give the synthesized recipe a unique tag so the engine treats
            // each one as a distinct trade. Format mirrors the vanilla
            // namespace (RecipeData.Trade.PlayerSell.<group>.<name>) so
            // anything that filters by tag prefix still groups them in.
            if (root["RecipeTag"] is JsonObject tagObj)
            {
                tagObj["TagName"] = "RecipeData.Trade.PlayerSell.QmCustom." + recipeId;
            }
            else
            {
                root["RecipeTag"] = new JsonObject
                {
                    ["TagName"] = "RecipeData.Trade.PlayerSell.QmCustom." + recipeId,
                };
            }

            // Wipe any UIData.Name carried over from the template (it
            // referenced a localization key that doesn't apply to us) and
            // null out the image so the engine falls back to the item's
            // own icon - the user picked an item, so its display already
            // works.
            if (root["UIData"] is JsonObject uiObj)
            {
                uiObj["Name"] = string.Empty;
                uiObj["Image"] = "None";
            }

            var relFile = CustomRecipesFolder + "/" + recipeId + ".json";
            var outPath = Path.Combine(outDir, "R5", "Plugins", "R5BusinessRules", "Content",
                                       "Recipes", CustomRecipesFolder, recipeId + ".json");
            Directory.CreateDirectory(Path.GetDirectoryName(outPath));
            File.WriteAllBytes(outPath, SerializeWithTabsAndCrlf(root));

            result.RecipesAdded++;
            result.WrittenRecipes.Add("Custom/" + recipeId);
        }

        // Loads a vanilla recipe, applies the sparse field overrides, and
        // writes the result if anything actually changed.
        void PatchVanillaRecipe(string vanillaRecipesDir, string outDir,
            string recipeId, string vanillaJsonPath, BuyerRecipeOverride ovr,
            BuyerPatchResult result)
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

            // Snapshot for the change-detection comparison.
            var before = root.DeepClone();

            if (!string.IsNullOrEmpty(ovr.ItemPath) || ovr.ItemCount.HasValue)
                UpdateTradeField(root, "RecipeCost", ovr.ItemPath, ovr.ItemCount);
            if (!string.IsNullOrEmpty(ovr.PayItemPath) || ovr.PayCount.HasValue)
                UpdateTradeField(root, "RecipeResult", ovr.PayItemPath, ovr.PayCount);

            // CraftRequirement: null = leave vanilla alone (don't touch),
            // any non-null value (including the literal "None") overrides.
            // Storing "None" is how the frontend clears a requirement -
            // the on-disk JSON keeps the field, just with the no-gate
            // sentinel the engine recognises.
            if (ovr.CraftRequirement != null)
            {
                root["CraftRequirement"] = ovr.CraftRequirement;
            }

            if (DeepEquals(before, root))
            {
                result.UnchangedSkip++;
                return;
            }

            // Output mirrors the vanilla on-disk subpath so the in-pak
            // layout is identical.
            var rel = vanillaJsonPath.Substring(Path.GetFullPath(vanillaRecipesDir).Length).TrimStart(
                Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var outPath = Path.Combine(outDir, "R5", "Plugins", "R5BusinessRules", "Content",
                                       "Recipes", rel);
            Directory.CreateDirectory(Path.GetDirectoryName(outPath));
            File.WriteAllBytes(outPath, SerializeWithTabsAndCrlf(root));

            result.RecipesEdited++;
            result.WrittenRecipes.Add(recipeId);
        }

        // Rewrites RecipeList[] for one buyer/seller list. Vanilla refs not
        // in RemovedRecipeIds survive in their original order; new refs are
        // appended at the end.
        void PatchRecipeList(string vanillaRecipeListsDir, string outDir,
            string listId, BuyerListOverride ovr,
            Dictionary<string, RecipePathPair> recipeMap,
            BuyerPatchResult result)
        {
            // Resolve the on-disk path for the list from its id (the id
            // already matches the relative path used by /api/buyers).
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

            var newArr = new JsonArray();
            if (ovr.RecipeOrder != null)
            {
                // Definitive-order mode: output exactly what RecipeOrder says.
                // Vanilla IDs not listed are implicitly dropped; custom IDs are
                // placed at their specified position.
                foreach (var id in ovr.RecipeOrder)
                {
                    if (string.IsNullOrEmpty(id)) continue;
                    var assetPath = ResolveAddedRecipeAssetPath(id, recipeMap);
                    if (assetPath == null)
                    {
                        result.Warnings.Add("Ordered recipe id '" + id
                            + "' (in list '" + listId + "') could not be resolved - skipping.");
                        continue;
                    }
                    newArr.Add(assetPath);
                    result.RefsAdded++;
                }
            }
            else
            {
                // Legacy mode: vanilla refs minus removed, then appended ids.
                var removed = ovr.RemovedRecipeIds != null
                    ? new HashSet<string>(ovr.RemovedRecipeIds, StringComparer.OrdinalIgnoreCase)
                    : null;
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
                            result.Warnings.Add("Added recipe id '" + id
                                + "' (in list '" + listId + "') could not be resolved - skipping ref.");
                            continue;
                        }
                        newArr.Add(assetPath);
                        result.RefsAdded++;
                    }
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

        // Either a "QM_Custom_*" id (synthetic - the patcher knows where to
        // file it) or a vanilla basename (looked up via the map). Returns
        // null on unknown vanilla ids so the caller can warn.
        static string ResolveAddedRecipeAssetPath(string id,
            Dictionary<string, RecipePathPair> recipeMap)
        {
            if (IsCustomRecipeId(id))
            {
                // Convention chosen by the patcher itself.
                return RecipesPathPrefix + CustomRecipesFolder + "/" + id + "." + id;
            }
            if (recipeMap.TryGetValue(id, out var pair))
                return pair.AssetPath;
            return null;
        }

        // Replaces the Item + Count on a RecipeCost / RecipeResult array
        // for SYNTHESIZED recipes - both fields are always provided so we
        // can write a clean single-entry array even if the template had
        // multiple entries (which trade recipes never do, but be defensive).
        static void SetTradeField(JsonObject root, string key, string itemPath, int count)
        {
            root[key] = new JsonArray(
                new JsonObject
                {
                    ["Item"] = itemPath,
                    ["Count"] = count,
                });
        }

        // Sparse update for EDITED recipes - keeps every other field on the
        // first entry (rare but possible: future engine versions might add
        // schema fields to the cost/result entry). null itemPath / null
        // count = leave that side of the leaf alone.
        static void UpdateTradeField(JsonObject root, string key, string itemPath, int? count)
        {
            if (!(root[key] is JsonArray arr) || arr.Count == 0)
            {
                // Vanilla didn't have the field array - synthesize a fresh
                // one. Without both fields set, we don't actually know what
                // to write, so bail.
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

        static bool HasAnyCustomRecipe(Dictionary<string, BuyerRecipeOverride> recipes)
        {
            if (recipes == null) return false;
            foreach (var kv in recipes)
                if (kv.Value != null && kv.Value.IsCustom) return true;
            return false;
        }

        // /R5BusinessRules/Recipes/A/B/Name.Name -> "Name"
        // Mirrors how AssetPathToId is computed elsewhere; rightmost name
        // segment regardless of whether the ref uses "/Path/Name" or
        // "/Path/Name.Name" form.
        static string AssetPathToBasename(string assetPath)
        {
            if (string.IsNullOrEmpty(assetPath)) return assetPath;
            var s = assetPath;
            var dot = s.LastIndexOf('.');
            var slash = s.LastIndexOf('/');
            var cut = Math.Max(dot, slash);
            return cut >= 0 && cut < s.Length - 1 ? s.Substring(cut + 1) : s;
        }

        // JSON arrays are order-sensitive; objects must have the same keys
        // in the same order with deep-equal values.
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

        // Tab-indent (size 1), CRLF line endings, trailing CRLF. Matches
        // the format LootPatcher emits so all patched JSONs share one
        // canonical shape inside the pak.
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
            public string AssetPath;   // /R5BusinessRules/Recipes/.../Name.Name
            public string JsonPath;    // absolute on-disk path
        }
    }

    public sealed class BuyerPatchResult
    {
        public int RecipesScanned;
        public int RecipesEdited;     // vanilla recipe patched + written
        public int RecipesAdded;      // synthesized "QM_Custom_*" written
        public int ListsScanned;
        public int ListsWritten;
        public int RefsAdded;         // total recipe refs appended across all lists
        public int RefsRemoved;       // total recipe refs stripped across all lists
        public int UnchangedSkip;     // override resolved to vanilla shape
        public int NoSchema;          // root not a JsonObject / wrong type

        public List<string> WrittenRecipes = new List<string>();
        public List<string> WrittenLists   = new List<string>();
        public List<string> Warnings       = new List<string>();
    }
}
