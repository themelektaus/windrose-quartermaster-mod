using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Windrose.Quartermaster.Core;
using Windrose.Quartermaster.Core.BuildingCreator;

namespace Windrose.Quartermaster.Web.Endpoints;

// Powers the per-row "Resource picker" in the Building Recipe editor
// (Etappe H2). Returns a list of all vanilla R5BLInventoryItem resources
// extracted to Sources/Vanilla. Lightweight - no CUE4Parse, no extraction,
// just a JSON-glob over the resource folder built once on first request.
//
//   GET /api/vanilla-resources?search=<q>&limit=<n>
//       Search by display-name or stem (case-insensitive substring).
//       Default limit 50, max 200. Returns VanillaResourceDto[].
public static class VanillaResourcesEndpoint
{
    static readonly object _gate = new object();
    static VanillaResourceCatalog _catalog;
    static HashSet<string> _availableIcons;
    static string _iconsDir;

    public static void Map(WebApplication app, string repoRoot)
    {
        EnsureBootstrap(repoRoot);

        app.MapGet("/api/vanilla-resources", (string search, int? limit) =>
        {
            try
            {
                var cat = GetCatalog();
                int lim = limit.GetValueOrDefault(50);
                if (lim < 1) lim = 1;
                // Cap raised to 500 so the frontend can load the full
                // catalog (~159 resources) at once and filter client-side
                // using the loot-picker UX pattern.
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

    // Index PNGs under Icons/ once; resources whose stem matches a file
    // get an iconUrl in the DTO. Cheap directory scan, rebuilt only when
    // bootstrap fires (process lifetime).
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

    // Expose the catalog so other endpoints (BuildingsEndpoint's
    // inspect-recipe handler, BuildPipeline's validation step) can reuse
    // the same instance without each rebuilding from disk.
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
            // Resources live under the R5BusinessRules plugin's
            // InventoryItems tree, in a sibling Resource/ subfolder.
            var resourceDir = Path.Combine(paths.VanillaInventoryItems,
                "DefaultItems", "Resource");
            // Icons folder lives at the repo root - same place
            // ItemsEndpoint reads them from. Used to attach iconUrl to
            // the resource DTOs so the recipe picker shows thumbnails.
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
