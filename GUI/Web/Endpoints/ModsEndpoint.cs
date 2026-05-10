using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Windrose.Quartermaster.Core;

namespace Windrose.Quartermaster.Web.Endpoints;

// Endpoints around the Windrose ~mods/ folder (the path Windrose actually
// reads .pak files from at startup). Two routes:
//
//   GET    /api/mods               -> list every .pak with metadata + a
//                                     boolean flag whether the file was
//                                     produced by us (filename prefix).
//   DELETE /api/mods/{filename}    -> move the pak to the Windows recycle
//                                     bin. ONLY allowed for our own paks
//                                     (Quartermaster_*_P.pak); foreign mods
//                                     return 403 so we can never delete a
//                                     mod the user installed elsewhere.
//
// A Quartermaster build can ship as one .pak (JSON patches only) or as
// a .pak / .ucas / .utoc triplet sharing the same basename when the
// profile also enables the pickup-radius patch. UE5 mounts triplets
// with a matching basename as one logical container, so we treat them
// as one entity here too: list-view aggregates sibling sizes, delete
// endpoint recycles all three files together so the user can't end up
// with a half-deleted triplet that confuses UE5 on next mount.
//
// The recycle-bin step uses Microsoft.VisualBasic.FileIO.FileSystem; the
// assembly ships with the .NET runtime on Windows so no extra NuGet ref
// is required.
public static class ModsEndpoint
{
    // Filename prefix that identifies a pak produced by BuildPipeline.
    // Keep this in sync with BuildPipeline.SanitizeForFileName / the
    // hardcoded "Quartermaster_<safe>_P.pak" template.
    public const string OwnedPrefix = "Quartermaster_";
    public const string OwnedSuffix = "_P.pak";

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
                foreach (var path in Directory.EnumerateFiles(modsDir, "*.pak", SearchOption.TopDirectoryOnly))
                {
                    var fi = new FileInfo(path);
                    var owned = IsQuartermasterPak(fi.Name);
                    // For our own paks: total size includes the IoStore
                    // companions (.ucas/.utoc) that ship next to the .pak
                    // - otherwise the pickup-radius triplet looks
                    // misleadingly tiny (the .pak alone is ~350 bytes).
                    long totalSize = fi.Length;
                    if (owned)
                    {
                        var basePath = path.Substring(0, path.Length - ".pak".Length);
                        foreach (var ext in new[] { ".ucas", ".utoc" })
                        {
                            var companion = basePath + ext;
                            if (File.Exists(companion)) totalSize += new FileInfo(companion).Length;
                        }
                    }
                    files.Add(new
                    {
                        filename = fi.Name,
                        sizeBytes = totalSize,
                        modifiedUtc = fi.LastWriteTimeUtc.ToString("o"),
                        isQuartermaster = owned,
                        // Strip the prefix/suffix so the UI can show a
                        // friendly "x4" instead of "Quartermaster_x4_P.pak"
                        // for our own paks. null for foreign mods.
                        displayName = owned ? StripOwnedAffixes(fi.Name) : null,
                    });
                }
            }
            // Sort: our paks first (so the user's working set is at the top),
            // then foreign mods, alphabetical within each group.
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
            // Path-traversal guard: anything other than a bare filename
            // (no slashes, no parent refs) is rejected outright.
            if (string.IsNullOrEmpty(filename)
                || filename.Contains('/') || filename.Contains('\\')
                || filename.Contains("..")
                || Path.GetFileName(filename) != filename)
            {
                return Results.BadRequest(new { error = "Invalid filename" });
            }

            // Only our own paks are deletable. Foreign mods are off-limits
            // even if the request hits the right endpoint - the 403 makes
            // the constraint explicit instead of silently no-op'ing.
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

            // Quartermaster paks may ship with sibling IoStore files
            // (.ucas/.utoc) that the engine treats as one logical mod
            // alongside the .pak (the pickup-radius triplet). Recycle
            // them together so the user can't end up with a half-deleted
            // triplet that confuses the engine on next mount.
            var basePath = fullPath.Substring(0, fullPath.Length - ".pak".Length);
            var companions = new[] { ".ucas", ".utoc" };
            var recycled = new List<string> { filename };
            try
            {
                // Send to the Windows recycle bin instead of File.Delete so
                // a misclick is recoverable via Explorer. UIOption.OnlyErrorDialogs
                // means the standard "do you want to recycle this?" prompt is
                // suppressed (we already do our own confirm in the UI) but
                // any actual error still pops a dialog - which is fine for
                // a desktop tool.
                Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(
                    fullPath,
                    Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs,
                    Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin,
                    Microsoft.VisualBasic.FileIO.UICancelOption.ThrowException);
                foreach (var ext in companions)
                {
                    var companionPath = basePath + ext;
                    if (File.Exists(companionPath))
                    {
                        Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(
                            companionPath,
                            Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs,
                            Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin,
                            Microsoft.VisualBasic.FileIO.UICancelOption.ThrowException);
                        recycled.Add(Path.GetFileName(companionPath));
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

    static bool IsQuartermasterPak(string filename)
    {
        return filename != null
            && filename.StartsWith(OwnedPrefix, StringComparison.Ordinal)
            && filename.EndsWith(OwnedSuffix, StringComparison.Ordinal);
    }

    static string StripOwnedAffixes(string filename)
    {
        if (!IsQuartermasterPak(filename)) return filename;
        return filename.Substring(
            OwnedPrefix.Length,
            filename.Length - OwnedPrefix.Length - OwnedSuffix.Length);
    }
}
