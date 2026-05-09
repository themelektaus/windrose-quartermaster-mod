using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Windrose.Quartermaster.Core;

namespace Windrose.Quartermaster.Web.Endpoints;

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
            // Pickup-radius is shipped as a pre-baked mod triplet inside
            // this assembly (see Quartermaster.Web.csproj <EmbeddedResource>
            // entries). Pipeline only opens these streams when the profile
            // actually has globals.pickupRadius.doubled = true.
            pipeline.PickupRadiusAssetProvider = ext =>
            {
                var asm = typeof(BuildEndpoint).Assembly;
                return asm.GetManifestResourceStream("PickupRadius." + ext);
            };

            // Redirect the pak straight into Windrose's ~mods/ folder so
            // the engine picks it up without a manual copy step. SteamLocator
            // throws a descriptive error if the install can't be found --
            // surface that as a 500 with the same shape as a build failure.
            try
            {
                pipeline.OutputDir = SteamLocator.FindModsDir();
            }
            catch (Exception ex)
            {
                return Results.Json(new
                {
                    success = false,
                    error = "Could not locate Windrose ~mods folder: " + ex.Message,
                    log,
                }, statusCode: 500);
            }

            try
            {
                var result = await Task.Run(() => pipeline.Build(profile, keepTemp: body.KeepTemp));
                // The loot patcher only runs when the profile actually has
                // loot config, so lootPatchResult is null on stack-only
                // profiles. The frontend distinguishes "no loot configured"
                // (=null) vs "loot configured but no LT actually changed"
                // (=present, written:0) by the field's presence.
                object lootPatchResult = null;
                if (result.LootPatchResult != null)
                {
                    var lpr = result.LootPatchResult;
                    lootPatchResult = new
                    {
                        scanned = lpr.Scanned,
                        unchangedSkip = lpr.UnchangedSkip,
                        noSchema = lpr.NoSchema,
                        written = lpr.Written,
                        multiplierApplied = lpr.MultiplierApplied,
                        edited = lpr.Edited,
                        removed = lpr.Removed,
                        added = lpr.Added,
                        warnings = lpr.Warnings,
                    };
                }
                // PakResult is null on pickup-radius-only builds (no item /
                // loot changes -> no main pak written). Frontend treats null
                // sizeBytes/fileCount as "no main pak" and only shows the
                // pickup-triplet info.
                object pickupRadiusInfo = null;
                if (result.PickupPakPath != null)
                {
                    pickupRadiusInfo = new
                    {
                        pakPath = result.PickupPakPath,
                        ucasPath = result.PickupUcasPath,
                        utocPath = result.PickupUtocPath,
                    };
                }
                return Results.Json(new
                {
                    success = true,
                    pakPath = result.PakPath,
                    sizeBytes = result.PakResult != null ? result.PakResult.SizeBytes : 0L,
                    fileCount = result.PakResult != null ? result.PakResult.FileCount : 0,
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
                    lootPatchResult,
                    pickupRadius = pickupRadiusInfo,
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
