using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Windrose.StackSize.Core;

namespace Windrose.StackSize.Gui.Endpoints;

// CRUD endpoints for Profile objects:
//
//   GET    /api/profiles               -> list of summary objects
//   GET    /api/profiles/{id}          -> full profile
//   POST   /api/profiles               -> create user profile (server assigns id)
//   PUT    /api/profiles/{id}          -> overwrite user profile
//   DELETE /api/profiles/{id}          -> delete user profile
//   POST   /api/profiles/{id}/duplicate -> clone any profile (incl. builtins)
//                                          into a new user profile
//
// Builtins are read-only: PUT/DELETE on a builtin returns 403. They are
// editable only via /duplicate -> edit the clone.
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
            return Results.Json(ToFullDto(profile));
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
            // so they can't accidentally overwrite a builtin or another user
            // profile.
            incoming.Id = Guid.NewGuid().ToString();
            incoming.IsBuiltin = false;

            try { store.Save(incoming); }
            catch (Exception ex) { return Results.BadRequest(new { error = ex.Message }); }

            return Results.Created("/api/profiles/" + incoming.Id, ToFullDto(incoming));
        });

        app.MapPut("/api/profiles/{id}", async (string id, HttpRequest req) =>
        {
            var existing = store.Load(id);
            if (existing == null) return Results.NotFound(new { error = "Profile not found", id });
            if (existing.IsBuiltin)
                return Results.Json(new { error = "Builtin profiles cannot be modified -- duplicate first" },
                                    statusCode: 403);

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
            incoming.IsBuiltin = false;
            incoming.CreatedAt = existing.CreatedAt;

            try { store.Save(incoming); }
            catch (Exception ex) { return Results.BadRequest(new { error = ex.Message }); }

            return Results.Json(ToFullDto(incoming));
        });

        app.MapDelete("/api/profiles/{id}", (string id) =>
        {
            var existing = store.Load(id);
            if (existing == null) return Results.NotFound(new { error = "Profile not found", id });
            if (existing.IsBuiltin)
                return Results.Json(new { error = "Builtin profiles cannot be deleted" }, statusCode: 403);

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
                IsBuiltin = false,
            };

            try { store.Save(clone); }
            catch (Exception ex) { return Results.BadRequest(new { error = ex.Message }); }

            return Results.Created("/api/profiles/" + clone.Id, ToFullDto(clone));
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

    // Lightweight summary for the list view -- the full profile (including
    // every override) only loads when the user opens it.
    static object ToSummary(Profile p)
    {
        return new
        {
            id = p.Id,
            name = p.Name,
            description = p.Description,
            isBuiltin = p.IsBuiltin,
            createdAt = p.CreatedAt,
            modifiedAt = p.ModifiedAt,
            overrideCount = p.Overrides == null ? 0 : p.Overrides.Count,
            lootOverrideCount = p.LootOverrides == null ? 0 : p.LootOverrides.Count,
            hasGlobalStackSize = p.Globals != null && p.Globals.StackSize != null
                                 && (p.Globals.StackSize.Multiplier.HasValue
                                     || p.Globals.StackSize.Absolute.HasValue),
            hasGlobalLoot = p.Globals != null && p.Globals.Loot != null
                            && p.Globals.Loot.ByCategory != null
                            && p.Globals.Loot.ByCategory.Count > 0,
        };
    }

    // Full profile + isBuiltin flag attached for the client.
    // (isBuiltin is [JsonIgnore] on the model so it never hits disk -- we
    // inject it here on the way out.)
    static JsonNode ToFullDto(Profile p)
    {
        var node = JsonSerializer.SerializeToNode(p, ProfileStore.JsonOpts).AsObject();
        node["isBuiltin"] = p.IsBuiltin;
        return node;
    }
}
