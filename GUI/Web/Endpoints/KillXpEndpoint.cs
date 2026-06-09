using System;
using System.Collections.Generic;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Windrose.Quartermaster.Core;

namespace Windrose.Quartermaster.Web.Endpoints;

// Serves the XP-for-kills keyword catalog for the Kill XP tab. The keyword list is
// generated at runtime from the vanilla pak (BP_Mob_* blueprint names) like the
// other vanilla sources, so it tracks game updates without a hand-maintained list.
public static class KillXpEndpoint
{
    static readonly object _gate = new object();
    static KillXpMobCatalog _catalog;

    public static void Map(WebApplication app, string repoRoot)
    {
        app.MapGet("/api/kill-xp/catalog", () =>
        {
            try
            {
                var cat = GetCatalog(repoRoot);
                var dtos = new List<KillXpKeywordDto>(cat.All.Count);
                foreach (var e in cat.All)
                {
                    dtos.Add(new KillXpKeywordDto
                    {
                        keyword      = e.Keyword,
                        label        = e.Label,
                        category     = e.Category,
                        suggestedXp  = e.SuggestedXp,
                        matchesPawns = e.MatchesPawns,
                    });
                }
                return Results.Json(dtos);
            }
            catch (Exception ex)
            {
                // 503 so the frontend can show "catalog unavailable" instead of crashing.
                return Results.Json(new { error = ex.Message }, statusCode: 503);
            }
        });
    }

    static KillXpMobCatalog GetCatalog(string repoRoot)
    {
        if (_catalog != null) return _catalog;
        lock (_gate)
        {
            if (_catalog != null) return _catalog;
            _catalog = new KillXpMobCatalog
            {
                PaksDir   = SteamLocator.FindVanillaPaksDir(),
                AesKey    = WindroseGameSecrets.AesKey,
                UsmapPath = UsmapLocator.Find(repoRoot),
                Log       = msg => Console.WriteLine("[kill-xp/catalog] " + msg),
            };
        }
        return _catalog;
    }
}

sealed class KillXpKeywordDto
{
    public string keyword;             // DLL config key (substring of the pawn UClass name)
    public string label;               // human-ish display label, derived from the class name
    public string category;            // Wildlife / Undead / Blackbeard / Crew / Senkamati / Giant / Quest / Other
    public int suggestedXp;            // hint only; the user picks the actual value (0 = follow default)
    public List<string> matchesPawns;  // killable pawn classes this keyword's substring hits (tooltip)
}
