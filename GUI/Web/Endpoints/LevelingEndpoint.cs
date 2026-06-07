using System;
using System.Linq;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Windrose.Quartermaster.Core;

namespace Windrose.Quartermaster.Web.Endpoints;

public static class LevelingEndpoint
{
    public static void Map(WebApplication app, string repoRoot)
    {
        var paths = WindrosePaths.FromModRoot(repoRoot);

        // Per-level vanilla reward table for the Level Rewards tab. Each row is one
        // hero level with its Exp threshold and vanilla talent/stat rewards. The
        // front-end computes effective values (hybrid multiplier + per-level
        // overrides) client-side, so this payload is multiplier-independent. The
        // single vanilla DA_HeroLevels asset is extracted on a cache miss so a fresh
        // checkout works without a full re-dump.
        app.MapGet("/api/leveling/catalog", () =>
        {
            try
            {
                new VanillaConfigExtractor(paths).EnsureHeroLevels();
                var preview = new LevelingPatcher().BuildPreview(paths.VanillaHeroLevels, null);
                var dto = preview.Select(p => new
                {
                    level = p.Level,
                    exp = p.Exp,
                    vanillaTalent = p.VanillaTalent,
                    vanillaStat = p.VanillaStat,
                    // Level 1 is the starting row: a reward here crashes the game, so
                    // the UI locks it. Guard both the level-1 index and Exp==0.
                    isStarting = p.Exp == 0 || p.Level == 1,
                });
                return Results.Json(dto);
            }
            catch (Exception ex)
            {
                return Results.Json(new { error = ex.Message }, statusCode: 500);
            }
        });
    }
}
