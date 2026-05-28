using System;
using System.Collections.Generic;

namespace Windrose.Quartermaster.Core
{
    // Item-centric profile: per-item overrides hold ALL the modifications for
    // a single item (`overrides[itemId] = { stackSize, ..., future props }`),
    // while globals are grouped per property (`globals.stackSize = {...}`).
    // This way both axes can grow independently:
    //   * adding a new property (e.g. Weight) -> add a field to ItemOverride
    //     and a new typed Global class (`globals.weight = {...}`)
    //   * old profiles stay backward-compatible (missing fields = null = skip)

    public sealed class Profile
    {
        public string Id;
        public string Name;
        public string Description;
        public DateTimeOffset CreatedAt;
        public DateTimeOffset ModifiedAt;
        public ProfileGlobals Globals;
        public Dictionary<string, ItemOverride> Overrides;

        // Per-LootTable overrides; key is the LT's path relative to the
        // LootTables/ tree, without ".json" extension (e.g.
        // "Mobs/DA_LT_Mob_BlackBeard_Sergeant_Final"). Null = no loot
        // overrides for this profile.
        public Dictionary<string, LootTableOverride> LootOverrides;

        // Per-Recipe edits for the Buyers/Sellers trade tabs. Keyed by
        // recipeId (the filename basename of the on-disk R5BLRecipeData
        // JSON, e.g. "DA_RD_CID_Food_Rum_Bottle_T03_Sell"). For custom
        // recipes added via the Buyers tab's "Add Entry" UI the key uses
        // the well-known prefix "QM_Custom_<hex>" and IsCustom=true; the
        // patcher then synthesizes the JSON instead of editing a vanilla
        // baseline. Recipe edits are GLOBAL - if the same recipe is
        // referenced by multiple RecipeLists, every list sees the new
        // values (matches the "Trade Rum Bottles" mod approach).
        // Null = no buyer-recipe overrides for this profile.
        public Dictionary<string, BuyerRecipeOverride> BuyerRecipes;

        // Per-RecipeList edits (add/remove recipe refs from a buyer or
        // seller NPC's roster). Keyed by buyerListId (path under RecipeLists/
        // without ".json", e.g. "TradeBrethren/DA_RecipeList_Trade_Brethren1_
        // PlayerSells"). The same key shape the GET /api/buyers endpoint
        // emits as `id`. Null = no list-level edits for this profile.
        public Dictionary<string, BuyerListOverride> BuyerLists;

        // Per-Recipe edits for the Sellers tab (vendors = PlayerBuys lists).
        // Mirrors BuyerRecipes structurally: keyed by recipeId basename of
        // an R5BLRecipeData JSON (e.g. "DA_RD_Piastre_Buy"). For custom
        // entries the key uses the "QM_SCustom_<hex>" prefix so the seller
        // and buyer custom namespaces stay disjoint and the patcher can
        // identify which patcher owns a given synthesized recipe. Like
        // BuyerRecipes, edits are GLOBAL - if the same recipe appears in
        // multiple PlayerBuys RecipeLists every list sees the new values.
        // Null = no seller-recipe overrides for this profile.
        public Dictionary<string, SellerRecipeOverride> SellerRecipes;

        // Per-RecipeList edits for PlayerBuys rosters (Sellers tab).
        // Mirrors BuyerLists; same id shape /api/sellers emits. Null = no
        // list-level edits.
        public Dictionary<string, SellerListOverride> SellerLists;

        // Custom items added via the Item Creator tab. Each entry is a
        // fresh InventoryItem cloned from a vanilla template (currently:
        // DA_DID_Misc_CoinPiastre_T02). The ItemCreatorPatcher loads the
        // template JSON, applies the per-field overrides, writes the
        // result to InventoryItems/Custom/<filename>.json, and appends
        // ItemName/ItemDescription rows to a copy of the vanilla
        // InventoryItems.csv so the engine resolves the FText TableId/Key
        // references the patched JSON contains.
        //
        // Ordered list (not dict) because the UI renders them as a
        // user-visible list and order matters for stability of card
        // positions during editing. The Id field is the primary key.
        public List<CustomItem> CustomItems;

        // Custom buildings added via the Building Creator tab. Each entry
        // describes one user-supplied "Buildable" cloned from a Vanilla
        // DA (the user picks the donor template via the GUI's Vanilla
        // template browser). The BuildingPatcher synthesises it from the
        // Vanilla DA + Vanilla MI clone + the user's cooked mesh / icon
        // / image texture. The patcher pipeline lives in
        // Tools/QuartermasterCore/BuildingCreator/ and is invoked by the
        // orchestrator (Etappe E) once per entry during Build.
        //
        // Like CustomItems, this is an ordered list (not dict) so the UI
        // renders deterministic card positions while editing. Id is the
        // primary key and is used for both the output DA stem
        // (DA_BI_<Id>) and the localization key (Decoration_<Id>_Name).
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
        public PickaxeRangeGlobal PickaxeRange;
        public CooldownsGlobal Cooldowns;
        public ProductionTimesGlobal ProductionTimes;
        public ShipMusicGlobal ShipMusic;
        public ShipMusicAddGlobal ShipMusicAdd;
        public LightingGlobal Lighting;
        // future: WeightGlobal Weight;
        // future: RarityGlobal Rarity;
    }

    public sealed class StackSizeGlobal
    {
        // Mutually exclusive: setting both is a profile-validation error.
        // Both null = stack-size globals not active for this profile.
        public int? Multiplier;   // vanillaStack * Multiplier (clamped at Cap)
        public int? Absolute;     // vanillaStack ignored, target = Absolute
        public int? Cap;          // upper bound; only relevant for Multiplier mode
    }

    public sealed class LootGlobal
    {
        // Per top-level-bucket multiplier (Mobs/Chests/Foliage/...). Both
        // Min and Max of each LootData entry are multiplied by this factor,
        // rounded AwayFromZero. Missing bucket = 1.0 (vanilla). Use "*" as
        // a wildcard fallback for un-listed buckets.
        public Dictionary<string, double> ByCategory;
    }

    // Pickup-radius is delivered as a freshly built IoStore mod triplet
    // alongside the main Quartermaster pak. The pipeline runs retoc on
    // the vanilla GA_Loot_AutoPickup Blueprint, patches the CDO via
    // UAssetAPI to set MagnetRadius = 400 * Multiplier, then re-packs as
    // a UE5 IoStore container.
    //
    // The Multiplier field is the user-facing scalar (slider in the GUI,
    // "1.5", "2", "5", "10", ...). 1.0 = vanilla = no triplet emitted;
    // null = no pickup config.
    public sealed class PickupRadiusGlobal
    {
        // Final scaling factor applied to the vanilla 400cm magnet range.
        // null OR == 1.0 -> no pickup-radius mod is built for this profile.
        public double? Multiplier;
    }

