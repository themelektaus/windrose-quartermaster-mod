using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32;

namespace Windrose.Quartermaster.Core
{
    public static class SteamLocator
    {
        // Priority order: client install ships the Windows variant, dedicated server the WindowsServer variant.
        public static readonly string[] VanillaPakNames =
        {
            "pakchunk0-Windows.pak",
            "pakchunk0-WindowsServer.pak",
        };

        public static string FindSteamInstallPath()
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                return ReadSteamRegistry();
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
                return ReadSteamLinux();
            return null;
        }

        static string ReadSteamLinux()
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var steamDot = Path.Combine(home, ".steam");

            var steam = Path.Combine(steamDot, "steam");
            if (Directory.Exists(Path.Combine(steam, "steamapps"))) return steam;

            var standard = Path.Combine(home, ".local", "share", "Steam");
            if (Directory.Exists(Path.Combine(standard, "steamapps"))) return standard;

            var flatpak = Path.Combine(home, ".var", "app",
                "com.valvesoftware.Steam", ".local", "share", "Steam");
            if (Directory.Exists(Path.Combine(flatpak, "steamapps"))) return flatpak;

            return null;
        }

        [SupportedOSPlatform("windows")]
        static string ReadSteamRegistry()
        {
            using (var hkcu = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam"))
            {
                if (hkcu != null)
                {
                    var p = hkcu.GetValue("SteamPath") as string;
                    if (!string.IsNullOrEmpty(p)) return p.Replace('/', '\\');
                }
            }
            // Registry32: Steam writes the machine-wide key under WOW6432Node.
            using (var hklm = RegistryKey
                .OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry32)
                .OpenSubKey(@"SOFTWARE\Valve\Steam"))
            {
                if (hklm != null)
                {
                    var p = hklm.GetValue("InstallPath") as string;
                    if (!string.IsNullOrEmpty(p)) return p.Replace('/', '\\');
                }
            }
            return null;
        }

        public static List<string> FindLibraryPaths(string steamPath)
        {
            var libs = new List<string>();
            if (string.IsNullOrEmpty(steamPath)) return libs;
            libs.Add(steamPath);

            var vdf = Path.Combine(steamPath, "steamapps", "libraryfolders.vdf");
            if (!File.Exists(vdf)) return libs;

            foreach (var rawLine in File.ReadAllLines(vdf))
            {
                var line = rawLine.Trim();
                if (!line.StartsWith("\"path\"", StringComparison.Ordinal)) continue;
                var idx = line.IndexOf('"', 6);
                if (idx < 0) continue;
                var end = line.IndexOf('"', idx + 1);
                if (end < 0) continue;
                var p = line.Substring(idx + 1, end - idx - 1).Replace("\\\\", "\\");
                if (!libs.Contains(p)) libs.Add(p);
            }
            return libs;
        }

        public static string FindVanillaPak()
        {
            // A stale/typoed override resolves to null and falls through to Steam rather than throwing.
            var fromOverride = GameInstallOverride.TryResolveVanillaPak();
            if (!string.IsNullOrEmpty(fromOverride)) return fromOverride;

            var overrideGameRoot = GameInstallOverride.GetGameRoot();
            var hasOverride = !string.IsNullOrEmpty(overrideGameRoot);

            var steam = FindSteamInstallPath();
            if (string.IsNullOrEmpty(steam))
            {
                var hint = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
                    ? "no SteamPath in HKCU and no InstallPath in HKLM\\SOFTWARE\\WOW6432Node\\Valve\\Steam"
                    : "checked ~/.steam/steam, ~/.local/share/Steam and the Flatpak path";
                var msg = $"Could not locate the Steam install ({hint}). " +
                          "Pass an explicit pak path to override.";
                if (hasOverride)
                    msg = "Configured game install does not contain a Windrose vanilla pak " +
                          "(<gameRoot>\\R5\\Content\\Paks\\): " + overrideGameRoot +
                          "\nSteam auto-detect also failed: " + hint;
                throw new InvalidOperationException(msg);
            }
            var libs = FindLibraryPaths(steam);
            foreach (var lib in libs)
            {
                var paksDir = Path.Combine(lib, "steamapps", "common",
                    "Windrose", "R5", "Content", "Paks");
                if (!Directory.Exists(paksDir)) continue;
                foreach (var name in VanillaPakNames)
                {
                    var candidate = Path.Combine(paksDir, name);
                    if (File.Exists(candidate)) return Path.GetFullPath(candidate);
                }
            }
            var searched = string.Join("\n  ", libs.ConvertAll(l =>
                Path.Combine(l, "steamapps", "common", "Windrose", "R5", "Content", "Paks")));
            var failMsg = "Could not find a Windrose vanilla pak under any Steam library.\n" +
                          "Searched:\n  " + searched + "\n" +
                          "Pass an explicit pak path to override.";
            if (hasOverride)
                failMsg = "Configured game install does not contain a Windrose vanilla pak: " +
                          overrideGameRoot + "\nSteam auto-detect also failed:\n  " + searched;
            throw new InvalidOperationException(failMsg);
        }

        public static string FindVanillaPaksDir()
        {
            return Path.GetDirectoryName(FindVanillaPak());
        }

        public static string FindModsDir()
        {
            var paks = FindVanillaPaksDir();
            var mods = Path.Combine(paks, "~mods");
            Directory.CreateDirectory(mods);
            return mods;
        }

        public static string FindBinariesWin64Dir()
        {
            var paksDir = FindVanillaPaksDir();
            var r5Root = Path.GetDirectoryName(Path.GetDirectoryName(paksDir));
            if (string.IsNullOrEmpty(r5Root))
            {
                throw new InvalidOperationException(
                    "Could not derive R5 root from Paks dir: " + paksDir);
            }
            var bin = Path.Combine(r5Root, "Binaries", "Win64");
            if (!Directory.Exists(bin))
            {
                throw new InvalidOperationException(
                    "Windrose Binaries/Win64 folder missing under R5 root: " + bin
                    + " - the game install looks broken.");
            }
            return bin;
        }
    }
}
