using System;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Windrose.Quartermaster.Core;

namespace Windrose.Quartermaster.Web.Endpoints;

// Global UI scale lives in the per-user UE config (Engine.ini), not in a pak
// or a profile. Like the savegame patcher this is a direct, user-triggered
// local-file edit, so it gets its own GET (read live value) / POST (write).
public static class UiScaleEndpoint
{
    public sealed record UiScaleRequest(double Scale);

    public static void Map(WebApplication app, string repoRoot)
    {
        app.MapGet("/api/uiscale", () =>
        {
            try
            {
                var path = UiScalePatcher.EngineIniPath();
                var current = UiScalePatcher.ReadCurrentScale();
                return Results.Ok(new
                {
                    success = true,
                    supported = path != null,
                    scale = current ?? UiScalePatcher.VanillaScale,
                    isSet = current.HasValue,
                    readOnly = UiScalePatcher.IsReadOnly(),
                    path,
                });
            }
            catch (Exception ex)
            {
                return Results.Json(new { success = false, error = ex.Message },
                    statusCode: StatusCodes.Status500InternalServerError);
            }
        });

        app.MapPost("/api/uiscale", (UiScaleRequest req) =>
        {
            if (req == null)
                return Results.Json(new { success = false, error = "Missing body." },
                    statusCode: StatusCodes.Status400BadRequest);

            try
            {
                var r = UiScalePatcher.Apply(req.Scale);
                return Results.Ok(new
                {
                    success = true,
                    written = r.Written,
                    scale = r.Scale,
                    fileExisted = r.FileExisted,
                    sectionExisted = r.SectionExisted,
                    keyExisted = r.KeyExisted,
                    readOnlySet = r.ReadOnlySet,
                    path = r.Path,
                });
            }
            catch (Exception ex)
            {
                return Results.Json(new { success = false, error = ex.Message },
                    statusCode: StatusCodes.Status500InternalServerError);
            }
        });
    }
}
