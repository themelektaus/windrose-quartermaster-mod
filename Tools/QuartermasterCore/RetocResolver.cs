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
    // Resolves retoc.exe in the mod root. Downloads and SHA256-verifies a
    // pinned release on first use; subsequent calls just return the cached
    // path. Same shape as RepakResolver -- the two tools are independent
    // (repak handles Pak1, retoc handles UE5 IoStore .ucas/.utoc), but the
    // download / pin / verify story is identical so the implementations
    // mirror each other line-for-line.
    //
    // Why retoc: UE5 ships Blueprint assets via IoStore (.ucas/.utoc), not
    // Pak1. We can't shadow them via a loose .uasset in a Pak1 -- the engine
    // looks them up through the IoStore reader, which expects Zen-Package
    // bytes (a different format from the Pak1 Legacy-Asset format). retoc
    // is the toolchain for the round-trip Zen <-> Legacy and for packing a
    // proper IoStore mod triplet.
    //
    // SHA256 verification protects against download corruption / mirror
    // glitches, not against active tampering -- there's no signature check
    // on the .sha256 sidecar itself.
    public sealed class RetocResolver
    {
        public const string PinnedVersion = "0.1.5";
        public const string AssetName = "retoc_cli-x86_64-pc-windows-msvc.zip";

        readonly string _modRoot;

        public RetocResolver(string modRoot)
        {
            if (string.IsNullOrEmpty(modRoot)) throw new ArgumentNullException("modRoot");
            _modRoot = modRoot;
        }

        // Hook the GUI / CLI can wire up to surface progress. Single-line
        // status messages, fired sparingly.
        public Action<string> Log;

        public string Resolve()
        {
            var retocExe = Path.Combine(_modRoot, "retoc.exe");
            if (File.Exists(retocExe))
            {
                return retocExe;
            }

            LogLine("retoc.exe not present -- downloading v" + PinnedVersion);
            Directory.CreateDirectory(_modRoot);
            Download(retocExe);
            LogLine("Installed: " + retocExe + " (retoc v" + PinnedVersion + ")");
            return retocExe;
        }

        void Download(string targetPath)
        {
            var url = "https://github.com/trumank/retoc/releases/download/v"
                      + PinnedVersion + "/" + AssetName;
            var tmpDir = Path.Combine(Path.GetTempPath(),
                "windrose-retoc-" + Guid.NewGuid().ToString("N"));
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

                var found = Directory.EnumerateFiles(extractDir, "retoc.exe",
                    SearchOption.AllDirectories).FirstOrDefault();
                if (found == null)
                {
                    throw new InvalidOperationException(
                        "retoc.exe not found inside " + AssetName);
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
