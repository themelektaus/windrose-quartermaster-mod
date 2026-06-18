using System;
using System.IO;
using System.Text;
using System.Text.Json.Nodes;

namespace Windrose.Quartermaster.Core
{
    // Patches player and chest inventory blueprints to grow slot counts.
    //
    // Player inventory (DA_PlayerInventoryParams.json):
    //   Jewelry module  - Ring / Necklace / Backpack equipment slots (vanilla 1/1/1)
    //   Default module  - base player inventory grid (vanilla 16 slots)
    //
    // Chest inventories (DA_ChestInventoryParams*.json):
    //   Multiplier applied to every variant's CountSlots.
    //
    // IMPORTANT: shipping these paks only changes the inventory *template* used
    // when a NEW character/chest is placed. Existing characters bake their slot
    // layout into the RocksDB save at creation, so they need the separate save
    // patcher (InventorySaveSlotsPatcher) on top of this pak.
    public sealed class InventorySlotsPatcher
    {
        public const int VanillaRingSlots = 1;
        public const int VanillaNecklaceSlots = 1;
        public const int VanillaBackpackSlots = 1;
        public const int VanillaPlayerInventorySlots = 16;
        public const int MinSlots = 1;
        public const int MaxSlots = 10;
        public const int MaxPlayerInventorySlots = 256;
        public const double MinBackpackSlotsMultiplier = 0.25;
        public const double MaxBackpackSlotsMultiplier = 10.0;

        // Vanilla extra slots per backpack tier (DA_Backpack_SlotCountModifierParams).
        public static readonly (string Tier, string FileName, int CountSlots)[] BackpackTiers = new[]
        {
            ("L00", "Backpack_Simple_L00_T01/DA_Backpack_SlotCountModifierParams_L00_T01.json", 4),
            ("L01", "Backpack_Simple_L01_T01/DA_Backpack_SlotCountModifierParams__L01_T01.json", 8),
            ("L02", "Backpack_Simple_L02_T01/DA_Backpack_SlotCountModifierParams__L02_T01.json", 12),
            ("L03", "Backpack_Simple_L03_T01/DA_Backpack_SlotCountModifierParams__L03_T01.json", 16),
            ("L04", "Backpack_Simple_L04_T01/DA_Backpack_SlotCountModifierParams__L04_T01.json", 20),
            ("L10", "Backpack_Simple_L10_T01/DA_Backpack_SlotCountModifierParams__L10_T01.json", 1000),
        };

        const string JewelryTag = "Inventory.Module.Jewelry";
        const string DefaultTag = "Inventory.Module.Default";
        // Matched as case-insensitive substrings against each slot's SlotParams
        // reference path; classified by marker, never by array index.
        const string RingMarker = "DA_BL_Slot_Equipment_Ring";
        const string NecklaceMarker = "DA_BL_Slot_Equipment_Necklace";
        const string BackpackMarker = "DA_BL_Slot_Equipment_Backpack";

        static readonly UTF8Encoding Utf8NoBom = new UTF8Encoding(false);

        public Action<string> Log;

        public InventorySlotsPatchResult PatchToDirectory(
            string vanillaInventoryDir, string outDir,
            int? ringSlots, int? necklaceSlots, int? backpackSlots,
            int? playerInventorySlots, double? chestSlotsMultiplier)
        {
            if (string.IsNullOrEmpty(vanillaInventoryDir))
                throw new ArgumentNullException("vanillaInventoryDir");
            if (string.IsNullOrEmpty(outDir))
                throw new ArgumentNullException("outDir");

            var result = new InventorySlotsPatchResult();
            int effRing = ringSlots ?? VanillaRingSlots;
            int effNeck = necklaceSlots ?? VanillaNecklaceSlots;
            int effBack = backpackSlots ?? VanillaBackpackSlots;
            int effDefault = playerInventorySlots ?? VanillaPlayerInventorySlots;
            double effChest = chestSlotsMultiplier ?? 1.0;
            ValidateSlots(effRing, effNeck, effBack, effDefault);
            result.RingSlots = effRing;
            result.NecklaceSlots = effNeck;
            result.BackpackSlots = effBack;
            result.PlayerInventorySlots = effDefault;
            result.ChestSlotsMultiplier = effChest;

            bool jewelryChanged = effRing != VanillaRingSlots
                                  || effNeck != VanillaNecklaceSlots
                                  || effBack != VanillaBackpackSlots;
            bool defaultChanged = effDefault != VanillaPlayerInventorySlots;
            bool chestChanged = Math.Abs(effChest - 1.0) > 1e-9;

            if (!jewelryChanged && !defaultChanged && !chestChanged)
            {
                result.Skipped = true;
                return result;
            }

            // --- Player inventory (Jewelry + Default modules) ---
            if (jewelryChanged || defaultChanged)
            {
                PatchPlayerInventory(vanillaInventoryDir, outDir, result,
                    effRing, effNeck, effBack, effDefault,
                    jewelryChanged, defaultChanged);
            }

            // --- Chest inventories ---
            if (chestChanged)
                PatchChestInventories(vanillaInventoryDir, outDir, result, effChest);

            if (!result.Written)
            {
                result.Skipped = true;
                return result;
            }

            return result;
        }

