using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Windrose.Quartermaster.Core;

namespace Windrose.Quartermaster.Web.Endpoints;

// Unified existing-character save patcher.
//
// The paks (Equipment Slots, Ship Slots, Level Rewards) only affect NEWLY
// created characters / ships. This retro-fits the same values onto an EXISTING
// character's RocksDB save: ring / necklace slots, every owned ship's cargo /
// combat-order slots, and the Level Rewards talent / stat points.
//
// There is ONE discovery endpoint (a merged per-character view) and ONE patch
// endpoint. The patch request carries the per-area targets; only the areas the
// caller includes are touched, and each underlying patcher additionally no-ops
// when the save already matches - so a single button writes just what actually
// differs from the profile. Writes go straight into the save, so this is a
// deliberate, user-triggered action - never part of the pak build.
public static class SavegameEndpoint
{
    // Per-area patch targets. Any of Equipment / Progression / Ships may be null
    // (or empty) - only the supplied areas are patched.
    public sealed record EquipmentTarget(int RingSlots, int NecklaceSlots, int BackpackSlots = 1, int PlayerInventorySlots = 0, double BackpackSlotsMultiplier = 1.0);
    public sealed record ProgressionTarget(int TalentPoints, int StatPoints);
    public sealed record ShipTarget(string ShipKey, double CargoMultiplier, int CombatOrderSlots);
    public sealed record CharacterPatchRequest(
        string DbFolder,
        EquipmentTarget Equipment,
        ProgressionTarget Progression,
        ShipTarget[] Ships,
        bool Force);

