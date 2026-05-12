using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Windrose.Quartermaster.Core;

namespace Windrose.Quartermaster.Web.Endpoints;

// CRUD endpoints for Profile objects:
//
//   GET    /api/profiles               -> list of summary objects
//   GET    /api/profiles/{id}          -> full profile
//   POST   /api/profiles               -> create profile (server assigns id)
//   PUT    /api/profiles/{id}          -> overwrite profile
//   DELETE /api/profiles/{id}          -> delete profile
//   POST   /api/profiles/{id}/duplicate -> clone a profile into a new one
public static class ProfilesEndpoint
{
    public static void Map(WebApplication app, string repoRoot)
    {
        var paths = WindrosePaths.FromModRoot(repoRoot);
        var store = new ProfileStore(paths);

        app.MapGet("/api/profiles", () =>
        {
            var summaries = store.LoadAll().Select(ToSummary).ToList();
            return Results.Json(summaries);
        });

        app.MapGet("/api/profiles/{id}", (string id) =>
        {
            var profile = store.Load(id);
            if (profile == null) return Results.NotFound(new { error = "Profile not found", id });
            return Results.Json(profile, ProfileStore.JsonOpts);
        });

        app.MapPost("/api/profiles", async (HttpRequest req) =>
        {
            Profile incoming;
            try
            {
                incoming = await JsonSerializer.DeserializeAsync<Profile>(req.Body, ProfileStore.JsonOpts);
            }
            catch (Exception ex)
            {
                return Results.BadRequest(new { error = "Invalid JSON: " + ex.Message });
            }
            if (incoming == null) return Results.BadRequest(new { error = "Empty body" });
            if (string.IsNullOrWhiteSpace(incoming.Name))
                return Results.BadRequest(new { error = "name is required" });

            // Server always picks the id; ignore any client-supplied value
            // so they can't accidentally overwrite another profile.
            incoming.Id = Guid.NewGuid().ToString();

            try { store.Save(incoming); }
            catch (Exception ex) { return Results.BadRequest(new { error = ex.Message }); }

            return Results.Created("/api/profiles/" + incoming.Id, incoming);
        });

        app.MapPut("/api/profiles/{id}", async (string id, HttpRequest req) =>
        {
            var existing = store.Load(id);
            if (existing == null) return Results.NotFound(new { error = "Profile not found", id });

            Profile incoming;
            try
            {
                incoming = await JsonSerializer.DeserializeAsync<Profile>(req.Body, ProfileStore.JsonOpts);
            }
            catch (Exception ex)
            {
                return Results.BadRequest(new { error = "Invalid JSON: " + ex.Message });
            }
            if (incoming == null) return Results.BadRequest(new { error = "Empty body" });

            // Path id wins over body id; preserve created-at across edits.
            incoming.Id = id;
            incoming.CreatedAt = existing.CreatedAt;

            try { store.Save(incoming); }
            catch (Exception ex) { return Results.BadRequest(new { error = ex.Message }); }

            return Results.Json(incoming, ProfileStore.JsonOpts);
        });

        app.MapDelete("/api/profiles/{id}", (string id) =>
        {
            var existing = store.Load(id);
            if (existing == null) return Results.NotFound(new { error = "Profile not found", id });

            try
            {
                if (!store.Delete(id))
                    return Results.NotFound(new { error = "Profile file not found", id });
            }
            catch (Exception ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
            return Results.NoContent();
        });

        app.MapPost("/api/profiles/{id}/duplicate", (string id) =>
        {
            var src = store.Load(id);
            if (src == null) return Results.NotFound(new { error = "Profile not found", id });

            var clone = new Profile
            {
                Id = Guid.NewGuid().ToString(),
                Name = (src.Name ?? "Profile") + " (copy)",
                Description = src.Description,
                Globals = CloneGlobals(src.Globals),
                Overrides = src.Overrides == null
                    ? new Dictionary<string, ItemOverride>()
                    : src.Overrides.ToDictionary(
                        kvp => kvp.Key,
                        kvp => kvp.Value == null
                            ? null
                            : new ItemOverride { StackSize = kvp.Value.StackSize }),
                LootOverrides = CloneLootOverrides(src.LootOverrides),
                BuyerRecipes = CloneBuyerRecipes(src.BuyerRecipes),
                BuyerLists = CloneBuyerLists(src.BuyerLists),
                CustomItems = CloneCustomItems(src.CustomItems),
            };

            try { store.Save(clone); }
            catch (Exception ex) { return Results.BadRequest(new { error = ex.Message }); }

            return Results.Created("/api/profiles/" + clone.Id, clone);
        });
    }

    static ProfileGlobals CloneGlobals(ProfileGlobals g)
    {
        if (g == null) return null;
        return new ProfileGlobals
        {
            StackSize = g.StackSize == null
                ? null
                : new StackSizeGlobal
                {
                    Multiplier = g.StackSize.Multiplier,
                    Absolute = g.StackSize.Absolute,
                    Cap = g.StackSize.Cap,
                },
            Loot = g.Loot == null
                ? null
                : new LootGlobal
                {
                    ByCategory = g.Loot.ByCategory == null
                        ? null
                        : new Dictionary<string, double>(g.Loot.ByCategory),
                },
            PickupRadius = g.PickupRadius == null
                ? null
                : new PickupRadiusGlobal
                {
                    Multiplier = g.PickupRadius.Multiplier,
                },
            FastTravelBells = g.FastTravelBells == null
                ? null
                : new FastTravelBellsGlobal
                {
                    BellCap = g.FastTravelBells.BellCap,
                    SignalFireCap = g.FastTravelBells.SignalFireCap,
                },
            BuildingStability = g.BuildingStability == null
                ? null
                : new BuildingStabilityGlobal
                {
                    Enabled = g.BuildingStability.Enabled,
                },
            NoSmoke = g.NoSmoke == null
                ? null
                : new NoSmokeGlobal
                {
                    Campfire = g.NoSmoke.Campfire,
                    Furnace = g.NoSmoke.Furnace,
                    Kiln = g.NoSmoke.Kiln,
                },
            MinimapRange = g.MinimapRange == null
                ? null
                : new MinimapRangeGlobal
                {
                    Multiplier = g.MinimapRange.Multiplier,
                },
            BonfireRadius = g.BonfireRadius == null
                ? null
                : new BonfireRadiusGlobal
                {
                    Multiplier = g.BonfireRadius.Multiplier,
                },
        };
    }

    // Deep-clones the per-LT override map. Each LootTableOverride contains
    // dictionaries / lists that we don't want shared with the source profile
    // (otherwise editing the clone would mutate the original).
    static Dictionary<string, LootTableOverride> CloneLootOverrides(
        Dictionary<string, LootTableOverride> src)
    {
        if (src == null) return null;
        var result = new Dictionary<string, LootTableOverride>(src.Count);
        foreach (var kvp in src)
        {
            var v = kvp.Value;
            if (v == null) { result[kvp.Key] = null; continue; }
            result[kvp.Key] = new LootTableOverride
            {
                Entries = v.Entries == null
                    ? null
                    : v.Entries.ToDictionary(
                        e => e.Key,
                        e => e.Value == null
                            ? null
                            : new LootEntryEdit
                            {
                                Min = e.Value.Min,
                                Max = e.Value.Max,
                                Weight = e.Value.Weight,
                                LootItem = e.Value.LootItem,
                                LootTable = e.Value.LootTable,
                            }),
                Removed = v.Removed == null ? null : new List<int>(v.Removed),
                Added = v.Added == null
                    ? null
                    : v.Added.Select(a => a == null
                        ? null
                        : new LootEntry
                        {
                            Min = a.Min,
                            Max = a.Max,
                            Weight = a.Weight,
                            LootItem = a.LootItem,
                            LootTable = a.LootTable,
                        }).ToList(),
            };
        }
        return result;
    }

    // Deep-clones the per-recipe edit map. Safe to share BuyerRecipeOverride
    // values across clones in theory (they're flat), but explicit copying
    // matches the LootOverrides pattern and makes future field additions
    // safer (a forgotten reference-share would silently corrupt the source
    // profile).
    static Dictionary<string, BuyerRecipeOverride> CloneBuyerRecipes(
        Dictionary<string, BuyerRecipeOverride> src)
    {
        if (src == null) return null;
        var result = new Dictionary<string, BuyerRecipeOverride>(src.Count);
        foreach (var kvp in src)
        {
            var v = kvp.Value;
            if (v == null) { result[kvp.Key] = null; continue; }
            result[kvp.Key] = new BuyerRecipeOverride
            {
                ItemPath = v.ItemPath,
                ItemCount = v.ItemCount,
                PayItemPath = v.PayItemPath,
                PayCount = v.PayCount,
                IsCustom = v.IsCustom,
            };
        }
        return result;
    }

    // Deep-clones the custom items list. Each CustomItem is flat (only
    // primitive fields + nullable scalars) so a per-field copy is safe.
    static List<CustomItem> CloneCustomItems(List<CustomItem> src)
    {
        if (src == null) return null;
        var result = new List<CustomItem>(src.Count);
        foreach (var c in src)
        {
            if (c == null) { result.Add(null); continue; }
            result.Add(new CustomItem
            {
                Id = c.Id,
                TemplateId = c.TemplateId,
                Name = c.Name,
                Description = c.Description,
                MaxCountInSlot = c.MaxCountInSlot,
                Rarity = c.Rarity,
                KeepInInventoryOnDeath = c.KeepInInventoryOnDeath,
                ItemTexture = c.ItemTexture,
                VanityText = c.VanityText,
            });
        }
        return result;
    }

    // Deep-clones the per-list edit map. List values are reference-sharing
    // hazards (AddedRecipeIds / RemovedRecipeIds are mutable Lists), so
    // the clone copies the lists explicitly.
    static Dictionary<string, BuyerListOverride> CloneBuyerLists(
        Dictionary<string, BuyerListOverride> src)
    {
        if (src == null) return null;
        var result = new Dictionary<string, BuyerListOverride>(src.Count);
        foreach (var kvp in src)
        {
            var v = kvp.Value;
            if (v == null) { result[kvp.Key] = null; continue; }
            result[kvp.Key] = new BuyerListOverride
            {
                AddedRecipeIds = v.AddedRecipeIds == null ? null : new List<string>(v.AddedRecipeIds),
                RemovedRecipeIds = v.RemovedRecipeIds == null ? null : new List<string>(v.RemovedRecipeIds),
            };
        }
        return result;
    }

    // Lightweight summary for the list view - the full profile (including
    // every override) only loads when the user opens it.
    static object ToSummary(Profile p)
    {
        return new
        {
            id = p.Id,
            name = p.Name,
            description = p.Description,
            createdAt = p.CreatedAt,
            modifiedAt = p.ModifiedAt,
            overrideCount = p.Overrides == null ? 0 : p.Overrides.Count,
            lootOverrideCount = p.LootOverrides == null ? 0 : p.LootOverrides.Count,
            buyerRecipeCount = p.BuyerRecipes == null ? 0 : p.BuyerRecipes.Count,
            buyerListCount = p.BuyerLists == null ? 0 : p.BuyerLists.Count,
            customItemCount = p.CustomItems == null ? 0 : p.CustomItems.Count,
            hasGlobalStackSize = p.Globals != null && p.Globals.StackSize != null
                                 && (p.Globals.StackSize.Multiplier.HasValue
                                     || p.Globals.StackSize.Absolute.HasValue),
            hasGlobalLoot = p.Globals != null && p.Globals.Loot != null
                            && p.Globals.Loot.ByCategory != null
                            && p.Globals.Loot.ByCategory.Count > 0,
            // Multiplier > 1.0 means a triplet would actually be built;
            // 1.0 / null means "no pickup mod", same as no pickup config.
            hasGlobalPickupRadius = p.Globals != null && p.Globals.PickupRadius != null
                                    && p.Globals.PickupRadius.Multiplier.HasValue
                                    && Math.Abs(p.Globals.PickupRadius.Multiplier.Value - 1.0) > 1e-9,
            // True when the profile would actually patch the
            // BuildLimits JSON: at least one cap differs from vanilla
            // (10 bells, 3 signal fires). Mirrors the same logic the
            // build pipeline uses to decide whether to run the patcher.
            hasGlobalFastTravelBells = HasFastTravelBellsConfig(p),
            // True when the single-toggle "enhanced building stability"
            // is on - the build will then self-bake the 787 supported
            // vanilla DA_BI* DataAssets (overwriting the IntegritySettings
            // floats directly in their raw zen chunks) and ship them in
            // the _Raw_P companion triplet next to the main mod.
            hasGlobalBuildingStability = p.Globals != null
                                         && p.Globals.BuildingStability != null
                                         && p.Globals.BuildingStability.Enabled.GetValueOrDefault(false),
            // True when at least one NoSmoke category is on - the build
            // will then self-bake the relevant vanilla Niagara assets
            // (silencing emitter handles) into the IoStore composite.
            hasGlobalNoSmoke = HasAnyNoSmokeCategory(p),
            // True when Minimap-range is configured with a multiplier
            // > 1.0 (1.0 / null collapses to "no minimap mod"). The
            // build then lazy-extracts the vanilla DefaultR5MapSettings
            // .ini, scales four reveal-range fields linearly, and
            // ships the result as a loose file in the _Raw_P companion
            // .pak (shared with stability when both are active).
            hasGlobalMinimapRange = p.Globals != null
                                    && p.Globals.MinimapRange != null
                                    && p.Globals.MinimapRange.Multiplier.HasValue
                                    && Math.Abs(p.Globals.MinimapRange.Multiplier.Value - 1.0) > 1e-9,
            // True when Bonfire-radius is configured with a multiplier
            // > 1.0 (1.0 / null collapses to "no bonfire mod"). The build
            // then patches the two influence floats on
            // DA_BI_Utilities_BuildingCenterT01 and ships the result in
            // the shared IoStore composite triplet alongside Pickup /
            // NoSmoke.
            hasGlobalBonfireRadius = p.Globals != null
                                     && p.Globals.BonfireRadius != null
                                     && p.Globals.BonfireRadius.Multiplier.HasValue
                                     && Math.Abs(p.Globals.BonfireRadius.Multiplier.Value - 1.0) > 1e-9,
        };
    }

    static bool HasAnyNoSmokeCategory(Profile p)
    {
        var n = p.Globals != null ? p.Globals.NoSmoke : null;
        if (n == null) return false;
        return n.Campfire.GetValueOrDefault(false)
            || n.Furnace.GetValueOrDefault(false)
            || n.Kiln.GetValueOrDefault(false);
    }

    static bool HasFastTravelBellsConfig(Profile p)
    {
        var b = p.Globals != null ? p.Globals.FastTravelBells : null;
        if (b == null) return false;
        if (b.BellCap.HasValue && b.BellCap.Value != BellLimitsPatcher.VanillaBellCap)
            return true;
        if (b.SignalFireCap.HasValue && b.SignalFireCap.Value != BellLimitsPatcher.VanillaSignalFireCap)
            return true;
        return false;
    }

}
