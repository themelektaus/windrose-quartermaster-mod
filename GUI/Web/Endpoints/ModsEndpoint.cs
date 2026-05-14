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
//   DELETE /api/mods/{filename}    -> move the pak to the trash
//                                     (Windows recycle bin or XDG trash on Linux).
//                                     ONLY allowed for our own paks
//                                     (Quartermaster_*_P.pak); foreign mods
//                                     return 403 so we can never delete a
//                                     mod the user installed elsewhere.
//
// A Quartermaster build can ship as multiple files behind a single logical
// mod:
//
//   - The main pak (.pak) and its optional IoStore composite companions
//     (.ucas/.utoc) under one basename, e.g.
//     Quartermaster_<name>_P.{pak,ucas,utoc}. This carries item/loot/bell
//     JSON patches plus the Pickup and NoSmoke IoStore features.
//
//   - A SEPARATE raw companion under
//     Quartermaster_<name>_Raw_P.{pak[,ucas,utoc]}. This holds features
//     that can't ride along in the composite: building-stability ships
//     its patched zen chunks via retoc unpack-raw/pack-raw (incompatible
//     with the composite's to-zen container), and minimap-range ships a
//     loose R5/Config/DefaultR5MapSettings.ini via repak's PakFile
//     subsystem (to-zen silently drops non-asset files). The container
//     is adaptive - .pak only when minimap alone is active; .pak + .ucas
//     + .utoc when stability is involved.
//
// We treat all of those files as ONE logical mod here: list view shows
// one row per "name" with aggregated size; delete recycles every file
// belonging to the logical mod so the user can't end up with a half-
// deleted set that confuses UE5 on next mount.
//
// The trash step uses CrossPlatformTrash.DeleteToTrash which dispatches
// to Windows recycle bin or XDG trash spec on Linux/macOS.
public static class ModsEndpoint
{
    // Filename prefix that identifies a pak produced by BuildPipeline.
    // Keep this in sync with BuildPipeline.SanitizeForFileName / the
    // hardcoded "Quartermaster_<safe>_P.pak" template.
    public const string OwnedPrefix = "Quartermaster_";
    public const string OwnedSuffix = "_P.pak";

    // Suffix that identifies the raw companion's pak. We hide these
    // from the listing and aggregate them into the parent mod's row
    // (or surface them standalone when there's no parent). Keep in
    // sync with BuildPipeline.RawCompanionSuffix.
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
                // Two-pass enumeration so the raw companion can fold
                // into its parent's row.
                //
                // Pass 1: index every Quartermaster_*_Raw_P.pak by the
                // parent display name (the part between the prefix and
                // the _Raw_P suffix) - that's the same display name the
                // parent .pak would compute.
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

                // Pass 2: enumerate every .pak; skip raw-companion entries
                // because they get folded into their parent OR (when they
                // exist standalone, i.e. the raw companion's features are
                // the only ones the profile activated) get surfaced under
                // the parent display name with no aggregation needed.
                // Track which companions ended up folded so any leftover
                // companions can be promoted to standalone rows.
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

                        // Aggregate sibling .ucas/.utoc that share this
                        // basename (the composite IoStore triplet).
                        var basePath = path.Substring(0, path.Length - ".pak".Length);
                        foreach (var ext in new[] { ".ucas", ".utoc" })
                        {
                            var companion = basePath + ext;
                            if (File.Exists(companion)) totalSize += new FileInfo(companion).Length;
                        }

                        // Aggregate the raw companion if it exists for
                        // this display name (separate basename, same
                        // logical mod). Layout is adaptive - the .pak
                        // alone counts when minimap-only ships there.
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
                        // Strip the prefix/suffix so the UI can show a
                        // friendly "x4" instead of "Quartermaster_x4_P.pak"
                        // for our own paks. null for foreign mods.
                        displayName = displayName,
                    });
                }

                // Pass 3: surface raw-companion-only mods (raw companion
                // exists but parent .pak doesn't - the profile activated
                // only features that route into the raw companion) under
                // the parent display name.
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

            // Quartermaster mods may comprise multiple files:
            //
            //   1. The main triplet/pak set: <basename>.{pak,ucas,utoc}
            //      where <basename> is e.g. "Quartermaster_<name>_P".
            //
            //   2. The raw companion:
            //      Quartermaster_<name>_Raw_P.{pak[,ucas,utoc]}, when the
            //      profile enabled stability and/or minimap-range.
            //
            // The user invokes DELETE on a SINGLE pak filename, but the
            // expected behaviour is "delete the whole logical mod" - so we
            // need to recycle every file that belongs to the same display
            // name. Two cases:
            //
            //   - Filename is the main pak (..._P.pak): recycle its main
            //     triplet AND any matching raw companion files.
            //   - Filename is the raw companion (..._Raw_P.pak):
            //     recycle only its file set (the main set, if it exists,
            //     stays - the user explicitly clicked the companion row,
            //     which only appears when the companion is standalone).
            var recycled = new List<string>();
            try
            {
                if (IsRawCompanionPak(filename))
                {
                    RecycleTriplet(fullPath, recycled);
                }
                else
                {
                    // Main pak. Recycle its triplet + any matching raw
                    // companion for the same display name.
                    RecycleTriplet(fullPath, recycled);

                    var displayName = StripOwnedAffixes(filename);
                    if (!string.IsNullOrEmpty(displayName))
                    {
                        var rawPakName = OwnedPrefix + displayName + RawCompanionPakSuffix;
                        var rawPakPath = Path.Combine(modsDir, rawPakName);
                        if (File.Exists(rawPakPath))
                            RecycleTriplet(rawPakPath, recycled);
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

    // Sums file sizes of <basename>.{pak,ucas,utoc} for the given .pak path.
    // Used to aggregate triplet companions into the row's reported size.
    // Missing companions contribute 0 (silent - companions are optional).
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

    // Recycles every file of the triplet rooted at the given .pak path
    // (the .pak itself + sibling .ucas/.utoc). Appends each recycled
    // filename to the `recycled` list so the response can report what
    // was acted on.
    static void RecycleTriplet(string pakPath, List<string> recycled)
    {
        // Cross-platform trash: Windows recycle bin or XDG trash on Linux/macOS.
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

    // True for raw companion paks (Quartermaster_<name>_Raw_P.pak).
    // These are technically also valid IsQuartermasterPak filenames (they
    // satisfy prefix + _P.pak suffix), so listing logic checks this FIRST
    // to fold them under the parent.
    static bool IsRawCompanionPak(string filename)
    {
        return filename != null
            && filename.StartsWith(OwnedPrefix, StringComparison.Ordinal)
            && filename.EndsWith(RawCompanionPakSuffix, StringComparison.Ordinal);
    }

    static string StripOwnedAffixes(string filename)
    {
        // Order matters: check the LONGER raw companion suffix first,
        // otherwise "_Raw_P.pak" would be parsed as the generic "_P.pak"
        // suffix leaving an extra "_Raw" tail in the display name.
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

    // Strips the raw-companion-specific affixes (helper kept separate so
    // the generic StripOwnedAffixes can stay a thin compatibility wrapper).
    // Returns the parent display name - e.g. "Quartermaster_Tausi_Raw_P.pak"
    // -> "Tausi".
    static string StripRawCompanionAffixes(string filename)
    {
        if (!IsRawCompanionPak(filename)) return filename;
        return filename.Substring(
            OwnedPrefix.Length,
            filename.Length - OwnedPrefix.Length - RawCompanionPakSuffix.Length);
    }
}