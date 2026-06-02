using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json.Nodes;

namespace Windrose.Quartermaster.Core
{
    // Patches the per-ship inventory blueprints (DA_ShipInventory_*.json) to grow
    // ship cargo and the Combat Orders equipment slot - the "Expanded Naval
    // Tactics" mod, but slider-driven. Two independent knobs:
    //   * cargo   = Inventory.Module.Default -> DA_BL_Slot_Chest CountSlots, set
    //               to round(vanillaBase * multiplier). Vanilla differs per ship
    //               (and per variant), so a multiplier is the natural control;
    //               the reference mod simply doubles (x2).
    //   * combat  = Inventory.Module.Equipment ->
    //               DA_BL_Slot_ShipEquipment_CombatOrders CountSlots, an absolute
    //               count (vanilla 1, the mod sets 5).
    //
    // Scope matches the reference mod exactly: Brig / Frigate / Ketch and their
    // _Stock / _Blackbeard / _Brethren variants (12 files). Cutter, Merchant,
    // Boat and the Default template are deliberately left at vanilla.
    //
    // IMPORTANT: this pak only changes the template used when a NEW ship is
    // created. Existing ships bake their slot layout into the RocksDB save, so
    // they need the separate save patcher (ShipSaveSlotsPatcher) on top of this.
    public sealed class ShipSlotsPatcher
    {
        public const int VanillaCombatOrders = 1;
        public const double VanillaCargoMultiplier = 1.0;
        public const double MinCargoMultiplier = 1.0;
        public const double MaxCargoMultiplier = 10.0;
        public const int MinCombatOrders = 1;
        public const int MaxCombatOrders = 10;
        // Hard ceiling on the resulting cargo cell count so an extreme multiplier
        // can never balloon a ship's live slot array to an absurd size.
        public const int MaxCargoCells = 200;

        const string DefaultModuleTag = "Inventory.Module.Default";
        const string EquipmentModuleTag = "Inventory.Module.Equipment";
        const string ChestMarker = "DA_BL_Slot_Chest";
        const string CombatOrdersMarker = "DA_BL_Slot_ShipEquipment_CombatOrders";

        // Filename-prefix allowlist (without the .json). Matches the reference mod.
        static readonly string[] ShipFamilies =
        {
            "DA_ShipInventory_Brig",
            "DA_ShipInventory_Frigate",
            "DA_ShipInventory_Ketch",
        };

        public Action<string> Log;
        void LogLine(string m) { if (Log != null) Log(m); }

        public static bool IsTargetShipFile(string fileNameNoExt)
        {
            if (string.IsNullOrEmpty(fileNameNoExt)) return false;
            foreach (var fam in ShipFamilies)
                if (fileNameNoExt.StartsWith(fam, StringComparison.Ordinal))
                    return true;
            return false;
        }

        // round(base * multiplier), away from zero, clamped to a sane ceiling.
        public static int CargoTarget(int vanillaBase, double multiplier)
        {
            if (vanillaBase <= 0) return vanillaBase;
            int t = (int)Math.Round(vanillaBase * multiplier, MidpointRounding.AwayFromZero);
            if (t < vanillaBase) t = vanillaBase; // never shrink below vanilla
            if (t > MaxCargoCells) t = MaxCargoCells;
            return t;
        }

