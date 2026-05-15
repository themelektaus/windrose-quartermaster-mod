using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using UAssetAPI;
using UAssetAPI.UnrealTypes;
using UAssetAPI.PropertyTypes.Objects;
using UAssetAPI.PropertyTypes.Structs;
using UAssetAPI.ExportTypes;
using UAssetAPI.Unversioned;

namespace Windrose.Quartermaster.Core
{
    // Patches the PassiveReloadGPData.ReloadTime FloatProperty inside every
    // player-firearm LogicParams DataAsset, multiplying it by a user-supplied
    // scalar < 1.0 to shorten reload times. The structure is:
    //
    //   Default__DA_RangeWpn_*_LogicParams (NormalExport, R5RangeWeaponItemLogicParams)
    //   + PassiveReloadGPData (StructProperty, R5RangeWeaponPassiveReloadParams)
    //     + ReloadTime (FloatProperty, seconds)
    //
    // Vanilla reload times sit between 12 s (Pistol_Reliable) and 15 s
    // (Musket_Infantry). The patcher applies the SAME multiplier to every
    // tier so the user only needs one slider; per-variant tuning would
    // require more UI for marginal gain.
    //
    // Workflow context (mirrors PickaxeRangePatcher):
    //   game IoStore (.ucas)
    //     -> retoc to-legacy   (Zen package -> Legacy .uasset+.uexp)
    //     -> THIS CLASS        (multiply PassiveReloadGPData.ReloadTime)
    //     -> retoc to-zen      (Legacy -> IoStore triplet)
    public sealed class RangedReloadPatcher
    {
        // Bidirectional range: < 1.0 shortens reload (faster), > 1.0 lengthens
        // it (harder gameplay). 1.0 = vanilla; the GUI null-collapses at 1.0.
        public const double MinMultiplier = 0.1;
        public const double MaxMultiplier = 3.0;

        public const string PassiveReloadGPDataProp = "PassiveReloadGPData";
        public const string ReloadTimeProp          = "ReloadTime";

        // Filename stem -> virtual asset path. Covers every player firearm
        // LogicParams variant present in 5.6.
        //
        // IMPORTANT: not every weapon family ships both _Base and _Advanced
        // variants. The list below was verified against the live game paks
        // via `retoc list` - assuming a symmetric Base+Advanced matrix is
        // wrong and produced "retoc did not produce expected asset" errors.
        // Total: 19 actual LogicParams (7 pistols + 6 muskets + 5 blunderbuss).
        //   Pistols:     Blank_Base, Reliable_Base + Advanced, Rusty_Base,
        //                DrakesDoom_Base + Advanced, Corrupted_Advanced
        //   Muskets:     Blank_Base, Infantry_Base + Advanced,
        //                Reliable_Base + Advanced, Sniper_Base + Advanced
        //   Blunderbuss: Blank_Base, Reliable_Base + Advanced,
        //                Dragonbreath_Base + Advanced
        public static readonly Dictionary<string, string> WeaponAssets =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                // ----- PISTOLS -----
                { "DA_RangeWpn_Pistol_Blank_Base_LogicParams",
                  "R5/Content/Gameplay/ItemsLogic/Weapon/Wpn_OffHand/Pistol_Blank_Base/RangeWpn/DA_RangeWpn_Pistol_Blank_Base_LogicParams.uasset" },
                { "DA_RangeWpn_Pistol_Reliable_Base_LogicParams",
                  "R5/Content/Gameplay/ItemsLogic/Weapon/Wpn_OffHand/Pistol_Reliable_Base/RangeWpn/DA_RangeWpn_Pistol_Reliable_Base_LogicParams.uasset" },
                { "DA_RangeWpn_Pistol_Reliable_Advanced_LogicParams",
                  "R5/Content/Gameplay/ItemsLogic/Weapon/Wpn_OffHand/Pistol_Reliable_Advanced/RangeWpn/DA_RangeWpn_Pistol_Reliable_Advanced_LogicParams.uasset" },
                { "DA_RangeWpn_Pistol_Rusty_Base_LogicParams",
                  "R5/Content/Gameplay/ItemsLogic/Weapon/Wpn_OffHand/Pistol_Rusty_Base/RangeWpn/DA_RangeWpn_Pistol_Rusty_Base_LogicParams.uasset" },
                { "DA_RangeWpn_Pistol_DrakesDoom_Base_LogicParams",
                  "R5/Content/Gameplay/ItemsLogic/Weapon/Wpn_OffHand/Pistol_DrakesDoom_Base/RangeWpn/DA_RangeWpn_Pistol_DrakesDoom_Base_LogicParams.uasset" },
                { "DA_RangeWpn_Pistol_DrakesDoom_Advanced_LogicParams",
                  "R5/Content/Gameplay/ItemsLogic/Weapon/Wpn_OffHand/Pistol_DrakesDoom_Advanced/RangeWpn/DA_RangeWpn_Pistol_DrakesDoom_Advanced_LogicParams.uasset" },
                { "DA_RangeWpn_Pistol_Corrupted_Advanced_LogicParams",
                  "R5/Content/Gameplay/ItemsLogic/Weapon/Wpn_OffHand/Pistol_Corrupted_Advanced/RangeWpn/DA_RangeWpn_Pistol_Corrupted_Advanced_LogicParams.uasset" },

