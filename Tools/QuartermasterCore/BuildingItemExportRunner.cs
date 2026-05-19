// Runs BuildingItemExporter with the standard Windrose path layout:
//   PaksDir   = Steam install Paks/ dir (auto-located)
//   Usmap     = newest *.usmap under ModRoot (UsmapLocator)
//   OutDir    = Sources/Vanilla/  (so extracted files land 1:1 next to the
//               existing dumped JSONs from VanillaDumper)
//
// Mirrors IconExtractionRunner: each Run() resolves inputs, invokes the
// extractor in-process, and reports stats. Used by the /api/export endpoint
// in the GUI (SSE-streamed log) and indirectly by anyone shelling out via
// the CLI in the future.

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

        // Optional explicit overrides; null/empty values are auto-resolved.
        public string PaksDirOverride;
        public string UsmapOverride;
        public string OutDirOverride;
        public string GameVersion = "UE5_6";

        // Default scope: every Building-related subtree across the IoStore
        // containers. Substring matching is lenient on the prefix convention
        // ("Game/Content/..." vs "R5/Content/...") so users with a slightly
        // different layout still get matches.
        //
        // Scope rationale:
        //   - Gameplay/Building/     : DA_BI_*, BP_*, FX params, gameplay materials
        //   - Environment/Gameplay/Building/ : SM_*, environment materials, textures
        //   - Audio/Game/Building/   : build/destroy cues, set-piece audio
        // Together this covers everything needed to clone a building item end-
        // to-end in the UE editor (visual + behavior + audio).
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
