using System;
using System.Collections.Generic;
using System.Linq;
using Windrose.Quartermaster.Core;

namespace Windrose.Quartermaster.Web.Endpoints;

public static partial class ProfilesEndpoint
{
    static ProfileGlobals CloneGlobals(ProfileGlobals g)
    {
        if (g == null) return null;
        return new ProfileGlobals
        {
            StackSize = g.StackSize == null
                ? null
                : new StackSizeGlobal
                {
                    Multiplier = g.StackSize.Multiplier,
                    Absolute = g.StackSize.Absolute,
                    Cap = g.StackSize.Cap,
                },
            Loot = g.Loot == null
                ? null
                : new LootGlobal
                {
                    ByCategory = g.Loot.ByCategory == null
                        ? null
                        : new Dictionary<string, double>(g.Loot.ByCategory),
                },
            PickupRadius = g.PickupRadius == null
                ? null
                : new PickupRadiusGlobal
                {
                    Multiplier = g.PickupRadius.Multiplier,
                },
            ShipPickup = g.ShipPickup == null
                ? null
                : new ShipPickupGlobal
                {
                    Multiplier = g.ShipPickup.Multiplier,
                },
            DepositVisual = g.DepositVisual == null
                ? null
                : new DepositVisualGlobal
                {
                    Iron = g.DepositVisual.Iron,
                    IronTexture = g.DepositVisual.IronTexture,
                    Sulfur = g.DepositVisual.Sulfur,
                    SulfurTexture = g.DepositVisual.SulfurTexture,
                },
            CropOverlap = g.CropOverlap == null
                ? null
                : new CropOverlapGlobal
                {
                    Multiplier = g.CropOverlap.Multiplier,
                },
            PlayerStats = g.PlayerStats == null
                ? null
                : new PlayerStatsGlobal
                {
                    HealthMultiplier = g.PlayerStats.HealthMultiplier,
                    StaminaMultiplier = g.PlayerStats.StaminaMultiplier,
                },
            FastTravelBells = g.FastTravelBells == null
                ? null
                : new FastTravelBellsGlobal
                {
                    BellCap = g.FastTravelBells.BellCap,
                    SignalFireCap = g.FastTravelBells.SignalFireCap,
                },
            BuildingStability = g.BuildingStability == null
                ? null
                : new BuildingStabilityGlobal
                {
                    Enabled = g.BuildingStability.Enabled,
                },
            NoSmoke = g.NoSmoke == null
                ? null
                : new NoSmokeGlobal
                {
                    Campfire = g.NoSmoke.Campfire,
                    Furnace = g.NoSmoke.Furnace,
                    Kiln = g.NoSmoke.Kiln,
                },
            MinimapRange = g.MinimapRange == null
                ? null
                : new MinimapRangeGlobal
                {
                    Multiplier = g.MinimapRange.Multiplier,
                },
            NoFog = g.NoFog == null
                ? null
                : new NoFogGlobal
                {
                    Enabled = g.NoFog.Enabled,
                },
            LandFastTravel = g.LandFastTravel == null
                ? null
                : new LandFastTravelGlobal
                {
                    Enabled = g.LandFastTravel.Enabled,
                },
            BonfireRadius = g.BonfireRadius == null
                ? null
                : new BonfireRadiusGlobal
                {
                    Multiplier = g.BonfireRadius.Multiplier,
                },
            BonfireMusic = g.BonfireMusic == null
                ? null
                : new BonfireMusicGlobal
                {
                    OriginalFilename = g.BonfireMusic.OriginalFilename,
                    Volume = g.BonfireMusic.Volume,
                },
            PickaxeRange = g.PickaxeRange == null
                ? null
                : new PickaxeRangeGlobal
                {
                    Multiplier = g.PickaxeRange.Multiplier,
                },
            Cooldowns = g.Cooldowns == null
                ? null
                : new CooldownsGlobal
                {
                    ElixirMultiplier         = g.Cooldowns.ElixirMultiplier,
                    MedicineMultiplier       = g.Cooldowns.MedicineMultiplier,
                    RecallMultiplier         = g.Cooldowns.RecallMultiplier,
                    ShipRepairKitMultiplier  = g.Cooldowns.ShipRepairKitMultiplier,
                    BoarWhistleMultiplier    = g.Cooldowns.BoarWhistleMultiplier,
                    ShipSummonMultiplier     = g.Cooldowns.ShipSummonMultiplier,
                    RangedReloadMultiplier   = g.Cooldowns.RangedReloadMultiplier,
                    ShipCannonMultiplier     = g.Cooldowns.ShipCannonMultiplier,
                    SoulEaterAbilityMultiplier  = g.Cooldowns.SoulEaterAbilityMultiplier,
                    FoodBuffDurationMultiplier  = g.Cooldowns.FoodBuffDurationMultiplier,
                },
            ProductionTimes = g.ProductionTimes == null
                ? null
                : new ProductionTimesGlobal
                {
                    CropGrowthMultiplier    = g.ProductionTimes.CropGrowthMultiplier,
                    SmeltingMultiplier      = g.ProductionTimes.SmeltingMultiplier,
                    KilnMultiplier          = g.ProductionTimes.KilnMultiplier,
                    TanningMultiplier       = g.ProductionTimes.TanningMultiplier,
                    MillingMultiplier       = g.ProductionTimes.MillingMultiplier,
                    BuildingBitsMultiplier  = g.ProductionTimes.BuildingBitsMultiplier,
                    DecorationMultiplier    = g.ProductionTimes.DecorationMultiplier,
                    ArmorWeaponMultiplier   = g.ProductionTimes.ArmorWeaponMultiplier,
                    TradeOutpostMultiplier  = g.ProductionTimes.TradeOutpostMultiplier,
                    OtherMultiplier         = g.ProductionTimes.OtherMultiplier,
                },
            ShipMusic = g.ShipMusic == null
                ? null
                : new ShipMusicGlobal
                {
                    Songs = g.ShipMusic.Songs == null
                        ? null
                        : g.ShipMusic.Songs.ToDictionary(
                            kvp => kvp.Key,
                            kvp => kvp.Value == null
                                ? null
                                : new ShipMusicSlotOverride
                                {
                                    OriginalFilename = kvp.Value.OriginalFilename,
                                }),
                    ExcludedSlots = g.ShipMusic.ExcludedSlots == null
                        ? null
                        : new List<string>(g.ShipMusic.ExcludedSlots),
                },
            ShipMusicAdd = g.ShipMusicAdd == null
                ? null
                : new ShipMusicAddGlobal
                {
                    Tracks = g.ShipMusicAdd.Tracks == null
                        ? null
                        : g.ShipMusicAdd.Tracks
                            .Where(t => t != null)
                            .Select(t => new ShipMusicAddedTrack
                            {
                                TrackKey = t.TrackKey,
                                Title = t.Title,
                                OriginalFilename = t.OriginalFilename,
                            })
                            .ToList(),
                },
        };
    }

