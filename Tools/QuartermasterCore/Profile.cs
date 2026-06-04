using System;
using System.Collections.Generic;

namespace Windrose.Quartermaster.Core
{
    public sealed class Profile
    {
        public string Id;
        public string Name;
        public string Description;
        public DateTimeOffset CreatedAt;
        public DateTimeOffset ModifiedAt;
        public ProfileGlobals Globals;
        public Dictionary<string, ItemOverride> Overrides;
        public Dictionary<string, LootTableOverride> LootOverrides;
        public Dictionary<string, NpcSpawnOverride> NpcSpawnOverrides;
        public Dictionary<string, BuyerRecipeOverride> BuyerRecipes;
        public Dictionary<string, BuyerListOverride> BuyerLists;
        public Dictionary<string, SellerRecipeOverride> SellerRecipes;
        public Dictionary<string, SellerListOverride> SellerLists;
        public List<CustomItem> CustomItems;
        public List<CustomBuilding> CustomBuildings;
    }

    public sealed class ProfileGlobals
    {
        public StackSizeGlobal StackSize;
        public LootGlobal Loot;
        public PickupRadiusGlobal PickupRadius;
        public ShipPickupGlobal ShipPickup;
        public FastTravelBellsGlobal FastTravelBells;
        public EquipmentSlotsGlobal EquipmentSlots;
        public ShipSlotsGlobal ShipSlots;
        public BuildingStabilityGlobal BuildingStability;
        public NoSmokeGlobal NoSmoke;
        public MinimapRangeGlobal MinimapRange;
        public NoFogGlobal NoFog;
        public PersistentLootGlobal PersistentLoot;
        public KeepStatusGlobal KeepStatus;
        public LandFastTravelGlobal LandFastTravel;
        public BonfireRadiusGlobal BonfireRadius;
        public BonfireMusicGlobal BonfireMusic;
        public PickaxeRangeGlobal PickaxeRange;
        public CooldownsGlobal Cooldowns;
        public ProductionTimesGlobal ProductionTimes;
        public ShipMusicGlobal ShipMusic;
        public ShipMusicAddGlobal ShipMusicAdd;
        public LightingGlobal Lighting;
        public ShipSpeedGlobal ShipSpeed;
        public NpcSpawnGlobal NpcSpawn;
    }

    public sealed class StackSizeGlobal
    {
        // Multiplier and Absolute are mutually exclusive.
        public int? Multiplier;
        public int? Absolute;
        public int? Cap;
    }

    public sealed class LootGlobal
    {
        public Dictionary<string, double> ByCategory;
    }

    // Global NPC-spawn tuning. Respawn is a MULTIPLIER on each spawner's vanilla
    // RespawnInterval (so relative rarity is preserved); by default it only
    // touches "standard" spawners (vanilla interval <= 120 min) so boss / rare
    // long-timer spawners keep their cadence unless IncludeSpecialTimers is set.
    // Count is a MULTIPLIER on every Amount Min/Max. 1.0 = unchanged.
    public sealed class NpcSpawnGlobal
    {
        public bool? Enabled;
        public double? RespawnMultiplier;
        public double? CountMultiplier;
        public bool? IncludeSpecialTimers;
    }

    // Per-spawner override (keyed by spawner id = pak-relative path under
    // A2_Spawners without the .json). Absolute values; any set field wins over
    // the global. RespawnMinutes applies only to files that have a
    // RespawnInterval. CountMin/CountMax apply to every Amount block in the file.
    public sealed class NpcSpawnOverride
    {
        public int? RespawnMinutes;
        public int? CountMin;
        public int? CountMax;
    }

    public sealed class PickupRadiusGlobal
    {
        public double? Multiplier;
    }

    // "Extended Ship Pickup Radius": MULTIPLIER on every overlap-shape Radius /
    // HalfHeight in the ship interaction-zone DataAssets (floating sea-loot pickup
    // sphere + per-ship-type interaction zones). 1.0 = vanilla. Applied on top of
    // freshly-extracted vanilla values, so no drift across game updates.
    public sealed class ShipPickupGlobal
    {
        public double? Multiplier;
    }

    public sealed class FastTravelBellsGlobal
    {
        public int? BellCap;
        public int? SignalFireCap;
    }

