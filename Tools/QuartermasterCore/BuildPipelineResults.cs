using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Windrose.Quartermaster.Core.BuildingCreator;
using Windrose.Quartermaster.Core.Deploy;

namespace Windrose.Quartermaster.Core
{
    public sealed class BuildPipelineResult
    {
        public Profile Profile;
        public PatchResult PatchResult;
        public LootPatchResult LootPatchResult;
        public BellLimitsPatchResult BellLimitsResult;
        public BuyerPatchResult BuyerPatchResult;
        public SellerPatchResult SellerPatchResult;
        public ItemCreatorPatchResult ItemCreatorResult;
        public PakBuildResult PakResult;
        public string PakPath;
        public PickupTripletResult PickupResult;
        public double? PickupMultiplier;
        public BuildingStabilityResult StabilityResult;
        public NoSmokeResult NoSmokeResult;
        public MinimapRangeResult MinimapResult;
        public BonfireRadiusResult BonfireResult;
        public PickaxeRangeResult PickaxeRangeResult;
        public CooldownsResult CooldownsResult;
        public ShipMusicResult ShipMusicResult;
        public ShipMusicAddResult ShipMusicAddResult;
        public BonfireMusicResult BonfireMusicResult;
        public LightingResult LightingResult;
        public CropGrowthPatchResult CropGrowthResult;
        public CookingDurationPatchResult CookingDurationResult;
        public List<BuildingPatchResult> BuildingResults;
        public string TmpDir;
        public bool Success;
    }

    public sealed class BuildingStabilityResult
    {
        public bool Enabled;
        public List<BuildingStabilityAssetResult> AssetResults;
        public string PakPath;
        public string UcasPath;
        public string UtocPath;
        public long PakSize;
        public long UcasSize;
        public long UtocSize;
    }

    public sealed class NoSmokeResult
    {
        public List<NoSmokeCategory> Categories;
        public List<NoSmokeAssetResult> AssetResults;
    }

    public sealed class NoSmokeAssetResult
    {
        public string AssetPath;
        public int TotalHandles;
        public int FlippedHandles;
    }

    public sealed class MinimapRangeResult
    {
        public bool Enabled;
        public double Multiplier;
        public MinimapRangePatchResult Patch;
        public string PakPath;
        public long PakSize;
    }

    public sealed class BonfireRadiusResult
    {
        public bool Enabled;
        public double Multiplier;
        public BonfireRadiusPatchResult Patch;
        public string PakPath;
        public string UcasPath;
        public string UtocPath;
    }

    public sealed class PickaxeRangeResult
    {
        public bool Enabled;
        public double Multiplier;
        public List<PickaxeRangePatchResult> AssetResults;
        public string PakPath;
        public string UcasPath;
        public string UtocPath;
    }

    public sealed class LightingJob
    {
        public LightingPatcher.LightInfo Info;
        public double Multiplier;
    }

    public sealed class LightingResult
    {
        public bool Enabled;
        public double OverallMultiplier;
        public List<LightingPatchResult> AssetResults;
        public string PakPath;
        public string UcasPath;
        public string UtocPath;
    }

    public enum CooldownJobShape
    {
        ScalableFloatDuration,
        TopLevelMagnitude,
        RangedReload,
        ShipCannon,
    }

    public sealed class CooldownJob
    {
        public string Family;
        public string AssetStem;
        public string VirtualPath;
        public double Multiplier;
        public CooldownJobShape Shape;
    }

    public sealed class CooldownJobResult
    {
        public string Family;
        public string AssetStem;
        public double Multiplier;
        public float VanillaValue;
        public float EffectiveValue;
        public int BatteryCount;
        public int PatchedBatteryCount;
    }

    public sealed class CooldownsResult
    {
        public bool Enabled;
        public List<CooldownJobResult> JobResults;
        public string PakPath;
        public string UcasPath;
        public string UtocPath;
    }

    public sealed class ShipMusicJob
    {
        public ShipMusicSlots.SlotInfo Slot;
        public string UserWavPath;
        public string OriginalFilename;

        // Absolute VolumeMultiplier; 0.45 = vanilla baseline (pipeline skips cue patching).
        public double UserVolume;
    }

    public sealed class ShipMusicResult
    {
        public bool Enabled;
        public List<ShipMusicPatchResult> SlotResults;
        public string PakPath;
        public string UcasPath;
        public string UtocPath;
    }

    public sealed class BonfireMusicJob
    {
        // Null when IsSynthesizedSilence is true (build generates a silence WAV).
        public string UserWavPath;
        public string OriginalFilename;
        public double UserVolume = 1.0;

        public bool IsSynthesizedSilence;
    }

    public sealed class BonfireMusicResult
    {
        public bool Enabled;
        public ShipMusicPatchResult SlotResult;
        public string PakPath;
        public string UcasPath;
        public string UtocPath;
    }
}
