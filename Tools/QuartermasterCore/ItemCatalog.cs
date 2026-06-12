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
            public string PackagePath;  // custom items only: mounted package for the DLL's sync-load fallback; null for vanilla
            public string Category;     // spawner group (see Categorize); "Custom" for item-creator items
        }

        // Custom items created in the Configurator's item creator are cooked into each
        // profile's pak under this mount (ItemCreatorPatcher writes
        // R5/Plugins/R5BusinessRules/Content/InventoryItems/Custom/<Id>.json).
        const string CustomItemPackageRoot = "/R5BusinessRules/InventoryItems/Custom/";

        // Sorted by display name (case-insensitive). Empty when the vanilla sources are not
        // extracted yet AND no installed profile carries custom items - callers should keep
        // any previously written catalog in that case. profileJsonPaths: the installed
        // qm_profile_*.json files; their customItems (id + friendly name) join the catalog.
        public static List<Entry> Build(string dataRoot, IEnumerable<string> profileJsonPaths = null)
        {
            var entries = new List<Entry>();
            var sourcesDir = Path.Combine(dataRoot, "Sources", "Vanilla");
            var iconsDir = Path.Combine(dataRoot, "Icons");

            if (Directory.Exists(sourcesDir))
            {
                foreach (var path in Directory.EnumerateFiles(sourcesDir, "*.json", SearchOption.AllDirectories))
                {
                    string id = IsInventoryItem(path);
                    if (id == null) continue;
                    string displayName = ReadEnglishName(iconsDir, id) ?? id;
                    // The game's localization flags dead items with an uppercase "NOT USED"
                    // marker (usually a name prefix, sometimes after "Decoration: ") - those
                    // assets are cut content and stay out of the spawner. Case-sensitive on
                    // purpose: lowercase occurrences would be regular item names.
                    if (displayName.Contains("NOT USED", StringComparison.Ordinal)) continue;
                    entries.Add(new Entry
                    {
                        AssetId = id,
                        DisplayName = displayName,
                        Category = Categorize(path, sourcesDir),
                    });
                }
            }

            AppendCustomItems(entries, profileJsonPaths);
            DisambiguateDuplicates(entries);
            entries.Sort((a, b) => string.Compare(a.DisplayName, b.DisplayName, StringComparison.OrdinalIgnoreCase));
            return entries;
        }

        // The installed profile JSONs are the source of truth for custom items: the pak
        // itself is opaque at this layer, but every build deploys its profile JSON with
        // the customItems list (id = PDA asset name in the pak, name = friendly name).
        static void AppendCustomItems(List<Entry> entries, IEnumerable<string> profileJsonPaths)
        {
            if (profileJsonPaths == null) return;
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var e in entries) seen.Add(e.AssetId);

            foreach (var profilePath in profileJsonPaths)
            {
                try
                {
                    using var stream = File.OpenRead(profilePath);
                    using var doc = JsonDocument.Parse(stream);
                    if (doc.RootElement.ValueKind != JsonValueKind.Object) continue;
                    if (!doc.RootElement.TryGetProperty("customItems", out var items)
                        || items.ValueKind != JsonValueKind.Array) continue;

                    foreach (var item in items.EnumerateArray())
                    {
                        if (item.ValueKind != JsonValueKind.Object) continue;
                        string id = item.TryGetProperty("id", out var idEl) && idEl.ValueKind == JsonValueKind.String
                            ? idEl.GetString() : null;
                        if (string.IsNullOrWhiteSpace(id) || !seen.Add(id)) continue;
                        string name = item.TryGetProperty("name", out var nameEl) && nameEl.ValueKind == JsonValueKind.String
                            ? nameEl.GetString() : null;
                        entries.Add(new Entry
                        {
                            AssetId = id,
                            DisplayName = string.IsNullOrWhiteSpace(name) ? id : name.Trim(),
                            PackagePath = CustomItemPackageRoot + id,
                            Category = "Custom",
                        });
                    }
                }
                catch
                {
                    // Unreadable profile JSON - its custom items just stay out of the catalog.
                }
            }
        }

        // Spawner category from the vanilla source folder under .../InventoryItems/ - the
        // folder tree is the cleanest taxonomy the game data offers (the PDA's own
        // InventoryItemUIData.Category puts over half the items into "Misc"). Folders are
        // mapped onto ~10 coarse groups; anything unmapped (NPC station items, treasure
        // maps, expedition curios, items outside the InventoryItems tree) lands in "Misc".
        // Match order matters: the specific Consumables/* splits run before the catch-all.
        static string Categorize(string jsonPath, string sourcesDir)
        {
            string rel = Path.GetRelativePath(sourcesDir, jsonPath).Replace('\\', '/');
            int idx = rel.IndexOf("/InventoryItems/", StringComparison.OrdinalIgnoreCase);
            string sub = idx >= 0 ? rel.Substring(idx + "/InventoryItems/".Length) : "";

            bool Under(string prefix) => sub.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);

            if (Under("Equipments/Weapon") || Under("Ammo/"))            return "Weapons";
            if (Under("Equipments/Armor") || Under("Equipments/Backpack")) return "Armor";
            if (Under("Equipments/Jewelry"))                              return "Jewelry";
            if (Under("Equipments/Tool") || Under("Equipments/Resource")) return "Tools";
            if (Under("Consumables/SeaTrade") || Under("DefaultItems/Trading")) return "Trading";
            if (Under("Consumables/Ship"))                                return "Ship";
            if (Under("Consumables/"))                                    return "Consumables";
            if (Under("DefaultItems/Resource"))                           return "Resources";
            if (Under("Ship/") || Under("DefaultItems/Misc/ShipCustomization")) return "Ship";
            if (Under("DefaultItems/Misc/RecipePaperUnlock"))             return "Recipes";
            return "Misc";
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
