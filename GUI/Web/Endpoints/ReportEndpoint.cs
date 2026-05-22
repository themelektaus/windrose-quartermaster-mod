using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Windrose.Quartermaster.Core;
using Windrose.Quartermaster.Core.Deploy;

namespace Windrose.Quartermaster.Web.Endpoints;

// POST /api/report   body: { title: "...", description: "..." }
//
// User-facing bug-report submission. Collects everything we need to
// triage an issue without forcing the user to dig up files manually:
//
//   - R5.log                       (game's own log, %LOCALAPPDATA%\R5\Saved\Logs\R5.log)
//   - Quartermaster_Inject.log     (our DLL log, same folder)
//   - All profile JSONs            (<DataRoot>\Profiles\*.json)
//   - ~mods/ file listing          (filename + size for every pak in the game's
//                                   ~mods folder, identifies WHICH paks were
//                                   active when the issue occurred)
//   - metadata.json                (Quartermaster version, OS, timestamps, ~mods dir,
//                                   data root, title+description from the user)
//
// Everything goes into an in-memory ZIP, the ZIP is base64-encoded and POSTed
// as a single JSON request to ReportEndpointUrl below. The receiver is
// expected to decode `attachment` (base64 ZIP), extract files of interest
// and link them on an issue tracker.
//
// The outbound URL is a hardcoded constant - intentionally simple, the
// expectation is a self-hosted ingestion endpoint that the maintainer
// configures themselves. Change ReportEndpointUrl below to point at your
// receiver.
public static class ReportEndpoint
{
    // ------------------------------------------------------------------
    // CONFIGURE THIS: where reports get POSTed to.
    //
    // Expected receiver behaviour: accepts a JSON body with the shape
    //   { title, description, attachmentName, attachment (base64 zip), metadata }
    // returns any 2xx status on success. Response body (if any) is
    // forwarded to the user so a server can return e.g. an issue URL.
    // ------------------------------------------------------------------
    private const string ReportEndpointUrl = "https://example.com/quartermaster-reports";

    // Cap on the outbound JSON body. The base64-encoded ZIP can grow with
    // multi-MB R5.log files; we still want a sane upper bound so the
    // request doesn't take ages or timeout. 50 MB raw -> ~67 MB base64,
    // which covers very long sessions with verbose logs.
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

            // Build the ZIP in memory. We collect best-effort - any missing
            // file (e.g. user never ran the game so R5.log doesn't exist)
            // is silently skipped, the metadata.json records what was
            // included / what was missing so the receiver still has a
            // complete picture.
            string modsDir = null;
            try { modsDir = SteamLocator.FindModsDir(); }
            catch { /* not located - metadata will say so */ }

            var collected = new List<string>();
            var missing = new List<string>();
            byte[] zipBytes;
            using (var zipStream = new MemoryStream())
            {
                using (var zip = new ZipArchive(zipStream, ZipArchiveMode.Create, leaveOpen: true))
                {
                    // ----- Logs -----
                    var logsDir = Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                        "R5", "Saved", "Logs");

                    TryAddFileToZip(zip, Path.Combine(logsDir, "R5.log"),
                        "logs/R5.log", collected, missing);
                    TryAddFileToZip(zip, Path.Combine(logsDir, "Quartermaster_Inject.log"),
                        "logs/Quartermaster_Inject.log", collected, missing);

                    // ----- Profiles -----
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

                    // ----- ~mods/ file listing -----
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
                            catch { /* ignore - we still want to log the name */ }
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

                    // ----- Metadata -----
                    var metaObj = new
                    {
                        title = body.Title,
                        description = body.Description,
                        timestampUtc = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ"),
                        quartermasterVersion = GetAssemblyVersion(),
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

            // Cap check (after compression - we don't bound the ZIP build
            // itself because that's bounded by what's on disk and we want
            // to give the user clean failure messaging).
            if (zipBytes.LongLength > MaxBodyBytes)
            {
                return Results.Json(new
                {
                    success = false,
                    error = "Report payload exceeds the upload ceiling ("
                        + (zipBytes.LongLength / 1024 / 1024) + " MB > "
                        + (MaxBodyBytes / 1024 / 1024) + " MB). "
                        + "This usually means R5.log is enormous - try restarting the game once to rotate it.",
                }, statusCode: 413);
            }

            // ----- Outbound POST -----
            var outboundPayload = new
            {
                title = body.Title,
                description = body.Description,
                attachmentName = "quartermaster-report-" +
                    DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + ".zip",
                attachment = Convert.ToBase64String(zipBytes),
                metadata = new
                {
                    quartermasterVersion = GetAssemblyVersion(),
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
            catch { /* ignore */ }

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
            // Use Open + manual copy instead of CreateEntryFromFile because the
            // source files (especially R5.log) may be open for write by the
            // running game; we need FileShare.ReadWrite to read them anyway.
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

    private static void AddTextToZip(ZipArchive zip, string entryName, string text)
    {
        var entry = zip.CreateEntry(entryName, CompressionLevel.Optimal);
        using var entryStream = entry.Open();
        var bytes = Encoding.UTF8.GetBytes(text);
        entryStream.Write(bytes, 0, bytes.Length);
    }

    private static string GetAssemblyVersion()
    {
        try
        {
            var asm = typeof(ReportEndpoint).Assembly;
            var info = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>();
            if (info != null && !string.IsNullOrEmpty(info.InformationalVersion))
                return info.InformationalVersion;
            return asm.GetName().Version?.ToString() ?? "unknown";
        }
        catch { return "unknown"; }
    }

    private sealed class ReportRequestDto
    {
        public string Title { get; set; }
        public string Description { get; set; }
    }
}