    // Number of Ring / Necklace equipment slots (vanilla 1/1). null = vanilla.
    // Drives both the pak blueprint (InventorySlotsPatcher) and the existing-
    // character save patch (InventorySaveSlotsPatcher) so they stay in sync.
    public sealed class EquipmentSlotsGlobal
    {
        public int? RingSlots;
        public int? NecklaceSlots;
    }

    // Ship cargo + Combat Orders slots (the "Expanded Naval Tactics" mod).
    // CargoMultiplier scales each ship's vanilla cargo (null/1.0 = vanilla);
    // CombatOrderSlots is an absolute count (null/1 = vanilla). Drives both the
    // pak blueprint (ShipSlotsPatcher) and the existing-ship save patch
    // (ShipSaveSlotsPatcher) so new and existing ships stay in sync.
    public sealed class ShipSlotsGlobal
    {
        public double? CargoMultiplier;
        public int? CombatOrderSlots;
    }

    public sealed class BuildingStabilityGlobal
    {
        public bool? Enabled;
    }

    public sealed class NoSmokeGlobal
    {
        public bool? Campfire;
        public bool? Furnace;
        public bool? Kiln;
    }

    public sealed class MinimapRangeGlobal
    {
        public double? Multiplier;
    }

    // Disables fog of war on both the minimap and the fullscreen world map (the
    // "Windrose No Fog of War" mod). One shared toggle: vanilla flips
    // bFogEnabled=True->False in DefaultR5MapSettings.ini, which covers both
    // maps (the minimap material inherits the worldmap's fog source). Rides the
    // same map-settings pak / +MapsConfig tuple as MinimapRange.
    public sealed class NoFogGlobal
    {
        public bool? Enabled;
    }

    public sealed class PersistentLootGlobal
    {
        public bool? Enabled;
    }

    // Keeps food/elixir/comfort buffs through death (the "Keep Status" mod). Vanilla
    // strips every GAS.Effect.Status effect on death; this swaps that broad removal
    // for a curated set of transient sub-prefixes so the buffs survive.
    public sealed class KeepStatusGlobal
    {
        public bool? Enabled;
    }

    // Lets the fast-travel bell be placed inland, not just near the coast (the
    // "Land Fast Travel" mod). Vanilla restricts placement via the R5BuildingItem
    // CoastlineDistanceRange; the override widens that range on both fast-travel-
    // bell DataAssets. Ships prebuilt override assets through the IoStore composite.
    public sealed class LandFastTravelGlobal
    {
        public bool? Enabled;
    }

    public sealed class BonfireRadiusGlobal
    {
        public double? Multiplier;
    }

    public sealed class BonfireMusicGlobal
    {
        public string OriginalFilename;
        public double? Volume;
    }

    public sealed class PickaxeRangeGlobal
    {
        public double? Multiplier;
    }

    public sealed class CooldownsGlobal
    {
        public double? ElixirMultiplier;
        public double? MedicineMultiplier;
        public double? RecallMultiplier;
        public double? ShipRepairKitMultiplier;
        public double? BoarWhistleMultiplier;
        public double? ShipSummonMultiplier;
        public double? RangedReloadMultiplier;
        public double? ShipCannonMultiplier;
        public double? SoulEaterAbilityMultiplier;
        public double? FoodBuffDurationMultiplier;
    }

    public sealed class ProductionTimesGlobal
    {
        public double? CropGrowthMultiplier;
        public double? SmeltingMultiplier;
        public double? KilnMultiplier;
        public double? TanningMultiplier;
        public double? MillingMultiplier;
        public double? BuildingBitsMultiplier;
        public double? DecorationMultiplier;
        public double? ArmorWeaponMultiplier;
        public double? TradeOutpostMultiplier;
        public double? OtherMultiplier;
    }

    public sealed class ShipMusicGlobal
    {
        public Dictionary<string, ShipMusicSlotOverride> Songs;
        public List<string> ExcludedSlots;
    }

    public sealed class ShipMusicSlotOverride
    {
        public string OriginalFilename;
        public double? Volume;
    }

    public sealed class ShipMusicAddGlobal
    {
        public List<ShipMusicAddedTrack> Tracks;
    }

    public sealed class ShipMusicAddedTrack
    {
        public string TrackKey;
        public string Title;
        public string OriginalFilename;
        public double? Volume;
    }

    public sealed class LightingGlobal
    {
        public double? OverallMultiplier;
        public Dictionary<string, double> Overrides;
    }

