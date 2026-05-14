using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;

namespace Windrose.Quartermaster.Core
{
    // Extracts the AES-encrypted Windrose-content prefixes from the vanilla
    // pak via repak.exe. Replaces Library/Dump.ps1 +
    // Dump-WindroseVanilla.ps1 - same repak invocation, same default
    // paths, same "force overwrite" semantics.
    //
    // The list of prefixes to extract is read from VanillaSourceManifest -
    // adding a new required vanilla artifact to that manifest automatically
    // teaches both this dumper (what to unpack) and SetupRunner.Probe()
    // (what to check) about it. No more "extracted by Run() but not
    // checked by Probe()" drift (which previously left BuildingLimits as
    // an unchecked dependency for the bell-limits patcher).
    //
    // Auto-resolves repak.exe (download on first use) and the vanilla pak
    // (via Steam) when the corresponding inputs are null/empty.
    public sealed class VanillaDumper
    {
        readonly WindrosePaths _paths;

        public VanillaDumper(WindrosePaths paths)
        {
            if (paths == null) throw new ArgumentNullException("paths");
            _paths = paths;
        }

        public Action<string> Log;

        // Optional explicit overrides; null/empty values are auto-resolved.
        public string VanillaPakOverride;
        public string RepakExeOverride;
        public string OutDirOverride;
        // True == repak gets `-f` (overwrite existing files). Setup callers
        // want this so repeated runs don't fail on stale output.
        public bool Force = true;
        // True == empty the OutDir before unpacking. Off by default; useful
        // when the schema changed and you want a clean slate.
        public bool Clean;

        public DumpResult Run()
        {
            var vanillaPak = !string.IsNullOrEmpty(VanillaPakOverride)
                ? Path.GetFullPath(VanillaPakOverride)
                : SteamLocator.FindVanillaPak();
            if (!File.Exists(vanillaPak))
            {
                throw new FileNotFoundException("Vanilla pak not found: " + vanillaPak);
            }
            LogLine("VanillaPak: " + vanillaPak);

            string repakExe = RepakExeOverride;
            if (string.IsNullOrEmpty(repakExe))
            {
                var resolver = new RepakResolver(_paths.ModRoot);
                resolver.Log = Log;
                repakExe = resolver.Resolve();
            }
            LogLine("RepakExe:   " + repakExe);

            var outDir = !string.IsNullOrEmpty(OutDirOverride)
                ? Path.GetFullPath(OutDirOverride)
                : _paths.Vanilla;
            Directory.CreateDirectory(outDir);
            LogLine("OutDir:     " + outDir);

            if (Clean)
            {
                LogLine("Clean: emptying OutDir");
                foreach (var entry in Directory.EnumerateFileSystemEntries(outDir))
                {
                    if (Directory.Exists(entry)) Directory.Delete(entry, true);
                    else File.Delete(entry);
                }
                LogLine("OutDir emptied");
            }

            // Manifest-driven: every required vanilla artifact is one
            // manifest entry, and we unpack them in declaration order so
            // the log reads as a stable progression. Adding a new entry
            // to VanillaSourceManifest is the ONLY change needed to make
            // both this dumper and SetupRunner.Probe() aware of it.
            foreach (var entry in VanillaSourceManifest.Entries)
            {
                LogLine("Unpacking " + entry.Label + " from pak (" + entry.PakIncludePath + ")");
                RunRepakUnpack(repakExe, vanillaPak, outDir, entry.PakIncludePath);
            }

            return Statistics(outDir);
        }

        void RunRepakUnpack(string repakExe, string vanillaPak, string outDir, string includePrefix)
        {
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
            psi.ArgumentList.Add(outDir);
            if (Force) psi.ArgumentList.Add("-f");
            psi.ArgumentList.Add(vanillaPak);

            // Display the same command without the AES key so logs are safe
            // to share with users.
            LogLine("repak --aes-key <hidden> unpack -i " + includePrefix +
                    " -o \"" + outDir + "\"" + (Force ? " -f" : "") +
                    " \"" + vanillaPak + "\"");

            WineHelper.ApplyWine(psi);
            var proc = Process.Start(psi);
            proc.OutputDataReceived += (s, e) => { if (e.Data != null) LogLine(e.Data); };
            proc.ErrorDataReceived  += (s, e) => { if (e.Data != null) LogLine(e.Data); };
            proc.BeginOutputReadLine();
            proc.BeginErrorReadLine();
            proc.WaitForExit();
            if (proc.ExitCode != 0)
            {
                throw new InvalidOperationException(
                    "repak unpack failed (exit " + proc.ExitCode + ")");
            }
        }

