// BuildingItemExporter: bulk-extracts every pak-internal virtual file whose
// virtual path contains one of the configured substrings to a 1:1 layout
// on disk. Powers the "Export" card in the Mods tab.
//
// Architecture mirrors IconExtractor (Tools/IconExtractor/IconExtractor.cs)
// but extracts raw bytes per virtual file (GameFile.Read()) instead of
// decoding UTexture2D to PNG. We need the raw uasset+uexp+ubulk triplets on
// disk so the user can clone them in the UE editor.
//
// Re-runs are incremental: existing files with matching byte length are
// skipped, so the second click is effectively a no-op once an export is
// complete. To force a full re-extract the user deletes the OutDir manually.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using CUE4Parse.Compression;
using CUE4Parse.Encryption.Aes;
using CUE4Parse.FileProvider;
using CUE4Parse.FileProvider.Objects;
using CUE4Parse.MappingsProvider;
using CUE4Parse.UE4.Objects.Core.Misc;
using CUE4Parse.UE4.Versions;

namespace Windrose.Quartermaster.Core
{
    public sealed class BuildingItemExporterOptions
    {
        public string PaksDir = string.Empty;
        public string AesKey = string.Empty;
        public string OutDir = string.Empty;
        public string UsmapPath = string.Empty;
        public string GameVersion = "UE5_6";

        // Pak-relative path substrings; any virtual file whose path contains
        // one of these (case-insensitive) gets extracted. Matching is
        // substring-based so we don't have to know the exact pak-internal
        // prefix convention ("Game/Content/" vs "R5/Content/" vs leading slash).
        public List<string> IncludeSubstrings = new List<string>();
    }

    public sealed class BuildingItemExportResult
    {
        public int FilesMatched;
        public int FilesWritten;
        public int FilesSkippedExisting;
        public int FilesFailed;
        public long TotalBytesWritten;
        public string OutDir = string.Empty;
    }

    public static class BuildingItemExporter
    {
        [ThreadStatic] static Action<string> _log;
        static void Out(string msg) { if (_log != null) _log(msg); }

        public static BuildingItemExportResult Run(
            BuildingItemExporterOptions opts,
            Action<string> log)
        {
            if (opts == null) throw new ArgumentNullException("opts");
            ValidateOptions(opts);

            _log = log;
            try
            {
                return RunCore(opts);
            }
            finally
            {
                _log = null;
            }
        }

        static void ValidateOptions(BuildingItemExporterOptions o)
        {
            if (string.IsNullOrWhiteSpace(o.PaksDir))   throw new ArgumentException("PaksDir is required");
            if (string.IsNullOrWhiteSpace(o.AesKey))    throw new ArgumentException("AesKey is required");
            if (string.IsNullOrWhiteSpace(o.OutDir))    throw new ArgumentException("OutDir is required");
            if (string.IsNullOrWhiteSpace(o.UsmapPath)) throw new ArgumentException("UsmapPath is required");
            if (!Directory.Exists(o.PaksDir))           throw new ArgumentException("PaksDir not found: " + o.PaksDir);
            if (!File.Exists(o.UsmapPath))              throw new ArgumentException("Usmap not found: " + o.UsmapPath);
            if (o.IncludeSubstrings == null || o.IncludeSubstrings.Count == 0)
                throw new ArgumentException("IncludeSubstrings must contain at least one entry");
        }

        static BuildingItemExportResult RunCore(BuildingItemExporterOptions a)
        {
            Directory.CreateDirectory(a.OutDir);

            EnsureOodle();

            Out("[..] Initializing CUE4Parse provider (" + a.GameVersion + ")");
            Out("     PaksDir: " + a.PaksDir);
            Out("     OutDir:  " + a.OutDir);

            var version = ParseGameVersion(a.GameVersion);
            var provider = new DefaultFileProvider(
                a.PaksDir,
                SearchOption.TopDirectoryOnly,
                new VersionContainer(version));
            provider.MappingsContainer = new FileUsmapTypeMappingsProvider(a.UsmapPath);
            Out("[OK] Usmap loaded");
            provider.Initialize();

            // Submit the AES key for the zero-guid (default) and every
            // distinct GUID present on the unloaded readers (UE5 IoStore
            // can assign different GUIDs per .utoc even with the same key).
            var aes = new FAesKey(a.AesKey);
            var seenGuids = new HashSet<FGuid> { new FGuid() };
            foreach (var v in provider.UnloadedVfs) seenGuids.Add(v.EncryptionKeyGuid);
            foreach (var g in seenGuids) provider.SubmitKey(g, aes);

            var mounted = provider.Mount();
            Out("[OK] Provider ready: " + provider.Files.Count + " virtual files (mounted +" + mounted + ")");

            // Normalize needles to forward-slash style + case-fold prep work.
            var needles = a.IncludeSubstrings
                .Where(s => !string.IsNullOrWhiteSpace(s))
                .Select(s => s.Replace('\\', '/'))
                .ToArray();
            Out("[..] Filter substrings (" + needles.Length + "):");
            foreach (var n in needles) Out("       " + n);

            // Bucket matches per needle so the progress log stays informative.
            var perNeedle = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var n in needles) perNeedle[n] = 0;

