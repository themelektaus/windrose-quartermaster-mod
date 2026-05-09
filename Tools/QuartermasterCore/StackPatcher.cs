using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace Windrose.Quartermaster.Core
{
    // Reads vanilla item JSONs and writes a parallel directory containing only
    // the items that changed under a given Profile. The output directory is
    // what gets fed into repak.
    //
    // Resolver rule per item (StackSize):
    //   1. profile.Overrides[itemId].StackSize  (unconditional, even Equipment)
    //   2. profile.Globals.StackSize.Absolute
    //   3. vanillaStack * Globals.StackSize.Multiplier  (clamped at Cap)
    //   4. null  -> skip (item stays vanilla in-game)
    //
    // For vanillaStack == 1 items, globals (steps 2/3) only apply when the item
    // is "promotable" -- i.e. one of:
    //   * ItemClass == "Consumable"
    //   * ItemType.TagName == "Inventory.ItemType.Resource"  (game's own
    //     classification -- catches treasure/loot Misc items like the
    //     Senkamati pieces that look like resources to the game but live
    //     under Category=Misc)
    //   * ItemClass == "Default" && Category == "Resource"   (legacy folder-
    //     based rule; covers a couple of items the game still tags Quest.Other
    //     even though they sit in Resource/)
    // Equipment / NPCs / Ship cannons / Quest tokens stay at 1 unless the
    // user explicitly sets a per-item override.
    //
    // Behaviour matches Library/Apply.ps1 byte-for-byte (same regex pass on the
    // raw JSON, single-occurrence replace, UTF-8 no-BOM output).
    public sealed class StackPatcher
    {
        // Parsing regexes -- intentionally identical to the PS version.
        static readonly Regex MaxCountRegex = new Regex(
            "(\"MaxCountInSlot\"\\s*:\\s*)(\\d+)",
            RegexOptions.Compiled);

        static readonly Regex ItemClassRegex = new Regex(
            "\"ItemClass\"\\s*:\\s*\"([^\"]+)\"",
            RegexOptions.Compiled);

        static readonly Regex CategoryRegex = new Regex(
            "\"Category\"\\s*:\\s*\"([^\"]+)\"",
            RegexOptions.Compiled);

        // Matches the nested ItemType { TagName: ... } block. The TagName key
        // appears in many other contexts (ItemTag, ActivationAbilityTag, ...),
        // so we anchor on the parent property name to avoid false positives.
        static readonly Regex ItemTypeTagRegex = new Regex(
            "\"ItemType\"\\s*:\\s*\\{\\s*\"TagName\"\\s*:\\s*\"([^\"]+)\"",
            RegexOptions.Compiled);

        static readonly UTF8Encoding Utf8NoBom = new UTF8Encoding(false);

        // PowerShell-style wildcards relative to vanillaDir; default excludes
        // dev-only test items. Configurable per instance for unit tests / future
        // profile-level filters.
        public string[] ExcludeRelativePaths = new[] { "*\\Tests\\*" };

        public PatchResult PatchToDirectory(string vanillaDir, string outDir, Profile profile)
        {
            if (string.IsNullOrEmpty(vanillaDir)) throw new ArgumentNullException("vanillaDir");
            if (string.IsNullOrEmpty(outDir))    throw new ArgumentNullException("outDir");
            if (profile == null)                  throw new ArgumentNullException("profile");
            if (!Directory.Exists(vanillaDir))    throw new DirectoryNotFoundException(vanillaDir);

            ValidateProfile(profile);
            Directory.CreateDirectory(outDir);

            var result = new PatchResult();
            var vanillaFull = Path.GetFullPath(vanillaDir);
            var overrides = profile.Overrides ?? new Dictionary<string, ItemOverride>(0);
            var stackGlobal = profile.Globals != null ? profile.Globals.StackSize : null;

            foreach (var jsonPath in Directory.EnumerateFiles(vanillaFull, "*.json", SearchOption.AllDirectories))
            {
                result.Scanned++;
                var rel = jsonPath.Substring(vanillaFull.Length).TrimStart(
                    Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

                if (IsExcluded(rel))
                {
                    result.Excluded++;
                    continue;
                }

                var content = File.ReadAllText(jsonPath, Encoding.UTF8);
                var match = MaxCountRegex.Match(content);
                if (!match.Success)
                {
                    result.NoSchema++;
                    continue;
                }

                var oldVal = int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture);
                var itemId = Path.GetFileNameWithoutExtension(jsonPath);

                int? userOverride = null;
                ItemOverride itemOverride;
                if (overrides.TryGetValue(itemId, out itemOverride) && itemOverride != null)
                {
                    userOverride = itemOverride.StackSize;
                }

                int? target;
                bool wasCapped = false;
                bool wasPromoted = false;
                bool fromOverride = false;

                if (userOverride.HasValue)
                {
                    // Per-item override: the user explicitly asked for this value,
                    // so we apply it unconditionally -- no Equipment/Ship gate.
                    target = userOverride.Value;
                    fromOverride = true;
                    if (oldVal <= 1) wasPromoted = true;
                }
                else
                {
                    // No per-item override: globals only apply if the item is
                    // (a) already stackable (oldVal > 1) or (b) "promotable"
                    // (Consumable / Default+Resource at oldVal == 1).
                    if (oldVal <= 1 && !IsPromotable(content))
                    {
                        result.Skipped++;
                        continue;
                    }

                    target = ComputeFromGlobal(stackGlobal, oldVal, out wasCapped);
                    if (target.HasValue && oldVal <= 1) wasPromoted = true;
                }

                if (!target.HasValue || target.Value == oldVal)
                {
                    result.UnchangedSkip++;
                    continue;
                }

                var newVal = target.Value;
                var newContent = MaxCountRegex.Replace(
                    content,
                    m => m.Groups[1].Value + newVal.ToString(CultureInfo.InvariantCulture),
                    1);

                var outPath = Path.Combine(outDir, rel);
                Directory.CreateDirectory(Path.GetDirectoryName(outPath));
                File.WriteAllText(outPath, newContent, Utf8NoBom);

                result.Written++;
                if (wasPromoted)  result.Promoted++;
                if (fromOverride) result.Overridden++;
                if (wasCapped)    result.Capped++;
                result.WrittenItems.Add(itemId);
            }

            return result;
        }

        static int? ComputeFromGlobal(StackSizeGlobal g, int vanilla, out bool wasCapped)
        {
            wasCapped = false;
            if (g == null) return null;

            if (g.Absolute.HasValue)
            {
                return g.Absolute.Value;
            }

            if (g.Multiplier.HasValue)
            {
                long target = (long)vanilla * g.Multiplier.Value;
                if (g.Cap.HasValue && g.Cap.Value > 0 && target > g.Cap.Value)
                {
                    target = g.Cap.Value;
                    wasCapped = true;
                }
                if (target > int.MaxValue) target = int.MaxValue;
                return (int)target;
            }

            return null;
        }

        static bool IsPromotable(string content)
        {
            string itemClass = null;
            string category = null;
            string itemType = null;

            var icMatch = ItemClassRegex.Match(content);
            if (icMatch.Success) itemClass = icMatch.Groups[1].Value;

            var catMatch = CategoryRegex.Match(content);
            if (catMatch.Success) category = catMatch.Groups[1].Value;

            var itMatch = ItemTypeTagRegex.Match(content);
            if (itMatch.Success) itemType = itMatch.Groups[1].Value;

            return itemClass == "Consumable"
                || itemType == "Inventory.ItemType.Resource"
                || (itemClass == "Default" && category == "Resource");
        }

        bool IsExcluded(string relPath)
        {
            if (ExcludeRelativePaths == null || ExcludeRelativePaths.Length == 0) return false;
            foreach (var pat in ExcludeRelativePaths)
            {
                if (LikeMatch(relPath, pat)) return true;
            }
            return false;
        }

        // Mimics PowerShell's -like operator (case-insensitive on Windows).
        static bool LikeMatch(string text, string pattern)
        {
            var rx = "^" + Regex.Escape(pattern).Replace("\\*", ".*").Replace("\\?", ".") + "$";
            return Regex.IsMatch(text, rx, RegexOptions.IgnoreCase);
        }

        static void ValidateProfile(Profile profile)
        {
            if (profile.Globals == null || profile.Globals.StackSize == null) return;

            var s = profile.Globals.StackSize;
            if (s.Absolute.HasValue && s.Multiplier.HasValue)
                throw new ArgumentException(
                    "Profile.Globals.StackSize: Absolute and Multiplier are mutually exclusive");
            if (s.Absolute.HasValue && s.Absolute.Value < 0)
                throw new ArgumentException("Profile.Globals.StackSize.Absolute must be >= 0");
            if (s.Multiplier.HasValue && s.Multiplier.Value < 1)
                throw new ArgumentException("Profile.Globals.StackSize.Multiplier must be >= 1");
            if (s.Cap.HasValue && s.Cap.Value < 0)
                throw new ArgumentException("Profile.Globals.StackSize.Cap must be >= 0");
        }
    }
}
