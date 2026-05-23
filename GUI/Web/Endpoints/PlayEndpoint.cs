using System;
using System.Diagnostics;
using System.IO;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Windrose.Quartermaster.Core;

namespace Windrose.Quartermaster.Web.Endpoints;

// POST /api/play   no body
//
// Launches the Windrose top-level Windrose.exe via the OS shell. We deliberately
// go through the top-level launcher (sibling of R5/, Engine/) and not the
// shipping EXE under R5/Binaries/Win64/ because the top-level binary is the
// one Steam knows about - launching it through ShellExecute lets Steam pick up
// the process (overlay, friends, achievements), and crash-handling/DRM glue
// all wire up correctly. The shipping EXE works too but is "outside Steam"
// from the platform's point of view.
//
// No profile context, no build pipeline interaction - this is a thin wrapper
// around Process.Start. Returns immediately after spawning; the game continues
// to run independently of Quartermaster.
public static class PlayEndpoint
{
    public static void Map(WebApplication app, string repoRoot)
    {
        app.MapPost("/api/play", () =>
        {
            string binariesDir;
            try
            {
                binariesDir = SteamLocator.FindBinariesWin64Dir();
            }
            catch (Exception ex)
            {
                return Results.Json(new
                {
                    success = false,
                    error = "Could not locate Windrose install: " + ex.Message,
                }, statusCode: StatusCodes.Status404NotFound);
            }

            var r5Dir = Path.GetDirectoryName(Path.GetDirectoryName(binariesDir));
            var gameRoot = !string.IsNullOrEmpty(r5Dir) ? Path.GetDirectoryName(r5Dir) : null;
            if (string.IsNullOrEmpty(gameRoot))
            {
                return Results.Json(new
                {
                    success = false,
                    error = "Could not derive game root from binaries dir: " + binariesDir,
                }, statusCode: StatusCodes.Status500InternalServerError);
            }

            var exePath = Path.Combine(gameRoot, "Windrose.exe");
            if (!File.Exists(exePath))
            {
                return Results.Json(new
                {
                    success = false,
                    error = "Windrose.exe not found at expected location: " + exePath,
                }, statusCode: StatusCodes.Status404NotFound);
            }

            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = exePath,
                    WorkingDirectory = gameRoot,
                    UseShellExecute = true,
                };
                using var proc = Process.Start(psi);
                var pid = proc?.Id ?? 0;

                return Results.Ok(new
                {
                    success = true,
                    exePath,
                    pid,
                });
            }
            catch (Exception ex)
            {
                return Results.Json(new
                {
                    success = false,
                    error = "Failed to launch Windrose: " + ex.Message,
                }, statusCode: StatusCodes.Status500InternalServerError);
            }
        });
    }
}
