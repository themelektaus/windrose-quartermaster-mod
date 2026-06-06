using System;
using System.Collections.Generic;
using System.Linq;
using Windrose.Quartermaster.Core;

namespace Windrose.Quartermaster.Web.Endpoints;

public static partial class ProfilesEndpoint
{
    static object ToSummary(Profile p)
    {
        return new
        {
            id = p.Id,
            name = p.Name,
            description = p.Description,
            createdAt = p.CreatedAt,
            modifiedAt = p.ModifiedAt,
            overrideCount = p.Overrides == null ? 0 : p.Overrides.Count,
            lootOverrideCount = p.LootOverrides == null ? 0 : p.LootOverrides.Count,
            buyerRecipeCount = p.BuyerRecipes == null ? 0 : p.BuyerRecipes.Count,
            buyerListCount = p.BuyerLists == null ? 0 : p.BuyerLists.Count,
            sellerRecipeCount = p.SellerRecipes == null ? 0 : p.SellerRecipes.Count,
            sellerListCount = p.SellerLists == null ? 0 : p.SellerLists.Count,
            customItemCount = p.CustomItems == null ? 0 : p.CustomItems.Count,
            customBuildingCount = p.CustomBuildings == null ? 0 : p.CustomBuildings.Count,
            hasGlobalStackSize = p.Globals != null && p.Globals.StackSize != null
                                 && (p.Globals.StackSize.Multiplier.HasValue
                                     || p.Globals.StackSize.Absolute.HasValue),
            hasGlobalLoot = p.Globals != null && p.Globals.Loot != null
                            && p.Globals.Loot.ByCategory != null
                            && p.Globals.Loot.ByCategory.Count > 0,
            hasGlobalPickupRadius = p.Globals != null && p.Globals.PickupRadius != null
                                    && p.Globals.PickupRadius.Multiplier.HasValue
                                    && Math.Abs(p.Globals.PickupRadius.Multiplier.Value - 1.0) > 1e-9,
            hasGlobalShipPickup = p.Globals != null && p.Globals.ShipPickup != null
                                    && p.Globals.ShipPickup.Multiplier.HasValue
                                    && Math.Abs(p.Globals.ShipPickup.Multiplier.Value - 1.0) > 1e-9,
            hasGlobalDepositVisual = p.Globals != null && p.Globals.DepositVisual != null
                                    && (p.Globals.DepositVisual.Iron.GetValueOrDefault(false)
                                        || p.Globals.DepositVisual.Sulfur.GetValueOrDefault(false)),
            hasGlobalCropOverlap = p.Globals != null && p.Globals.CropOverlap != null
                                    && p.Globals.CropOverlap.Multiplier.HasValue
                                    && Math.Abs(p.Globals.CropOverlap.Multiplier.Value - 1.0) > 1e-9,
            hasGlobalPlayerStats = p.Globals != null && p.Globals.PlayerStats != null
                                    && ((p.Globals.PlayerStats.HealthMultiplier.HasValue
                                         && Math.Abs(p.Globals.PlayerStats.HealthMultiplier.Value - 1.0) > 1e-9)
                                        || (p.Globals.PlayerStats.StaminaMultiplier.HasValue
                                            && Math.Abs(p.Globals.PlayerStats.StaminaMultiplier.Value - 1.0) > 1e-9)),
            hasGlobalFastTravelBells = HasFastTravelBellsConfig(p),
            hasGlobalBuildingStability = p.Globals != null
                                         && p.Globals.BuildingStability != null
                                         && p.Globals.BuildingStability.Enabled.GetValueOrDefault(false),
            hasGlobalNoSmoke = HasAnyNoSmokeCategory(p),
            hasGlobalMinimapRange = p.Globals != null
                                    && p.Globals.MinimapRange != null
                                    && p.Globals.MinimapRange.Multiplier.HasValue
                                    && Math.Abs(p.Globals.MinimapRange.Multiplier.Value - 1.0) > 1e-9,
            hasGlobalNoFog = p.Globals != null
                             && p.Globals.NoFog != null
                             && p.Globals.NoFog.Enabled.GetValueOrDefault(false),
            hasGlobalLandFastTravel = p.Globals != null
                             && p.Globals.LandFastTravel != null
                             && p.Globals.LandFastTravel.Enabled.GetValueOrDefault(false),
            hasGlobalBonfireRadius = p.Globals != null
                                     && p.Globals.BonfireRadius != null
                                     && p.Globals.BonfireRadius.Multiplier.HasValue
                                     && Math.Abs(p.Globals.BonfireRadius.Multiplier.Value - 1.0) > 1e-9,
            hasGlobalPickaxeRange = p.Globals != null
                                    && p.Globals.PickaxeRange != null
                                    && p.Globals.PickaxeRange.Multiplier.HasValue
                                    && Math.Abs(p.Globals.PickaxeRange.Multiplier.Value - 1.0) > 1e-9,
            hasGlobalCooldowns = p.Globals != null
                                 && p.Globals.Cooldowns != null
                                 && AnyCooldownActive(p.Globals.Cooldowns),
            hasGlobalProductionTimes = p.Globals != null
                                       && p.Globals.ProductionTimes != null
                                       && AnyProductionTimeActive(p.Globals.ProductionTimes),
            hasGlobalShipMusic = p.Globals != null
                                 && p.Globals.ShipMusic != null
                                 && p.Globals.ShipMusic.Songs != null
                                 && p.Globals.ShipMusic.Songs.Count > 0,
        };
    }

