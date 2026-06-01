using System;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;

namespace Windrose.Quartermaster.Core
{
    public sealed class MinimapRangePatcher
    {
        public const double VanillaFootRevealBrushSize = 37.0;
        public const double VanillaFootMiniMapShowDistance = 250.0;
        public const double VanillaShipRevealBrushSize = 290.0;
        public const double VanillaShipMiniMapShowDistance = 750.0;

        public const double MinMultiplier = 1.0;
        public const double MaxMultiplier = 10.0;

        public Action<string> Log;

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

            var raw = File.ReadAllText(vanillaIniPath);

            // UE5 merges config arrays across paks unless reset; without this
            // directive the engine would concatenate the vanilla +MapsConfig tuple
            // with ours instead of replacing it.
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
                var eol = raw.Contains("\r\n") ? "\r\n" : "\n";
                int afterHeader = sectionIdx + sectionHeader.Length;
                int insertAt = afterHeader;
                if (insertAt < raw.Length && raw[insertAt] == '\r') insertAt++;
                if (insertAt < raw.Length && raw[insertAt] == '\n') insertAt++;
                raw = raw.Substring(0, insertAt) + clearArray + eol + raw.Substring(insertAt);
            }

            // Single Regex.Replace over all four patterns: sequential passes would
            // re-match earlier output when a multiplier promotes a foot value into
            // a ship value (e.g. foot dist 250 -> 750 collides with ship dist 750).
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
                throw new InvalidOperationException(
                    "MinimapRangePatcher: unexpected regex hit '" + t + "'");
            });

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

        // UE5 cooks INI floats as exactly 6 decimal digits; needle and replacement
        // must match that representation byte-for-byte.
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
