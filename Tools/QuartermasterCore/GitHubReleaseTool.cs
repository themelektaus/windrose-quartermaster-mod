using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;

namespace Windrose.Quartermaster.Core
{
    // Downloads + SHA256-verifies a pinned tool exe from a GitHub release zip,
    // caching it in the mod root. SHA256 guards against corrupt downloads, not tampering.
    static class GitHubReleaseTool
    {
        public static string Resolve(
            string modRoot, string exeName, string toolLabel,
            string version, string assetName, string downloadUrl, Action<string> log)
        {
            var exePath = Path.Combine(modRoot, exeName);
            if (File.Exists(exePath)) return exePath;

            log?.Invoke(exeName + " not present - downloading v" + version);
            Directory.CreateDirectory(modRoot);
            Download(exePath, exeName, assetName, downloadUrl, log);
            log?.Invoke("Installed: " + exePath + " (" + toolLabel + " v" + version + ")");
            return exePath;
        }

        static void Download(string targetPath, string exeName, string assetName, string url, Action<string> log)
        {
            var tmpDir = Path.Combine(Path.GetTempPath(),
                "windrose-tool-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tmpDir);

            try
            {
                log?.Invoke("URL: " + url);
                var zipPath = Path.Combine(tmpDir, assetName);
                var shaPath = zipPath + ".sha256";

                using (var http = new HttpClient())
                {
                    http.Timeout = TimeSpan.FromMinutes(2);
                    http.DefaultRequestHeaders.UserAgent.Add(
                        new ProductInfoHeaderValue("Windrose-Quartermaster-GUI", "1.0"));
                    DownloadTo(http, url, zipPath);
                    DownloadTo(http, url + ".sha256", shaPath);
                }

                var expected = File.ReadAllText(shaPath).Trim()
                    .Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries)[0]
                    .ToLowerInvariant();

                string actual;
                using (var sha = SHA256.Create())
                using (var fs = File.OpenRead(zipPath))
                {
                    actual = ToHex(sha.ComputeHash(fs));
                }

                if (actual != expected)
                {
                    throw new InvalidOperationException(
                        "SHA256 mismatch for " + assetName + ".\n" +
                        "  Expected: " + expected + "\n" +
                        "  Actual:   " + actual);
                }
                log?.Invoke("SHA256 verified");

                var extractDir = Path.Combine(tmpDir, "extract");
                ZipFile.ExtractToDirectory(zipPath, extractDir);

                var found = Directory.EnumerateFiles(extractDir, exeName,
                    SearchOption.AllDirectories).FirstOrDefault();
                if (found == null)
                {
                    throw new InvalidOperationException(
                        exeName + " not found inside " + assetName);
                }
                File.Copy(found, targetPath, true);
            }
            finally
            {
                try { Directory.Delete(tmpDir, true); } catch { }
            }
        }

        static void DownloadTo(HttpClient http, string url, string targetPath)
        {
            using (var resp = http.GetAsync(url).GetAwaiter().GetResult())
            {
                resp.EnsureSuccessStatusCode();
                using (var fs = File.Create(targetPath))
                {
                    resp.Content.CopyToAsync(fs).GetAwaiter().GetResult();
                }
            }
        }

        static string ToHex(byte[] bytes)
        {
            var sb = new StringBuilder(bytes.Length * 2);
            for (int i = 0; i < bytes.Length; i++) sb.Append(bytes[i].ToString("x2"));
            return sb.ToString();
        }
    }
}
