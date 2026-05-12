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
        // Must match Tools/IconExtractor/IconExtractor.csproj <TargetFramework>.
        // Used to invalidate stale publish/ caches built against an older
        // TFM (e.g. user upgraded from a net8.0 build and the cached EXE
        // would still ask for the net8.0 runtime at launch).
        //
        // Public so the deployed-EXE seed path
        // (Web.Program.SeedIconExtractorIfMissing) can share the exact same
        // freshness logic without duplicating the constant.
        public const string ExpectedTfm = "net10.0";

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

            if (File.Exists(exePath))
            {
                if (IsPublishFresh(publishDir)) return exePath;
                LogLine("IconExtractor.exe present but built against a different .NET runtime - rebuilding");
                TryDeletePublishDir(publishDir);
            }
            else
            {
                LogLine("IconExtractor.exe not present - building");
            }

            var csproj = Path.Combine(projectDir, "IconExtractor.csproj");
            if (!File.Exists(csproj))
            {
                // Deployed end-users never see this in the happy path -
                // Web.Program.SeedIconExtractorIfMissing extracts a
                // prebuilt publish/ from the embedded zip on startup, so
                // by the time we get here the EXE exists and matches
                // ExpectedTfm. If we land here in a deployed install
                // anyway, the embedded zip wasn't present (very old EXE)
                // or extraction was wiped between startup and setup -
                // either way "the project file is missing" is misleading.
                throw new FileNotFoundException(
                    "IconExtractor build artifact is missing and rebuilding " +
                    "from source isn't possible because the project file " +
                    "isn't shipped with the deployed EXE.\n\n" +
                    "Fix: re-download or re-extract the latest " +
                    "Quartermaster.exe (the prebuilt IconExtractor is " +
                    "embedded in it) and start it once before running " +
                    "setup again.\n\n" +
                    "Expected at: " + csproj);
            }

            // CUE4Parse submodule preflight - transparently `git submodule
            // update --init` if the submodule isn't checked out yet, so a
            // fresh clone "just works" without the user knowing about
            // submodules. If git itself isn't on PATH or the repo isn't a
            // clone (e.g. zip download), surface a clear hint.
            var cue4parseCsproj = Path.Combine(_modRoot, "Tools", "CUE4Parse",
                "CUE4Parse", "CUE4Parse.csproj");
            if (!File.Exists(cue4parseCsproj))
            {
                EnsureCUE4ParseSubmodule(cue4parseCsproj);
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

        // Initializes the CUE4Parse git submodule into Tools/CUE4Parse/.
        // Mirrors `git submodule update --init Tools/CUE4Parse` run from the
        // mod root. Streams git's output through the Log callback so the
        // setup overlay shows the clone progress live. Throws with an
        // actionable message when git isn't on PATH or when the mod root
        // isn't actually a git clone.
        void EnsureCUE4ParseSubmodule(string expectedCsproj)
        {
            LogLine("CUE4Parse submodule not initialized - running git submodule update --init");

            var dotGit = Path.Combine(_modRoot, ".git");
            if (!Directory.Exists(dotGit) && !File.Exists(dotGit))
            {
                throw new InvalidOperationException(
                    "Cannot auto-initialize the CUE4Parse submodule: " + _modRoot +
                    " is not a git clone (no .git directory).\n\n" +
                    "Either re-clone the repository with `git clone --recursive ...`, " +
                    "or download CUE4Parse manually from " +
                    "https://github.com/FabianFG/CUE4Parse and place it under " +
                    "Tools/CUE4Parse/.");
            }

            var psi = new ProcessStartInfo
            {
                FileName = "git",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                WorkingDirectory = _modRoot,
            };
            psi.ArgumentList.Add("submodule");
            psi.ArgumentList.Add("update");
            psi.ArgumentList.Add("--init");
            psi.ArgumentList.Add("--progress");
            psi.ArgumentList.Add("Tools/CUE4Parse");

            Process proc;
            try
            {
                proc = Process.Start(psi);
            }
            catch (System.ComponentModel.Win32Exception ex)
            {
                throw new InvalidOperationException(
                    "git not found in PATH. Install Git for Windows from " +
                    "https://git-scm.com/download/win, or initialize the " +
                    "submodule manually:\n\n" +
                    "  git submodule update --init Tools/CUE4Parse",
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
                    "git submodule update --init Tools/CUE4Parse failed (exit "
                    + proc.ExitCode + "). Check the log above for details.");
            }

            if (!File.Exists(expectedCsproj))
            {
                throw new InvalidOperationException(
                    "git submodule update completed, but " + expectedCsproj +
                    " is still missing. The .gitmodules entry may be out of date.");
            }
            LogLine("CUE4Parse submodule initialized.");
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

        // Sniffs IconExtractor.runtimeconfig.json next to the cached EXE and
        // checks whether its "tfm" field matches ExpectedTfm. We do a plain
        // substring check rather than parsing JSON to keep this dependency-
        // free and tolerant of formatting (the publish step emits stable
        // JSON, but we don't want to pull in System.Text.Json from QC just
        // for this). Missing/unreadable config defaults to "stale" so we
        // rebuild defensively.
        //
        // Public so the deployed-EXE seed path
        // (Web.Program.SeedIconExtractorIfMissing) can share the exact
        // same freshness probe used by the resolver here.
        public static bool IsPublishFresh(string publishDir)
        {
            var runtimeConfig = Path.Combine(publishDir, "IconExtractor.runtimeconfig.json");
            if (!File.Exists(runtimeConfig)) return false;

            string text;
            try { text = File.ReadAllText(runtimeConfig); }
            catch { return false; }

            // Looking for: "tfm": "net10.0"
            var needle = "\"tfm\": \"" + ExpectedTfm + "\"";
            if (text.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0) return true;

            // Tolerate the no-space variant just in case the SDK emits it
            // differently in a future release.
            needle = "\"tfm\":\"" + ExpectedTfm + "\"";
            return text.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        void TryDeletePublishDir(string publishDir)
        {
            try
            {
                if (Directory.Exists(publishDir))
                {
                    Directory.Delete(publishDir, recursive: true);
                }
            }
            catch (Exception ex)
            {
                // Non-fatal: dotnet publish will overwrite individual files.
                // The runtimeconfig.json is regenerated either way, so even
                // if we can't wipe the whole dir, the freshness check will
                // pass on the next Resolve().
                LogLine("Could not delete stale publish dir (" + ex.Message
                    + ") - continuing, dotnet publish will overwrite.");
            }
        }

        void LogLine(string msg)
        {
            if (Log != null) Log(msg);
        }
    }
}
