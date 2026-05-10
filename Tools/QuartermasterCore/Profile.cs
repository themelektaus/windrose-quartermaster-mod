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
    }

    public sealed class ProfileGlobals
    {
        public StackSizeGlobal StackSize;
        public LootGlobal Loot;
        public PickupRadiusGlobal PickupRadius;
        public FastTravelBellsGlobal FastTravelBells;
        public BuildingStabilityGlobal BuildingStability;
        public NoSmokeGlobal NoSmoke;
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
    // Pak1 -- no IoStore triplet, no retoc step. The vanilla file has
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

    // "Enhanced building stability" toggle. When enabled, ships the 787
    // pre-cooked DA_BI* DataAssets from the BetterStructureSupport
    // reference mod (References/BetterStructureSupport_P.{pak,ucas,utoc})
    // verbatim, alongside any other IoStore content for this profile.
    //
    // We do NOT patch the values ourselves because vanilla DA_BI assets
    // serialize via UE5's tag-stream format that UAssetAPI cannot
    // decode (loads as RawExport). The reference mod's variants ARE
    // parseable but for a single on/off toggle we don't need to patch
    // them anyway -- the bundled values (BlockWeight=0, MaxLoad=1e7,
    // MinIntersection=0) match the "buildings never collapse" UX.
    //
    // null OR Enabled=false -> no stability assets shipped for this
    // profile; the IoStore output omits the stability source entirely.
    public sealed class BuildingStabilityGlobal
    {
        public bool? Enabled;
    }

    // "No smoke" visual tweak: hides the smoke / flame Niagara FX on
    // campfires, furnaces and kilns by setting every EmitterHandle's
    // bIsEnabled = false on the corresponding NiagaraSystem export.
    //
    // Self-baked from vanilla via UAssetAPI (Niagara assets parse cleanly
    // unlike DA_BI), so no reference mod is shipped -- the build pipeline
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
        // VANILLA list before any removal -- the patcher reconciles.
        public Dictionary<int, LootEntryEdit> Entries;

        // Vanilla-indices that should NOT appear in the output. Combined
        // with Entries: an index can appear in both, in which case Removed
        // wins (the entry is skipped, the edit is ignored).
        public List<int> Removed;

        // Brand-new entries appended after the surviving vanilla entries
        // (in declaration order). Always full schema -- there's no vanilla
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
}