        void PatchPlayerInventory(
            string vanillaDir, string outDir, InventorySlotsPatchResult result,
            int effRing, int effNeck, int effBack, int effDefault,
            bool jewelryChanged, bool defaultChanged)
        {
            var vanillaJsonPath = Path.Combine(vanillaDir, "DA_PlayerInventoryParams.json");
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

            bool anyPatched = false;

            foreach (var moduleNode in modules)
            {
                var tag = moduleNode?["ModuleTag"]?["TagName"]?.GetValue<string>();

                // Jewelry module: Ring / Necklace / Backpack
                if (jewelryChanged && string.Equals(tag, JewelryTag, StringComparison.Ordinal))
                {
                    var slots = moduleNode["Slots"]?.AsArray();
                    if (slots == null) continue;
                    foreach (var slotNode in slots)
                    {
                        if (slotNode == null) continue;
                        var sp = slotNode["SlotParams"]?.GetValue<string>() ?? "";
                        int? target = ClassifyJewelrySlot(sp, effRing, effNeck, effBack);
                        if (!target.HasValue) continue;
                        var oldVal = slotNode["CountSlots"]?.GetValue<int>() ?? -1;
                        if (oldVal == target.Value) continue;
                        slotNode["CountSlots"] = target.Value;
                        anyPatched = true;
                        if (sp.IndexOf(RingMarker, StringComparison.OrdinalIgnoreCase) >= 0)
                            result.RingPatched = true;
                        else if (sp.IndexOf(NecklaceMarker, StringComparison.OrdinalIgnoreCase) >= 0)
                            result.NecklacePatched = true;
                        else if (sp.IndexOf(BackpackMarker, StringComparison.OrdinalIgnoreCase) >= 0)
                            result.BackpackPatched = true;
                    }
                }

                // Default module: base player inventory grid
                if (defaultChanged && string.Equals(tag, DefaultTag, StringComparison.Ordinal))
                {
                    var slots = moduleNode["Slots"]?.AsArray();
                    if (slots == null) continue;
                    foreach (var slotNode in slots)
                    {
                        if (slotNode == null) continue;
                        var oldVal = slotNode["CountSlots"]?.GetValue<int>() ?? -1;
                        if (oldVal == effDefault) continue;
                        slotNode["CountSlots"] = effDefault;
                        result.DefaultPatched = true;
                        anyPatched = true;
                    }
                }
            }

            if (!anyPatched) return;

            var bytes = R5Json.SerializeWithTabsAndCrlf(root);
            var outFile = Path.Combine(outDir, "R5", "Plugins", "R5BusinessRules",
                "Content", "Inventory", "DA_PlayerInventoryParams.json");
            Directory.CreateDirectory(Path.GetDirectoryName(outFile));
            File.WriteAllBytes(outFile, bytes);
            result.Written = true;
            result.OutputPath = outFile;

            LogLine("  inventory slots: ring " + effRing + " (vanilla " + VanillaRingSlots
                + "), necklace " + effNeck + " (vanilla " + VanillaNecklaceSlots
                + "), backpack " + effBack + " (vanilla " + VanillaBackpackSlots
                + "), default " + effDefault + " (vanilla " + VanillaPlayerInventorySlots + ")");
        }

        void PatchChestInventories(
            string vanillaDir, string outDir, InventorySlotsPatchResult result,
            double multiplier)
        {
            // All chest inventory JSON files in the vanilla dump.
            string[] chestFiles = {
                "DA_ChestInventoryParams.json",
                "DA_ChestInventoryParams12.json",
                "DA_ChestInventoryParams_Slots40.json",
                "DA_ChestInventoryParams_Slots120.json",
            };
            int patchedCount = 0;
            foreach (var fileName in chestFiles)
            {
                var vanillaPath = Path.Combine(vanillaDir, fileName);
                if (!File.Exists(vanillaPath)) continue;

                var raw = File.ReadAllText(vanillaPath, Encoding.UTF8);
                var root = JsonNode.Parse(raw) as JsonObject;
                if (root == null) continue;

                var modules = root["InventoryModules"]?.AsArray();
                if (modules == null) continue;

                bool patched = false;
                foreach (var moduleNode in modules)
                {
                    var slots = moduleNode?["Slots"]?.AsArray();
                    if (slots == null) continue;
                    foreach (var slotNode in slots)
                    {
                        if (slotNode == null) continue;
                        int vanilla = slotNode["CountSlots"]?.GetValue<int>() ?? 0;
                        int target = Math.Max(1, (int)Math.Round(vanilla * multiplier));
                        if (vanilla == target) continue;
                        slotNode["CountSlots"] = target;
                        patched = true;
                    }
                }
                if (!patched) continue;

                var bytes = R5Json.SerializeWithTabsAndCrlf(root);
                var outFile = Path.Combine(outDir, "R5", "Plugins", "R5BusinessRules",
                    "Content", "Inventory", fileName);
                Directory.CreateDirectory(Path.GetDirectoryName(outFile));
                File.WriteAllBytes(outFile, bytes);
                patchedCount++;
            }
            if (patchedCount > 0)
            {
                result.ChestPatched = true;
                result.Written = true;
                LogLine("  chest slots: " + patchedCount + " chest inventories scaled by "
                    + multiplier.ToString("0.0") + "x");
            }
        }