    // Fast-travel-bell + signal-fire placement caps. Patches
    // R5/Content/Gameplay/BuildingLimits/DA_BuildLimits_FastTravel.json
    // (a small DataAsset config file) and ships it inside the main
    // Pak1 - no IoStore triplet, no retoc step. The vanilla file has
    // three R5BuildingAmountLimit entries:
    //   [0] Bell variant 1 (DA_BI_Utilities_FastTravel_Bell)   default 10
    //   [1] Bell variant 2 (DA_BI_Utilities_FastTravelBell_02) default 10
    //   [2] Signal fire     (DA_BI_SignalFireT01)              default  3
    // The two bell variants share the user-facing "BellCap" because the
    // game enforces them as a single placement budget per player. Signal
    // fires have a distinct cap.
    //
    // null fields = "leave at vanilla". Patching is skipped entirely when
    // the resolved cap equals the vanilla default for that entry, so
    // BellCap=10 + SignalFireCap=3 is functionally identical to the
    // FastTravelBells global being null.
    public sealed class FastTravelBellsGlobal
    {
        public int? BellCap;        // both bell variants; 10..1000
        public int? SignalFireCap;  // 3..1000
    }

    // "Enhanced building stability" toggle. When enabled, the build
    // pipeline self-bakes the 787 supported vanilla DA_BI* DataAssets
    // (~862 total in 5.6, 75 are excluded as non-placeable / special-
    // physics) by overwriting four floats in the IntegritySettings
    // StructProperty directly in the asset's raw zen-format chunk bytes:
    //   BlockWeight=0, BlockMaxHorizontalLoad=1e7,
    //   BlockMaxVerticalLoad=1e7, BlockMinimumIntersectionExtent=0
    //
    // The byte-level patch is necessary because the to-legacy + to-zen
    // round-trip produces game-incompatible output for this asset class
    // (R5CollisionApproximation uses a custom C++ Serialize() that
    // breaks under unversioned <-> versioned property re-encoding).
    // BuildingStabilityPatcher therefore uses retoc unpack-raw + a
    // pattern-match-and-overwrite within the raw chunk + retoc pack-raw,
    // which leaves every byte not part of IntegritySettings untouched.
    //
    // Stability ships as its own _PStab_P companion triplet next to the
    // main mod; ModsEndpoint aggregates both into one logical mod row.
    //
    // null OR Enabled=false -> no stability triplet for this profile.
    public sealed class BuildingStabilityGlobal
    {
        public bool? Enabled;
    }

    // "No smoke" visual tweak: hides the smoke / flame Niagara FX on
    // campfires, furnaces and kilns by setting every EmitterHandle's
    // bIsEnabled = false on the corresponding NiagaraSystem export.
    //
    // Self-baked from vanilla via UAssetAPI (Niagara assets parse cleanly
    // unlike DA_BI): the build pipeline extracts the live game's Niagara
    // assets via retoc to-legacy, runs NoSmokePatcher on them, and re-packs
    // into the IoStore composite.
    //
    // Three independent toggles map to three asset groups:
    //   Campfire = FX_Bonefire_Center, FX_Campfire_smoldering, FX_Campfire_stylized_small
    //   Furnace  = FX_Flame_Furnace_T1, FX_Flame_Furnace_T3
    //   Kiln     = FX_Smoke_Kiln_T3, FX_Smoke_Kiln_Dop_T3
    //
    // null OR all three flags off/null -> no NoSmoke source contributes
    // to the IoStore composite. Each flag independently controls whether
    // its group's vanilla assets get patched and shipped.
    public sealed class NoSmokeGlobal
    {
        public bool? Campfire;
        public bool? Furnace;
        public bool? Kiln;
    }

    // Minimap reveal-range patch. Ships a modified
    // R5/Content/Config/DefaultR5MapSettings.ini as a loose file inside
    // the _Raw_P companion .pak, alongside (or independently of) the
    // building-stability .ucas/.utoc. The vanilla baseline INI is
    // lazy-extracted from the AES-encrypted pakchunk0-Windows.pak on
    // first build and cached under Sources/Vanilla/R5/Config/.
    //
    // Multiplier scales four scalar fields linearly:
    //   foot-class RevealBrushSize        vanilla 37   -> 37 * Multiplier
    //   foot-class MiniMapShowDistance    vanilla 250  -> 250 * Multiplier
    //   ship-class RevealBrushSize        vanilla 290  -> 290 * Multiplier
    //   ship-class MiniMapShowDistance    vanilla 750  -> 750 * Multiplier
    //
    // MaxMapResolution stays at the vanilla 2048 (unscaled).
    //
    // null OR Multiplier == 1.0 -> no minimap pak is built for this
    // profile. Same "no key in JSON" semantics as the other globals.
    public sealed class MinimapRangeGlobal
    {
        // Final scaling factor applied to the four vanilla reveal-range
        // floats. null OR == 1.0 -> no minimap mod is built for this profile.
        public double? Multiplier;
    }

    // Bonfire / building-center influence-radius patch. Scales two
    // FloatProperties on R5/Content/Gameplay/Building/BuildingUtilities/
    // DA_BI_Utilities_BuildingCenterT01.uasset by the same user multiplier:
    //
    //   InfluenceRadius   vanilla 5000 cm -> 5000 * Multiplier  (~50 m base)
    //   InfluenceHeight   vanilla 3000 cm -> 3000 * Multiplier  (~30 m base)
    //
    // These define the cylindrical "you can build here" zone around a
    // placed building-center / bonfire. An ingame probe (multiplier=3.0
    // ?  15000/9000) confirmed these are the gameplay-relevant fields
    // (the BP_BuildingBlock's ScenarioOverlapSphere radius alone had no
    // visible effect when tuned in isolation).
    //
    // The patch ships in the IoStore composite triplet (same .ucas/.utoc
    // as Pickup / NoSmoke) because the patched DataAsset goes through
    // retoc to-zen cleanly - the affected bytes sit well below the
    // CollisionApproximation tail that defeats the to-zen round-trip for
    // OTHER DA_BI* assets (the Stability set), so this asset doesn't
    // hit the same crash class the BuildingStability patcher works
    // around with raw-chunk patching.
    //
    // null OR Multiplier == 1.0 -> no bonfire patch is included in the
    // build (same null-collapse pattern as PickupRadius / MinimapRange).
    public sealed class BonfireRadiusGlobal
    {
        // Final scaling factor applied to both vanilla influence floats.
        // null OR == 1.0 -> no bonfire patch is built for this profile.
        public double? Multiplier;
    }