                // ----- MUSKETS -----
                { "DA_RangeWpn_Musket_Blank_Base_LogicParams",
                  "R5/Content/Gameplay/ItemsLogic/Weapon/Wpn_TwoHand/Musket_Blank_Base/RangeWpn/DA_RangeWpn_Musket_Blank_Base_LogicParams.uasset" },
                { "DA_RangeWpn_Musket_Infantry_Base_LogicParams",
                  "R5/Content/Gameplay/ItemsLogic/Weapon/Wpn_TwoHand/Musket_Infantry_Base/RangeWpn/DA_RangeWpn_Musket_Infantry_Base_LogicParams.uasset" },
                { "DA_RangeWpn_Musket_Infantry_Advanced_LogicParams",
                  "R5/Content/Gameplay/ItemsLogic/Weapon/Wpn_TwoHand/Musket_Infantry_Advanced/RangeWpn/DA_RangeWpn_Musket_Infantry_Advanced_LogicParams.uasset" },
                { "DA_RangeWpn_Musket_Reliable_Base_LogicParams",
                  "R5/Content/Gameplay/ItemsLogic/Weapon/Wpn_TwoHand/Musket_Reliable_Base/RangeWpn/DA_RangeWpn_Musket_Reliable_Base_LogicParams.uasset" },
                { "DA_RangeWpn_Musket_Reliable_Advanced_LogicParams",
                  "R5/Content/Gameplay/ItemsLogic/Weapon/Wpn_TwoHand/Musket_Reliable_Advanced/RangeWpn/DA_RangeWpn_Musket_Reliable_Advanced_LogicParams.uasset" },
                { "DA_RangeWpn_Musket_Sniper_Base_LogicParams",
                  "R5/Content/Gameplay/ItemsLogic/Weapon/Wpn_TwoHand/Musket_Sniper_Base/RangeWpn/DA_RangeWpn_Musket_Sniper_Base_LogicParams.uasset" },
                { "DA_RangeWpn_Musket_Sniper_Advanced_LogicParams",
                  "R5/Content/Gameplay/ItemsLogic/Weapon/Wpn_TwoHand/Musket_Sniper_Advanced/RangeWpn/DA_RangeWpn_Musket_Sniper_Advanced_LogicParams.uasset" },

                // ----- BLUNDERBUSS -----
                { "DA_RangeWpn_Blunderbuss_Blank_Base_LogicParams",
                  "R5/Content/Gameplay/ItemsLogic/Weapon/Wpn_TwoHand/Blunderbuss_Blank_Base/RangeWpn/DA_RangeWpn_Blunderbuss_Blank_Base_LogicParams.uasset" },
                { "DA_RangeWpn_Blunderbuss_Reliable_Base_LogicParams",
                  "R5/Content/Gameplay/ItemsLogic/Weapon/Wpn_TwoHand/Blunderbuss_Reliable_Base/RangeWpn/DA_RangeWpn_Blunderbuss_Reliable_Base_LogicParams.uasset" },
                { "DA_RangeWpn_Blunderbuss_Reliable_Advanced_LogicParams",
                  "R5/Content/Gameplay/ItemsLogic/Weapon/Wpn_TwoHand/Blunderbuss_Reliable_Advanced/RangeWpn/DA_RangeWpn_Blunderbuss_Reliable_Advanced_LogicParams.uasset" },
                { "DA_RangeWpn_Blunderbuss_Dragonbreath_Base_LogicParams",
                  "R5/Content/Gameplay/ItemsLogic/Weapon/Wpn_TwoHand/Blunderbuss_Dragonbreath_Base/RangeWpn/DA_RangeWpn_Blunderbuss_Dragonbreath_Base_LogicParams.uasset" },
                { "DA_RangeWpn_Blunderbuss_Dragonbreath_Advanced_LogicParams",
                  "R5/Content/Gameplay/ItemsLogic/Weapon/Wpn_TwoHand/Blunderbuss_Dragonbreath_Advanced/RangeWpn/DA_RangeWpn_Blunderbuss_Dragonbreath_Advanced_LogicParams.uasset" },
            };

