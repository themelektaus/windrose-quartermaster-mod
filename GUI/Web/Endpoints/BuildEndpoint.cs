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
            // Pickup-radius is built fresh per build by retoc + UAssetAPI:
            // the pipeline pulls the live game's vanilla GA_Loot_AutoPickup,
            // patches MagnetRadius via UAssetAPI, then re-packs as an
            // IoStore triplet next to the main pak. Only used when the
            // profile actually has globals.pickupRadius.multiplier > 1.0.
            // Surfacing the same "no Steam install" error shape for both
            // the main-pak ~mods target and this provider keeps the
            // failure path uniform.
            pipeline.GamePaksDirProvider = SteamLocator.FindVanillaPaksDir;

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
                // PickupResult is null when the profile didn't request
                // pickup or set multiplier=1.0 (no triplet to ship).
                // PakResult is null on pickup-radius-only builds (no item /
                // loot changes -> no main pak written). The frontend treats
                // both null shapes as "this domain wasn't built" rather
                // than "domain build failed".
                //
                // When BOTH are built, pickup's PakPath is null because
                // the main pak overwrites the would-be pickup .pak stub
                // at the same path -- only the .ucas/.utoc are uniquely
                // pickup-owned. The shared on-disk basename is reported
                // via result.PakPath in that case.
                object pickupRadiusInfo = null;
                if (result.PickupResult != null)
                {
                    var pr = result.PickupResult;
                    pickupRadiusInfo = new
                    {
                        pakPath = pr.PakPath,        // null when main was built
                        ucasPath = pr.UcasPath,
                        utocPath = pr.UtocPath,
                        pakSize = pr.PakSize,        // 0 when main was built
                        ucasSize = pr.UcasSize,
                        utocSize = pr.UtocSize,
                        magnetRadius = pr.MagnetRadius,
                        multiplier = result.PickupMultiplier,
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