    // Pickaxe range / reach patch. Multiplies the TraceScaleModifier on each
    // pickaxe tier's InstanceParams DataAsset so the melee trace shapes grow
    // proportionally - in practice that means the player can chop nodes from
    // slightly further away. Only the TraceScaleModifier axis is touched
    // (variant A): the shared trace DataAssets and the deeply nested
    // SectionsData/FoliagePrediction trace entries are left at vanilla, since
    // the engine multiplies them by the top-level TraceScaleModifier at
    // hit-resolution time anyway.
    //
    // Four assets are patched in one shot (one per tier):
    //   T00 Stone   - DA_MeleeWpn_Pickaxe_T00_Stone_InstanceParams
    //   T01 Crude   - DA_MeleeWpn_Pickaxe_T01_Crude_InstanceParams
    //   T02 Regular - DA_MeleeWpn_Pickaxe_T02_Regular_InstanceParams
    //   T03 Reliable- DA_MeleeWpn_Pickaxe_T03_Reliable_InstanceParams
    //
    // The patch ships in the shared IoStore composite triplet
    // (sharedBaseName.ucas/utoc) alongside Pickup / Bonfire / NoSmoke.
    //
    // null OR Multiplier == 1.0 -> no pickaxe-range patch is built for this
    // profile. Same null-collapse pattern as the other multiplier globals.
    public sealed class PickaxeRangeGlobal
    {
        // Final scaling factor applied to each tier's vanilla
        // TraceScaleModifier. null OR == 1.0 -> no patch is built.
        public double? Multiplier;
    }

    // Cooldown-shortening patches. Each multiplier scales the vanilla
    // cooldown / reload time of a category by the chosen factor:
    //   1.0 = vanilla (no patch built for that property)
    //   0.5 = half the vanilla cooldown
    //   0.1 = ten times faster
    //
    // Eight independent axes, each null-collapses individually so the
    // build only ships assets the user actually wants modified. Covers
    // the player + ship consumable cooldowns (first five fields) plus four
    // additional cooldown families found in vanilla (boar whistle, ship
    // summon, ranged reload, ship cannons).
    //
    // Patch shapes per field:
    //   * Elixir, ShipRepairKit, BoarWhistle, ShipSummon - scale a
    //     ScalableFloatMagnitude.Value FloatProperty on a GameplayEffect
    //     DataAsset (deep struct walk).
    //   * Medicine, Recall - scale a top-level Magnitude FloatProperty
    //     on a BP_Calc R5ModMagCalc_SimpleAttributeBased asset.
    //   * RangedReload - scale a nested
    //     PassiveReloadGPData.ReloadTime FloatProperty across ~20
    //     firearm LogicParams DataAssets.
    //   * ShipCannon - scale every Battery's AimingData.ReloadTime
    //     FloatProperty inside an ArrayProperty on each Ship's
    //     BatteryManagerParams DataAsset.
    //
    // All patches ship in the same shared IoStore composite triplet as
    // pickup / bonfire / pickaxe.
    public sealed class CooldownsGlobal
    {
        // Player consumable: GE_Cooldown_Elixir (vanilla 3 s).
        public double? ElixirMultiplier;

        // Player consumable: BP_Calc_ConsCdBonus_Medicine.Magnitude
        // (vanilla 15 - drives GE_Cooldown_Medicine via custom calc class).
        public double? MedicineMultiplier;

        // Player consumable: BP_Calc_ConsCdBonus_Recall.Magnitude
        // (vanilla 600 - drives GE_Cooldown_Potion_Recall via custom calc).
        public double? RecallMultiplier;

        // Ship consumable: BOTH GE_Ship_Cooldown_RepairKit (vanilla 40 s)
        // and GE_Ship_Cooldown_RepairKit_Small (vanilla 20 s). The mod
        // treats them as one family because they sit next to each other
        // and have the same effective cooldown semantics.
        public double? ShipRepairKitMultiplier;

        // Player consumable: GE_SpawnerCooldown (vanilla 1.0 - the
        // multiplier on the CurveTable lookup. Setting < 1.0 shortens
        // boar-whistle cooldown proportionally).
        public double? BoarWhistleMultiplier;

        // Player ability: GE_ShipSummon_Cooldown
        // (vanilla 5 s - plain ScalableFloat, no curve).
        public double? ShipSummonMultiplier;

        // Player firearms: PassiveReloadGPData.ReloadTime across all
        // pistol / musket / blunderbuss LogicParams variants. Vanilla
        // ranges from ~12 s (Pistol_Reliable) to ~15 s (Musket_Infantry).
        public double? RangedReloadMultiplier;

        // Ship combat: AimingData.ReloadTime on every BatteryManager
        // entry of every hull (Cutter, Brig, Frigate, Ketch, ...).
        // Vanilla 10 s per battery.
        public double? ShipCannonMultiplier;
    }

    // Production / station-related time scaling. One multiplier per
    // recipe family (classified by RecipeTag + filename heuristics) plus
    // the crop-growth axis for the farming side.
    //
    // Crop growth patches R5BLCropParams.GrowthDuration (FTimespan ticks)
    // on every DA_Crop_*.json under Farming/Crops/. The vanilla value is
    // scaled by the user multiplier.
    //
    // Cooking-duration multipliers all patch R5BLRecipeData.
    // CookingProcessDuration (seconds, top-level integer/float field) on
    // recipes whose RecipeTag matches the family's classifier. Vanilla
    // values per family:
    //   * Smelting    (Furnace recipes)    ~4200 s (1h10) per ingot
    //   * Kiln        (charcoal/coconut)   ~4200 s
    //   * Tanning     (TanLeather/Tannin)  ~4200 s
    //   * Milling     (cornmeal/juices)    varies
    //   * BuildingBits  (Bits.* tag)       small (often 30 s)
    //   * Decoration  (Deco.* tag)         varies, often 30..1800 s
    //   * ArmorWeapon (Armor.*, ItemUpgrade.*) varies
    //   * TradeOutpost (NPC-order wait)    4200 s (1h10)
    //   * Other        (everything else)   varies
    //
    // null OR Multiplier == 1.0 -> no patch for that family. Each axis
    // null-collapses independently so the build only ships recipes the
    // user actually wants modified.
    //
    // The CookingDurationPatcher runs AFTER BuyerPatcher and
    // SellerPatcher, merging into their output files if a recipe was
    // already modified by a trade-list edit (so the final JSON has
    // both the new cost/result AND the new duration).
    public sealed class ProductionTimesGlobal
    {
        // Crop growth: R5BLCropParams.GrowthDuration (FTimespan ticks).
        public double? CropGrowthMultiplier;

        // Furnace: copper/iron/gold/tumbago/holy ingots, ash.
        public double? SmeltingMultiplier;

        // Kiln: charcoal, coconut oil.
        public double? KilnMultiplier;

        // Tannery: tannin, tan-leather, elastic leather.
        public double? TanningMultiplier;

        // Mill / press: cornmeal, grape juice, pineapple juice, varnish,
        // flax oil.
        public double? MillingMultiplier;

        // Anvil / workbench - building bits (Bits.*). Highest-volume
        // family in vanilla (~194 recipes).
        public double? BuildingBitsMultiplier;

