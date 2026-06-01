using System;
using System.Collections.Generic;
using System.IO;

namespace Windrose.Quartermaster.Core
{
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

            var tmpRoot = req.TempDir;
            if (string.IsNullOrEmpty(tmpRoot))
                tmpRoot = Path.Combine(Path.GetTempPath(), "windrose-iostore-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tmpRoot);
            var stagingDir = Path.Combine(tmpRoot, "legacy");
            if (Directory.Exists(stagingDir)) Directory.Delete(stagingDir, true);
            Directory.CreateDirectory(stagingDir);

            var sourceResults = new List<IoStoreCompositeSourceResult>(req.Sources.Count);
            foreach (var src in req.Sources)
            {
                if (src == null) throw new ArgumentException("Null source spec");
                if (string.IsNullOrEmpty(src.Name))
                    throw new ArgumentException("Source.Name is required");

                // Empty InputDir = pre-staged mode: AfterExtract must supply the files itself.
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
            var r = ToolProcess.RunCapture(retocExe, args);
            if (r.ExitCode != 0)
            {
                throw new InvalidOperationException(
                    "retoc " + args[0] + " failed (exit " + r.ExitCode + ")\n" + r.ErrOrOut);
            }
        }

        void LogLine(string msg)
        {
            if (Log != null) Log(msg);
        }
    }

    public sealed class IoStoreCompositeRequest
    {
        public string RetocExe;
        // Base path WITHOUT extension; builder appends ".pak"/".ucas"/".utoc".
        public string OutputBasePath;
        public string TempDir;
        public bool Overwrite = true;
        public List<IoStoreCompositeSource> Sources = new List<IoStoreCompositeSource>();
    }

    public sealed class IoStoreCompositeSource
    {
        public string Name;
        // Null/empty skips retoc to-legacy and only runs AfterExtract (pre-staged mode).
        public string InputDir;
        public string Filter;
        // Additional filters appended to Filter; retoc OR-matches repeated --filter flags.
        public List<string> Filters;
        // Runs after this source's to-legacy step, receiving the staging dir.
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
        public string StagingDir;
        public List<IoStoreCompositeSourceResult> Sources;
    }

    public sealed class IoStoreCompositeSourceResult
    {
        public string Name;
    }
}
