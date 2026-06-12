using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace Windrose.Quartermaster.Core
{
    // One human-readable line per ACTIVE feature of a profile - the detail rows under each mod
    // name in qm_modtab_mods.txt, rendered by the in-game Quartermaster settings tab. Inactive
    // nodes (null, 1.0x multipliers, empty collections) produce no line, so the panel never
    // lists no-ops.
    public static class ProfileSummary
    {
        public static List<string> Lines(Profile p)
        {
            var lines = new List<string>();
            if (p == null) return lines;
            var g = p.Globals;

            if (g != null)
            {
                if (g.StackSize != null)
                {
                    string baseTxt = null;
                    if (g.StackSize.Multiplier.HasValue && g.StackSize.Multiplier.Value != 1)
                        baseTxt = "Stack size x" + g.StackSize.Multiplier.Value;
                    else if (g.StackSize.Absolute.HasValue)
                        baseTxt = "Stack size " + g.StackSize.Absolute.Value;
                    if (baseTxt != null)
                    {
                        if (g.StackSize.Cap.HasValue) baseTxt += " (cap " + g.StackSize.Cap.Value + ")";
                        lines.Add(baseTxt);
                    }
                }

                if (g.Loot?.ByCategory != null && g.Loot.ByCategory.Count > 0)
                {
                    var entries = g.Loot.ByCategory.OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase)
                                                   .Select(kv => kv.Key + " " + X(kv.Value)).ToList();
                    lines.Add("Loot chances: " + (entries.Count <= 4
                        ? string.Join(", ", entries)
                        : string.Join(", ", entries.Take(4)) + " +" + (entries.Count - 4) + " more"));
                }

                if (Active(g.PickupRadius?.Multiplier)) lines.Add("Pickup radius " + X(g.PickupRadius.Multiplier.Value));
                if (Active(g.ShipPickup?.Multiplier))   lines.Add("Ship pickup radius " + X(g.ShipPickup.Multiplier.Value));

                if (g.FastTravelBells != null && (g.FastTravelBells.BellCap.HasValue || g.FastTravelBells.SignalFireCap.HasValue))
                {
                    var parts = new List<string>();
                    if (g.FastTravelBells.BellCap.HasValue)       parts.Add(g.FastTravelBells.BellCap.Value + " bells");
                    if (g.FastTravelBells.SignalFireCap.HasValue) parts.Add(g.FastTravelBells.SignalFireCap.Value + " signal fires");
                    lines.Add("Fast travel caps: " + string.Join(", ", parts));
                }

                if (g.EquipmentSlots != null && (g.EquipmentSlots.RingSlots.HasValue || g.EquipmentSlots.NecklaceSlots.HasValue))
                {
                    var parts = new List<string>();
                    if (g.EquipmentSlots.RingSlots.HasValue)     parts.Add(g.EquipmentSlots.RingSlots.Value + " ring");
                    if (g.EquipmentSlots.NecklaceSlots.HasValue) parts.Add(g.EquipmentSlots.NecklaceSlots.Value + " necklace");
                    lines.Add("Equipment slots: " + string.Join(", ", parts));
                }

                if (g.ShipSlots != null)
                {
                    if (Active(g.ShipSlots.CargoMultiplier)) lines.Add("Ship cargo " + X(g.ShipSlots.CargoMultiplier.Value));
                    if (g.ShipSlots.CombatOrderSlots.HasValue && g.ShipSlots.CombatOrderSlots.Value != 1)
                        lines.Add("Combat order slots: " + g.ShipSlots.CombatOrderSlots.Value);
                }

                if (g.BuildingStability?.Enabled == true) lines.Add("Enhanced building stability");

                if (g.NoSmoke != null)
                {
                    var parts = new List<string>();
                    if (g.NoSmoke.Campfire == true) parts.Add("campfire");
                    if (g.NoSmoke.Furnace == true)  parts.Add("furnace");
                    if (g.NoSmoke.Kiln == true)     parts.Add("kiln");
                    if (parts.Count > 0) lines.Add("No smoke: " + string.Join(", ", parts));
                }

                if (Active(g.MinimapRange?.Multiplier)) lines.Add("Minimap range " + X(g.MinimapRange.Multiplier.Value));
                if (g.NoFog?.Enabled == true)           lines.Add("No fog of war");
                if (g.PersistentLoot?.Enabled == true)  lines.Add("Persistent loot");
                if (g.KeepStatus?.Enabled == true)      lines.Add("Keep status effects on death");
                if (g.Shanty?.Enabled == true)          lines.Add("Keep shanties playing");
                if (g.ItemSpawner?.Enabled == true)     lines.Add("Reward spawner");
                if (g.LandFastTravel?.Enabled == true)  lines.Add("Land fast travel");
                if (Active(g.BonfireRadius?.Multiplier)) lines.Add("Bonfire radius " + X(g.BonfireRadius.Multiplier.Value));
                if (!string.IsNullOrEmpty(g.BonfireMusic?.OriginalFilename)) lines.Add("Custom bonfire music");
                if (Active(g.PickaxeRange?.Multiplier)) lines.Add("Pickaxe range " + X(g.PickaxeRange.Multiplier.Value));

                if (g.Cooldowns != null)
                {
                    int n = CountActive(g.Cooldowns.ElixirMultiplier, g.Cooldowns.MedicineMultiplier,
                        g.Cooldowns.RecallMultiplier, g.Cooldowns.ShipRepairKitMultiplier,
                        g.Cooldowns.BoarWhistleMultiplier, g.Cooldowns.ShipSummonMultiplier,
                        g.Cooldowns.RangedReloadMultiplier, g.Cooldowns.ShipCannonMultiplier,
                        g.Cooldowns.ShipCannonRangeMultiplier, g.Cooldowns.ShipCannonDamageMultiplier,
                        g.Cooldowns.SoulEaterAbilityMultiplier, g.Cooldowns.SoulHarvestDamageMultiplier,
                        g.Cooldowns.SoulHarvestRadiusMultiplier, g.Cooldowns.ShipBoardingRangeMultiplier,
                        g.Cooldowns.ShipBoardingAimMultiplier, g.Cooldowns.ShipBoardingAngleMultiplier,
                        g.Cooldowns.ShipBoardingSpeedMultiplier, g.Cooldowns.FoodBuffDurationMultiplier);
                    if (n > 0) lines.Add("Cooldowns and combat: " + Count(n, "value") + " tuned");
                }

                if (g.ProductionTimes != null)
                {
                    int n = CountActive(g.ProductionTimes.CropGrowthMultiplier, g.ProductionTimes.SmeltingMultiplier,
                        g.ProductionTimes.KilnMultiplier, g.ProductionTimes.TanningMultiplier,
                        g.ProductionTimes.MillingMultiplier, g.ProductionTimes.BuildingBitsMultiplier,
                        g.ProductionTimes.DecorationMultiplier, g.ProductionTimes.ArmorWeaponMultiplier,
                        g.ProductionTimes.TradeOutpostMultiplier, g.ProductionTimes.OtherMultiplier);
                    if (n > 0) lines.Add("Production times: " + Count(n, "category", "categories") + " tuned");
                }

                if (g.ShipMusic != null)
                {
                    var parts = new List<string>();
                    if (g.ShipMusic.Songs != null && g.ShipMusic.Songs.Count > 0)
                        parts.Add(Count(g.ShipMusic.Songs.Count, "track") + " replaced");
                    if (g.ShipMusic.ExcludedSlots != null && g.ShipMusic.ExcludedSlots.Count > 0)
                        parts.Add(Count(g.ShipMusic.ExcludedSlots.Count, "track") + " removed");
                    if (parts.Count > 0) lines.Add("Ship music: " + string.Join(", ", parts));
                }
                if (g.ShipMusicAdd?.Tracks != null && g.ShipMusicAdd.Tracks.Count > 0)
                    lines.Add("Ship music: " + Count(g.ShipMusicAdd.Tracks.Count, "track") + " added");

                if (g.Lighting != null)
                {
                    var parts = new List<string>();
                    if (Active(g.Lighting.OverallMultiplier)) parts.Add("overall " + X(g.Lighting.OverallMultiplier.Value));
                    int n = CountActive(g.Lighting.Overrides);
                    if (n > 0) parts.Add(Count(n, "override"));
                    if (parts.Count > 0) lines.Add("Lighting: " + string.Join(", ", parts));
                }

                if (g.ShipSpeed != null)
                {
                    var parts = new List<string>();
                    if (Active(g.ShipSpeed.OverallMultiplier)) parts.Add("overall " + X(g.ShipSpeed.OverallMultiplier.Value));
                    int n = CountActive(g.ShipSpeed.Overrides);
                    if (n > 0) parts.Add(Count(n, "curve override"));
                    if (parts.Count > 0) lines.Add("Ship speed: " + string.Join(", ", parts));
                }

                if (g.XpReward != null)
                {
                    var parts = new List<string>();
                    if (Active(g.XpReward.QuestMultiplier)) parts.Add("quests " + X(g.XpReward.QuestMultiplier.Value));
                    if (Active(g.XpReward.PoiMultiplier))   parts.Add("POI chests " + X(g.XpReward.PoiMultiplier.Value));
                    int n = CountActive(g.XpReward.Overrides);
                    if (n > 0) parts.Add(Count(n, "override"));
                    if (parts.Count > 0) lines.Add("XP rewards: " + string.Join(", ", parts));
                }

                if (g.KillXp != null)
                {
                    var parts = new List<string>();
                    if (g.KillXp.DefaultXp.HasValue && g.KillXp.DefaultXp.Value > 0)
                        parts.Add(g.KillXp.DefaultXp.Value + " base XP");
                    if (g.KillXp.Keywords != null && g.KillXp.Keywords.Count > 0)
                        parts.Add(Count(g.KillXp.Keywords.Count, "enemy rule"));
                    if (parts.Count > 0) lines.Add("XP for kills: " + string.Join(", ", parts));
                }

                if (g.LevelingRework != null)
                {
                    var parts = new List<string>();
                    if (Active(g.LevelingRework.TalentMultiplier)) parts.Add("talent " + X(g.LevelingRework.TalentMultiplier.Value));
                    if (Active(g.LevelingRework.StatMultiplier))   parts.Add("stat " + X(g.LevelingRework.StatMultiplier.Value));
                    if (g.LevelingRework.Overrides != null && g.LevelingRework.Overrides.Count > 0)
                        parts.Add(Count(g.LevelingRework.Overrides.Count, "level") + " pinned");
                    if (parts.Count > 0) lines.Add("Level rewards: " + string.Join(", ", parts));
                }

                if (g.NpcSpawn != null && (g.NpcSpawn.Enabled == true || Active(g.NpcSpawn.RespawnMultiplier) || Active(g.NpcSpawn.CountMultiplier)))
                {
                    var parts = new List<string>();
                    if (Active(g.NpcSpawn.RespawnMultiplier)) parts.Add("respawn " + X(g.NpcSpawn.RespawnMultiplier.Value));
                    if (Active(g.NpcSpawn.CountMultiplier))   parts.Add("count " + X(g.NpcSpawn.CountMultiplier.Value));
                    lines.Add("NPC spawns" + (parts.Count > 0 ? ": " + string.Join(", ", parts) : " tuned"));
                }

                if (g.DepositVisual != null)
                {
                    var parts = new List<string>();
                    if (g.DepositVisual.Iron == true)   parts.Add("iron");
                    if (g.DepositVisual.Sulfur == true) parts.Add("sulfur");
                    if (parts.Count > 0) lines.Add("Deposit visuals: " + string.Join(", ", parts));
                }

                if (Active(g.CropOverlap?.Multiplier)) lines.Add("Crop overlap " + X(g.CropOverlap.Multiplier.Value));

                if (g.PlayerStats != null)
                {
                    var parts = new List<string>();
                    if (Active(g.PlayerStats.HealthMultiplier))  parts.Add("health " + X(g.PlayerStats.HealthMultiplier.Value));
                    if (Active(g.PlayerStats.StaminaMultiplier)) parts.Add("stamina " + X(g.PlayerStats.StaminaMultiplier.Value));
                    if (parts.Count > 0) lines.Add("Player stats: " + string.Join(", ", parts));
                }

                if (g.BuildingRotation != null)
                {
                    var parts = new List<string>();
                    if (g.BuildingRotation.Add1 == true)  parts.Add("1");
                    if (g.BuildingRotation.Add5 == true)  parts.Add("5");
                    if (g.BuildingRotation.Add10 == true) parts.Add("10");
                    if (parts.Count > 0) lines.Add("Building rotation: +" + string.Join("/+", parts) + " deg steps");
                }
            }

            if (p.Overrides != null && p.Overrides.Count > 0)
                lines.Add(Count(p.Overrides.Count, "item stack override"));
            if (p.LootOverrides != null && p.LootOverrides.Count > 0)
                lines.Add(Count(p.LootOverrides.Count, "loot table") + " edited");
            if (p.NpcSpawnOverrides != null && p.NpcSpawnOverrides.Count > 0)
                lines.Add(Count(p.NpcSpawnOverrides.Count, "spawner override"));

            int buyer  = (p.BuyerRecipes?.Count ?? 0)  + (p.BuyerLists?.Count ?? 0);
            int seller = (p.SellerRecipes?.Count ?? 0) + (p.SellerLists?.Count ?? 0);
            if (buyer > 0 || seller > 0)
            {
                var parts = new List<string>();
                if (buyer > 0)  parts.Add(Count(buyer, "buyer edit"));
                if (seller > 0) parts.Add(Count(seller, "seller edit"));
                lines.Add("Trading: " + string.Join(", ", parts));
            }

            if (p.CustomItems != null && p.CustomItems.Count > 0)
            {
                int weather = p.CustomItems.Count(ci => ci != null && ci.WeatherId.HasValue);
                lines.Add(Count(p.CustomItems.Count, "custom item")
                          + (weather > 0 ? " (" + Count(weather, "weather effect") + ")" : ""));
            }
            if (p.CustomBuildings != null && p.CustomBuildings.Count > 0)
                lines.Add(Count(p.CustomBuildings.Count, "custom building"));

            return lines;
        }

        static string X(double v) => "x" + v.ToString("0.##", CultureInfo.InvariantCulture);

        static bool Active(double? v) => v.HasValue && Math.Abs(v.Value - 1.0) > 1e-9;

        static int CountActive(params double?[] values) => values.Count(Active);

        static int CountActive(Dictionary<string, double> overrides)
            => overrides == null ? 0 : overrides.Count(kv => Math.Abs(kv.Value - 1.0) > 1e-9);

        static string Count(int n, string singular, string plural = null)
            => n.ToString(CultureInfo.InvariantCulture) + " " + (n == 1 ? singular : (plural ?? singular + "s"));
    }
}
