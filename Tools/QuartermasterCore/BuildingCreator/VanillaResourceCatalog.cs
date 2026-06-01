using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace Windrose.Quartermaster.Core.BuildingCreator
{
    // Indexes vanilla DA_DID_Resource_*.json definitions (plain JSON, no CUE4Parse) for the recipe editor's resource picker.
    public sealed class VanillaResourceCatalog
    {
        public string VanillaResourceDir;
        public Action<string> Log;

        readonly object _gate = new object();
        bool _built;
        List<VanillaResourceEntry> _entries;

        public IReadOnlyList<VanillaResourceEntry> All
        {
            get
            {
                EnsureBuilt();
                return _entries;
            }
        }

        public VanillaResourceEntry FindByPackagePath(string packagePath)
        {
            if (string.IsNullOrWhiteSpace(packagePath)) return null;
            EnsureBuilt();
            foreach (var e in _entries)
            {
                if (string.Equals(e.PackagePath, packagePath, StringComparison.OrdinalIgnoreCase))
                    return e;
            }
            return null;
        }

        public IReadOnlyList<VanillaResourceEntry> Search(string query, int limit = 50)
        {
            EnsureBuilt();
            if (limit <= 0) limit = 50;
            if (string.IsNullOrWhiteSpace(query))
            {
                return _entries.Take(limit).ToList();
            }
            var q = query.Trim();
            return _entries
                .Select(e => new { Entry = e, Score = ScoreMatch(e, q) })
                .Where(x => x.Score >= 0)
                .OrderBy(x => x.Score)
                .ThenBy(x => x.Entry.DisplayName, StringComparer.OrdinalIgnoreCase)
                .Take(limit)
                .Select(x => x.Entry)
                .ToList();
        }

        static int ScoreMatch(VanillaResourceEntry e, string q)
        {
            int dn = e.DisplayName?.IndexOf(q, StringComparison.OrdinalIgnoreCase) ?? -1;
            int st = e.Stem?.IndexOf(q, StringComparison.OrdinalIgnoreCase) ?? -1;
            if (dn < 0 && st < 0) return -1;
            if (dn < 0) return 1000 + st;
            return dn;
        }

        public void Invalidate()
        {
            lock (_gate)
            {
                _built = false;
                _entries = null;
            }
        }

        void EnsureBuilt()
        {
            if (_built) return;
            lock (_gate)
            {
                if (_built) return;
                _entries = BuildIndex();
                _built = true;
            }
        }

        List<VanillaResourceEntry> BuildIndex()
        {
            if (string.IsNullOrWhiteSpace(VanillaResourceDir))
                throw new InvalidOperationException("VanillaResourceCatalog.VanillaResourceDir not set");
            if (!Directory.Exists(VanillaResourceDir))
                throw new InvalidOperationException(
                    "VanillaResourceCatalog.VanillaResourceDir not found: " + VanillaResourceDir
                    + " (Setup must extract DA_DID_Resource_*.json from pakchunk0-Windows.pak)");

            LogLine("[resource-catalog] scanning " + VanillaResourceDir);
            var entries = new List<VanillaResourceEntry>(256);
            foreach (var jsonPath in Directory.EnumerateFiles(
                VanillaResourceDir, "DA_DID_Resource_*.json", SearchOption.TopDirectoryOnly))
            {
                try
                {
                    var entry = ParseEntry(jsonPath);
                    if (entry != null) entries.Add(entry);
                }
                catch (Exception ex)
                {
                    LogLine("[resource-catalog] skipping malformed " + Path.GetFileName(jsonPath) + ": " + ex.Message);
                }
            }
            entries.Sort((a, b) => string.Compare(a.DisplayName, b.DisplayName, StringComparison.OrdinalIgnoreCase));
            LogLine("[resource-catalog] indexed " + entries.Count + " resource(s)");
            return entries;
        }

        static VanillaResourceEntry ParseEntry(string jsonPath)
        {
            var stem = Path.GetFileNameWithoutExtension(jsonPath);
            using var doc = JsonDocument.Parse(File.ReadAllText(jsonPath));
            var root = doc.RootElement;

            if (root.ValueKind != JsonValueKind.Object) return null;
            if (!root.TryGetProperty("$type", out var typeEl)) return null;
            if (typeEl.ValueKind != JsonValueKind.String) return null;
            if (!string.Equals(typeEl.GetString(), "R5BLInventoryItem", StringComparison.Ordinal))
                return null;

            string iconPath = "";
            string itemNameKey = "";
            string itemTag = "";

            if (root.TryGetProperty("InventoryItemGppData", out var gpp)
                && gpp.ValueKind == JsonValueKind.Object)
            {
                if (gpp.TryGetProperty("ItemTag", out var tagEl)
                    && tagEl.ValueKind == JsonValueKind.Object
                    && tagEl.TryGetProperty("TagName", out var tagName)
                    && tagName.ValueKind == JsonValueKind.String)
                {
                    itemTag = tagName.GetString() ?? "";
                }
            }

            if (root.TryGetProperty("InventoryItemUIData", out var ui)
                && ui.ValueKind == JsonValueKind.Object)
            {
                if (ui.TryGetProperty("ItemTexture", out var texEl)
                    && texEl.ValueKind == JsonValueKind.String)
                {
                    iconPath = texEl.GetString() ?? "";
                }
                if (ui.TryGetProperty("ItemName", out var nameEl)
                    && nameEl.ValueKind == JsonValueKind.Object
                    && nameEl.TryGetProperty("Key", out var keyEl)
                    && keyEl.ValueKind == JsonValueKind.String)
                {
                    itemNameKey = keyEl.GetString() ?? "";
                }
            }

            // Must match the exact "<path>/<Stem>.<Stem>" form RecipeCost[i].Item uses, so the patcher writes it through verbatim.
            var packagePath =
                "/R5BusinessRules/InventoryItems/DefaultItems/Resource/"
                + stem + "." + stem;

            return new VanillaResourceEntry
            {
                Stem = stem,
                PackagePath = packagePath,
                DisplayName = PrettifyStem(stem),
                IconPath = iconPath,
                ItemNameKey = itemNameKey,
                ItemTag = itemTag,
            };
        }

        static string PrettifyStem(string stem)
        {
            if (string.IsNullOrEmpty(stem)) return stem ?? "";
            var s = stem;
            if (s.StartsWith("DA_DID_Resource_", StringComparison.Ordinal))
                s = s.Substring("DA_DID_Resource_".Length);
            else if (s.StartsWith("DA_DID_", StringComparison.Ordinal))
                s = s.Substring("DA_DID_".Length);
            return s.Replace('_', ' ');
        }

        void LogLine(string msg) { if (Log != null) Log(msg); }
    }

    public sealed class VanillaResourceEntry
    {
        public string Stem;
        public string PackagePath;
        public string DisplayName;
        public string IconPath;
        public string ItemNameKey;
        public string ItemTag;
    }
}
