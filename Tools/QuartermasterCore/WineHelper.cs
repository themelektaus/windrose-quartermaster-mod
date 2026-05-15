using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;

namespace Windrose.Quartermaster.Core
{
    // On Linux, Windows .exe binaries must be run via Wine. We resolve a wine
    // binary in this order:
    //   1. `wine` on $PATH        (standalone install: pacman, apt, Discover-WINE)
    //   2. Proton's bundled wine  (~/.steam/.../steamapps/common/Proton*/files/bin/wine)
    //   3. GE-Proton bundled wine (~/.steam/.../steamapps/common/GE-Proton*/files/bin/wine)
    // Result is cached for the lifetime of the process.
    //
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

        // Exposed for diagnostics. Returns null on Windows or if no wine was found
        // (does not throw). The first call performs the search and caches the result.
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
            // 1) wine on PATH (preferred - cleanest, user installed it explicitly)
            var pathWine = FindOnPath("wine");
            if (pathWine != null) return pathWine;

            // 2) Proton-bundled wine (Steam Deck / any user with Proton installed)
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
                    // Skip unreadable PATH entries silently.
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

                    // Cover stock Proton (e.g. "Proton 9.0", "Proton Experimental")
                    // and GE-Proton (e.g. "GE-Proton9-25").
                    var globs = new[] { "Proton*", "GE-Proton*" };
                    foreach (var glob in globs)
                    {
                        IEnumerable<string> dirs;
                        try { dirs = Directory.EnumerateDirectories(common, glob); }
                        catch { continue; }
                        foreach (var dir in dirs)
                        {
                            var name = Path.GetFileName(dir);
                            // Newer Proton: <root>/files/bin/wine
                            // Older Proton: <root>/dist/bin/wine
                            var winePath = Path.Combine(dir, "files", "bin", "wine");
                            if (!File.Exists(winePath))
                                winePath = Path.Combine(dir, "dist", "bin", "wine");
                            if (File.Exists(winePath))
                                candidates.Add((name, winePath));
                        }
                    }
                }

                if (candidates.Count == 0) return null;

                // Prefer the latest version. Lexicographic descending sort on the
                // directory name is a good-enough heuristic for "Proton 9.0" >
                // "Proton 8.0" > "Proton 7.0" and friends. Stock Proton beats
                // GE-Proton only because 'P' < 'G' alphabetically reversed - but
                // either works, so we don't fight over it.
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