        // Workbench / deco-bench (Deco.* tag). ~106 recipes.
        public double? DecorationMultiplier;

        // WeaponTable + armor + ItemUpgrade.*. ~25 recipes.
        public double? ArmorWeaponMultiplier;

        // Trade Outpost NPC order wait times (TradeOutpost.* tag).
        // ~148 recipes, all 4200 s vanilla.
        public double? TradeOutpostMultiplier;

        // Catch-all for recipes that didn't match any family above
        // (~50 recipes). Off by default - user opts in.
        public double? OtherMultiplier;
    }

    // Ship-music (sea-shanty) replacement. Vanilla ships 10 SoundWave
    // assets under R5/Content/Audio/Game/Music/Shanti/SWAV/ (Blow The
    // Man Down, Bully In The Alley, Drunken Sailor, Good Morning Ladies,
    // Leave Her Johnny, Maggie May, Old Maui, Rolling Home, The British
    // Tars, Whiskey Johnny). Each is referenced by four SoundCues (per
    // ship size Small/Medium/Large plus a VoiceNoPlayer crew variant),
    // so replacing one SWAV automatically affects all four playback
    // contexts.
    //
    // The audio sits in BINK Audio format (RAD proprietary) inside the
    // .ubulk bulk-data. There's no open-source Bink encoder, so the
    // user has to cook their replacement audio through the UE5 Editor
    // first - they import a WAV as USoundWave, let the editor cook the
    // project for Windows, then hand the resulting .uasset+.uexp+.ubulk
    // triplet to Quartermaster. ShipMusicPatcher then re-writes the
    // FName table inside the user's .uasset so the engine resolves the
    // file under the vanilla slot's asset path, and copies all three
    // files into the IoStore composite staging tree.
    //
    // Storage: per-profile under Profiles/<id>/ShipMusic/<slotStem>/
    //   audio.uasset, audio.uexp, audio.ubulk
    //
    // Songs is keyed by the vanilla SWAV stem (e.g.
    // "SWAV_Shanti_DrunkenSailor"). Missing key = vanilla shanty plays.
    // null Songs OR empty dict = no ship-music source contributes to
    // the IoStore composite.
    public sealed class ShipMusicGlobal
    {
        public Dictionary<string, ShipMusicSlotOverride> Songs;

        // SWAV stems the user excluded from the shanty rotation. An
        // excluded slot is removed from the DA's Shanty.Cues array at
        // build time so the engine never picks it (instead of just
        // silencing it, which would still occupy a rotation slot).
        // Indices used by the build pipeline come from the position in
        // ShipMusicSlots.All - storing stems keeps the config robust
        // against future slot reordering.
        //
        // null OR empty list = nothing excluded. An override (Songs
        // entry) on the same stem stays on disk but does not play while
        // the slot is excluded - re-including restores it instantly.
        public List<string> ExcludedSlots;
    }

    // One replaced shanty slot. The audio bytes themselves live on
    // disk under Profiles/<id>/ShipMusic/<slotStem>/audio.{uasset,uexp,ubulk};
    // this struct only holds metadata for the GUI (so the card can show
    // "Custom: MyTune.uasset" instead of an opaque hash).
    public sealed class ShipMusicSlotOverride
    {
        // Original filename the user picked, e.g. "MyAwesomeShanty.uasset".
        // Display-only - the patcher reads the renamed copy from disk.
        public string OriginalFilename;
    }

    // Ship-music ADD feature (distinct from the Override above): user-supplied
    // sea-shanty tracks added BEYOND the 10 vanilla slots. The game's four
    // DA_<ShipType>_AudioParams DataAssets get their Shanty.Cues array extended
    // by ShipMusicAddDaPatcher, and four new SoundCue assets per track plus one
    // new SoundWave per track get packed into the mod IoStore composite
    // (ShipMusicAddCueCloner + Bink-encoded SWAV via ShipMusicPatcher's
    // template pipeline).
    //
    // Storage per added track (on disk under the profile):
    //   Profiles/<id>/ShipMusicAdd/<trackKey>/
    //     audio.wav                  source WAV the user uploaded
    //     meta.json (optional)       OriginalFilename + title for the GUI
    //
    // trackKey is a stable, filesystem-safe identifier (typically the SWAV
    // stem suffix, e.g. "MyTune" -> SWAV stem becomes SWAV_Shanti_MyTune
    // and Cue stems become CUE_Shanti_<index>_<flavor>(_VoicePlayer)).
    //
    // Index assignment is positional: trackKey order in Tracks decides which
    // vanilla DA index slot each one occupies (vanilla 1..10 reserved for
    // overrides). Track at index 0 in the list becomes slot 11; index 1
    // becomes slot 12; etc.
    //
    // null = no added tracks for this profile (vanilla shanty count = 10).
    public sealed class ShipMusicAddGlobal
    {
        // Ordered list of added tracks. Position in list -> slot index
        // assignment (Tracks[0] -> slot 11, Tracks[1] -> slot 12, ...).
        // Empty list or null = no ship-music-add source contributes.
        public List<ShipMusicAddedTrack> Tracks;
    }

    public sealed class ShipMusicAddedTrack
    {
        // Stable filesystem-safe key. Forms the SWAV stem suffix
        // (SWAV_Shanti_<TrackKey>) and is the on-disk subdirectory under
        // Profiles/<id>/ShipMusicAdd/<TrackKey>/. Constrained to
        // [A-Za-z0-9_] by the GUI/validation; tampered profiles with
        // unsafe characters get rejected in ResolveShipMusicAddJobs.
        public string TrackKey;

        // Human-friendly display title for the GUI card. Not used in any
        // baked asset name. Optional.
        public string Title;

        // Original filename the user picked, e.g. "MyTrack.wav".
        // Display-only - the patcher reads audio.wav from the per-track dir.
        public string OriginalFilename;
    }

    // Lighting-radius patch. Multiplies the AttenuationRadius FloatProperty
    // on every supported PointLight Blueprint (see LightingPatcher.Lights
    // for the registry). Exposes one slider per light source so users can
    // tune them independently - "all torches twice as bright but candles
    // vanilla" is expressable.
    //
    // Resolution rule per light source:
    //   1. If Overrides[stem] is set AND != 1.0 -> that's the effective
    //      multiplier for the light.
    //   2. Otherwise the OverallMultiplier applies (default 1.0).
    //
    // A light contributes to the build only when its effective multiplier
    // is != 1.0. If the resolved set is empty (overall == 1.0 and no
    // per-light override is != 1.0), no lighting source is added to the
    // IoStore composite.
    //
    // null = no lighting config for this profile (vanilla baseline).
    public sealed class LightingGlobal
    {
        // Overall multiplier applied to every light whose per-light
        // override is null or 1.0. Range [0.1, 10.0]; the GUI clamps to
        // these bounds. null is treated as 1.0.
        public double? OverallMultiplier;

