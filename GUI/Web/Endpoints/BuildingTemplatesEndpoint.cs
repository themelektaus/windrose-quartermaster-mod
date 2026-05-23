using System;
using System.Collections.Generic;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Windrose.Quartermaster.Core;
using Windrose.Quartermaster.Core.BuildingCreator;

namespace Windrose.Quartermaster.Web.Endpoints;

// Building Creator tab endpoints. The Vanilla-DA browser is the only
// template source - the user picks a vanilla DA via the picker, the
// inspector reads its Mesh / Icon / Recipe refs + FText keys, and the
// build pipeline hydrates a BuildingTemplate from the result.
//
//   /api/building-templates/vanilla?search=&category= -> indexed Vanilla
//                                                        DA_BI_*.uasset
//                                                        catalog (~850
//                                                        entries)
//   /api/building-templates/vanilla/categories        -> distinct
//                                                        category-folder
//                                                        names for the
//                                                        picker's facet
//                                                        filter
//   /api/building-templates/vanilla/inspect?id=...    -> per-DA metadata
//                                                        (Mesh / Icon /
//                                                        Recipe refs +
//                                                        FText keys)
public static class BuildingTemplatesEndpoint
{
    static readonly object _gate = new object();
    static VanillaBuildingTemplateCatalog _vanillaCatalog;
    // Captured at Map() time so other endpoints (BuildEndpoint) can trigger
    // the same lazy bootstrap without re-plumbing repoRoot through every
    // call site. Without this, clicking Build before opening the Buildings
    // tab leaves _vanillaCatalog null and ResolveBuildingTemplate logs
    // "BuildingTemplateCatalog is not configured - skipping" for every
    // Vanilla-DA-path templateId.
    static string _repoRoot;

    public static void Map(WebApplication app, string repoRoot)
    {
        _repoRoot = repoRoot;

        // Lazy bootstrap: defer SteamLocator / UsmapLocator lookups to the
        // first endpoint hit so a missing Steam install or usmap doesn't
        // crash the app at startup. Failures become 503s on the first
        // request instead of a "Quartermaster failed to start" dialog.

        // Etappe I.1: searchable catalog over every Vanilla DA_BI_*.uasset
        // under /Game/Gameplay/Building/. Lightweight - path-level
        // metadata only. The GUI uses this for the "Browse Vanilla
        // templates" picker; the picked DA path will be hydrated into a
        // full BuildingTemplate by the I.2 inspector at build-time.
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

        // Distinct category-folder names for the picker's facet filter.
        // ~8 entries on Windrose 5.6 (BuildingDecoration, BuildingPoi,
        // BuildingCrafts, BuildingFarming, BuildingItems, BuildingPoi,
        // BuildingUtilities, BuildingEmployees, BuildingDockyard).
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

    // Used by other endpoints (BuildingsEndpoint.InspectRecipe) and by
    // BuildPipeline.ResolveBuildingTemplate to hydrate a profile's
    // templateId into a full BuildingTemplate. Triggers the lazy bootstrap
    // itself so callers that bypass the Buildings tab (notably the Build
    // button) still see a populated catalog.
    public static VanillaBuildingTemplateCatalog GetSharedCatalog()
    {
        EnsureBootstrap();
        return _vanillaCatalog
            ?? throw new InvalidOperationException("Vanilla building template catalog not bootstrapped");
    }

    // No-arg overload for callers outside this endpoint (BuildEndpoint).
    // Uses the repoRoot captured at Map() time.
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
