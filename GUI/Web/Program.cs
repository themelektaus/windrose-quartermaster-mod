using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Windrose.Quartermaster.Web.Endpoints;

namespace Windrose.Quartermaster.Web;

public static class Program
{
    public static int Main(string[] args)
    {
        // Headless CLI paths -- bypass the WebApplication entirely.
        //   --test-patcher       : smoke-test the StackPatcher / BuildPipeline
        //                          against the legacy PowerShell pipeline.
        //   --test-loot-patcher  : smoke-test the LootPatcher by writing a
        //                          single-bucket multiplier patch into
        //                          .build-tmp/loot_smoke_<...>/.
        //   --setup [--force]    : run the dump + icon extraction pipeline
        //                          (replaces the old Dump-WindroseVanilla.ps1 +
        //                          Extract-Icons.ps1 wrappers).
        if (args.Length > 0 && (args[0] == "--test-patcher" || args[0] == "--test-loot-patcher" || args[0] == "--setup"))
        {
            // AppContext.BaseDirectory = GUI/Web/bin/<cfg>/<tfm>/ -- five ups
            // gets us to the repo root.
            var repoRootCli = Path.GetFullPath(
                Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
            if (args[0] == "--setup")
                return PatcherCli.RunSetup(args, repoRootCli);
            return PatcherCli.Run(args, repoRootCli);
        }

        var app = CreateWebApp(args, "http://localhost:17777");
        app.Run();
        return 0;
    }

    /// <summary>
    /// Build the configurator's <see cref="WebApplication"/>. Used by both the
    /// CLI entry point above (which passes the fixed <c>17777</c> URL and runs
    /// blocking via <c>app.Run()</c>) and by the WPF wrapper (which passes
    /// <c>http://127.0.0.1:0</c> for a dynamic port and starts the app via
    /// <c>app.StartAsync()</c> in-process behind a WebView2).
    /// </summary>
    /// <param name="repoRoot">
    /// Explicit repo root for the wrapper to override the
    /// <c>ContentRoot/..</c> default. The default convention works for
    /// <c>dotnet run --project GUI</c> (ContentRoot = GUI/, parent = repo)
    /// but breaks for the WPF wrapper whose ContentRoot is its bin directory.
    /// </param>
    public static WebApplication CreateWebApp(string[] args, string url, string repoRoot = "")
    {
        var builder = WebApplication.CreateBuilder(args);
        builder.Services.Configure<JsonOptions>(opts =>
        {
            // Keep the wire-format consistent with the on-disk profile files
            // (camelCase, fields-as-properties, drop nulls).
            opts.SerializerOptions.IncludeFields = true;
            opts.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
            opts.SerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
            opts.SerializerOptions.WriteIndented = false;
        });
        builder.WebHost.UseUrls(url);

        var app = builder.Build();

        var resolvedRoot = !string.IsNullOrEmpty(repoRoot)
            ? repoRoot
            : Path.GetFullPath(Path.Combine(app.Environment.ContentRootPath, "../.."));
        var iconsDir = Path.Combine(resolvedRoot, "Icons");

        app.UseDefaultFiles();
        app.UseStaticFiles();

        // Icons live outside wwwroot (they're produced by IconExtractor.exe
        // into the repo's Icons/ folder). Mount them at /Icons/* so the
        // frontend can reference them directly without us proxying. We
        // create the folder upfront so the first-run setup can populate
        // it without restarting the GUI.
        Directory.CreateDirectory(iconsDir);
        app.UseStaticFiles(new StaticFileOptions
        {
            FileProvider = new PhysicalFileProvider(iconsDir),
            RequestPath = "/Icons"
        });

        ItemsEndpoint.Map(app, resolvedRoot);
        LootTablesEndpoint.Map(app, resolvedRoot);
        ProfilesEndpoint.Map(app, resolvedRoot);
        BuildEndpoint.Map(app, resolvedRoot);
        SetupEndpoint.Map(app, resolvedRoot);

        return app;
    }
}
