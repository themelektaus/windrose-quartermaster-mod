using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Windrose.Quartermaster.Core
{
    // Reads the vanilla DA_BuildLimits_FastTravel.json (a small R5BuildingLimits
    // DataAsset shipped as JSON in the Pak1 part of pakchunk0-Windows.pak)
    // and writes a patched copy under outDir, mirroring the in-pak path so
    // PakBuilder can pack the result without any path massaging.
    //
    // The vanilla file has exactly three AmountLimits entries:
    //   [0] DA_BI_Utilities_FastTravel_Bell     MaxAmount 10  (bell variant 1)
    //   [1] DA_BI_Utilities_FastTravelBell_02   MaxAmount 10  (bell variant 2)
    //   [2] DA_BI_SignalFireT01                 MaxAmount  3  (signal fire)
    //
    // BellCap controls both bell entries; SignalFireCap controls the third.
    // We classify by the path-suffix of the entry's Collection[0] reference,
    // not by index, so a future game patch that reorders or adds entries
    // doesn't silently corrupt the output.
    //
    // The patcher is a no-op when the resolved caps equal the vanilla
    // defaults (10/3) - it returns Written=false and writes nothing. The
    // BuildPipeline uses that signal to skip including the JSON in the
    // pak entirely (otherwise we'd ship a bit-for-bit copy of vanilla,
    // which is harmless but pollutes mod-diff tooling).
    public sealed class BellLimitsPatcher
    {
        public const int VanillaBellCap = 10;
        public const int VanillaSignalFireCap = 3;

        // Substring matchers against the Collection[0] reference path.
        // Case-insensitive to survive a future cosmetic rename.
        const string BellMarker1 = "DA_BI_Utilities_FastTravel_Bell";
        const string BellMarker2 = "DA_BI_Utilities_FastTravelBell_02";
        const string SignalFireMarker = "DA_BI_SignalFireT01";

        static readonly UTF8Encoding Utf8NoBom = new UTF8Encoding(false);

        public BellLimitsPatchResult PatchToDirectory(
            string vanillaBuildingLimitsDir, string outDir,
            int? bellCap, int? signalFireCap)
        {
            if (string.IsNullOrEmpty(vanillaBuildingLimitsDir))
                throw new ArgumentNullException("vanillaBuildingLimitsDir");
            if (string.IsNullOrEmpty(outDir))
                throw new ArgumentNullException("outDir");

            var result = new BellLimitsPatchResult();

            // Resolve effective caps (null = vanilla); short-circuit if both
            // are vanilla - nothing to write.
            int effBell = bellCap ?? VanillaBellCap;
            int effSignal = signalFireCap ?? VanillaSignalFireCap;
            ValidateCaps(effBell, effSignal);

            if (effBell == VanillaBellCap && effSignal == VanillaSignalFireCap)
            {
                result.Skipped = true;
                result.BellCap = effBell;
                result.SignalFireCap = effSignal;
                return result;
            }

            var vanillaJsonPath = Path.Combine(
                vanillaBuildingLimitsDir, "DA_BuildLimits_FastTravel.json");
            if (!File.Exists(vanillaJsonPath))
                throw new FileNotFoundException(
                    "Vanilla DA_BuildLimits_FastTravel.json not found: "
                    + vanillaJsonPath
                    + " - run Setup to dump the BuildingLimits/ tree.");

            var raw = File.ReadAllText(vanillaJsonPath, Encoding.UTF8);
            var root = JsonNode.Parse(raw);
            if (root == null)
                throw new InvalidDataException(
                    "Failed to parse " + vanillaJsonPath + " as JSON");

            var amounts = root["AmountLimits"]?.AsArray();
            if (amounts == null)
                throw new InvalidDataException(
                    "DA_BuildLimits_FastTravel.json is missing the AmountLimits array.");

            var unmatched = new List<string>();
            foreach (var entryNode in amounts)
            {
                if (entryNode == null) continue;
                var collection = entryNode["Collection"]?.AsArray();
                if (collection == null || collection.Count == 0) continue;
                var refPath = collection[0]?.GetValue<string>() ?? "";

                int? targetCap = ClassifyEntry(refPath, effBell, effSignal);
                if (!targetCap.HasValue)
                {
                    // Future asset added by a game patch we don't recognise.
                    // Leave it untouched (preserves the vanilla cap).
                    unmatched.Add(refPath);
                    continue;
                }

                var oldNode = entryNode["MaxAmount"];
                int oldVal = oldNode != null ? oldNode.GetValue<int>() : -1;
                if (oldVal == targetCap.Value)
                {
                    // No-op for this entry (e.g. user picked BellCap=10).
                    continue;
                }

                entryNode["MaxAmount"] = targetCap.Value;
                if (refPath.IndexOf(BellMarker2, StringComparison.OrdinalIgnoreCase) >= 0
                    || refPath.IndexOf(BellMarker1, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    result.BellsPatched++;
                }
                else if (refPath.IndexOf(SignalFireMarker, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    result.SignalFiresPatched++;
                }
            }

            // Bail without writing if classification matched nothing - we
            // must not silently ship a pak with the same content as vanilla
            // and pretend we patched something.
            if (result.BellsPatched == 0 && result.SignalFiresPatched == 0)
            {
                result.Skipped = true;
                result.BellCap = effBell;
                result.SignalFireCap = effSignal;
                result.Unmatched = unmatched;
                return result;
            }

            // Write back with the same indentation style as the vanilla
            // file (tab-indent, the form the game cooker emits).
            var serialized = root.ToJsonString(new JsonSerializerOptions
            {
                WriteIndented = true,
                Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            });

            // System.Text.Json indents with two spaces; rewrite to tabs to
            // match the vanilla cooker output (cosmetic, but keeps diffs
            // clean against any future side-by-side compare against the
            // vanilla source).
            serialized = ConvertSpaceIndentToTabs(serialized);

            // Mirror the in-pak directory layout exactly: the dumper
            // extracted to .../Vanilla/R5/Content/Gameplay/BuildingLimits/
            // so we write to <outDir>/R5/Content/Gameplay/BuildingLimits/.
            var outFile = Path.Combine(outDir, "R5", "Content", "Gameplay",
                "BuildingLimits", "DA_BuildLimits_FastTravel.json");
            Directory.CreateDirectory(Path.GetDirectoryName(outFile));
            File.WriteAllText(outFile, serialized, Utf8NoBom);

            result.Written = true;
            result.OutputPath = outFile;
            result.BellCap = effBell;
            result.SignalFireCap = effSignal;
            result.Unmatched = unmatched;
            return result;
        }

        // null = entry isn't one of our known categories (don't touch).
        static int? ClassifyEntry(string refPath, int bellCap, int signalFireCap)
        {
            if (string.IsNullOrEmpty(refPath)) return null;

            // Order matters: Bell_02 must match before the broader Bell match,
            // since "FastTravelBell_02" contains "FastTravel_Bell" via fuzz
            // would NOT match (it's "FastTravel_Bell" vs "FastTravelBell_02"
            // - different separator), but we still anchor the more specific
            // marker first to be robust against any future renaming.
            if (refPath.IndexOf(BellMarker2, StringComparison.OrdinalIgnoreCase) >= 0)
                return bellCap;
            if (refPath.IndexOf(BellMarker1, StringComparison.OrdinalIgnoreCase) >= 0)
                return bellCap;
            if (refPath.IndexOf(SignalFireMarker, StringComparison.OrdinalIgnoreCase) >= 0)
                return signalFireCap;
            return null;
        }

        static string ConvertSpaceIndentToTabs(string s)
        {
            // System.Text.Json uses 2-space indent. Convert leading runs of
            // 2*N spaces to N tabs. Lines that don't start with " " are
            // copied as-is. This is a no-op for content that already uses
            // tabs.
            var sb = new StringBuilder(s.Length);
            int i = 0;
            while (i < s.Length)
            {
                int leading = 0;
                while (i + leading < s.Length && s[i + leading] == ' ') leading++;
                int tabs = leading / 2;
                int remainder = leading % 2;
                for (int t = 0; t < tabs; t++) sb.Append('\t');
                for (int r = 0; r < remainder; r++) sb.Append(' ');
                i += leading;
                while (i < s.Length && s[i] != '\n')
                {
                    sb.Append(s[i]);
                    i++;
                }
                if (i < s.Length)
                {
                    sb.Append('\n');
                    i++;
                }
            }
            return sb.ToString();
        }

        static void ValidateCaps(int bellCap, int signalFireCap)
        {
            if (bellCap < 1 || bellCap > 10000)
                throw new ArgumentOutOfRangeException("bellCap",
                    bellCap, "BellCap must be between 1 and 10000");
            if (signalFireCap < 1 || signalFireCap > 10000)
                throw new ArgumentOutOfRangeException("signalFireCap",
                    signalFireCap, "SignalFireCap must be between 1 and 10000");
        }
    }

    public sealed class BellLimitsPatchResult
    {
        // True when both caps resolved to vanilla defaults OR no entry
        // matched a known marker - no JSON was written.
        public bool Skipped;
        // True when a patched JSON was written to OutputPath.
        public bool Written;
        public string OutputPath;
        public int BellsPatched;        // number of bell-variant entries updated (0..2)
        public int SignalFiresPatched;  // number of signal-fire entries updated (0..1)
        public int BellCap;
        public int SignalFireCap;
        // Collection-paths that didn't match any known marker - typically
        // empty; non-empty would mean a game update added a new BuildingLimits
        // entry we should consider exposing in the GUI.
        public List<string> Unmatched;
    }
}
