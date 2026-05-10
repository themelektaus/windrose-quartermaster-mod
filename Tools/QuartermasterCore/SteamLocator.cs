using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32;

namespace Windrose.Quartermaster.Core
{
    // Resolves the Steam install path and the Windrose vanilla pak by
    // walking libraryfolders.vdf - the same logic Library/Common.ps1
    // used to do via Get-SteamInstallPath / Get-SteamLibraryPaths /
    // Get-WindroseVanillaPak.
    //
    // Windows-only (Steam-on-Linux is a different layout) - we throw a
    // descriptive error on non-Windows and skip the registry lookup. The
    // .NET CA1416 (Platform compatibility) analyzer is suppressed at the
    // call sites because we gate every registry call behind an OS check.
    public static class SteamLocator
    {
        // Pak filenames we accept, in priority order. Steam ships the
        // Windows variant; dedicated server installs ship the
        // WindowsServer variant. Either one contains the encrypted
        // InventoryItems JSONs.
        public static readonly string[] VanillaPakNames =
        {
            "pakchunk0-Windows.pak",
            "pakchunk0-WindowsServer.pak",
        };

        // Returns the Steam install directory (e.g. "C:\Program Files (x86)\Steam"),
        // or null if Steam isn't installed / not on Windows.
        public static string FindSteamInstallPath()
        {
            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                return null;
            }
            return ReadSteamRegistry();
        }

        [SupportedOSPlatform("windows")]
        static string ReadSteamRegistry()
        {
            // Per-user (HKCU) is what Steam writes when launched normally.
            using (var hkcu = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam"))
            {
                if (hkcu != null)
                {
                    var p = hkcu.GetValue("SteamPath") as string;
                    if (!string.IsNullOrEmpty(p)) return p.Replace('/', '\\');
                }
            }
            // Machine-wide 32-bit hive that the Steam installer creates.
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

        // Parses Steam's libraryfolders.vdf and returns every library root.
        // We only care about the "path" entries; full VDF parsing isn't needed.
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
                // matches:    "path"    "C:\\SteamLibrary"
                if (!line.StartsWith("\"path\"", StringComparison.Ordinal)) continue;
                var idx = line.IndexOf('"', 6); // first quote after "path"
                if (idx < 0) continue;
                var end = line.IndexOf('"', idx + 1);
                if (end < 0) continue;
                var p = line.Substring(idx + 1, end - idx - 1).Replace("\\\\", "\\");
                if (!libs.Contains(p)) libs.Add(p);
            }
            return libs;
        }

        // Returns the absolute path to the Windrose vanilla pak by
        // probing every Steam library for
        //   <library>\steamapps\common\Windrose\R5\Content\Paks\<paknames>.
        // Throws a descriptive error when nothing is found so callers can
        // surface it to the user.
        public static string FindVanillaPak()
        {
            var steam = FindSteamInstallPath();
            if (string.IsNullOrEmpty(steam))
            {
                throw new InvalidOperationException(
                    "Could not locate the Steam install (no SteamPath in HKCU and " +
                    "no InstallPath in HKLM\\SOFTWARE\\WOW6432Node\\Valve\\Steam). " +
                    "Pass an explicit pak path to override.");
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
            throw new InvalidOperationException(
                "Could not find a Windrose vanilla pak under any Steam library.\n" +
                "Searched:\n  " + searched + "\n" +
                "Pass an explicit pak path to override.");
        }

        // Convenience: returns the Paks/ directory that contains the vanilla
        // pak (CUE4Parse needs the directory, not the .pak file itself).
        public static string FindVanillaPaksDir()
        {
            return Path.GetDirectoryName(FindVanillaPak());
        }

        // Returns the absolute path to Windrose's user-mods folder
        //   <SteamLib>\steamapps\common\Windrose\R5\Content\Paks\~mods
        // which is where the engine picks up loose .pak files at startup.
        // The folder is created if it does not exist (fresh installs ship
        // without it). Throws via FindVanillaPak when the game itself can't
        // be located so callers can surface a single uniform error.
        public static string FindModsDir()
        {
            var paks = FindVanillaPaksDir();
            var mods = Path.Combine(paks, "~mods");
            Directory.CreateDirectory(mods);
            return mods;
        }
    }
}
