using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;

namespace Windrose.Quartermaster.Core
{
    // Builds a UE5 IoStore mod triplet (.pak stub + .ucas + .utoc) from one
    // or more "sources". A source contributes Legacy .uasset+.uexp files
    // into a shared staging tree; after all sources have been extracted,
    // optional UAssetAPI patches run on individual assets, and finally a
    // single retoc to-zen call packs the whole tree into the triplet.
    //
    // Two source kinds in use today:
    //
    //   1. VanillaFilter - runs `retoc to-legacy` on the live game's Paks
    //      directory with --filter <stem>, e.g. "GA_Loot_AutoPickup".
    //      Produces 1..N legacy assets that we then patch in-place via the
    //      AfterExtract callback (UAssetAPI). This is how the pickup-radius
    //      mod is built: vanilla bytes -> legacy -> UAssetAPI adds the
    //      MagnetRadius FloatProperty -> back to zen.
    //
    //   2. (No active reference-mod source today - the building-stability
    //      feature switched to a vanilla self-bake using byte-level
    //      patches on the unparseable RawExport fallback. The reference-
    //      mod adoption pattern is preserved in this builder for future
    //      features that might genuinely need 1:1 adoption: drop the
    //      mod's .pak/.ucas/.utoc + the game's global.{ucas,utoc} into
    //      the same input dir and call with Filter=null.)
    //
    // The builder writes everything into the caller-supplied TempDir and
    // produces the final triplet at OutputBasePath + ".{pak,ucas,utoc}".
    public sealed class IoStoreCompositeBuilder
    {
        public Action<string> Log;

