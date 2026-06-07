using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;
using static Windrose.Quartermaster.Core.R5Json;

namespace Windrose.Quartermaster.Core
{
    // "Level Rewards": the player level-up reward table is a single
    // R5BLEntityProgressionLevelParams DataAsset shipped as raw .json at
    //   R5/Plugins/R5BusinessRules/Content/EntityProgression/DA_HeroLevels.json
    // Each Levels[] entry is { Exp, TalentPointsReward, StatPointsReward }; index 0
    // is the level-1 starting state (Exp 0, no rewards). The reference "Leveling
    // Rework" mod simply rewrites every level to fixed fat rewards (~8 talent /
    // ~43 stat). We reproduce the idea generically as a HYBRID multiplier, because
    // vanilla talent rewards drop to 0 on several level-ups (lv 2/10/12/14) and a
    // plain multiplier would leave those gaps at zero:
    //
    //     effective = round( vanilla + multiplier )    when multiplier > 1 and vanilla < 2
    //     effective = round( vanilla * multiplier )    otherwise
    //
    // i.e. when BOOSTING, the 0/1-point "dead" levels ADD the multiplier (at 3x:
    // 0->3, 1->4) so they jump clear of a plain scale, while levels with >=2 vanilla
    // points scale normally (2->6, 3->9) - this preserves the vanilla shape AND
    // kills the dead levels. When REDUCING (<1x) everything is scaled plainly so 0
    // entries are not inflated.
    // The Exp curve is never touched, and neither is the level-1 / Exp==0 starting
    // entry: the reference "Leveling Rework" mod warns that adding a talent/stat
    // reward at level 1 CRASHES the game, so that first row is always left at its
    // vanilla value (guarded on BOTH the level-1 index and Exp==0).
    //
    // Two independent dimensions: TalentMultiplier and StatMultiplier. A multiplier
    // of 1.0 is vanilla = that field is left untouched; if BOTH are 1.0 the whole
    // file is skipped. Applied on top of the freshly-extracted vanilla asset, so no
    // drift across game updates. Pure data, no DLL - rides the main legacy pak.
    public sealed class LevelingPatcher
    {
        const string ParamsType = "R5BLEntityProgressionLevelParams";

        // Pak-relative path of the single asset this patcher writes.
        static readonly string[] OutRelSegments =
        {
            "R5", "Plugins", "R5BusinessRules", "Content", "EntityProgression", "DA_HeroLevels.json"
        };

        public Action<string> Log;

        // Hybrid per-field reward. multiplier<=0 or ==1 -> vanilla (no change). When
        // boosting (>1), the vanilla 0/1-point "dead" levels ADD the multiplier
        // instead of scaling (at 3x: 0->3, 1->4) so they jump clear of a plain
        // scale, while levels with >=2 vanilla points scale normally (2->6, 3->9).
        // When reducing (<1) the value is always scaled plainly so 0 entries are not
        // inflated. Result is clamped to >= 0. Callers must NOT invoke this for the
        // level-1 / Exp==0 starting entry (it is left at 0).
        public static int ApplyHybrid(int vanilla, double multiplier)
        {
            if (!IsFinitePositive(multiplier)) return vanilla;
            if (Math.Abs(multiplier - 1.0) < 1e-9) return vanilla;
            double effective = (multiplier > 1.0 && vanilla < 2)
                ? vanilla + multiplier
                : vanilla * multiplier;
            int v = (int)Math.Round(effective, MidpointRounding.AwayFromZero);
            return v < 0 ? 0 : v;
        }

        // Per-level effective reward: an explicit absolute override wins (clamped to
        // >= 0); otherwise the vanilla value is run through the hybrid multiplier
        // (which is itself a no-op at 1.0). Callers must NOT invoke this for the
        // level-1 / Exp==0 starting entry.
        public static int ResolveLevelReward(int vanilla, double multiplier, int? overrideValue)
        {
            if (overrideValue.HasValue) return overrideValue.Value < 0 ? 0 : overrideValue.Value;
            return ApplyHybrid(vanilla, multiplier);
        }

