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
            };
            return Results.Json(catalog);
        });
    }

    // Project the (Core-side) BuildingTemplate onto the (Web-side) DTO.
    // We strip the heavy Vanilla-asset paths (Mesh / Icon / DA / MI /
    // textures) the GUI doesn't need - those are an internal patcher
    // concern, and surfacing them would let a curious user "fix" them
    // into something the patcher pipeline doesn't expect.
    static BuildingTemplateDto ToDto(BuildingTemplate t)
    {
        var slots = new List<BuildingTemplateSlotDto>();
        if (t.Slots != null)
        {
            foreach (var s in t.Slots)
            {
                slots.Add(new BuildingTemplateSlotDto
                {
                    slotName           = s.SlotName,
                    userAlbedoRequired = s.UserAlbedoRequired,
                });
            }
        }
        return new BuildingTemplateDto
        {
            id          = t.Id,
            label       = t.DisplayName,
            description = t.Description,
            // Until we ship more than one template, every entry is a
            // Decoration. Encode it as a coarse kind so the GUI can
            // group by it later.
            kind        = "Decoration",
            categoryTag = t.CategoryTag,
            slots       = slots,
        };
    }
}
