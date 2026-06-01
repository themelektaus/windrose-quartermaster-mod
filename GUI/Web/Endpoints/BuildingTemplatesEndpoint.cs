using System;
using System.Collections.Generic;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Windrose.Quartermaster.Core;
using Windrose.Quartermaster.Core.BuildingCreator;

namespace Windrose.Quartermaster.Web.Endpoints;

public static class BuildingTemplatesEndpoint
{
    static readonly object _gate = new object();
    static VanillaBuildingTemplateCatalog _vanillaCatalog;
    static string _repoRoot;

    public static void Map(WebApplication app, string repoRoot)
    {
        _repoRoot = repoRoot;

        app.MapGet("/api/building-templates/vanilla", (string search, string category, int? limit) =>
        {
            try
            {
                EnsureBootstrap(repoRoot);
                var cat = GetVanillaCatalog();
                int lim = limit.GetValueOrDefault(100);
                if (lim < 1) lim = 1;
                if (lim > 1000) lim = 1000;
                var hits = cat.Search(search ?? "", category ?? "", lim);
                var dtos = new List<VanillaBuildingTemplateDto>(hits.Count);
                foreach (var e in hits)
                {
                    dtos.Add(new VanillaBuildingTemplateDto
                    {
                        id          = e.Id,
                        displayName = e.DisplayName,
                        category    = e.Category,
                        packagePath = e.PackagePath,
                    });
                }
                return Results.Json(dtos);
            }
            catch (Exception ex)
            {
                return Results.Json(new { error = ex.Message }, statusCode: 503);
            }
        });

        app.MapGet("/api/building-templates/vanilla/categories", () =>
        {
            try
            {
                EnsureBootstrap(repoRoot);
                var cat = GetVanillaCatalog();
                return Results.Json(cat.Categories);
            }
            catch (Exception ex)
            {
                return Results.Json(new { error = ex.Message }, statusCode: 503);
            }
        });

        app.MapGet("/api/building-templates/vanilla/inspect", (string id) =>
        {
            if (string.IsNullOrWhiteSpace(id))
                return Results.Json(new { error = "id query parameter is required" }, statusCode: 400);

            try
            {
                EnsureBootstrap(repoRoot);
                var dto = InspectVanillaTemplate(id);
                return Results.Json(dto);
            }
            catch (Exception ex)
            {
                return Results.Json(new { error = ex.Message }, statusCode: 500);
            }
        });
    }

    static VanillaBuildingTemplateInspectDto InspectVanillaTemplate(string id)
    {
        var inspector = new VanillaBuildingTemplateInspector
        {
            Catalog = GetVanillaCatalog(),
            Log     = msg => Console.WriteLine("[building-inspect] " + msg),
        };
        var ins = inspector.Inspect(id);
        return new VanillaBuildingTemplateInspectDto
        {
            id              = ins.Id,
            displayName     = ins.DisplayName,
            category        = ins.Category,
            packagePath     = ins.PackagePath,
            pakRelativePath = ins.PakRelativePath,
            assetClass      = ins.AssetClass,
            meshStem        = ins.MeshStem,
            meshPath        = ins.MeshPath,
            iconStem        = ins.IconStem,
            iconPath        = ins.IconPath,
            recipeStem      = ins.RecipeStem,
            recipePath      = ins.RecipePath,
            recipeJsonPath  = ins.RecipeJsonPath,
            nameKey         = ins.NameKey,
            descriptionKey  = ins.DescriptionKey,
            error           = ins.Error,
            warnings        = ins.Warnings ?? new List<string>(),
        };
    }

    public static VanillaBuildingTemplateCatalog GetSharedCatalog()
    {
        EnsureBootstrap();
        return _vanillaCatalog
            ?? throw new InvalidOperationException("Vanilla building template catalog not bootstrapped");
    }

    public static void EnsureBootstrap()
    {
        if (_vanillaCatalog != null) return;
        if (string.IsNullOrEmpty(_repoRoot))
            throw new InvalidOperationException("BuildingTemplatesEndpoint.Map was not invoked - repoRoot unknown");
        EnsureBootstrap(_repoRoot);
    }

    static void EnsureBootstrap(string repoRoot)
    {
        if (_vanillaCatalog != null) return;
        lock (_gate)
        {
            if (_vanillaCatalog != null) return;

            var paksDir   = SteamLocator.FindVanillaPaksDir();
            var aesKey    = WindroseGameSecrets.AesKey;
            var usmapPath = UsmapLocator.Find(repoRoot);

            _vanillaCatalog = new VanillaBuildingTemplateCatalog
            {
                PaksDir   = paksDir,
                AesKey    = aesKey,
                UsmapPath = usmapPath,
                Log       = msg => Console.WriteLine("[building-catalog] " + msg),
            };
        }
    }

    static VanillaBuildingTemplateCatalog GetVanillaCatalog() => _vanillaCatalog
        ?? throw new InvalidOperationException("Vanilla building template catalog not bootstrapped");
}
