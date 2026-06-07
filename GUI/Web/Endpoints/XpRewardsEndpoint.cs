using System;
using System.Linq;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Windrose.Quartermaster.Core;

namespace Windrose.Quartermaster.Web.Endpoints;

public static class XpRewardsEndpoint
{
    public static void Map(WebApplication app, string repoRoot)
    {
        var paths = WindrosePaths.FromModRoot(repoRoot);

        // Catalog of per-entry override targets for the XP Reward tab. Each row is a
        // quest / POI-chest reward DataAsset with a non-zero vanilla ExperienceCount
        // (XP=0 entries - quest "Core" containers etc. - are dropped: scaling 0 is a
        // no-op and would only clutter the list). The vanilla tree is extracted on a
        // cache miss so a fresh checkout works without a full re-dump.
        app.MapGet("/api/xp-rewards/catalog", () =>
        {
            try
            {
                var ext = new VanillaConfigExtractor(paths);
                ext.EnsureDirectory(paths.VanillaQuestRewards, WindroseGameSecrets.QuestRewardsPath);

                var catalog = new XpRewardPatcher().BuildCatalog(paths.VanillaQuestRewards);
                var dto = catalog
                    .Where(c => c.VanillaXp > 0)
                    .Select(c => new
                    {
                        stem = c.Stem,
                        isPoi = c.IsPoi,
                        category = c.TopCategory,
                        group = c.Group,
                        displayName = c.DisplayName,
                        vanillaXp = c.VanillaXp,
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