        public IoStoreCompositeResult Build(IoStoreCompositeRequest req)
        {
            if (req == null) throw new ArgumentNullException("req");
            if (string.IsNullOrEmpty(req.RetocExe)) throw new ArgumentException("RetocExe is required");
            if (req.Sources == null || req.Sources.Count == 0)
                throw new ArgumentException("At least one source is required");
            if (string.IsNullOrEmpty(req.OutputBasePath))
                throw new ArgumentException("OutputBasePath is required");
            if (!File.Exists(req.RetocExe))
                throw new FileNotFoundException("retoc.exe not found: " + req.RetocExe);

            // Triplet output paths - caller passes ".../<base>" without
            // extension, we derive .pak/.ucas/.utoc from it.
            var outDir = Path.GetDirectoryName(req.OutputBasePath);
            if (!string.IsNullOrEmpty(outDir)) Directory.CreateDirectory(outDir);
            var outPak  = req.OutputBasePath + ".pak";
            var outUcas = req.OutputBasePath + ".ucas";
            var outUtoc = req.OutputBasePath + ".utoc";

            if (req.Overwrite)
            {
                foreach (var p in new[] { outPak, outUcas, outUtoc })
                {
                    if (File.Exists(p)) File.Delete(p);
                }
            }
            else
            {
                foreach (var p in new[] { outPak, outUcas, outUtoc })
                {
                    if (File.Exists(p))
                        throw new IOException("Output already exists (overwrite=false): " + p);
                }
            }

            // Per-build temp tree: caller hands us the root, we own the
            // staging/ subdir within it. Wiping aggressively at the start
            // so a stale tree from a previous run can never leak.
            var tmpRoot = req.TempDir;
            if (string.IsNullOrEmpty(tmpRoot))
                tmpRoot = Path.Combine(Path.GetTempPath(), "windrose-iostore-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tmpRoot);
            var stagingDir = Path.Combine(tmpRoot, "legacy");
            if (Directory.Exists(stagingDir)) Directory.Delete(stagingDir, true);
            Directory.CreateDirectory(stagingDir);

            // Step 1: extract every source into the shared staging tree.
            // Multiple to-legacy calls into the same dir are safe - retoc
            // overwrites scriptobjects.bin idempotently (it's a function of
            // the GAME's global container, not the input mod) and asset
            // files land at distinct paths because the engine forbids two
            // assets sharing one virtual path.
            var sourceResults = new List<IoStoreCompositeSourceResult>(req.Sources.Count);
            foreach (var src in req.Sources)
            {
                if (src == null) throw new ArgumentException("Null source spec");
                if (string.IsNullOrEmpty(src.Name))
                    throw new ArgumentException("Source.Name is required");

                // Two source modes:
                //   * InputDir set -> classic vanilla / mod-pak extraction
                //     via retoc to-legacy, then optional AfterExtract patch.
                //   * InputDir null -> "pre-staged" mode: the caller drops
                //     its files into the staging tree from AfterExtract
                //     directly (e.g. ShipMusicPatcher copies a user-cooked
                //     SoundWave triplet straight into legacy/<vanilla path>).
                //     AfterExtract is REQUIRED in this mode - otherwise the
                //     source would contribute nothing.
                if (string.IsNullOrEmpty(src.InputDir))
                {
                    if (src.AfterExtract == null)
                        throw new ArgumentException(
                            "Source '" + src.Name + "' has no InputDir and no "
                            + "AfterExtract callback - pre-staged sources must "
                            + "supply an AfterExtract to provide their files.");
                    LogLine("Pre-staging [" + src.Name + "] into " + stagingDir);
                    src.AfterExtract(stagingDir);
                    sourceResults.Add(new IoStoreCompositeSourceResult { Name = src.Name });
                    continue;
                }

                if (!Directory.Exists(src.InputDir))
                    throw new DirectoryNotFoundException(
                        "Source '" + src.Name + "' input dir not found: " + src.InputDir);

                var argv = new List<string> { "to-legacy", src.InputDir, stagingDir, "--version", "UE5_6" };
                var filters = new List<string>();
                if (!string.IsNullOrEmpty(src.Filter)) filters.Add(src.Filter);
                if (src.Filters != null)
                {
                    foreach (var f in src.Filters)
                    {
                        if (!string.IsNullOrEmpty(f)) filters.Add(f);
                    }
                }
                foreach (var f in filters)
                {
                    argv.Add("--filter");
                    argv.Add(f);
                }

                LogLine("retoc to-legacy [" + src.Name + "]: "
                        + src.InputDir
                        + (filters.Count == 0 ? "" : " --filter " + string.Join(" --filter ", filters)));
                RunRetoc(req.RetocExe, argv.ToArray());

                // Optional UAssetAPI patches that need to see the freshly
                // extracted bytes. Caller hands us a callback because the
                // exact patching logic (Blueprint CDO vs DataAsset, which
                // properties to add) is feature-specific and lives in
                // PickupBlueprintPatcher, etc.
                if (src.AfterExtract != null)
                {
                    LogLine("Patching [" + src.Name + "] in " + stagingDir);
                    src.AfterExtract(stagingDir);
                }

                sourceResults.Add(new IoStoreCompositeSourceResult
                {
                    Name = src.Name,
                });
            }

            // Step 2: pack the unified staging tree as IoStore. retoc
            // wants the .utoc target; it auto-creates the sibling .pak
            // and .ucas at the same basename.
            LogLine("retoc to-zen: " + stagingDir + " -> " + outUtoc);
            RunRetoc(req.RetocExe, new[]
            {
                "to-zen",
                "--version", "UE5_6",
                stagingDir,
                outUtoc,
            });

            if (!File.Exists(outPak) || !File.Exists(outUcas) || !File.Exists(outUtoc))
            {
                throw new InvalidOperationException(
                    "retoc to-zen reported success but one or more triplet files are missing:\n"
                    + "  pak : " + outPak + " exists=" + File.Exists(outPak) + "\n"
                    + "  ucas: " + outUcas + " exists=" + File.Exists(outUcas) + "\n"
                    + "  utoc: " + outUtoc + " exists=" + File.Exists(outUtoc));
            }

            return new IoStoreCompositeResult
            {
                PakPath  = outPak,
                UcasPath = outUcas,
                UtocPath = outUtoc,
                PakSize  = new FileInfo(outPak).Length,
                UcasSize = new FileInfo(outUcas).Length,
                UtocSize = new FileInfo(outUtoc).Length,
                StagingDir = stagingDir,
                Sources = sourceResults,
            };
        }

        void RunRetoc(string retocExe, string[] args)
        {
            var psi = new ProcessStartInfo
            {
                FileName = retocExe,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            foreach (var a in args) psi.ArgumentList.Add(a);

            WineHelper.ApplyWine(psi);
            var proc = Process.Start(psi);
            var stdout = proc.StandardOutput.ReadToEnd();
            var stderr = proc.StandardError.ReadToEnd();
            proc.WaitForExit();

            if (proc.ExitCode != 0)
            {
                throw new InvalidOperationException(
                    "retoc " + args[0] + " failed (exit " + proc.ExitCode + ")\n"
                    + (string.IsNullOrEmpty(stderr) ? stdout : stderr));
            }
        }

        void LogLine(string msg)
        {
            if (Log != null) Log(msg);
        }
    }

    public sealed class IoStoreCompositeRequest
    {
        // Absolute path to retoc.exe.
        public string RetocExe;
        // Output triplet base path WITHOUT extension; the builder appends
        // ".pak" / ".ucas" / ".utoc". Pass e.g. "<outDir>/Quartermaster_<name>_P".
        public string OutputBasePath;
        // Per-build temp tree root. The builder owns "<TempDir>/legacy/"
        // entirely (wipes + recreates).
        public string TempDir;
        // Whether to delete pre-existing triplet files at OutputBasePath.
        public bool Overwrite = true;
        // One or more sources contributing Legacy assets to the staging
        // tree, processed in order.
        public List<IoStoreCompositeSource> Sources = new List<IoStoreCompositeSource>();
    }

    // One contributing source. Either game-paks-with-filter (vanilla
    // extraction) or a reference-mod path that already contains the
    // pre-cooked assets we want to ship verbatim.
    public sealed class IoStoreCompositeSource
    {
        // Human-readable label used in the build log.
        public string Name;
        // retoc to-legacy <InputDir>. For vanilla extraction this is the
        // game's Paks/ dir (must contain global.utoc). For reference-mod
        // adoption, point this at a directory containing the mod's
        // .pak/.ucas/.utoc PLUS a copy of the game's global.{ucas,utoc}
        // (retoc needs them to resolve ScriptObjects).
        //
        // Optional. When null/empty the builder skips the retoc to-legacy
        // step entirely and only invokes AfterExtract; useful when the
        // caller already owns the bytes (e.g. ShipMusicPatcher copying
        // a user-supplied SoundWave triplet straight into staging) and
        // wants the IoStoreCompositeBuilder to handle just the to-zen
        // packing at the end.
        public string InputDir;
        // Optional --filter for retoc to-legacy. Empty means "extract every
        // asset in InputDir" - only safe for tightly scoped mod extracts.
        public string Filter;
        // Optional additional filters appended to Filter. retoc accepts
        // repeated --filter flags and OR-matches them, so this lets one
        // source pull N specific assets in a single to-legacy call (used
        // by NoSmoke to extract up to 7 Niagara assets in one shot).
        public List<string> Filters;
        // Optional callback invoked AFTER this source's to-legacy step. The
        // staging dir (where assets just landed) is passed in, and the
        // callback can mutate any of the freshly written .uasset/.uexp
        // pairs (e.g. via PickupBlueprintPatcher). Use this for vanilla
        // sources that need a UAssetAPI patch; leave null for sources that
        // ship their bytes 1:1.
        public Action<string> AfterExtract;
    }

    public sealed class IoStoreCompositeResult
    {
        public string PakPath;
        public string UcasPath;
        public string UtocPath;
        public long PakSize;
        public long UcasSize;
        public long UtocSize;
        // The legacy staging tree retoc to-zen consumed; useful for
        // post-mortem inspection when keepTemp=true on the pipeline.
        public string StagingDir;
        public List<IoStoreCompositeSourceResult> Sources;
    }

    public sealed class IoStoreCompositeSourceResult
    {
        public string Name;
    }
}
