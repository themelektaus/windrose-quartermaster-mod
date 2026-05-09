using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Windrose.Quartermaster.Core
{
    // Reads vanilla LootTable JSONs and writes a parallel directory
    // containing only the LTs that changed under a given Profile. Output
    // dir mirrors the in-pak layout (R5/Plugins/.../LootTables/<...>) so
    // it can be co-packed with the StackPatcher output by repak.
    //
    // Resolver rule per LootTable entry (LootData[i]):
    //   1. profile.LootOverrides[ltId].Removed contains i  -> entry skipped
    //   2. profile.LootOverrides[ltId].Entries[i].Min/Max  -> wins over multiplier
    //   3. else: vanillaMin/Max * globals.loot.byCategory[bucket] (rounded AwayFromZero)
    //   4. Weight/LootItem/LootTable: edit overrides directly, no multiplier
    //   5. profile.LootOverrides[ltId].Added is appended verbatim after the survivors
    //
    // After computing the result list, the patcher does a deep equality check
    // against the vanilla list -- if nothing actually changed, the LT is
    // skipped (no file is written, the engine uses vanilla).
    //
    // Output formatting matches vanilla LootTable JSONs: tab indent
    // (size 1), CRLF line endings, trailing CRLF. Field order on the
    // entries is preserved exactly, including the counter-intuitive
    // Min/Max/Weight/LootItem/ItemAttributeModifiers/LootTable sequence
    // the engine expects.
    public sealed class LootPatcher
    {
        // UE asset paths reference the in-pak LootTables under this prefix.
        // Sub-table refs use this path even though the on-disk JSONs sit
        // under Sources/Vanilla/R5/Plugins/R5BusinessRules/Content/LootTables/.
        const string LootTablesPathPrefix = "/R5BusinessRules/LootTables/";

        // No-BOM UTF-8; line endings handled manually because we pin
        // CRLF to match vanilla regardless of host platform.
        static readonly UTF8Encoding Utf8NoBom = new UTF8Encoding(false);

        // The dump tree always lives under this relative root. We strip it
        // when computing the LT id (Mobs/DA_LT_..., not the full prefix).
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

                // Compute a stable LT id: subpath under LootTables/, no extension.
                // E.g. "Mobs/DA_LT_Mob_BlackBeard_Sergeant_Final".
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
                    // No global multiplier and no per-LT override -> vanilla.
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
                    // Override schema referenced indices that don't exist;
                    // ApplyOverrides logged it via result.Warnings.
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

            return result;
        }

        // The bucket key is the first path segment under LootTables/, e.g.
        // "Mobs", "Chests", "Foliage". Sub-folders below that are folded
        // into the same bucket -- "Mobs/Rss/..." still hits "Mobs". This
        // matches the user-facing per-category multiplier UI (no sub-tree).
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

        // Builds the new LootData array. Returns null if the override is
        // structurally invalid (e.g. references a non-existent vanilla
        // index AND the build would produce something nonsensical) and
        // logs a warning. Otherwise applies edits + removals + appends.
        static JsonArray ApplyOverrides(JsonArray vanillaData, double multiplier,
            LootTableOverride ovr, string ltId, LootPatchResult result)
        {
            var newArr = new JsonArray();

            var removed = ovr != null && ovr.Removed != null
                ? new HashSet<int>(ovr.Removed)
                : new HashSet<int>();

            // Warn (don't fail) if the override references indices outside
            // the vanilla range -- the schema is forgiving so old profiles
            // survive game-patch shifts.
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

        // Produces a fresh JsonObject in the canonical field order:
        // Min, Max, Weight, LootItem, ItemAttributeModifiers (if present in
        // vanilla), LootTable. This order matches what the engine emits.
        // The optionality of ItemAttributeModifiers is a vanilla schema
        // variation -- 278 of 1589 vanilla LTs simply don't include the
        // field on their entries, and we mirror that exactly.
        static JsonObject BuildEntry(JsonObject vanilla, LootEntryEdit edit, double multiplier)
        {
            var vMin    = vanilla["Min"]?.GetValue<int>() ?? 0;
            var vMax    = vanilla["Max"]?.GetValue<int>() ?? 0;
            var vWeight = vanilla["Weight"]?.GetValue<int>() ?? 0;
            var vItem   = vanilla["LootItem"]?.GetValue<string>() ?? "None";
            var vTable  = vanilla["LootTable"]?.GetValue<string>() ?? "None";
            var vAttrs  = vanilla["ItemAttributeModifiers"] as JsonArray;

            // Sub-table refs roll the referenced LT N times -- multiplying
            // that count would compound with the leaf-table drops (which
            // already get the multiplier). User intent for "Mobs x2" is
            // "twice the drops", not "roll each sub-table twice", so the
            // multiplier is skipped on orchestrator entries entirely.
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

            // Field order MUST match vanilla emission order. ItemAttributeModifiers
            // is inserted only when vanilla had it (preserves 278 schema-light LTs).
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

        // For brand-new (Added) entries, follow the predominant vanilla
        // shape and always emit ItemAttributeModifiers as []. Sub-table-
        // only LTs that omit the field are a vanilla-only quirk; user-
        // added entries should match the full schema.
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

        // JSON arrays are order-sensitive; objects must have the same keys
        // in the same order with deep-equal values.
        static bool DeepEquals(JsonNode a, JsonNode b)
        {
            if (a == null && b == null) return true;
            if (a == null || b == null) return false;

            if (a is JsonObject oa && b is JsonObject ob)
            {
                if (oa.Count != ob.Count) return false;
                using var ea = oa.GetEnumerator();
                using var eb = ob.GetEnumerator();
                while (ea.MoveNext() && eb.MoveNext())
                {
                    if (ea.Current.Key != eb.Current.Key) return false;
                    if (!DeepEquals(ea.Current.Value, eb.Current.Value)) return false;
                }
                return true;
            }
            if (a is JsonArray aa && b is JsonArray ab)
            {
                if (aa.Count != ab.Count) return false;
                for (int i = 0; i < aa.Count; i++)
                {
                    if (!DeepEquals(aa[i], ab[i])) return false;
                }
                return true;
            }
            if (a is JsonValue va && b is JsonValue vb)
            {
                return va.ToJsonString() == vb.ToJsonString();
            }
            return false;
        }

        // Tab-indent (size 1), CRLF line endings, trailing CRLF. The
        // System.Text.Json writer emits CRLF for the inter-token newlines;
        // we stitch the final CRLF on manually to match vanilla.
        static byte[] SerializeWithTabsAndCrlf(JsonObject root)
        {
            using var ms = new MemoryStream();
            var writerOptions = new JsonWriterOptions
            {
                Indented = true,
                IndentCharacter = '\t',
                IndentSize = 1,
                NewLine = "\r\n",
                // The pak file allows raw / and . in strings; UnsafeRelaxed
                // matches what the vanilla emitter produces (no \u escapes
                // for ASCII characters above 0x20).
                Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            };
            using (var writer = new Utf8JsonWriter(ms, writerOptions))
            {
                root.WriteTo(writer);
            }
            // Trailing CRLF -- vanilla LT JSONs end with one.
            ms.WriteByte((byte)'\r');
            ms.WriteByte((byte)'\n');
            return ms.ToArray();
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
        public int NoSchema;          // root not a JsonObject or no LootData array

        public int MultiplierApplied;
        public int Edited;            // had non-empty Entries
        public int Removed;           // had non-empty Removed
        public int Added;             // had non-empty Added

        public List<string> WrittenLootTables = new List<string>();
        public List<string> Warnings = new List<string>();
    }
}
