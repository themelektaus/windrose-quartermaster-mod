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
    // Resolves repak.exe in the mod root. Downloads and SHA256-verifies a
    // pinned release on first use; subsequent calls just return the cached
    // path. Mirrors Library/Common.ps1's Get-RepakExe byte-for-byte (same
    // version pin, same release URL, same hash check).
    //
    // SHA256 verification protects against download corruption / mirror
    // glitches, not against active tampering - there's no signature check
    // on the .sha256 sidecar itself.
    public sealed class RepakResolver
    {
        public const string PinnedVersion = "0.2.3";
        public const string AssetName = "repak_cli-x86_64-pc-windows-msvc.zip";

        readonly string _modRoot;

        public RepakResolver(string modRoot)
        {
            if (string.IsNullOrEmpty(modRoot)) throw new ArgumentNullException("modRoot");
            _modRoot = modRoot;
        }

        // Hook the GUI / CLI can wire up to surface progress. Single-line
        // status messages, fired sparingly.
        public Action<string> Log;

        public string Resolve()
        {
            var repakExe = Path.Combine(_modRoot, "repak.exe");
            if (File.Exists(repakExe))
            {
                return repakExe;
            }

            LogLine("repak.exe not present - downloading v" + PinnedVersion);
            Directory.CreateDirectory(_modRoot);
            Download(repakExe);
            LogLine("Installed: " + repakExe + " (repak v" + PinnedVersion + ")");
            return repakExe;
        }

        void Download(string targetPath)
        {
            var url = "https://github.com/trumank/repak/releases/download/v"
                      + PinnedVersion + "/" + AssetName;
            var tmpDir = Path.Combine(Path.GetTempPath(),
                "windrose-repak-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tmpDir);

            try
            {
                LogLine("URL: " + url);
                var zipPath = Path.Combine(tmpDir, AssetName);
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
                        "SHA256 mismatch for " + AssetName + ".\n" +
                        "  Expected: " + expected + "\n" +
                        "  Actual:   " + actual);
                }
                LogLine("SHA256 verified");

                var extractDir = Path.Combine(tmpDir, "extract");
                ZipFile.ExtractToDirectory(zipPath, extractDir);

                var found = Directory.EnumerateFiles(extractDir, "repak.exe",
                    SearchOption.AllDirectories).FirstOrDefault();
                if (found == null)
                {
                    throw new InvalidOperationException(
                        "repak.exe not found inside " + AssetName);
                }
                File.Copy(found, targetPath, true);
            }
            finally
            {
                try { Directory.Delete(tmpDir, true); } catch { /* best effort */ }
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

        void LogLine(string msg)
        {
            if (Log != null) Log(msg);
        }
    }
}
