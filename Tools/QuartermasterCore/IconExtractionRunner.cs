using System;
using System.IO;
using System.Linq;

namespace Windrose.Quartermaster.Core
{
    // Runs the full icon-extraction pipeline:
    //   1. Resolve all the moving pieces (Sources/, Icons/, paks dir,
    //      .usmap, AES key).
    //   2. Walk Sources and build the manifest JSON
    //      (delegated to IconManifestBuilder).
    //   3. Invoke the IconExtractor library in-process with the manifest.
    //   4. Clean up the temp manifest, report stats.
    //
    // Replaces Library/Icons.ps1 + Extract-Icons.ps1.
    public sealed class IconExtractionRunner
    {
        readonly WindrosePaths _paths;

        public IconExtractionRunner(WindrosePaths paths)
        {
            if (paths == null) throw new ArgumentNullException("paths");
            _paths = paths;
        }

        public Action<string> Log;

        // Optional explicit overrides; null/empty values are auto-resolved.
        public string SourceDirOverride;
        public string OutDirOverride;
        public string PaksDirOverride;
        public string UsmapOverride;
        public string GameVersion = "UE5_6";

        public IconExtractionResult Run()
        {
            // --- 1. Resolve paths ------------------------------------------
            var sourceDir = !string.IsNullOrEmpty(SourceDirOverride)
                ? Path.GetFullPath(SourceDirOverride)
                : _paths.Vanilla;
            if (!Directory.Exists(sourceDir))
            {
                throw new DirectoryNotFoundException(
                    "Source folder not found: " + sourceDir +
                    "\n\nRun the dump step first to extract the vanilla JSONs.");
            }
            LogLine("Source:    " + sourceDir);

            var outDir = !string.IsNullOrEmpty(OutDirOverride)
                ? Path.GetFullPath(OutDirOverride)
                : Path.Combine(_paths.ModRoot, "Icons");
            Directory.CreateDirectory(outDir);
            LogLine("OutDir:    " + outDir);

            var paksDir = !string.IsNullOrEmpty(PaksDirOverride)
                ? Path.GetFullPath(PaksDirOverride)
                : SteamLocator.FindVanillaPaksDir();
            if (!Directory.Exists(paksDir))
            {
                throw new DirectoryNotFoundException("Paks dir not found: " + paksDir);
            }
            LogLine("PaksDir:   " + paksDir);

            var usmap = !string.IsNullOrEmpty(UsmapOverride)
                ? Path.GetFullPath(UsmapOverride)
                : UsmapLocator.Find(_paths.ModRoot);
            if (!File.Exists(usmap))
            {
                throw new FileNotFoundException("Usmap not found: " + usmap);
            }
            LogLine("Usmap:     " + usmap);

            // --- 2. Build manifest ----------------------------------------
            LogLine("Scanning JSONs for ItemTexture paths");
            var manifest = new IconManifestBuilder { Log = Log }.Build(sourceDir);

            var manifestPath = new IconManifestBuilder().WriteToTempFile(manifest.Entries);
            try
            {
                // --- 3. Invoke IconExtractor (in-process) -----------------
                LogLine("Running IconExtractor");
                // Hide the AES key in the displayed banner so logs are safe to share.
                LogLine("IconExtractor (in-process) --paks-dir \"" + paksDir + "\" --aes-key <hidden>" +
                        " --manifest \"" + manifestPath + "\" --out-dir \"" + outDir + "\"" +
                        " --usmap \"" + usmap + "\" --game-version " + GameVersion);

                Windrose.IconExtractor.IconExtractor.Run(
                    new Windrose.IconExtractor.IconExtractorOptions
                    {
                        PaksDir      = paksDir,
                        AesKey       = WindroseGameSecrets.AesKey,
                        ManifestPath = manifestPath,
                        OutDir       = outDir,
                        UsmapPath    = usmap,
                        GameVersion  = GameVersion,
                    },
                    Log);
            }
            finally
            {
                try { File.Delete(manifestPath); } catch { /* best effort */ }
            }

            return Statistics(outDir);
        }

        IconExtractionResult Statistics(string outDir)
        {
            var pngs  = Directory.EnumerateFiles(outDir, "*.png",  SearchOption.TopDirectoryOnly).ToList();
            var jsons = Directory.EnumerateFiles(outDir, "*.json", SearchOption.TopDirectoryOnly).ToList();
            long pngBytes  = 0; foreach (var p in pngs)  pngBytes  += new FileInfo(p).Length;
            long metaBytes = 0; foreach (var p in jsons) metaBytes += new FileInfo(p).Length;
            LogLine(pngs.Count + " PNG files written (" + (pngBytes / 1024.0).ToString("0.0") + " KB total)");
            if (jsons.Count > 0)
            {
                LogLine(jsons.Count + " metadata sidecars written (" +
                        (metaBytes / 1024.0).ToString("0.0") + " KB total)");
            }
            return new IconExtractionResult
            {
                OutDir = outDir,
                PngCount = pngs.Count,
                MetadataCount = jsons.Count,
                PngBytes = pngBytes,
                MetadataBytes = metaBytes,
            };
        }

        void LogLine(string msg)
        {
            if (Log != null) Log(msg);
        }
    }

    public sealed class IconExtractionResult
    {
        public string OutDir;
        public int PngCount;
        public int MetadataCount;
        public long PngBytes;
        public long MetadataBytes;
    }
}
