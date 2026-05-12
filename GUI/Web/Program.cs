using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
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
        // Headless CLI paths - bypass the WebApplication entirely.
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
            // Use the same data-root resolver as the WebApplication path:
            // dev runs (`dotnet run --project GUI/Web`) walk up to the repo,
            // deployed/copied EXEs land at <exe-dir>\QuartermasterData\.
            var (cliRoot, _) = ResolveDataRoot();
            if (args[0] == "--setup")
                return PatcherCli.RunSetup(args, cliRoot);
            return PatcherCli.Run(args, cliRoot);
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
    /// <param name="dataRoot">
    /// Explicit data root to override the auto-resolver. The default
    /// (<see cref="ResolveDataRoot"/>) walks up from <c>AppContext.BaseDirectory</c>
    /// looking for a <c>Tools\QuartermasterCore\QuartermasterCore.csproj</c>
    /// marker (= dev / repo run) and falls back to
    /// <c>&lt;exe-dir&gt;\QuartermasterData\</c> for a deployed EXE that's
    /// been copied somewhere outside the source tree - so the data folder
    /// travels with the EXE (USB-stick portable).
    /// </param>
    public static WebApplication CreateWebApp(string[] args, string url, string dataRoot = "")
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

        var (resolvedRoot, isDeployed) = !string.IsNullOrEmpty(dataRoot)
            ? (Path.GetFullPath(dataRoot), !LooksLikeDevRepo(dataRoot))
            : ResolveDataRoot();

        // Make sure the data layout exists. Dev/repo runs already have these;
        // deployed runs need them created on first launch in QuartermasterData/.
        Directory.CreateDirectory(resolvedRoot);
        var iconsDir = Path.Combine(resolvedRoot, "Icons");
        Directory.CreateDirectory(iconsDir);
        Directory.CreateDirectory(Path.Combine(resolvedRoot, "Profiles"));

        // Deployed EXE: seed the embedded UE5 *.usmap so setup works without
        // the user first dumping one via UE4SS. Only seed when none is
        // present so a user-supplied newer dump (e.g. after a game update)
        // wins. Dev mode skips this: the on-disk file is the source of
        // truth and you're probably editing it.
        //
        // (The IconExtractor used to need a similar seed step - we extracted
        // a prebuilt publish/ zip into <DataRoot>\Tools\IconExtractor\ on
        // first run. It's now linked in-process via QuartermasterCore so
        // there's no sibling EXE to seed; the runtime libraries travel
        // inside this assembly.)
        if (isDeployed)
        {
            SeedUsmapIfMissing(resolvedRoot);
        }

        // Static files: prefer the on-disk wwwroot if it sits next to the
        // ContentRoot (= dev run via `dotnet run --project GUI/Web`, where
        // editing app.css/app.js shows up immediately on refresh). Otherwise
        // fall back to the manifest embedded provider so the single-file EXE
        // serves the frontend straight from its own assembly.
        var diskWebRoot = Path.Combine(app.Environment.ContentRootPath, "wwwroot");
        IFileProvider webFileProvider;
        if (Directory.Exists(diskWebRoot) && File.Exists(Path.Combine(diskWebRoot, "index.html")))
        {
            webFileProvider = new PhysicalFileProvider(diskWebRoot);
        }
        else
        {
            webFileProvider = new ManifestEmbeddedFileProvider(
                typeof(Program).Assembly, "/wwwroot");
        }
        app.UseDefaultFiles(new DefaultFilesOptions { FileProvider = webFileProvider });
        app.UseStaticFiles(new StaticFileOptions { FileProvider = webFileProvider });

        // Icons live outside wwwroot (they're produced by the in-process
        // IconExtractor library into the data root's Icons/ folder). Mount
        // them at /Icons/* so the frontend can reference them directly
        // without us proxying.
        app.UseStaticFiles(new StaticFileOptions
        {
            FileProvider = new PhysicalFileProvider(iconsDir),
            RequestPath = "/Icons"
        });

        ItemsEndpoint.Map(app, resolvedRoot);
        LootTablesEndpoint.Map(app, resolvedRoot);
        BuyersEndpoint.Map(app, resolvedRoot);
        SellersEndpoint.Map(app, resolvedRoot);
        ProfilesEndpoint.Map(app, resolvedRoot);
        BuildEndpoint.Map(app, resolvedRoot);
        SetupEndpoint.Map(app, resolvedRoot);
        ModsEndpoint.Map(app, resolvedRoot);

        return app;
    }

    /// <summary>
    /// Resolves the data root for runtime files (Profiles, Sources, Icons,
    /// Tools, Builds). Walks up from <see cref="AppContext.BaseDirectory"/>
    /// looking for a <c>Tools\QuartermasterCore\QuartermasterCore.csproj</c>
    /// marker - if found, that's a dev/repo run and we use it directly.
    /// Otherwise the EXE has been deployed somewhere outside its source
    /// tree, and we route reads/writes to a sibling folder
    /// <c>QuartermasterData\</c> next to the EXE - so the data travels
    /// with the EXE (USB-stick portable).
    /// </summary>
    /// <remarks>
    /// We seed the walk from <see cref="AppContext.BaseDirectory"/> rather
    /// than <see cref="Environment.CurrentDirectory"/> / ContentRootPath on
    /// purpose: the latter depends on whoever invoked the EXE (e.g. starting
    /// from a shell that happens to live inside the repo would give a false
    /// positive on the marker). BaseDirectory is the actual binary location
    /// - for a single-file EXE this is the launch directory (where the
    /// .exe physically sits), not the self-extract temp dir, which is
    /// exactly where we want <c>QuartermasterData\</c> to live.
    /// </remarks>
    /// <returns>
    /// (<c>path</c>, <c>isDeployed</c>) - callers use the second flag to
    /// gate "seed-from-embedded" behavior so dev edits aren't clobbered.
    /// </returns>
    public static (string Path, bool IsDeployed) ResolveDataRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        for (int i = 0; i < 10 && current is not null; i++)
        {
            if (LooksLikeDevRepo(current.FullName))
                return (current.FullName, false);
            current = current.Parent;
        }
        var deployed = Path.Combine(AppContext.BaseDirectory, "QuartermasterData");
        return (deployed, true);
    }

    static bool LooksLikeDevRepo(string root)
    {
        // Marker file that only exists in the source tree (not in the
        // deployed EXE bundle nor in <DataRoot>\QuartermasterData\).
        return File.Exists(Path.Combine(root, "Tools", "QuartermasterCore",
                                              "QuartermasterCore.csproj"));
    }

    /// <summary>
    /// Writes the embedded UE5 mappings file (<c>Usmap.*.usmap</c> resource)
    /// into <paramref name="dataRoot"/>, but only if no <c>*.usmap</c> is
    /// already there. Newer dumps - e.g. one the user grabbed via UE4SS
    /// Ctrl+Num6 after a game update - are preserved (and win in
    /// <see cref="UsmapLocator"/> by mtime). The embedded copy is a
    /// "good-enough default" so a fresh EXE drop can run setup without
    /// any external prerequisites.
    /// </summary>
    static void SeedUsmapIfMissing(string dataRoot)
    {
        // Bail if any *.usmap is already at the data root - user-supplied
        // dumps take precedence over our embedded fallback.
        if (Directory.EnumerateFiles(dataRoot, "*.usmap", SearchOption.TopDirectoryOnly).Any())
            return;

        var asm = typeof(Program).Assembly;
        const string prefix = "Usmap.";
        var resourceName = asm.GetManifestResourceNames()
            .FirstOrDefault(n => n.StartsWith(prefix, StringComparison.Ordinal)
                              && n.EndsWith(".usmap", StringComparison.OrdinalIgnoreCase));
        if (resourceName == null) return;

        // Resource name shape: "Usmap.<filename>.usmap" - strip prefix
        // for the on-disk name so an updated EXE can ship a different
        // dump and the user sees the version-tagged filename.
        var filename = resourceName.Substring(prefix.Length);
        var targetPath = Path.Combine(dataRoot, filename);
        using var src = asm.GetManifestResourceStream(resourceName);
        if (src == null) return;
        using var dst = File.Create(targetPath);
        src.CopyTo(dst);
    }
}
