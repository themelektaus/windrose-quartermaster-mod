using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using static Windrose.Quartermaster.Core.R5Json;

namespace Windrose.Quartermaster.Core
{
    // "XP Reward Multiplier" (the POI / Quest "x2" reference mods). Every quest and
    // POI-chest reward is an R5BLQuestParams DataAsset shipped as raw .json under
    //   R5/Content/Gameplay/Scenario/Player/{FactionQuests,LocalEventQuests,
    //                                         MainQuest,SideQuest,POIChest}/...
    // with an integer ExperienceCount. The reference mods simply doubled that value
    // and shipped the file back in a loose pak (the game reads the override path).
    // We reproduce the mechanism generically: read each vanilla file, multiply
    // ExperienceCount by the user's effective multiplier, and write the file back
    // unchanged except for that one number (so it round-trips byte-for-byte against
    // vanilla's tab/CRLF JSON). Pure data, no DLL.
    //
    // Two dimensions: QuestMultiplier scales the four quest trees, PoiMultiplier
    // scales POIChest/*. A per-stem override (keyed by the file's stem, e.g.
    // "DA_QP_MainQuest_ForgottenRelic_Core") wins over its dimension's overall.
    // A multiplier of 1.0 (overall or override) is vanilla = the file is skipped.
    public sealed class XpRewardPatcher
    {
        // The five Scenario/Player subtrees that carry R5BLQuestParams rewards. The
        // first, "POIChest", is the POI dimension; the rest are the quest dimension.
        const string PoiTopDir = "POIChest";
        static readonly string[] QuestTopDirs =
            { "FactionQuests", "LocalEventQuests", "MainQuest", "SideQuest" };

        const string QuestParamsType = "R5BLQuestParams";

        public Action<string> Log;

        public sealed class CatalogEntry
        {
            public string Stem;        // file name without .json (override key)
            public bool IsPoi;         // POIChest -> scaled by PoiMultiplier
            public string TopCategory; // "POIChest" / "MainQuest" / ...
            public string Group;       // biome (POI) or quest-name folder
            public string DisplayName; // prettified leaf name for the row label
            public int VanillaXp;      // ExperienceCount in vanilla
        }

        sealed class RewardFile
        {
            public string JsonPath;
            public string Rel;         // forward-slash relative path under the tree
            public string Stem;
            public bool IsPoi;
            public string TopCategory;
            public string Group;
            public JsonObject Root;
            public int VanillaXp;
        }

