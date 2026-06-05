using System;
using System.Collections.Generic;
using System.Linq;

namespace Windrose.Quartermaster.Core
{
    // Single source of truth for the "Deposit visuals" feature (the Iron / Sulfur
    // "Visual Tweak" reference mods). Each reference mod patches ONE deposit
    // MaterialInstanceConstant and re-points its "Albedo" texture parameter at a
    // DIFFERENT texture that already ships in the game - no new texture asset is
    // bundled. We generalise that: per deposit the user enables a swap and picks
    // which stock texture becomes the base colour (default = the reference choice).
    //
    // The texture pool is grounded in the game AssetRegistry (deposit-native
    // albedos + a few vivid metal tiles), so every option resolves at runtime.
    public static class DepositVisualCatalog
    {
        public sealed class TextureOption
        {
            public string Key;          // stable id persisted in the profile
            public string Label;        // UI label
            public string Stem;         // FName written into the MI import (e.g. "T_Gold_A")
            public string PackagePath;  // full /Game/... package path of the Texture2D
        }

        public sealed class DepositTarget
        {
            public string Key;               // "iron" / "sulfur"
            public string Label;             // UI label
            public string AssetStem;         // retoc --filter stem + legacy file stem
            public string AssetVirtualPath;  // legacy uasset path under the staging root
            public string ParamName;         // texture parameter to re-point ("Albedo")
            public string VanillaTextureKey; // option that equals the untouched game look (no-op)
            public string DefaultTextureKey; // the reference-mod choice (UI default)
        }

        // Verified against the game AssetRegistry (47k assets). Stems/paths are the
        // exact Texture2D packages; the patcher writes these into the MI import.
        public static readonly IReadOnlyList<TextureOption> Textures = new[]
        {
            new TextureOption { Key = "iron",            Label = "Iron ore (vanilla iron)",       Stem = "T_SourceIron_A",                  PackagePath = "/Game/Environment/Deposits/Iron/Textures/T_SourceIron_A" },
            new TextureOption { Key = "sulfur",          Label = "Sulfur (yellow)",               Stem = "T_SourceSulfur_A",                PackagePath = "/Game/Environment/Deposits/Sulfur/Textures/T_SourceSulfur_A" },
            new TextureOption { Key = "rock",            Label = "Rock / stone (vanilla sulfur)", Stem = "T_RockSmooth_AH",                 PackagePath = "/Game/Environment/Deposits/Rocks/Textures/T_RockSmooth_AH" },
            new TextureOption { Key = "mushroom_bright", Label = "Mushroom (bright)",             Stem = "T_MushroomCluster_Stump_01_MTRM", PackagePath = "/Game/Environment/Deposits/Mushrooms/MushroomCluster/Textures/T_MushroomCluster_Stump_01_MTRM" },
            new TextureOption { Key = "mushroom",        Label = "Mushroom",                      Stem = "T_MushroomCluster_Stump_01_A",    PackagePath = "/Game/Environment/Deposits/Mushrooms/MushroomCluster/Textures/T_MushroomCluster_Stump_01_A" },
            new TextureOption { Key = "salt",            Label = "Salt (white)",                  Stem = "T_Source_Salt_01_A",              PackagePath = "/Game/Environment/Deposits/Salt/Textures/T_Source_Salt_01_A" },
            new TextureOption { Key = "clay",            Label = "Clay",                          Stem = "T_Clay_02_A",                     PackagePath = "/Game/Environment/Deposits/Clay/Textures/T_Clay_02_A" },
            new TextureOption { Key = "clay_weathered",  Label = "Clay (weathered)",              Stem = "T_Clay_04_A",                     PackagePath = "/Game/Environment/Deposits/Clay/Textures/T_Clay_04_A" },
            new TextureOption { Key = "gold",            Label = "Gold (vivid)",                  Stem = "T_Gold_A",                        PackagePath = "/Game/Environment/Shaders/Textures/Tile/Metal/T_Gold_A" },
            new TextureOption { Key = "copper",          Label = "Copper",                        Stem = "T_Copper_A",                      PackagePath = "/Game/Environment/Shaders/Textures/Tile/Metal/T_Copper_A" },
            new TextureOption { Key = "bronze",          Label = "Bronze",                        Stem = "T_Bronze_01_A",                   PackagePath = "/Game/Environment/Shaders/Textures/Tile/Metal/T_Bronze_01_A" },
            new TextureOption { Key = "cast_iron",       Label = "Cast iron (dark)",              Stem = "T_CastIron_A",                    PackagePath = "/Game/Environment/Shaders/Textures/Tile/Metal/T_CastIron_A" },
        };

        // NOTE the path quirk: iron lives under ".../Iron/Materials/" (plural) while
        // sulfur lives under ".../Sulfur/Material/" (singular) - both confirmed by
        // retoc to-legacy of the reference mods.
        public static readonly IReadOnlyList<DepositTarget> Deposits = new[]
        {
            new DepositTarget
            {
                Key = "iron",
                Label = "Iron deposits",
                AssetStem = "MI_Source_Iron_01",
                AssetVirtualPath = "R5/Content/Environment/Deposits/Iron/Materials/MI_Source_Iron_01.uasset",
                ParamName = "Albedo",
                VanillaTextureKey = "iron",
                DefaultTextureKey = "mushroom_bright",
            },
            new DepositTarget
            {
                Key = "sulfur",
                Label = "Sulfur deposits",
                AssetStem = "MI_Rock_Sulfur_01",
                AssetVirtualPath = "R5/Content/Environment/Deposits/Sulfur/Material/MI_Rock_Sulfur_01.uasset",
                ParamName = "Albedo",
                VanillaTextureKey = "rock",
                DefaultTextureKey = "sulfur",
            },
        };

        public static TextureOption FindTexture(string key) =>
            key == null ? null : Textures.FirstOrDefault(t => string.Equals(t.Key, key, StringComparison.OrdinalIgnoreCase));

        public static DepositTarget FindDeposit(string key) =>
            key == null ? null : Deposits.FirstOrDefault(d => string.Equals(d.Key, key, StringComparison.OrdinalIgnoreCase));
    }
}
