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
    // paths, same "force overwrite" semantics. Three prefixes are extracted
    // in sequence: InventoryItems (item defs), LootTables (drop pools), and
    // BuildingLimits (DA_BuildLimits_FastTravel.json + siblings).
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
            RunRepakUnpack(repakExe, vanillaPak, outDir, WindroseGameSecrets.InventoryItemsPath);

            LogLine("Unpacking LootTables from pak");
            RunRepakUnpack(repakExe, vanillaPak, outDir, WindroseGameSecrets.LootTablesPath);

            LogLine("Unpacking BuildingLimits from pak");
            RunRepakUnpack(repakExe, vanillaPak, outDir, WindroseGameSecrets.BuildingLimitsPath);

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
            var lootRoot = Path.Combine(outDir, "R5", "Plugins", "R5BusinessRules",
                "Content", "LootTables");
            var buildLimitsRoot = Path.Combine(outDir, "R5", "Content",
                "Gameplay", "BuildingLimits");

            int totalCount = 0;
            var byCategory = new Dictionary<string, int>(StringComparer.Ordinal);

            CollectStatistics(invRoot, "items", byCategory, ref totalCount);
            CollectStatistics(lootRoot, "loot", byCategory, ref totalCount);
            CollectStatistics(buildLimitsRoot, "buildlimits", byCategory, ref totalCount);

            LogLine(totalCount + " JSON files extracted");
            foreach (var kv in byCategory.OrderBy(p => p.Key, StringComparer.Ordinal))
            {
                LogLine(string.Format("  {0,-25} {1,6}", kv.Key, kv.Value));
            }
            return new DumpResult { OutDir = outDir, FileCount = totalCount, ByCategory = byCategory };
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
