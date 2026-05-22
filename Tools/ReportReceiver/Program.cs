using System;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Windrose.Quartermaster.ReportReceiver;

public static class Program
{
    public static int Main(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);

        const long uploadCeiling = 200L * 1024 * 1024;
        builder.Services.Configure<KestrelServerOptions>(opts =>
        {
            opts.Limits.MaxRequestBodySize = uploadCeiling;
        });
        builder.Services.Configure<Microsoft.AspNetCore.Http.Features.FormOptions>(opts =>
        {
            opts.MultipartBodyLengthLimit = uploadCeiling;
        });

        var app = builder.Build();

        var reportsDir = ResolveReportsDir();
        Directory.CreateDirectory(reportsDir);
        Console.WriteLine($"[ReportReceiver] Reports directory: {reportsDir}");

        app.MapPost("/", async (HttpRequest req) =>
        {
            ReportPayload payload;
            try
            {
                payload = await JsonSerializer.DeserializeAsync<ReportPayload>(
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

            if (payload is null
                || string.IsNullOrWhiteSpace(payload.Title)
                || string.IsNullOrWhiteSpace(payload.Description)
                || string.IsNullOrWhiteSpace(payload.Attachment))
            {
                return Results.BadRequest(new
                {
                    error = "title, description and attachment are required",
                });
            }

            byte[] zipBytes;
            try
            {
                zipBytes = Convert.FromBase64String(payload.Attachment);
            }
            catch (FormatException ex)
            {
                return Results.BadRequest(new { error = "attachment is not valid base64: " + ex.Message });
            }

            var stamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss");
            var safeTitle = SanitizeForPath(payload.Title);
            if (string.IsNullOrEmpty(safeTitle))
                safeTitle = "untitled";

            string targetDir = Path.Combine(reportsDir, $"{stamp}-{safeTitle}");
            int suffix = 2;
            while (Directory.Exists(targetDir))
            {
                targetDir = Path.Combine(reportsDir, $"{stamp}-{safeTitle}-{suffix}");
                suffix++;
            }
            Directory.CreateDirectory(targetDir);

            int filesExtracted = 0;
            try
            {
                using var ms = new MemoryStream(zipBytes);
                using var zip = new ZipArchive(ms, ZipArchiveMode.Read);
                foreach (var entry in zip.Entries)
                {
                    if (string.IsNullOrEmpty(entry.Name))
                        continue; // directory entry
                    var destPath = Path.GetFullPath(Path.Combine(targetDir, entry.FullName));
                    var targetDirFull = Path.GetFullPath(targetDir + Path.DirectorySeparatorChar);
                    if (!destPath.StartsWith(targetDirFull, StringComparison.OrdinalIgnoreCase))
                    {
                        Console.WriteLine($"[ReportReceiver] Skipping zip entry outside target: {entry.FullName}");
                        continue;
                    }
                    Directory.CreateDirectory(Path.GetDirectoryName(destPath)!);
                    using var entryStream = entry.Open();
                    using var dest = File.Create(destPath);
                    await entryStream.CopyToAsync(dest);
                    filesExtracted++;
                }
            }
            catch (Exception ex)
            {
                return Results.Json(new
                {
                    error = "Failed to extract attachment: " + ex.Message,
                    folder = targetDir,
                }, statusCode: 500);
            }

            var txt = new StringBuilder();
            txt.AppendLine("Title:");
            txt.AppendLine(payload.Title.Trim());
            txt.AppendLine();
            txt.AppendLine("Description:");
            txt.AppendLine(payload.Description.Trim());
            txt.AppendLine();
            txt.AppendLine("- - -");
            txt.AppendLine($"Received UTC : {DateTime.UtcNow:yyyy-MM-ddTHH:mm:ssZ}");
            txt.AppendLine($"Attachment   : {payload.AttachmentName ?? "(unset)"}");
            txt.AppendLine($"Files in zip : {filesExtracted}");
            if (payload.Metadata is not null)
            {
                txt.AppendLine();
                txt.AppendLine("Metadata:");
                txt.AppendLine(JsonSerializer.Serialize(payload.Metadata,
                    new JsonSerializerOptions { WriteIndented = true }));
            }
            await File.WriteAllTextAsync(
                Path.Combine(targetDir, "report.txt"), txt.ToString(), Encoding.UTF8);

            Console.WriteLine(
                $"[ReportReceiver] Saved report '{payload.Title}' -> {targetDir} ({filesExtracted} file(s))");

            return Results.Json(new
            {
                success = true,
                folder = targetDir,
                filesExtracted = filesExtracted,
            });
        });

        app.MapGet("/", () => Results.Text("GET not allowed", "text/plain"));

        app.Run("http://0.0.0.0:17778");
        return 0;
    }

    private static string ResolveReportsDir()
    {
        var fromEnv = Environment.GetEnvironmentVariable("REPORT_RECEIVER_DIR");
        if (!string.IsNullOrWhiteSpace(fromEnv))
            return Path.GetFullPath(fromEnv);
        return Path.Combine(AppContext.BaseDirectory, "reports");
    }

    private static string SanitizeForPath(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (var ch in s.Trim())
        {
            if (char.IsLetterOrDigit(ch)) sb.Append(ch);
            else if (ch == ' ' || ch == '-' || ch == '_') sb.Append('-');
        }
        var result = sb.ToString();
        while (result.Contains("--", StringComparison.Ordinal))
            result = result.Replace("--", "-");
        result = result.Trim('-');
        if (result.Length > 60) result = result.Substring(0, 60).TrimEnd('-');
        return result;
    }

    private sealed class ReportPayload
    {
        [JsonPropertyName("title")]
        public string Title { get; set; }
        [JsonPropertyName("description")]
        public string Description { get; set; }
        [JsonPropertyName("attachmentName")]
        public string AttachmentName { get; set; }
        [JsonPropertyName("attachment")]
        public string Attachment { get; set; }
        [JsonPropertyName("metadata")]
        public JsonElement? Metadata { get; set; }
    }
}
