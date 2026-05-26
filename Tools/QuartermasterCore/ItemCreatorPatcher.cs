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
    // Localization is handled by emitting a per-profile string-table CSV
    // at
    //   R5/Content/Localization/Data/InventoryItems_<shortProfileId>.csv
    // The patched JSONs reference {TableId:"InventoryItems_<shortId>",
    // Key:"<Id>_..."} for ItemName / ItemDescription / ItemVanity, which
    // the engine resolves against the per-profile CSV at runtime. Using
    // a per-profile CSV name (instead of overriding the shared vanilla
    // InventoryItems.csv) means two profiles' paks no longer collide via
    // pak load-order - the loser's items would otherwise resolve to
    // <MISSING_STRING> because only one pak's CSV at the shared path
    // wins. ShortProfileId comes from WindrosePaths and is the first 8
    // hex chars of the Profile.Id GUID (matches the QmBldg_<8hex> id
    // scheme).
    //
    // Idempotency: if no custom items exist, the patcher returns an empty
    // result without touching any files.
    //
    // Output formatting matches vanilla: tab indent (size 1), CRLF line
    // endings, trailing CRLF for the JSONs (BuyerPatcher / LootPatcher
    // share this style). The CSV uses BOM + CRLF + standard
    // Key,SourceString,Context header (matches vanilla CSV layout).
    public sealed class ItemCreatorPatcher
    {
        // Output anchor paths inside the staging directory.
        const string CustomItemsFolder = "Custom";

        // CSV header. Matches the vanilla layout exactly.
        const string CsvHeader = "Key,SourceString,Context\r\n";

        // BOM (\xEF\xBB\xBF) - the Windrose CSV loader uses it as a
        // sanity marker; omitting it would make the loader reject the
        // file silently. Each per-profile CSV starts with the BOM and
        // then the header line.
        static readonly byte[] Utf8Bom = new byte[] { 0xEF, 0xBB, 0xBF };

        // No-BOM UTF-8 (the vanilla CSV / JSON files are saved this way).
        static readonly UTF8Encoding Utf8NoBom = new UTF8Encoding(false);

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

            // vanillaInventoryCsvPath is no longer read - the per-profile
            // CSV is a fresh file with only the profile's own rows. Param
            // is kept for API stability (callers still pass it).
            _ = vanillaInventoryCsvPath;

            var result = new ItemCreatorPatchResult();
            var customs = profile.CustomItems;
            if (customs == null || customs.Count == 0) return result;

            if (!Directory.Exists(vanillaInventoryItemsDir))
                throw new DirectoryNotFoundException(vanillaInventoryItemsDir);

            Directory.CreateDirectory(outDir);

            // Per-profile string-table id. Every custom item's FText
            // properties reference this TableId, and the CSV gets written
            // under R5/Content/Localization/Data/<TableId>.csv so the
            // Windrose loader registers exactly this table at boot.
            var itemsTableId = WindrosePaths.InventoryItemsTableIdFor(profile.Id);

            // Cache template JSONs by basename across the loop so two
            // custom items cloning the same template don't pay for two
            // disk reads + parses.
            var templateCache = new Dictionary<string, JsonObject>(StringComparer.OrdinalIgnoreCase);

            // Rows to append to the vanilla CSV at the end. Captured per
            // item so the loop body stays focused on the JSON write.
            var csvRows = new List<CsvRow>(customs.Count * 2);

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

                ApplyCustomItemOverrides(root, custom, willBakeIcon, itemsTableId);

                // Write the JSON at the conventional Custom/ subfolder.
                var relFile = Path.Combine("R5", "Plugins", "R5BusinessRules",
                                           "Content", "InventoryItems",
                                           CustomItemsFolder, custom.Id + ".json");
                var outPath = Path.Combine(outDir, relFile);
                Directory.CreateDirectory(Path.GetDirectoryName(outPath));
                File.WriteAllBytes(outPath, SerializeWithTabsAndCrlf(root));
                result.ItemsWritten++;
                result.WrittenItems.Add(custom.Id);

                // Even if Name/Description/Vanity are empty strings we emit
                // rows - a custom item with no display name appears as a
                // blank in the inventory which is rarely what the user
                // wants, but it's a recoverable mistake (just type the
                // name later). The empty value still binds the FText key
                // to something, preventing the engine from falling back to
                // the "missing key" placeholder. VanityText follows the
                // exact same rule as Name/Description: whatever the user
                // typed is the truth, including "" (hides the flavor line).
                csvRows.Add(new CsvRow(custom.Id + "_ItemName", custom.Name ?? string.Empty));
                csvRows.Add(new CsvRow(custom.Id + "_ItemDescription", custom.Description ?? string.Empty));
                csvRows.Add(new CsvRow(custom.Id + "_ItemVanity", custom.VanityText ?? string.Empty));
            }

            if (csvRows.Count > 0)
            {
                var pakInternalCsvPath = WindrosePaths.InventoryItemsCsvPakPathFor(profile.Id);
                WriteCsv(pakInternalCsvPath, outDir, csvRows, result);
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
        static void ApplyCustomItemOverrides(JsonObject root, CustomItem custom, bool willBakeIcon, string itemsTableId)
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
                // FText shape used by every vanilla item: TableId + Key.
                // The CSV side of the patcher will emit matching rows so
                // the engine resolves these at runtime.
                ui["ItemName"] = new JsonObject
                {
                    ["TableId"] = itemsTableId,
                    ["Key"] = custom.Id + "_ItemName",
                };
                ui["ItemDescription"] = new JsonObject
                {
                    ["TableId"] = itemsTableId,
                    ["Key"] = custom.Id + "_ItemDescription",
                };

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
                // tooltip; no "inherit from template" fallback. The loop
                // at the bottom of PatchToDirectory always emits the
                // matching CSV row.
                ui["VanityText"] = new JsonObject
                {
                    ["TableId"] = itemsTableId,
                    ["Key"] = custom.Id + "_ItemVanity",
                };
            }
        }

        // Writes a fresh per-profile CSV at <outDir>/<pakInternalCsvPath>.
        // No vanilla baseline is copied - the per-profile TableId only
        // carries this profile's custom-item rows, so the file is small
        // and focused. The Windrose CSV loader expects a BOM + standard
        // header on its localization CSVs, and CRLF line endings; we
        // emit all three to match the vanilla layout.
        void WriteCsv(string pakInternalCsvPath, string outDir,
            List<CsvRow> rows, ItemCreatorPatchResult result)
        {
            var outPath = Path.Combine(outDir, pakInternalCsvPath.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(outPath));

            using var ms = new MemoryStream();
            ms.Write(Utf8Bom, 0, Utf8Bom.Length);
            var headerBytes = Utf8NoBom.GetBytes(CsvHeader);
            ms.Write(headerBytes, 0, headerBytes.Length);

            foreach (var row in rows)
            {
                var line = EscapeCsvField(row.Key) + ","
                         + EscapeCsvField(row.Value) + ","
                         + EscapeCsvField(string.Empty)
                         + "\r\n";
                var lineBytes = Utf8NoBom.GetBytes(line);
                ms.Write(lineBytes, 0, lineBytes.Length);
            }

            File.WriteAllBytes(outPath, ms.ToArray());
            result.CsvRowsAppended = rows.Count;
            result.CsvWritten = true;
            result.CsvOutPath = outPath;
        }

        // Standard CSV escaping: wrap in double quotes, double any internal
        // double quotes. Newlines stay literal inside the quoted value -
        // matches how vanilla rows that span lines (e.g. multi-paragraph
        // descriptions) look.
        static string EscapeCsvField(string s)
        {
            if (s == null) s = string.Empty;
            return "\"" + s.Replace("\"", "\"\"") + "\"";
        }

        // Valid id characters: alnum + underscore. Custom items pass an id
        // that becomes a filename, asset name, GameplayTag suffix, and CSV
        // key prefix simultaneously - so we lock it down to what every
        // consumer accepts. Frontend enforces this too but defense in depth
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

        readonly struct CsvRow
        {
            public readonly string Key;
            public readonly string Value;
            public CsvRow(string key, string value) { Key = key; Value = value; }
        }
    }

    public sealed class ItemCreatorPatchResult
    {
        public int Scanned;
        public int ItemsWritten;      // count of new JSONs written under Custom/
        public bool CsvWritten;       // true if the per-profile InventoryItems_<shortId>.csv was emitted
        public int CsvRowsAppended;   // 3x ItemsWritten (Name + Description + Vanity) for a successful run
        // Absolute on-disk path of the written CSV (per-profile, lives
        // under <outDir>/R5/Content/Localization/Data/InventoryItems_<shortId>.csv).
        public string CsvOutPath;

        public List<string> WrittenItems = new List<string>();
        public List<string> Warnings    = new List<string>();
    }
}