        // Per-light override map. Key = light stem (matches
        // LightingPatcher.Lights[].Stem, e.g. "BP_PointLight_WildFire").
        // Value = multiplier that fully replaces the overall for THIS
        // light (i.e. it does NOT compound with OverallMultiplier).
        // 1.0 collapses to "no override" semantically; the resolver
        // re-applies the overall.
        public Dictionary<string, double> Overrides;
    }

    public sealed class ItemOverride
    {
        public int? StackSize;    // null = no per-item override for this property
        // future: float? Weight;
        // future: string Rarity;
    }

    // A LootTable override holds the full edit-spec for a single LT:
    // sparse per-entry edits, a removal-list of vanilla indices, and a list
    // of brand-new entries to append. All three are independent and can be
    // combined in any way.
    public sealed class LootTableOverride
    {
        // Vanilla-index -> sparse field overrides. Indices reference the
        // VANILLA list before any removal - the patcher reconciles.
        public Dictionary<int, LootEntryEdit> Entries;

        // Vanilla-indices that should NOT appear in the output. Combined
        // with Entries: an index can appear in both, in which case Removed
        // wins (the entry is skipped, the edit is ignored).
        public List<int> Removed;

        // Brand-new entries appended after the surviving vanilla entries
        // (in declaration order). Always full schema - there's no vanilla
        // baseline to inherit from.
        public List<LootEntry> Added;
    }

    // Sparse: only non-null fields take effect. null = vanilla wins (after
    // any global multiplier is applied for Min/Max).
    public sealed class LootEntryEdit
    {
        public int? Min;
        public int? Max;
        public int? Weight;
        // For LootItem and LootTable: null = unchanged; a path = new value;
        // the literal sentinel "None" = explicitly clear the slot.
        public string LootItem;
        public string LootTable;
        // ItemAttributeModifiers stays read-only in Phase 1.
    }

    // Full schema for new entries. ItemAttributeModifiers is always
    // emitted as [] in Phase 1.
    public sealed class LootEntry
    {
        public int Min;
        public int Max;
        public int Weight;        // 0 for List-type tables, > 0 for Weight-type
        public string LootItem;   // "None" or an asset path
        public string LootTable;  // "None" or a sub-LT asset path
    }

    // A user-defined custom item, cloned from a vanilla template. The
    // patcher walks profile.CustomItems and for each entry:
    //   1. Loads <TemplateId>.json from Sources/Vanilla/InventoryItems
    //   2. Clones it in memory and overwrites the editable fields
    //   3. Writes the result under
    //      InventoryItems/Custom/<Id>.json so the engine indexes it as
    //      /R5BusinessRules/InventoryItems/Custom/<Id>.<Id>
    //   4. Appends two rows (ItemName, ItemDescription) to a copy of the
    //      vanilla InventoryItems.csv string-table, using Key
    //      "<Id>_ItemName" / "<Id>_ItemDescription". The patched JSON
    //      references these keys via the InventoryItemUIData FText shape
    //      vanilla items use ({TableId, Key}).
    //
    // The Id convention "QmItem_<8hex>" mirrors the Buyer custom-recipe
    // pattern ("QM_Custom_<8hex>"). The 8-hex suffix is generated by the
    // frontend at create time and never changes, so per-item state survives
    // re-saves cleanly.
    public sealed class CustomItem
    {
        // Stable id, frontend-generated. Used as filename basename, JSON
        // key prefix, and FName for the synthetic GameplayTag. Must be a
        // valid filename + safe for the engine (alnum + underscore only).
        public string Id;

        // Basename of the vanilla item to clone (e.g.
        // "DA_DID_Misc_CoinPiastre_T02"). The patcher locates the source
        // JSON by walking Sources/Vanilla/InventoryItems for a file with
        // this stem; folder doesn't matter (UE indexes assets by basename
        // anyway).
        public string TemplateId;

        // Free-text display name. Stored verbatim (not localized) and
        // emitted into the modded InventoryItems.csv as the SourceString
        // for <Id>_ItemName. Empty string = leave the template's name in
        // place (rare; usually you want a custom name when cloning).
        public string Name;

        // Free-text description, written into the CSV the same way. Use
        // "\r\n" inside the string for line breaks - the engine renders
        // them in the tooltip.
        public string Description;

        // Per-field overrides for the template's defaults. null = inherit
        // from the template (which for Piastre means MaxStack=9999,
        // Rarity=Rare, KeepOnDeath=true).
        public int? MaxCountInSlot;
        public string Rarity;
        public bool? KeepInInventoryOnDeath;

        // Verbatim asset-path to the icon texture. null/empty = inherit
        // from the template (Piastre uses
        // "/Game/UI/Icons/Items/New/T_ItemIcon_Loot_T02_CoinPiastre_01.T_..."
        // ).
        //
        // Two ways this gets populated:
        //   - User leaves IconPath empty -> ItemTexture stays null/template.
        //   - User uploads a PNG via the "Upload Icon..." button -> the
        //     server stores the bytes at
        //       Profiles/<profileId>/Icons/<itemId>.png
        //     and the build pipeline's IconBakerPatcher synthesises a
        //     legacy uasset+uexp under
        //       R5/Content/UI/Icons/Items/Custom/T_QmCustomIcon_<id>
        //     The patcher then overwrites ItemTexture with the matching
        //     /Game/UI/.../T_QmCustomIcon_<id>.T_QmCustomIcon_<id>
        //     reference, so the synthesized JSON points at the baked
        //     texture instead of the template's icon.
        public string ItemTexture;

        // Filename (basename only, no slashes) of the per-profile PNG that
        // backs this item's custom icon. Stored under
        //   Profiles/<profileId>/Icons/<IconPath>
        // by the upload endpoint. null/empty = no custom icon, the build
        // falls back to whatever ItemTexture references (template default
        // when ItemTexture is also null).
        //
        // Lives separately from ItemTexture so a profile.json on its own
        // (sans Icons/ folder) is still useful for reading - the asset
        // path tells you which icon the build TARGETED, IconPath tells
        // you whether the build can REGENERATE it from local PNG bytes.
        public string IconPath;

        // Italic flavor / vanity text shown at the bottom of the tooltip
        // (the line that reads "Acht-Reales! Acht-Reales!" on the vanilla
        // Piastre). Treated exactly like Name and Description: the patcher
        // always overwrites InventoryItemUIData.VanityText with an FText
        // pointing at "<Id>_ItemVanity", and always emits the matching CSV
        // row. null is normalized to "" - an empty flavor line in the
        // tooltip, no inherit-from-template fallback.
        public string VanityText;
    }