        // Walks every manifest entry's on-disk landing spot and produces a
        // per-category file count plus an overall total - same shape as
        // the old hand-rolled version, just driven from the manifest now.
        // Single-file entries (e.g. the CSV) contribute exactly one row
        // to the breakdown so they remain visible as a heartbeat.
        DumpResult Statistics(string outDir)
        {
            int totalCount = 0;
            var byCategory = new Dictionary<string, int>(StringComparer.Ordinal);

            foreach (var entry in VanillaSourceManifest.Entries)
            {
                // Resolve the on-disk path for this entry, respecting
                // OutDirOverride. The manifest stores DiskPath as a func of
                // WindrosePaths whose defaults point to _paths.Vanilla/...;
                // when the caller redirected the dumper to a different
                // outDir, rewrite the prefix so stats reflect what was
                // actually written.
                var canonical = entry.DiskPath(_paths);
                var root = RelocateToOutDir(canonical, outDir);

                if (entry.ProbeKind == VanillaSourceProbeKind.SingleFile)
                {
                    if (File.Exists(root))
                    {
                        byCategory[entry.Key] = 1;
                        totalCount++;
                    }
                    else
                    {
                        LogLine("[!] Expected file not produced: " + root);
                    }
                    continue;
                }

                CollectStatistics(root, entry.Key, byCategory, ref totalCount);
            }

            LogLine(totalCount + " JSON files extracted");
            foreach (var kv in byCategory.OrderBy(p => p.Key, StringComparer.Ordinal))
            {
                LogLine(string.Format("  {0,-32} {1,6}", kv.Key, kv.Value));
            }
            return new DumpResult { OutDir = outDir, FileCount = totalCount, ByCategory = byCategory };
        }

        // When OutDirOverride is set, the manifest's canonical disk path
        // (which is computed from _paths.Vanilla) points to the WRONG
        // place. Rewrite the prefix so stats land on the actual extraction
        // target. No-op when outDir == _paths.Vanilla (the production case).
        string RelocateToOutDir(string canonical, string outDir)
        {
            if (string.IsNullOrEmpty(canonical)) return canonical;
            var defaultRoot = _paths.Vanilla;
            // Normalize trailing separators so the prefix-match is robust
            // against minor casing/separator differences from Path.Combine.
            if (canonical.StartsWith(defaultRoot, StringComparison.OrdinalIgnoreCase))
            {
                var tail = canonical.Substring(defaultRoot.Length).TrimStart(
                    Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                return Path.Combine(outDir, tail);
            }
            return canonical;
        }

        // Walks one of the extracted prefix-trees and tallies a per-category
        // count, prefixed with the tree label so InventoryItems and LootTables
        // counters don't collide (e.g. "items/Food" vs "loot/Food").
        void CollectStatistics(string root, string treeLabel,
            Dictionary<string, int> byCategory, ref int totalCount)
        {
            if (!Directory.Exists(root))
            {
                LogLine("[!] Expected directory not produced: " + root);
                return;
            }

            var files = Directory.EnumerateFiles(root, "*.json", SearchOption.AllDirectories).ToList();
            totalCount += files.Count;
            var prefixLen = root.Length + 1; // skip the trailing separator
            foreach (var f in files)
            {
                var rel = f.Length > prefixLen ? f.Substring(prefixLen) : Path.GetFileName(f);
                var segs = rel.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                var cat = treeLabel + "/" + (segs.Length >= 2 ? segs[0] : "(other)");
                int count;
                byCategory.TryGetValue(cat, out count);
                byCategory[cat] = count + 1;
            }
        }

        void LogLine(string msg)
        {
            if (Log != null) Log(msg);
        }
    }

    public sealed class DumpResult
    {
        public string OutDir;
        public int FileCount;
        public Dictionary<string, int> ByCategory;
    }
}
