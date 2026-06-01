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
    public sealed class ItemCreatorPatcher
    {
        const string CustomItemsFolder = "Custom";

        // bakeableItemIds: ids with a verified PNG that will get a baked texture; null disables the synthesized-icon path entirely.
        public ItemCreatorPatchResult PatchToDirectory(
            string vanillaInventoryItemsDir,
            string vanillaInventoryCsvPath,
            string outDir,
            Profile profile,
            HashSet<string> bakeableItemIds = null)
        {
            if (string.IsNullOrEmpty(vanillaInventoryItemsDir)) throw new ArgumentNullException("vanillaInventoryItemsDir");
            if (string.IsNullOrEmpty(outDir))                   throw new ArgumentNullException("outDir");
            if (profile == null)                                 throw new ArgumentNullException("profile");
            if (string.IsNullOrEmpty(profile.Id))                throw new ArgumentException("profile.Id is required (drives the per-profile string-table id)");

            // No longer read; param kept for API stability.
            _ = vanillaInventoryCsvPath;

            var result = new ItemCreatorPatchResult();
            var customs = profile.CustomItems;
            if (customs == null || customs.Count == 0) return result;

            if (!Directory.Exists(vanillaInventoryItemsDir))
                throw new DirectoryNotFoundException(vanillaInventoryItemsDir);

            Directory.CreateDirectory(outDir);

            var templateCache = new Dictionary<string, JsonObject>(StringComparer.OrdinalIgnoreCase);

            foreach (var custom in customs)
            {
                result.Scanned++;
                if (custom == null) continue;
                if (string.IsNullOrWhiteSpace(custom.Id))
                {
                    result.Warnings.Add("Custom item with empty Id - skipped.");
                    continue;
                }
                if (!IsSafeId(custom.Id))
                {
                    result.Warnings.Add("Custom item id '" + custom.Id
                        + "' contains illegal characters - skipped (allowed: A-Z a-z 0-9 _).");
                    continue;
                }
                if (string.IsNullOrWhiteSpace(custom.TemplateId))
                {
                    result.Warnings.Add("Custom item '" + custom.Id
                        + "' has no TemplateId - skipped.");
                    continue;
                }

                JsonObject template;
                if (!templateCache.TryGetValue(custom.TemplateId, out template))
                {
                    template = LoadTemplate(vanillaInventoryItemsDir, custom.TemplateId);
                    if (template == null)
                    {
                        result.Warnings.Add("Template '" + custom.TemplateId
                            + "' not found in vanilla InventoryItems - skipped (item '"
                            + custom.Id + "').");
                        continue;
                    }
                    templateCache[custom.TemplateId] = template;
                }

                // DeepClone: the cached template must stay pristine for siblings cloning the same template.
                var root = (JsonObject)template.DeepClone();

                bool willBakeIcon = bakeableItemIds != null
                    && !string.IsNullOrEmpty(custom.Id)
                    && bakeableItemIds.Contains(custom.Id);
                if (!willBakeIcon
                    && !string.IsNullOrWhiteSpace(custom.IconPath)
                    && string.IsNullOrWhiteSpace(custom.ItemTexture))
                {
                    result.Warnings.Add("Custom item '" + custom.Id
                        + "' has IconPath '" + custom.IconPath
                        + "' but no baked texture will be produced - "
                        + "item ships with the template icon. "
                        + "(Re-upload the PNG, or run a GUI build.)");
                }

                ApplyCustomItemOverrides(root, custom, willBakeIcon);

                var relFile = Path.Combine("R5", "Plugins", "R5BusinessRules",
                                           "Content", "InventoryItems",
                                           CustomItemsFolder, custom.Id + ".json");
                var outPath = Path.Combine(outDir, relFile);
                Directory.CreateDirectory(Path.GetDirectoryName(outPath));
                File.WriteAllBytes(outPath, SerializeWithTabsAndCrlf(root));
                result.ItemsWritten++;
                result.WrittenItems.Add(custom.Id);
            }

            return result;
        }

        static JsonObject LoadTemplate(string vanillaDir, string basename)
        {
            foreach (var path in Directory.EnumerateFiles(vanillaDir, "*.json", SearchOption.AllDirectories))
            {
                if (string.Equals(Path.GetFileNameWithoutExtension(path), basename, StringComparison.OrdinalIgnoreCase))
                {
                    try
                    {
                        var node = JsonNode.Parse(File.ReadAllText(path, Encoding.UTF8));
                        return node as JsonObject;
                    }
                    catch
                    {
                        return null;
                    }
                }
            }
            return null;
        }

        static void ApplyCustomItemOverrides(JsonObject root, CustomItem custom, bool willBakeIcon)
        {
            var gpp = root["InventoryItemGppData"] as JsonObject;
            var ui  = root["InventoryItemUIData"]  as JsonObject;

            if (gpp != null)
            {
                if (custom.MaxCountInSlot.HasValue && custom.MaxCountInSlot.Value > 0)
                {
                    gpp["MaxCountInSlot"] = custom.MaxCountInSlot.Value;
                }
                if (!string.IsNullOrWhiteSpace(custom.Rarity))
                {
                    gpp["Rarity"] = custom.Rarity;
                }
                if (custom.KeepInInventoryOnDeath.HasValue)
                {
                    gpp["bKeepInInventoryOnDeath"] = custom.KeepInInventoryOnDeath.Value;
                }

                // Deliberately leave the template's GameplayTag untouched: a mod pak cannot register new tags, and an unregistered tag is rejected at marshalling time, silently breaking consume/buff/recipe lookups.
            }

            if (ui != null)
            {
                // A plain string is accepted here: UE's FText JSON deserializer reads it as an inline FText, so no string-table entry is needed.
                ui["ItemName"] = custom.Name ?? string.Empty;
                ui["ItemDescription"] = custom.Description ?? string.Empty;

                if (willBakeIcon)
                {
                    ui["ItemTexture"] = IconBakerPatcher.ItemTextureRefFor(custom.Id);
                }
                else if (!string.IsNullOrWhiteSpace(custom.ItemTexture))
                {
                    ui["ItemTexture"] = custom.ItemTexture;
                }

                ui["VanityText"] = custom.VanityText ?? string.Empty;
            }
        }

        static bool IsSafeId(string id)
        {
            foreach (var c in id)
            {
                if (char.IsLetterOrDigit(c) || c == '_') continue;
                return false;
            }
            return id.Length > 0 && id.Length <= 80;
        }

    }

    public sealed class ItemCreatorPatchResult
    {
        public int Scanned;
        public int ItemsWritten;

        public List<string> WrittenItems = new List<string>();
        public List<string> Warnings    = new List<string>();
    }
}
