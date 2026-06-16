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
        public ShantyGlobal Shanty;
        public ItemSpawnerGlobal ItemSpawner;
        public LightingGlobal Lighting;
        public ShipSpeedGlobal ShipSpeed;
        public XpRewardGlobal XpReward;
        public KillXpGlobal KillXp;
        public LevelingReworkGlobal LevelingRework;
        public NpcSpawnGlobal NpcSpawn;
        public DepositVisualGlobal DepositVisual;
        public CropOverlapGlobal CropOverlap;
        public PlayerStatsGlobal PlayerStats;
        public BuildingRotationGlobal BuildingRotation;
    }

    // "Building Degrees of Freedom": adds finer rotation steps (1/5/10 deg) to the
    // building-mode rotation cycle. R5BuildingSettings.RotationSteps is config-backed
    // (DefaultR5BuildingSettings.ini), so the override ships as a loose INI in the
    // main pak - no DLL / UE4SS needed. Vanilla steps (15/30/45/90) and the vanilla
    // DefaultRotationStep (30) are preserved; the enabled fine steps are merged in.
    // Each flag is purely additive; null/false = that step is not added.
    public sealed class BuildingRotationGlobal
    {
        public bool? Add1;
        public bool? Add5;
        public bool? Add10;
    }

    // "Better Crop Overlap": MULTIPLIER on every plantable crop's CapsuleRadius
    // (the horizontal planting-collision footprint in the BP_Farming actors).
    // 1.0 = vanilla; lower lets crops be planted closer together / overlapping.
    // Applied on top of freshly-extracted vanilla values, so no drift across
    // game updates.
    public sealed class CropOverlapGlobal
    {
        public double? Multiplier;
    }

    // "More Stamina (+ Health)": independent MULTIPLIERS on the player's base Health
    // and Stamina attributes in CT_CharactersAttributes. 1.0 = vanilla. Health scales
    // Hero_Health/Hero_MaxHealth; Stamina scales Hero_Stamina/Hero_MaxStamina/
    // Hero_StaminaRegRate. Applied on top of freshly-extracted vanilla values, so no
    // drift across game updates.
    public sealed class PlayerStatsGlobal
    {
        public double? HealthMultiplier;
        public double? StaminaMultiplier;
    }

    // "Deposit visuals" (the Iron / Sulfur "Visual Tweak" reference mods): per
    // deposit, re-point the deposit material's "Albedo" texture at a stock game
    // texture so the node reads more clearly. The *Texture fields hold a
    // DepositVisualCatalog texture key; null = the reference default for that
    // deposit. A deposit only ships if enabled AND its chosen texture differs from
    // the untouched game look.
    public sealed class DepositVisualGlobal
    {
        public bool? Iron;
        public string IronTexture;
        public bool? Sulfur;
        public string SulfurTexture;
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
        public double? TreeMultiplier;
        public double? DigVolumeMultiplier;
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

    // "Keep Shanties Playing": keeps the crew sea shanty playing after you leave the
    // ship's helm. Vanilla stops the shanty when you step away from the wheel; at the
    // helm, B still starts/stops it as usual. DLL-only (the dxgi proxy suppresses the
    // helm-leave stop) - ships no pak content. enabled=true deploys the
    // qm_shanty_<profile>.txt sentinel next to the DLL; absent/false = vanilla.
    public sealed class ShantyGlobal
    {
        public bool? Enabled;
    }

    // "Reward Spawner": the in-game mod tab's grant section (category/item dropdowns +
    // Add Item button + XP field). DLL-only (no pak content) - enabled=true makes
    // the deployer write the qm_modtab_layout_itemspawner.json user-layout extension
    // into Win64/Quartermaster, which the DLL splices into its base panel layout.
    // The JSON key keeps its historic "itemSpawner" name for profile compat.
    // The file is shared across profiles: present while ANY installed profile enables
    // it, removed with the last one. Absent/false = no spawner section in the tab.
    public sealed class ItemSpawnerGlobal
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
        public double? ShipCannonRangeMultiplier;
        public double? ShipCannonDamageMultiplier;
        public double? SoulEaterAbilityMultiplier;
        public double? SoulHarvestDamageMultiplier;
        public double? SoulHarvestRadiusMultiplier;
        public double? ShipBoardingRangeMultiplier;
        public double? ShipBoardingAimMultiplier;
        public double? ShipBoardingAngleMultiplier;
        public double? ShipBoardingSpeedMultiplier;
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

    // "XP Reward Multiplier" (the POI / Quest "x2" reference mods): MULTIPLIER on
    // the ExperienceCount of every R5BLQuestParams reward DataAsset. QuestMultiplier
    // scales the quest rewards (FactionQuests / LocalEventQuests / MainQuest /
    // SideQuest); PoiMultiplier scales the POI-chest rewards (POIChest/*). Overrides
    // (keyed by the asset stem, e.g. "DA_QP_MainQuest_ForgottenRelic_Core") win over
    // the matching overall for an individual entry. A 1.0 value (overall or override)
    // is vanilla = no-op. Applied on top of freshly-extracted vanilla values, so no
    // drift across game updates.
    public sealed class XpRewardGlobal
    {
        public double? QuestMultiplier;
        public double? PoiMultiplier;
        public Dictionary<string, double> Overrides;
    }

    // "XP for Kills": grants flat XP on every enemy kill via the dxgi DLL
    // (qm_killxp) - NOT a pak feature, so this only drives a per-profile sidecar
    // (qm_killxp_onkill_<profile>.txt) next to dxgi.dll, mirroring Weather Control.
    // DefaultXp is the flat XP for any enemy not matched by a keyword (0/absent =
    // vanilla, no grant). Keywords maps a case-insensitive substring of the killed
    // pawn's runtime UClass name to a flat XP amount; the DLL applies the LONGEST
    // matching keyword (so "Mob_Boar" covers every Boar variant, a longer
    // "Mob_Boar_Mega" overrides it). A keyword absent from the map follows DefaultXp.
    // No-op (vanilla) iff DefaultXp is 0/absent AND Keywords is empty.
    public sealed class KillXpGlobal
    {
        public int? DefaultXp;
        public Dictionary<string, int> Keywords;
    }

    // "Level Rewards": HYBRID multipliers on the per-level reward table
    // (DA_HeroLevels, R5BLEntityProgressionLevelParams). TalentMultiplier scales
    // TalentPointsReward, StatMultiplier scales StatPointsReward, each level
    // independently. Because vanilla talent rewards dip to 0 on some level-ups, a
    // boost (>1x) floors every leveled-up entry to "at least 1 point" before
    // scaling (so the dead levels still grant points); a reduction (<1x) does not.
    // The Exp curve and the level-1 starting entry are never touched. 1.0 = vanilla
    // (that dimension is left untouched). Overrides pin an exact reward for an
    // individual level (winning over the multiplier); a node with both multipliers
    // 1.0 and no overrides = no-op. See LevelingPatcher.
    public sealed class LevelingReworkGlobal
    {
        public double? TalentMultiplier;
        public double? StatMultiplier;

        // Per-level ABSOLUTE overrides keyed by 1-based level number. A set value
        // pins that level/dimension to an exact reward, winning over the hybrid
        // multiplier; a null field on the override follows the multiplier. The
        // level-1 / Exp==0 starting entry is never modified, even if present here.
        public Dictionary<int, LevelRewardOverride> Overrides;
    }

    public sealed class LevelRewardOverride
    {
        public int? Talent;
        public int? Stat;
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

        // Weather Control use-effect: when set to a valid weather id (0..13), the
        // item's ConsumableData is repointed at a per-weather clone and the dxgi
        // DLL sets that weather on use. null/absent = "(vanilla)" - no weather
        // effect (the item stays a normal rum bottle: vanilla drink, no weather).
        public int? WeatherId;
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