    // Per-motor-curve speed multipliers. OverallMultiplier scales every ship
    // curve; Overrides (keyed by CRV_*Motor stem) win over the overall for an
    // individual curve. A 1.0 value (overall or override) is vanilla = no-op.
    public sealed class ShipSpeedGlobal
    {
        public double? OverallMultiplier;
        public Dictionary<string, double> Overrides;
    }

    public sealed class ItemOverride
    {
        public int? StackSize;
    }

    public sealed class LootTableOverride
    {
        public Dictionary<int, LootEntryEdit> Entries;
        public List<int> Removed;
        public List<LootEntry> Added;
    }

    public sealed class LootEntryEdit
    {
        public int? Min;
        public int? Max;
        public int? Weight;
        // "None" clears the slot; null leaves it unchanged.
        public string LootItem;
        public string LootTable;
    }

    public sealed class LootEntry
    {
        public int Min;
        public int Max;
        public int Weight;
        public string LootItem;
        public string LootTable;
    }

    public sealed class CustomItem
    {
        public string Id;
        public string TemplateId;
        public string Name;
        public string Description;
        public int? MaxCountInSlot;
        public string Rarity;
        public bool? KeepInInventoryOnDeath;
        public string ItemTexture;
        public string IconPath;
        public string VanityText;
    }

    public sealed class BuyerRecipeOverride
    {
        public string ItemPath;
        public int? ItemCount;
        public string PayItemPath;
        public int? PayCount;
        public string CraftRequirement;
        public bool IsCustom;
    }

    public sealed class SellerRecipeOverride
    {
        public string ItemPath;
        public int? ItemCount;
        public string PayItemPath;
        public int? PayCount;
        public string CraftRequirement;
        public bool IsCustom;
    }

    public sealed class BuyerListOverride
    {
        public List<string> AddedRecipeIds;
        public List<string> RemovedRecipeIds;
        public List<string> RecipeOrder;
    }

    public sealed class CustomBuilding
    {
        public string Id;
        public string TemplateId;
        public string Name;
        public string Description;
        public string CookedFolderPath;
        public string AssetPrefix;
        public string MeshStem;
        public string IconStem;
        public Dictionary<string, CustomBuildingSlot> Slots;
        public List<RecipeCostEntry> RecipeCost;
        public string ComponentPresetId;

        // Back-compat: migrates the legacy "flamePresetId" JSON key into
        // ComponentPresetId on load. Getter returns null so it is never
        // re-serialized.
        [System.Text.Json.Serialization.JsonInclude]
        public string FlamePresetId
        {
            get => null;
            set { if (!string.IsNullOrEmpty(value) && string.IsNullOrEmpty(ComponentPresetId)) ComponentPresetId = value; }
        }

        public double AudioRangeMeters;
        public double AudioVolume;
        public AudioSourceMeta AudioSource;

        public string ResolveAssetPrefix()
        {
            if (!string.IsNullOrWhiteSpace(AssetPrefix)) return AssetPrefix.Trim();
            return DeriveAssetPrefixFromMeshStem(MeshStem);
        }

        public static string DeriveAssetPrefixFromMeshStem(string meshStem)
        {
            if (string.IsNullOrWhiteSpace(meshStem)) return "";
            var s = meshStem.Trim();
            if (s.StartsWith("SM_", StringComparison.OrdinalIgnoreCase))
                s = s.Substring(3);
            var m = System.Text.RegularExpressions.Regex.Match(s, @"^(.*)_\d+$");
            if (m.Success) s = m.Groups[1].Value;
            return s;
        }
    }

    public sealed class AudioSourceMeta
    {
        public string OriginalFilename;
        public float DurationSec;
        public int SampleRate;
        public int Channels;
        public long SizeBytes;
    }

    public sealed class RecipeCostEntry
    {
        public string ItemPath;
        public int Count;
    }

    public sealed class CustomBuildingSlot
    {
        public string VanillaMaterialParentPath;
        public Dictionary<string, float> ScalarParams;
        public Dictionary<string, float[]> VectorParams;
        public Dictionary<string, string> TextureParams;
    }

    public sealed class SellerListOverride
    {
        public List<string> AddedRecipeIds;
        public List<string> RemovedRecipeIds;
        public List<string> RecipeOrder;
    }
}
