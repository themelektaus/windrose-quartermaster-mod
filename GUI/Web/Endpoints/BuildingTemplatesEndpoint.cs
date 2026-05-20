using System;
using System.Collections.Generic;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Windrose.Quartermaster.Core;
using Windrose.Quartermaster.Core.BuildingCreator;

namespace Windrose.Quartermaster.Web.Endpoints;

// GET /api/building-templates -> the catalog of "Buildable" archetypes
// the Building Creator tab can clone.
//
// Etappe I.1 introduces the Vanilla-DA browser:
//   /api/building-templates                           -> hardcoded
//                                                        Painting + Bucket
//                                                        (legacy templates,
//                                                         kept until I.2
//                                                         migration is in)
//   /api/building-templates/vanilla?search=&category= -> indexed Vanilla
//                                                        DA_BI_*.uasset
//                                                        catalog (~850
//                                                        entries)
//   /api/building-templates/vanilla/categories        -> distinct
//                                                        category-folder
//                                                        names for the
//                                                        picker's facet
//                                                        filter
//
// I.2 will add an /inspect endpoint that returns per-DA metadata
// (Mesh / Icon / Recipe refs + FText keys) for the dynamic template
// hydration in the patcher.
public static class BuildingTemplatesEndpoint
{
    static readonly object _gate = new object();
    static VanillaBuildingTemplateCatalog _vanillaCatalog;

    public static void Map(WebApplication app, string repoRoot)
    {
        EnsureBootstrap(repoRoot);

        // Legacy: hardcoded Painting + Bucket templates. The Building
        // Creator GUI still consumes this endpoint for the "Quick
        // template" dropdown until the I.2 frontend swap.
        app.MapGet("/api/building-templates", () =>
        {
            var catalog = new List<BuildingTemplateDto>
            {
                ToDto(BuildingTemplate.Painting()),
                ToDto(BuildingTemplate.Bucket()),
            };
            return Results.Json(catalog);
        });

        // Etappe I.1: searchable catalog over every Vanilla DA_BI_*.uasset
        // under /Game/Gameplay/Building/. Lightweight - path-level
        // metadata only. The GUI uses this for the "Browse Vanilla
        // templates" picker; the picked DA path will be hydrated into a
        // full BuildingTemplate by the I.2 inspector at build-time.
        app.MapGet("/api/building-templates/vanilla", (string search, string category, int? limit) =>
        {
            try
            {
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

        // Distinct category-folder names for the picker's facet filter.
        // ~8 entries on Windrose 5.6 (BuildingDecoration, BuildingPoi,
        // BuildingCrafts, BuildingFarming, BuildingItems, BuildingPoi,
        // BuildingUtilities, BuildingEmployees, BuildingDockyard).
        app.MapGet("/api/building-templates/vanilla/categories", () =>
        {
            try
            {
                var cat = GetVanillaCatalog();
                return Results.Json(cat.Categories);
            }
            catch (Exception ex)
            {
                return Results.Json(new { error = ex.Message }, statusCode: 503);
            }
        });

        // Etappe I.2: per-DA inspection. Loads the picked Vanilla DA
        // through the shared CUE4Parse provider and surfaces its
        // Mesh/Icon/Recipe refs + FText keys so the frontend can:
        //   - Render a per-template preview (mesh stem, icon stem, recipe stem)
        //   - Pre-fill the recipe editor with the picked DA's vanilla cost
        //   - Sanity-check that the picked DA is a R5BuildingItem
        app.MapGet("/api/building-templates/vanilla/inspect", (string id) =>
        {
            if (string.IsNullOrWhiteSpace(id))
                return Results.Json(new { error = "id query parameter is required" }, statusCode: 400);

            try
            {
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

    // Used by other endpoints (BuildingsEndpoint.InspectRecipe) and by
    // BuildPipeline.ResolveBuildingTemplate to hydrate a profile's
    // templateId into a full BuildingTemplate. Public-static to keep the
    // catalog singleton accessible across endpoint files.
    public static VanillaBuildingTemplateCatalog GetSharedCatalog() => _vanillaCatalog
        ?? throw new InvalidOperationException("Vanilla building template catalog not bootstrapped");

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

    // Project the (Core-side) BuildingTemplate onto the (Web-side) DTO.
    // Etappe G: templates no longer carry material slot definitions
    // (slots come from the user's cooked mesh) - the DTO only exposes
    // gameplay-side metadata.
    static BuildingTemplateDto ToDto(BuildingTemplate t)
    {
        return new BuildingTemplateDto
        {
            id          = t.Id,
            label       = t.DisplayName,
            description = t.Description,
            // All current templates target the Decoration tab; encode as
            // a coarse kind so a future GUI grouping has something to
            // hang on.
            kind        = "Decoration",
            categoryTag = t.CategoryTag,
        };
    }
}
