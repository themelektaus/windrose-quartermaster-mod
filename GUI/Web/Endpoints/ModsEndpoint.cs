using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Windrose.Quartermaster.Core;
using Windrose.Quartermaster.Core.Deploy;

namespace Windrose.Quartermaster.Web.Endpoints;

public static class ModsEndpoint
{
    public const string OwnedPrefix = "Quartermaster_";
    public const string OwnedSuffix = "_P.pak";

    public const string RawCompanionPakSuffix = "_Raw_P.pak";

    public static void Map(WebApplication app, string repoRoot)
    {
        app.MapGet("/api/mods", () =>
        {
            string modsDir;
            try
            {
                modsDir = SteamLocator.FindModsDir();
            }
            catch (Exception ex)
            {
                return Results.Json(new
                {
                    error = "Could not locate Windrose ~mods folder: " + ex.Message,
                    modsDir = (string)null,
                    files = Array.Empty<object>(),
                }, statusCode: 500);
            }

            var files = new List<object>();
            if (Directory.Exists(modsDir))
            {
                var rawCompanions = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var path in Directory.EnumerateFiles(modsDir, "*.pak", SearchOption.TopDirectoryOnly))
                {
                    var name = Path.GetFileName(path);
                    if (IsRawCompanionPak(name))
                    {
                        var displayName = StripRawCompanionAffixes(name);
                        if (!string.IsNullOrEmpty(displayName))
                            rawCompanions[displayName] = path;
                    }
                }

                var foldedCompanions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var path in Directory.EnumerateFiles(modsDir, "*.pak", SearchOption.TopDirectoryOnly))
                {
                    var fi = new FileInfo(path);
                    if (IsRawCompanionPak(fi.Name)) continue;

                    var owned = IsQuartermasterPak(fi.Name);
                    long totalSize = fi.Length;
                    string displayName = null;
                    if (owned)
                    {
                        displayName = StripOwnedAffixes(fi.Name);

                        var basePath = path.Substring(0, path.Length - ".pak".Length);
                        foreach (var ext in new[] { ".ucas", ".utoc" })
                        {
                            var companion = basePath + ext;
                            if (File.Exists(companion)) totalSize += new FileInfo(companion).Length;
                        }

                        if (rawCompanions.TryGetValue(displayName, out var rawPakPath))
                        {
                            totalSize += AggregateTripletSize(rawPakPath);
                            foldedCompanions.Add(displayName);
                        }
                    }
                    files.Add(new
                    {
                        filename = fi.Name,
                        sizeBytes = totalSize,
                        modifiedUtc = fi.LastWriteTimeUtc.ToString("o"),
                        isQuartermaster = owned,
                        displayName = displayName,
                    });
                }

                foreach (var kv in rawCompanions)
                {
                    if (foldedCompanions.Contains(kv.Key)) continue;
                    var fi = new FileInfo(kv.Value);
                    files.Add(new
                    {
                        filename = fi.Name,
                        sizeBytes = AggregateTripletSize(kv.Value),
                        modifiedUtc = fi.LastWriteTimeUtc.ToString("o"),
                        isQuartermaster = true,
                        displayName = kv.Key,
                    });
                }
            }
            files.Sort((a, b) =>
            {
                var aOwn = (bool)((dynamic)a).isQuartermaster;
                var bOwn = (bool)((dynamic)b).isQuartermaster;
                if (aOwn != bOwn) return aOwn ? -1 : 1;
                var aName = (string)((dynamic)a).filename;
                var bName = (string)((dynamic)b).filename;
                return string.Compare(aName, bName, StringComparison.OrdinalIgnoreCase);
            });

            return Results.Json(new
            {
                modsDir,
                files,
            });
        });

        app.MapDelete("/api/mods/{filename}", (string filename) =>
        {
            if (string.IsNullOrEmpty(filename)
                || filename.Contains('/') || filename.Contains('\\')
                || filename.Contains("..")
                || Path.GetFileName(filename) != filename)
            {
                return Results.BadRequest(new { error = "Invalid filename" });
            }

            if (!IsQuartermasterPak(filename))
            {
                return Results.Json(new
                {
                    error = "Refusing to delete a mod that wasn't produced by Quartermaster.",
                    filename,
                }, statusCode: 403);
            }

            string modsDir;
            try
            {
                modsDir = SteamLocator.FindModsDir();
            }
            catch (Exception ex)
            {
                return Results.Json(new
                {
                    error = "Could not locate Windrose ~mods folder: " + ex.Message,
                }, statusCode: 500);
            }

            var fullPath = Path.Combine(modsDir, filename);
            if (!File.Exists(fullPath))
            {
                return Results.NotFound(new { error = "File not found", filename });
            }

            var recycled = new List<string>();
            try
            {
                if (IsRawCompanionPak(filename))
                {
                    RecycleTriplet(fullPath, recycled);
                }
                else
                {
                    RecycleTriplet(fullPath, recycled);

                    var displayName = StripOwnedAffixes(filename);
                    if (!string.IsNullOrEmpty(displayName))
                    {
                        var rawPakName = OwnedPrefix + displayName + RawCompanionPakSuffix;
                        var rawPakPath = Path.Combine(modsDir, rawPakName);
                        if (File.Exists(rawPakPath))
                            RecycleTriplet(rawPakPath, recycled);

                        TryRemoveDeployedSidecars(repoRoot, displayName, recycled);

                        TryRemoveDeployedDllIfIdle(repoRoot, recycled);
                    }
                }
            }
            catch (Exception ex)
            {
                return Results.Json(new
                {
                    error = "Failed to recycle file: " + ex.Message,
                    filename,
                }, statusCode: 500);
            }

            return Results.Json(new
            {
                success = true,
                filename,
                action = "recycled",
                recycled,
            });
        });
    }

    // Removes everything this profile deployed into Win64/Quartermaster: the
    // profile JSON (the pak's source of truth) plus all four feature sidecars.
    static void TryRemoveDeployedSidecars(string repoRoot, string displayName, List<string> recycled)
    {
        try
        {
            var deployer = new GameDeployer(repoRoot);
            foreach (var path in new[]
            {
                deployer.TargetProfileJsonPath(displayName),
                deployer.TargetItemsJsonPath(displayName),
                deployer.TargetProfileWeatherTriggerPath(displayName),
                deployer.TargetProfileKillXpPath(displayName),
                deployer.TargetProfileShantyPath(displayName),
                deployer.TargetProfileLootConfigPath(displayName),
            })
            {
                if (!File.Exists(path)) continue;
                CrossPlatformTrash.DeleteToTrash(path);
                recycled.Add(Path.GetFileName(path));
            }
            // The trash deletes above bypass RemoveProfileJson, so re-merge the
            // mod tab's qm_modtab_mods.txt here, plus the catalog and the Item
            // Spawner user-layout (the removed profile may have been the last one
            // enabling either).
            deployer.RegenerateModsManifest();
            deployer.RegenerateItemCatalog();
            deployer.RegenerateItemSpawnerLayout();
        }
        catch
        {
        }
    }

    static void TryRemoveDeployedDllIfIdle(string repoRoot, List<string> recycled)
    {
        try
        {
            var deployer = new GameDeployer(repoRoot);
            deployer.RemoveDllIfNoProfilesLeft(path =>
            {
                CrossPlatformTrash.DeleteToTrash(path);
                recycled.Add(Path.GetFileName(path));
            });
        }
        catch
        {
        }
    }

    static long AggregateTripletSize(string pakPath)
    {
        long total = 0;
        if (File.Exists(pakPath)) total += new FileInfo(pakPath).Length;
        var basePath = pakPath.Substring(0, pakPath.Length - ".pak".Length);
        foreach (var ext in new[] { ".ucas", ".utoc" })
        {
            var companion = basePath + ext;
            if (File.Exists(companion)) total += new FileInfo(companion).Length;
        }
        return total;
    }

    static void RecycleTriplet(string pakPath, List<string> recycled)
    {
        CrossPlatformTrash.DeleteToTrash(pakPath);
        recycled.Add(Path.GetFileName(pakPath));

        var basePath = pakPath.Substring(0, pakPath.Length - ".pak".Length);
        foreach (var ext in new[] { ".ucas", ".utoc" })
        {
            var companion = basePath + ext;
            if (!File.Exists(companion)) continue;
            CrossPlatformTrash.DeleteToTrash(companion);
            recycled.Add(Path.GetFileName(companion));
        }
    }

    static bool IsQuartermasterPak(string filename)
    {
        return filename != null
            && filename.StartsWith(OwnedPrefix, StringComparison.Ordinal)
            && filename.EndsWith(OwnedSuffix, StringComparison.Ordinal);
    }

    // Raw companion paks also satisfy IsQuartermasterPak; check this first.
    static bool IsRawCompanionPak(string filename)
    {
        return filename != null
            && filename.StartsWith(OwnedPrefix, StringComparison.Ordinal)
            && filename.EndsWith(RawCompanionPakSuffix, StringComparison.Ordinal);
    }

    static string StripOwnedAffixes(string filename)
    {
        // Check the longer RawCompanionPakSuffix before the generic OwnedSuffix.
        if (IsRawCompanionPak(filename))
        {
            return filename.Substring(
                OwnedPrefix.Length,
                filename.Length - OwnedPrefix.Length - RawCompanionPakSuffix.Length);
        }
        if (!IsQuartermasterPak(filename)) return filename;
        return filename.Substring(
            OwnedPrefix.Length,
            filename.Length - OwnedPrefix.Length - OwnedSuffix.Length);
    }

    static string StripRawCompanionAffixes(string filename)
    {
        if (!IsRawCompanionPak(filename)) return filename;
        return filename.Substring(
            OwnedPrefix.Length,
            filename.Length - OwnedPrefix.Length - RawCompanionPakSuffix.Length);
    }

    // GET /api/mods/server-status - whether a dedicated server install exists.
    public static void MapServerStatus(WebApplication app)
    {
        app.MapGet("/api/mods/server-status", () =>
        {
            var serverRoot = SteamLocator.FindServerRoot();
            return Results.Json(new
            {
                detected = serverRoot != null,
                serverRoot,
            });
        });
    }

    // GET /api/mods/export-zip - streams a ZIP containing all deployed
    // Quartermaster files (paks + DLL + sidecars) with game-relative paths.
    // Structure:   R5/Content/Paks/~mods/Quartermaster_*.*
    //              R5/Binaries/Win64/dxgi.dll
    //              R5/Binaries/Win64/Quartermaster/qm_*.*
    public static void MapExportZip(WebApplication app, string repoRoot)
    {
        app.MapGet("/api/mods/export-zip", async (HttpContext ctx) =>
        {
            string modsDir;
            string binDir;
            try
            {
                modsDir = SteamLocator.FindModsDir();
                binDir = SteamLocator.FindBinariesWin64Dir();
            }
            catch (Exception ex)
            {
                ctx.Response.StatusCode = 500;
                await ctx.Response.WriteAsJsonAsync(new
                {
                    error = "Could not locate game paths: " + ex.Message,
                });
                return;
            }

            var entries = new List<(string DiskPath, string ZipPath)>();

            // Paks: all Quartermaster_* files (.pak, .ucas, .utoc)
            if (Directory.Exists(modsDir))
            {
                foreach (var ext in new[] { "*.pak", "*.ucas", "*.utoc" })
                {
                    foreach (var path in Directory.GetFiles(modsDir, ext, SearchOption.TopDirectoryOnly))
                    {
                        var name = Path.GetFileName(path);
                        if (!name.StartsWith(OwnedPrefix, StringComparison.Ordinal)) continue;
                        entries.Add((path, "R5/Content/Paks/~mods/" + name));
                    }
                }
            }

            // DLL (only if it is ours - leave foreign proxies alone)
            var dllPath = Path.Combine(binDir, "dxgi.dll");
            if (File.Exists(dllPath) && GameDeployer.IsQuartermasterDllStatic(dllPath))
                entries.Add((dllPath, "R5/Binaries/Win64/dxgi.dll"));

            // Sidecars
            var sidecarDir = Path.Combine(binDir, "Quartermaster");
            if (Directory.Exists(sidecarDir))
            {
                foreach (var path in Directory.GetFiles(sidecarDir, "qm_*", SearchOption.TopDirectoryOnly))
                {
                    var name = Path.GetFileName(path);
                    entries.Add((path, "R5/Binaries/Win64/Quartermaster/" + name));
                }
            }

            if (entries.Count == 0)
            {
                ctx.Response.StatusCode = 404;
                await ctx.Response.WriteAsJsonAsync(new
                {
                    error = "No Quartermaster files deployed - build a profile first.",
                });
                return;
            }

            using var ms = new MemoryStream();
            using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
            {
                foreach (var (diskPath, zipPath) in entries)
                {
                    var entry = zip.CreateEntry(zipPath, CompressionLevel.Fastest);
                    using var src = File.OpenRead(diskPath);
                    using var dst = entry.Open();
                    await src.CopyToAsync(dst);
                }
            }

            ctx.Response.ContentType = "application/zip";
            ctx.Response.Headers["Content-Disposition"] =
                "attachment; filename=\"Quartermaster_Export.zip\"";
            ctx.Response.ContentLength = ms.Length;
            ms.Position = 0;
            await ms.CopyToAsync(ctx.Response.Body);
        });
    }
}