using System;
using System.Linq;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Windrose.Quartermaster.Core;

namespace Windrose.Quartermaster.Web.Endpoints;

// Existing-character equipment-slot save patcher (the "More Rings and Necklace
// Slots" mod only affects new characters; this retro-fits existing ones).
// Writes directly to the RocksDB save, so it is a deliberate, user-triggered
// action - not part of the pak build.
public static class SavegameEndpoint
{
    public sealed record SlotPatchRequest(string DbFolder, int RingSlots, int NecklaceSlots, bool Force);

    public static void Map(WebApplication app, string repoRoot)
    {
        app.MapGet("/api/savegame/characters", () =>
        {
            try
            {
                var patcher = new InventorySaveSlotsPatcher();
                var root = InventorySaveSlotsPatcher.SaveProfilesRoot();
                if (root == null)
                    return Results.Ok(new { success = true, supported = false, characters = Array.Empty<object>() });

                var chars = patcher.DiscoverCharacters()
                    .Select(c => new
                    {
                        dbFolder = c.DbFolder,
                        characterId = c.CharacterId,
                        playerName = c.PlayerName,
                        ringSlots = c.RingSlots,
                        necklaceSlots = c.NecklaceSlots,
                        blueprintRing = c.BlueprintRing,
                        blueprintNeck = c.BlueprintNeck,
                    })
                    .ToArray();
                return Results.Ok(new { success = true, supported = true, characters = chars });
            }
            catch (Exception ex)
            {
                return Results.Json(new { success = false, error = ex.Message },
                    statusCode: StatusCodes.Status500InternalServerError);
            }
        });

        app.MapPost("/api/savegame/patch", (SlotPatchRequest req) =>
        {
            if (req == null || string.IsNullOrEmpty(req.DbFolder))
                return Results.Json(new { success = false, error = "Missing character folder." },
                    statusCode: StatusCodes.Status400BadRequest);

            try
            {
                var patcher = new InventorySaveSlotsPatcher();
                var r = patcher.PatchCharacter(req.DbFolder, req.RingSlots, req.NecklaceSlots, req.Force);

                if (r.BlockingItems != null && r.BlockingItems.Count > 0)
                    return Results.Ok(new
                    {
                        success = false,
                        blocked = true,
                        blockingItems = r.BlockingItems,
                        playerName = r.PlayerName,
                    });

                return Results.Ok(new
                {
                    success = true,
                    patched = r.Patched,
                    alreadyMatches = r.AlreadyMatches,
                    playerName = r.PlayerName,
                    oldRing = r.OldRing,
                    oldNeck = r.OldNeck,
                    newRing = r.NewRing,
                    newNeck = r.NewNeck,
                    checkpointZipRebuilt = r.CheckpointZipRebuilt,
                    backupCreated = r.BackupPath != null,
                });
            }
            catch (Exception ex)
            {
                // Most common cause: the game is still running and holds the DB lock.
                return Results.Json(new
                {
                    success = false,
                    error = ex.Message
                        + " (make sure Windrose is fully closed before patching).",
                }, statusCode: StatusCodes.Status500InternalServerError);
            }
        });
    }
}
