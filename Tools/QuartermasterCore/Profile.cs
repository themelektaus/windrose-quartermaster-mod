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
        public FastTravelBellsGlobal FastTravelBells;
        public BuildingStabilityGlobal BuildingStability;
        public NoSmokeGlobal NoSmoke;
        public MinimapRangeGlobal MinimapRange;
        public BonfireRadiusGlobal BonfireRadius;
        public BonfireMusicGlobal BonfireMusic;
        public PickaxeRangeGlobal PickaxeRange;
        public CooldownsGlobal Cooldowns;
        public ProductionTimesGlobal ProductionTimes;
        public ShipMusicGlobal ShipMusic;
        public ShipMusicAddGlobal ShipMusicAdd;
        public LightingGlobal Lighting;
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

    public sealed class PickupRadiusGlobal
    {
        public double? Multiplier;
    }

    public sealed class FastTravelBellsGlobal
    {
        public int? BellCap;
        public int? SignalFireCap;
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
