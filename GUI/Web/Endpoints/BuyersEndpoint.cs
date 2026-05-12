using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Windrose.Quartermaster.Core;

namespace Windrose.Quartermaster.Web.Endpoints;

// GET /api/buyers -> all R5BLRecipeList JSONs in Sources/Vanilla/.../RecipeLists/
// whose filename indicates PlayerSells (the NPC buys from the player).
// Each list is enriched with the resolved cost/result of every referenced
// R5BLRecipeData entry (looked up under Sources/Vanilla/.../Recipes/), so the
// frontend can render a flat table without a second round-trip per entry.
//
// Read-only: phase 1 of the buyers feature only displays the trade rosters.
// CRUD (add/remove items, edit prices) follows in a later iteration.
public static class BuyersEndpoint
{
    // Path prefix the recipe references use; everything we resolve sits under
    // the R5BusinessRules plugin tree.
    const string PluginPathPrefix = "/R5BusinessRules/";

    public static void Map(WebApplication app, string repoRoot)
    {
        var paths = WindrosePaths.FromModRoot(repoRoot);

        app.MapGet("/api/buyers", async () =>
        {
            var buyers = await LoadBuyers(paths);
            return Results.Json(buyers);
        });
    }

    static async Task<List<BuyerDto>> LoadBuyers(WindrosePaths paths)
    {
        var result = new List<BuyerDto>();
        if (!Directory.Exists(paths.VanillaRecipeLists)) return result;

        // The recipes-root is where we resolve every recipe ref to a file.
        // If it's missing we still surface the lists, just with unresolved
        // entries so the frontend can show "Sources/Vanilla/.../Recipes/
        // is missing - re-run setup".
        var recipesRoot = Directory.Exists(paths.VanillaRecipes)
            ? Path.GetFullPath(paths.VanillaRecipes)
            : null;

        var rootFull = Path.GetFullPath(paths.VanillaRecipeLists);
        foreach (var path in Directory.EnumerateFiles(rootFull, "*.json", SearchOption.AllDirectories))
        {
            // Filename is the cheapest filter: only PlayerSells lists make
            // it into the Buyers tab. Crafting lists (Furnace_T01, etc.) and
            // PlayerBuys lists (= Versorger, a different future tab) are
            // skipped here.
            var fileName = Path.GetFileNameWithoutExtension(path);
            if (fileName.IndexOf("PlayerSells", StringComparison.OrdinalIgnoreCase) < 0)
                continue;

            var dto = await TryParseBuyer(rootFull, recipesRoot, path);
            if (dto != null) result.Add(dto);
        }

        result.Sort((a, b) => string.CompareOrdinal(a.id, b.id));
        return result;
    }

