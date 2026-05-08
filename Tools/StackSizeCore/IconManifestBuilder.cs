using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Windrose.StackSize.Core
{
    // Walks Sources/Vanilla and emits the manifest JSON that
    // IconExtractor.exe consumes. Mirrors the PowerShell logic that lived
    // in Library/Icons.ps1 (the only behavioural difference: this version
    // uses System.Text.Json.Nodes, which gives us a clean way to reject
    // mistyped FText refs without try/catch chains).
    //
    // The shape of the per-entry record (and of the FText / curve nested
    // records) MUST match WindroseIconExtractor.Program's ManifestEntry --
    // change both ends together if you touch this.
    public sealed class IconManifestBuilder
    {
        public Action<string> Log;

        public BuildResult Build(string sourcesDir)
        {
            if (string.IsNullOrEmpty(sourcesDir)) throw new ArgumentNullException("sourcesDir");
            if (!Directory.Exists(sourcesDir))
            {
                throw new DirectoryNotFoundException(
                    "Source folder not found: " + sourcesDir +
                    "\n\nRun the dump step first to extract the vanilla JSONs.");
            }

            var entries = new List<ManifestEntry>();
            var skipped = 0;
            var withName = 0;
            var withVanity = 0;
            var withEffects = 0;
            var withSetEffects = 0;
            var withCurveData = 0;

            foreach (var path in Directory.EnumerateFiles(sourcesDir, "*.json", SearchOption.AllDirectories))
            {
                var entry = TryBuildEntry(path);
                if (entry == null) { skipped++; continue; }
                entries.Add(entry);
                if (entry.NameTable != null) withName++;
                if (entry.VanityTable != null) withVanity++;
                if (entry.Effects != null && entry.Effects.Count > 0) withEffects++;
                if (entry.SetEffects != null && entry.SetEffects.Count > 0) withSetEffects++;
                if (entry.DescriptionData != null && entry.DescriptionData.Count > 0) withCurveData++;
            }

            LogLine("Manifest entries: " + entries.Count + " (skipped " + skipped + " JSONs without ItemTexture)");
            LogLine("Localization keys: " + withName + " name, " + withVanity + " vanity, " +
                    withEffects + " effects, " + withSetEffects + " set-effects, " +
                    withCurveData + " curve-data");

            if (entries.Count == 0)
            {
                throw new InvalidOperationException(
                    "No InventoryItemUIData.ItemTexture paths found under " + sourcesDir);
            }
            return new BuildResult { Entries = entries };
        }

        // Writes the manifest to a JSON file. Returns the absolute path.
        // Used by IconExtractionRunner; callers can also build the in-memory
        // list and serialize it themselves.
        public string WriteToTempFile(IList<ManifestEntry> entries)
        {
            var path = Path.Combine(Path.GetTempPath(),
                "windrose-icons-" + Guid.NewGuid().ToString("N") + ".json");
            var json = JsonSerializer.Serialize(entries, ManifestSerializerOptions);
            File.WriteAllText(path, json);
            return path;
        }

        // The IconExtractor expects a particular JSON shape: camelCase keys,
        // nulls omitted (so "no FText for VanityText" is just an absent field).
        public static readonly JsonSerializerOptions ManifestSerializerOptions = new JsonSerializerOptions
        {
            IncludeFields = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            WriteIndented = false,
        };

        ManifestEntry TryBuildEntry(string jsonPath)
        {
            JsonNode root;
            try
            {
                using (var stream = File.OpenRead(jsonPath))
                {
                    root = JsonNode.Parse(stream);
                }
            }
            catch
            {
                return null;
            }
            var rootObj = root as JsonObject;
            if (rootObj == null) return null;

            // Only InventoryItem definitions get textures.
            var typeNode = rootObj["$type"];
            if (typeNode == null) return null;
            // Some items live under a different $type (NPC data, etc.);
            // checking only for ItemTexture below filters those naturally.

            var ui = rootObj["InventoryItemUIData"] as JsonObject;
            if (ui == null) return null;

            var texturePath = AsString(ui["ItemTexture"]);
            if (string.IsNullOrEmpty(texturePath) || texturePath == "None") return null;

            var entry = new ManifestEntry
            {
                ItemId = Path.GetFileNameWithoutExtension(jsonPath),
                TexturePath = texturePath,
            };

            // Title / description FText refs.
            ReadFText(ui["ItemName"],        out entry.NameTable,   out entry.NameKey);
            ReadFText(ui["ItemDescription"], out entry.DescTable,   out entry.DescKey);
            ReadFText(ui["VanityText"],      out entry.VanityTable, out entry.VanityKey);

            // EffectsDescriptions: array of FText. Empty array is the common case.
            var effectsArr = ui["EffectsDescriptions"] as JsonArray;
            if (effectsArr != null)
            {
                foreach (var n in effectsArr)
                {
                    string t, k;
                    if (TryReadFText(n, out t, out k))
                    {
                        if (entry.Effects == null) entry.Effects = new List<EffectRef>();
                        entry.Effects.Add(new EffectRef { Table = t, Key = k });
                    }
                }
            }

            // ItemDescriptionData: shared placeholder source for {0}, {1}, ...
            var ddArr = ui["ItemDescriptionData"] as JsonArray;
            if (ddArr != null)
            {
                foreach (var n in ddArr)
                {
                    var d = n as JsonObject;
                    if (d == null) continue;
                    var ct = AsString(d["CurveTable"]);
                    var rn = AsString(d["RowName"]);
                    if (string.IsNullOrEmpty(ct) || ct == "None" || string.IsNullOrEmpty(rn)) continue;
                    var lvl = AsInt(d["CurveLevel"], 0);
                    var dt  = AsString(d["DisplayType"]) ?? string.Empty;
                    var inv = AsBool(d["bInverseValue"], false);

                    if (entry.DescriptionData == null) entry.DescriptionData = new List<CurveRef>();
                    entry.DescriptionData.Add(new CurveRef
                    {
                        CurveTable  = ct,
                        RowName     = rn,
                        CurveLevel  = lvl,
                        DisplayType = dt,
                        Inverse     = inv,
                    });
                }
            }

            // SetEffectsDescriptions: structs with nested FText + tag + count.
            var setArr = ui["SetEffectsDescriptions"] as JsonArray;
            if (setArr != null)
            {
                foreach (var n in setArr)
                {
                    var s = n as JsonObject;
                    if (s == null) continue;

                    string sn_t, sn_k, sd_t, sd_k;
                    var hasName = TryReadFText(s["Name"],        out sn_t, out sn_k);
                    var hasDesc = TryReadFText(s["Description"], out sd_t, out sd_k);
                    if (!hasName && !hasDesc) continue;

                    var tag = (string)null;
                    var tagObj = s["SetEffectTag"] as JsonObject;
                    if (tagObj != null)
                    {
                        var tn = AsString(tagObj["TagName"]);
                        if (!string.IsNullOrEmpty(tn) && tn != "None") tag = tn;
                    }
                    var count = AsInt(s["ActivationCount"], 0);

                    if (entry.SetEffects == null) entry.SetEffects = new List<SetEffectRef>();
                    entry.SetEffects.Add(new SetEffectRef
                    {
                        NameTable       = hasName ? sn_t : null,
                        NameKey         = hasName ? sn_k : null,
                        DescTable       = hasDesc ? sd_t : null,
                        DescKey         = hasDesc ? sd_k : null,
                        SetEffectTag    = tag,
                        ActivationCount = count,
                    });
                }
            }

            return entry;
        }

        // ---- JsonNode helpers (null-safe, type-tolerant) -----------------

        static string AsString(JsonNode n)
        {
            if (n == null) return null;
            var v = n as JsonValue;
            if (v == null) return null;
            string s;
            return v.TryGetValue<string>(out s) ? s : null;
        }

        static int AsInt(JsonNode n, int fallback)
        {
            if (n == null) return fallback;
            var v = n as JsonValue;
            if (v == null) return fallback;
            int i;
            if (v.TryGetValue<int>(out i)) return i;
            long l;
            if (v.TryGetValue<long>(out l)) return (int)l;
            double d;
            if (v.TryGetValue<double>(out d)) return (int)d;
            return fallback;
        }

        static bool AsBool(JsonNode n, bool fallback)
        {
            if (n == null) return fallback;
            var v = n as JsonValue;
            if (v == null) return fallback;
            bool b;
            return v.TryGetValue<bool>(out b) ? b : fallback;
        }

        // VanityText is polymorph: empty string in some items, FText {TableId,Key}
        // in others. We accept only the FText-shaped form.
        static bool TryReadFText(JsonNode n, out string table, out string key)
        {
            table = null;
            key = null;
            var obj = n as JsonObject;
            if (obj == null) return false;
            var t = AsString(obj["TableId"]);
            var k = AsString(obj["Key"]);
            if (string.IsNullOrEmpty(t) || string.IsNullOrEmpty(k)) return false;
            table = t;
            key = k;
            return true;
        }

        static void ReadFText(JsonNode n, out string table, out string key)
        {
            TryReadFText(n, out table, out key);
        }

        void LogLine(string msg)
        {
            if (Log != null) Log(msg);
        }

        public sealed class BuildResult
        {
            public List<ManifestEntry> Entries;
        }
    }

    // ---- On-disk schema ---------------------------------------------------
    // Property names map to camelCase (see ManifestSerializerOptions). Keep
    // these in sync with WindroseIconExtractor.Program's ManifestEntry --
    // both ends serialize/deserialize the same on-disk shape.

    public sealed class ManifestEntry
    {
        public string ItemId;
        public string TexturePath;
        public string NameTable;
        public string NameKey;
        public string DescTable;
        public string DescKey;
        public string VanityTable;
        public string VanityKey;
        public List<EffectRef> Effects;
        public List<SetEffectRef> SetEffects;
        public List<CurveRef> DescriptionData;
    }

    public sealed class EffectRef
    {
        public string Table;
        public string Key;
    }

    public sealed class SetEffectRef
    {
        public string NameTable;
        public string NameKey;
        public string DescTable;
        public string DescKey;
        public string SetEffectTag;
        public int ActivationCount;
    }

    public sealed class CurveRef
    {
        public string CurveTable;
        public string RowName;
        public int CurveLevel;
        public string DisplayType;
        public bool Inverse;
    }
}
