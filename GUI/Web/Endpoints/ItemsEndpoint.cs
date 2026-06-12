using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Windrose.Quartermaster.Core;

namespace Windrose.Quartermaster.Web.Endpoints;

public static class ItemsEndpoint
{
    const string ContentSegment = "Content";

    public static void Map(WebApplication app, string repoRoot)
    {
        var sourcesDir = Path.Combine(repoRoot, "Sources", "Vanilla");
        var iconsDir = Path.Combine(repoRoot, "Icons");

        app.MapGet("/api/items", async (HttpRequest req) =>
        {
            var lang = req.Query["lang"].ToString();
            var items = await LoadItems(sourcesDir, iconsDir, lang);
            return Results.Json(items);
        });

        app.MapGet("/api/item-languages", () => Results.Json(ListLanguages(iconsDir)));

        // The GUI's item display-name language, persisted in the data root so the
        // catalog generation (qm_modtab_items.txt) uses the same choice the header
        // dropdown shows. GET: { language: "de" } or { language: null } when unset.
        app.MapGet("/api/item-language",
            () => Results.Json(new { language = ItemLanguagePreference.Load(repoRoot) }));

        app.MapPost("/api/item-language", async (HttpRequest req) =>
        {
            string lang;
            try
            {
                using var doc = await JsonDocument.ParseAsync(req.Body);
                lang = doc.RootElement.ValueKind == JsonValueKind.Object
                       && doc.RootElement.TryGetProperty("language", out var el)
                       && el.ValueKind == JsonValueKind.String
                    ? el.GetString() : null;
            }
            catch (JsonException)
            {
                return Results.BadRequest(new { error = "Invalid JSON body." });
            }
            // Codes come from the icon metadata keys - keep anything longer or
            // path-unsafe out of the persisted file.
            if (!string.IsNullOrEmpty(lang)
                && (lang.Length > 16 || lang.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0))
            {
                return Results.BadRequest(new { error = "Invalid language code." });
            }
            ItemLanguagePreference.Save(repoRoot, lang);
            return Results.NoContent();
        });
    }

    // Language codes offered by the icon metadata: the extractor writes the same
    // language set into every Icons/*.json, so the keys of any one readable file
    // are the full list. Empty until the icons are extracted.
    static List<string> ListLanguages(string iconsDir)
    {
        var result = new List<string>();
        if (!Directory.Exists(iconsDir)) return result;
        foreach (var path in Directory.EnumerateFiles(iconsDir, "*.json", SearchOption.TopDirectoryOnly))
        {
            try
            {
                using var stream = File.OpenRead(path);
                var node = JsonNode.Parse(stream);
                if (node is not JsonObject obj) continue;
                foreach (var kv in obj)
                {
                    if (kv.Value is JsonObject) result.Add(kv.Key);
                }
                if (result.Count > 0) return result;
            }
            catch
            {
                // Unreadable metadata file - try the next one.
            }
        }
        return result;
    }

    static async Task<List<ItemDto>> LoadItems(string sourcesDir, string iconsDir, string lang)
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
            var item = await TryParseItem(iconsDir, path, availableIcons, lang);
            if (item is not null)
            {
                result.Add(item);
            }
        }

        result.Sort((a, b) => string.CompareOrdinal(a.id, b.id));

        return result;
    }

    static string DerivePath(string jsonPath, string id)
    {
        var parts = jsonPath.Replace('\\', '/').Split('/');
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

    static async Task<ItemDto> TryParseItem(string iconsDir, string jsonPath, HashSet<string> availableIcons, string lang)
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
                    item.meta = SelectMeta(meta.AsObject(), lang);
                }
            }

            return item;
        }
        catch
        {
            return null;
        }
    }

    // Icon metadata is keyed by language code. Pick the requested language,
    // fall back to English, then to the first language present.
    static JsonNode SelectMeta(JsonObject meta, string lang)
    {
        if (!string.IsNullOrEmpty(lang))
        {
            if (meta.TryGetPropertyValue(lang, out var exact) && exact is JsonObject) return exact;
            foreach (var kv in meta)
            {
                if (kv.Value is JsonObject && string.Equals(kv.Key, lang, StringComparison.OrdinalIgnoreCase)) return kv.Value;
            }
        }
        if (meta.TryGetPropertyValue("en", out var en) && en is JsonObject) return en;
        foreach (var kv in meta)
        {
            if (kv.Value is JsonObject) return kv.Value;
        }
        return null;
    }
}
