using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Windrose.Quartermaster.Core;
using Windrose.Quartermaster.Core.BuildingCreator;

namespace Windrose.Quartermaster.Web.Endpoints;

public static class VanillaResourcesEndpoint
{
    static readonly object _gate = new object();
    static VanillaResourceCatalog _catalog;
    static HashSet<string> _availableIcons;
    static string _iconsDir;

    public static void Map(WebApplication app, string repoRoot)
    {
        app.MapGet("/api/vanilla-resources", (string search, int? limit) =>
        {
            try
            {
                EnsureBootstrap(repoRoot);
                var cat = GetCatalog();
                int lim = limit.GetValueOrDefault(50);
                if (lim < 1) lim = 1;
                if (lim > 500) lim = 500;
                var hits = cat.Search(search ?? "", lim);
                var dtos = new List<VanillaResourceDto>(hits.Count);
                var icons = GetAvailableIcons();
                foreach (var e in hits)
                {
                    var iconUrl = (icons != null && icons.Contains(e.Stem))
                        ? "/Icons/" + e.Stem + ".png"
                        : "";
                    dtos.Add(new VanillaResourceDto
                    {
                        stem = e.Stem,
                        packagePath = e.PackagePath,
                        displayName = e.DisplayName,
                        iconPath = e.IconPath,
                        iconUrl = iconUrl,
                        itemTag = e.ItemTag,
                    });
                }
                return Results.Json(dtos);
            }
            catch (Exception ex)
            {
                return Results.Json(new { error = ex.Message }, statusCode: 503);
            }
        });
    }

    static HashSet<string> GetAvailableIcons()
    {
        var icons = _availableIcons;
        if (icons != null) return icons;
        lock (_gate)
        {
            if (_availableIcons != null) return _availableIcons;
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (!string.IsNullOrWhiteSpace(_iconsDir) && Directory.Exists(_iconsDir))
            {
                foreach (var path in Directory.EnumerateFiles(_iconsDir, "*.png", SearchOption.TopDirectoryOnly))
                {
                    set.Add(Path.GetFileNameWithoutExtension(path));
                }
            }
            _availableIcons = set;
            return set;
        }
    }

    public static VanillaResourceCatalog GetSharedCatalog()
    {
        return GetCatalog();
    }

    static void EnsureBootstrap(string repoRoot)
    {
        lock (_gate)
        {
            if (_catalog != null) return;
            var paths = WindrosePaths.FromModRoot(repoRoot);
            var resourceDir = Path.Combine(paths.VanillaInventoryItems,
                "DefaultItems", "Resource");
            _iconsDir = Path.Combine(repoRoot, "Icons");
            _catalog = new VanillaResourceCatalog
            {
                VanillaResourceDir = resourceDir,
                Log = msg => Console.Error.WriteLine(msg),
            };
        }
    }

    static VanillaResourceCatalog GetCatalog()
    {
        var c = _catalog;
        if (c == null) throw new InvalidOperationException("VanillaResourcesEndpoint not bootstrapped");
        return c;
    }
}
