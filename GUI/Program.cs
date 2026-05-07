using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;

namespace Windrose.StackSize.Gui
{
    public static class Program
    {
        public static void Main(string[] args)
        {
            WebApplicationBuilder builder = WebApplication.CreateBuilder(args);
            builder.WebHost.UseUrls("http://localhost:17777");

            WebApplication app = builder.Build();

            // GUI/ lives directly under the repo root, so go up one level from ContentRoot.
            string repoRoot = Path.GetFullPath(Path.Combine(app.Environment.ContentRootPath, ".."));
            string sourcesDir = Path.Combine(repoRoot, "Sources", "Vanilla");
            string iconsDir = Path.Combine(repoRoot, "Icons");

            app.UseDefaultFiles();
            app.UseStaticFiles();

#if DEBUG
            // In Debug, expose the repo's Icons/ folder under /Icons.
            // In Release we'd embed/copy them, but that's a future concern.
            if (Directory.Exists(iconsDir))
            {
                app.UseStaticFiles(new StaticFileOptions
                {
                    FileProvider = new PhysicalFileProvider(iconsDir),
                    RequestPath = "/Icons"
                });
            }
#endif

            app.MapGet("/api/items", () =>
            {
                List<ItemDto> items = LoadItems(sourcesDir, iconsDir);
                return Results.Json(items);
            });

            app.Run();
        }

        private static List<ItemDto> LoadItems(string sourcesDir, string iconsDir)
        {
            List<ItemDto> result = new List<ItemDto>();
            if (!Directory.Exists(sourcesDir))
            {
                return result;
            }

            HashSet<string> availableIcons = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (Directory.Exists(iconsDir))
            {
                foreach (string iconPath in Directory.EnumerateFiles(iconsDir, "*.png", SearchOption.TopDirectoryOnly))
                {
                    availableIcons.Add(Path.GetFileNameWithoutExtension(iconPath));
                }
            }

            foreach (string path in Directory.EnumerateFiles(sourcesDir, "*.json", SearchOption.AllDirectories))
            {
                ItemDto item = TryParseItem(path, availableIcons);
                if (item != null)
                {
                    result.Add(item);
                }
            }

            result.Sort(delegate (ItemDto a, ItemDto b) { return string.CompareOrdinal(a.Id, b.Id); });
            return result;
        }

        private static ItemDto TryParseItem(string jsonPath, HashSet<string> availableIcons)
        {
            try
            {
                using (FileStream stream = File.OpenRead(jsonPath))
                using (JsonDocument doc = JsonDocument.Parse(stream))
                {
                    JsonElement root = doc.RootElement;
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

                    string id = Path.GetFileNameWithoutExtension(jsonPath);

                    int maxCountInSlot = 0;
                    string itemClass = "";
                    string rarity = "";
                    string category = "";

                    if (root.TryGetProperty("InventoryItemGppData", out JsonElement gpp) && gpp.ValueKind == JsonValueKind.Object)
                    {
                        if (gpp.TryGetProperty("MaxCountInSlot", out JsonElement maxEl) && maxEl.ValueKind == JsonValueKind.Number)
                        {
                            maxEl.TryGetInt32(out maxCountInSlot);
                        }
                        if (gpp.TryGetProperty("ItemClass", out JsonElement icEl) && icEl.ValueKind == JsonValueKind.String)
                        {
                            itemClass = icEl.GetString();
                        }
                        if (gpp.TryGetProperty("Rarity", out JsonElement rEl) && rEl.ValueKind == JsonValueKind.String)
                        {
                            rarity = rEl.GetString();
                        }
                    }

                    if (root.TryGetProperty("InventoryItemUIData", out JsonElement ui) && ui.ValueKind == JsonValueKind.Object)
                    {
                        if (ui.TryGetProperty("Category", out JsonElement catEl) && catEl.ValueKind == JsonValueKind.String)
                        {
                            category = catEl.GetString();
                        }
                    }

                    string icon = availableIcons.Contains(id) ? ("/Icons/" + id + ".png") : null;

                    return new ItemDto
                    {
                        Id = id,
                        Name = id,
                        Icon = icon,
                        MaxCountInSlot = maxCountInSlot,
                        ItemClass = itemClass,
                        Rarity = rarity,
                        Category = category
                    };
                }
            }
            catch
            {
                return null;
            }
        }
    }

    internal sealed class ItemDto
    {
        [JsonPropertyName("id")]
        public string Id { get; set; }

        [JsonPropertyName("name")]
        public string Name { get; set; }

        [JsonPropertyName("icon")]
        public string Icon { get; set; }

        [JsonPropertyName("maxCountInSlot")]
        public int MaxCountInSlot { get; set; }

        [JsonPropertyName("itemClass")]
        public string ItemClass { get; set; }

        [JsonPropertyName("rarity")]
        public string Rarity { get; set; }

        [JsonPropertyName("category")]
        public string Category { get; set; }
    }
}
