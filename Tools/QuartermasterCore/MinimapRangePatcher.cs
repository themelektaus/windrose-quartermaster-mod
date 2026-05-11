using System;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;

namespace Windrose.Quartermaster.Core
{
    // Renders a patched R5/Config/DefaultR5MapSettings.ini from the
    // vanilla baseline by scaling the four reveal-range fields by a
    // user-supplied multiplier.
    //
    // The vanilla file has a single +MapsConfig=(...) tuple. Inside it
    // sit one DefaultRevealData and a RevealData array with six entries
    // (R5Character, ShallowBoat, Ketch, Brig, Frigate, BatteryPawn).
    // Each carries two scalars we want to scale:
    //
    //     RevealBrushSize           (vanilla 37 for foot/ShallowBoat,
    //                                vanilla 290 for the four ship classes)
    //     MiniMapShowDistance       (vanilla 250 for foot/ShallowBoat,
    //                                vanilla 750 for the four ship classes)
    //
    // Both classes scale by the SAME multiplier: that's the linear
    // scaling the BetterMinimapRange_2x_2x_P reference mod ships, just
    // tunable instead of hardcoded to 2x. MaxMapResolution stays at the
    // vanilla 2048 (the reference mod doesn't change it either despite
    // the "_2x_2x_" filename - the second 2x refers to MiniMapShowDistance,
    // not resolution).
    //
    // The patch is applied as four targeted regex replacements rather
    // than parsing the whole config grammar: the file ships as a flat
    // KEY=VALUE format with the entire +MapsConfig=(...) tuple as a
    // single ~2 KB line, and reading the existing values plus rewriting
    // them line-by-line would balloon the patcher without buying
    // anything (every other config knob in the file stays vanilla).
    public sealed class MinimapRangePatcher
    {
        // Vanilla scalar values the regex replacements key off. If a
        // future game patch changes these, the regexes stop matching
        // and the patcher fails fast with a clear error rather than
        // silently shipping an unscaled file.
        public const double VanillaFootRevealBrushSize = 37.0;
        public const double VanillaFootMiniMapShowDistance = 250.0;
        public const double VanillaShipRevealBrushSize = 290.0;
        public const double VanillaShipMiniMapShowDistance = 750.0;

        // Allowed multiplier range. 1.0 == vanilla (no-op), upper bound
        // is generous; anything beyond ~5x lets the player see most of
        // the map at once which kills exploration.
        public const double MinMultiplier = 1.0;
        public const double MaxMultiplier = 10.0;

        public Action<string> Log;

