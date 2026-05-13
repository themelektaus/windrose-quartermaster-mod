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
    // Localization is handled by emitting an extended copy of the vanilla
    // InventoryItems.csv string-table at
    //   R5/Content/Localization/Data/InventoryItems.csv
    // The patched JSONs reference {TableId:"InventoryItems", Key:"<Id>_..."}
    // for ItemName / ItemDescription, which the engine resolves against the
    // CSV at runtime. Because a mod pak's CSV completely overrides vanilla
    // at the same path, the emitted file MUST contain every vanilla row
    // plus the new ones - otherwise vanilla item names break.
    //
    // Idempotency: if no custom items exist, the patcher returns an empty
    // result without touching any files.
    //
    // Output formatting matches vanilla: tab indent (size 1), CRLF line
    // endings, trailing CRLF for the JSONs (BuyerPatcher / LootPatcher
    // share this style). The CSV is emitted with the vanilla CRLF style
    // and the same trailing-newline rule.
    public sealed class ItemCreatorPatcher
    {
        // Output anchor paths inside the staging directory.
        const string CustomItemsFolder = "Custom";
        const string CsvOutRelPath = "R5/Content/Localization/Data/InventoryItems.csv";

        // No-BOM UTF-8 (the vanilla CSV / JSON files are saved this way).
        static readonly UTF8Encoding Utf8NoBom = new UTF8Encoding(false);

        public ItemCreatorPatchResult PatchToDirectory(
            string vanillaInventoryItemsDir,
            string vanillaInventoryCsvPath,
            string outDir,
            Profile profile)
        {
            if (string.IsNullOrEmpty(vanillaInventoryItemsDir)) throw new ArgumentNullException("vanillaInventoryItemsDir");
            if (string.IsNullOrEmpty(vanillaInventoryCsvPath))  throw new ArgumentNullException("vanillaInventoryCsvPath");
            if (string.IsNullOrEmpty(outDir))                   throw new ArgumentNullException("outDir");
            if (profile == null)                                 throw new ArgumentNullException("profile");

            var result = new ItemCreatorPatchResult();
            var customs = profile.CustomItems;
            if (customs == null || customs.Count == 0) return result;

            if (!Directory.Exists(vanillaInventoryItemsDir))
                throw new DirectoryNotFoundException(vanillaInventoryItemsDir);
            if (!File.Exists(vanillaInventoryCsvPath))
                throw new FileNotFoundException(
                    "Vanilla InventoryItems.csv not found at " + vanillaInventoryCsvPath
                    + " - re-run setup so the dumper extracts it.");

            Directory.CreateDirectory(outDir);

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

                ApplyCustomItemOverrides(root, custom);

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
                WriteExtendedCsv(vanillaInventoryCsvPath, outDir, csvRows, result);
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
        // profile.
        static void ApplyCustomItemOverrides(JsonObject root, CustomItem custom)
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

                // Give the synthesized item a unique GameplayTag so the
                // engine doesn't conflate it with the template (which
                // would scramble any downstream code that maps tags ->
                // items, e.g. recipes that reference the original
                // Piastre's tag). Vanilla items use namespaces like
                // ItemData.Misc.CoinPiastre.T02 - we slot ours under a
                // dedicated QmCustom subspace for easy tag-prefix filtering.
                if (gpp["ItemTag"] is JsonObject tagObj)
                {
                    tagObj["TagName"] = "ItemData.QmCustom." + custom.Id;
                }
                else
                {
                    gpp["ItemTag"] = new JsonObject
                    {
                        ["TagName"] = "ItemData.QmCustom." + custom.Id,
                    };
                }
            }

            if (ui != null)
            {
                // FText shape used by every vanilla item: TableId + Key.
                // The CSV side of the patcher will emit matching rows so
                // the engine resolves these at runtime.
                ui["ItemName"] = new JsonObject
                {
                    ["TableId"] = "InventoryItems",
                    ["Key"] = custom.Id + "_ItemName",
                };
                ui["ItemDescription"] = new JsonObject
                {
                    ["TableId"] = "InventoryItems",
                    ["Key"] = custom.Id + "_ItemDescription",
                };

                // Custom icon path is opt-in. Empty / null = keep the
                // template's texture, which is what phase 1 always does
                // (the frontend renders the field read-only).
                if (!string.IsNullOrWhiteSpace(custom.ItemTexture))
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
                    ["TableId"] = "InventoryItems",
                    ["Key"] = custom.Id + "_ItemVanity",
                };
            }
        }

        // The CSV layout matches what repak extracts: UTF-8 (no BOM), CRLF
        // line endings, header row "Key,SourceString,Context", and each
        // data row uses doubled double-quotes for escaping. We read the
        // vanilla file as-is and append our rows verbatim - this keeps
        // diffs against vanilla tiny and avoids re-quoting issues with
        // edge cases (entries containing newlines, etc.).
        void WriteExtendedCsv(string vanillaCsvPath, string outDir,
            List<CsvRow> rows, ItemCreatorPatchResult result)
        {
            var outPath = Path.Combine(outDir, CsvOutRelPath.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(outPath));

            // Slurp the vanilla bytes. We append to a binary buffer so
            // BOM / trailing-newline subtleties are preserved exactly.
            var vanillaBytes = File.ReadAllBytes(vanillaCsvPath);

            using var ms = new MemoryStream();
            ms.Write(vanillaBytes, 0, vanillaBytes.Length);

            // Ensure the vanilla content ends with CRLF before we append.
            // Most exports do, but the last line of pak-extracted CSVs is
            // sometimes naked - this avoids merging "...vanilla" +
            // "QmItem...,..." into one corrupt line.
            if (vanillaBytes.Length > 0)
            {
                var lastByte = vanillaBytes[vanillaBytes.Length - 1];
                if (lastByte != (byte)'\n')
                {
                    ms.WriteByte((byte)'\r');
                    ms.WriteByte((byte)'\n');
                }
            }

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
        public bool CsvWritten;       // true if the extended InventoryItems.csv was emitted
        public int CsvRowsAppended;   // 2x ItemsWritten (Name + Description) for a successful run

        public List<string> WrittenItems = new List<string>();
        public List<string> Warnings    = new List<string>();
    }
}
