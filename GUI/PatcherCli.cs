using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Windrose.StackSize.Core;

namespace Windrose.StackSize.Gui
{
    // Headless CLI shim that lets us drive the patcher from the command line:
    //
    //   dotnet run --project GUI -- --test-patcher --multiplier 4 --build-pak
    //   dotnet run --project GUI -- --test-patcher --profile x4
    //
    // Used to smoke-test parity with the legacy PowerShell pipeline before the
    // full Build endpoint exists (Phase 4).  Keeps the production HTTP path
    // untouched -- the flag short-circuits Main before the WebApplication is
    // built.
    public static class PatcherCli
    {
        public static int Run(string[] args, string repoRoot)
        {
            int? multiplier = null;
            int? absolute = null;
            int? cap = null;
            string vanilla = Path.Combine(repoRoot, "Sources", "Vanilla");
            string outDir = null;
            string pakPath = null;
            bool buildPak = false;
            string profileSpec = null;
            bool keepTemp = false;

            for (int i = 1; i < args.Length; i++)
            {
                var a = args[i];
                switch (a)
                {
                    case "--multiplier":
                        multiplier = int.Parse(args[++i]);
                        break;
                    case "--absolute":
                        absolute = int.Parse(args[++i]);
                        break;
                    case "--cap":
                        cap = int.Parse(args[++i]);
                        break;
                    case "--vanilla":
                        vanilla = args[++i];
                        break;
                    case "--out":
                        outDir = args[++i];
                        break;
                    case "--pak":
                        pakPath = args[++i];
                        buildPak = true;
                        break;
                    case "--build-pak":
                        buildPak = true;
                        break;
                    case "--profile":
                        profileSpec = args[++i];
                        break;
                    case "--keep-temp":
                        keepTemp = true;
                        break;
                    default:
                        Console.Error.WriteLine("Unknown argument: " + a);
                        return 2;
                }
            }

            // Profile mode: load a builtin / user profile by id or name and
            // run the full BuildPipeline (patch + pack + cleanup).
            if (!string.IsNullOrEmpty(profileSpec))
            {
                return RunProfileMode(repoRoot, profileSpec, keepTemp);
            }

            if (!multiplier.HasValue && !absolute.HasValue)
            {
                Console.Error.WriteLine("Specify --multiplier <N> or --absolute <N>");
                return 2;
            }
            if (string.IsNullOrEmpty(outDir))
            {
                var tag = multiplier.HasValue ? ("x" + multiplier.Value) : ("abs" + absolute.Value);
                outDir = Path.Combine(repoRoot, ".build-tmp", "smoke_" + tag);
            }
            if (Directory.Exists(outDir))
            {
                Console.WriteLine("Cleaning existing output: " + outDir);
                Directory.Delete(outDir, true);
            }

            var profile = new Profile
            {
                Id = Guid.NewGuid().ToString(),
                Name = "smoke-test",
                Globals = new ProfileGlobals
                {
                    StackSize = new StackSizeGlobal
                    {
                        Multiplier = multiplier,
                        Absolute = absolute,
                        Cap = cap,
                    },
                },
                Overrides = new Dictionary<string, ItemOverride>(),
            };

            Console.WriteLine("Vanilla: " + vanilla);
            Console.WriteLine("Out:     " + outDir);
            Console.WriteLine("Mode:    " + (multiplier.HasValue
                ? ("multiplier x" + multiplier.Value + (cap.HasValue ? (" (cap " + cap.Value + ")") : ""))
                : ("absolute " + absolute.Value)));

            var patcher = new StackPatcher();
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var result = patcher.PatchToDirectory(vanilla, outDir, profile);
            sw.Stop();

            Console.WriteLine();
            Console.WriteLine("Scanned        : " + result.Scanned);
            Console.WriteLine("Excluded (Tests): " + result.Excluded);
            Console.WriteLine("NoSchema       : " + result.NoSchema);
            Console.WriteLine("Skipped (st1)  : " + result.Skipped);
            Console.WriteLine("UnchangedSkip  : " + result.UnchangedSkip);
            Console.WriteLine("Written        : " + result.Written);
            Console.WriteLine("  Promoted     : " + result.Promoted);
            Console.WriteLine("  Overridden   : " + result.Overridden);
            Console.WriteLine("  Capped       : " + result.Capped);
            Console.WriteLine("Time           : " + sw.Elapsed.TotalSeconds.ToString("0.00") + "s");

            // Spot check: Banana should be vanillaStack * multiplier (or absolute)
            var bananaPath = Directory.EnumerateFiles(outDir, "DA_CID_Food_Raw_Banana_T01.json", SearchOption.AllDirectories).FirstOrDefault();
            if (bananaPath != null)
            {
                var content = File.ReadAllText(bananaPath);
                var match = System.Text.RegularExpressions.Regex.Match(content, "\"MaxCountInSlot\"\\s*:\\s*(\\d+)");
                Console.WriteLine();
                Console.WriteLine("Spot check Banana (vanilla=50): " + (match.Success ? match.Groups[1].Value : "<not found>"));
            }

            if (buildPak)
            {
                if (string.IsNullOrEmpty(pakPath))
                {
                    var tag = multiplier.HasValue ? ("x" + multiplier.Value) : ("abs" + absolute.Value);
                    pakPath = Path.Combine(repoRoot, ".build-tmp", "smoke_" + tag + "_P.pak");
                }

                Console.WriteLine();
                Console.WriteLine("Resolving repak.exe...");
                var resolver = new RepakResolver(repoRoot);
                resolver.Log = m => Console.WriteLine("  " + m);
                var repakExe = resolver.Resolve();
                Console.WriteLine("repak: " + repakExe);

                Console.WriteLine();
                Console.WriteLine("Packing...");
                var builder = new PakBuilder(repakExe);
                builder.Log = m => Console.WriteLine("  " + m);
                var pakResult = builder.Build(outDir, pakPath, overwrite: true);
                Console.WriteLine();
                Console.WriteLine("Pak     : " + pakResult.PakPath);
                Console.WriteLine("Size    : " + Math.Round(pakResult.SizeBytes / 1024.0, 1) + " KB");
                Console.WriteLine("Files   : " + pakResult.FileCount);
            }

            return 0;
        }