            var matchedFiles = new List<KeyValuePair<string, GameFile>>();
            foreach (var kv in provider.Files)
            {
                var key = kv.Key.Replace('\\', '/');
                foreach (var n in needles)
                {
                    if (key.IndexOf(n, StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        matchedFiles.Add(kv);
                        perNeedle[n]++;
                        break;
                    }
                }
            }
            Out("[OK] Matched " + matchedFiles.Count + " file(s) total:");
            foreach (var kvp in perNeedle)
                Out("       " + kvp.Key + " -> " + kvp.Value);

            int written = 0, skipped = 0, failed = 0;
            long totalBytes = 0;

            // ~20 progress lines across the whole run so the log doesn't
            // drown the user but they can tell the export is alive.
            int progressEvery = Math.Max(1, matchedFiles.Count / 20);
            int processed = 0;
            foreach (var kv in matchedFiles)
            {
                var key = kv.Key.Replace('\\', '/');
                var outPath = Path.Combine(a.OutDir, key.Replace('/', Path.DirectorySeparatorChar));
                try
                {
                    if (File.Exists(outPath))
                    {
                        // Incremental: trust on-disk file if size matches.
                        // Cheap soft-validity check; users wanting a full
                        // re-extract delete the OutDir manually.
                        var fi = new FileInfo(outPath);
                        if (fi.Length == kv.Value.Size)
                        {
                            skipped++;
                            processed++;
                            if (processed % progressEvery == 0)
                                Out("     " + processed + "/" + matchedFiles.Count + "  written=" + written + " skipped=" + skipped);
                            continue;
                        }
                    }

                    Directory.CreateDirectory(Path.GetDirectoryName(outPath));
                    var bytes = kv.Value.Read();
                    File.WriteAllBytes(outPath, bytes);
                    written++;
                    totalBytes += bytes.Length;
                }
                catch (Exception ex)
                {
                    failed++;
                    Out("[X]  " + key + ": " + ex.GetType().Name + ": " + ex.Message);
                }

                processed++;
                if (processed % progressEvery == 0)
                    Out("     " + processed + "/" + matchedFiles.Count + "  written=" + written + " skipped=" + skipped);
            }

            Out("");
            Out("[OK] Done: " + written + " written, " + skipped + " skipped (already on disk), " + failed + " failed");
            Out("     Total bytes written: " + FormatBytes(totalBytes));

            return new BuildingItemExportResult
            {
                FilesMatched = matchedFiles.Count,
                FilesWritten = written,
                FilesSkippedExisting = skipped,
                FilesFailed = failed,
                TotalBytesWritten = totalBytes,
                OutDir = a.OutDir,
            };
        }

        // Mirror of IconExtractor.EnsureOodle - duplicated here so an Export
        // run on a fresh install (where the user never opened the Items tab
        // and thus IconExtractor never primed Oodle) still works end-to-end.
        // OodleHelper.Initialize is idempotent so calling it twice is fine.
        static void EnsureOodle()
        {
            var here = AppContext.BaseDirectory;
            var dllPath = Path.Combine(here, OodleHelper.OodleFileName);
            if (!File.Exists(dllPath))
            {
                Out("[..] Downloading Oodle DLL (" + OodleHelper.OodleFileName + ")");
                using (var http = new HttpClient { Timeout = TimeSpan.FromMinutes(2) })
                {
                    var ok = OodleHelper.DownloadOodleDllFromOodleUEAsync(http, dllPath)
                        .GetAwaiter().GetResult();
                    if (!ok || !File.Exists(dllPath))
                        throw new InvalidOperationException("Failed to download Oodle DLL from OodleUE.");
                }
                Out("[OK] Oodle DLL: " + dllPath);
            }
            OodleHelper.Initialize(dllPath);
        }

        static EGame ParseGameVersion(string raw)
        {
            var s = raw.Trim().ToUpperInvariant();
            if (s.StartsWith("GAME_")) s = s.Substring(5);
            if (s.StartsWith("UE")) s = "GAME_" + s;
            else if (s.Length > 0 && char.IsDigit(s[0])) s = "GAME_UE" + s.Replace('.', '_');
            else s = "GAME_" + s;
            if (Enum.TryParse<EGame>(s, ignoreCase: true, out var v)) return v;
            throw new ArgumentException("Unknown EGame value: " + raw);
        }

        static string FormatBytes(long n)
        {
            const double KB = 1024;
            const double MB = KB * 1024;
            const double GB = MB * 1024;
            if (n >= GB) return (n / GB).ToString("0.00") + " GB";
            if (n >= MB) return (n / MB).ToString("0.00") + " MB";
            if (n >= KB) return (n / KB).ToString("0.00") + " KB";
            return n + " B";
        }
    }
}
