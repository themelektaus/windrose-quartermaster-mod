using System;
using System.Text;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Windrose.StackSize.Core;

namespace Windrose.StackSize.Gui.Endpoints;

// Two endpoints that wrap StackSizeCore.SetupRunner:
//
//   GET  /api/setup/status   ->  fast, returns SetupStatus as JSON. Used
//                                by the frontend on first load to decide
//                                whether to show the setup banner.
//
//   POST /api/setup/run      ->  Server-Sent Events stream. Runs the
//                                missing setup steps (or all of them if
//                                ?force=true is set). Each log line is
//                                emitted as an SSE event "log"; a final
//                                "done" event carries success/error.
//
// We funnel the synchronous SetupRunner.Log callback through a
// Channel<string> so the SSE writer can flush each line as it arrives
// without blocking the runner thread.
public static class SetupEndpoint
{
    // Mutex so concurrent /api/setup/run calls don't trample each other.
    // 0 == idle, 1 == running.
    static int _running;

    public static void Map(WebApplication app, string repoRoot)
    {
        var paths = WindrosePaths.FromModRoot(repoRoot);

        app.MapGet("/api/setup/status", () =>
        {
            var runner = new SetupRunner(paths);
            var status = runner.Probe();
            return Results.Json(new
            {
                isReady = status.IsReady,
                hasVanillaSources = status.HasVanillaSources,
                hasIcons = status.HasIcons,
                iconsDir = status.IconsDir,
                hasUsmap = status.HasUsmap,
                usmapPath = status.UsmapPath,
                hasRepak = status.HasRepak,
                hasIconExtractor = status.HasIconExtractor,
                hasVanillaPak = status.HasVanillaPak,
                vanillaPakPath = status.VanillaPakPath,
                vanillaPakError = status.VanillaPakError,
                isRunning = _running == 1,
            });
        });

        app.MapPost("/api/setup/run", async (HttpContext ctx) =>
        {
            // Single-flight guard so concurrent requests can't both kick
            // off dotnet publish + repak unpack at the same time.
            if (Interlocked.CompareExchange(ref _running, 1, 0) != 0)
            {
                ctx.Response.StatusCode = 409;
                await ctx.Response.WriteAsJsonAsync(new { error = "Setup is already running" });
                return;
            }

            ctx.Response.StatusCode = 200;
            ctx.Response.Headers.Append("Content-Type", "text/event-stream");
            ctx.Response.Headers.Append("Cache-Control", "no-cache");
            ctx.Response.Headers.Append("X-Accel-Buffering", "no"); // disable any reverse-proxy buffering

            var force = ctx.Request.Query.ContainsKey("force") &&
                        string.Equals(ctx.Request.Query["force"], "true", StringComparison.OrdinalIgnoreCase);

            // Bounded channel; back-pressure on the runner side is fine
            // because Console-style output is naturally bursty and the
            // browser drains it in milliseconds.
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
                            // Best-effort write -- if the channel is closed
                            // (e.g. client disconnected) we just drop the line.
                            // .NET 10's Channel WriteAsync from a sync callback
                            // would deadlock; TryWrite + spin works because
                            // the bounded capacity is huge relative to typical
                            // log volume.
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
                // Channel completed without exception == success.
                await runTask; // surface any non-channel exceptions
                await WriteSseEvent(ctx, "done", "{\"success\":true}");
            }
            catch (OperationCanceledException)
            {
                // Client disconnected -- nothing to send back.
            }
            catch (Exception ex)
            {
                var payload = "{\"success\":false,\"error\":" +
                              System.Text.Json.JsonEncodedText.Encode(ex.Message ?? "unknown error") + "}";
                try { await WriteSseEvent(ctx, "done", payload); }
                catch { /* connection may already be gone */ }
            }
            finally
            {
                Interlocked.Exchange(ref _running, 0);
            }
        });
    }

    // SSE message format:
    //   event: <name>
    //   data: <one-line payload>
    //   <blank line>
    // Payload may not contain newlines per spec; we strip CR/LF defensively.
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