        public ShipSlotsPatchResult PatchToDirectory(
            string vanillaShipDir, string outDir,
            double? cargoMultiplier, int? combatOrderSlots)
        {
            if (string.IsNullOrEmpty(vanillaShipDir))
                throw new ArgumentNullException(nameof(vanillaShipDir));
            if (string.IsNullOrEmpty(outDir))
                throw new ArgumentNullException(nameof(outDir));

            var result = new ShipSlotsPatchResult();
            double effMult = cargoMultiplier ?? VanillaCargoMultiplier;
            int effCombat = combatOrderSlots ?? VanillaCombatOrders;
            ValidateInputs(effMult, effCombat);
            result.CargoMultiplier = effMult;
            result.CombatOrderSlots = effCombat;

            bool cargoActive = Math.Abs(effMult - VanillaCargoMultiplier) > 1e-9;
            bool combatActive = effCombat != VanillaCombatOrders;
            if (!cargoActive && !combatActive)
            {
                result.Skipped = true;
                return result;
            }

            if (!Directory.Exists(vanillaShipDir))
                throw new DirectoryNotFoundException(
                    "Vanilla ship inventory dir not found: " + vanillaShipDir
                    + " - run Setup to dump the Inventory/ tree.");

            foreach (var path in Directory.EnumerateFiles(vanillaShipDir, "*.json"))
            {
                var nameNoExt = Path.GetFileNameWithoutExtension(path);
                if (!IsTargetShipFile(nameNoExt)) continue;

                var raw = File.ReadAllText(path, Encoding.UTF8);
                var root = JsonNode.Parse(raw) as JsonObject;
                if (root == null)
                    throw new InvalidDataException("Failed to parse " + path + " as a JSON object");

                bool changed = PatchOneShip(root, effMult, effCombat, cargoActive, combatActive, nameNoExt, result);
                if (!changed) continue;

                var bytes = R5Json.SerializeWithTabsAndCrlf(root);
                var outFile = Path.Combine(outDir, "R5", "Plugins", "R5BusinessRules",
                    "Content", "Inventory", "Ship", nameNoExt + ".json");
                Directory.CreateDirectory(Path.GetDirectoryName(outFile));
                File.WriteAllBytes(outFile, bytes);
                result.FilesWritten++;
            }

            if (result.FilesWritten == 0)
            {
                // Nothing actually changed (e.g. multiplier rounded to vanilla on
                // every ship and combat orders already 1): skip to avoid shipping
                // byte-identical copies while claiming a patch.
                result.Skipped = true;
                return result;
            }

            result.Written = true;
            LogLine("  ship slots: cargo x" + effMult.ToString("0.###", CultureInfo.InvariantCulture)
                + " (vanilla x1), combat orders " + effCombat + " (vanilla " + VanillaCombatOrders + ") - "
                + result.FilesWritten + " ship file(s)");
            return result;
        }

        bool PatchOneShip(JsonObject root, double mult, int combat,
            bool cargoActive, bool combatActive, string shipName, ShipSlotsPatchResult result)
        {
            var modules = root["InventoryModules"]?.AsArray();
            if (modules == null)
                throw new InvalidDataException(shipName + ": missing InventoryModules array.");

            bool changed = false;
            foreach (var moduleNode in modules)
            {
                var tag = moduleNode?["ModuleTag"]?["TagName"]?.GetValue<string>();
                var slots = moduleNode?["Slots"]?.AsArray();
                if (slots == null) continue;

                if (cargoActive && string.Equals(tag, DefaultModuleTag, StringComparison.Ordinal))
                {
                    foreach (var slot in slots)
                    {
                        if (slot == null) continue;
                        var sp = slot["SlotParams"]?.GetValue<string>() ?? "";
                        if (sp.IndexOf(ChestMarker, StringComparison.OrdinalIgnoreCase) < 0) continue;
                        var cs = slot["CountSlots"];
                        if (cs == null) continue;
                        int baseVal = cs.GetValue<int>();
                        int target = CargoTarget(baseVal, mult);
                        if (target != baseVal)
                        {
                            slot["CountSlots"] = target;
                            changed = true;
                            result.CargoSlotsPatched++;
                        }
                    }
                }

                if (combatActive && string.Equals(tag, EquipmentModuleTag, StringComparison.Ordinal))
                {
                    foreach (var slot in slots)
                    {
                        if (slot == null) continue;
                        var sp = slot["SlotParams"]?.GetValue<string>() ?? "";
                        if (sp.IndexOf(CombatOrdersMarker, StringComparison.OrdinalIgnoreCase) < 0) continue;
                        var cs = slot["CountSlots"];
                        if (cs == null) continue;
                        if (cs.GetValue<int>() != combat)
                        {
                            slot["CountSlots"] = combat;
                            changed = true;
                            result.CombatSlotsPatched++;
                        }
                    }
                }
            }
            return changed;
        }

        static void ValidateInputs(double mult, int combat)
        {
            if (mult < MinCargoMultiplier - 1e-9 || mult > MaxCargoMultiplier + 1e-9)
                throw new ArgumentOutOfRangeException(nameof(mult), mult,
                    "Cargo multiplier must be between " + MinCargoMultiplier + " and " + MaxCargoMultiplier);
            if (combat < MinCombatOrders || combat > MaxCombatOrders)
                throw new ArgumentOutOfRangeException(nameof(combat), combat,
                    "Combat order slots must be between " + MinCombatOrders + " and " + MaxCombatOrders);
        }
    }

    public sealed class ShipSlotsPatchResult
    {
        public bool Skipped;
        public bool Written;
        public int FilesWritten;
        public int CargoSlotsPatched;
        public int CombatSlotsPatched;
        public double CargoMultiplier;
        public int CombatOrderSlots;
    }
}