        // Enumerates every R5BLQuestParams reward file under the vanilla tree, parsed
        // and classified. Shared by BuildCatalog (GUI) and PatchToDirectory (build)
        // so the two never drift on what counts as an XP reward.
        IEnumerable<RewardFile> EnumerateRewards(string vanillaQuestRewardsDir)
        {
            var vanillaFull = Path.GetFullPath(vanillaQuestRewardsDir);
            foreach (var jsonPath in Directory.EnumerateFiles(vanillaFull, "*.json", SearchOption.AllDirectories))
            {
                var rel = jsonPath.Substring(vanillaFull.Length).TrimStart(
                        Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                    .Replace(Path.DirectorySeparatorChar, '/')
                    .Replace(Path.AltDirectorySeparatorChar, '/');

                var segs = rel.Split('/');
                if (segs.Length < 1) continue;
                var top = segs[0];

                bool isPoi = string.Equals(top, PoiTopDir, StringComparison.OrdinalIgnoreCase);
                bool isQuest = QuestTopDirs.Any(d => string.Equals(d, top, StringComparison.OrdinalIgnoreCase));
                if (!isPoi && !isQuest) continue; // ignore Notes / Research / Recipe*Unlock / ...

                JsonObject root;
                try
                {
                    root = JsonNode.Parse(File.ReadAllText(jsonPath, Encoding.UTF8)) as JsonObject;
                }
                catch (JsonException)
                {
                    continue;
                }
                if (root == null) continue;

                var type = root["$type"]?.GetValue<string>();
                if (!string.Equals(type, QuestParamsType, StringComparison.Ordinal)) continue;
                if (!TryGetInt(root["ExperienceCount"], out var xp)) continue;

                var stem = segs[segs.Length - 1];
                if (stem.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                    stem = stem.Substring(0, stem.Length - 5);

                // Group: the folder under the top category (biome for POI, quest-name
                // folder for quests); falls back to the top category for flat files.
                var group = segs.Length >= 3 ? segs[1] : top;

                yield return new RewardFile
                {
                    JsonPath = jsonPath,
                    Rel = rel,
                    Stem = stem,
                    IsPoi = isPoi,
                    TopCategory = top,
                    Group = group,
                    Root = root,
                    VanillaXp = xp,
                };
            }
        }

        // Catalog for the GUI's per-entry override list. Ordered by category then
        // group then stem so the frontend can render stable grouped sections.
        public List<CatalogEntry> BuildCatalog(string vanillaQuestRewardsDir)
        {
            if (string.IsNullOrEmpty(vanillaQuestRewardsDir)) throw new ArgumentNullException("vanillaQuestRewardsDir");
            if (!Directory.Exists(vanillaQuestRewardsDir)) throw new DirectoryNotFoundException(vanillaQuestRewardsDir);

            var list = new List<CatalogEntry>();
            foreach (var f in EnumerateRewards(vanillaQuestRewardsDir))
            {
                list.Add(new CatalogEntry
                {
                    Stem = f.Stem,
                    IsPoi = f.IsPoi,
                    TopCategory = f.TopCategory,
                    Group = f.Group,
                    DisplayName = Prettify(f.Stem),
                    VanillaXp = f.VanillaXp,
                });
            }
            return list
                .OrderBy(e => e.IsPoi ? 0 : 1)
                .ThenBy(e => e.TopCategory, StringComparer.OrdinalIgnoreCase)
                .ThenBy(e => e.Group, StringComparer.OrdinalIgnoreCase)
                .ThenBy(e => e.Stem, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        public XpRewardPatchResult PatchToDirectory(string vanillaQuestRewardsDir, string outDir, Profile profile)
        {
            if (string.IsNullOrEmpty(vanillaQuestRewardsDir)) throw new ArgumentNullException("vanillaQuestRewardsDir");
            if (string.IsNullOrEmpty(outDir)) throw new ArgumentNullException("outDir");
            if (profile == null) throw new ArgumentNullException("profile");
            if (!Directory.Exists(vanillaQuestRewardsDir)) throw new DirectoryNotFoundException(vanillaQuestRewardsDir);

            var xp = profile.Globals != null ? profile.Globals.XpReward : null;
            double questMul = ResolveDimension(xp != null ? xp.QuestMultiplier : null);
            double poiMul = ResolveDimension(xp != null ? xp.PoiMultiplier : null);
            var overrides = xp != null && xp.Overrides != null
                ? xp.Overrides
                : new Dictionary<string, double>(0);

            Directory.CreateDirectory(outDir);
            var result = new XpRewardPatchResult();

            foreach (var f in EnumerateRewards(vanillaQuestRewardsDir))
            {
                result.Scanned++;

                double effective = ResolveEffective(f.Stem, f.IsPoi, questMul, poiMul, overrides);
                if (Math.Abs(effective - 1.0) < 1e-9)
                {
                    result.UnchangedSkip++;
                    continue;
                }

                int newXp = (int)Math.Round(f.VanillaXp * effective, MidpointRounding.AwayFromZero);
                if (newXp == f.VanillaXp)
                {
                    result.UnchangedSkip++;
                    continue;
                }

                f.Root["ExperienceCount"] = newXp;

                var outPath = Path.Combine(outDir, f.Rel.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(outPath));
                File.WriteAllBytes(outPath, SerializeWithTabsAndCrlf(f.Root));

                result.Written++;
                if (f.IsPoi) result.PoiWritten++; else result.QuestWritten++;
                if (overrides.ContainsKey(f.Stem)) result.OverrideApplied++;
            }

            LogLine("XP rewards: " + result.Written + " written ("
                    + result.QuestWritten + " quest, " + result.PoiWritten + " POI, "
                    + result.OverrideApplied + " per-entry override) of "
                    + result.Scanned + " scanned");
            return result;
        }

        // Per-entry effective multiplier (mirrors the frontend's resolve logic):
        // an override that differs from vanilla wins; otherwise the entry follows
        // its dimension's overall (POI vs quest).
        static double ResolveEffective(string stem, bool isPoi, double questMul, double poiMul,
            Dictionary<string, double> overrides)
        {
            if (overrides != null && overrides.TryGetValue(stem, out var ov)
                && IsFinitePositive(ov) && Math.Abs(ov - 1.0) > 1e-9)
            {
                return ov;
            }
            return isPoi ? poiMul : questMul;
        }

        static double ResolveDimension(double? m)
        {
            if (!m.HasValue) return 1.0;
            var v = m.Value;
            return IsFinitePositive(v) ? v : 1.0;
        }

        static bool IsFinitePositive(double v)
        {
            return !double.IsNaN(v) && !double.IsInfinity(v) && v > 0.0;
        }

        static bool TryGetInt(JsonNode node, out int value)
        {
            value = 0;
            if (node is JsonValue jv && jv.TryGetValue<int>(out var i))
            {
                value = i;
                return true;
            }
            return false;
        }

        // "DA_QP_MainQuest_ForgottenRelic_Core" -> "MainQuest ForgottenRelic Core";
        // "DA_QP_POIQuest_CJ_Cave_01" -> "POIQuest CJ Cave 01". Cosmetic only.
        static string Prettify(string stem)
        {
            if (string.IsNullOrEmpty(stem)) return stem;
            var s = stem;
            if (s.StartsWith("DA_QP_", StringComparison.OrdinalIgnoreCase))
                s = s.Substring("DA_QP_".Length);
            else if (s.StartsWith("DA_", StringComparison.OrdinalIgnoreCase))
                s = s.Substring("DA_".Length);
            return s.Replace('_', ' ').Trim();
        }

        void LogLine(string msg)
        {
            if (Log != null) Log(msg);
        }
    }

    public sealed class XpRewardPatchResult
    {
        public int Scanned;
        public int Written;
        public int UnchangedSkip;
        public int QuestWritten;
        public int PoiWritten;
        public int OverrideApplied;
    }
}
