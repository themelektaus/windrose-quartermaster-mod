using System;
using System.IO;
using System.Linq;

namespace Windrose.Quartermaster.Core
{
    public sealed class VanillaConfigExtractor
    {
        readonly WindrosePaths _paths;
        readonly RepakResolver _repakResolver;

        public VanillaConfigExtractor(WindrosePaths paths)
        {
            if (paths == null) throw new ArgumentNullException("paths");
            _paths = paths;
            _repakResolver = new RepakResolver(paths.ModRoot);
        }

        public Action<string> Log;

        public string EnsureMapSettings()
        {
            return EnsureFile(
                vanillaRelPath: "R5/Config/DefaultR5MapSettings.ini",
                includePrefix:  "R5/Config/DefaultR5MapSettings.ini");
        }

        public string EnsureBuildingSettings()
        {
            return EnsureFile(
                vanillaRelPath: "R5/Config/DefaultR5BuildingSettings.ini",
                includePrefix:  "R5/Config/DefaultR5BuildingSettings.ini");
        }

        // The single DA_HeroLevels.json (player level-up reward table). Returns the
        // cached path (== _paths.VanillaHeroLevels) extracting on a cache miss.
        public string EnsureHeroLevels()
        {
            return EnsureFile(
                vanillaRelPath: WindroseGameSecrets.HeroLevelsRelPath,
                includePrefix:  WindroseGameSecrets.HeroLevelsRelPath);
        }

        // Directory variant of EnsureFile: guarantees a vanilla subtree is present
        // on disk, extracting `includePrefix` from the vanilla pak on a cache miss.
        // `vanillaDir` is the cached absolute dir (e.g. paths.VanillaQuestRewards).
        // A directory that exists but holds no *.json is treated as a miss and
        // re-extracted (handles a half-populated cache). Returns `vanillaDir`.
        public string EnsureDirectory(string vanillaDir, string includePrefix)
        {
            if (string.IsNullOrEmpty(vanillaDir))
                throw new ArgumentNullException("vanillaDir");
            if (string.IsNullOrEmpty(includePrefix))
                throw new ArgumentNullException("includePrefix");

            bool hasJson = Directory.Exists(vanillaDir)
                && Directory.EnumerateFiles(vanillaDir, "*.json", SearchOption.AllDirectories).Any();
            if (hasJson)
            {
                return vanillaDir;
            }

            LogLine("VanillaConfig: cache miss for " + includePrefix
                    + " - extracting from vanilla pak");
            var vanillaPak = SteamLocator.FindVanillaPak();
            if (!File.Exists(vanillaPak))
            {
                throw new FileNotFoundException(
                    "Vanilla pak not found at " + vanillaPak
                    + " - reinstall the game or pass an explicit pak path.");
            }

            _repakResolver.Log = Log;
            var repakExe = _repakResolver.Resolve();

            Directory.CreateDirectory(_paths.Vanilla);

            LogLine("repak --aes-key <hidden> unpack -i " + includePrefix
                    + " -o \"" + _paths.Vanilla + "\" -f \"" + vanillaPak + "\"");

            var r = ToolProcess.RunCapture(repakExe, new[]
            {
                "--aes-key", WindroseGameSecrets.AesKey,
                "unpack",
                "-i", includePrefix,
                "-o", _paths.Vanilla,
                "-f",
                vanillaPak,
            });
            if (r.ExitCode != 0)
            {
                throw new InvalidOperationException(
                    "repak unpack failed (exit " + r.ExitCode + ") while "
                    + "extracting " + includePrefix + ":\n" + r.ErrOrOut);
            }

            if (!Directory.Exists(vanillaDir)
                || !Directory.EnumerateFiles(vanillaDir, "*.json", SearchOption.AllDirectories).Any())
            {
                throw new InvalidOperationException(
                    "repak unpack reported success but " + vanillaDir
                    + " holds no .json - in-pak path may have moved "
                    + "(used includePrefix='" + includePrefix + "').");
            }

            int n = Directory.EnumerateFiles(vanillaDir, "*.json", SearchOption.AllDirectories).Count();
            LogLine("VanillaConfig: cached " + includePrefix + " (" + n + " json)");
            return vanillaDir;
        }

        public string EnsureFile(string vanillaRelPath, string includePrefix)
        {
            if (string.IsNullOrEmpty(vanillaRelPath))
                throw new ArgumentNullException("vanillaRelPath");
            if (string.IsNullOrEmpty(includePrefix))
                throw new ArgumentNullException("includePrefix");

            var cachedPath = Path.Combine(_paths.Vanilla,
                vanillaRelPath.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(cachedPath))
            {
                return cachedPath;
            }

            LogLine("VanillaConfig: cache miss for " + vanillaRelPath
                    + " - extracting from vanilla pak");
            var vanillaPak = SteamLocator.FindVanillaPak();
            if (!File.Exists(vanillaPak))
            {
                throw new FileNotFoundException(
                    "Vanilla pak not found at " + vanillaPak
                    + " - reinstall the game or pass an explicit pak path.");
            }

            _repakResolver.Log = Log;
            var repakExe = _repakResolver.Resolve();

            Directory.CreateDirectory(_paths.Vanilla);

            LogLine("repak --aes-key <hidden> unpack -i " + includePrefix
                    + " -o \"" + _paths.Vanilla + "\" -f \"" + vanillaPak + "\"");

            var r = ToolProcess.RunCapture(repakExe, new[]
            {
                "--aes-key", WindroseGameSecrets.AesKey,
                "unpack",
                "-i", includePrefix,
                "-o", _paths.Vanilla,
                "-f",
                vanillaPak,
            });
            if (r.ExitCode != 0)
            {
                throw new InvalidOperationException(
                    "repak unpack failed (exit " + r.ExitCode + ") while "
                    + "extracting " + vanillaRelPath + ":\n" + r.ErrOrOut);
            }

            if (!File.Exists(cachedPath))
            {
                throw new InvalidOperationException(
                    "repak unpack reported success but " + cachedPath
                    + " was not produced - in-pak path may have moved "
                    + "(used includePrefix='" + includePrefix + "').");
            }

            LogLine("VanillaConfig: cached " + vanillaRelPath
                    + " (" + new FileInfo(cachedPath).Length + " B)");
            return cachedPath;
        }

        void LogLine(string msg)
        {
            if (Log != null) Log(msg);
        }
    }
}
