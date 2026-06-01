using System;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Windrose.Quartermaster.Core;

namespace Windrose.Quartermaster.Web.Endpoints;

public static class GameInstallEndpoint
{
    public static void Map(WebApplication app, string repoRoot)
    {
        app.MapGet("/api/game-install", () =>
        {
            return Results.Json(BuildStatus());
        });

        app.MapPost("/api/game-install", async (HttpContext ctx) =>
        {
            string body;
            using (var reader = new StreamReader(ctx.Request.Body))
            {
                body = await reader.ReadToEndAsync();
            }

            string gameRoot = null;
            try
            {
                using var doc = JsonDocument.Parse(body ?? "{}");
                if (doc.RootElement.ValueKind == JsonValueKind.Object &&
                    doc.RootElement.TryGetProperty("gameRoot", out var gr) &&
                    gr.ValueKind == JsonValueKind.String)
                {
                    gameRoot = gr.GetString();
                }
            }
            catch (Exception ex)
            {
                return Results.BadRequest(new { error = "Invalid JSON body: " + ex.Message });
            }

            if (string.IsNullOrWhiteSpace(gameRoot))
                return Results.BadRequest(new { error = "gameRoot must not be empty - use DELETE to clear the override." });

            var (ok, err) = GameInstallOverride.Validate(gameRoot);
            if (!ok)
                return Results.BadRequest(new { error = err });

            try
            {
                GameInstallOverride.SaveGameRoot(gameRoot);
            }
            catch (Exception ex)
            {
                return Results.Problem("Failed to persist game-install override: " + ex.Message, statusCode: 500);
            }

            return Results.Json(BuildStatus());
        });

        app.MapDelete("/api/game-install", () =>
        {
            try
            {
                GameInstallOverride.Clear();
            }
            catch (Exception ex)
            {
                return Results.Problem("Failed to clear game-install override: " + ex.Message, statusCode: 500);
            }
            return Results.Json(BuildStatus());
        });
    }

    static object BuildStatus()
    {
        var overrideGameRoot = GameInstallOverride.LoadGameRoot();
        var hasOverride = !string.IsNullOrEmpty(overrideGameRoot);

        (bool ok, string err) overrideValid = (false, null);
        if (hasOverride)
            overrideValid = GameInstallOverride.Validate(overrideGameRoot);

        // Candidate must contain a vanilla pak, not just a Windrose dir: leftover empty
        // dirs from a prior install would otherwise outrank the real install on a later library.
        string steamGameRoot = null;
        string steamError = null;
        try
        {
            var steam = SteamLocator.FindSteamInstallPath();
            if (!string.IsNullOrEmpty(steam))
            {
                foreach (var lib in SteamLocator.FindLibraryPaths(steam))
                {
                    var paksDir = Path.Combine(lib, "steamapps", "common",
                        "Windrose", "R5", "Content", "Paks");
                    if (!Directory.Exists(paksDir)) continue;
                    var hasVanilla = false;
                    foreach (var name in SteamLocator.VanillaPakNames)
                    {
                        if (File.Exists(Path.Combine(paksDir, name))) { hasVanilla = true; break; }
                    }
                    if (!hasVanilla) continue;
                    steamGameRoot = Path.GetFullPath(
                        Path.Combine(lib, "steamapps", "common", "Windrose"));
                    break;
                }
                if (string.IsNullOrEmpty(steamGameRoot))
                    steamError = "Steam is installed but no Windrose vanilla pak was found under any library's steamapps/common/Windrose/R5/Content/Paks/.";
            }
            else
            {
                steamError = "Steam install not detected.";
            }
        }
        catch (Exception ex)
        {
            steamError = ex.Message;
        }

        string effectiveGameRoot = null;
        string effectiveVanillaPak = null;
        string effectiveError = null;
        try
        {
            effectiveVanillaPak = SteamLocator.FindVanillaPak();
            var paksDir = Path.GetDirectoryName(effectiveVanillaPak);
            var contentDir = Path.GetDirectoryName(paksDir);
            var r5Dir = Path.GetDirectoryName(contentDir);
            effectiveGameRoot = Path.GetDirectoryName(r5Dir);
        }
        catch (Exception ex)
        {
            effectiveError = ex.Message;
        }

        return new
        {
            overrideSet = hasOverride,
            overrideGameRoot = overrideGameRoot,
            overrideValid = overrideValid.ok,
            overrideError = overrideValid.err,
            steamGameRoot = steamGameRoot,
            steamError = steamError,
            effectiveGameRoot = effectiveGameRoot,
            effectiveVanillaPak = effectiveVanillaPak,
            effectiveError = effectiveError,
            isResolved = !string.IsNullOrEmpty(effectiveVanillaPak),
        };
    }
}
