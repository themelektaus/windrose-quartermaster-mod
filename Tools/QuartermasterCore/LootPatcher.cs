using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using static Windrose.Quartermaster.Core.R5Json;

namespace Windrose.Quartermaster.Core
{
    public sealed class LootPatcher
    {
        const string LootTablesPathPrefix = "/R5BusinessRules/LootTables/";

        const string VanillaRoot = "R5/Plugins/R5BusinessRules/Content/LootTables";

        public LootPatchResult PatchToDirectory(string vanillaLootTablesDir, string outDir, Profile profile)
        {
            if (string.IsNullOrEmpty(vanillaLootTablesDir)) throw new ArgumentNullException("vanillaLootTablesDir");
            if (string.IsNullOrEmpty(outDir))               throw new ArgumentNullException("outDir");
            if (profile == null)                            throw new ArgumentNullException("profile");
            if (!Directory.Exists(vanillaLootTablesDir))    throw new DirectoryNotFoundException(vanillaLootTablesDir);

            ValidateProfile(profile);
            Directory.CreateDirectory(outDir);

            var result = new LootPatchResult();
            var lootGlobal = profile.Globals != null ? profile.Globals.Loot : null;
            var lootOverrides = profile.LootOverrides ?? new Dictionary<string, LootTableOverride>(0);
            var vanillaFull = Path.GetFullPath(vanillaLootTablesDir);

            foreach (var jsonPath in Directory.EnumerateFiles(vanillaFull, "*.json", SearchOption.AllDirectories))
            {
                result.Scanned++;

                var rel = jsonPath.Substring(vanillaFull.Length).TrimStart(
                    Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                var ltId = rel
                    .Replace(Path.DirectorySeparatorChar, '/')
                    .Replace(Path.AltDirectorySeparatorChar, '/');
                if (ltId.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                    ltId = ltId.Substring(0, ltId.Length - 5);

                var bucket = ExtractBucket(ltId);
                var multiplier = ResolveMultiplier(lootGlobal, bucket);
                LootTableOverride ovr;
                lootOverrides.TryGetValue(ltId, out ovr);

                if (multiplier == 1.0 && ovr == null)
                {
                    result.UnchangedSkip++;
                    continue;
                }

                JsonObject root;
                try
                {
                    var node = JsonNode.Parse(File.ReadAllText(jsonPath, Encoding.UTF8));
                    root = node as JsonObject;
                    if (root == null) { result.NoSchema++; continue; }
                }
                catch (JsonException)
                {
                    result.NoSchema++;
                    continue;
                }

                if (!(root["LootData"] is JsonArray vanillaData))
                {
                    result.NoSchema++;
                    continue;
                }

                var newData = ApplyOverrides(vanillaData, multiplier, ovr, ltId, result);
                if (newData == null)
                {
                    continue;
                }

                if (DeepEquals(vanillaData, newData))
                {
                    result.UnchangedSkip++;
                    continue;
                }

                root["LootData"] = newData;
                if (multiplier != 1.0)               result.MultiplierApplied++;
                if (ovr != null && ovr.Entries != null && ovr.Entries.Count > 0) result.Edited++;
                if (ovr != null && ovr.Removed != null && ovr.Removed.Count > 0) result.Removed++;
                if (ovr != null && ovr.Added != null && ovr.Added.Count > 0)     result.Added++;

                var outPath = Path.Combine(outDir, rel);
                Directory.CreateDirectory(Path.GetDirectoryName(outPath));
                File.WriteAllBytes(outPath, SerializeWithTabsAndCrlf(root));

                result.Written++;
                result.WrittenLootTables.Add(ltId);
            }

            // Collect tree/digvolume multipliers for the DLL.
            // Trees (SegmentTree) and mine walls (DigVolume) have drops baked
            // into binary DataAssets - not in DA_LT_* JSON loot tables - so the
            // DLL patches their UObjects directly. These are independent user-
            // facing multipliers on their own card in the Loot Tables tab.
            if (lootGlobal?.TreeMultiplier is double tm && tm != 1.0)
                result.TreeMultiplier = tm;
            if (lootGlobal?.DigVolumeMultiplier is double dvm && dvm != 1.0)
                result.DigVolumeMultiplier = dvm;

            return result;
        }

        static string ExtractBucket(string ltId)
        {
            var slash = ltId.IndexOf('/');
            return slash < 0 ? "(other)" : ltId.Substring(0, slash);
        }

        static double ResolveMultiplier(LootGlobal g, string bucket)
        {
            if (g == null || g.ByCategory == null) return 1.0;
            double m;
            if (g.ByCategory.TryGetValue(bucket, out m)) return m;
            if (g.ByCategory.TryGetValue("*", out m))    return m;
            return 1.0;
        }

        static JsonArray ApplyOverrides(JsonArray vanillaData, double multiplier,
            LootTableOverride ovr, string ltId, LootPatchResult result)
        {
            var newArr = new JsonArray();

            var removed = ovr != null && ovr.Removed != null
                ? new HashSet<int>(ovr.Removed)
                : new HashSet<int>();

            if (ovr != null)
            {
                if (ovr.Entries != null)
                {
                    foreach (var idx in ovr.Entries.Keys)
                    {
                        if (idx < 0 || idx >= vanillaData.Count)
                            result.Warnings.Add(ltId + ": entry edit at index " + idx +
                                " out of range (vanilla has " + vanillaData.Count + " entries)");
                    }
                }
                if (ovr.Removed != null)
                {
                    foreach (var idx in ovr.Removed)
                    {
                        if (idx < 0 || idx >= vanillaData.Count)
                            result.Warnings.Add(ltId + ": removed index " + idx +
                                " out of range (vanilla has " + vanillaData.Count + " entries)");
                    }
                }
            }

            for (int i = 0; i < vanillaData.Count; i++)
            {
                if (removed.Contains(i)) continue;

                LootEntryEdit edit = null;
                if (ovr != null && ovr.Entries != null) ovr.Entries.TryGetValue(i, out edit);

                var src = (JsonObject)vanillaData[i];
                var entry = BuildEntry(src, edit, multiplier);
                newArr.Add(entry);
            }

            if (ovr != null && ovr.Added != null)
            {
                foreach (var add in ovr.Added)
                {
                    newArr.Add(BuildAddedEntry(add));
                }
            }

            return newArr;
        }

        static JsonObject BuildEntry(JsonObject vanilla, LootEntryEdit edit, double multiplier)
        {
            var vMin    = vanilla["Min"]?.GetValue<int>() ?? 0;
            var vMax    = vanilla["Max"]?.GetValue<int>() ?? 0;
            var vWeight = vanilla["Weight"]?.GetValue<int>() ?? 0;
            var vItem   = vanilla["LootItem"]?.GetValue<string>() ?? "None";
            var vTable  = vanilla["LootTable"]?.GetValue<string>() ?? "None";
            var vAttrs  = vanilla["ItemAttributeModifiers"] as JsonArray;

            // Sub-table orchestrator entries skip the multiplier; else drops compound with the leaf table's.
            bool isOrchestrator =
                string.Equals(vItem, "None", StringComparison.Ordinal)
                && !string.Equals(vTable, "None", StringComparison.Ordinal);

            int newMin, newMax;
            if (edit != null && edit.Min.HasValue)
            {
                newMin = edit.Min.Value;
            }
            else if (isOrchestrator)
            {
                newMin = vMin;
            }
            else
            {
                newMin = (int)Math.Round(vMin * multiplier, MidpointRounding.AwayFromZero);
            }
            if (edit != null && edit.Max.HasValue)
            {
                newMax = edit.Max.Value;
            }
            else if (isOrchestrator)
            {
                newMax = vMax;
            }
            else
            {
                newMax = (int)Math.Round(vMax * multiplier, MidpointRounding.AwayFromZero);
            }
            var newWeight = (edit != null && edit.Weight.HasValue) ? edit.Weight.Value : vWeight;
            var newItem   = (edit != null && edit.LootItem  != null) ? edit.LootItem  : vItem;
            var newTable  = (edit != null && edit.LootTable != null) ? edit.LootTable : vTable;

            // Field order must match vanilla emission order; emit ItemAttributeModifiers only when vanilla had it.
            var obj = new JsonObject
            {
                ["Min"] = newMin,
                ["Max"] = newMax,
                ["Weight"] = newWeight,
                ["LootItem"] = newItem,
            };
            if (vAttrs != null)
            {
                obj["ItemAttributeModifiers"] = (JsonArray)vAttrs.DeepClone();
            }
            obj["LootTable"] = newTable;
            return obj;
        }

        static JsonObject BuildAddedEntry(LootEntry e)
        {
            return new JsonObject
            {
                ["Min"] = e.Min,
                ["Max"] = e.Max,
                ["Weight"] = e.Weight,
                ["LootItem"] = e.LootItem ?? "None",
                ["ItemAttributeModifiers"] = new JsonArray(),
                ["LootTable"] = e.LootTable ?? "None",
            };
        }

        static void ValidateProfile(Profile profile)
        {
            if (profile.Globals == null || profile.Globals.Loot == null) return;
            var loot = profile.Globals.Loot;
            if (loot.ByCategory == null) return;
            foreach (var kv in loot.ByCategory)
            {
                if (kv.Value < 0)
                    throw new ArgumentException("Profile.Globals.Loot.ByCategory[" + kv.Key +
                        "] must be >= 0 (got " + kv.Value + ")");
            }
        }
    }

    public sealed class LootPatchResult
    {
        public int Scanned;
        public int Written;
        public int UnchangedSkip;
        public int NoSchema;

        public int MultiplierApplied;
        public int Edited;
        public int Removed;
        public int Added;

        public List<string> WrittenLootTables = new List<string>();
        public List<string> Warnings = new List<string>();

        /// <summary>
        /// Multiplier for UR5SegmentTreeData drops (Divi, Palms, Ficus).
        /// Trees have their loot in binary DataAssets, not in DA_LT_* JSONs.
        /// </summary>
        public double TreeMultiplier = 1.0;

        /// <summary>
        /// Multiplier for UR5DigVolumeConfig drops (Iron ore mines, Copper).
        /// Mine walls have their loot in binary DataAssets, not in DA_LT_* JSONs.
        /// </summary>
        public double DigVolumeMultiplier = 1.0;
    }
}
