using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Windrose.StackSize.Gui.Endpoints;

namespace Windrose.StackSize.Gui;

public static class Program
{
    public static int Main(string[] args)
    {
        // Headless CLI paths -- bypass the WebApplication entirely.
        //   --test-patcher       : smoke-test the StackPatcher / BuildPipeline
        //                          against the legacy PowerShell pipeline.
        //   --test-loot-patcher  : smoke-test the LootPatcher; round-trip
        //                          against MoreEnemyResources_2x_P.pak.
        //   --setup [--force]    : run the dump + icon extraction pipeline
        //                          (replaces the old Dump-WindroseVanilla.ps1 +
        //                          Extract-Icons.ps1 wrappers).
        if (args.Length > 0 && (args[0] == "--test-patcher" || args[0] == "--test-loot-patcher" || args[0] == "--setup"))
        {
            var repoRootCli = Path.GetFullPath(
                Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
            if (args[0] == "--setup")
                return PatcherCli.RunSetup(args, repoRootCli);
            return PatcherCli.Run(args, repoRootCli);
        }

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
        builder.WebHost.UseUrls("http://localhost:17777");

        var app = builder.Build();

        var repoRoot = Path.GetFullPath(Path.Combine(app.Environment.ContentRootPath, ".."));
        var iconsDir = Path.Combine(repoRoot, "Icons");

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

        ItemsEndpoint.Map(app, repoRoot);
        ProfilesEndpoint.Map(app, repoRoot);
        BuildEndpoint.Map(app, repoRoot);
        SetupEndpoint.Map(app, repoRoot);

        app.Run();
        return 0;
    }
}