    // Edit-spec for a single Recipe (R5BLRecipeData) that powers a buyer
    // entry (PlayerSells lists). The 4 trade fields (sold item + count,
    // paid item + count) plus the reputation gate are the mutable values;
    // everything else (RecipeTag, UIData, ...) is preserved verbatim from
    // vanilla for IsCustom=false, or synthesized from a template for
    // IsCustom=true.
    //
    // Sparse: null fields = "vanilla wins" (for IsCustom=false). Custom
    // recipes (IsCustom=true) must have all four trade fields set or the
    // patcher will refuse to emit the file - there's no vanilla baseline to
    // fall back to. The patcher validates this and surfaces a warning
    // instead of writing broken JSON. CraftRequirement on custom recipes
    // defaults to "None" when null.
    public sealed class BuyerRecipeOverride
    {
        // Item the player gives up (RecipeCost[0].Item). Full asset path
        // form: "/R5BusinessRules/InventoryItems/Consumables/Misc/DA_DID_..."
        public string ItemPath;
        public int? ItemCount;

        // Item the player receives (RecipeResult[0].Item). For PlayerSells
        // recipes this is typically a coin variant (Piastre/Guinea/...).
        public string PayItemPath;
        public int? PayCount;

        // Reputation gate (CraftRequirement). Full asset path form or the
        // literal "None". null = "leave at vanilla" (for IsCustom=false) /
        // "None" (for IsCustom=true). Vanilla PlayerSells recipes ship
        // with CraftRequirement="None" so the dropdown is mostly cosmetic
        // for buyers, but exposing it keeps the UI symmetric with sellers
        // and lets power-users gate buyer recipes too.
        public string CraftRequirement;

        // True = the patcher synthesizes a fresh R5BLRecipeData JSON from
        // an internal template (cloned from a vanilla PlayerSells recipe at
        // build time so the schema matches the live game's expectations).
        // False = the patcher loads the vanilla file matching this key's
        // basename and patches the four fields in-place.
        public bool IsCustom;
    }

    // Edit-spec for a single Recipe (R5BLRecipeData) that powers a seller
    // entry (PlayerBuys lists, i.e. the NPC is a vendor). Field semantics
    // match BuyerRecipeOverride - itemId / itemCount = the "main" item
    // being traded (= what the NPC delivers on the Sellers side, =
    // RecipeResult on disk), payItemId / payCount = the currency the
    // player pays (= RecipeCost on disk). The SellerPatcher does the
    // Cost/Result swap on its way to JSON so the override shape stays
    // uniform across both tabs - the frontend never has to care which
    // R5BLRecipeData side maps to which UI column.
    //
    // Vanilla PlayerBuys recipes regularly have CraftRequirement set to a
    // faction reputation gate (Smugglers/Brethren/Bucaneers/Civilians,
    // levels 1-4), so the dropdown is genuinely useful here.
    public sealed class SellerRecipeOverride
    {
        // Item the NPC delivers (RecipeResult[0].Item on disk). Full asset
        // path form.
        public string ItemPath;
        public int? ItemCount;

        // Item the player pays in (RecipeCost[0].Item on disk). Typically
        // a coin variant.
        public string PayItemPath;
        public int? PayCount;

        // Reputation gate (CraftRequirement). Full asset path or "None".
        // null = "leave at vanilla" for vanilla edits, "None" for customs.
        public string CraftRequirement;

        // True = synthesize the JSON from a vanilla PlayerBuys template.
        // False = patch the vanilla file matching this key's basename.
        public bool IsCustom;
    }

    // Edit-spec for a single R5BLRecipeList JSON. Captures the RecipeList[]
    // diff against vanilla: which references to drop, which new ones to
    // append. The patcher rebuilds the list in this order:
    //   1. iterate vanilla refs, skip any whose basename is in RemovedRecipeIds
    //   2. append entries for each id in AddedRecipeIds, resolving to either
    //      the vanilla recipe path or the synthesized "QM_Custom_*" path
    //
    // Both lists deduplicate themselves silently - the frontend never
    // sends duplicates, but a profile edited by hand might.
    public sealed class BuyerListOverride
    {
        // Recipe basenames to append to the RecipeList[]. Resolution rule:
        //   * "QM_Custom_*"       -> /R5BusinessRules/Recipes/Custom/<id>.<id>
        //   * everything else     -> looked up via the vanilla recipe map
        //                            (basename -> on-disk path) the patcher
        //                            builds at start.
        public List<string> AddedRecipeIds;

        // Recipe basenames to strip from the vanilla RecipeList[]. Matched
        // against the trailing segment of each vanilla ref (after the last
        // '/' and trimmed of any ".AssetName" suffix).
        public List<string> RemovedRecipeIds;

        // Definitive output order when set. Contains all IDs (vanilla
        // basenames + QM_Custom_* custom ids) that should appear in the
        // rebuilt RecipeList, in the desired sequence. Vanilla IDs absent
        // from this list are implicitly removed. null = use legacy mode
        // (iterate vanilla minus RemovedRecipeIds, then append AddedRecipeIds).
        public List<string> RecipeOrder;
    }

    // A user-defined custom building, cloned from a user-picked Vanilla
    // "Buildable" template (any DA_BI_*.uasset under
    // /Game/Gameplay/Building/, picked via the GUI's Vanilla template
    // browser). The BuildingPatcher pipeline:
    //   1. Stages the user's cooked assets (mesh + icon + image texture)
    //      into the mod-pak's output namespace (WindrosePaths.ModItems-
    //      PackagePath). User-cooked custom Materials/MIs are SKIPPED -
    //      we bisected to the fact that those crash the shipping game.
    //   2. Rewrites the user-cooked mesh's NameMap so its per-slot
    //      material refs (M_<AssetPrefix>_<SlotName>) point at our
    //      cloned MI stems (MI_<AssetPrefix>_<SlotName>).
    //   3. Extracts the Vanilla DA + Vanilla MI(s) via retoc to-legacy,
    //      then clones each under our mod path with the NameMap rewritten
    //      (texture refs -> user-custom or shared default-VTs; mesh+icon
    //      refs -> user-cooked stems; localization key -> per-building
    //      synthesized key).
    //   4. Patches the cloned MI's VectorParameterValues in-place via
    //      UAssetAPI to apply user-declared color overrides per slot
    //      (e.g. forcing EdgeColor/AOColor to white so a user-supplied
    //      image renders untinted).
    //
    // The DLL's per-profile qm_items_<safeName>.json picks up DA_BI_<Id> via
    // the orchestrator (Etappe E) so the engine spawns one widget per Building
    // under the template's CategoryTag tab at inject-time.
    //
    // Id convention "QmBldg_<8hex>" mirrors the CustomItem pattern. The
    // 8-hex suffix is frontend-generated at create-time and never changes,
    // so per-building state survives re-saves cleanly.
    public sealed class CustomBuilding
    {
        // Stable id, frontend-generated. Filename-safe (alnum +
        // underscore only). Drives:
        //   * Output DA stem: DA_BI_<Id>
        //   * Localization key: Decoration_<Id>_Name
        //   * qm_items_<profile>.json entry name
        public string Id;

