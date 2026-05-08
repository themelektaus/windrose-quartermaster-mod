using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Windrose.StackSize.Core
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

        // Runtime-only flag, populated by ProfileStore after loading from
        // Profiles/_builtin/. Never written back to disk (profiles in the
        // _builtin dir simply ARE builtin).
        [JsonIgnore] public bool IsBuiltin;
    }

    public sealed class ProfileGlobals
    {
        public StackSizeGlobal StackSize;
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

    public sealed class ItemOverride
    {
        public int? StackSize;    // null = no per-item override for this property
        // future: float? Weight;
        // future: string Rarity;
    }
}
