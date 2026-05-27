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
    // Synthesizes brand-new R5BLInventoryItem JSONs from user-supplied
    // CustomItem entries. Each entry is cloned from a vanilla template,
    // gets its editable fields overwritten, and lands under
    //   R5/Plugins/R5BusinessRules/Content/InventoryItems/Custom/<Id>.json
    // so the engine indexes it as
    //   /R5BusinessRules/InventoryItems/Custom/<Id>.<Id>
    //
    // Localization is emitted inline as plain-string FText values directly
    // in the JSON (no StringTable indirection). Vanilla itself ships items
    // that do this - e.g. DA_DID_Misc_EliaShell_T04 has
    //   "ItemName": "Ozhereliye Elii", "ItemDescription": "...", "VanityText": "..."
    // The UE FText JSON deserializer treats a plain string property as
    // FText.Base with Namespace="" and SourceString=value, exactly the
    // shape we need for user-supplied display text.
    //
    // Why no StringTable: a per-profile CSV (InventoryItems_<shortId>.csv)
    // would not be picked up by the Windrose CSV loader at boot - it only
    // registers the two vanilla CSVs (InventoryItems.csv + BuildingItems.csv)
    // by hardcoded name. Per-profile CSVs in the pak would land on disk
    // but never become StringTables, so the FText lookups would always
    // resolve to <MISSING_STRING>. Plain-string FText sidesteps the
    // loader entirely and ships the text in the item JSON itself.
    //
    // Idempotency: if no custom items exist, the patcher returns an empty
    // result without touching any files.
    //
    // Output formatting matches vanilla: tab indent (size 1), CRLF line
    // endings, trailing CRLF for the JSONs (BuyerPatcher / LootPatcher
    // share this style).
    public sealed class ItemCreatorPatcher
    {
        // Output anchor paths inside the staging directory.
        const string CustomItemsFolder = "Custom";

        // bakeableItemIds: set of CustomItem.Id values for which the build
        // pipeline already verified an uploaded PNG exists on disk and a
        // baked T_QmCustomIcon_<id> texture WILL be produced in the
        // IoStore composite. Items with IconPath set but missing from
        // this set fall back to either custom.ItemTexture (if set) or
        // the cloned template's default. Pass null to disable the
        // synthesized-icon path entirely (used by CLI / tests that don't
        // run the IoStore composite).
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

            // vanillaInventoryCsvPath is no longer read - display text is
            // emitted as plain-string FText inline in each item JSON, so
            // no CSV is generated at all. Param kept for API stability
            // (older callers still pass it).
            _ = vanillaInventoryCsvPath;

            var result = new ItemCreatorPatchResult();
            var customs = profile.CustomItems;
            if (customs == null || customs.Count == 0) return result;

            if (!Directory.Exists(vanillaInventoryItemsDir))
                throw new DirectoryNotFoundException(vanillaInventoryItemsDir);

            Directory.CreateDirectory(outDir);

            // Cache template JSONs by basename across the loop so two
            // custom items cloning the same template don't pay for two
            // disk reads + parses.
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

                // DeepClone so the cached template stays pristine for any
                // sibling custom item that also clones from it.
                var root = (JsonObject)template.DeepClone();

                bool willBakeIcon = bakeableItemIds != null
                    && !string.IsNullOrEmpty(custom.Id)
                    && bakeableItemIds.Contains(custom.Id);
                if (!willBakeIcon
                    && !string.IsNullOrWhiteSpace(custom.IconPath)
                    && string.IsNullOrWhiteSpace(custom.ItemTexture))
                {
                    // User configured a custom icon but the build pipeline
                    // can't bake it (file missing, or running under CLI
                    // with the IoStore composite disabled). Emit a clear
                    // warning so the user understands why the item ships
                    // with the template's icon instead of the uploaded
                    // PNG. Picked up by the Build log via result.Warnings.
                    result.Warnings.Add("Custom item '" + custom.Id
                        + "' has IconPath '" + custom.IconPath
                        + "' but no baked texture will be produced - "
                        + "item ships with the template icon. "
                        + "(Re-upload the PNG, or run a GUI build.)");
                }

                ApplyCustomItemOverrides(root, custom, willBakeIcon);

                // Write the JSON at the conventional Custom/ subfolder.
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

        // Walk the vanilla tree for a JSON file with the matching basename.
        // Returns the parsed JsonObject root or null if not found. Multiple
        // matches would be a vanilla bug (basename collision) so we just
        // take the first one and continue.
        static JsonObject LoadTemplate(string vanillaDir, string basename)
        {
            // Direct file probe is cheaper than full enumeration when the
            // basename is well-known, but vanilla items can live in any
            // subfolder (DefaultItems/Misc, Consumables/Food, ...), so a
            // walk-and-match is the only reliable lookup.
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

        // Applies the user-editable overrides onto the cloned template root.
        // Each field has its own null/empty handling so the user can leave
        // a property at "inherit from template" by leaving it null in the
        // profile. willBakeIcon comes from the build pipeline's pre-flight
        // PNG existence check; only when true do we point ItemTexture at
        // the synthesized Custom/T_QmCustomIcon_<id> asset.
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

                // ItemTag handling: we KEEP the template's original tag
                // verbatim instead of synthesizing a unique one. Two
                // reasons:
                //
                // 1. UE5 validates every GameplayTag against the
                //    registered tag list at marshalling time. Tags that
                //    aren't registered (in DefaultGameplayTags.ini or via
                //    native code) get rejected with an "Invalid gameplay
                //    tag name" R5Check - which means any code path that
                //    queries the tag (consume abilities, buffs, recipes)
                //    silently fails. We can't register new tags from a
                //    mod pak, so a synthesized tag like
                //    "ItemData.QmCustom.<id>" or
                //    "ConsData.Food.Rum.Bottle.T03.QmCustom_<id>" both
                //    fail validation. Empirically: cloning Rum Bottle
                //    with an appended tag broke right-click consume.
                //
                // 2. Identity-separation between our clone and the
                //    template is already provided by the unique asset
                //    path (/R5BusinessRules/InventoryItems/Custom/<id>).
                //    Buyer recipes / loot tables reference items by
                //    asset path, not by tag, so the clone won't be
                //    accidentally matched by recipes asking for the
                //    template asset.
                //
                // Consequence the user should know about: gameplay
                // systems that filter by tag (e.g. "any Rum Bottle T03")
                // will now also match our clone. For consumables that's
                // exactly the desired behaviour - the Use button needs
                // the tag to fire the consume ability. For purely
                // cosmetic clones the impact is negligible.
            }

            if (ui != null)
            {
                // FText shape: plain string. UE's FText JSON deserializer
                // treats a string property as FText.Base (HistoryType=0)
                // with Namespace="" and SourceString=<value>. Vanilla
                // items like DA_DID_Misc_EliaShell_T04 ship their
                // ItemName / ItemDescription this way. The text travels
                // inside the item asset itself - no string-table lookup,
                // no per-profile CSV that the loader would not register.
                ui["ItemName"] = custom.Name ?? string.Empty;
                ui["ItemDescription"] = custom.Description ?? string.Empty;

                // Custom icon resolution priority:
                //   1. willBakeIcon true       -> point ItemTexture at the
                //      synthesized Custom/T_QmCustomIcon_<id> asset that
                //      IconBakerPatcher will bake into the IoStore composite.
                //      Helper kept identical to the one the baker uses so
                //      the synthesised JSON ref always matches the baked
                //      asset's package path.
                //   2. custom.ItemTexture set  -> verbatim asset reference
                //      (used by templates pulled from the catalog or
                //      hand-written paths). Also the fallback when the
                //      user configured an IconPath but the bake won't run
                //      (PNG missing / CLI build).
                //   3. neither                 -> keep whatever the cloned
                //      template had (vanilla Piastre uses
                //      .../T_ItemIcon_Loot_T02_CoinPiastre_01).
                if (willBakeIcon)
                {
                    ui["ItemTexture"] = IconBakerPatcher.ItemTextureRefFor(custom.Id);
                }
                else if (!string.IsNullOrWhiteSpace(custom.ItemTexture))
                {
                    ui["ItemTexture"] = custom.ItemTexture;
                }

                // VanityText is always overridden - same flow as Name /
                // Description. Empty input means empty flavor line in the
                // tooltip; no "inherit from template" fallback. Vanilla
                // items overwhelmingly use the plain-string form (e.g.
                // every consumable ships VanityText: "").
                ui["VanityText"] = custom.VanityText ?? string.Empty;
            }
        }

        // Valid id characters: alnum + underscore. Custom items pass an id
        // that becomes a filename, asset name, and GameplayTag suffix
        // simultaneously - so we lock it down to what every consumer
        // accepts. Frontend enforces this too but defense in depth
        // catches malformed profiles edited by hand.
        static bool IsSafeId(string id)
        {
            foreach (var c in id)
            {
                if (char.IsLetterOrDigit(c) || c == '_') continue;
                return false;
            }
            return id.Length > 0 && id.Length <= 80;
        }

        // Tab-indent (size 1), CRLF line endings, trailing CRLF. Same shape
        // BuyerPatcher uses, so all patched JSONs share one canonical
        // serializer.
        static byte[] SerializeWithTabsAndCrlf(JsonObject root)
        {
            using var ms = new MemoryStream();
            var writerOptions = new JsonWriterOptions
            {
                Indented = true,
                IndentCharacter = '\t',
                IndentSize = 1,
                NewLine = "\r\n",
                Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            };
            using (var writer = new Utf8JsonWriter(ms, writerOptions))
            {
                root.WriteTo(writer);
            }
            ms.WriteByte((byte)'\r');
            ms.WriteByte((byte)'\n');
            return ms.ToArray();
        }

    }

    public sealed class ItemCreatorPatchResult
    {
        public int Scanned;
        public int ItemsWritten;      // count of new JSONs written under Custom/

        public List<string> WrittenItems = new List<string>();
        public List<string> Warnings    = new List<string>();
    }
}
