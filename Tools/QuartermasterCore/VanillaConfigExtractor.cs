using System;
using System.Diagnostics;
using System.IO;

namespace Windrose.Quartermaster.Core
{
    // Lazy on-demand extractor for vanilla R5/Config/*.ini files.
    //
    // Some IoStore-only features (the Minimap-range patch is the first
    // user of this) need to read a vanilla config file as a baseline,
    // then ship a modified copy as a loose file inside a repak .pak.
    // The configs are stored AES-encrypted inside pakchunk0-Windows.pak;
    // we extract them once into <ModRoot>/Sources/Vanilla/R5/Config/ on
    // first build and reuse the cached copy on subsequent runs.
    //
    // The cache path is the same Sources/Vanilla/ tree VanillaDumper uses
    // for InventoryItems/LootTables/BuildingLimits - one canonical place
    // for all extracted vanilla baselines. Different prefix subtree so
    // the two extractors never collide.
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

        // Returns the cached path to vanilla R5/Config/DefaultR5MapSettings.ini.
        // Performs a lazy AES-key + repak unpack on first call (when the
        // cached file is missing); subsequent calls return immediately.
        //
        // The minimap-range feature reads this file, scales four float
        // values inside its single +MapsConfig=(...) tuple, and writes
        // the result into the minimap pak's staging dir.
        public string EnsureMapSettings()
        {
            // R5/Config/DefaultR5MapSettings.ini is the file the game
            // engine reads at startup for R5MapSettings; the per-platform
            // *R5MapSettings.ini overrides don't carry the MapsConfig
            // array we want to scale.
            return EnsureFile(
                vanillaRelPath: "R5/Config/DefaultR5MapSettings.ini",
                includePrefix:  "R5/Config/DefaultR5MapSettings.ini");
        }

        // Generic helper - if other features ever need another config
        // file, they can call this directly with their own paths.
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

            // repak's -i prefix filter is applied to in-pak entries.
            // Passing the FULL path of the single file we want gives us
            // exactly one extracted file (verified empirically with
            // DefaultR5MapSettings.ini - extracts 1 file under
            // <outDir>/R5/Config/DefaultR5MapSettings.ini).
            Directory.CreateDirectory(_paths.Vanilla);

            var psi = new ProcessStartInfo
            {
                FileName = repakExe,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            psi.ArgumentList.Add("--aes-key");
            psi.ArgumentList.Add(WindroseGameSecrets.AesKey);
            psi.ArgumentList.Add("unpack");
            psi.ArgumentList.Add("-i");
            psi.ArgumentList.Add(includePrefix);
            psi.ArgumentList.Add("-o");
            psi.ArgumentList.Add(_paths.Vanilla);
            psi.ArgumentList.Add("-f");
            psi.ArgumentList.Add(vanillaPak);

            LogLine("repak --aes-key <hidden> unpack -i " + includePrefix
                    + " -o \"" + _paths.Vanilla + "\" -f \"" + vanillaPak + "\"");

            WineHelper.ApplyWine(psi);
            var proc = Process.Start(psi);
            var stdout = proc.StandardOutput.ReadToEnd();
            var stderr = proc.StandardError.ReadToEnd();
            proc.WaitForExit();
            if (proc.ExitCode != 0)
            {
                throw new InvalidOperationException(
                    "repak unpack failed (exit " + proc.ExitCode + ") while "
                    + "extracting " + vanillaRelPath + ":\n"
                    + (string.IsNullOrEmpty(stderr) ? stdout : stderr));
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
