using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Windrose.Quartermaster.Core;
using Windrose.Quartermaster.Core.BuildingCreator;

namespace Windrose.Quartermaster.Web.Endpoints;

public static class BuildEndpoint
{
    public static void Map(WebApplication app, string repoRoot)
    {
        var paths = WindrosePaths.FromModRoot(repoRoot);
        var store = new ProfileStore(paths);

        app.MapPost("/api/build", async (HttpRequest req) =>
        {
            BuildRequestDto body;
            try
            {
                body = await JsonSerializer.DeserializeAsync<BuildRequestDto>(
                    req.Body, ProfileStore.JsonOpts);
            }
            catch (Exception ex)
            {
                return Results.BadRequest(new { error = "Invalid JSON: " + ex.Message });
            }

            if (body == null || string.IsNullOrEmpty(body.ProfileId))
                return Results.BadRequest(new { error = "profileId is required" });

            var profile = store.Load(body.ProfileId);
            if (profile == null)
                return Results.NotFound(new { error = "Profile not found", id = body.ProfileId });

            var log = new List<string>();
            var pipeline = new BuildPipeline(paths);
            pipeline.Log = m => log.Add(m);
            pipeline.GamePaksDirProvider = SteamLocator.FindVanillaPaksDir;

            try
            {
                pipeline.BuildingTemplateCatalog = BuildingTemplatesEndpoint.GetSharedCatalog();
            }
            catch (Exception ex)
            {
                log.Add("[ERR] Building template catalog bootstrap failed: " + ex.Message);
            }

            try
            {
                pipeline.OutputDir = SteamLocator.FindModsDir();
            }
            catch (Exception ex)
            {
                return Results.Json(new
                {
                    success = false,
                    error = "Could not locate Windrose ~mods folder: " + ex.Message,
                    log,
                }, statusCode: 500);
            }

            try
            {
                var result = await Task.Run(() => pipeline.Build(profile, keepTemp: body.KeepTemp));
                object lootPatchResult = null;
                if (result.LootPatchResult != null)
                {
                    var lpr = result.LootPatchResult;
                    lootPatchResult = new
                    {
                        scanned = lpr.Scanned,
                        unchangedSkip = lpr.UnchangedSkip,
                        noSchema = lpr.NoSchema,
                        written = lpr.Written,
                        multiplierApplied = lpr.MultiplierApplied,
                        edited = lpr.Edited,
                        removed = lpr.Removed,
                        added = lpr.Added,
                        warnings = lpr.Warnings,
                    };
                }
                object pickupRadiusInfo = null;
                if (result.PickupResult != null)
                {
                    var pr = result.PickupResult;
                    pickupRadiusInfo = new
                    {
                        pakPath = pr.PakPath,
                        ucasPath = pr.UcasPath,
                        utocPath = pr.UtocPath,
                        pakSize = pr.PakSize,
                        ucasSize = pr.UcasSize,
                        utocSize = pr.UtocSize,
                        magnetRadius = pr.MagnetRadius,
                        multiplier = result.PickupMultiplier,
                    };
                }
                object bellLimitsInfo = null;
                if (result.BellLimitsResult != null)
                {
                    var br = result.BellLimitsResult;
                    bellLimitsInfo = new
                    {
                        skipped = br.Skipped,
                        written = br.Written,
                        bellCap = br.BellCap,
                        signalFireCap = br.SignalFireCap,
                        bellsPatched = br.BellsPatched,
                        signalFiresPatched = br.SignalFiresPatched,
                        unmatched = br.Unmatched,
                    };
                }
                object equipmentSlotsInfo = null;
                if (result.EquipmentSlotsResult != null)
                {
                    var er = result.EquipmentSlotsResult;
                    equipmentSlotsInfo = new
                    {
                        skipped = er.Skipped,
                        written = er.Written,
                        ringSlots = er.RingSlots,
                        necklaceSlots = er.NecklaceSlots,
                        ringPatched = er.RingPatched,
                        necklacePatched = er.NecklacePatched,
                    };
                }
                object shipSlotsInfo = null;
                if (result.ShipSlotsResult != null)
                {
                    var sr = result.ShipSlotsResult;
                    shipSlotsInfo = new
                    {
                        skipped = sr.Skipped,
                        written = sr.Written,
                        cargoMultiplier = sr.CargoMultiplier,
                        combatOrderSlots = sr.CombatOrderSlots,
                        filesWritten = sr.FilesWritten,
                    };
                }
                object buildingStabilityInfo = null;
                if (result.StabilityResult != null)
                {
                    buildingStabilityInfo = new
                    {
                        enabled = result.StabilityResult.Enabled,
                    };
                }
                object minimapRangeInfo = null;
                if (result.MinimapResult != null)
                {
                    var mr = result.MinimapResult;
                    minimapRangeInfo = new
                    {
                        multiplier = mr.Multiplier,
                        pakPath = mr.PakPath,
                        pakSize = mr.PakSize,
                        vanilla = new
                        {
                            footBrush = mr.Patch.VanillaFootBrush,
                            footDistance = mr.Patch.VanillaFootDistance,
                            shipBrush = mr.Patch.VanillaShipBrush,
                            shipDistance = mr.Patch.VanillaShipDistance,
                        },
                        effective = new
                        {
                            footBrush = mr.Patch.EffectiveFootBrush,
                            footDistance = mr.Patch.EffectiveFootDistance,
                            shipBrush = mr.Patch.EffectiveShipBrush,
                            shipDistance = mr.Patch.EffectiveShipDistance,
                        },
                    };
                }
                object noFogInfo = null;
                if (result.NoFogResult != null)
                {
                    noFogInfo = new
                    {
                        enabled = result.NoFogResult.Enabled,
                    };
                }
                object landFastTravelInfo = null;
                if (result.LandFastTravelResult != null)
                {
                    landFastTravelInfo = new
                    {
                        enabled = result.LandFastTravelResult.Enabled,
                        assetsReplaced = result.LandFastTravelResult.AssetsReplaced,
                    };
                }
                object bonfireRadiusInfo = null;
                if (result.BonfireResult != null)
                {
                    var br = result.BonfireResult;
                    bonfireRadiusInfo = new
                    {
                        multiplier = br.Multiplier,
                        ucasPath = br.UcasPath,
                        utocPath = br.UtocPath,
                        vanilla = new
                        {
                            influenceRadius = br.Patch != null ? br.Patch.VanillaInfluenceRadius : 0f,
                            influenceHeight = br.Patch != null ? br.Patch.VanillaInfluenceHeight : 0f,
                        },
                        effective = new
                        {
                            influenceRadius = br.Patch != null ? br.Patch.EffectiveInfluenceRadius : 0f,
                            influenceHeight = br.Patch != null ? br.Patch.EffectiveInfluenceHeight : 0f,
                        },
                    };
                }
                object pickaxeRangeInfo = null;
                if (result.PickaxeRangeResult != null)
                {
                    var pr = result.PickaxeRangeResult;
                    pickaxeRangeInfo = new
                    {
                        multiplier = pr.Multiplier,
                        ucasPath = pr.UcasPath,
                        utocPath = pr.UtocPath,
                        tiers = pr.AssetResults == null
                            ? null
                            : pr.AssetResults.Select(ar => new
                            {
                                stem = ar.AssetStem,
                                vanilla = ar.VanillaTraceScaleModifier,
                                effective = ar.EffectiveTraceScaleModifier,
                                added = ar.Added,
                            }).ToArray(),
                    };
                }
                object lightingInfo = null;
                if (result.LightingResult != null)
                {
                    var lr = result.LightingResult;
                    lightingInfo = new
                    {
                        overallMultiplier = lr.OverallMultiplier,
                        ucasPath = lr.UcasPath,
                        utocPath = lr.UtocPath,
                        lights = lr.AssetResults == null
                            ? null
                            : lr.AssetResults.Select(ar => new
                            {
                                stem = ar.Stem,
                                multiplier = ar.Multiplier,
                                vanilla = ar.VanillaAttenuationRadius,
                                effective = ar.EffectiveAttenuationRadius,
                            }).ToArray(),
                    };
                }
                object shipSpeedInfo = null;
                if (result.ShipSpeedResult != null)
                {
                    var ss = result.ShipSpeedResult;
                    shipSpeedInfo = new
                    {
                        overallMultiplier = ss.OverallMultiplier,
                        ucasPath = ss.UcasPath,
                        utocPath = ss.UtocPath,
                        curves = ss.AssetResults == null
                            ? null
                            : ss.AssetResults.Select(ar => new
                            {
                                stem = ar.Stem,
                                shipType = ar.ShipType,
                                role = ar.Role,
                                multiplier = ar.Multiplier,
                                vanilla = ar.VanillaMaxValue,
                                effective = ar.EffectiveMaxValue,
                            }).ToArray(),
                    };
                }
                object cooldownsInfo = null;
                if (result.CooldownsResult != null)
                {
                    var cd = result.CooldownsResult;
                    var families = (cd.JobResults == null
                        ? Enumerable.Empty<CooldownJobResult>()
                        : cd.JobResults)
                        .GroupBy(j => j.Family ?? "")
                        .Select(g =>
                        {
                            var first = g.First();
                            return new
                            {
                                family = g.Key,
                                multiplier = first.Multiplier,
                                assetCount = g.Count(),
                                vanilla = first.VanillaValue,
                                effective = first.EffectiveValue,
                                batteryCount = g.Sum(x => x.BatteryCount),
                                patchedBatteryCount = g.Sum(x => x.PatchedBatteryCount),
                            };
                        })
                        .ToArray();
                    cooldownsInfo = new
                    {
                        ucasPath = cd.UcasPath,
                        utocPath = cd.UtocPath,
                        families,
                    };
                }
                object shipMusicInfo = null;
                if (result.ShipMusicResult != null
                    && result.ShipMusicResult.SlotResults != null
                    && result.ShipMusicResult.SlotResults.Count > 0)
                {
                    var sm = result.ShipMusicResult;
                    shipMusicInfo = new
                    {
                        ucasPath = sm.UcasPath,
                        utocPath = sm.UtocPath,
                        slots = sm.SlotResults.Select(s => new
                        {
                            stem = s.SlotStem,
                            title = s.SlotTitle,
                            originalFilename = s.OriginalFilename,
                            sampleRate = s.SampleRate,
                            numChannels = s.NumChannels,
                            durationSeconds = s.DurationSeconds,
                            ubulkSize = s.UbulkSize,
                            diagnostic = s.FormatDiagnostic(),
                        }).ToArray(),
                    };
                }
                object shipMusicAddInfo = null;
                bool hasAddTracks = result.ShipMusicAddResult != null
                    && result.ShipMusicAddResult.TrackResults != null
                    && result.ShipMusicAddResult.TrackResults.Count > 0;
                bool hasExcludes = result.ShipMusicAddResult != null
                    && result.ShipMusicAddResult.ExcludedSlotIndices != null
                    && result.ShipMusicAddResult.ExcludedSlotIndices.Count > 0;
                if (hasAddTracks || hasExcludes)
                {
                    var sma = result.ShipMusicAddResult;
                    var excludedSlots = (sma.ExcludedSlotIndices ?? new List<int>())
                        .Where(idx => idx >= 0 && idx < ShipMusicSlots.All.Count)
                        .Select(idx => new
                        {
                            index = idx,
                            stem = ShipMusicSlots.All[idx].Stem,
                            title = ShipMusicSlots.All[idx].Title,
                        })
                        .ToArray();
                    shipMusicAddInfo = new
                    {
                        ucasPath = sma.UcasPath,
                        utocPath = sma.UtocPath,
                        tracks = (sma.TrackResults ?? new List<ShipMusicAddTrackResult>()).Select(t => new
                        {
                            trackKey = t.TrackKey,
                            newIndex = t.NewIndex,
                            title = t.Title,
                            originalFilename = t.OriginalFilename,
                            swavStem = t.SwavStem,
                            swavVirtualPath = t.SwavVirtualPath,
                            binkBytes = t.BinkBytes,
                            durationSeconds = t.DurationSeconds,
                            sampleRate = t.SampleRate,
                            channels = t.Channels,
                            cueStems = t.CueStemsCreated == null ? null : t.CueStemsCreated.ToArray(),
                        }).ToArray(),
                        excludedSlots,
                    };
                }
                object bonfireMusicInfo = null;
                if (result.BonfireMusicResult != null
                    && result.BonfireMusicResult.SlotResult != null)
                {
                    var bm = result.BonfireMusicResult;
                    var s = bm.SlotResult;
                    bonfireMusicInfo = new
                    {
                        ucasPath = bm.UcasPath,
                        utocPath = bm.UtocPath,
                        stem = s.SlotStem,
                        title = s.SlotTitle,
                        originalFilename = s.OriginalFilename,
                        sampleRate = s.SampleRate,
                        numChannels = s.NumChannels,
                        durationSeconds = s.DurationSeconds,
                        ubulkSize = s.UbulkSize,
                        diagnostic = s.FormatDiagnostic(),
                    };
                }
                object cropGrowthInfo = null;
                if (result.CropGrowthResult != null && result.CropGrowthResult.Written > 0)
                {
                    var cg = result.CropGrowthResult;
                    var first = cg.PatchedCrops != null && cg.PatchedCrops.Count > 0
                        ? cg.PatchedCrops[0]
                        : null;
                    cropGrowthInfo = new
                    {
                        multiplier = cg.Multiplier,
                        cropCount = cg.Written,
                        sampleVanillaTicks  = first != null ? first.VanillaTicks   : 0L,
                        sampleEffectiveTicks = first != null ? first.EffectiveTicks : 0L,
                    };
                }
                object cookingDurationInfo = null;
                if (result.CookingDurationResult != null
                    && result.CookingDurationResult.FamilySummaries != null
                    && result.CookingDurationResult.FamilySummaries.Count > 0)
                {
                    var cd2 = result.CookingDurationResult;
                    var familyArr = cd2.FamilySummaries.Values
                        .OrderBy(f => f.Family.ToString())
                        .Select(f => new
                        {
                            family = f.Family.ToString(),
                            multiplier = f.Multiplier,
                            assetCount = f.AssetCount,
                            vanillaAvg   = f.AssetCount > 0 ? f.VanillaSum   / f.AssetCount : 0.0,
                            effectiveAvg = f.AssetCount > 0 ? f.EffectiveSum / f.AssetCount : 0.0,
                        })
                        .ToArray();
                    cookingDurationInfo = new
                    {
                        totalPatched = cd2.Written,
                        mergedWithTrade = cd2.MergedWithTrade,
                        families = familyArr,
                    };
                }
                object customBuildingsInfo = null;
                if (result.BuildingResults != null && result.BuildingResults.Count > 0)
                {
                    customBuildingsInfo = new
                    {
                        count = result.BuildingResults.Count,
                        items = result.BuildingResults.Select(b => new
                        {
                            buildingId = b.BuildingId,
                            templateId = b.TemplateId,
                            outputDaStem = b.OutputDaStem,
                            outputDaPath = b.OutputDaPath,
                            stagedFileCount = b.StagedFiles != null ? b.StagedFiles.Count : 0,
                            warningCount = b.Warnings != null ? b.Warnings.Count : 0,
                            warnings = b.Warnings,
                        }).ToArray(),
                    };
                }
                object noSmokeInfo = null;
                if (result.NoSmokeResult != null)
                {
                    var ns = result.NoSmokeResult;
                    int totalFlipped = 0;
                    if (ns.AssetResults != null)
                    {
                        foreach (var ar in ns.AssetResults) totalFlipped += ar.FlippedHandles;
                    }
                    noSmokeInfo = new
                    {
                        categories = ns.Categories == null
                            ? new string[0]
                            : ns.Categories.Select(c => c.ToString()).ToArray(),
                        assetCount = ns.AssetResults == null ? 0 : ns.AssetResults.Count,
                        flippedHandles = totalFlipped,
                        assets = ns.AssetResults == null
                            ? null
                            : ns.AssetResults.Select(ar => new
                            {
                                path = ar.AssetPath,
                                totalHandles = ar.TotalHandles,
                                flippedHandles = ar.FlippedHandles,
                            }).ToArray(),
                    };
                }
                return Results.Json(new
                {
                    success = true,
                    pakPath = result.PakPath,
                    sizeBytes = result.PakResult != null ? result.PakResult.SizeBytes : 0L,
                    fileCount = result.PakResult != null ? result.PakResult.FileCount : 0,
                    patchResult = new
                    {
                        scanned = result.PatchResult.Scanned,
                        excluded = result.PatchResult.Excluded,
                        noSchema = result.PatchResult.NoSchema,
                        skipped = result.PatchResult.Skipped,
                        unchangedSkip = result.PatchResult.UnchangedSkip,
                        written = result.PatchResult.Written,
                        promoted = result.PatchResult.Promoted,
                        overridden = result.PatchResult.Overridden,
                        capped = result.PatchResult.Capped,
                    },
                    lootPatchResult,
                    pickupRadius = pickupRadiusInfo,
                    bellLimits = bellLimitsInfo,
                    equipmentSlots = equipmentSlotsInfo,
                    shipSlots = shipSlotsInfo,
                    buildingStability = buildingStabilityInfo,
                    noSmoke = noSmokeInfo,
                    minimapRange = minimapRangeInfo,
                    noFog = noFogInfo,
                    landFastTravel = landFastTravelInfo,
                    bonfireRadius = bonfireRadiusInfo,
                    pickaxeRange = pickaxeRangeInfo,
                    cooldowns = cooldownsInfo,
                    shipMusic = shipMusicInfo,
                    shipMusicAdd = shipMusicAddInfo,
                    bonfireMusic = bonfireMusicInfo,
                    lighting = lightingInfo,
                    shipSpeed = shipSpeedInfo,
                    cropGrowth = cropGrowthInfo,
                    cookingDuration = cookingDurationInfo,
                    customBuildings = customBuildingsInfo,
                    log,
                });
            }
            catch (Exception ex)
            {
                return Results.Json(new
                {
                    success = false,
                    error = ex.Message,
                    log,
                }, statusCode: 500);
            }
        });
    }

    public sealed class BuildRequestDto
    {
        public string ProfileId;
        public bool KeepTemp;
    }
}