    static bool AnyCooldownActive(CooldownsGlobal cd)
    {
        return IsActive(cd.ElixirMultiplier)
            || IsActive(cd.MedicineMultiplier)
            || IsActive(cd.RecallMultiplier)
            || IsActive(cd.ShipRepairKitMultiplier)
            || IsActive(cd.BoarWhistleMultiplier)
            || IsActive(cd.ShipSummonMultiplier)
            || IsActive(cd.RangedReloadMultiplier)
            || IsActive(cd.ShipCannonMultiplier)
            || IsActive(cd.ShipCannonRangeMultiplier)
            || IsActive(cd.SoulEaterAbilityMultiplier)
            || IsActive(cd.FoodBuffDurationMultiplier);
    }

    static bool AnyProductionTimeActive(ProductionTimesGlobal pt)
    {
        return IsActive(pt.CropGrowthMultiplier)
            || IsActive(pt.SmeltingMultiplier)
            || IsActive(pt.KilnMultiplier)
            || IsActive(pt.TanningMultiplier)
            || IsActive(pt.MillingMultiplier)
            || IsActive(pt.BuildingBitsMultiplier)
            || IsActive(pt.DecorationMultiplier)
            || IsActive(pt.ArmorWeaponMultiplier)
            || IsActive(pt.TradeOutpostMultiplier)
            || IsActive(pt.OtherMultiplier);
    }

    static bool IsActive(double? m)
    {
        return m.HasValue && Math.Abs(m.Value - 1.0) > 1e-9;
    }

    static bool HasAnyNoSmokeCategory(Profile p)
    {
        var n = p.Globals != null ? p.Globals.NoSmoke : null;
        if (n == null) return false;
        return n.Campfire.GetValueOrDefault(false)
            || n.Furnace.GetValueOrDefault(false)
            || n.Kiln.GetValueOrDefault(false);
    }

    // The id becomes a filename, so reject path-traversal and Win32-reserved names.
    static bool IsSafeProfileId(string id)
    {
        if (string.IsNullOrWhiteSpace(id)) return false;
        if (id.Length > 128) return false;
        foreach (var ch in id)
        {
            if (!(char.IsLetterOrDigit(ch) || ch == '-' || ch == '_'))
                return false;
        }
        switch (id.ToUpperInvariant())
        {
            case "CON":  case "PRN":  case "AUX":  case "NUL":
            case "COM1": case "COM2": case "COM3": case "COM4":
            case "COM5": case "COM6": case "COM7": case "COM8": case "COM9":
            case "LPT1": case "LPT2": case "LPT3": case "LPT4":
            case "LPT5": case "LPT6": case "LPT7": case "LPT8": case "LPT9":
                return false;
        }
        return true;
    }

    static bool HasFastTravelBellsConfig(Profile p)
    {
        var b = p.Globals != null ? p.Globals.FastTravelBells : null;
        if (b == null) return false;
        if (b.BellCap.HasValue && b.BellCap.Value != BellLimitsPatcher.VanillaBellCap)
            return true;
        if (b.SignalFireCap.HasValue && b.SignalFireCap.Value != BellLimitsPatcher.VanillaSignalFireCap)
            return true;
        return false;
    }
}
