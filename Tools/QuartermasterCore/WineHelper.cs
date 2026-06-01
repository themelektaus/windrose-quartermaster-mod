using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;

namespace Windrose.Quartermaster.Core
{
    // Call ApplyWine() on any ProcessStartInfo targeting a .exe before Process.Start.
    static class WineHelper
    {
        static readonly object _lock = new object();
        static bool _resolved;
        static string _resolvedWine;

        public static void ApplyWine(ProcessStartInfo psi)
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return;

            var wine = ResolveWineOrThrow();
            psi.ArgumentList.Insert(0, psi.FileName);
            psi.FileName = wine;
        }

        // Returns null (never throws) when no wine is found, unlike ResolveWineOrThrow.
        public static string TryGetWineBinary()
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return null;
            lock (_lock)
            {
                if (!_resolved)
                {
                    _resolvedWine = DetectWine();
                    _resolved = true;
                }
                return _resolvedWine;
            }
        }

        static string ResolveWineOrThrow()
        {
            lock (_lock)
            {
                if (!_resolved)
                {
                    _resolvedWine = DetectWine();
                    _resolved = true;
                }
                if (_resolvedWine == null) ThrowMissing();
                return _resolvedWine;
            }
        }

        static string DetectWine()
        {
            var pathWine = FindOnPath("wine");
            if (pathWine != null) return pathWine;

            var proton = FindProtonWine();
            if (proton != null) return proton;

            return null;
        }

        static string FindOnPath(string exe)
        {
            var pathEnv = Environment.GetEnvironmentVariable("PATH");
            if (string.IsNullOrEmpty(pathEnv)) return null;
            foreach (var dir in pathEnv.Split(':'))
            {
                if (string.IsNullOrEmpty(dir)) continue;
                try
                {
                    var candidate = Path.Combine(dir, exe);
                    if (File.Exists(candidate)) return candidate;
                }
                catch
                {
                }
            }
            return null;
        }

        static string FindProtonWine()
        {
            try
            {
                var steam = SteamLocator.FindSteamInstallPath();
                if (string.IsNullOrEmpty(steam)) return null;

                var libs = SteamLocator.FindLibraryPaths(steam);
                var candidates = new List<(string Name, string Path)>();
                foreach (var lib in libs)
                {
                    var common = Path.Combine(lib, "steamapps", "common");
                    if (!Directory.Exists(common)) continue;

                    var globs = new[] { "Proton*", "GE-Proton*" };
                    foreach (var glob in globs)
                    {
                        IEnumerable<string> dirs;
                        try { dirs = Directory.EnumerateDirectories(common, glob); }
                        catch { continue; }
                        foreach (var dir in dirs)
                        {
                            var name = Path.GetFileName(dir);
                            var winePath = Path.Combine(dir, "files", "bin", "wine");
                            if (!File.Exists(winePath))
                                winePath = Path.Combine(dir, "dist", "bin", "wine");
                            if (File.Exists(winePath))
                                candidates.Add((name, winePath));
                        }
                    }
                }

                if (candidates.Count == 0) return null;

                candidates.Sort((a, b) => string.CompareOrdinal(b.Name, a.Name));
                return candidates[0].Path;
            }
            catch
            {
                return null;
            }
        }

        static void ThrowMissing()
        {
            throw new InvalidOperationException(
                "Could not find a 'wine' binary. Quartermaster needs Wine to run " +
                "Windows-only tools (repak.exe, retoc.exe) on Linux.\n" +
                "Options:\n" +
                "  - Steam Deck: open Discover (Desktop Mode), install 'WINE', then make " +
                "the 'wine' command available on PATH (e.g. via a symlink in ~/.local/bin).\n" +
                "  - Arch/SteamOS: 'sudo steamos-readonly disable && sudo pacman -S wine && " +
                "sudo steamos-readonly enable'.\n" +
                "  - Or install any Proton version through Steam - Quartermaster will " +
                "auto-detect Proton's bundled wine under steamapps/common/Proton*/files/bin/wine.");
        }
    }
}
