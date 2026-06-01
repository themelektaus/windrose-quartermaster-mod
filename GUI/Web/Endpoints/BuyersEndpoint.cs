using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Windrose.Quartermaster.Core;

namespace Windrose.Quartermaster.Web.Endpoints;

public static class BuyersEndpoint
{
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

        var recipesRoot = Directory.Exists(paths.VanillaRecipes)
            ? Path.GetFullPath(paths.VanillaRecipes)
            : null;

        var rootFull = Path.GetFullPath(paths.VanillaRecipeLists);
        foreach (var path in Directory.EnumerateFiles(rootFull, "*.json", SearchOption.AllDirectories))
        {
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
                return dto;

            foreach (var refEl in listEl.EnumerateArray())
            {
                if (refEl.ValueKind != JsonValueKind.String) continue;
                var refStr = refEl.GetString();

                if (IsReputationRecipe(refStr)) continue;

                var entry = ResolveRecipe(recipesRoot, refStr);
                if (entry != null) dto.entries.Add(entry);
            }

            if (dto.entries.Count == 0) return null;

            return dto;
        }
        catch
        {
            return null;
        }
    }

    static void DeriveFactionLabel(string id, BuyerDto dto)
    {
        var lastSlash = id.LastIndexOf('/');
        var folder = lastSlash > 0 ? id.Substring(0, lastSlash) : string.Empty;
        var file = lastSlash > 0 ? id.Substring(lastSlash + 1) : id;

        var faction = folder.StartsWith("Trade", StringComparison.OrdinalIgnoreCase)
            ? folder.Substring(5)
            : "(other)";
        if (faction == "Civilians") faction = "Civilians";
        dto.faction = faction;

        const string marker = "_PlayerSells";
        var markerIdx = file.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        var slot = string.Empty;
        if (markerIdx >= 0)
        {
            var beforeMarker = file.Substring(0, markerIdx);
            var lastUnderscore = beforeMarker.LastIndexOf('_');
            var slotBase = lastUnderscore >= 0 ? beforeMarker.Substring(lastUnderscore + 1) : beforeMarker;
            var digitStart = slotBase.Length;
            while (digitStart > 0 && char.IsDigit(slotBase[digitStart - 1])) digitStart--;
            var slotNum = digitStart < slotBase.Length ? slotBase.Substring(digitStart) : slotBase;

            var afterMarker = file.Substring(markerIdx + marker.Length);
            slot = string.IsNullOrEmpty(afterMarker)
                ? slotNum
                : slotNum + afterMarker;
        }
        dto.slot = slot;

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

    static bool IsReputationRecipe(string recipeRef)
    {
        if (string.IsNullOrEmpty(recipeRef)) return false;
        return recipeRef.IndexOf("Reputation_BlackbeardSign", StringComparison.OrdinalIgnoreCase) >= 0
            || recipeRef.IndexOf("Reputation_FactionSign",    StringComparison.OrdinalIgnoreCase) >= 0;
    }

    // Cost/Result mapping is the inverse of SellersEndpoint.ResolveRecipe:
    // here itemId is RecipeCost (the item the player sells).
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
            return unresolved;

        var afterPlugin = recipeRef.Substring(PluginPathPrefix.Length);
        const string recipesSegment = "Recipes/";
        if (!afterPlugin.StartsWith(recipesSegment, StringComparison.Ordinal))
            return unresolved;
        var subRef = afterPlugin.Substring(recipesSegment.Length);

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
            return;
        }
    }

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
