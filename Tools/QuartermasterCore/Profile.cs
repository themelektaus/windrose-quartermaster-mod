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
    // unlike DA_BI), so no reference mod is shipped - the build pipeline
    // extracts the live game's Niagara assets via retoc to-legacy, runs
    // NoSmokePatcher on them, and re-packs into the IoStore composite.
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
    // The reference mod BetterMinimapRange_2x_2x_P matches Multiplier=2.0
    // exactly (the second 2x in its name refers to MiniMapShowDistance,
    // NOT MaxMapResolution which stays at the vanilla 2048).
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
    // placed building-center / bonfire. The reference mod
    // ExtendedBonfireRadius_3x_P matches Multiplier=3.0 (15000/9000); an
    // ingame probe confirmed these are the gameplay-relevant fields (the
    // BP_BuildingBlock's ScenarioOverlapSphere radius alone had no
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
    // slightly further away. Mirrors the UE4SS reference mod "Pickaxe Range"
    // but only its TraceScaleModifier axis (variant A): the shared trace
    // DataAssets and the deeply nested SectionsData/FoliagePrediction trace
    // entries are left at vanilla, since the engine multiplies them by the
    // top-level TraceScaleModifier at hit-resolution time anyway.
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
