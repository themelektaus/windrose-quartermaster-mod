using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;

namespace Windrose.StackSize.Core
{
    // Extracts the AES-encrypted InventoryItems prefix from the Windrose
    // vanilla pak via repak.exe. Replaces Library/Dump.ps1 +
    // Dump-WindroseVanilla.ps1 -- same repak invocation, same default
    // paths, same "force overwrite" semantics.
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

            LogLine("Unpacking InventoryItems from pak");
            RunRepakUnpack(repakExe, vanillaPak, outDir);

            return Statistics(outDir);
        }

        void RunRepakUnpack(string repakExe, string vanillaPak, string outDir)
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
            psi.ArgumentList.Add(WindroseGameSecrets.InventoryItemsPath);
            psi.ArgumentList.Add("-o");
            psi.ArgumentList.Add(outDir);
            if (Force) psi.ArgumentList.Add("-f");
            psi.ArgumentList.Add(vanillaPak);

            // Display the same command without the AES key so logs are safe
            // to share with users.
            LogLine("repak --aes-key <hidden> unpack -i " + WindroseGameSecrets.InventoryItemsPath +
                    " -o \"" + outDir + "\"" + (Force ? " -f" : "") +
                    " \"" + vanillaPak + "\"");

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
            var invRoot = Path.Combine(outDir, "R5", "Plugins", "R5BusinessRules",
                "Content", "InventoryItems");
            if (!Directory.Exists(invRoot))
            {
                LogLine("[!] Expected directory not produced: " + invRoot);
                return new DumpResult { OutDir = outDir, FileCount = 0, ByCategory = new Dictionary<string, int>() };
            }

            var files = Directory.EnumerateFiles(invRoot, "*.json", SearchOption.AllDirectories).ToList();
            var byCategory = new Dictionary<string, int>(StringComparer.Ordinal);
            var prefixLen = invRoot.Length + 1; // skip the trailing separator
            foreach (var f in files)
            {
                var rel = f.Length > prefixLen ? f.Substring(prefixLen) : Path.GetFileName(f);
                var segs = rel.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                var cat = segs.Length >= 2 ? segs[0] : "(other)";
                int count;
                byCategory.TryGetValue(cat, out count);
                byCategory[cat] = count + 1;
            }
            LogLine(files.Count + " JSON files extracted");
            foreach (var kv in byCategory.OrderBy(p => p.Key, StringComparer.Ordinal))
            {
                LogLine(string.Format("  {0,-15} {1,6}", kv.Key, kv.Value));
            }
            return new DumpResult { OutDir = outDir, FileCount = files.Count, ByCategory = byCategory };
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
