using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
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
        if (isDeployed)
        {
            SeedUsmapIfMissing(resolvedRoot);
            SeedIconExtractorIfMissing(Path.Combine(resolvedRoot, "Tools", "IconExtractor"));
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

        // Icons live outside wwwroot (they're produced by IconExtractor.exe
        // into the data root's Icons/ folder). Mount them at /Icons/* so the
        // frontend can reference them directly without us proxying.
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
    /// Extracts the embedded <c>IconExtractor.publish.zip</c> resource into
    /// <paramref name="iconExtractorDir"/><c>\publish\</c> when no
    /// <c>IconExtractor.exe</c> is there yet, OR when the existing one is a
    /// stale build against an older .NET runtime (TFM mismatch with
    /// <see cref="Windrose.Quartermaster.Core.IconExtractorBuilder.ExpectedTfm"/>).
    /// Mirrors what
    /// <see cref="Windrose.Quartermaster.Core.IconExtractorBuilder"/> does
    /// for dev builds (where it runs <c>dotnet publish</c> against the
    /// CUE4Parse submodule on demand) - the deployed EXE skips the build
    /// step entirely because the publish output was baked into the
    /// assembly at <c>dotnet publish</c> time.
    /// <para>
    /// Game-update story: if a future CUE4Parse / IconExtractor change
    /// requires a new tool build, the user just pulls a fresh EXE; the
    /// older extracted files at <c>publish\</c> are overwritten by the
    /// next start (we delete the existing <c>publish\</c> dir first).
    /// </para>
    /// <para>
    /// Cross-update bug fixed here: when an older deployed EXE had already
    /// extracted a net8.0 build into <c>publish\</c>, a newer EXE with a
    /// net10.0 embedded zip used to short-circuit on the existing
    /// <c>IconExtractor.exe</c> and never re-seed. The freshness probe
    /// closes that gap so the deployed-EXE path doesn't depend on the
    /// from-source rebuild fallback (which fails in a real deployment
    /// because <c>Tools/IconExtractor/IconExtractor.csproj</c> isn't
    /// shipped alongside the EXE).
    /// </para>
    /// </summary>
    static void SeedIconExtractorIfMissing(string iconExtractorDir)
    {
        var asm = typeof(Program).Assembly;
        const string resourceName = "IconExtractor.publish.zip";
        var publishDir = Path.Combine(iconExtractorDir, "publish");
        var exePath = Path.Combine(publishDir, "IconExtractor.exe");

        // Already extracted AND built against the expected runtime - nothing
        // to do. The IconExtractorBuilder will pick the existing exe up via
        // its short-circuit path. If it exists but the TFM marker says it
        // was built against a different runtime, fall through so we re-seed
        // from the (presumably-fresher) embedded zip.
        if (File.Exists(exePath) &&
            Windrose.Quartermaster.Core.IconExtractorBuilder.IsPublishFresh(publishDir))
        {
            return;
        }

        using var src = asm.GetManifestResourceStream(resourceName);
        if (src == null)
        {
            // Embedded resource missing - happens for plain `dotnet build`
            // outputs where the pre-publish target didn't run. The
            // IconExtractorBuilder will fall back to building from source
            // (works in dev runs since the Tools/IconExtractor/ folder is
            // there). Deployed EXEs always have the resource because
            // PublishIconExtractorForEmbed is gated on _IsPublishing.
            return;
        }

        Directory.CreateDirectory(iconExtractorDir);
        // Wipe any partial / stale publish dir from a prior version of the
        // EXE so we never end up with mixed-version DLLs (different SkiaSharp
        // build, etc.). Safe because publish/ is a derived artifact - nothing
        // user-authored lives there.
        if (Directory.Exists(publishDir))
        {
            try { Directory.Delete(publishDir, recursive: true); }
            catch { /* best-effort - ZipArchive.ExtractToDirectory will fail
                       loudly below if it really can't replace the files. */ }
        }
        Directory.CreateDirectory(publishDir);

        using var zip = new ZipArchive(src, ZipArchiveMode.Read);
        zip.ExtractToDirectory(publishDir, overwriteFiles: true);
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
