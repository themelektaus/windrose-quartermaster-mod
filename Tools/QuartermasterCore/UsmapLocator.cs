using System;
using System.IO;
using System.Linq;

namespace Windrose.Quartermaster.Core
{
    public static class UsmapLocator
    {
        public static string Find(string modRoot)
        {
            if (string.IsNullOrEmpty(modRoot)) throw new ArgumentNullException("modRoot");
            if (!Directory.Exists(modRoot))
            {
                throw new InvalidOperationException(
                    "Mod root does not exist: " + modRoot);
            }

            var newest = Directory.EnumerateFiles(modRoot, "*.usmap", SearchOption.TopDirectoryOnly)
                .Select(p => new FileInfo(p))
                .OrderByDescending(f => f.LastWriteTimeUtc)
                .FirstOrDefault();

            if (newest == null)
                throw new InvalidOperationException(MissingMessage(modRoot));
            return newest.FullName;
        }

        public static string MissingMessage(string modRoot)
        {
            if (WindrosePaths.IsDevRepoRoot(modRoot))
            {
                return
                    "No *.usmap file found in " + modRoot + ".\n\n" +
                    "Generate one with Dumper-7:\n" +
                    "  1. Start Windrose via Steam, load a save, walk around for 5-10 seconds.\n" +
                    "  2. Run Tools\\Dumper7Setup\\run_dump.bat (press F8 to dump, F6 to unload).\n" +
                    "  3. Copy the produced .usmap from Tools\\Dumper7Setup\\output\\ into " + modRoot + ".";
            }
            return
                "Type mappings (.usmap) are missing from " + modRoot + ".\n\n" +
                "This file ships with Quartermaster and is restored automatically on launch - " +
                "restart the app to recreate it. If it keeps failing, place a current .usmap into the folder above.";
        }

        public static bool TryFind(string modRoot, out string path)
        {
            path = null;
            if (string.IsNullOrEmpty(modRoot) || !Directory.Exists(modRoot)) return false;
            var newest = Directory.EnumerateFiles(modRoot, "*.usmap", SearchOption.TopDirectoryOnly)
                .Select(p => new FileInfo(p))
                .OrderByDescending(f => f.LastWriteTimeUtc)
                .FirstOrDefault();
            if (newest == null) return false;
            path = newest.FullName;
            return true;
        }
    }
}
