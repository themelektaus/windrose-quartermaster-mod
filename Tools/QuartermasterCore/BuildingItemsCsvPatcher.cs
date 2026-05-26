using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Windrose.Quartermaster.Core.BuildingCreator;

namespace Windrose.Quartermaster.Core
{
    // Writes a per-profile R5/Content/Localization/Data/BuildingItems_<shortProfileId>.csv
    // string-table containing one row per cloned-building FText key. The
    // Windrose game registers every CSV under that directory at boot as a
    // StringTable whose TableId equals the filename stem, so the per-
    // profile naming gives each profile its own independently-loaded
    // string-table - two profiles can ship custom buildings without
    // their (identically-named) "BuildingItems.csv" overrides colliding
    // via pak load-order.
    //
    // Pairs with FTextKeyRewriter which rewrites each cloned building
    // DA's FText.TableId FName from "BuildingItems" to the same per-
    // profile id, so the DA body and CSV resolve against each other.
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
    // Output formatting matches vanilla exactly: UTF-8 with BOM (the
    // Windrose CSV loader expects the BOM marker), CRLF line endings,
    // the standard "Key,SourceString,Context" header, and doubled-double-
    // quote escaping for each data row.
    public sealed class BuildingItemsCsvPatcher
    {
        // BOM (\xEF\xBB\xBF) prefix on the header line. The vanilla CSVs
        // start with it and the Windrose CSV-loader uses it as a sanity
        // marker - omitting the BOM here would make the loader reject
        // the file silently.
        static readonly byte[] Utf8Bom = new byte[] { 0xEF, 0xBB, 0xBF };

        // CSV header. Matches the vanilla layout exactly.
        const string CsvHeader = "Key,SourceString,Context\r\n";

        // No-BOM UTF-8 (the vanilla CSV body is saved this way; the BOM
        // is a single 3-byte prefix on the file, not on every line).
        static readonly UTF8Encoding Utf8NoBom = new UTF8Encoding(false);

        public Action<string> Log;

        // Emits the per-profile CSV into
        //   <outDir>/R5/Content/Localization/Data/BuildingItems_<shortProfileId>.csv
        // based on the per-Building OutputNameKey / OutputDescriptionKey
        // set on each BuildingPatchResult.
        //
        // profileId: the full GUID-form Profile.Id. WindrosePaths.ShortProfileId
        // strips dashes and takes the first 8 hex chars - that suffix
        // becomes the CSV filename stem and the FText TableId. Must be
        // non-empty.
        public BuildingItemsCsvPatchResult PatchToDirectory(
            string outDir,
            string profileId,
            IList<BuildingPatchResult> buildingResults)
        {
            if (string.IsNullOrEmpty(outDir))   throw new ArgumentNullException("outDir");
            if (string.IsNullOrEmpty(profileId)) throw new ArgumentNullException("profileId");

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

            var pakInternalPath = WindrosePaths.BuildingItemsCsvPakPathFor(profileId);
            WriteCsv(pakInternalPath, outDir, csvRows, result);
            return result;
        }

        // Writes a fresh CSV at <outDir>/<pakInternalPath>. No vanilla
        // baseline is included - the per-profile TableId is brand-new
        // and only carries this profile's keys, so a small focused file
        // is correct (and avoids inflating every profile's pak with the
        // full vanilla CSV body). The header + BOM match vanilla so the
        // Windrose loader accepts it.
        void WriteCsv(string pakInternalPath, string outDir,
                      IList<CsvRow> rows, BuildingItemsCsvPatchResult result)
        {
            var outPath = Path.Combine(outDir, pakInternalPath.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(outPath));

            using var ms = new MemoryStream();
            ms.Write(Utf8Bom, 0, Utf8Bom.Length);
            var headerBytes = Utf8NoBom.GetBytes(CsvHeader);
            ms.Write(headerBytes, 0, headerBytes.Length);

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
            result.CsvOutPath = outPath;
            LogLine("Per-profile BuildingItems CSV written: " + outPath + " (" + rows.Count + " rows)");
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
        // Absolute on-disk path of the written CSV (per-profile, lives
        // under <outDir>/R5/Content/Localization/Data/BuildingItems_<shortId>.csv).
        public string CsvOutPath;
    }
}
