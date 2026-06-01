using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Windrose.Quartermaster.Core;

namespace Windrose.Quartermaster.Web.Endpoints;

public static class SetupEndpoint
{
    // 0 == idle, 1 == running.
    static int _running;

    public static void Map(WebApplication app, string repoRoot)
    {
        var paths = WindrosePaths.FromModRoot(repoRoot);

        app.MapGet("/api/setup/status", () =>
        {
            var runner = new SetupRunner(paths);
            var status = runner.Probe();
            var sources = (status.Sources ?? new List<VanillaSourceStatus>())
                .Select(s => new
                {
                    key = s.Key,
                    label = s.Label,
                    description = s.Description,
                    diskPath = s.DiskPath,
                    ok = s.Ok,
                })
                .ToArray();
            return Results.Json(new
            {
                isReady = status.IsReady,
                hasVanillaSources = status.HasVanillaSources,
                sources = sources,
                hasIcons = status.HasIcons,
                iconsDir = status.IconsDir,
                hasUsmap = status.HasUsmap,
                usmapPath = status.UsmapPath,
                hasRepak = status.HasRepak,
                hasIconExtractor = status.HasIconExtractor,
                hasVanillaPak = status.HasVanillaPak,
                vanillaPakPath = status.VanillaPakPath,
                vanillaPakError = status.VanillaPakError,
                hasFfmpeg = status.HasFfmpeg,
                ffmpegPath = status.FfmpegPath,
                isRunning = _running == 1,
            });
        });

        app.MapPost("/api/setup/run", async (HttpContext ctx) =>
        {
            if (Interlocked.CompareExchange(ref _running, 1, 0) != 0)
            {
                ctx.Response.StatusCode = 409;
                await ctx.Response.WriteAsJsonAsync(new { error = "Setup is already running" });
                return;
            }

            ctx.Response.StatusCode = 200;
            ctx.Response.Headers.Append("Content-Type", "text/event-stream");
            ctx.Response.Headers.Append("Cache-Control", "no-cache");
            ctx.Response.Headers.Append("X-Accel-Buffering", "no");

            var force = ctx.Request.Query.ContainsKey("force") &&
                        string.Equals(ctx.Request.Query["force"], "true", StringComparison.OrdinalIgnoreCase);

            var channel = Channel.CreateBounded<string>(new BoundedChannelOptions(1024)
            {
                FullMode = BoundedChannelFullMode.Wait,
                SingleReader = true,
                SingleWriter = true,
            });

            var aborted = ctx.RequestAborted;
            var runTask = Task.Run(() =>
            {
                try
                {
                    var runner = new SetupRunner(paths)
                    {
                        ForceAll = force,
                        Log = msg =>
                        {
                            // Sync callback: async WriteAsync here would deadlock, so spin on TryWrite.
                            while (!channel.Writer.TryWrite(msg))
                            {
                                if (aborted.IsCancellationRequested) return;
                                Thread.Sleep(1);
                            }
                        },
                    };
                    runner.Run();
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
                await WriteSseEvent(ctx, "done", "{\"success\":true}");
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

    // SSE payload may not contain newlines; CR/LF are stripped.
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
