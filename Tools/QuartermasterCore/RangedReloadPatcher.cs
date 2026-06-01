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
    public sealed class RangedReloadPatcher
    {
        public const double MinMultiplier = 0.1;
        public const double MaxMultiplier = 3.0;

        public const string PassiveReloadGPDataProp = "PassiveReloadGPData";
        public const string ReloadTimeProp          = "ReloadTime";

        // Intentionally asymmetric: not every family ships both _Base and _Advanced. Do not fill in by assuming symmetry.
        public static readonly Dictionary<string, string> WeaponAssets =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
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
            var asset = new UAsset(inputAssetPath, UAssetIo.Ue, mappings);

            // The CDO is not necessarily the first NormalExport; locate it by PassiveReloadGPData presence.
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