        // Reads vanillaIniPath, scales the four fields by `multiplier`,
        // and writes the result to outIniPath (creating parent dirs as
        // needed). Returns counts of replacements per field so callers
        // can sanity-check the patcher actually hit the expected ~4 sites
        // each.
        //
        // Throws InvalidOperationException if a field is missing or
        // its count doesn't match the expected vanilla layout - silent
        // partial patches would ship a half-vanilla, half-patched INI
        // which would be impossible to diagnose at runtime.
        public MinimapRangePatchResult PatchToFile(
            string vanillaIniPath, string outIniPath, double multiplier)
        {
            if (string.IsNullOrEmpty(vanillaIniPath))
                throw new ArgumentNullException("vanillaIniPath");
            if (string.IsNullOrEmpty(outIniPath))
                throw new ArgumentNullException("outIniPath");
            if (!File.Exists(vanillaIniPath))
                throw new FileNotFoundException("Vanilla INI not found: " + vanillaIniPath);
            if (multiplier < MinMultiplier || multiplier > MaxMultiplier)
                throw new ArgumentOutOfRangeException("multiplier",
                    "Multiplier " + multiplier + " is outside [" + MinMultiplier
                    + ", " + MaxMultiplier + "] - the GUI should have clamped this.");

            // Vanilla 5.6 ships this file as 7 lines (3 leading blanks,
            // [/Script/R5.R5MapSettings], +MapsConfig=(...), 2 trailing
            // blanks). UTF-8 without BOM, CRLF line endings on Windows.
            // We preserve everything verbatim except the four scalar
            // fields - byte-identical output for multiplier==1.0
            // EXCEPT for the inserted !MapsConfig=ClearArray directive
            // (see below).
            var raw = File.ReadAllText(vanillaIniPath);

            // UE5's config system MERGES arrays across hierarchy levels
            // unless an explicit ClearArray directive resets them. Without
            // !MapsConfig=ClearArray, when our mod-pak's copy of the file
            // is mounted alongside the vanilla pak, the engine would
            // potentially concatenate both +MapsConfig=(...) tuples
            // (vanilla + ours) instead of replacing the vanilla one - the
            // game then either picks the wrong tuple or merges fields in
            // ways that defeat the patch. Inserting the directive right
            // after the section header guarantees the vanilla array is
            // dropped before our patched +MapsConfig=(...) is applied.
            // The reference mod BetterMinimapRange_2x_2x_P does the same.
            const string sectionHeader = "[/Script/R5.R5MapSettings]";
            const string clearArray = "!MapsConfig=ClearArray";
            int sectionIdx = raw.IndexOf(sectionHeader, StringComparison.Ordinal);
            if (sectionIdx < 0)
            {
                throw new InvalidOperationException(
                    "MinimapRangePatcher: section header '" + sectionHeader
                    + "' not found in " + vanillaIniPath
                    + " - the vanilla file's structure may have changed.");
            }
            if (raw.IndexOf(clearArray, StringComparison.Ordinal) < 0)
            {
                // Determine the line ending the file uses so the inserted
                // directive matches the surrounding style (vanilla 5.6
                // ships CRLF, but be defensive in case a future re-dump
                // produces LF).
                var eol = raw.Contains("\r\n") ? "\r\n" : "\n";
                int afterHeader = sectionIdx + sectionHeader.Length;
                // Skip the EOL chars immediately after the header so the
                // directive lands on its own line beneath the header.
                int insertAt = afterHeader;
                if (insertAt < raw.Length && raw[insertAt] == '\r') insertAt++;
                if (insertAt < raw.Length && raw[insertAt] == '\n') insertAt++;
                raw = raw.Substring(0, insertAt) + clearArray + eol + raw.Substring(insertAt);
            }

            // Single-pass alternation: all four vanilla patterns are
            // matched in ONE Regex.Replace call, so each occurrence is
            // visited once and rewritten to its own patched value. Doing
            // this in four sequential passes would re-match patched
            // output from earlier passes whenever the multiplier turns
            // a foot-class value into a ship-class one (e.g. multiplier=3
            // promotes foot MiniMapShowDistance 250 -> 750, which then
            // collides with the ship-class 750 pattern in the next pass).
            int footBrushHits = 0, footDistHits = 0;
            int shipBrushHits = 0, shipDistHits = 0;

            var footBrush = Vanilla(VanillaFootRevealBrushSize);
            var footDist  = Vanilla(VanillaFootMiniMapShowDistance);
            var shipBrush = Vanilla(VanillaShipRevealBrushSize);
            var shipDist  = Vanilla(VanillaShipMiniMapShowDistance);

            string footBrushPatched = "RevealBrushSize="
                + Patched(VanillaFootRevealBrushSize * multiplier);
            string footDistPatched  = "MiniMapShowDistance="
                + Patched(VanillaFootMiniMapShowDistance * multiplier);
            string shipBrushPatched = "RevealBrushSize="
                + Patched(VanillaShipRevealBrushSize * multiplier);
            string shipDistPatched  = "MiniMapShowDistance="
                + Patched(VanillaShipMiniMapShowDistance * multiplier);

            // Order in the alternation matters: ship-class patterns
            // ("RevealBrushSize=290.000000") share the field name with
            // foot-class ("RevealBrushSize=37.000000") but the regex
            // engine commits to the first matching alternative, so we
            // anchor on the FULL "field=value" string and let either
            // alternative win for a given position. Regex.Escape on the
            // literal values keeps them treated as plain strings.
            var pattern = Regex.Escape("RevealBrushSize=" + footBrush)
                + "|" + Regex.Escape("MiniMapShowDistance=" + footDist)
                + "|" + Regex.Escape("RevealBrushSize=" + shipBrush)
                + "|" + Regex.Escape("MiniMapShowDistance=" + shipDist);

            raw = Regex.Replace(raw, pattern, m =>
            {
                var t = m.Value;
                if (t == "RevealBrushSize=" + footBrush)
                {
                    footBrushHits++;
                    return footBrushPatched;
                }
                if (t == "MiniMapShowDistance=" + footDist)
                {
                    footDistHits++;
                    return footDistPatched;
                }
                if (t == "RevealBrushSize=" + shipBrush)
                {
                    shipBrushHits++;
                    return shipBrushPatched;
                }
                if (t == "MiniMapShowDistance=" + shipDist)
                {
                    shipDistHits++;
                    return shipDistPatched;
                }
                // Regex matched something we didn't enumerate - shouldn't
                // happen with literal alternation, but failing loud here
                // beats writing a stale value silently.
                throw new InvalidOperationException(
                    "MinimapRangePatcher: unexpected regex hit '" + t + "'");
            });

            // Expected hit counts per vanilla layout:
            //   foot brush  -> 3 sites: DefaultRevealData + R5Character + ShallowBoat
            //   foot dist   -> 3 sites: same as above
            //   ship brush  -> 4 sites: Ketch + Brig + Frigate + BatteryPawn
            //   ship dist   -> 4 sites: same as above
            // Anything else means the vanilla file's shape moved and
            // the patch we just made may have hit the wrong fields.
            if (footBrushHits != 3 || footDistHits != 3
                || shipBrushHits != 4 || shipDistHits != 4)
            {
                throw new InvalidOperationException(
                    "MinimapRangePatcher: unexpected hit counts in "
                    + vanillaIniPath
                    + " - foot.brush=" + footBrushHits + " (expected 3), "
                    + "foot.dist=" + footDistHits + " (expected 3), "
                    + "ship.brush=" + shipBrushHits + " (expected 4), "
                    + "ship.dist=" + shipDistHits + " (expected 4). "
                    + "The vanilla DefaultR5MapSettings.ini layout may have "
                    + "changed - delete the cached file under Sources/Vanilla/ "
                    + "to force a re-extract, or update the vanilla constants.");
            }

            var outDir = Path.GetDirectoryName(outIniPath);
            if (!string.IsNullOrEmpty(outDir)) Directory.CreateDirectory(outDir);
            // Preserve the vanilla encoding (UTF-8 no-BOM, CRLF). File.ReadAllText
            // returns the bytes as-is, and File.WriteAllText with default UTF-8
            // (without explicit Encoding) writes no BOM and preserves the
            // embedded line endings.
            File.WriteAllText(outIniPath, raw);

            LogLine("MinimapRange: patched DefaultR5MapSettings.ini "
                    + "(multiplier=" + multiplier.ToString("0.##", CultureInfo.InvariantCulture)
                    + ", foot brush " + Fmt(VanillaFootRevealBrushSize) + "->"
                    + Fmt(VanillaFootRevealBrushSize * multiplier)
                    + ", foot dist " + Fmt(VanillaFootMiniMapShowDistance) + "->"
                    + Fmt(VanillaFootMiniMapShowDistance * multiplier)
                    + ", ship brush " + Fmt(VanillaShipRevealBrushSize) + "->"
                    + Fmt(VanillaShipRevealBrushSize * multiplier)
                    + ", ship dist " + Fmt(VanillaShipMiniMapShowDistance) + "->"
                    + Fmt(VanillaShipMiniMapShowDistance * multiplier)
                    + ")");

            return new MinimapRangePatchResult
            {
                Multiplier = multiplier,
                FootBrushSites = footBrushHits,
                FootDistanceSites = footDistHits,
                ShipBrushSites = shipBrushHits,
                ShipDistanceSites = shipDistHits,
                VanillaFootBrush = VanillaFootRevealBrushSize,
                VanillaFootDistance = VanillaFootMiniMapShowDistance,
                VanillaShipBrush = VanillaShipRevealBrushSize,
                VanillaShipDistance = VanillaShipMiniMapShowDistance,
                EffectiveFootBrush = VanillaFootRevealBrushSize * multiplier,
                EffectiveFootDistance = VanillaFootMiniMapShowDistance * multiplier,
                EffectiveShipBrush = VanillaShipRevealBrushSize * multiplier,
                EffectiveShipDistance = VanillaShipMiniMapShowDistance * multiplier,
            };
        }

        // UE5 cooks float values into INI files with exactly 6 decimal
        // digits and no exponent (e.g. "37.000000"). These two helpers
        // produce strings in that shape so our needle and replacement
        // tokens match the file's representation byte-for-byte.
        static string Vanilla(double v)
        {
            return v.ToString("0.000000", CultureInfo.InvariantCulture);
        }
        static string Patched(double v)
        {
            return v.ToString("0.000000", CultureInfo.InvariantCulture);
        }

        static string Fmt(double v)
        {
            return v.ToString("0.0", CultureInfo.InvariantCulture);
        }

        void LogLine(string msg)
        {
            if (Log != null) Log(msg);
        }
    }

    public sealed class MinimapRangePatchResult
    {
        public double Multiplier;
        public int FootBrushSites;
        public int FootDistanceSites;
        public int ShipBrushSites;
        public int ShipDistanceSites;
        public double VanillaFootBrush;
        public double VanillaFootDistance;
        public double VanillaShipBrush;
        public double VanillaShipDistance;
        public double EffectiveFootBrush;
        public double EffectiveFootDistance;
        public double EffectiveShipBrush;
        public double EffectiveShipDistance;
    }
}
