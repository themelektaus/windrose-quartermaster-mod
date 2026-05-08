using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Windrose.StackSize.Core;

namespace Windrose.StackSize.Gui.Endpoints;

// POST /api/build  body: { profileId: "...", keepTemp: false }
//
// Synchronously runs the full pipeline (patch -> pack -> cleanup) for the
// given profile. Returns the captured log lines + patcher counters + final
// pak metadata.  Sub-second for typical profiles, so we don't bother with
// background-job orchestration / SSE here.
public static class BuildEndpoint
{
    public static void Map(WebApplication app, string repoRoot)
    {
        var paths = WindrosePaths.FromModRoot(repoRoot);
        var store = new ProfileStore(paths);

        app.MapPost("/api/build", async (HttpRequest req) =>
        {
            BuildRequestDto body;
            try
            {
                body = await JsonSerializer.DeserializeAsync<BuildRequestDto>(
                    req.Body, ProfileStore.JsonOpts);
            }
            catch (Exception ex)
            {
                return Results.BadRequest(new { error = "Invalid JSON: " + ex.Message });
            }

            if (body == null || string.IsNullOrEmpty(body.ProfileId))
                return Results.BadRequest(new { error = "profileId is required" });

            var profile = store.Load(body.ProfileId);
            if (profile == null)
                return Results.NotFound(new { error = "Profile not found", id = body.ProfileId });

            var log = new List<string>();
            var pipeline = new BuildPipeline(paths);
            // Pipeline.Build runs synchronously on a worker thread; the
            // callback fires from there. List access is safe because we
            // only read it from the same thread (Task.Run completion below).
            pipeline.Log = m => log.Add(m);

            try
            {
                var result = await Task.Run(() => pipeline.Build(profile, keepTemp: body.KeepTemp));
                return Results.Json(new
                {
                    success = true,
                    pakPath = result.PakPath,
                    sizeBytes = result.PakResult.SizeBytes,
                    fileCount = result.PakResult.FileCount,
                    patchResult = new
                    {
                        scanned = result.PatchResult.Scanned,
                        excluded = result.PatchResult.Excluded,
                        noSchema = result.PatchResult.NoSchema,
                        skipped = result.PatchResult.Skipped,
                        unchangedSkip = result.PatchResult.UnchangedSkip,
                        written = result.PatchResult.Written,
                        promoted = result.PatchResult.Promoted,
                        overridden = result.PatchResult.Overridden,
                        capped = result.PatchResult.Capped,
                    },
                    log,
                });
            }
            catch (Exception ex)
            {
                return Results.Json(new
                {
                    success = false,
                    error = ex.Message,
                    log,
                }, statusCode: 500);
            }
        });
    }

    public sealed class BuildRequestDto
    {
        public string ProfileId;
        public bool KeepTemp;
    }
}
