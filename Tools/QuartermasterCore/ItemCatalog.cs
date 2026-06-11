using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace Windrose.Quartermaster.Core
{
    // Data source of the in-game item spawner: every vanilla R5BLInventoryItem data asset
    // (Sources/Vanilla JSON dump) paired with its English display name (resolved by the
    // IconExtractor into Icons/[AssetId].json; asset id when no localization exists).
    // The GameDeployer writes the result as qm_modtab_items.txt so the DLL only reads a
    // finished list - no JSON parsing on the game side.
    public static class ItemCatalog
    {
        public sealed class Entry
        {
            public string AssetId;      // PDA asset name, e.g. DA_CID_Alchemy_Bandages_T01
            public string DisplayName;  // English name; disambiguated when duplicated
        }

        // Sorted by display name (case-insensitive). Empty when the vanilla sources are not
        // extracted yet - callers should keep any previously written catalog in that case.
        public static List<Entry> Build(string dataRoot)
        {
            var entries = new List<Entry>();
            var sourcesDir = Path.Combine(dataRoot, "Sources", "Vanilla");
            var iconsDir = Path.Combine(dataRoot, "Icons");
            if (!Directory.Exists(sourcesDir)) return entries;

            foreach (var path in Directory.EnumerateFiles(sourcesDir, "*.json", SearchOption.AllDirectories))
            {
                string id = IsInventoryItem(path);
                if (id == null) continue;
                entries.Add(new Entry
                {
                    AssetId = id,
                    DisplayName = ReadEnglishName(iconsDir, id) ?? id,
                });
            }

            DisambiguateDuplicates(entries);
            entries.Sort((a, b) => string.Compare(a.DisplayName, b.DisplayName, StringComparison.OrdinalIgnoreCase));
            return entries;
        }

        // Asset id (file stem) when the JSON is an R5BLInventoryItem dump, else null.
        static string IsInventoryItem(string jsonPath)
        {
            try
            {
                using var stream = File.OpenRead(jsonPath);
                using var doc = JsonDocument.Parse(stream);
                var root = doc.RootElement;
                if (root.ValueKind != JsonValueKind.Object) return null;
                if (!root.TryGetProperty("$type", out var typeEl)) return null;
                if (typeEl.ValueKind != JsonValueKind.String || typeEl.GetString() != "R5BLInventoryItem") return null;
                return Path.GetFileNameWithoutExtension(jsonPath);
            }
            catch
            {
                return null;
            }
        }

        static string ReadEnglishName(string iconsDir, string assetId)
        {
            var metaPath = Path.Combine(iconsDir, assetId + ".json");
            if (!File.Exists(metaPath)) return null;
            try
            {
                using var stream = File.OpenRead(metaPath);
                using var doc = JsonDocument.Parse(stream);
                if (doc.RootElement.ValueKind != JsonValueKind.Object) return null;
                if (!doc.RootElement.TryGetProperty("en", out var en) || en.ValueKind != JsonValueKind.Object) return null;
                if (!en.TryGetProperty("name", out var nameEl) || nameEl.ValueKind != JsonValueKind.String) return null;
                var name = nameEl.GetString();
                return string.IsNullOrWhiteSpace(name) ? null : name.Trim();
            }
            catch
            {
                return null;
            }
        }

        // Identically named items (tier variants sharing one localized name) get the asset id
        // appended so every dropdown row stays distinguishable: "Bandage (CID_Alchemy_Bandages_T02)".
        static void DisambiguateDuplicates(List<Entry> entries)
        {
            foreach (var group in entries.GroupBy(e => e.DisplayName, StringComparer.OrdinalIgnoreCase))
            {
                if (group.Count() < 2) continue;
                foreach (var e in group)
                {
                    var hint = e.AssetId.StartsWith("DA_", StringComparison.OrdinalIgnoreCase)
                        ? e.AssetId.Substring(3)
                        : e.AssetId;
                    e.DisplayName = e.DisplayName + " (" + hint + ")";
                }
            }
        }
    }
}