    static async Task<BuyerDto> TryParseBuyer(string rootFull, string recipesRoot, string jsonPath)
    {
        try
        {
            using var stream = File.OpenRead(jsonPath);
            using var doc = await JsonDocument.ParseAsync(stream);

            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;
            if (!root.TryGetProperty("$type", out var typeEl)
                || typeEl.ValueKind != JsonValueKind.String
                || typeEl.GetString() != "R5BLRecipeList")
                return null;

            // Build a stable id: path under RecipeLists/, '/'-separated, no ext.
            var rel = jsonPath.Substring(rootFull.Length).TrimStart(
                Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var id = rel
                .Replace(Path.DirectorySeparatorChar, '/')
                .Replace(Path.AltDirectorySeparatorChar, '/');
            if (id.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                id = id.Substring(0, id.Length - 5);

            var dto = new BuyerDto { id = id, entries = new List<BuyerEntryDto>() };
            DeriveFactionLabel(id, dto);

            if (!root.TryGetProperty("RecipeList", out var listEl)
                || listEl.ValueKind != JsonValueKind.Array)
                return dto; // empty list is still a valid buyer DTO

            foreach (var refEl in listEl.EnumerateArray())
            {
                if (refEl.ValueKind != JsonValueKind.String) continue;
                var refStr = refEl.GetString();

                // Skip reputation-token trade lines (BlackbeardSign,
                // FactionSign). These power a deeper rep-grinding mechanic;
                // the user explicitly does not want them in the editable
                // buyer roster.
                if (IsReputationRecipe(refStr)) continue;

                var entry = ResolveRecipe(recipesRoot, refStr);
                if (entry != null) dto.entries.Add(entry);
            }

            // If filtering left no entries, drop the list entirely - vanilla's
            // *_PlayerSells_02 lists are 100% reputation tokens, so they would
            // otherwise render as empty cards.
            if (dto.entries.Count == 0) return null;

            return dto;
        }
        catch
        {
            return null;
        }
    }

    // Filename anatomy (vanilla):
    //   DA_RecipeList_Trade_<Faction>1_PlayerSells       -> slot "1"
    //   DA_RecipeList_Trade_<Faction>1_PlayerSells_02    -> slot "1_02"
    // The id we already built carries the faction folder ("TradeBrethren") +
    // filename; derive faction + slot for human-friendly display.
    static void DeriveFactionLabel(string id, BuyerDto dto)
    {
        var lastSlash = id.LastIndexOf('/');
        var folder = lastSlash > 0 ? id.Substring(0, lastSlash) : string.Empty;
        var file = lastSlash > 0 ? id.Substring(lastSlash + 1) : id;

        // Folder is "TradeBrethren" / "TradeBucaneers" / etc.
        var faction = folder.StartsWith("Trade", StringComparison.OrdinalIgnoreCase)
            ? folder.Substring(5)
            : "(other)";
        if (faction == "Civilians") faction = "Civilians"; // spelled identically vanilla-side
        dto.faction = faction;

        // Slot suffix: everything after "_PlayerSells".
        const string marker = "_PlayerSells";
        var markerIdx = file.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        var slot = string.Empty;
        if (markerIdx >= 0)
        {
            // Strip leading "DA_RecipeList_Trade_<faction>" prefix to read off
            // the slot number ("1", "2", ...). We then re-glue any suffix
            // (e.g. "_02") that lives after "_PlayerSells".
            var beforeMarker = file.Substring(0, markerIdx);
            var lastUnderscore = beforeMarker.LastIndexOf('_');
            var slotBase = lastUnderscore >= 0 ? beforeMarker.Substring(lastUnderscore + 1) : beforeMarker;
            // Strip leading faction letters - the slot number is the trailing
            // digit-tail of slotBase (e.g. "Brethren1" -> "1").
            var digitStart = slotBase.Length;
            while (digitStart > 0 && char.IsDigit(slotBase[digitStart - 1])) digitStart--;
            var slotNum = digitStart < slotBase.Length ? slotBase.Substring(digitStart) : slotBase;

            var afterMarker = file.Substring(markerIdx + marker.Length);
            slot = string.IsNullOrEmpty(afterMarker)
                ? slotNum
                : slotNum + afterMarker; // e.g. "1_02"
        }
        dto.slot = slot;

        // Label heuristics: vanilla naming convention puts "_02" on the second
        // inventory list of the same NPC slot. Render that as "Inventory 2"
        // so a non-modder reading the UI gets the gist.
        if (slot.Contains('_'))
        {
            var bits = slot.Split('_');
            dto.label = faction + " Trader " + bits[0] + " (Inventory " + bits[1].TrimStart('0') + ")";
        }
        else if (!string.IsNullOrEmpty(slot))
        {
            dto.label = faction + " Trader " + slot;
        }
        else
        {
            dto.label = faction + " Trader";
        }
    }

    // Detects reputation-token recipes (BlackbeardSign / FactionSign). These
    // are part of the rep-grinding mechanic - turning the tokens in raises
    // your standing with a faction - and the user does not want them editable
    // alongside regular trade goods. Matching on the recipe path is enough;
    // every vanilla rep recipe lives under TradeGoods/ and embeds
    // "Reputation_BlackbeardSign_" or "Reputation_FactionSign_" in its name.
    static bool IsReputationRecipe(string recipeRef)
    {
        if (string.IsNullOrEmpty(recipeRef)) return false;
        return recipeRef.IndexOf("Reputation_BlackbeardSign", StringComparison.OrdinalIgnoreCase) >= 0
            || recipeRef.IndexOf("Reputation_FactionSign",    StringComparison.OrdinalIgnoreCase) >= 0;
    }

    // Resolves "/R5BusinessRules/Recipes/Economy/.../X.X" to the on-disk
    // JSON under Sources/Vanilla/R5/Plugins/R5BusinessRules/Content/Recipes/...
    // Returns a BuyerEntryDto with resolved=false (and only the raw ref
    // fields populated) if the recipe file isn't there, so the frontend can
    // still surface a row instead of silently dropping the entry.
    static BuyerEntryDto ResolveRecipe(string recipesRoot, string recipeRef)
    {
        if (string.IsNullOrEmpty(recipeRef)) return null;

        var unresolved = new BuyerEntryDto
        {
            recipePath = recipeRef,
            recipeId = AssetPathToId(recipeRef),
            resolved = false,
        };

        if (recipesRoot == null) return unresolved;
        if (!recipeRef.StartsWith(PluginPathPrefix, StringComparison.Ordinal))
            return unresolved; // out-of-tree ref - shouldn't happen for trade recipes

        // The recipes-root path we have is .../Content/Recipes/, but the asset
        // ref looks like /R5BusinessRules/Recipes/Economy/.../X.X. Trim the
        // plugin prefix + "Recipes/" segment to land at the sub-path.
        var afterPlugin = recipeRef.Substring(PluginPathPrefix.Length);
        const string recipesSegment = "Recipes/";
        if (!afterPlugin.StartsWith(recipesSegment, StringComparison.Ordinal))
            return unresolved;
        var subRef = afterPlugin.Substring(recipesSegment.Length);

        // Trim the canonical ".AssetName" suffix to get the sub-path of
        // the on-disk JSON, then convert '/' to OS separators.
        var dot = subRef.LastIndexOf('.');
        var slash = subRef.LastIndexOf('/');
        if (dot > slash) subRef = subRef.Substring(0, dot);

        var recipeJsonPath = Path.Combine(recipesRoot, subRef.Replace('/', Path.DirectorySeparatorChar) + ".json");
        if (!File.Exists(recipeJsonPath)) return unresolved;

        try
        {
            using var stream = File.OpenRead(recipeJsonPath);
            using var doc = JsonDocument.Parse(stream);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return unresolved;
            if (!root.TryGetProperty("$type", out var typeEl)
                || typeEl.ValueKind != JsonValueKind.String
                || typeEl.GetString() != "R5BLRecipeData")
                return unresolved;

            var entry = new BuyerEntryDto
            {
                recipePath = recipeRef,
                recipeId = Path.GetFileNameWithoutExtension(recipeJsonPath),
                resolved = true,
            };

            if (root.TryGetProperty("RecipeCost", out var costEl)
                && costEl.ValueKind == JsonValueKind.Array)
            {
                FillItemRef(costEl, out entry.itemId, out entry.itemPath, out entry.itemCount);
            }
            if (root.TryGetProperty("RecipeResult", out var resultEl)
                && resultEl.ValueKind == JsonValueKind.Array)
            {
                FillItemRef(resultEl, out entry.payItemId, out entry.payItemPath, out entry.payCount);
            }
            if (root.TryGetProperty("RecipeTag", out var tagEl)
                && tagEl.ValueKind == JsonValueKind.Object
                && tagEl.TryGetProperty("TagName", out var tagNameEl)
                && tagNameEl.ValueKind == JsonValueKind.String)
            {
                entry.recipeTag = tagNameEl.GetString();
            }
            if (root.TryGetProperty("CraftRequirement", out var reqEl)
                && reqEl.ValueKind == JsonValueKind.String)
            {
                var s = reqEl.GetString();
                if (!string.IsNullOrEmpty(s) && s != "None")
                    entry.craftRequirement = s;
            }

            return entry;
        }
        catch
        {
            return unresolved;
        }
    }

    // Pulls (Item path + Count) from the first entry of a RecipeCost /
    // RecipeResult array. Trade recipes ship exactly one entry per side, but
    // we defensively bail on out-of-shape input rather than throwing.
    static void FillItemRef(JsonElement arr, out string itemId, out string itemPath, out int count)
    {
        itemId = null;
        itemPath = null;
        count = 0;
        foreach (var el in arr.EnumerateArray())
        {
            if (el.ValueKind != JsonValueKind.Object) continue;
            if (el.TryGetProperty("Item", out var itemEl) && itemEl.ValueKind == JsonValueKind.String)
            {
                itemPath = itemEl.GetString();
                itemId = AssetPathToId(itemPath);
            }
            if (el.TryGetProperty("Count", out var cntEl) && cntEl.ValueKind == JsonValueKind.Number)
            {
                cntEl.TryGetInt32(out count);
            }
            return; // first entry only
        }
    }

    // UE asset path -> short id. Matches LootTablesEndpoint.AssetPathToId
    // semantics: takes the rightmost name segment regardless of whether the
    // ref uses the "/Path/Name" or "/Path/Name.Name" canonical form.
    static string AssetPathToId(string assetPath)
    {
        if (string.IsNullOrEmpty(assetPath)) return null;
        var s = assetPath;
        var dot = s.LastIndexOf('.');
        var slash = s.LastIndexOf('/');
        var cut = Math.Max(dot, slash);
        return cut >= 0 && cut < s.Length - 1 ? s.Substring(cut + 1) : s;
    }
}
