using System;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Windrose.Quartermaster.Core;

namespace Windrose.Quartermaster.Web.Endpoints;

// Endpoints for the user-configured game-install override - used when
// SteamLocator can't auto-detect the install (e.g. Epic Games, GOG,
// dedicated server, portable extraction, second copy on a non-Steam
// drive). The override layer is in GameInstallOverride; SteamLocator
// transparently consults it first so all build / deploy / report paths
// follow without per-call wiring.
//
//   GET    /api/game-install   ->  current override + Steam auto-detect
//                                  probe + validation. Used by the GUI to
//                                  pre-populate the "Configure" modal.
//
//   POST   /api/game-install   ->  body { "gameRoot": "..." }. Validates
//                                  that the folder contains at least one
//                                  R5\Binaries\Win64\Windrose*.exe plus a
//                                  vanilla pak before persisting. 400 with
//                                  details on validation failure.
//
//   DELETE /api/game-install   ->  clears the override (= back to Steam
//                                  auto-detect).
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

    // Probes both the persisted override and Steam auto-detect (without
    // throwing) so the frontend modal can show the user what's available
    // before they commit to a path. The vanillaPakPath field reflects
    // whichever source actually resolves - override beats Steam.
    static object BuildStatus()
    {
        var overrideGameRoot = GameInstallOverride.LoadGameRoot();
        var hasOverride = !string.IsNullOrEmpty(overrideGameRoot);

        (bool ok, string err) overrideValid = (false, null);
        if (hasOverride)
            overrideValid = GameInstallOverride.Validate(overrideGameRoot);

        // Steam-side probe without throwing - we want to suggest the
        // auto-detected path in the modal even if the user has a stale
        // override that's currently invalid.
        //
        // Important: we require the candidate folder to actually contain a
        // vanilla pak under R5\Content\Paks\, not just an existing Windrose
        // directory. Steam library leftovers from a previous install (or a
        // partially-uninstalled copy on a different drive) can leave an empty
        // Windrose folder behind that would otherwise outrank the real
        // install on a later library and produce a divergent suggestion in
        // the modal vs. the resolved effectiveGameRoot below.
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

        // What's the effective resolved pak? Override wins if valid, else
        // Steam autodetect attempt (which may itself fail).
        string effectiveGameRoot = null;
        string effectiveVanillaPak = null;
        string effectiveError = null;
        try
        {
            effectiveVanillaPak = SteamLocator.FindVanillaPak();
            // Derive gameRoot from the resolved pak: <gameRoot>/R5/Content/Paks/<name>
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