        public Action<string> Log;

        public RangedReloadPatchResult Patch(
            string inputAssetPath, string outputAssetPath,
            string usmapPath, double multiplier)
        {
            if (string.IsNullOrEmpty(inputAssetPath))
                throw new ArgumentNullException("inputAssetPath");
            if (string.IsNullOrEmpty(outputAssetPath))
                throw new ArgumentNullException("outputAssetPath");
            if (string.IsNullOrEmpty(usmapPath))
                throw new ArgumentNullException("usmapPath");
            if (!File.Exists(inputAssetPath))
                throw new FileNotFoundException("Legacy uasset not found: " + inputAssetPath);
            if (!File.Exists(usmapPath))
                throw new FileNotFoundException("Usmap mappings not found: " + usmapPath);
            if (multiplier < MinMultiplier || multiplier > MaxMultiplier)
                throw new ArgumentOutOfRangeException("multiplier",
                    "Multiplier " + multiplier + " is outside ["
                    + MinMultiplier + ", " + MaxMultiplier
                    + "] - the GUI should have clamped this.");

            LogLine("Loading usmap: " + usmapPath);
            var mappings = new Usmap(usmapPath);
            LogLine("Loading uasset: " + inputAssetPath);
            var asset = new UAsset(inputAssetPath, EngineVersion.VER_UE5_6, mappings);

            // UE5.6 LogicParams DataAssets ship sub-component exports next to
            // the Default__DA_*_C CDO (e.g. R5DrawWeaponTaskBase_0, ability
            // task sub-objects). The first NormalExport is therefore not
            // necessarily the CDO carrying PassiveReloadGPData - locate the
            // right export by property presence instead of by index.
            var passiveName = FName.FromString(asset, PassiveReloadGPDataProp);
            NormalExport target = null;
            int targetIndex = -1;
            StructPropertyData passive = null;
            for (int i = 0; i < asset.Exports.Count; i++)
            {
                if (asset.Exports[i] is NormalExport ne)
                {
                    var match = ne.Data.OfType<StructPropertyData>()
                        .FirstOrDefault(p => p.Name == passiveName && p.Value != null);
                    if (match != null)
                    {
                        target = ne;
                        targetIndex = i;
                        passive = match;
                        break;
                    }
                }
            }
            if (target == null || passive == null)
            {
                throw new InvalidOperationException(
                    "No PassiveReloadGPData StructProperty found in any NormalExport of "
                    + inputAssetPath
                    + " - expected an R5RangeWeaponItemLogicParams DataAsset with PassiveReloadGPData.");
            }

            var reloadName = FName.FromString(asset, ReloadTimeProp);
            var reloadProp = passive.Value.OfType<FloatPropertyData>()
                .FirstOrDefault(p => p.Name == reloadName);
            if (reloadProp == null)
            {
                throw new InvalidOperationException(
                    "No ReloadTime FloatProperty inside PassiveReloadGPData on "
                    + target.ObjectName + " in " + inputAssetPath + ".");
            }

            float vanillaValue = reloadProp.Value;
            float newValue = (float)(vanillaValue * multiplier);
            reloadProp.Value = newValue;
            LogLine("Updated PassiveReloadGPData.ReloadTime: "
                + vanillaValue.ToString("0.0000", CultureInfo.InvariantCulture)
                + " -> " + newValue.ToString("0.0000", CultureInfo.InvariantCulture)
                + " (multiplier=" + multiplier.ToString("0.##", CultureInfo.InvariantCulture) + ")");

            LogLine("Writing: " + outputAssetPath);
            asset.Write(outputAssetPath);

            return new RangedReloadPatchResult
            {
                AssetStem = Path.GetFileNameWithoutExtension(inputAssetPath),
                ExportIndex = targetIndex,
                Multiplier = multiplier,
                VanillaReloadTime = vanillaValue,
                EffectiveReloadTime = newValue,
            };
        }

        void LogLine(string msg)
        {
            if (Log != null) Log(msg);
        }
    }

    public sealed class RangedReloadPatchResult
    {
        public string AssetStem;
        public int ExportIndex;
        public double Multiplier;
        public float VanillaReloadTime;
        public float EffectiveReloadTime;
    }
}