    // Deep-clone so editing the clone never mutates the source profile's collections.
    static Dictionary<string, LootTableOverride> CloneLootOverrides(
        Dictionary<string, LootTableOverride> src)
    {
        if (src == null) return null;
        var result = new Dictionary<string, LootTableOverride>(src.Count);
        foreach (var kvp in src)
        {
            var v = kvp.Value;
            if (v == null) { result[kvp.Key] = null; continue; }
            result[kvp.Key] = new LootTableOverride
            {
                Entries = v.Entries == null
                    ? null
                    : v.Entries.ToDictionary(
                        e => e.Key,
                        e => e.Value == null
                            ? null
                            : new LootEntryEdit
                            {
                                Min = e.Value.Min,
                                Max = e.Value.Max,
                                Weight = e.Value.Weight,
                                LootItem = e.Value.LootItem,
                                LootTable = e.Value.LootTable,
                            }),
                Removed = v.Removed == null ? null : new List<int>(v.Removed),
                Added = v.Added == null
                    ? null
                    : v.Added.Select(a => a == null
                        ? null
                        : new LootEntry
                        {
                            Min = a.Min,
                            Max = a.Max,
                            Weight = a.Weight,
                            LootItem = a.LootItem,
                            LootTable = a.LootTable,
                        }).ToList(),
            };
        }
        return result;
    }

    static Dictionary<string, BuyerRecipeOverride> CloneBuyerRecipes(
        Dictionary<string, BuyerRecipeOverride> src)
    {
        if (src == null) return null;
        var result = new Dictionary<string, BuyerRecipeOverride>(src.Count);
        foreach (var kvp in src)
        {
            var v = kvp.Value;
            if (v == null) { result[kvp.Key] = null; continue; }
            result[kvp.Key] = new BuyerRecipeOverride
            {
                ItemPath = v.ItemPath,
                ItemCount = v.ItemCount,
                PayItemPath = v.PayItemPath,
                PayCount = v.PayCount,
                CraftRequirement = v.CraftRequirement,
                IsCustom = v.IsCustom,
            };
        }
        return result;
    }

    static Dictionary<string, SellerRecipeOverride> CloneSellerRecipes(
        Dictionary<string, SellerRecipeOverride> src)
    {
        if (src == null) return null;
        var result = new Dictionary<string, SellerRecipeOverride>(src.Count);
        foreach (var kvp in src)
        {
            var v = kvp.Value;
            if (v == null) { result[kvp.Key] = null; continue; }
            result[kvp.Key] = new SellerRecipeOverride
            {
                ItemPath = v.ItemPath,
                ItemCount = v.ItemCount,
                PayItemPath = v.PayItemPath,
                PayCount = v.PayCount,
                CraftRequirement = v.CraftRequirement,
                IsCustom = v.IsCustom,
            };
        }
        return result;
    }