        // Headless setup pipeline: runs the same dump + icon extraction
        // logic the GUI exposes via /api/setup/run, but writes progress to
        // stdout. Replaces the old Dump-WindroseVanilla.ps1 +
        // Extract-Icons.ps1 wrappers.
        //
        // Usage:
        //   dotnet run --project GUI -- --setup           (run missing steps only)
        //   dotnet run --project GUI -- --setup --force   (re-run every step)
        public static int RunSetup(string[] args, string repoRoot)
        {
            bool force = false;
            for (int i = 1; i < args.Length; i++)
            {
                if (args[i] == "--force") force = true;
                else
                {
                    Console.Error.WriteLine("Unknown argument: " + args[i]);
                    return 2;
                }
            }
            var paths = WindrosePaths.FromModRoot(repoRoot);
            var runner = new SetupRunner(paths)
            {
                ForceAll = force,
                Log = m => Console.WriteLine(m),
            };
            try
            {
                var status = runner.Probe();
                Console.WriteLine("Status:");
                Console.WriteLine("  vanilla sources : " + (status.HasVanillaSources ? "OK" : "MISSING"));
                Console.WriteLine("  icons           : " + (status.HasIcons          ? "OK" : "MISSING"));
                Console.WriteLine("  usmap           : " + (status.HasUsmap          ? "OK -- " + status.UsmapPath : "MISSING"));
                Console.WriteLine("  steam pak       : " + (status.HasVanillaPak     ? "OK -- " + status.VanillaPakPath : "MISSING -- " + status.VanillaPakError));
                Console.WriteLine();
                runner.Run();
                Console.WriteLine();
                Console.WriteLine("Setup complete.");
                return 0;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine();
                Console.Error.WriteLine("Setup failed: " + ex.Message);
                return 1;
            }
        }

        // Loads a profile by id or display-name and runs the full pipeline
        // through Builds/<sanitized-name>_P.pak. Used to verify Phase 3 against
        // the legacy PS pipeline and as a hand-test for user-created profiles.
        static int RunProfileMode(string repoRoot, string spec, bool keepTemp)
        {
            var paths = WindrosePaths.FromModRoot(repoRoot);
            var store = new ProfileStore(paths);
            var all = store.LoadAll();

            var profile = all.FirstOrDefault(p =>
                string.Equals(p.Id, spec, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(p.Name, spec, StringComparison.OrdinalIgnoreCase));

            if (profile == null)
            {
                Console.Error.WriteLine("Profile not found: " + spec);
                Console.Error.WriteLine("Available:");
                foreach (var p in all)
                {
                    Console.Error.WriteLine("  " + (p.IsBuiltin ? "[builtin] " : "[user]    ")
                                            + p.Name + "  (" + p.Id + ")");
                }
                return 3;
            }

            Console.WriteLine("Profile : " + profile.Name + (profile.IsBuiltin ? " [builtin]" : "")
                              + "  (" + profile.Id + ")");

            var pipeline = new BuildPipeline(paths);
            pipeline.Log = m => Console.WriteLine("  " + m);

            try
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                var result = pipeline.Build(profile, keepTemp: keepTemp);
                sw.Stop();
                Console.WriteLine();
                Console.WriteLine("Pak     : " + result.PakPath);
                Console.WriteLine("Size    : " + Math.Round(result.PakResult.SizeBytes / 1024.0, 1) + " KB");
                Console.WriteLine("Files   : " + result.PakResult.FileCount);
                Console.WriteLine("Time    : " + sw.Elapsed.TotalSeconds.ToString("0.00") + "s");
                return 0;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("Build failed: " + ex.Message);
                return 1;
            }
        }
    }
}
