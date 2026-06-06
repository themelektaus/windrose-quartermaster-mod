using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace Windrose.Quartermaster.Core
{
    // "Building Degrees of Freedom": merges finer rotation steps (a subset of
    // 1/5/10 deg) into R5BuildingSettings.RotationSteps. The values are config-
    // backed (DefaultR5BuildingSettings.ini, section [/Script/R5.R5BuildingSettings],
    // repeated "+RotationSteps = N" lines), so the override ships as a loose INI in
    // the main legacy pak - no DLL / UE4SS needed.
    //
    // Drift-safe: the vanilla step list is read from the file rather than hard-coded,
    // so a game update that changes 15/30/45/90 still merges correctly. A
    // "!RotationSteps=ClearArray" directive is prepended so UE5 replaces (not
    // concatenates) the array across paks. DefaultRotationStep is left at vanilla -
    // the feature is purely additive.
    public sealed class BuildingRotationPatcher
    {
        const string SectionHeader = "[/Script/R5.R5BuildingSettings]";
        const string ClearArray = "!RotationSteps=ClearArray";

        // Matches a single "+RotationSteps = N" line (vanilla uses spaces around =).
        static readonly Regex StepLine = new Regex(
            @"^[ \t]*\+RotationSteps[ \t]*=[ \t]*(\d+)[ \t]*\r?$",
            RegexOptions.Multiline | RegexOptions.Compiled);

        public Action<string> Log;

        // fineSteps is the enabled subset of {1,5,10} (any subset, any order).
        public BuildingRotationResult PatchToFile(
            string vanillaIniPath, string outIniPath, IReadOnlyList<int> fineSteps)
        {
            if (string.IsNullOrEmpty(vanillaIniPath))
                throw new ArgumentNullException("vanillaIniPath");
            if (string.IsNullOrEmpty(outIniPath))
                throw new ArgumentNullException("outIniPath");
            if (fineSteps == null) throw new ArgumentNullException("fineSteps");
            if (!File.Exists(vanillaIniPath))
                throw new FileNotFoundException("Vanilla INI not found: " + vanillaIniPath);

            var raw = File.ReadAllText(vanillaIniPath);
            var eol = raw.Contains("\r\n") ? "\r\n" : "\n";

            if (raw.IndexOf(SectionHeader, StringComparison.Ordinal) < 0)
            {
                throw new InvalidOperationException(
                    "BuildingRotationPatcher: section header '" + SectionHeader
                    + "' not found in " + vanillaIniPath
                    + " - the vanilla file's structure may have changed.");
            }

            var matches = StepLine.Matches(raw);
            if (matches.Count == 0)
            {
                throw new InvalidOperationException(
                    "BuildingRotationPatcher: no '+RotationSteps = N' lines found in "
                    + vanillaIniPath + " - the vanilla DefaultR5BuildingSettings.ini "
                    + "layout may have changed. Delete the cached file under "
                    + "Sources/Vanilla/ to force a re-extract.");
            }

            var vanillaSteps = matches
                .Select(m => int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture))
                .ToList();

            // Merge: vanilla steps + the enabled fine steps, de-duped, ascending.
            var finalSet = new SortedSet<int>(vanillaSteps);
            var addedSteps = new List<int>();
            foreach (var s in fineSteps)
            {
                if (finalSet.Add(s)) addedSteps.Add(s);
            }
            addedSteps.Sort();
            var finalSteps = finalSet.ToList();

            // Replace the contiguous vanilla "+RotationSteps" block (first..last match)
            // with the clear directive + the merged list. Each match excludes its
            // trailing newline, so the newline after the last line is preserved.
            var first = matches[0];
            var last = matches[matches.Count - 1];
            int spanStart = first.Index;
            int spanEnd = last.Index + last.Length;

            var sb = new System.Text.StringBuilder();
            sb.Append(ClearArray);
            foreach (var v in finalSteps)
            {
                sb.Append(eol).Append("+RotationSteps = ")
                  .Append(v.ToString(CultureInfo.InvariantCulture));
            }

            var patched = raw.Substring(0, spanStart) + sb.ToString() + raw.Substring(spanEnd);

            var outDir = Path.GetDirectoryName(outIniPath);
            if (!string.IsNullOrEmpty(outDir)) Directory.CreateDirectory(outDir);
            File.WriteAllText(outIniPath, patched);

            LogLine("BuildingRotation: merged steps into DefaultR5BuildingSettings.ini "
                    + "(added " + (addedSteps.Count == 0 ? "none" : string.Join("/", addedSteps))
                    + " deg; vanilla [" + string.Join(", ", vanillaSteps)
                    + "] -> [" + string.Join(", ", finalSteps) + "])");

            return new BuildingRotationResult
            {
                Enabled = true,
                AddedSteps = addedSteps,
                FinalSteps = finalSteps,
                VanillaSteps = vanillaSteps,
            };
        }

        void LogLine(string msg)
        {
            if (Log != null) Log(msg);
        }
    }
}
