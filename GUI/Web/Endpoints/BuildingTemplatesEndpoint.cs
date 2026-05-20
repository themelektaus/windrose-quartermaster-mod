using System.Collections.Generic;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Windrose.Quartermaster.Core.BuildingCreator;

namespace Windrose.Quartermaster.Web.Endpoints;

// GET /api/building-templates -> the catalog of "Buildable" archetypes
// the Building Creator tab can clone.
//
// Phase 1 ships exactly one entry: "Painting" - the Vanilla HighLands
// painting cloned to allow the user to mount a custom image on a
// wall. Additional templates (Furniture, Light, ...) can be added by
// extending BuildingTemplate.<Factory>() in
// Tools/QuartermasterCore/BuildingCreator/BuildingTemplate.cs and
// listing the factory call here - the frontend reads template slots
// dynamically, so a new template with extra slots needs no UI change.
public static class BuildingTemplatesEndpoint
{
    public static void Map(WebApplication app, string repoRoot)
    {
        app.MapGet("/api/building-templates", () =>
        {
            var catalog = new List<BuildingTemplateDto>
            {
                ToDto(BuildingTemplate.Painting()),
                ToDto(BuildingTemplate.Bucket()),
            };
            return Results.Json(catalog);
        });
    }

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
