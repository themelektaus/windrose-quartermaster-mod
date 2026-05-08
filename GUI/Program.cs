using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;

namespace Windrose.StackSize.Gui;

public static class Program
{
    static WebApplication app;

    static string RepoRoot => Path.GetFullPath(Path.Combine(app.Environment.ContentRootPath, ".."));
    static string SourcesDir => Path.Combine(RepoRoot, "Sources", "Vanilla");
    static string IconsDir => Path.Combine(RepoRoot, "Icons");

    public static void Main(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);
        builder.Services.Configure<JsonOptions>(x => x.SerializerOptions.IncludeFields = true);
        builder.WebHost.UseUrls("http://localhost:17777");

        app = builder.Build();

        app.UseDefaultFiles();
        app.UseStaticFiles();

        if (Directory.Exists(IconsDir))
        {
            app.UseStaticFiles(new StaticFileOptions
            {
                FileProvider = new PhysicalFileProvider(IconsDir),
                RequestPath = "/Icons"
            });
        }

        app.MapGet("/api/items", async () =>
        {
            var items = await LoadItems(SourcesDir, IconsDir);
            return Results.Json(items);
        });

        app.Run();
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

    static async Task<ItemDto> TryParseItem(string iconsDir, string jsonPath, HashSet<string> availableIcons)
    {
        try
        {
            using var stream = File.OpenRead(jsonPath);
            using var doc = JsonDocument.Parse(stream);

            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return null;
            }
            if (!root.TryGetProperty("$type", out JsonElement typeEl))
            {
                return null;
            }
            if (typeEl.ValueKind != JsonValueKind.String || typeEl.GetString() != "R5BLInventoryItem")
            {
                return null;
            }

            var item = new ItemDto { id = Path.GetFileNameWithoutExtension(jsonPath) };
            item.name = item.id;

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
                var iconJsonPath = $"{iconsDir}/{item.id}.json";
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
