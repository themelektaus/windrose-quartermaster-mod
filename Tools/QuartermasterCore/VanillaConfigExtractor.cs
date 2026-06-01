using System;
using System.IO;

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
