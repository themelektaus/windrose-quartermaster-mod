using System;
using System.Diagnostics;
using System.IO;

namespace Windrose.StackSize.Core
{
    // Wraps `repak pack` as a subprocess. Defaults match every Windrose mod
    // examined so far: MountPoint '../../../' lands pak entries at the game
    // root, V8B is the format used by the current Windrose build (UE5.6).
    public sealed class PakBuilder
    {
        public string MountPoint = "../../../";
        public string Version = "V8B";

        readonly string _repakExe;

        public PakBuilder(string repakExe)
        {
            if (string.IsNullOrEmpty(repakExe)) throw new ArgumentNullException("repakExe");
            _repakExe = repakExe;
        }

        public Action<string> Log;

        public PakBuildResult Build(string sourceDir, string outPakPath, bool overwrite = true)
        {
            if (string.IsNullOrEmpty(sourceDir)) throw new ArgumentNullException("sourceDir");
            if (string.IsNullOrEmpty(outPakPath)) throw new ArgumentNullException("outPakPath");
            if (!Directory.Exists(sourceDir))
                throw new DirectoryNotFoundException("Source not found: " + sourceDir);

            // Refuse silent destructive overwrite unless asked.
            if (File.Exists(outPakPath))
            {
                if (!overwrite)
                    throw new IOException("Output already exists (overwrite=false): " + outPakPath);
                File.Delete(outPakPath);
            }
            var outDir = Path.GetDirectoryName(outPakPath);
            if (!string.IsNullOrEmpty(outDir)) Directory.CreateDirectory(outDir);

            // Sanity: empty source dirs produce a useless empty pak.
            var fileCount = 0;
            foreach (var _ in Directory.EnumerateFiles(sourceDir, "*", SearchOption.AllDirectories))
            {
                fileCount++;
                if (fileCount >= 1) break;
            }
            if (fileCount == 0)
                throw new InvalidOperationException("Source folder is empty: " + sourceDir);

            LogLine("repak pack --mount-point " + MountPoint + " --version " + Version
                    + " " + Quote(sourceDir) + " " + Quote(outPakPath));

            var psi = new ProcessStartInfo
            {
                FileName = _repakExe,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            psi.ArgumentList.Add("pack");
            psi.ArgumentList.Add("--mount-point"); psi.ArgumentList.Add(MountPoint);
            psi.ArgumentList.Add("--version");     psi.ArgumentList.Add(Version);
            psi.ArgumentList.Add(sourceDir);
            psi.ArgumentList.Add(outPakPath);

            var proc = Process.Start(psi);
            var stdout = proc.StandardOutput.ReadToEnd();
            var stderr = proc.StandardError.ReadToEnd();
            proc.WaitForExit();

            if (proc.ExitCode != 0)
            {
                throw new InvalidOperationException(
                    "repak pack failed (exit " + proc.ExitCode + ")\n"
                    + (string.IsNullOrEmpty(stderr) ? stdout : stderr));
            }
            if (!File.Exists(outPakPath))
            {
                throw new InvalidOperationException("Pak was not created: " + outPakPath);
            }

            var size = new FileInfo(outPakPath).Length;
            LogLine("Pak built: " + outPakPath + "  (" + Math.Round(size / 1024.0, 1) + " KB)");

            return new PakBuildResult
            {
                PakPath = outPakPath,
                SizeBytes = size,
                FileCount = CountFiles(sourceDir),
                Stdout = stdout,
                Stderr = stderr,
            };
        }

        static int CountFiles(string dir)
        {
            int n = 0;
            foreach (var _ in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories)) n++;
            return n;
        }

        static string Quote(string s)
        {
            return s.IndexOf(' ') >= 0 ? "\"" + s + "\"" : s;
        }

        void LogLine(string msg)
        {
            if (Log != null) Log(msg);
        }
    }

    public sealed class PakBuildResult
    {
        public string PakPath;
        public long SizeBytes;
        public int FileCount;
        public string Stdout;
        public string Stderr;
    }
}