        // True if the override bag pins at least one level/dimension.
        static bool HasAnyOverride(Dictionary<int, LevelRewardOverride> overrides)
        {
            if (overrides == null) return false;
            foreach (var o in overrides.Values)
                if (o != null && (o.Talent.HasValue || o.Stat.HasValue)) return true;
            return false;
        }

        public sealed class LevelPreview
        {
            public int Level;          // 1-based (array index + 1)
            public int Exp;
            public int VanillaTalent;
            public int VanillaStat;
            public int EffectiveTalent;
            public int EffectiveStat;
        }

        // Vanilla-vs-effective per-level table for the GUI / verification. Does not
        // write anything. Mirrors PatchToDirectory's math exactly so the two never
        // drift on what a given pair of multipliers produces.
        public List<LevelPreview> BuildPreview(string vanillaHeroLevelsPath, Profile profile)
        {
            if (string.IsNullOrEmpty(vanillaHeroLevelsPath)) throw new ArgumentNullException("vanillaHeroLevelsPath");
            if (!File.Exists(vanillaHeroLevelsPath)) throw new FileNotFoundException(vanillaHeroLevelsPath);

            var lr = profile != null && profile.Globals != null ? profile.Globals.LevelingRework : null;
            double talentMul = ResolveMultiplier(lr != null ? lr.TalentMultiplier : null);
            double statMul = ResolveMultiplier(lr != null ? lr.StatMultiplier : null);
            var overrides = lr != null ? lr.Overrides : null;

            var root = ParseRoot(vanillaHeroLevelsPath);
            var levels = root["Levels"] as JsonArray;
            var list = new List<LevelPreview>();
            if (levels == null) return list;

            for (int i = 0; i < levels.Count; i++)
            {
                if (!(levels[i] is JsonObject entry)) continue;
                int exp = GetInt(entry["Exp"]);
                int talent = GetInt(entry["TalentPointsReward"]);
                int stat = GetInt(entry["StatPointsReward"]);
                // Level 1 = array index 0 (or Exp==0): never modified - a reward here
                // crashes the game (see class comment). Guard both for robustness.
                bool starting = i == 0 || exp == 0;
                LevelRewardOverride ov = null;
                if (overrides != null) overrides.TryGetValue(i + 1, out ov);
                list.Add(new LevelPreview
                {
                    Level = i + 1,
                    Exp = exp,
                    VanillaTalent = talent,
                    VanillaStat = stat,
                    EffectiveTalent = starting ? talent : ResolveLevelReward(talent, talentMul, ov != null ? ov.Talent : null),
                    EffectiveStat = starting ? stat : ResolveLevelReward(stat, statMul, ov != null ? ov.Stat : null),
                });
            }
            return list;
        }

