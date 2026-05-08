using System;
using System.IO;
using System.Text;

namespace Windrose.StackSize.Core
{
    // Orchestrates the full build for a given Profile:
    //   Vanilla/  --(StackPatcher)-->  .build-tmp/<profile-id>/  --(PakBuilder)-->  Builds/<name>_P.pak
    //
    // The temp directory is wiped before patching and (by default) deleted
    // after a successful pack -- callers can opt into keepTemp=true for
    // post-mortem debugging.
    public sealed class BuildPipeline
    {
        readonly WindrosePaths _paths;
        readonly StackPatcher _patcher;
        readonly RepakResolver _repakResolver;

        public Action<string> Log;

        public BuildPipeline(WindrosePaths paths)
        {
            if (paths == null) throw new ArgumentNullException("paths");
            _paths = paths;
            _patcher = new StackPatcher();
            _repakResolver = new RepakResolver(paths.ModRoot);
        }

        public BuildPipelineResult Build(Profile profile, bool keepTemp = false)
        {
            if (profile == null) throw new ArgumentNullException("profile");
            if (string.IsNullOrEmpty(profile.Id)) throw new ArgumentException("Profile.Id is required");
            if (string.IsNullOrEmpty(profile.Name)) throw new ArgumentException("Profile.Name is required");
            if (!Directory.Exists(_paths.Vanilla))
                throw new DirectoryNotFoundException(
                    "Vanilla source not found: " + _paths.Vanilla
                    + " -- run Dump-WindroseVanilla.ps1 first to extract it from the game pak");

            var safeName = SanitizeForFileName(profile.Name);
            var pakName = "StackSize_" + safeName + "_P.pak";
            var outPakPath = Path.Combine(_paths.Builds, pakName);
            var tmpDir = Path.Combine(_paths.BuildTmp, profile.Id);

            try
            {
                // Wipe the temp dir before patching: a stale tree from a
                // previous run could otherwise leak files into the new pak.
                if (Directory.Exists(tmpDir)) Directory.Delete(tmpDir, true);

                LogLine("Patching vanilla -> " + tmpDir);
                var patchResult = _patcher.PatchToDirectory(_paths.Vanilla, tmpDir, profile);

                LogLine("Patched: " + patchResult.Written + " items"
                        + " (" + patchResult.Promoted + " promoted, "
                        + patchResult.Overridden + " overridden, "
                        + patchResult.Capped + " capped)");

                if (patchResult.Written == 0)
                {
                    throw new InvalidOperationException(
                        "Profile produces no changes -- nothing to pack. "
                        + "Adjust globals or add per-item overrides.");
                }

                LogLine("Resolving repak.exe...");
                _repakResolver.Log = Log;
                var repakExe = _repakResolver.Resolve();

                LogLine("Packing -> " + outPakPath);
                Directory.CreateDirectory(_paths.Builds);
                var builder = new PakBuilder(repakExe);
                builder.Log = Log;
                var pakResult = builder.Build(tmpDir, outPakPath, overwrite: true);

                LogLine("Pak built: " + outPakPath
                        + " (" + Math.Round(pakResult.SizeBytes / 1024.0, 1) + " KB, "
                        + pakResult.FileCount + " files)");

                return new BuildPipelineResult
                {
                    Profile = profile,
                    PatchResult = patchResult,
                    PakResult = pakResult,
                    PakPath = outPakPath,
                    TmpDir = tmpDir,
                    Success = true,
                };
            }
            finally
            {
                if (!keepTemp && Directory.Exists(tmpDir))
                {
                    try { Directory.Delete(tmpDir, true); }
                    catch (Exception ex)
                    {
                        // Failure to clean up is annoying but not fatal --
                        // surface it in the log so the user can clean by hand.
                        LogLine("Warning: temp dir cleanup failed: " + ex.Message);
                    }
                }
            }
        }

        // Profile.Name -> filename component for "StackSize_<name>_P.pak".
        // Stay strict (alnum + dash + underscore) so the pak filename works
        // on every Windows / Linux server config the user might drop it on.
        // Spaces collapse to dashes; other chars are dropped.
        public static string SanitizeForFileName(string name)
        {
            if (string.IsNullOrEmpty(name)) return "Untitled";
            var sb = new StringBuilder(name.Length);
            foreach (var c in name)
            {
                if (char.IsLetterOrDigit(c) || c == '-' || c == '_') sb.Append(c);
                else if (c == ' ') sb.Append('-');
            }
            // Collapse runs of dashes / underscores to a single one for tidiness.
            var raw = sb.ToString();
            if (string.IsNullOrEmpty(raw)) return "Untitled";
            var collapsed = new StringBuilder(raw.Length);
            char prev = '\0';
            foreach (var c in raw)
            {
                if ((c == '-' || c == '_') && c == prev) continue;
                collapsed.Append(c);
                prev = c;
            }
            return collapsed.ToString().Trim('-', '_');
        }

        void LogLine(string msg)
        {
            if (Log != null) Log(msg);
        }
    }

    public sealed class BuildPipelineResult
    {
        public Profile Profile;
        public PatchResult PatchResult;
        public PakBuildResult PakResult;
        public string PakPath;
        public string TmpDir;
        public bool Success;
    }
}
