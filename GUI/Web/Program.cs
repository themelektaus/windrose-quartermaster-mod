using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
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
        if (args.Length > 0 && (args[0] == "--test-patcher" || args[0] == "--test-loot-patcher" || args[0] == "--setup"))
        {
            var (cliRoot, _) = ResolveDataRoot();
            if (args[0] == "--setup")
                return PatcherCli.RunSetup(args, cliRoot);
            return PatcherCli.Run(args, cliRoot);
        }

        TerminatePriorInstances();

        var app = CreateWebApp(args, "http://localhost:17777");

        if (OperatingSystem.IsLinux())
        {
            app.Lifetime.ApplicationStarted.Register(() =>
            {
                try
                {
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = "xdg-open",
                        Arguments = "http://localhost:17777",
                        UseShellExecute = false
                    });
                }
                catch { }
            });
        }

        app.Run();
        return 0;
    }

    public static WebApplication CreateWebApp(string[] args, string url, string dataRoot = "")
    {
        var builder = WebApplication.CreateBuilder(args);
        builder.Services.Configure<JsonOptions>(opts =>
        {
            opts.SerializerOptions.IncludeFields = true;
            opts.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
            opts.SerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
            opts.SerializerOptions.WriteIndented = false;
        });

        const long uploadCeiling = 200L * 1024 * 1024;
        builder.WebHost.ConfigureKestrel(opts =>
        {
            opts.Limits.MaxRequestBodySize = uploadCeiling;
        });
        builder.Services.Configure<Microsoft.AspNetCore.Http.Features.FormOptions>(opts =>
        {
            opts.MultipartBodyLengthLimit = uploadCeiling;
        });

        builder.WebHost.UseUrls(url);

        var app = builder.Build();

        var (resolvedRoot, isDeployed) = !string.IsNullOrEmpty(dataRoot)
            ? (Path.GetFullPath(dataRoot), !LooksLikeDevRepo(dataRoot))
            : ResolveDataRoot();

        Directory.CreateDirectory(resolvedRoot);
        var iconsDir = Path.Combine(resolvedRoot, "Icons");
        Directory.CreateDirectory(iconsDir);
        Directory.CreateDirectory(Path.Combine(resolvedRoot, "Profiles"));

        Windrose.Quartermaster.Core.WindrosePaths.ConfigureNativeDllDir(resolvedRoot);

        // Pre-load the RocksDB native via the standard .NET resolver so the save
        // patchers work in a PublishSingleFile build (RocksDbSharp's own loader
        // can't see the self-extracted native -> /api/savegame/characters|ships
        // would silently return empty). No-op in dev. See RocksDbNativeLoader.
        Windrose.Quartermaster.Core.RocksDbNativeLoader.EnsurePreloaded();

        Windrose.Quartermaster.Core.GameInstallOverride.ConfigureDataRoot(resolvedRoot);

        if (isDeployed)
        {
            SeedUsmapIfMissing(resolvedRoot);
            SyncDxgiDllFromEmbedded(resolvedRoot);
            SyncBinkAudioEncFromEmbedded(resolvedRoot);
            SeedTemplatesIfMissing(resolvedRoot);
        }

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

        app.UseStaticFiles(new StaticFileOptions
        {
            FileProvider = new PhysicalFileProvider(iconsDir),
            RequestPath = "/Icons"
        });

        ItemsEndpoint.Map(app, resolvedRoot);
        ItemTemplatesEndpoint.Map(app, resolvedRoot);
        BuildingTemplatesEndpoint.Map(app, resolvedRoot);
        BuildingsEndpoint.Map(app, resolvedRoot);
        VanillaMaterialsEndpoint.Map(app, resolvedRoot);
        VanillaResourcesEndpoint.Map(app, resolvedRoot);
        LootTablesEndpoint.Map(app, resolvedRoot);
        BuyersEndpoint.Map(app, resolvedRoot);
        SellersEndpoint.Map(app, resolvedRoot);
        ProfilesEndpoint.Map(app, resolvedRoot);
        BuildEndpoint.Map(app, resolvedRoot);
        SetupEndpoint.Map(app, resolvedRoot);
        GameInstallEndpoint.Map(app, resolvedRoot);
        ModsEndpoint.Map(app, resolvedRoot);
        ExportEndpoint.Map(app, resolvedRoot);
        ReportEndpoint.Map(app, resolvedRoot);
        PlayEndpoint.Map(app, resolvedRoot);
        SavegameEndpoint.Map(app, resolvedRoot);
        UiScaleEndpoint.Map(app, resolvedRoot);

        app.MapPost("/api/shutdown", (Microsoft.Extensions.Hosting.IHostApplicationLifetime lifetime) =>
        {
            lifetime.StopApplication();
            return Microsoft.AspNetCore.Http.Results.Ok();
        });

        return app;
    }

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
        return Windrose.Quartermaster.Core.WindrosePaths.IsDevRepoRoot(root);
    }

    static void TerminatePriorInstances()
    {
        var self = System.Diagnostics.Process.GetCurrentProcess();
        var name = self.ProcessName;
        if (string.IsNullOrEmpty(name)) return;

        var killedAny = false;
        foreach (var p in System.Diagnostics.Process.GetProcessesByName(name))
        {
            try
            {
                if (p.Id == self.Id) continue;
                p.Kill(entireProcessTree: true);
                p.WaitForExit(3000);
                killedAny = true;
            }
            catch
            {
            }
            finally
            {
                p.Dispose();
            }
        }

        if (killedAny)
        {
            // Let the kernel release the listening socket before we rebind.
            System.Threading.Thread.Sleep(300);
        }
    }

    static void SeedUsmapIfMissing(string dataRoot)
    {
        // A user-supplied dump takes precedence over the embedded fallback.
        if (Directory.EnumerateFiles(dataRoot, "*.usmap", SearchOption.TopDirectoryOnly).Any())
            return;

        var asm = typeof(Program).Assembly;
        const string prefix = "Usmap.";
        var resourceName = asm.GetManifestResourceNames()
            .FirstOrDefault(n => n.StartsWith(prefix, StringComparison.Ordinal)
                              && n.EndsWith(".usmap", StringComparison.OrdinalIgnoreCase));
        if (resourceName == null) return;

        var filename = resourceName.Substring(prefix.Length);
        var targetPath = Path.Combine(dataRoot, filename);
        using var src = asm.GetManifestResourceStream(resourceName);
        if (src == null) return;
        using var dst = File.Create(targetPath);
        src.CopyTo(dst);
    }

    // The stamp records the embedded resource hash; the on-disk DLL's own
    // hash is never recomputed, so a user's custom DLL keeps winning only
    // while its stamp stays pointed at that DLL's hash.
    static void SyncDxgiDllFromEmbedded(string dataRoot)
    {
        var targetPath = Path.Combine(dataRoot, "dxgi.dll");
        var stampPath  = targetPath + ".embedded-sha256";

        var asm = typeof(Program).Assembly;
        const string resourceName = "DllProxy.dxgi.dll";
        using var src = asm.GetManifestResourceStream(resourceName);
        if (src == null) return;

        using var ms = new MemoryStream();
        src.CopyTo(ms);
        var embeddedBytes = ms.ToArray();
        var embeddedHash = Convert.ToHexString(SHA256.HashData(embeddedBytes));

        if (File.Exists(targetPath) && File.Exists(stampPath))
        {
            var existingStamp = File.ReadAllText(stampPath).Trim();
            if (string.Equals(existingStamp, embeddedHash, StringComparison.OrdinalIgnoreCase))
                return;
        }

        File.WriteAllBytes(targetPath, embeddedBytes);
        File.WriteAllText(stampPath, embeddedHash);
    }

    static void SyncBinkAudioEncFromEmbedded(string dataRoot)
    {
        var toolsDir = Path.Combine(dataRoot, "Tools");
        var targetPath = Path.Combine(toolsDir, "binkaudioenc.exe");
        var stampPath  = targetPath + ".embedded-sha256";

        var asm = typeof(Program).Assembly;
        const string resourceName = "BinkAudioEnc.binkaudioenc.exe";
        using var src = asm.GetManifestResourceStream(resourceName);
        if (src == null) return;

        using var ms = new MemoryStream();
        src.CopyTo(ms);
        var embeddedBytes = ms.ToArray();
        var embeddedHash = Convert.ToHexString(SHA256.HashData(embeddedBytes));

        if (File.Exists(targetPath) && File.Exists(stampPath))
        {
            var existingStamp = File.ReadAllText(stampPath).Trim();
            if (string.Equals(existingStamp, embeddedHash, StringComparison.OrdinalIgnoreCase))
                return;
        }

        Directory.CreateDirectory(toolsDir);
        File.WriteAllBytes(targetPath, embeddedBytes);
        File.WriteAllText(stampPath, embeddedHash);
    }

    static void SeedTemplatesIfMissing(string dataRoot)
    {
        var asm = typeof(Program).Assembly;
        const string prefix = "Template/";
        var templatesRoot = Path.Combine(dataRoot, "Tools", "Templates");

        foreach (var resourceName in asm.GetManifestResourceNames())
        {
            if (!resourceName.StartsWith(prefix, StringComparison.Ordinal))
                continue;

            var rel = resourceName.Substring(prefix.Length)
                .Replace('/', Path.DirectorySeparatorChar);
            var targetPath = Path.Combine(templatesRoot, rel);
            // Skip if present so a user-supplied file wins over the seed.
            if (File.Exists(targetPath)) continue;

            var targetDir = Path.GetDirectoryName(targetPath);
            if (!string.IsNullOrEmpty(targetDir))
                Directory.CreateDirectory(targetDir);

            using var src = asm.GetManifestResourceStream(resourceName);
            if (src == null) continue;
            using var dst = File.Create(targetPath);
            src.CopyTo(dst);
        }
    }
}