        public LevelingPatchResult PatchToDirectory(string vanillaHeroLevelsPath, string outDir, Profile profile)
        {
            if (string.IsNullOrEmpty(vanillaHeroLevelsPath)) throw new ArgumentNullException("vanillaHeroLevelsPath");
            if (string.IsNullOrEmpty(outDir)) throw new ArgumentNullException("outDir");
            if (profile == null) throw new ArgumentNullException("profile");
            if (!File.Exists(vanillaHeroLevelsPath)) throw new FileNotFoundException(vanillaHeroLevelsPath);

            var lr = profile.Globals != null ? profile.Globals.LevelingRework : null;
            double talentMul = ResolveMultiplier(lr != null ? lr.TalentMultiplier : null);
            double statMul = ResolveMultiplier(lr != null ? lr.StatMultiplier : null);
            var overrides = lr != null ? lr.Overrides : null;

            var result = new LevelingPatchResult
            {
                TalentMultiplier = talentMul,
                StatMultiplier = statMul,
            };

            bool talentActive = Math.Abs(talentMul - 1.0) > 1e-9;
            bool statActive = Math.Abs(statMul - 1.0) > 1e-9;
            bool overridesActive = HasAnyOverride(overrides);
            if (!talentActive && !statActive && !overridesActive)
            {
                LogLine("Level rewards: both multipliers are 1.0 (vanilla) and no overrides - skipped");
                return result;
            }

            var root = ParseRoot(vanillaHeroLevelsPath);
            var levels = root["Levels"] as JsonArray;
            if (levels == null)
            {
                throw new InvalidOperationException(
                    "DA_HeroLevels.json has no Levels array (in-pak layout may have changed): "
                    + vanillaHeroLevelsPath);
            }

            bool anyChanged = false;
            for (int i = 0; i < levels.Count; i++)
            {
                if (!(levels[i] is JsonObject entry)) continue;
                result.LevelsScanned++;

                int exp = GetInt(entry["Exp"]);
                int talent = GetInt(entry["TalentPointsReward"]);
                int stat = GetInt(entry["StatPointsReward"]);

                result.VanillaTalentTotal += talent;
                result.VanillaStatTotal += stat;

                // Level 1 (array index 0, Exp 0) is the starting state: it grants
                // nothing in vanilla and in the reference mod, and the reference mod
                // warns that "adding stats at level 1 causes the game to crash". We
                // therefore NEVER floor/scale/override the first reward row - guarded
                // on BOTH the level-1 index and Exp==0 so a value can never slip in
                // even if the table layout shifts in a future game build.
                if (i == 0 || exp == 0)
                {
                    result.EffectiveTalentTotal += talent;
                    result.EffectiveStatTotal += stat;
                    continue;
                }

                LevelRewardOverride ov = null;
                if (overrides != null) overrides.TryGetValue(i + 1, out ov);
                int newTalent = ResolveLevelReward(talent, talentMul, ov != null ? ov.Talent : null);
                int newStat = ResolveLevelReward(stat, statMul, ov != null ? ov.Stat : null);

                result.EffectiveTalentTotal += newTalent;
                result.EffectiveStatTotal += newStat;

                if (newTalent != talent) { entry["TalentPointsReward"] = newTalent; anyChanged = true; }
                if (newStat != stat) { entry["StatPointsReward"] = newStat; anyChanged = true; }
                if (newTalent != talent || newStat != stat) result.LevelsPatched++;
            }

            if (!anyChanged)
            {
                LogLine("Level rewards: resolved values match vanilla - nothing to write");
                return result;
            }

            var outPath = Path.Combine(new[] { outDir }.Concat(OutRelSegments).ToArray());
            Directory.CreateDirectory(Path.GetDirectoryName(outPath));
            File.WriteAllBytes(outPath, SerializeWithTabsAndCrlf(root));
            result.Written = true;

            LogLine("Level rewards: " + result.LevelsPatched + " of " + result.LevelsScanned
                    + " level(s) patched (talent x" + talentMul.ToString("0.##")
                    + ", stat x" + statMul.ToString("0.##") + "); per-run totals talent "
                    + result.VanillaTalentTotal + " -> " + result.EffectiveTalentTotal
                    + ", stat " + result.VanillaStatTotal + " -> " + result.EffectiveStatTotal);
            return result;
        }

        static JsonObject ParseRoot(string path)
        {
            var root = JsonNode.Parse(File.ReadAllText(path, Encoding.UTF8)) as JsonObject;
            if (root == null)
                throw new InvalidOperationException("DA_HeroLevels.json did not parse to a JSON object: " + path);
            var type = root["$type"]?.GetValue<string>();
            if (!string.Equals(type, ParamsType, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "DA_HeroLevels.json $type is '" + (type ?? "<null>") + "', expected '"
                    + ParamsType + "' - wrong asset extracted: " + path);
            }
            return root;
        }

        static double ResolveMultiplier(double? m)
        {
            if (!m.HasValue) return 1.0;
            return IsFinitePositive(m.Value) ? m.Value : 1.0;
        }

        static bool IsFinitePositive(double v)
        {
            return !double.IsNaN(v) && !double.IsInfinity(v) && v > 0.0;
        }

        static int GetInt(JsonNode node)
        {
            if (node is JsonValue jv && jv.TryGetValue<int>(out var i)) return i;
            return 0;
        }

        void LogLine(string msg)
        {
            if (Log != null) Log(msg);
        }
    }

    public sealed class LevelingPatchResult
    {
        public bool Written;
        public int LevelsScanned;
        public int LevelsPatched;
        public double TalentMultiplier;
        public double StatMultiplier;
        public int VanillaTalentTotal;
        public int EffectiveTalentTotal;
        public int VanillaStatTotal;
        public int EffectiveStatTotal;
    }
}
