using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Windrose.Quartermaster.Core.BuildingCreator;

namespace Windrose.Quartermaster.Core
{
    // Extends the vanilla R5/Content/Localization/Data/BuildingItems.csv
    // with per-Building Name / Description rows so the FText keys that
    // FTextKeyRewriter committed to each cloned DA's body resolve to the
    // user-supplied display text at runtime.
    //
    // This is the sister of ItemCreatorPatcher's CSV emission, with two
    // structural differences:
    //   1. The KEYS come from FTextKeyRewriter's binary-rewrite output
    //      (BuildingPatchResult.OutputNameKey / OutputDescriptionKey)
    //      rather than being synthesized JSON-side. Each Building's keys
    //      were chosen to fit inside the vanilla key's byte budget so
    //      the same-length splice in the DA body works.
    //   2. There is no "skip if Description is null" rule like vanilla
    //      DAs sometimes have: if FTextKeyRewriter only committed a Name
    //      key (template carried no VanillaDescriptionKey), we emit only
    //      a Name row. Description is always optional.
    //
    // Idempotency: if no buildings produced FText keys (e.g. no custom
    // buildings in this profile, or all buildings filtered as skeletons),
    // PatchToDirectory is a no-op and returns an empty result without
    // touching disk.
    //
    // Output formatting matches vanilla exactly: UTF-8 (no BOM), CRLF
    // line endings, header preserved verbatim, doubled-double-quote
    // escaping for the appended rows. The vanilla CSV body is copied
    // verbatim and the new rows are appended at the end so diffs stay
    // minimal and re-pack tooling can verify integrity by comparing
    // prefixes.
    public sealed class BuildingItemsCsvPatcher
    {
        // Output path inside the staging directory (matches the pak-
        // internal layout under R5/Content/Localization/Data/).
        const string CsvOutRelPath = "R5/Content/Localization/Data/BuildingItems.csv";

        // No-BOM UTF-8 (the vanilla CSV is saved this way).
        static readonly UTF8Encoding Utf8NoBom = new UTF8Encoding(false);

        public Action<string> Log;

        // Emits the extended CSV into <outDir>/R5/Content/Localization/
        // Data/BuildingItems.csv based on the per-Building OutputNameKey /
        // OutputDescriptionKey set on each BuildingPatchResult.
        //
        // vanillaBuildingItemsCsvPath: absolute path to the dumped vanilla
        // CSV (WindrosePaths.VanillaBuildingItemsCsv). Must exist - if
        // setup hasn't extracted it yet, the caller should re-run Setup
        // to dump the baseline before invoking this patcher.
        public BuildingItemsCsvPatchResult PatchToDirectory(
            string vanillaBuildingItemsCsvPath,
            string outDir,
            IList<BuildingPatchResult> buildingResults)
        {
            if (string.IsNullOrEmpty(vanillaBuildingItemsCsvPath)) throw new ArgumentNullException("vanillaBuildingItemsCsvPath");
            if (string.IsNullOrEmpty(outDir))                       throw new ArgumentNullException("outDir");

            var result = new BuildingItemsCsvPatchResult();
            if (buildingResults == null || buildingResults.Count == 0)
            {
                LogLine("(no buildings - skipping BuildingItems.csv synthesis)");
                return result;
            }

            // Collect rows from every Building that actually got an FText
            // key committed. Missing key = the FTextKeyRewriter didn't
            // find the vanilla bytes in the DA body (template / vanilla
            // drift); we skip the row entirely so we don't pollute the
            // CSV with orphan entries.
            var csvRows = new List<CsvRow>(buildingResults.Count * 2);
            foreach (var b in buildingResults)
            {
                if (b == null) continue;
                if (!string.IsNullOrEmpty(b.OutputNameKey))
                {
                    csvRows.Add(new CsvRow(b.OutputNameKey, b.DisplayName ?? string.Empty));
                    result.NameRowsAppended++;
                }
                if (!string.IsNullOrEmpty(b.OutputDescriptionKey))
                {
                    csvRows.Add(new CsvRow(b.OutputDescriptionKey, b.Description ?? string.Empty));
                    result.DescriptionRowsAppended++;
                }
            }

            if (csvRows.Count == 0)
            {
                LogLine("(no FText keys committed to any building - skipping BuildingItems.csv synthesis)");
                return result;
            }

            if (!File.Exists(vanillaBuildingItemsCsvPath))
            {
                throw new FileNotFoundException(
                    "Vanilla BuildingItems.csv not found at " + vanillaBuildingItemsCsvPath
                    + " - re-run Setup so the dumper extracts it.");
            }

            WriteExtendedCsv(vanillaBuildingItemsCsvPath, outDir, csvRows, result);
            return result;
        }

        // Identical structure to ItemCreatorPatcher.WriteExtendedCsv -
        // slurp vanilla bytes, ensure trailing CRLF, append new rows
        // with CSV-escaped fields, write to outDir. Refactoring the
        // shared code into a helper isn't worth the indirection for
        // two callers and two CSV layouts.
        void WriteExtendedCsv(string vanillaCsvPath, string outDir,
                              IList<CsvRow> rows, BuildingItemsCsvPatchResult result)
        {
            var outPath = Path.Combine(outDir, CsvOutRelPath.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(outPath));

            var vanillaBytes = File.ReadAllBytes(vanillaCsvPath);

            using var ms = new MemoryStream();
            ms.Write(vanillaBytes, 0, vanillaBytes.Length);

            // Ensure trailing CRLF before we append - matches the
            // ItemCreator pattern exactly (some pak extracts produce a
            // naked last line).
            if (vanillaBytes.Length > 0)
            {
                var lastByte = vanillaBytes[vanillaBytes.Length - 1];
                if (lastByte != (byte)'\n')
                {
                    ms.WriteByte((byte)'\r');
                    ms.WriteByte((byte)'\n');
                }
            }

            foreach (var row in rows)
            {
                var line = EscapeCsvField(row.Key) + ","
                         + EscapeCsvField(row.Value) + ","
                         + EscapeCsvField(string.Empty)
                         + "\r\n";
                var lineBytes = Utf8NoBom.GetBytes(line);
                ms.Write(lineBytes, 0, lineBytes.Length);
            }

            File.WriteAllBytes(outPath, ms.ToArray());
            result.CsvRowsAppended = rows.Count;
            result.CsvWritten = true;
            LogLine("Extended BuildingItems.csv written: " + outPath + " (+" + rows.Count + " rows)");
        }

        // Standard CSV escaping: wrap in double quotes, double any
        // internal double quotes. Newlines inside the quoted value stay
        // literal - matches multi-paragraph descriptions in the vanilla
        // CSV (see e.g. "Decoration_Paintings_T02_Description").
        static string EscapeCsvField(string s)
        {
            if (s == null) s = string.Empty;
            return "\"" + s.Replace("\"", "\"\"") + "\"";
        }

        void LogLine(string msg) { if (Log != null) Log(msg); }

        readonly struct CsvRow
        {
            public readonly string Key;
            public readonly string Value;
            public CsvRow(string key, string value) { Key = key; Value = value; }
        }
    }

    public sealed class BuildingItemsCsvPatchResult
    {
        public bool CsvWritten;
        public int CsvRowsAppended;
        public int NameRowsAppended;
        public int DescriptionRowsAppended;
    }
}
