using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Windrose.Quartermaster.Core;

namespace Windrose.Quartermaster.Web.Endpoints;

public static class ExportEndpoint
{
    // 0 == idle, 1 == running.
    static int _running;

    public static void Map(WebApplication app, string repoRoot)
    {
        var paths = WindrosePaths.FromModRoot(repoRoot);

        app.MapGet("/api/export/status", () =>
        {
            var gameplayDir = Path.Combine(paths.Vanilla, "R5", "Content", "Gameplay", "Building");
            var environmentDir = Path.Combine(paths.Vanilla, "R5", "Content", "Environment", "Gameplay", "Building");
            var audioDir = Path.Combine(paths.Vanilla, "R5", "Content", "Audio", "Game", "Building");

            int gameplayCount = CountUassets(gameplayDir);
            int environmentCount = CountUassets(environmentDir);
            int audioCount = CountUassets(audioDir);

            return Results.Json(new
            {
                isRunning = _running == 1,
                outDir = paths.Vanilla,
                gameplay = new { path = gameplayDir, uassetCount = gameplayCount },
                environment = new { path = environmentDir, uassetCount = environmentCount },
                audio = new { path = audioDir, uassetCount = audioCount },
                totalUassetCount = gameplayCount + environmentCount + audioCount,
            });
        });

        app.MapPost("/api/export/building", async (HttpContext ctx) =>
        {
            if (Interlocked.CompareExchange(ref _running, 1, 0) != 0)
            {
                ctx.Response.StatusCode = 409;
                await ctx.Response.WriteAsJsonAsync(new { error = "Export is already running" });
                return;
            }

            ctx.Response.StatusCode = 200;
            ctx.Response.Headers.Append("Content-Type", "text/event-stream");
            ctx.Response.Headers.Append("Cache-Control", "no-cache");
            ctx.Response.Headers.Append("X-Accel-Buffering", "no");

            var channel = Channel.CreateBounded<string>(new BoundedChannelOptions(1024)
            {
                FullMode = BoundedChannelFullMode.Wait,
                SingleReader = true,
                SingleWriter = true,
            });

            var aborted = ctx.RequestAborted;
            BuildingItemExportResult result = null;
            var runTask = Task.Run(() =>
            {
                try
                {
                    var runner = new BuildingItemExportRunner(paths)
                    {
                        Log = msg =>
                        {
                            while (!channel.Writer.TryWrite(msg))
                            {
                                if (aborted.IsCancellationRequested) return;
                                Thread.Sleep(1);
                            }
                        },
                    };
                    result = runner.Run();
                    channel.Writer.TryComplete();
                }
                catch (Exception ex)
                {
                    channel.Writer.TryComplete(ex);
                }
            }, aborted);

            try
            {
                await foreach (var line in channel.Reader.ReadAllAsync(aborted))
                {
                    await WriteSseEvent(ctx, "log", line);
                }
                await runTask;
                var payload = result == null
                    ? "{\"success\":true}"
                    : "{\"success\":true," +
                      "\"filesMatched\":" + result.FilesMatched + "," +
                      "\"filesWritten\":" + result.FilesWritten + "," +
                      "\"filesSkippedExisting\":" + result.FilesSkippedExisting + "," +
                      "\"filesFailed\":" + result.FilesFailed + "," +
                      "\"totalBytesWritten\":" + result.TotalBytesWritten + "}";
                await WriteSseEvent(ctx, "done", payload);
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                var payload = "{\"success\":false,\"error\":" +
                              System.Text.Json.JsonEncodedText.Encode(ex.Message ?? "unknown error") + "}";
                try { await WriteSseEvent(ctx, "done", payload); }
                catch { }
            }
            finally
            {
                Interlocked.Exchange(ref _running, 0);
            }
        });
    }

    static int CountUassets(string dir)
    {
        if (!Directory.Exists(dir)) return 0;
        try
        {
            return Directory.EnumerateFiles(dir, "*.uasset", SearchOption.AllDirectories).Count();
        }
        catch
        {
            return 0;
        }
    }

    static async Task WriteSseEvent(HttpContext ctx, string eventName, string data)
    {
        var safe = (data ?? string.Empty).Replace('\r', ' ').Replace('\n', ' ');
        var sb = new StringBuilder(safe.Length + eventName.Length + 16);
        sb.Append("event: ").Append(eventName).Append('\n');
        sb.Append("data: ").Append(safe).Append('\n').Append('\n');
        await ctx.Response.WriteAsync(sb.ToString());
        await ctx.Response.Body.FlushAsync();
    }
}
