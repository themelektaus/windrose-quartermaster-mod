using System;
using System.Collections.Generic;
using System.IO;

namespace Windrose.Quartermaster.Core
{
    public sealed class BuildingItemExportRunner
    {
        readonly WindrosePaths _paths;

        public BuildingItemExportRunner(WindrosePaths paths)
        {
            if (paths == null) throw new ArgumentNullException("paths");
            _paths = paths;
        }

        public Action<string> Log;

        public string PaksDirOverride;
        public string UsmapOverride;
        public string OutDirOverride;
        public string GameVersion = "UE5_6";

        public List<string> IncludeSubstrings = new List<string>
        {
            "/Gameplay/Building/",
            "/Environment/Gameplay/Building/",
            "/Audio/Game/Building/",
        };

        public BuildingItemExportResult Run()
        {
            var paksDir = !string.IsNullOrEmpty(PaksDirOverride)
                ? Path.GetFullPath(PaksDirOverride)
                : SteamLocator.FindVanillaPaksDir();
            if (!Directory.Exists(paksDir))
                throw new DirectoryNotFoundException("Paks dir not found: " + paksDir);
            LogLine("PaksDir:  " + paksDir);

            var usmap = !string.IsNullOrEmpty(UsmapOverride)
                ? Path.GetFullPath(UsmapOverride)
                : UsmapLocator.Find(_paths.ModRoot);
            if (!File.Exists(usmap))
                throw new FileNotFoundException("Usmap not found: " + usmap);
            LogLine("Usmap:    " + usmap);

            var outDir = !string.IsNullOrEmpty(OutDirOverride)
                ? Path.GetFullPath(OutDirOverride)
                : _paths.Vanilla;
            Directory.CreateDirectory(outDir);
            LogLine("OutDir:   " + outDir);

            var opts = new BuildingItemExporterOptions
            {
                PaksDir = paksDir,
                AesKey = WindroseGameSecrets.AesKey,
                OutDir = outDir,
                UsmapPath = usmap,
                GameVersion = GameVersion,
                IncludeSubstrings = IncludeSubstrings,
            };

            LogLine("BuildingItemExporter (in-process) --paks-dir \"" + paksDir + "\" --aes-key <hidden>" +
                    " --out-dir \"" + outDir + "\" --usmap \"" + usmap + "\" --game-version " + GameVersion);

            return BuildingItemExporter.Run(opts, Log);
        }

        void LogLine(string msg)
        {
            if (Log != null) Log(msg);
        }
    }
}