        // Vanilla DA virtual path the user picked as donor template
        // (e.g. "/Game/Gameplay/Building/BuildingDecoration/DA_BI_FloorTorch_01").
        // The patcher resolves this through the BuildingTemplateCatalog
        // + Inspector to hydrate a BuildingTemplate (donor paths, FText
        // keys, recipe ref, category tag).
        public string TemplateId;

        // Free-text display name (shown in the in-game build menu). Stored
        // verbatim, emitted into a per-profile CSV under the key
        // "Decoration_<Id>_Name" so the engine resolves the FText ref
        // the patched DA carries.
        public string Name;

        // Free-text description (shown in the in-game build menu tooltip).
        // Same CSV-Synthese pattern as Name, under the key
        // "Decoration_<Id>_Desc".
        public string Description;

        // Path to the user's cooked-output folder for this building's
        // assets. The folder can live anywhere on disk - the patcher
        // restages every asset under the mod's output namespace at build
        // time and self-path-normalizes their FolderName field, so the
        // source layout (subfolders, naming) is up to the user. Two
        // accepted shapes:
        //   - Absolute path, e.g. some
        //       <UEProj>/Saved/Cooked/Windows/<ProjectName>/Content/<UserSubfolder>/
        //   - Profile-relative folder name (e.g. "MyPainting") which the
        //     build pipeline resolves via WindrosePaths.ResolveProfile-
        //     RelativeFolder to <Profiles>/<profileId>/<value> when that
        //     folder exists. Useful for portable profiles where the user
        //     drops the cooked output next to the profile JSON. Profile-
        //     relative resolution is applied at consumption time only -
        //     the stored value is never rewritten, so what the user
        //     typed round-trips through save/load.
        // The build pipeline scans the resolved folder for files whose
        // stem starts with AssetPrefix and stages them; user-cooked
        // materials (stem matching M_<AssetPrefix>_<SlotName>) are
        // skipped because those crash the shipping game.
        public string CookedFolderPath;

        // Asset-prefix the user picked in the UE editor (e.g.
        // "QmPainting"). Drives:
        //   * Cooked-folder scan filter (only files whose stem starts
        //     with this prefix get staged)
        //   * Per-slot user material stems (M_<AssetPrefix>_<SlotName>)
        //     that the mesh's NameMap references and the patcher rewrites
        //   * Per-slot MI clone stems (MI_<AssetPrefix>_<SlotName>) the
        //     patcher emits into the mod pak
        //
        // No longer a user-facing field: the GUI removed the input and
        // the value is auto-derived from MeshStem at save time (see
        // DeriveAssetPrefixFromMeshStem). Kept as a persisted field so
        // older profiles still round-trip and so build code reads the
        // resolved string from a single place. Use ResolveAssetPrefix()
        // when consuming - it falls back to the derived value when the
        // stored field is empty (e.g. profile was hand-edited).
        public string AssetPrefix;

        // User-cooked mesh stem (no extension, no slashes). Expected to
        // exist as <CookedFolderPath>/<MeshStem>.uasset.
        public string MeshStem;

        // User-cooked icon stem (Texture2D for the build-menu thumbnail).
        // Same path/extension expectation as MeshStem.
        public string IconStem;

        // Per-slot user inputs, keyed by stable mesh-slot identifier.
        // Etappe G: keys are mesh-derived (e.g. "0" / "1" by index, or
        // "WorldGridMaterial" / "lambert1" by name) - the GUI picks
        // whichever the user mesh exposes. null for a given key = the
        // slot has no user-config yet (Build will fail until the user
        // picks a Vanilla-MI parent for it).
        public Dictionary<string, CustomBuildingSlot> Slots;

        // Etappe H2: user-edited build cost. List of resource entries
        // each carrying a Vanilla DA_DID_Resource_* path + count.
        // Empty / null = use the template's Vanilla-recipe defaults
        // (i.e. clone-through without modifying RecipeCost). When the
        // user explicitly empties the list the patcher writes an empty
        // RecipeCost array so the building can be built for free.
        public List<RecipeCostEntry> RecipeCost;

        // Etappe J / Audio extension: optional Component-FX preset. When
        // set (non-empty), the build pipeline clones a vanilla donor BP
        // (e.g. BP_BuildingBlock_FloorTorch_C for "torch",
        // BP_BuildingBlock_PendulumClockT04_01 for "audio") once per used
        // preset into the mod-pak's output namespace, and patches this
        // building's DA so its ItemClass soft-class-ref points at the
        // cloned BP. Result: ingame the building spawns with the donor's
        // SCS-Components by inheritance (Niagara + Light + Audio for
        // "torch"; AudioComponent for "audio"). null or empty = no preset.
        //
        // Valid values: see ComponentPresetCatalog.Presets (Id field).
        // Currently: "torch", "audio".
        public string ComponentPresetId;

        // Backward-compat: profiles that opted into the Torch preset before
        // the FlamePreset -> ComponentPreset rename serialized as
        // "flamePresetId". On deserialize we migrate the value into the
        // new ComponentPresetId field. The getter returns null so with
        // JsonIgnoreCondition.WhenWritingNull the legacy key never gets
        // written back to disk - profiles saved after a successful load
        // will use the new key going forward.
        [System.Text.Json.Serialization.JsonInclude]
        public string FlamePresetId
        {
            get => null;
            set { if (!string.IsNullOrEmpty(value) && string.IsNullOrEmpty(ComponentPresetId)) ComponentPresetId = value; }
        }

        // -----------------------------------------------------------------
        // Audio preset extension (only meaningful when ComponentPresetId ==
        // "audio"). The user can:
        //
        //   - Upload a custom audio file (wav/mp3/ogg/...) which is
        //     transcoded to a 44.1 kHz stereo 16-bit PCM WAV and stored at
        //     <Profiles>/<id>/BuildingAudio/<buildingId>/audio.wav. If the
        //     file is absent, the cloned BP keeps the donor's vanilla audio
        //     (Tick-Tack from MS_Building_Clock_LP). When a user audio file
        //     IS present, the build pipeline stages a per-building SWAV +
        //     looping SoundCue under /Game/Quartermaster/ and rewires the
        //     cloned BP's AudioComponent.Sound to point at the cue.
        //
        //   - Set a max-audible distance (range) in meters. Driven by an
        //     AudioComponent.AttenuationOverrides inline override on the
        //     cloned BP, so we don't need to stage a separate
        //     SoundAttenuation asset per building. Default 15 m matches
        //     the typical "ambient loop you hear if you're next to it"
        //     feel; the upper bound is generous for big set pieces.
        //
        // Both fields are optional - the GUI only surfaces them when the
        // component preset is "audio".
        public double AudioRangeMeters;  // 0 = unset -> default 15 m

