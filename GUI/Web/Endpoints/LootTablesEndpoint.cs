using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Windrose.Quartermaster.Core;

using static Windrose.Quartermaster.Web.Endpoints.RecipeRefHelpers;
namespace Windrose.Quartermaster.Web.Endpoints;

public static class LootTablesEndpoint
{
    const string LootTablesPathPrefix = "/R5BusinessRules/LootTables/";

    public static void Map(WebApplication app, string repoRoot)
    {
        var paths = WindrosePaths.FromModRoot(repoRoot);

        app.MapGet("/api/loot-tables", async () =>
        {
            var tables = await LoadLootTables(paths.VanillaLootTables);
            return Results.Json(tables);
        });
    }

    static async Task<List<LootTableDto>> LoadLootTables(string lootDir)
    {
        var result = new List<LootTableDto>();
        if (!Directory.Exists(lootDir)) return result;

        var rootFull = Path.GetFullPath(lootDir);
        foreach (var path in Directory.EnumerateFiles(rootFull, "*.json", SearchOption.AllDirectories))
        {
            var dto = await TryParseLootTable(rootFull, path);
            if (dto != null) result.Add(dto);
        }

        result.Sort((a, b) => string.CompareOrdinal(a.id, b.id));
        return result;
    }

    static async Task<LootTableDto> TryParseLootTable(string rootFull, string jsonPath)
    {
        try
        {
            using var stream = File.OpenRead(jsonPath);
            using var doc = await JsonDocument.ParseAsync(stream);

            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;
            if (!root.TryGetProperty("$type", out var typeEl)
                || typeEl.ValueKind != JsonValueKind.String
                || typeEl.GetString() != "R5BLLootParams")
                return null;

            var rel = jsonPath.Substring(rootFull.Length).TrimStart(
                Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var ltId = rel
                .Replace(Path.DirectorySeparatorChar, '/')
                .Replace(Path.AltDirectorySeparatorChar, '/');
            if (ltId.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                ltId = ltId.Substring(0, ltId.Length - 5);

            var slash = ltId.IndexOf('/');
            var category = slash < 0 ? "(other)" : ltId.Substring(0, slash);

            var dto = new LootTableDto
            {
                id = ltId,
                category = category,
                type = root.TryGetProperty("LootTableType", out var ltt)
                       && ltt.ValueKind == JsonValueKind.String
                    ? ltt.GetString()
                    : null,
                entries = new List<LootEntryDto>(),
            };

            if (!root.TryGetProperty("LootData", out var dataEl)
                || dataEl.ValueKind != JsonValueKind.Array)
                return dto;

            int idx = 0;
            foreach (var e in dataEl.EnumerateArray())
            {
                if (e.ValueKind != JsonValueKind.Object) { idx++; continue; }

                int min = e.TryGetProperty("Min", out var mn) && mn.ValueKind == JsonValueKind.Number
                    ? mn.GetInt32() : 0;
                int max = e.TryGetProperty("Max", out var mx) && mx.ValueKind == JsonValueKind.Number
                    ? mx.GetInt32() : 0;
                int weight = e.TryGetProperty("Weight", out var w) && w.ValueKind == JsonValueKind.Number
                    ? w.GetInt32() : 0;

                string itemPath = ReadStringOrNone(e, "LootItem");
                string tablePath = ReadStringOrNone(e, "LootTable");

                dto.entries.Add(new LootEntryDto
                {
                    index = idx,
                    min = min,
                    max = max,
                    weight = weight,
                    lootItemPath = itemPath,
                    lootItemId = AssetPathToId(itemPath),
                    lootTablePath = tablePath,
                    lootTableId = LootTablePathToId(tablePath),
                });
                idx++;
            }

            return dto;
        }
        catch
        {
            return null;
        }
    }

    // UE sentinel "None" (and empty) -> null.
    static string ReadStringOrNone(JsonElement parent, string prop)
    {
        if (!parent.TryGetProperty(prop, out var el)) return null;
        if (el.ValueKind != JsonValueKind.String) return null;
        var s = el.GetString();
        if (string.IsNullOrEmpty(s) || s == "None") return null;
        return s;
    }

    // UE asset path -> short id. Handles both "/Folder/AssetName" and the
    // cooked "/Folder/AssetName.AssetName" forms, reducing both to "AssetName".

    static string LootTablePathToId(string assetPath)
    {
        if (string.IsNullOrEmpty(assetPath)) return null;
        if (!assetPath.StartsWith(LootTablesPathPrefix, StringComparison.Ordinal))
        {
            return AssetPathToId(assetPath);
        }
        var trimmed = assetPath.Substring(LootTablesPathPrefix.Length);
        var dot = trimmed.LastIndexOf('.');
        if (dot < 0) return trimmed;
        var slash = trimmed.LastIndexOf('/', dot - 1);
        var lastSeg = slash < 0 ? trimmed.Substring(0, dot) : trimmed.Substring(slash + 1, dot - slash - 1);
        var afterDot = trimmed.Substring(dot + 1);
        return string.Equals(lastSeg, afterDot, StringComparison.Ordinal)
            ? trimmed.Substring(0, dot)
            : trimmed;
    }
}
