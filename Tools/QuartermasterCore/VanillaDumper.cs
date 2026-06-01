using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;

namespace Windrose.Quartermaster.Core
{
    public sealed class VanillaDumper
    {
        readonly WindrosePaths _paths;

        public VanillaDumper(WindrosePaths paths)
        {
            if (paths == null) throw new ArgumentNullException("paths");
            _paths = paths;
        }

        public Action<string> Log;

        public string VanillaPakOverride;
        public string RepakExeOverride;
        public string OutDirOverride;
        // repak -f (overwrite existing files).
        public bool Force = true;
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

            // Re-log the command with the AES key redacted so logs are shareable.
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

        DumpResult Statistics(string outDir)
        {
            int totalCount = 0;
            var byCategory = new Dictionary<string, int>(StringComparer.Ordinal);

            foreach (var entry in VanillaSourceManifest.Entries)
            {
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

        string RelocateToOutDir(string canonical, string outDir)
        {
            if (string.IsNullOrEmpty(canonical)) return canonical;
            var defaultRoot = _paths.Vanilla;
            if (canonical.StartsWith(defaultRoot, StringComparison.OrdinalIgnoreCase))
            {
                var tail = canonical.Substring(defaultRoot.Length).TrimStart(
                    Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                return Path.Combine(outDir, tail);
            }
            return canonical;
        }

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
            var prefixLen = root.Length + 1;
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
