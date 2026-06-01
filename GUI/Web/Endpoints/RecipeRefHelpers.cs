using System;
using System.Text.Json;

namespace Windrose.Quartermaster.Web.Endpoints;

// Recipe-ref parsing shared by the buyer, seller and loot endpoints.
public static class RecipeRefHelpers
{
    public static bool IsReputationRecipe(string recipeRef)
    {
        if (string.IsNullOrEmpty(recipeRef)) return false;
        return recipeRef.IndexOf("Reputation_BlackbeardSign", StringComparison.OrdinalIgnoreCase) >= 0
            || recipeRef.IndexOf("Reputation_FactionSign",    StringComparison.OrdinalIgnoreCase) >= 0;
    }

    public static void FillItemRef(JsonElement arr, out string itemId, out string itemPath, out int count)
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

    public static string AssetPathToId(string assetPath)
    {
        if (string.IsNullOrEmpty(assetPath)) return null;
        var s = assetPath;
        var dot = s.LastIndexOf('.');
        var slash = s.LastIndexOf('/');
        var cut = Math.Max(dot, slash);
        return cut >= 0 && cut < s.Length - 1 ? s.Substring(cut + 1) : s;
    }
}
