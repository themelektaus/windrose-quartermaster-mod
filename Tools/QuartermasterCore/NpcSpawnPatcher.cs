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
    // Patches the vanilla AI-spawner JSON tree (A2_Spawners) - the runtime-JSON
    // DataAssets (UR5JsonRuntimeDA) the game reads to decide how many NPCs spawn
    // and how often they respawn. Two relevant schemas live under A2_Spawners:
    //
    //   R5GameplaySpawnerParams        -> RespawnInterval{Min,Max} (seconds)
    //                                     + Variants[].Collection[].Amount{Min,Max}
    //   R5GameplaySpawnerVariantPreset -> Collection[].Amount{Min,Max} (no timer)
    //
    // We only mutate RespawnInterval and Amount blocks; every other field is left
    // byte-/structure-identical (parse-edit-reserialize with vanilla tab+CRLF
    // formatting). Quest spawners (AI_POI_ForQuest) are never touched so quests
    // keep their scripted populations. MutatorPresets carry no Amount/timer and
    // therefore naturally fall through unchanged.
    public sealed class NpcSpawnPatcher
    {
        // Vanilla respawn cadence we treat as "standard". Spawners longer than
        // this (4h/6h boss & rare timers) are excluded from the global respawn
        // multiplier unless the profile opts in via IncludeSpecialTimers.
        const int StandardMaxRespawnSeconds = 7200; // 120 min

        // Path segment marking quest spawners we must not touch.
        const string QuestSegment = "/AI_POI_ForQuest/";

        public NpcSpawnPatchResult PatchToDirectory(string vanillaSpawnersDir, string outDir, Profile profile)
        {
            if (string.IsNullOrEmpty(vanillaSpawnersDir)) throw new ArgumentNullException("vanillaSpawnersDir");
            if (string.IsNullOrEmpty(outDir))             throw new ArgumentNullException("outDir");
            if (profile == null)                          throw new ArgumentNullException("profile");
            if (!Directory.Exists(vanillaSpawnersDir))    throw new DirectoryNotFoundException(vanillaSpawnersDir);

            var g = profile.Globals != null ? profile.Globals.NpcSpawn : null;
            double respawnMult = ResolveMult(g != null ? g.RespawnMultiplier : null);
            double countMult   = ResolveMult(g != null ? g.CountMultiplier : null);
            bool includeSpecial = g != null && g.IncludeSpecialTimers.GetValueOrDefault(false);
            bool globalEnabled = g != null && g.Enabled.GetValueOrDefault(false);
            bool respawnGlobalActive = globalEnabled && Math.Abs(respawnMult - 1.0) > 1e-9;
            bool countGlobalActive   = globalEnabled && Math.Abs(countMult - 1.0) > 1e-9;

            var overrides = profile.NpcSpawnOverrides ?? new Dictionary<string, NpcSpawnOverride>(0);

            Directory.CreateDirectory(outDir);
            var result = new NpcSpawnPatchResult();
            var vanillaFull = Path.GetFullPath(vanillaSpawnersDir);

            foreach (var jsonPath in Directory.EnumerateFiles(vanillaFull, "*.json", SearchOption.AllDirectories))
            {
                var rel = jsonPath.Substring(vanillaFull.Length).TrimStart(
                    Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                var relSlash = rel.Replace(Path.DirectorySeparatorChar, '/')
                                  .Replace(Path.AltDirectorySeparatorChar, '/');

                // Quest spawners are off-limits.
                if (("/" + relSlash).IndexOf(QuestSegment, StringComparison.OrdinalIgnoreCase) >= 0)
                    continue;

                result.Scanned++;

                var spawnerId = relSlash.EndsWith(".json", StringComparison.OrdinalIgnoreCase)
                    ? relSlash.Substring(0, relSlash.Length - 5)
                    : relSlash;

                NpcSpawnOverride ovr;
                overrides.TryGetValue(spawnerId, out ovr);
                bool hasOverride = ovr != null && (ovr.RespawnMinutes.HasValue
                                                 || ovr.CountMin.HasValue
                                                 || ovr.CountMax.HasValue);

                // Nothing to do for this file if neither a global nor an override applies.
                if (!hasOverride && !respawnGlobalActive && !countGlobalActive)
                {
                    result.UnchangedSkip++;
                    continue;
                }

                JsonObject root;
                try
                {
                    root = JsonNode.Parse(File.ReadAllText(jsonPath, Encoding.UTF8)) as JsonObject;
                    if (root == null) { result.NoSchema++; continue; }
                }
                catch (JsonException)
                {
                    result.NoSchema++;
                    continue;
                }

                bool changed = false;

                // The global multipliers are NPC-only (the reference mod's intent):
                // resource / chest / mineral-node spawners under A2_Spawners share
                // the schema but must not be swept by "more NPCs / faster respawn".
                // Per-spawner overrides still apply to anything the user picks.
                bool isNpc = IsNpcSpawner(root);
                bool respawnApplies = respawnGlobalActive && isNpc;
                bool countApplies = countGlobalActive && isNpc;

                // --- RespawnInterval (only present on R5GameplaySpawnerParams) ---
                if (root["RespawnInterval"] is JsonObject ri
                    && ri["Min"] is JsonNode && ri["Max"] is JsonNode)
                {
                    int vMin = ri["Min"].GetValue<int>();
                    int vMax = ri["Max"].GetValue<int>();
                    int nMin = vMin, nMax = vMax;

                    if (hasOverride && ovr.RespawnMinutes.HasValue)
                    {
                        nMin = nMax = Math.Max(0, ovr.RespawnMinutes.Value) * 60;
                    }
                    else if (respawnApplies
                             && (includeSpecial || vMin <= StandardMaxRespawnSeconds))
                    {
                        nMin = (int)Math.Round(vMin * respawnMult, MidpointRounding.AwayFromZero);
                        nMax = (int)Math.Round(vMax * respawnMult, MidpointRounding.AwayFromZero);
                        if (vMin > 0 && nMin < 1) nMin = 1;
                        if (vMax > 0 && nMax < 1) nMax = 1;
                    }

                    if (nMin != vMin || nMax != vMax)
                    {
                        ri["Min"] = nMin;
                        ri["Max"] = nMax;
                        changed = true;
                        result.RespawnChanged++;
                    }
                }

                // --- Amount blocks (Variants[].Collection[].Amount and top-level Collection[].Amount) ---
                int amtChanged = 0;
                foreach (var amount in CollectAmountBlocks(root))
                {
                    if (!(amount["Min"] is JsonNode) || !(amount["Max"] is JsonNode)) continue;
                    int vMin = amount["Min"].GetValue<int>();
                    int vMax = amount["Max"].GetValue<int>();
                    int nMin = vMin, nMax = vMax;

                    if (hasOverride && (ovr.CountMin.HasValue || ovr.CountMax.HasValue))
                    {
                        if (ovr.CountMin.HasValue) nMin = Math.Max(0, ovr.CountMin.Value);
                        if (ovr.CountMax.HasValue) nMax = Math.Max(0, ovr.CountMax.Value);
                    }
                    else if (countApplies)
                    {
                        nMin = (int)Math.Round(vMin * countMult, MidpointRounding.AwayFromZero);
                        nMax = (int)Math.Round(vMax * countMult, MidpointRounding.AwayFromZero);
                        if (vMin > 0 && nMin < 1) nMin = 1;
                        if (vMax > 0 && nMax < 1) nMax = 1;
                    }

                    if (nMax < nMin) nMax = nMin;

                    if (nMin != vMin || nMax != vMax)
                    {
                        amount["Min"] = nMin;
                        amount["Max"] = nMax;
                        amtChanged++;
                    }
                }
                if (amtChanged > 0)
                {
                    changed = true;
                    result.CountChanged += amtChanged;
                }

                if (!changed)
                {
                    result.UnchangedSkip++;
                    continue;
                }

                if (hasOverride) result.OverriddenFiles++;

                var outPath = Path.Combine(outDir, rel);
                Directory.CreateDirectory(Path.GetDirectoryName(outPath));
                File.WriteAllBytes(outPath, SerializeWithTabsAndCrlf(root));
                result.Written++;
                result.WrittenSpawners.Add(spawnerId);
            }

            // Stale-game-version guard: the spawner tree is large and stable; if a
            // game update relocates it, the source extraction would yield almost
            // nothing and the feature would silently no-op.
            if (result.Scanned < 100)
            {
                result.Warnings.Add("only " + result.Scanned + " spawner JSON scanned under "
                    + vanillaFull + " - the vanilla source may be missing or the game "
                    + "may have moved A2_Spawners.");
            }

            return result;
        }

        // Returns every Amount JsonObject reachable via the two known shapes.
        static IEnumerable<JsonObject> CollectAmountBlocks(JsonObject root)
        {
            // R5GameplaySpawnerParams: Variants[].Collection[].Amount
            if (root["Variants"] is JsonArray variants)
            {
                foreach (var v in variants)
                {
                    if (v is JsonObject vo && vo["Collection"] is JsonArray coll)
                    {
                        foreach (var c in coll)
                            if (c is JsonObject co && co["Amount"] is JsonObject a)
                                yield return a;
                    }
                }
            }
            // R5GameplaySpawnerVariantPreset: top-level Collection[].Amount
            if (root["Collection"] is JsonArray topColl)
            {
                foreach (var c in topColl)
                    if (c is JsonObject co && co["Amount"] is JsonObject a)
                        yield return a;
            }
        }

        // NPC spawners reference a character BP (/Game/.../Character/...) or
        // orchestrate other AI spawner collections (A2_Spawners/AI_). Resource /
        // chest / mineral-node spawners reference /Foliage/ or /POI/ assets and
        // are deliberately excluded from the NPC-only global multipliers.
        public static bool IsNpcSpawner(JsonObject root)
        {
            foreach (var assets in CollectAssetArrays(root))
            {
                foreach (var a in assets)
                {
                    var s = a?.GetValue<string>();
                    if (string.IsNullOrEmpty(s)) continue;
                    if (s.IndexOf("/Character/", StringComparison.OrdinalIgnoreCase) >= 0
                        || s.IndexOf("/A2_Spawners/AI_", StringComparison.OrdinalIgnoreCase) >= 0)
                        return true;
                }
            }
            return false;
        }

        static IEnumerable<JsonArray> CollectAssetArrays(JsonObject root)
        {
            if (root["Variants"] is JsonArray variants)
            {
                foreach (var v in variants)
                {
                    if (v is JsonObject vo && vo["Collection"] is JsonArray coll)
                    {
                        foreach (var c in coll)
                            if (c is JsonObject co && co["Assets"] is JsonArray a)
                                yield return a;
                    }
                }
            }
            if (root["Collection"] is JsonArray topColl)
            {
                foreach (var c in topColl)
                    if (c is JsonObject co && co["Assets"] is JsonArray a)
                        yield return a;
            }
        }

        static double ResolveMult(double? m)
        {
            if (!m.HasValue) return 1.0;
            if (m.Value <= 0.0) return 1.0;
            return m.Value;
        }
    }

    public sealed class NpcSpawnPatchResult
    {
        public int Scanned;
        public int Written;
        public int UnchangedSkip;
        public int NoSchema;

        public int RespawnChanged;   // files whose RespawnInterval was changed
        public int CountChanged;     // Amount blocks changed (across all files)
        public int OverriddenFiles;  // files touched by a per-spawner override

        public List<string> WrittenSpawners = new List<string>();
        public List<string> Warnings = new List<string>();
    }
}
