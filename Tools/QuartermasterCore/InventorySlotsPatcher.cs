using System;
using System.IO;
using System.Text;
using System.Text.Json.Nodes;

namespace Windrose.Quartermaster.Core
{
    // Patches the player inventory blueprint DA_PlayerInventoryParams.json to grow
    // the number of Ring / Necklace equipment slots (the "More Rings and Necklace
    // Slots" mod, but slider-driven). Vanilla is 1 ring + 1 necklace; the Jewelry
    // module's Slots[] carries a CountSlots int per slot type.
    //
    // IMPORTANT: shipping this pak only changes the inventory *template* used when a
    // NEW character is created. Existing characters bake their slot layout into the
    // RocksDB save at creation, so they need the separate save patcher
    // (InventorySaveSlotsPatcher) on top of this pak.
    public sealed class InventorySlotsPatcher
    {
        public const int VanillaRingSlots = 1;
        public const int VanillaNecklaceSlots = 1;
        public const int MinSlots = 1;
        public const int MaxSlots = 10;

        const string JewelryTag = "Inventory.Module.Jewelry";
        // Matched as case-insensitive substrings against each slot's SlotParams
        // reference path; classified by marker, never by array index.
        const string RingMarker = "DA_BL_Slot_Equipment_Ring";
        const string NecklaceMarker = "DA_BL_Slot_Equipment_Necklace";

        static readonly UTF8Encoding Utf8NoBom = new UTF8Encoding(false);

        public Action<string> Log;

        public InventorySlotsPatchResult PatchToDirectory(
            string vanillaInventoryDir, string outDir,
            int? ringSlots, int? necklaceSlots)
        {
            if (string.IsNullOrEmpty(vanillaInventoryDir))
                throw new ArgumentNullException("vanillaInventoryDir");
            if (string.IsNullOrEmpty(outDir))
                throw new ArgumentNullException("outDir");

            var result = new InventorySlotsPatchResult();
            int effRing = ringSlots ?? VanillaRingSlots;
            int effNeck = necklaceSlots ?? VanillaNecklaceSlots;
            ValidateSlots(effRing, effNeck);
            result.RingSlots = effRing;
            result.NecklaceSlots = effNeck;

            if (effRing == VanillaRingSlots && effNeck == VanillaNecklaceSlots)
            {
                result.Skipped = true;
                return result;
            }

            var vanillaJsonPath = Path.Combine(
                vanillaInventoryDir, "DA_PlayerInventoryParams.json");
            if (!File.Exists(vanillaJsonPath))
                throw new FileNotFoundException(
                    "Vanilla DA_PlayerInventoryParams.json not found: "
                    + vanillaJsonPath + " - run Setup to dump the Inventory/ tree.");

            var raw = File.ReadAllText(vanillaJsonPath, Encoding.UTF8);
            var root = JsonNode.Parse(raw) as JsonObject;
            if (root == null)
                throw new InvalidDataException(
                    "Failed to parse " + vanillaJsonPath + " as a JSON object");

            var modules = root["InventoryModules"]?.AsArray();
            if (modules == null)
                throw new InvalidDataException(
                    "DA_PlayerInventoryParams.json is missing the InventoryModules array.");

            JsonArray jewelrySlots = null;
            foreach (var moduleNode in modules)
            {
                var tag = moduleNode?["ModuleTag"]?["TagName"]?.GetValue<string>();
                if (string.Equals(tag, JewelryTag, StringComparison.Ordinal))
                {
                    jewelrySlots = moduleNode["Slots"]?.AsArray();
                    break;
                }
            }
            if (jewelrySlots == null)
                throw new InvalidDataException(
                    "Jewelry module (" + JewelryTag + ") with a Slots array not found "
                    + "in DA_PlayerInventoryParams.json - the data layout changed.");

            foreach (var slotNode in jewelrySlots)
            {
                if (slotNode == null) continue;
                var sp = slotNode["SlotParams"]?.GetValue<string>() ?? "";
                int? target = ClassifySlot(sp, effRing, effNeck);
                if (!target.HasValue) continue;

                var oldNode = slotNode["CountSlots"];
                int oldVal = oldNode != null ? oldNode.GetValue<int>() : -1;
                if (oldVal == target.Value) continue;

                slotNode["CountSlots"] = target.Value;
                if (sp.IndexOf(NecklaceMarker, StringComparison.OrdinalIgnoreCase) >= 0)
                    result.NecklacePatched = true;
                else if (sp.IndexOf(RingMarker, StringComparison.OrdinalIgnoreCase) >= 0)
                    result.RingPatched = true;
            }

            // Bail without writing if nothing matched: shipping a byte-identical
            // copy of vanilla while reporting a patch would be undiagnosable.
            if (!result.RingPatched && !result.NecklacePatched)
            {
                result.Skipped = true;
                return result;
            }

            var bytes = R5Json.SerializeWithTabsAndCrlf(root);
            var outFile = Path.Combine(outDir, "R5", "Plugins", "R5BusinessRules",
                "Content", "Inventory", "DA_PlayerInventoryParams.json");
            Directory.CreateDirectory(Path.GetDirectoryName(outFile));
            File.WriteAllBytes(outFile, bytes);

            result.Written = true;
            result.OutputPath = outFile;
            LogLine("  inventory slots: ring " + effRing + " (vanilla " + VanillaRingSlots
                + "), necklace " + effNeck + " (vanilla " + VanillaNecklaceSlots + ")");
            return result;
        }

        // Necklace MUST be tested before Ring: "DA_BL_Slot_Equipment_Necklace" does
        // not contain the Ring marker, but keep the order defensive and explicit.
        static int? ClassifySlot(string slotParams, int ringSlots, int necklaceSlots)
        {
            if (string.IsNullOrEmpty(slotParams)) return null;
            if (slotParams.IndexOf(NecklaceMarker, StringComparison.OrdinalIgnoreCase) >= 0)
                return necklaceSlots;
            if (slotParams.IndexOf(RingMarker, StringComparison.OrdinalIgnoreCase) >= 0)
                return ringSlots;
            return null;
        }

        static void ValidateSlots(int ringSlots, int necklaceSlots)
        {
            if (ringSlots < MinSlots || ringSlots > MaxSlots)
                throw new ArgumentOutOfRangeException("ringSlots", ringSlots,
                    "Ring slots must be between " + MinSlots + " and " + MaxSlots);
            if (necklaceSlots < MinSlots || necklaceSlots > MaxSlots)
                throw new ArgumentOutOfRangeException("necklaceSlots", necklaceSlots,
                    "Necklace slots must be between " + MinSlots + " and " + MaxSlots);
        }

        void LogLine(string msg)
        {
            if (Log != null) Log(msg);
        }
    }

    public sealed class InventorySlotsPatchResult
    {
        public bool Skipped;
        public bool Written;
        public string OutputPath;
        public bool RingPatched;
        public bool NecklacePatched;
        public int RingSlots;
        public int NecklaceSlots;
    }
}
