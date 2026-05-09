using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace Windrose.StackSize.Gui.Endpoints;

// GET /api/items -> all R5BLInventoryItem JSONs in Sources/Vanilla, merged
// with the per-item Icons/<id>.json sidecar (localized name + description +
// effects, when available).
public static class ItemsEndpoint
{
    // Marker segment in the on-disk source path. Everything below
    // ".../Plugins/<Plugin>/Content/" maps 1:1 to the UE asset path
    // "/<Plugin>/<rest>".
    const string ContentSegment = "Content";

    public static void Map(WebApplication app, string repoRoot)
    {
        var sourcesDir = Path.Combine(repoRoot, "Sources", "Vanilla");
        var iconsDir = Path.Combine(repoRoot, "Icons");

        app.MapGet("/api/items", async () =>
        {
            var items = await LoadItems(sourcesDir, iconsDir);
            return Results.Json(items);
        });
    }

    static async Task<List<ItemDto>> LoadItems(string sourcesDir, string iconsDir)
    {
        var result = new List<ItemDto>();
        if (!Directory.Exists(sourcesDir))
        {
            return result;
        }

        var availableIcons = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (Directory.Exists(iconsDir))
        {
            foreach (var iconPath in Directory.EnumerateFiles(iconsDir, "*.png", SearchOption.TopDirectoryOnly))
            {
                availableIcons.Add(Path.GetFileNameWithoutExtension(iconPath));
            }
        }

        foreach (var path in Directory.EnumerateFiles(sourcesDir, "*.json", SearchOption.AllDirectories))
        {
            var item = await TryParseItem(iconsDir, path, availableIcons);
            if (item is not null)
            {
                result.Add(item);
            }
        }

        result.Sort((a, b) => string.CompareOrdinal(a.id, b.id));

        return result;
    }

    // Convert a source path like
    //   .../Sources/Vanilla/R5/Plugins/R5BusinessRules/Content/InventoryItems/Consumables/Food/DA_CID_X.json
    // into the canonical UE asset reference
    //   /R5BusinessRules/InventoryItems/Consumables/Food/DA_CID_X.DA_CID_X
    // Returns null if the path doesn't match the expected ".../Plugins/<Plugin>/Content/..." layout.
    static string DerivePath(string jsonPath, string id)
    {
        var parts = jsonPath.Replace('\\', '/').Split('/');
        // Find ".../Plugins/<Plugin>/Content/<rest>"
        for (int i = 0; i + 2 < parts.Length; i++)
        {
            if (parts[i] == "Plugins" && parts[i + 2] == ContentSegment)
            {
                var plugin = parts[i + 1];
                var rest = string.Join('/', parts, i + 3, parts.Length - (i + 3));
                if (rest.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                {
                    rest = rest.Substring(0, rest.Length - ".json".Length);
                }
                return "/" + plugin + "/" + rest + "." + id;
            }
        }
        return null;
    }

    static async Task<ItemDto> TryParseItem(string iconsDir, string jsonPath, HashSet<string> availableIcons)
    {
        try
        {
            using var stream = File.OpenRead(jsonPath);
            using var doc = JsonDocument.Parse(stream);

            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;
            if (!root.TryGetProperty("$type", out JsonElement typeEl)) return null;
            if (typeEl.ValueKind != JsonValueKind.String || typeEl.GetString() != "R5BLInventoryItem") return null;

            var item = new ItemDto { id = Path.GetFileNameWithoutExtension(jsonPath) };
            item.name = item.id;
            item.path = DerivePath(jsonPath, item.id);

            if (root.TryGetProperty("InventoryItemGppData", out var gpp) && gpp.ValueKind == JsonValueKind.Object)
            {
                if (gpp.TryGetProperty("MaxCountInSlot", out var maxEl) && maxEl.ValueKind == JsonValueKind.Number)
                {
                    maxEl.TryGetInt32(out item.maxCountInSlot);
                }
                if (gpp.TryGetProperty("ItemClass", out var icEl) && icEl.ValueKind == JsonValueKind.String)
                {
                    item.itemClass = icEl.GetString();
                }
                if (gpp.TryGetProperty("Rarity", out var rEl) && rEl.ValueKind == JsonValueKind.String)
                {
                    item.rarity = rEl.GetString();
                }
                if (gpp.TryGetProperty("ItemType", out var itEl) && itEl.ValueKind == JsonValueKind.Object
                    && itEl.TryGetProperty("TagName", out var itTagEl) && itTagEl.ValueKind == JsonValueKind.String)
                {
                    item.itemType = itTagEl.GetString();
                }
            }

            if (root.TryGetProperty("InventoryItemUIData", out var ui) && ui.ValueKind == JsonValueKind.Object)
            {
                if (ui.TryGetProperty("Category", out var catEl) && catEl.ValueKind == JsonValueKind.String)
                {
                    item.category = catEl.GetString();
                }
            }

            if (availableIcons.Contains(item.id))
            {
                item.icon = $"/Icons/{item.id}.png";
                var iconJsonPath = Path.Combine(iconsDir, item.id + ".json");
                if (File.Exists(iconJsonPath))
                {
                    using var iconJsonStream = File.OpenRead(iconJsonPath);
                    var meta = await JsonNode.ParseAsync(iconJsonStream);
                    item.meta = meta.AsObject().Count > 0 ? meta[0] : null;
                }
            }

            return item;
        }
        catch
        {
            return null;
        }
    }
}