        // Patches the 6 per-tier backpack SlotCountModifierParams JSONs to scale
        // the slots each backpack tier adds to the Default inventory module.
        public InventorySlotsPatchResult PatchBackpackSlotsParams(
            string vanillaBackpackDir, string outDir, double multiplier)
        {
            if (string.IsNullOrEmpty(vanillaBackpackDir))
                throw new ArgumentNullException("vanillaBackpackDir");
            if (string.IsNullOrEmpty(outDir))
                throw new ArgumentNullException("outDir");

            var result = new InventorySlotsPatchResult();
            result.BackpackSlotsMultiplier = multiplier;
            if (Math.Abs(multiplier - 1.0) < 1e-9)
            {
                result.Skipped = true;
                return result;
            }

            int patchedCount = 0;
            foreach (var (tier, fileName, vanillaSlots) in BackpackTiers)
            {
                var vanillaPath = Path.Combine(vanillaBackpackDir, fileName);
                if (!File.Exists(vanillaPath))
                {
                    LogLine("  backpack " + tier + ": vanilla JSON not found, skipping");
                    continue;
                }
                var raw = File.ReadAllText(vanillaPath, Encoding.UTF8);
                var root = JsonNode.Parse(raw) as JsonObject;
                if (root == null) continue;

                var invData = root["InventorySlotsData"] as JsonObject;
                if (invData == null) continue;
                int oldVal = invData["CountSlots"]?.GetValue<int>() ?? 0;
                int newVal = Math.Max(1, (int)Math.Ceiling(vanillaSlots * multiplier));
                if (oldVal == newVal) continue;

                invData["CountSlots"] = newVal;
                var bytes = R5Json.SerializeWithTabsAndCrlf(root);
                var outFile = Path.Combine(outDir, "R5", "Content",
                    "Gameplay", "ItemsLogic", "Backpack", fileName);
                Directory.CreateDirectory(Path.GetDirectoryName(outFile));
                File.WriteAllBytes(outFile, bytes);
                patchedCount++;
                LogLine("  backpack " + tier + ": " + vanillaSlots + " -> " + newVal
                    + " slots (" + multiplier.ToString("0.0#") + "x)");
            }
            if (patchedCount > 0)
            {
                result.BackpackSlotsPatched = true;
                result.Written = true;
            }
            else
            {
                result.Skipped = true;
            }
            return result;
        }

        // Necklace MUST be tested before Ring: "DA_BL_Slot_Equipment_Necklace" does
        // not contain the Ring marker, but keep the order defensive and explicit.
        static int? ClassifyJewelrySlot(string slotParams, int ringSlots, int necklaceSlots, int backpackSlots)
        {
            if (string.IsNullOrEmpty(slotParams)) return null;
            if (slotParams.IndexOf(NecklaceMarker, StringComparison.OrdinalIgnoreCase) >= 0)
                return necklaceSlots;
            if (slotParams.IndexOf(BackpackMarker, StringComparison.OrdinalIgnoreCase) >= 0)
                return backpackSlots;
            if (slotParams.IndexOf(RingMarker, StringComparison.OrdinalIgnoreCase) >= 0)
                return ringSlots;
            return null;
        }

        static void ValidateSlots(int ring, int neck, int back, int defaultSlots)
        {
            if (ring < MinSlots || ring > MaxSlots)
                throw new ArgumentOutOfRangeException("ringSlots", ring,
                    "Ring slots must be between " + MinSlots + " and " + MaxSlots);
            if (neck < MinSlots || neck > MaxSlots)
                throw new ArgumentOutOfRangeException("necklaceSlots", neck,
                    "Necklace slots must be between " + MinSlots + " and " + MaxSlots);
            if (back < MinSlots || back > MaxSlots)
                throw new ArgumentOutOfRangeException("backpackSlots", back,
                    "Backpack slots must be between " + MinSlots + " and " + MaxSlots);
            if (defaultSlots < VanillaPlayerInventorySlots || defaultSlots > MaxPlayerInventorySlots)
                throw new ArgumentOutOfRangeException("playerInventorySlots", defaultSlots,
                    "Player inventory slots must be between " + VanillaPlayerInventorySlots
                    + " and " + MaxPlayerInventorySlots);
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
        public bool BackpackPatched;
        public bool DefaultPatched;
        public bool ChestPatched;
        public bool BackpackSlotsPatched;
        public int RingSlots;
        public int NecklaceSlots;
        public int BackpackSlots;
        public int PlayerInventorySlots;
        public double ChestSlotsMultiplier;
        public double BackpackSlotsMultiplier;
    }
}