        // Loudness multiplier baked into the cloned SoundCue's
        // VolumeMultiplier float property. 1.0 = vanilla loudness, 0.5 =
        // half, 2.0 = double. Lower than 0.01 effectively mutes the
        // building; we don't enforce an upper bound but the GUI caps at
        // 2.0 to avoid distortion. 0 = unset -> default 0.5 (chosen as a
        // safe ambient-loop loudness so a fresh building doesn't blast).
        public double AudioVolume;

        public AudioSourceMeta AudioSource;

        // -----------------------------------------------------------------
        // Returns the AssetPrefix that should drive the asset-allowlist
        // filter + the MI-clone stem naming. Reads the persisted field
        // first; falls back to the value derived from MeshStem when the
        // field is empty. Returns "" when neither is available (caller
        // gates on emptiness).
        // -----------------------------------------------------------------
        public string ResolveAssetPrefix()
        {
            if (!string.IsNullOrWhiteSpace(AssetPrefix)) return AssetPrefix.Trim();
            return DeriveAssetPrefixFromMeshStem(MeshStem);
        }

        // -----------------------------------------------------------------
        // Derive an asset prefix from a Mesh stem. The UE-editor naming
        // convention is "SM_<Prefix>_<Suffix>" where Suffix is usually a
        // sequence number (01, 02, ...). We:
        //
        //   1. Strip the leading "SM_" (case-insensitive).
        //   2. Strip a trailing "_<digits>" if present.
        //
        // Examples:
        //   "SM_QmWieselburger_01"   -> "QmWieselburger"
        //   "SM_QmPaint_Mat_01"      -> "QmPaint_Mat"
        //   "SM_QmPainting"          -> "QmPainting"   (no numeric suffix)
        //   ""                       -> ""             (caller gates)
        // -----------------------------------------------------------------
        public static string DeriveAssetPrefixFromMeshStem(string meshStem)
        {
            if (string.IsNullOrWhiteSpace(meshStem)) return "";
            var s = meshStem.Trim();
            if (s.StartsWith("SM_", StringComparison.OrdinalIgnoreCase))
                s = s.Substring(3);
            // Strip a trailing "_<digits>" if present.
            var m = System.Text.RegularExpressions.Regex.Match(s, @"^(.*)_\d+$");
            if (m.Success) s = m.Groups[1].Value;
            return s;
        }
    }

    // Metadata about a user-uploaded audio file for the Audio component
    // preset. Lives on CustomBuilding.AudioSource. The bytes themselves
    // sit at <Profiles>/<id>/BuildingAudio/<buildingId>/audio.wav (after
    // ffmpeg preprocessing to 44.1 kHz / stereo / 16-bit PCM); this DTO
    // carries only the display-friendly metadata so the GUI can render
    // "Custom: mysong.wav (12s)" without re-reading the WAV header.
    public sealed class AudioSourceMeta
    {
        // The filename as it was uploaded ("mysound.mp3"). Display only.
        public string OriginalFilename;
        // Duration in seconds. Display only AND used at build time to
        // size the SoundCue's Duration field.
        public float DurationSec;
        // Sample rate of the source (after preprocessing should be 44100).
        public int SampleRate;
        // Channel count (after preprocessing should be 2).
        public int Channels;
        // Cleaned-WAV size in bytes. Display only.
        public long SizeBytes;
    }

    // One row in CustomBuilding.RecipeCost. Mirrors the wire-format
    // RecipeCostEntryDto used by the inspect-recipe endpoint and the
    // recipe editor in the GUI.
    public sealed class RecipeCostEntry
    {
        // Full UE virtual path with .Stem suffix, e.g.
        //   /R5BusinessRules/InventoryItems/DefaultItems/Resource/
        //     DA_DID_Resource_Hardwood_T02.DA_DID_Resource_Hardwood_T02
        // The catalog hands these out; the patcher writes them through
        // verbatim into RecipeCost[i].Item.
        public string ItemPath;

        // Quantity required per craft.
        public int Count;
    }

    // Per-slot user input (Etappe G mesh-driven schema).
    //
    // Replaces the old template-driven schema (CustomAlbedoStem etc.)
    // which assumed the template hardcoded the param set. Each slot
    // now carries:
    //
    //   - VanillaMaterialParentPath: the Vanilla MI the user picked
    //     as the parent for this slot's clone. The patcher extracts
    //     this MI via retoc to-legacy and clones it into the mod's
    //     output namespace as MI_<AssetPrefix>_<SlotKey>.
    //
    //   - ScalarParams / VectorParams / TextureParams: user overrides
    //     for parameters that exist in the picked Vanilla MI. The GUI
    //     only ever offers params that the MI's ScalarParameterValues /
    //     VectorParameterValues / TextureParameterValues blocks contain
    //     (no param-add path - we just edit existing entries).
    //
    // All four collections are optional; an empty dict means "no
    // overrides, use Vanilla values as-is".
    public sealed class CustomBuildingSlot
    {
        // Vanilla MI the user picked as the shader / parent for this
        // slot. Required for a slot to participate in the build.
        // Format: "/Game/.../MI_<Name>" (UE virtual path, no extension).
        public string VanillaMaterialParentPath;

        // Per-parameter overrides. Keys are param names as they appear
        // in the Vanilla MI (e.g. "Roughness", "Edge Color", "Albedo").
        // Casing matches the MI - the patcher compares case-sensitively.
        //
        // Scalars: float -> overrides ScalarParameterValues entries.
        public Dictionary<string, float> ScalarParams;

        // Vectors: float[4] = RGBA -> overrides VectorParameterValues
        // entries (LinearColor.R/G/B/A). The frontend stores these as
        // a 4-element JSON array; the patcher reads via index.
        public Dictionary<string, float[]> VectorParams;

        // Textures: param-name -> texture stem (e.g. "T_QmPainting_Image").
        // The patcher rewrites the cloned MI's NameMap so the matching
        // texture ref points at the user-cooked texture (which must be
        // present in the cooked folder under that stem).
        public Dictionary<string, string> TextureParams;
    }

    // Edit-spec for a single R5BLRecipeList JSON on the Sellers side
    // (PlayerBuys rosters). Mirrors BuyerListOverride structurally; the
    // SellerPatcher applies the diff in the same order (drop vanilla refs
    // in Removed*, then append Added*). The custom-recipe prefix is
    // "QM_SCustom_*" (vs "QM_Custom_*" for buyers) so the patcher can tell
    // which side owns a given synthesized recipe id.
    public sealed class SellerListOverride
    {
        // Recipe basenames to append. "QM_SCustom_*" -> synthesized JSON
        // under /R5BusinessRules/Recipes/Custom/<id>.<id>; anything else
        // is looked up in the vanilla recipe map.
        public List<string> AddedRecipeIds;

        // Recipe basenames to strip from the vanilla RecipeList[].
        public List<string> RemovedRecipeIds;

        // Definitive output order when set. Mirrors BuyerListOverride.RecipeOrder
        // but for the seller side (QM_SCustom_* prefix for custom ids).
        // null = use legacy mode.
        public List<string> RecipeOrder;
    }
}
