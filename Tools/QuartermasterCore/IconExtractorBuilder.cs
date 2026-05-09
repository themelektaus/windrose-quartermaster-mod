using System;
using System.Diagnostics;
using System.IO;

namespace Windrose.Quartermaster.Core
{
    // Builds Tools/IconExtractor/IconExtractor.exe via `dotnet publish` on
    // first use. Cached output lives at Tools/IconExtractor/publish/
    // (gitignored).
    //
    // Mirrors Library/Common.ps1's Get-IconExtractorExe: same project path,
    // same publish dir, same preflight (CUE4Parse submodule + dotnet SDK).
    // Logged progress is forwarded to the optional Log callback so the
    // GUI can stream it to the browser.
    public sealed class IconExtractorBuilder
    {
        readonly string _modRoot;

        public IconExtractorBuilder(string modRoot)
        {
            if (string.IsNullOrEmpty(modRoot)) throw new ArgumentNullException("modRoot");
            _modRoot = modRoot;
        }

        public Action<string> Log;

        public string Resolve()
        {
            var projectDir = Path.Combine(_modRoot, "Tools", "IconExtractor");
            var publishDir = Path.Combine(projectDir, "publish");
            var exePath = Path.Combine(publishDir, "IconExtractor.exe");

            if (File.Exists(exePath)) return exePath;

            LogLine("IconExtractor.exe not present -- building");

            var csproj = Path.Combine(projectDir, "IconExtractor.csproj");
            if (!File.Exists(csproj))
            {
                throw new FileNotFoundException(
                    "IconExtractor project not found: " + csproj);
            }

            // CUE4Parse submodule preflight -- give a useful hint instead
            // of letting `dotnet publish` fail with a cryptic NU1100.
            var cue4parseCsproj = Path.Combine(_modRoot, "Tools", "CUE4Parse",
                "CUE4Parse", "CUE4Parse.csproj");
            if (!File.Exists(cue4parseCsproj))
            {
                throw new FileNotFoundException(
                    "CUE4Parse submodule is not initialized: " + cue4parseCsproj +
                    " is missing.\nRun this once from the modding root:\n\n" +
                    "  git submodule update --init Tools/CUE4Parse\n\n" +
                    "(Or pass --recursive on the original `git clone`.)");
            }

            LogLine("csproj : " + csproj);
            LogLine("output : " + publishDir);

            RunDotnetPublish(csproj, publishDir);

            if (!File.Exists(exePath))
            {
                throw new InvalidOperationException(
                    "Build succeeded but IconExtractor.exe is missing at " + exePath);
            }
            LogLine("Built: " + exePath);
            return exePath;
        }

        void RunDotnetPublish(string csproj, string publishDir)
        {
            var psi = new ProcessStartInfo
            {
                FileName = "dotnet",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            psi.ArgumentList.Add("publish");
            psi.ArgumentList.Add(csproj);
            psi.ArgumentList.Add("-c");
            psi.ArgumentList.Add("Release");
            psi.ArgumentList.Add("-r");
            psi.ArgumentList.Add("win-x64");
            psi.ArgumentList.Add("--self-contained");
            psi.ArgumentList.Add("false");
            psi.ArgumentList.Add("-o");
            psi.ArgumentList.Add(publishDir);
            psi.ArgumentList.Add("--nologo");
            psi.ArgumentList.Add("-v");
            psi.ArgumentList.Add("minimal");

            Process proc;
            try
            {
                proc = Process.Start(psi);
            }
            catch (System.ComponentModel.Win32Exception ex)
            {
                throw new InvalidOperationException(
                    "dotnet SDK not found in PATH. Install the .NET 10 SDK from " +
                    "https://dotnet.microsoft.com/download (only needed for the icon extractor).",
                    ex);
            }

            proc.OutputDataReceived += (s, e) => { if (e.Data != null) LogLine(e.Data); };
            proc.ErrorDataReceived  += (s, e) => { if (e.Data != null) LogLine(e.Data); };
            proc.BeginOutputReadLine();
            proc.BeginErrorReadLine();
            proc.WaitForExit();
            if (proc.ExitCode != 0)
            {
                throw new InvalidOperationException(
                    "dotnet publish failed (exit " + proc.ExitCode + ")");
            }
        }

        void LogLine(string msg)
        {
            if (Log != null) Log(msg);
        }
    }
}