    static List<CustomItem> CloneCustomItems(List<CustomItem> src)
    {
        if (src == null) return null;
        var result = new List<CustomItem>(src.Count);
        foreach (var c in src)
        {
            if (c == null) { result.Add(null); continue; }
            result.Add(new CustomItem
            {
                Id = c.Id,
                TemplateId = c.TemplateId,
                Name = c.Name,
                Description = c.Description,
                MaxCountInSlot = c.MaxCountInSlot,
                Rarity = c.Rarity,
                KeepInInventoryOnDeath = c.KeepInInventoryOnDeath,
                ItemTexture = c.ItemTexture,
                VanityText = c.VanityText,
                IconPath = c.IconPath,
            });
        }
        return result;
    }

    static List<CustomBuilding> CloneCustomBuildings(List<CustomBuilding> src)
    {
        if (src == null) return null;
        var result = new List<CustomBuilding>(src.Count);
        foreach (var b in src)
        {
            if (b == null) { result.Add(null); continue; }
            var resolvedPrefix = !string.IsNullOrWhiteSpace(b.AssetPrefix)
                ? b.AssetPrefix
                : CustomBuilding.DeriveAssetPrefixFromMeshStem(b.MeshStem);
            result.Add(new CustomBuilding
            {
                Id = b.Id,
                TemplateId = b.TemplateId,
                Name = b.Name,
                Description = b.Description,
                CookedFolderPath = b.CookedFolderPath,
                AssetPrefix = resolvedPrefix,
                MeshStem = b.MeshStem,
                IconStem = b.IconStem,
                Slots = CloneCustomBuildingSlots(b.Slots),
                ComponentPresetId = b.ComponentPresetId,
                AudioRangeMeters = b.AudioRangeMeters,
                AudioVolume      = b.AudioVolume,
                AudioSource = b.AudioSource == null
                    ? null
                    : new AudioSourceMeta
                    {
                        OriginalFilename = b.AudioSource.OriginalFilename,
                        DurationSec      = b.AudioSource.DurationSec,
                        SampleRate       = b.AudioSource.SampleRate,
                        Channels         = b.AudioSource.Channels,
                        SizeBytes        = b.AudioSource.SizeBytes,
                    },
            });
        }
        return result;
    }

    static Dictionary<string, CustomBuildingSlot> CloneCustomBuildingSlots(
        Dictionary<string, CustomBuildingSlot> src)
    {
        if (src == null) return null;
        var result = new Dictionary<string, CustomBuildingSlot>(src.Count, StringComparer.OrdinalIgnoreCase);
        foreach (var kvp in src)
        {
            var v = kvp.Value;
            if (v == null) { result[kvp.Key] = null; continue; }
            result[kvp.Key] = new CustomBuildingSlot
            {
                VanillaMaterialParentPath = v.VanillaMaterialParentPath,
                ScalarParams  = v.ScalarParams  == null ? null : new Dictionary<string, float>(v.ScalarParams, StringComparer.Ordinal),
                VectorParams  = v.VectorParams  == null ? null : CloneVectorParams(v.VectorParams),
                TextureParams = v.TextureParams == null ? null : new Dictionary<string, string>(v.TextureParams, StringComparer.Ordinal),
            };
        }
        return result;
    }

    static Dictionary<string, float[]> CloneVectorParams(Dictionary<string, float[]> src)
    {
        var result = new Dictionary<string, float[]>(src.Count, StringComparer.Ordinal);
        foreach (var kvp in src)
        {
            result[kvp.Key] = kvp.Value == null ? null : (float[])kvp.Value.Clone();
        }
        return result;
    }

    static Dictionary<string, BuyerListOverride> CloneBuyerLists(
        Dictionary<string, BuyerListOverride> src)
    {
        if (src == null) return null;
        var result = new Dictionary<string, BuyerListOverride>(src.Count);
        foreach (var kvp in src)
        {
            var v = kvp.Value;
            if (v == null) { result[kvp.Key] = null; continue; }
            result[kvp.Key] = new BuyerListOverride
            {
                AddedRecipeIds = v.AddedRecipeIds == null ? null : new List<string>(v.AddedRecipeIds),
                RemovedRecipeIds = v.RemovedRecipeIds == null ? null : new List<string>(v.RemovedRecipeIds),
                RecipeOrder = v.RecipeOrder == null ? null : new List<string>(v.RecipeOrder),
            };
        }
        return result;
    }

    static Dictionary<string, SellerListOverride> CloneSellerLists(
        Dictionary<string, SellerListOverride> src)
    {
        if (src == null) return null;
        var result = new Dictionary<string, SellerListOverride>(src.Count);
        foreach (var kvp in src)
        {
            var v = kvp.Value;
            if (v == null) { result[kvp.Key] = null; continue; }
            result[kvp.Key] = new SellerListOverride
            {
                AddedRecipeIds = v.AddedRecipeIds == null ? null : new List<string>(v.AddedRecipeIds),
                RemovedRecipeIds = v.RemovedRecipeIds == null ? null : new List<string>(v.RemovedRecipeIds),
                RecipeOrder = v.RecipeOrder == null ? null : new List<string>(v.RecipeOrder),
            };
        }
        return result;
    }
}
