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
        // Headless smoke-test path: bypass the WebApplication entirely.
        // Used during Phase 1-3 development to verify the StackPatcher /
        // BuildPipeline against the legacy PowerShell pipeline. Will stay
        // available as a CLI for headless / CI builds.
        if (args.Length > 0 && args[0] == "--test-patcher")
        {
            var repoRootCli = Path.GetFullPath(
                Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
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
        // frontend can reference them directly without us proxying.
        if (Directory.Exists(iconsDir))
        {
            app.UseStaticFiles(new StaticFileOptions
            {
                FileProvider = new PhysicalFileProvider(iconsDir),
                RequestPath = "/Icons"
            });
        }

        ItemsEndpoint.Map(app, repoRoot);
        ProfilesEndpoint.Map(app, repoRoot);
        BuildEndpoint.Map(app, repoRoot);

        app.Run();
        return 0;
    }
}
