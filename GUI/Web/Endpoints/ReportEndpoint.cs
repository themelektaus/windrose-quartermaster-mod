using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Windrose.Quartermaster.Core;
using Windrose.Quartermaster.Core.Deploy;

namespace Windrose.Quartermaster.Web.Endpoints;

public static class ReportEndpoint
{
    private const string ReportEndpointUrl = "https://quartermaster-report.nockal.com";

    private const long MaxBodyBytes = 75L * 1024 * 1024;

    private static readonly HttpClient s_http = new HttpClient
    {
        Timeout = TimeSpan.FromSeconds(60),
    };

    public static void Map(WebApplication app, string repoRoot)
    {
        var paths = WindrosePaths.FromModRoot(repoRoot);

        app.MapPost("/api/report", async (HttpRequest req) =>
        {
            ReportRequestDto body;
            try
            {
                body = await JsonSerializer.DeserializeAsync<ReportRequestDto>(
                    req.Body,
                    new JsonSerializerOptions
                    {
                        PropertyNameCaseInsensitive = true,
                    });
            }
            catch (Exception ex)
            {
                return Results.BadRequest(new { error = "Invalid JSON: " + ex.Message });
            }

            if (body == null
                || string.IsNullOrWhiteSpace(body.Title)
                || string.IsNullOrWhiteSpace(body.Description))
            {
                return Results.BadRequest(new
                {
                    error = "title and description are required",
                });
            }

            // Nickname is optional; trim and cap so a stray value can't bloat
            // the metadata. Empty -> null so consumers can treat "anonymous".
            var nickname = body.Nickname?.Trim();
            if (string.IsNullOrEmpty(nickname)) nickname = null;
            else if (nickname.Length > 80) nickname = nickname.Substring(0, 80);

            string modsDir = null;
            try { modsDir = SteamLocator.FindModsDir(); }
            catch { }

            var collected = new List<string>();
            var missing = new List<string>();
            byte[] zipBytes;
            using (var zipStream = new MemoryStream())
            {
                using (var zip = new ZipArchive(zipStream, ZipArchiveMode.Create, leaveOpen: true))
                {
                    var savedDir = Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                        "R5", "Saved");
                    var logsDir = Path.Combine(savedDir, "Logs");

                    TryAddFileToZip(zip, Path.Combine(logsDir, "R5.log"),
                        "logs/R5.log", collected, missing);
                    TryAddFileToZip(zip, Path.Combine(logsDir, "Quartermaster_Inject.log"),
                        "logs/Quartermaster_Inject.log", collected, missing);

                    // Savegames: per numeric-Steam-ID profile only the live
                    // RocksDB_v2 store (what the game and all save patchers
                    // actually use). The non-numeric siblings ("... - Kopie",
                    // "<id>_Backups") and RocksDB_v2_Backups are user/game
                    // backup copies that multiply the payload without
                    // diagnostic value. Config + SaveGames are tiny.
                    TryAddDirectoryToZip(zip, Path.Combine(savedDir, "Config"),
                        "saved/Config/", collected, missing);
                    TryAddDirectoryToZip(zip, Path.Combine(savedDir, "SaveGames"),
                        "saved/SaveGames/", collected, missing);
                    var saveProfilesRoot = Path.Combine(savedDir, "SaveProfiles");
                    if (Directory.Exists(saveProfilesRoot))
                    {
                        foreach (var steamDir in Directory.EnumerateDirectories(saveProfilesRoot))
                        {
                            var steamId = Path.GetFileName(steamDir);
                            if (!ulong.TryParse(steamId, out _)) continue;
                            TryAddDirectoryToZip(zip,
                                Path.Combine(steamDir, "RocksDB_v2"),
                                "saved/SaveProfiles/" + steamId + "/RocksDB_v2/",
                                collected, missing);
                            var acct = Path.Combine(steamDir, "RocksDB", "AccountDescription.json");
                            if (File.Exists(acct))
                            {
                                TryAddFileToZip(zip, acct,
                                    "saved/SaveProfiles/" + steamId + "/AccountDescription.json",
                                    collected, missing);
                            }
                        }
                    }
                    else
                    {
                        missing.Add("saved/SaveProfiles/ (directory not found: "
                            + saveProfilesRoot + ")");
                    }

                    if (Directory.Exists(paths.Profiles))
                    {
                        foreach (var jsonPath in Directory.EnumerateFiles(
                            paths.Profiles, "*.json", SearchOption.TopDirectoryOnly))
                        {
                            var name = Path.GetFileName(jsonPath);
                            TryAddFileToZip(zip, jsonPath,
                                "profiles/" + name, collected, missing);
                        }
                    }
                    else
                    {
                        missing.Add("profiles/ (directory not found: " + paths.Profiles + ")");
                    }

                    var modsListing = new StringBuilder();
                    if (modsDir != null && Directory.Exists(modsDir))
                    {
                        modsListing.AppendLine("# " + modsDir);
                        modsListing.AppendLine();
                        var entries = Directory.EnumerateFiles(modsDir, "*", SearchOption.TopDirectoryOnly)
                            .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
                            .ToList();
                        foreach (var entry in entries)
                        {
                            long size = -1;
                            DateTime mtime = default;
                            try
                            {
                                var fi = new FileInfo(entry);
                                size = fi.Length;
                                mtime = fi.LastWriteTimeUtc;
                            }
                            catch { }
                            modsListing.AppendLine(string.Format(
                                "{0,12}  {1:yyyy-MM-dd HH:mm:ss}Z  {2}",
                                size >= 0 ? size.ToString() : "?",
                                mtime,
                                Path.GetFileName(entry)));
                        }
                        if (entries.Count == 0)
                            modsListing.AppendLine("(empty)");
                    }
                    else
                    {
                        modsListing.AppendLine("# ~mods folder not found");
                        if (modsDir != null)
                            modsListing.AppendLine("# searched: " + modsDir);
                    }
                    AddTextToZip(zip, "mods.txt", modsListing.ToString());
                    collected.Add("mods.txt");

                    // Annotated SaveProfiles tree - mirrors the Characters-tab
                    // discovery filters so "no characters found" reports are
                    // self-diagnosing (numeric-Steam-ID gate, CURRENT, Jewelry
                    // module). Folder names + flags only, never save contents.
                    string saveProfilesDiag;
                    try { saveProfilesDiag = InventorySaveSlotsPatcher.DiagnoseSaveProfiles(); }
                    catch (Exception ex) { saveProfilesDiag = "diagnostic failed: " + ex; }
                    AddTextToZip(zip, "saveprofiles.txt", saveProfilesDiag);
                    collected.Add("saveprofiles.txt");

                    var metaObj = new
                    {
                        title = body.Title,
                        description = body.Description,
                        nickname = nickname,
                        timestampUtc = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ"),
                        quartermasterVersion = AppVersion.Informational,
                        os = Environment.OSVersion.VersionString,
                        platform = Environment.OSVersion.Platform.ToString(),
                        machineName = Environment.MachineName,
                        userName = Environment.UserName,
                        is64Bit = Environment.Is64BitOperatingSystem,
                        clrVersion = Environment.Version.ToString(),
                        dataRoot = repoRoot,
                        modsDir = modsDir,
                        collected = collected,
                        missing = missing,
                    };
                    AddTextToZip(zip, "metadata.json",
                        JsonSerializer.Serialize(metaObj, new JsonSerializerOptions
                        {
                            WriteIndented = true,
                        }));
                    collected.Add("metadata.json");
                }
                zipBytes = zipStream.ToArray();
            }

            if (zipBytes.LongLength > MaxBodyBytes)
            {
                return Results.Json(new
                {
                    success = false,
                    error = "Report payload exceeds the upload ceiling ("
                        + (zipBytes.LongLength / 1024 / 1024) + " MB > "
                        + (MaxBodyBytes / 1024 / 1024) + " MB). "
                        + "This usually means R5.log or the savegames are enormous - try restarting the game once to rotate the log.",
                }, statusCode: 413);
            }

            var outboundPayload = new
            {
                title = body.Title,
                description = body.Description,
                nickname = nickname,
                version = AppVersion.Informational,
                attachmentName = "quartermaster-report-" +
                    DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + ".zip",
                attachment = Convert.ToBase64String(zipBytes),
                metadata = new
                {
                    quartermasterVersion = AppVersion.Informational,
                    os = Environment.OSVersion.VersionString,
                    timestampUtc = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ"),
                    attachmentSizeBytes = zipBytes.LongLength,
                    collected = collected,
                    missing = missing,
                },
            };

            string outboundJson = JsonSerializer.Serialize(outboundPayload);
            using var content = new StringContent(outboundJson, Encoding.UTF8);
            content.Headers.ContentType = new MediaTypeHeaderValue("application/json");

            HttpResponseMessage response;
            try
            {
                response = await s_http.PostAsync(ReportEndpointUrl, content);
            }
            catch (Exception ex)
            {
                return Results.Json(new
                {
                    success = false,
                    error = "Could not reach the report endpoint: " + ex.Message,
                    endpointUrl = ReportEndpointUrl,
                    attachmentSizeBytes = zipBytes.LongLength,
                    collected = collected,
                    missing = missing,
                }, statusCode: 502);
            }

            string respBody = "";
            try { respBody = await response.Content.ReadAsStringAsync(); }
            catch { }

            return Results.Json(new
            {
                success = response.IsSuccessStatusCode,
                statusCode = (int)response.StatusCode,
                serverResponse = respBody,
                endpointUrl = ReportEndpointUrl,
                attachmentSizeBytes = zipBytes.LongLength,
                collected = collected,
                missing = missing,
            }, statusCode: response.IsSuccessStatusCode ? 200 : (int)response.StatusCode);
        });
    }

    private static void TryAddFileToZip(
        ZipArchive zip, string sourcePath, string entryName,
        List<string> collected, List<string> missing)
    {
        try
        {
            if (!File.Exists(sourcePath))
            {
                missing.Add(entryName + " (not found: " + sourcePath + ")");
                return;
            }
            // Source files may be held open for write by the running game; FileShare.ReadWrite is required to read them.
            var entry = zip.CreateEntry(entryName, CompressionLevel.Optimal);
            using var entryStream = entry.Open();
            using var src = new FileStream(sourcePath,
                FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            src.CopyTo(entryStream);
            collected.Add(entryName);
        }
        catch (Exception ex)
        {
            missing.Add(entryName + " (read error: " + ex.Message + ")");
        }
    }

    // Recursive directory capture. One summary line in `collected` instead of
    // one entry per file - a live RocksDB store holds hundreds of SSTs and
    // would otherwise drown the metadata/response listings.
    private static void TryAddDirectoryToZip(
        ZipArchive zip, string sourceDir, string entryPrefix,
        List<string> collected, List<string> missing)
    {
        try
        {
            if (!Directory.Exists(sourceDir))
            {
                missing.Add(entryPrefix + " (not found: " + sourceDir + ")");
                return;
            }
            int files = 0, failed = 0;
            long bytes = 0;
            foreach (var path in Directory.EnumerateFiles(
                sourceDir, "*", SearchOption.AllDirectories))
            {
                var rel = Path.GetRelativePath(sourceDir, path)
                    .Replace(Path.DirectorySeparatorChar, '/');
                try
                {
                    // Open the source before creating the entry so a locked
                    // file (e.g. the RocksDB LOCK while the game runs) does
                    // not leave an empty zip entry behind.
                    using var src = new FileStream(path,
                        FileMode.Open, FileAccess.Read,
                        FileShare.ReadWrite | FileShare.Delete);
                    var entry = zip.CreateEntry(entryPrefix + rel, CompressionLevel.Optimal);
                    using var entryStream = entry.Open();
                    bytes += src.Length;
                    src.CopyTo(entryStream);
                    files++;
                }
                catch
                {
                    failed++;
                }
            }
            collected.Add(entryPrefix + " (" + files + " file(s), "
                + (bytes / 1024) + " KB)");
            if (failed > 0)
                missing.Add(entryPrefix + " (" + failed + " file(s) unreadable)");
        }
        catch (Exception ex)
        {
            missing.Add(entryPrefix + " (error: " + ex.Message + ")");
        }
    }

    private static void AddTextToZip(ZipArchive zip, string entryName, string text)
    {
        var entry = zip.CreateEntry(entryName, CompressionLevel.Optimal);
        using var entryStream = entry.Open();
        var bytes = Encoding.UTF8.GetBytes(text);
        entryStream.Write(bytes, 0, bytes.Length);
    }

    private sealed class ReportRequestDto
    {
        public string Title { get; set; }
        public string Description { get; set; }
        public string Nickname { get; set; }
    }
}
