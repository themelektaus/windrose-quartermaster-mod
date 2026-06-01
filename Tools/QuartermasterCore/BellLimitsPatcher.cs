using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Windrose.Quartermaster.Core
{
    public sealed class BellLimitsPatcher
    {
        public const int VanillaBellCap = 10;
        public const int VanillaSignalFireCap = 3;

        // Matched as case-insensitive substrings against the entry's Collection[0]
        // reference path; entries are classified by marker, not by array index.
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
                    unmatched.Add(refPath);
                    continue;
                }

                var oldNode = entryNode["MaxAmount"];
                int oldVal = oldNode != null ? oldNode.GetValue<int>() : -1;
                if (oldVal == targetCap.Value)
                {
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

            // Bail without writing if nothing matched: shipping a byte-identical
            // copy of vanilla while reporting a patch would be undiagnosable.
            if (result.BellsPatched == 0 && result.SignalFiresPatched == 0)
            {
                result.Skipped = true;
                result.BellCap = effBell;
                result.SignalFireCap = effSignal;
                result.Unmatched = unmatched;
                return result;
            }

            var serialized = root.ToJsonString(new JsonSerializerOptions
            {
                WriteIndented = true,
                Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            });

            // Match the vanilla cooker's tab indentation (System.Text.Json emits spaces).
            serialized = ConvertSpaceIndentToTabs(serialized);

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

        // null = entry isn't one of our known categories (leave untouched).
        static int? ClassifyEntry(string refPath, int bellCap, int signalFireCap)
        {
            if (string.IsNullOrEmpty(refPath)) return null;

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
        public bool Skipped;
        public bool Written;
        public string OutputPath;
        public int BellsPatched;
        public int SignalFiresPatched;
        public int BellCap;
        public int SignalFireCap;
        public List<string> Unmatched;
    }
}