    public static void Map(WebApplication app, string repoRoot)
    {
        // Vanilla ship-inventory dir (for per-ship-type cargo base lookup); resolve
        // once at registration so the per-request handlers stay cheap.
        var shipVanillaDir = WindrosePaths.FromModRoot(repoRoot).VanillaShipInventory;

        // ---- ONE discovery endpoint: merged per-character state ----
        // Joins the three read-only discoveries by character DB folder, so every
        // character carries its equipment, progression and owned ships in one row.
        app.MapGet("/api/savegame/characters", () =>
        {
            try
            {
                var root = InventorySaveSlotsPatcher.SaveProfilesRoot();
                if (root == null)
                    return Results.Ok(new { success = true, supported = false, characters = Array.Empty<object>() });

                var agg = new Dictionary<string, CharAgg>(StringComparer.OrdinalIgnoreCase);
                CharAgg Get(string folder, string id)
                {
                    if (!agg.TryGetValue(folder, out var a))
                        agg[folder] = a = new CharAgg { DbFolder = folder, CharacterId = id };
                    return a;
                }

                foreach (var c in new InventorySaveSlotsPatcher().DiscoverCharacters())
                {
                    var a = Get(c.DbFolder, c.CharacterId);
                    a.PlayerName ??= c.PlayerName;
                    a.Equipment = c;
                }
                foreach (var c in new ProgressionSaveSlotsPatcher().DiscoverCharacters())
                {
                    var a = Get(c.DbFolder, c.CharacterId);
                    a.PlayerName ??= c.PlayerName;
                    a.CharacterLevel = c.CharacterLevel;
                    a.Progression = c;
                }
                foreach (var s in new ShipSaveSlotsPatcher(shipVanillaDir).DiscoverShips())
                {
                    var a = Get(s.DbFolder, s.CharacterId);
                    a.PlayerName ??= s.OwnerName;
                    a.Ships.Add(s);
                }

                var characters = agg.Values
                    .OrderBy(a => a.PlayerName ?? a.CharacterId, StringComparer.OrdinalIgnoreCase)
                    .Select(a => new
                    {
                        dbFolder = a.DbFolder,
                        characterId = a.CharacterId,
                        playerName = a.PlayerName ?? a.CharacterId,
                        characterLevel = a.CharacterLevel,
                        equipment = a.Equipment == null ? null : (object)new
                        {
                            ringSlots = a.Equipment.RingSlots,
                            necklaceSlots = a.Equipment.NecklaceSlots,
                            backpackSlots = a.Equipment.BackpackSlots,
                            defaultSlots = a.Equipment.DefaultSlots,
                            blueprintRing = a.Equipment.BlueprintRing,
                            blueprintNeck = a.Equipment.BlueprintNeck,
                            blueprintBack = a.Equipment.BlueprintBack,
                            blueprintDefault = a.Equipment.BlueprintDefault,
                            hasBackpackEquipped = a.Equipment.HasBackpackEquipped,
                            backpackExtraSlots = a.Equipment.BackpackExtraSlots,
                        },
                        progression = a.Progression == null ? null : (object)new
                        {
                            rewardLevel = a.Progression.RewardLevel,
                            characterLevel = a.Progression.CharacterLevel,
                            freeTalent = a.Progression.FreeTalent,
                            freeStat = a.Progression.FreeStat,
                            spentTalent = a.Progression.SpentTalent,
                            spentStat = a.Progression.SpentStat,
                            earnedTalent = a.Progression.EarnedTalent,
                            earnedStat = a.Progression.EarnedStat,
                        },
                        ships = a.Ships.Select(s => new
                        {
                            dbFolder = s.DbFolder,
                            shipKey = s.ShipKey,
                            shipName = s.ShipName,
                            sourceDa = s.SourceDa,
                            supported = s.Supported,
                            cargoSlots = s.CargoSlots,
                            blueprintCargo = s.BlueprintCargo,
                            vanillaCargoBase = s.VanillaCargoBase,
                            combatSlots = s.CombatSlots,
                            blueprintCombat = s.BlueprintCombat,
                        }).ToArray(),
                    })
                    .ToArray();

                return Results.Ok(new { success = true, supported = true, characters });
            }
            catch (Exception ex)
            {
                return Results.Json(new { success = false, error = ex.Message },
                    statusCode: StatusCodes.Status500InternalServerError);
            }
        });

        // ---- ONE patch endpoint: apply only the supplied areas ----
        app.MapPost("/api/savegame/patch", (CharacterPatchRequest req) =>
        {
            if (req == null || string.IsNullOrEmpty(req.DbFolder))
                return Results.Json(new { success = false, error = "Missing character folder." },
                    statusCode: StatusCodes.Status400BadRequest);

            try
            {
                var blocking = new List<string>();
                bool anyBackup = false, anyCheckpoint = false;
                string playerName = null;
                object equipmentResult = null;
                object progressionResult = null;
                var shipResults = new List<object>();

                // Progression first: sets the free pools to the target (raises or
                // lowers, e.g. back to vanilla 1x); only unspent points, never blocks.
                if (req.Progression != null)
                {
                    var r = new ProgressionSaveSlotsPatcher()
                        .PatchCharacter(req.DbFolder, req.Progression.TalentPoints, req.Progression.StatPoints);
                    playerName ??= r.PlayerName;
                    anyBackup |= r.BackupPath != null;
                    anyCheckpoint |= r.CheckpointZipRebuilt;
                    progressionResult = new
                    {
                        applied = r.Patched,
                        alreadyMatches = r.AlreadyMatches,
                        oldTalent = r.OldTalent,
                        newTalent = r.NewTalent,
                        oldStat = r.OldStat,
                        newStat = r.NewStat,
                    };
                }

                // Equipment: can block on a destructive shrink (returns without
                // writing); on a non-destructive change it writes immediately.
                if (req.Equipment != null)
                {
                    int? defSlots = req.Equipment.PlayerInventorySlots > 0
                        ? req.Equipment.PlayerInventorySlots : (int?)null;
                    var r = new InventorySaveSlotsPatcher()
                        .PatchCharacter(req.DbFolder,
                            req.Equipment.RingSlots, req.Equipment.NecklaceSlots,
                            req.Equipment.BackpackSlots, defSlots,
                            req.Equipment.BackpackSlotsMultiplier,
                            req.Force);
                    playerName ??= r.PlayerName;
                    if (r.BlockingItems != null && r.BlockingItems.Count > 0)
                    {
                        foreach (var b in r.BlockingItems) blocking.Add("Equipment: " + b);
                        equipmentResult = new { applied = false, blocked = true };
                    }
                    else
                    {
                        anyBackup |= r.BackupPath != null;
                        anyCheckpoint |= r.CheckpointZipRebuilt;
                        equipmentResult = new
                        {
                            applied = r.Patched,
                            alreadyMatches = r.AlreadyMatches,
                            oldRing = r.OldRing,
                            oldNeck = r.OldNeck,
                            oldBack = r.OldBack,
                            oldDefault = r.OldDefault,
                            newRing = r.NewRing,
                            newNeck = r.NewNeck,
                            newBack = r.NewBack,
                            newDefault = r.NewDefault,
                            newBackpackExtraSlots = r.NewBackpackExtraSlots,
                        };
                    }
                }

                // Ships: one entry per requested ship; each can block independently.
                if (req.Ships != null)
                {
                    var shipPatcher = new ShipSaveSlotsPatcher(shipVanillaDir);
                    foreach (var st in req.Ships)
                    {
                        if (st == null || string.IsNullOrEmpty(st.ShipKey)) continue;
                        var r = shipPatcher.PatchShip(req.DbFolder, st.ShipKey,
                            st.CargoMultiplier, st.CombatOrderSlots, req.Force);
                        if (r.BlockingItems != null && r.BlockingItems.Count > 0)
                        {
                            var label = string.IsNullOrEmpty(r.ShipName) ? r.SourceDa : r.ShipName;
                            foreach (var b in r.BlockingItems) blocking.Add("Ship " + label + ": " + b);
                            shipResults.Add(new { shipKey = st.ShipKey, shipName = r.ShipName, applied = false, blocked = true });
                            continue;
                        }
                        anyBackup |= r.BackupPath != null;
                        anyCheckpoint |= r.CheckpointZipRebuilt;
                        shipResults.Add(new
                        {
                            shipKey = st.ShipKey,
                            shipName = r.ShipName,
                            sourceDa = r.SourceDa,
                            applied = r.Patched,
                            alreadyMatches = r.AlreadyMatches,
                            unsupported = r.Unsupported,
                            oldCargo = r.OldCargo,
                            newCargo = r.NewCargo,
                            oldCombat = r.OldCombat,
                            newCombat = r.NewCombat,
                        });
                    }
                }

                return Results.Ok(new
                {
                    success = true,
                    blocked = blocking.Count > 0,
                    blockingItems = blocking,
                    playerName,
                    equipment = equipmentResult,
                    progression = progressionResult,
                    ships = shipResults,
                    backupCreated = anyBackup,
                    checkpointZipRebuilt = anyCheckpoint,
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

    // Per-character aggregate used to merge the three read-only discoveries.
    sealed class CharAgg
    {
        public string DbFolder;
        public string CharacterId;
        public string PlayerName;
        public int CharacterLevel;
        public SaveCharacter Equipment;
        public SaveProgressionCharacter Progression;
        public List<SaveShip> Ships = new();
    }
}
