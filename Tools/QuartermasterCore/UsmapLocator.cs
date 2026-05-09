using System;
using System.IO;
using System.Linq;

namespace Windrose.Quartermaster.Core
{
    // Locates a UE5 mappings file (.usmap) in the mod root.
    // CUE4Parse needs this to deserialize unversioned property layouts on
    // Windrose builds (UE5.6 + R5BusinessRules plugin). Generate one by
    // pressing Ctrl+Num6 in-game with UE4SS' Keybinds mod active -- the
    // dumper writes a file like `R5-5.6.1-0+UE5-<hash>.usmap`. Drop it
    // into the mod root.
    //
    // When multiple .usmap files are present, the most recently modified
    // one wins -- after a game update you just dump a fresh one and ignore
    // the older copies.
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
            {
                throw new InvalidOperationException(
                    "No *.usmap file found in " + modRoot + ".\n\n" +
                    "Generate one with UE4SS' built-in dumper:\n" +
                    "  1. Start Windrose, load a save, walk around for 5-10 seconds.\n" +
                    "  2. Press Ctrl+Num6 (UE4SS Keybinds mod -> DumpUSMAP).\n" +
                    "  3. Copy the produced .usmap (typically next to UE4SS.exe under\n" +
                    "     <Game>\\R5\\Binaries\\Win64\\ue4ss\\) into " + modRoot + ".");
            }
            return newest.FullName;
        }

        // Non-throwing variant for the setup-status check.
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
