using System.Linq;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Windrose.Quartermaster.Core;

namespace Windrose.Quartermaster.Web.Endpoints;

// Serves the static "Deposit visuals" catalog (deposit targets + selectable albedo
// textures + per-deposit defaults) so the Misc-tab card can populate its dropdowns.
public static class DepositVisualEndpoint
{
    public static void Map(WebApplication app, string repoRoot)
    {
        app.MapGet("/api/deposit-visual/catalog", () =>
        {
            var deposits = DepositVisualCatalog.Deposits.Select(d => new
            {
                key = d.Key,
                label = d.Label,
                defaultTexture = d.DefaultTextureKey,
                vanillaTexture = d.VanillaTextureKey,
            });
            var textures = DepositVisualCatalog.Textures.Select(t => new
            {
                key = t.Key,
                label = t.Label,
            });
            return Results.Json(new { deposits, textures });
        });
    }
}
