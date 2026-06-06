using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Windrose.Quartermaster.Core.BuildingCreator;
using Windrose.Quartermaster.Core.Deploy;

namespace Windrose.Quartermaster.Core
{
    public sealed partial class BuildPipeline
    {
        static double ResolvePickupMultiplier(Profile profile)
        {
            if (profile.Globals == null || profile.Globals.PickupRadius == null) return 1.0;
            var pr = profile.Globals.PickupRadius;
            if (pr.Multiplier.HasValue) return pr.Multiplier.Value;
            return 1.0;
        }

        static double ResolveShipPickupMultiplier(Profile profile)
        {
            if (profile.Globals == null || profile.Globals.ShipPickup == null) return 1.0;
            var sp = profile.Globals.ShipPickup;
            if (sp.Multiplier.HasValue) return sp.Multiplier.Value;
            return 1.0;
        }

        static double ResolveCropOverlapMultiplier(Profile profile)
        {
            if (profile.Globals == null || profile.Globals.CropOverlap == null) return 1.0;
            var co = profile.Globals.CropOverlap;
            if (co.Multiplier.HasValue) return co.Multiplier.Value;
            return 1.0;
        }

        static double ResolvePlayerStatsHealthMultiplier(Profile profile)
        {
            if (profile.Globals == null || profile.Globals.PlayerStats == null) return 1.0;
            var ps = profile.Globals.PlayerStats;
            if (ps.HealthMultiplier.HasValue) return ps.HealthMultiplier.Value;
            return 1.0;
        }

        static double ResolvePlayerStatsStaminaMultiplier(Profile profile)
        {
            if (profile.Globals == null || profile.Globals.PlayerStats == null) return 1.0;
            var ps = profile.Globals.PlayerStats;
            if (ps.StaminaMultiplier.HasValue) return ps.StaminaMultiplier.Value;
            return 1.0;
        }

        static bool ResolveStabilityEnabled(Profile profile)
        {
            var bs = profile.Globals != null ? profile.Globals.BuildingStability : null;
            if (bs == null) return false;
            return bs.Enabled.GetValueOrDefault(false);
        }

        static double ResolveMinimapMultiplier(Profile profile)
        {
            if (profile.Globals == null || profile.Globals.MinimapRange == null) return 1.0;
            var mr = profile.Globals.MinimapRange;
            if (mr.Multiplier.HasValue) return mr.Multiplier.Value;
            return 1.0;
        }

        static bool ResolveNoFogEnabled(Profile profile)
        {
            var nf = profile.Globals != null ? profile.Globals.NoFog : null;
            if (nf == null) return false;
            return nf.Enabled.GetValueOrDefault(false);
        }

        static bool ResolvePersistentLootEnabled(Profile profile)
        {
            var pl = profile.Globals != null ? profile.Globals.PersistentLoot : null;
            if (pl == null) return false;
            return pl.Enabled.GetValueOrDefault(false);
        }

        static bool ResolveKeepStatusEnabled(Profile profile)
        {
            var ks = profile.Globals != null ? profile.Globals.KeepStatus : null;
            if (ks == null) return false;
            return ks.Enabled.GetValueOrDefault(false);
        }

        static bool ResolveLandFastTravelEnabled(Profile profile)
        {
            var lft = profile.Globals != null ? profile.Globals.LandFastTravel : null;
            if (lft == null) return false;
            return lft.Enabled.GetValueOrDefault(false);
        }

        static double ResolveBonfireMultiplier(Profile profile)
        {
            if (profile.Globals == null || profile.Globals.BonfireRadius == null) return 1.0;
            var br = profile.Globals.BonfireRadius;
            if (br.Multiplier.HasValue) return br.Multiplier.Value;
            return 1.0;
        }

        static double ResolvePickaxeRangeMultiplier(Profile profile)
        {
            if (profile.Globals == null || profile.Globals.PickaxeRange == null) return 1.0;
            var pr = profile.Globals.PickaxeRange;
            if (pr.Multiplier.HasValue) return pr.Multiplier.Value;
            return 1.0;
        }

        static double ResolveLightingOverallMultiplier(Profile profile)
        {
            if (profile.Globals == null || profile.Globals.Lighting == null) return 1.0;
            var lg = profile.Globals.Lighting;
            if (lg.OverallMultiplier.HasValue) return lg.OverallMultiplier.Value;
            return 1.0;
        }

        static double ResolveLightingMultiplierFor(Profile profile, string stem)
        {
            if (profile.Globals == null || profile.Globals.Lighting == null) return 1.0;
            var lg = profile.Globals.Lighting;
            double overall = lg.OverallMultiplier.HasValue ? lg.OverallMultiplier.Value : 1.0;
            if (lg.Overrides != null && stem != null)
            {
                foreach (var kv in lg.Overrides)
                {
                    if (string.Equals(kv.Key, stem, StringComparison.OrdinalIgnoreCase))
                    {
                        // A 1.0 override means "follow the overall multiplier".
                        if (Math.Abs(kv.Value - 1.0) > 1e-9) return kv.Value;
                        break;
                    }
                }
            }
            return overall;
        }

        static List<LightingJob> ResolveLightingJobs(Profile profile)
        {
            var jobs = new List<LightingJob>();
            if (profile == null || profile.Globals == null || profile.Globals.Lighting == null)
                return jobs;
            foreach (var info in LightingPatcher.Lights)
            {
                double m = ResolveLightingMultiplierFor(profile, info.Stem);
                if (Math.Abs(m - 1.0) < 1e-9) continue;
                if (m < LightingPatcher.MinMultiplier || m > LightingPatcher.MaxMultiplier) continue;
                jobs.Add(new LightingJob { Info = info, Multiplier = m });
            }
            return jobs;
        }

        static double ResolveShipSpeedOverallMultiplier(Profile profile)
        {
            if (profile.Globals == null || profile.Globals.ShipSpeed == null) return 1.0;
            var ss = profile.Globals.ShipSpeed;
            if (ss.OverallMultiplier.HasValue) return ss.OverallMultiplier.Value;
            return 1.0;
        }

        static double ResolveShipSpeedMultiplierFor(Profile profile, string stem)
        {
            if (profile.Globals == null || profile.Globals.ShipSpeed == null) return 1.0;
            var ss = profile.Globals.ShipSpeed;
            double overall = ss.OverallMultiplier.HasValue ? ss.OverallMultiplier.Value : 1.0;
            if (ss.Overrides != null && stem != null)
            {
                foreach (var kv in ss.Overrides)
                {
                    if (string.Equals(kv.Key, stem, StringComparison.OrdinalIgnoreCase))
                    {
                        // A 1.0 override means "follow the overall multiplier".
                        if (Math.Abs(kv.Value - 1.0) > 1e-9) return kv.Value;
                        break;
                    }
                }
            }
            return overall;
        }

        static List<ShipSpeedJob> ResolveShipSpeedJobs(Profile profile)
        {
            var jobs = new List<ShipSpeedJob>();
            if (profile == null || profile.Globals == null || profile.Globals.ShipSpeed == null)
                return jobs;
            foreach (var info in ShipSpeedPatcher.Curves)
            {
                double m = ResolveShipSpeedMultiplierFor(profile, info.Stem);
                if (Math.Abs(m - 1.0) < 1e-9) continue;
                if (m < ShipSpeedPatcher.MinMultiplier || m > ShipSpeedPatcher.MaxMultiplier) continue;
                jobs.Add(new ShipSpeedJob { Info = info, Multiplier = m });
            }
            return jobs;
        }

        static double ResolveCropGrowthMultiplier(Profile profile)
        {
            var pt = profile.Globals != null ? profile.Globals.ProductionTimes : null;
            if (pt == null) return 1.0;
            if (pt.CropGrowthMultiplier.HasValue) return pt.CropGrowthMultiplier.Value;
            return 1.0;
        }

        static double ResolveShipCannonMultiplier(Profile profile)
        {
            var cd = profile.Globals != null ? profile.Globals.Cooldowns : null;
            if (cd == null) return 1.0;
            if (cd.ShipCannonMultiplier.HasValue) return cd.ShipCannonMultiplier.Value;
            return 1.0;
        }

        static double ResolveShipCannonRangeMultiplier(Profile profile)
        {
            var cd = profile.Globals != null ? profile.Globals.Cooldowns : null;
            if (cd == null) return 1.0;
            if (cd.ShipCannonRangeMultiplier.HasValue) return cd.ShipCannonRangeMultiplier.Value;
            return 1.0;
        }

        // The enabled fine rotation steps (subset of {1,5,10}), ascending. Empty =
        // feature off. Purely additive: vanilla steps are merged in by the patcher.
        static List<int> ResolveBuildingRotationFineSteps(Profile profile)
        {
            var steps = new List<int>();
            var br = profile.Globals != null ? profile.Globals.BuildingRotation : null;
            if (br == null) return steps;
            if (br.Add1.GetValueOrDefault(false)) steps.Add(1);
            if (br.Add5.GetValueOrDefault(false)) steps.Add(5);
            if (br.Add10.GetValueOrDefault(false)) steps.Add(10);
            return steps;
        }

        static CookingDurationPatcher.FamilyMultipliers ResolveCookingFamilies(Profile profile)
        {
            var pt = profile.Globals != null ? profile.Globals.ProductionTimes : null;
            if (pt == null) return null;
            return new CookingDurationPatcher.FamilyMultipliers
            {
                Smelting     = pt.SmeltingMultiplier,
                Kiln         = pt.KilnMultiplier,
                Tanning      = pt.TanningMultiplier,
                Milling      = pt.MillingMultiplier,
                BuildingBits = pt.BuildingBitsMultiplier,
                Decoration   = pt.DecorationMultiplier,
                ArmorWeapon  = pt.ArmorWeaponMultiplier,
                TradeOutpost = pt.TradeOutpostMultiplier,
                Other        = pt.OtherMultiplier,
            };
        }

        static List<CooldownJob> ResolveCooldownJobs(Profile profile)
        {
            var jobs = new List<CooldownJob>();
            var cd = profile.Globals != null ? profile.Globals.Cooldowns : null;
            if (cd == null) return jobs;

            if (HasCooldownMultiplier(cd.ElixirMultiplier))
            {
                AddScalableFloatJobs(jobs, CooldownsPatcher.ElixirAssets,
                    cd.ElixirMultiplier.Value, "elixir");
            }
            if (HasCooldownMultiplier(cd.MedicineMultiplier))
            {
                AddTopLevelMagnitudeJobs(jobs, CooldownsPatcher.MedicineAssets,
                    cd.MedicineMultiplier.Value, "medicine");
            }
            if (HasCooldownMultiplier(cd.RecallMultiplier))
            {
                AddTopLevelMagnitudeJobs(jobs, CooldownsPatcher.RecallAssets,
                    cd.RecallMultiplier.Value, "recall");
            }
            if (HasCooldownMultiplier(cd.ShipRepairKitMultiplier))
            {
                AddScalableFloatJobs(jobs, CooldownsPatcher.ShipRepairKitAssets,
                    cd.ShipRepairKitMultiplier.Value, "ship-repair-kit");
            }
            if (HasCooldownMultiplier(cd.BoarWhistleMultiplier))
            {
                AddScalableFloatJobs(jobs, CooldownsPatcher.BoarWhistleAssets,
                    cd.BoarWhistleMultiplier.Value, "boar-whistle");
            }
            if (HasCooldownMultiplier(cd.ShipSummonMultiplier))
            {
                AddScalableFloatJobs(jobs, CooldownsPatcher.ShipSummonAssets,
                    cd.ShipSummonMultiplier.Value, "ship-summon");
            }
            if (HasCooldownMultiplier(cd.RangedReloadMultiplier))
            {
                foreach (var kv in RangedReloadPatcher.WeaponAssets)
                {
                    jobs.Add(new CooldownJob
                    {
                        Family = "ranged-reload",
                        AssetStem = kv.Key,
                        VirtualPath = kv.Value,
                        Multiplier = cd.RangedReloadMultiplier.Value,
                        Shape = CooldownJobShape.RangedReload,
                    });
                }
            }
            // NOTE: Ship Cannons reload is NOT a cooldown job. It patches the loose
            // R5CannonParams .json (player-only) into the legacy pak via
            // CannonReloadPatcher - see ResolveShipCannonMultiplier + the main
            // BuildAsync flow. The old DA_BatteryManagerParams uasset patch had no
            // in-game effect.
            if (HasCooldownMultiplier(cd.SoulEaterAbilityMultiplier))
            {
                jobs.Add(new CooldownJob
                {
                    Family = "soul-eater",
                    AssetStem = WeaponAbilityCooldownPatcher.CurveTableStem,
                    VirtualPath = WeaponAbilityCooldownPatcher.CurveTableVirtualPath,
                    RowName = WeaponAbilityCooldownPatcher.SoulEaterRow,
                    Multiplier = cd.SoulEaterAbilityMultiplier.Value,
                    Shape = CooldownJobShape.WeaponAbilityCurve,
                });
            }
            if (HasCooldownMultiplier(cd.FoodBuffDurationMultiplier))
            {
                jobs.Add(new CooldownJob
                {
                    Family = "food-duration",
                    AssetStem = WeaponAbilityCooldownPatcher.FoodCurveTableStem,
                    VirtualPath = WeaponAbilityCooldownPatcher.FoodCurveTableVirtualPath,
                    Multiplier = cd.FoodBuffDurationMultiplier.Value,
                    Shape = CooldownJobShape.FoodDurationCurve,
                });
            }
            return jobs;
        }

        static bool HasCooldownMultiplier(double? m)
        {
            return m.HasValue && Math.Abs(m.Value - 1.0) > 1e-9;
        }

        List<ShipMusicJob> ResolveShipMusicJobs(Profile profile)
        {
            var jobs = new List<ShipMusicJob>();
            var sm = profile.Globals != null ? profile.Globals.ShipMusic : null;
            if (sm == null || sm.Songs == null || sm.Songs.Count == 0) return jobs;
            foreach (var kv in sm.Songs)
            {
                var stem = kv.Key;
                var ov = kv.Value;
                if (ov == null) continue;
                if (!ShipMusicSlots.ByStem.TryGetValue(stem, out var slot))
                {
                    LogLine("ShipMusic: skipping unknown slot stem '"
                            + stem + "' (not in vanilla catalog)");
                    continue;
                }
                var slotDir = _paths.ProfileShipMusicSlotDir(profile.Id, stem);
                var userWav = Path.Combine(slotDir, "audio.wav");
                if (!File.Exists(userWav))
                {
                    LogLine("ShipMusic: slot '" + stem
                            + "' is configured but its audio.wav is missing in "
                            + slotDir + " - falling back to vanilla.");
                    continue;
                }
                jobs.Add(new ShipMusicJob
                {
                    Slot = slot,
                    UserWavPath = userWav,
                    OriginalFilename = ov.OriginalFilename,
                    // null -> 0.45 = vanilla baseline (pipeline skips cue patching at this value).
                    UserVolume = ov.Volume.HasValue ? ov.Volume.Value : 0.45,
                });
            }
            return jobs;
        }

        BonfireMusicJob ResolveBonfireMusicJob(Profile profile)
        {
            var bm = profile.Globals != null ? profile.Globals.BonfireMusic : null;
            if (bm == null) return null;

            // null -> 1.0 (vanilla loudness); 0 produces digital silence (mute).
            double vol = bm.Volume ?? 1.0;
            if (vol < 0.0) vol = 0.0;
            if (vol > 1.0) vol = 1.0;

            var dir = _paths.ProfileBonfireMusicDir(profile.Id);
            var userWav = Path.Combine(dir, "audio.wav");
            bool hasUserWav = File.Exists(userWav);

            if (!hasUserWav)
            {
                bool hasFilename = !string.IsNullOrEmpty(bm.OriginalFilename);
                bool wantsMute = vol <= 1e-4;
                if (hasFilename)
                {
                    LogLine("BonfireMusic: '" + bm.OriginalFilename
                            + "' is configured but its audio.wav is missing in "
                            + dir + " - falling back to vanilla 'The Hearth'.");
                    return null;
                }
                if (!wantsMute)
                {
                    return null;
                }
                // Mute with no upload: synthesize silence at build time.
                return new BonfireMusicJob
                {
                    UserWavPath = null,
                    OriginalFilename = null,
                    UserVolume = 0.0,
                    IsSynthesizedSilence = true,
                };
            }

            return new BonfireMusicJob
            {
                UserWavPath = userWav,
                OriginalFilename = bm.OriginalFilename,
                UserVolume = vol,
                IsSynthesizedSilence = false,
            };
        }

        List<ShipMusicAddJob> ResolveShipMusicAddJobs(Profile profile)
        {
            var jobs = new List<ShipMusicAddJob>();
            var sma = profile.Globals != null ? profile.Globals.ShipMusicAdd : null;
            if (sma == null || sma.Tracks == null || sma.Tracks.Count == 0) return jobs;

            // First free slot after the 10 vanilla cues.
            int nextIndex = 11;
            for (int i = 0; i < sma.Tracks.Count; i++)
            {
                var t = sma.Tracks[i];
                if (t == null) continue;
                if (string.IsNullOrEmpty(t.TrackKey))
                {
                    LogLine("ShipMusicAdd: skipping track[" + i + "] - empty TrackKey");
                    continue;
                }
                if (!IsSafeTrackKey(t.TrackKey))
                {
                    LogLine("ShipMusicAdd: skipping track '" + t.TrackKey
                            + "' - TrackKey contains characters outside [A-Za-z0-9_]");
                    continue;
                }
                var trackDir = _paths.ProfileShipMusicAddTrackDir(profile.Id, t.TrackKey);
                var userWav = Path.Combine(trackDir, "audio.wav");
                if (!File.Exists(userWav))
                {
                    LogLine("ShipMusicAdd: track '" + t.TrackKey
                            + "' is configured but its audio.wav is missing in "
                            + trackDir + " - skipping.");
                    continue;
                }
                jobs.Add(new ShipMusicAddJob
                {
                    TrackKey = t.TrackKey,
                    NewIndex = nextIndex.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    UserWavPath = userWav,
                    Title = t.Title,
                    OriginalFilename = t.OriginalFilename,
                    // null -> 0.45 = vanilla baseline volume.
                    UserVolume = t.Volume.HasValue ? t.Volume.Value : 0.45,
                });
                nextIndex++;
            }
            return jobs;
        }

        // ShipMusicSlots.All position is the authoritative slot index.
        HashSet<int> ResolveShipMusicExcludedIndices(Profile profile)
        {
            var result = new HashSet<int>();
            var sm = profile.Globals != null ? profile.Globals.ShipMusic : null;
            var excluded = sm != null ? sm.ExcludedSlots : null;
            if (excluded == null || excluded.Count == 0) return result;

            var stemToIndex = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < ShipMusicSlots.All.Count; i++)
                stemToIndex[ShipMusicSlots.All[i].Stem] = i;

            foreach (var stem in excluded)
            {
                if (string.IsNullOrEmpty(stem)) continue;
                if (!stemToIndex.TryGetValue(stem, out int idx))
                {
                    LogLine("ShipMusic: skipping unknown excluded slot stem '" + stem
                            + "' (not in ShipMusicSlots.All registry)");
                    continue;
                }
                result.Add(idx);
            }
            return result;
        }

        static bool IsSafeTrackKey(string key)
        {
            if (string.IsNullOrEmpty(key)) return false;
            foreach (var c in key)
            {
                if (!((c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z')
                      || (c >= '0' && c <= '9') || c == '_'))
                    return false;
            }
            return true;
        }

        static void AddScalableFloatJobs(List<CooldownJob> jobs,
            Dictionary<string, string> assets, double multiplier, string family)
        {
            foreach (var kv in assets)
            {
                jobs.Add(new CooldownJob
                {
                    Family = family,
                    AssetStem = kv.Key,
                    VirtualPath = kv.Value,
                    Multiplier = multiplier,
                    Shape = CooldownJobShape.ScalableFloatDuration,
                });
            }
        }

        static void AddTopLevelMagnitudeJobs(List<CooldownJob> jobs,
            Dictionary<string, string> assets, double multiplier, string family)
        {
            foreach (var kv in assets)
            {
                jobs.Add(new CooldownJob
                {
                    Family = family,
                    AssetStem = kv.Key,
                    VirtualPath = kv.Value,
                    Multiplier = multiplier,
                    Shape = CooldownJobShape.TopLevelMagnitude,
                });
            }
        }

        static int CountCooldownFamilies(List<CooldownJob> jobs)
        {
            var set = new HashSet<string>(StringComparer.Ordinal);
            foreach (var j in jobs) if (j != null && j.Family != null) set.Add(j.Family);
            return set.Count;
        }

        CooldownJobResult RunCooldownJob(CooldownJob job, string legacyAssetPath, string usmapPath)
        {
            switch (job.Shape)
            {
                case CooldownJobShape.ScalableFloatDuration:
                {
                    var patcher = new CooldownsPatcher { Log = Log };
                    var r = patcher.PatchScalableFloatDuration(
                        legacyAssetPath, legacyAssetPath, usmapPath, job.Multiplier);
                    return new CooldownJobResult
                    {
                        Family = job.Family,
                        AssetStem = r.AssetStem,
                        Multiplier = job.Multiplier,
                        VanillaValue = r.VanillaValue,
                        EffectiveValue = r.EffectiveValue,
                        BatteryCount = 0,
                        PatchedBatteryCount = 0,
                    };
                }
                case CooldownJobShape.TopLevelMagnitude:
                {
                    var patcher = new CooldownsPatcher { Log = Log };
                    var r = patcher.PatchTopLevelMagnitude(
                        legacyAssetPath, legacyAssetPath, usmapPath, job.Multiplier);
                    return new CooldownJobResult
                    {
                        Family = job.Family,
                        AssetStem = r.AssetStem,
                        Multiplier = job.Multiplier,
                        VanillaValue = r.VanillaValue,
                        EffectiveValue = r.EffectiveValue,
                        BatteryCount = 0,
                        PatchedBatteryCount = 0,
                    };
                }
                case CooldownJobShape.RangedReload:
                {
                    var patcher = new RangedReloadPatcher { Log = Log };
                    var r = patcher.Patch(
                        legacyAssetPath, legacyAssetPath, usmapPath, job.Multiplier);
                    return new CooldownJobResult
                    {
                        Family = job.Family,
                        AssetStem = r.AssetStem,
                        Multiplier = job.Multiplier,
                        VanillaValue = r.VanillaReloadTime,
                        EffectiveValue = r.EffectiveReloadTime,
                        BatteryCount = 0,
                        PatchedBatteryCount = 0,
                    };
                }
                case CooldownJobShape.WeaponAbilityCurve:
                {
                    var patcher = new WeaponAbilityCooldownPatcher { Log = Log };
                    var r = patcher.Patch(
                        legacyAssetPath, legacyAssetPath, usmapPath, job.RowName, job.Multiplier);
                    return new CooldownJobResult
                    {
                        Family = job.Family,
                        AssetStem = r.AssetStem,
                        Multiplier = job.Multiplier,
                        VanillaValue = r.VanillaValue,
                        EffectiveValue = r.EffectiveValue,
                        BatteryCount = 0,
                        PatchedBatteryCount = 0,
                    };
                }
                case CooldownJobShape.FoodDurationCurve:
                {
                    var patcher = new WeaponAbilityCooldownPatcher { Log = Log };
                    var r = patcher.PatchRowsBySuffix(
                        legacyAssetPath, legacyAssetPath, usmapPath,
                        WeaponAbilityCooldownPatcher.DurationRowSuffix, job.Multiplier);
                    // Re-use Battery counters as "rows found / rows patched" for the readout.
                    return new CooldownJobResult
                    {
                        Family = job.Family,
                        AssetStem = r.AssetStem,
                        Multiplier = job.Multiplier,
                        VanillaValue = r.VanillaValue,
                        EffectiveValue = r.EffectiveValue,
                        BatteryCount = r.RowsPatched,
                        PatchedBatteryCount = r.RowsPatched,
                    };
                }
                default:
                    throw new InvalidOperationException(
                        "Unknown CooldownJobShape: " + job.Shape);
            }
        }

        static List<NoSmokeCategory> ResolveNoSmokeCategories(Profile profile)
        {
            var result = new List<NoSmokeCategory>();
            var ns = profile.Globals != null ? profile.Globals.NoSmoke : null;
            if (ns == null) return result;
            if (ns.Campfire.GetValueOrDefault(false)) result.Add(NoSmokeCategory.Campfire);
            if (ns.Furnace.GetValueOrDefault(false))  result.Add(NoSmokeCategory.Furnace);
            if (ns.Kiln.GetValueOrDefault(false))     result.Add(NoSmokeCategory.Kiln);
            return result;
        }

        // Builds the Albedo-swap jobs for the "Deposit visuals" feature. A deposit
        // contributes a job only when enabled AND its chosen texture differs from the
        // untouched game look (selecting the vanilla texture is a no-op, so we drop it
        // rather than ship an identical asset). Unknown texture keys fall back to the
        // deposit's reference default.
        static List<DepositVisualJob> ResolveDepositVisualJobs(Profile profile)
        {
            var jobs = new List<DepositVisualJob>();
            var dv = profile.Globals != null ? profile.Globals.DepositVisual : null;
            if (dv == null) return jobs;

            foreach (var deposit in DepositVisualCatalog.Deposits)
            {
                bool enabled;
                string chosenKey;
                if (deposit.Key == "iron")        { enabled = dv.Iron.GetValueOrDefault(false);   chosenKey = dv.IronTexture; }
                else if (deposit.Key == "sulfur") { enabled = dv.Sulfur.GetValueOrDefault(false); chosenKey = dv.SulfurTexture; }
                else continue;
                if (!enabled) continue;

                var texture = DepositVisualCatalog.FindTexture(chosenKey)
                              ?? DepositVisualCatalog.FindTexture(deposit.DefaultTextureKey);
                if (texture == null) continue;

                // Selecting the vanilla texture means "no visible change" - skip it.
                if (string.Equals(texture.Key, deposit.VanillaTextureKey, StringComparison.OrdinalIgnoreCase))
                    continue;

                jobs.Add(new DepositVisualJob
                {
                    DepositKey            = deposit.Key,
                    AssetStem             = deposit.AssetStem,
                    AssetVirtualPath      = deposit.AssetVirtualPath,
                    ParamName             = deposit.ParamName,
                    NewTextureStem        = texture.Stem,
                    NewTexturePackagePath = texture.PackagePath,
                    TextureLabel          = texture.Label,
                });
            }
            return jobs;
        }

        static bool HasLootConfiguration(Profile profile)
        {
            if (profile.LootOverrides != null && profile.LootOverrides.Count > 0) return true;
            var loot = profile.Globals != null ? profile.Globals.Loot : null;
            if (loot == null || loot.ByCategory == null) return false;
            foreach (var kv in loot.ByCategory)
            {
                if (kv.Value != 1.0) return true;
            }
            return false;
        }

        static bool HasNpcSpawnConfiguration(Profile profile)
        {
            if (profile.NpcSpawnOverrides != null)
            {
                foreach (var kv in profile.NpcSpawnOverrides)
                {
                    var o = kv.Value;
                    if (o != null && (o.RespawnMinutes.HasValue || o.CountMin.HasValue || o.CountMax.HasValue))
                        return true;
                }
            }
            var g = profile.Globals != null ? profile.Globals.NpcSpawn : null;
            if (g == null || !g.Enabled.GetValueOrDefault(false)) return false;
            double r = g.RespawnMultiplier ?? 1.0;
            double c = g.CountMultiplier ?? 1.0;
            return (r > 0.0 && Math.Abs(r - 1.0) > 1e-9)
                || (c > 0.0 && Math.Abs(c - 1.0) > 1e-9);
        }

        static bool HasCustomItemsConfiguration(Profile profile)
        {
            var customs = profile.CustomItems;
            if (customs == null || customs.Count == 0) return false;
            foreach (var c in customs)
            {
                if (c == null) continue;
                if (!string.IsNullOrWhiteSpace(c.Id) && !string.IsNullOrWhiteSpace(c.TemplateId))
                    return true;
            }
            return false;
        }

        static bool HasCustomBuildingsConfiguration(Profile profile)
        {
            var buildings = profile.CustomBuildings;
            if (buildings == null || buildings.Count == 0) return false;
            foreach (var b in buildings)
            {
                if (b == null) continue;
                if (string.IsNullOrWhiteSpace(b.Id)) continue;
                if (string.IsNullOrWhiteSpace(b.TemplateId)) continue;
                if (string.IsNullOrWhiteSpace(b.CookedFolderPath)) continue;
                if (string.IsNullOrWhiteSpace(b.MeshStem)) continue;
                if (string.IsNullOrWhiteSpace(b.ResolveAssetPrefix())) continue;
                return true;
            }
            return false;
        }

        static string DescribeSkeletonBuildings(Profile profile)
        {
            var buildings = profile.CustomBuildings;
            if (buildings == null || buildings.Count == 0) return null;
            var sb = new System.Text.StringBuilder();
            int skeletonCount = 0;
            for (int i = 0; i < buildings.Count; i++)
            {
                var b = buildings[i];
                if (b == null) continue;
                var missing = new List<string>();
                if (string.IsNullOrWhiteSpace(b.Id))               missing.Add("id");
                if (string.IsNullOrWhiteSpace(b.TemplateId))       missing.Add("templateId");
                if (string.IsNullOrWhiteSpace(b.CookedFolderPath)) missing.Add("cookedFolderPath");
                if (string.IsNullOrWhiteSpace(b.MeshStem))         missing.Add("meshStem");
                if (!string.IsNullOrWhiteSpace(b.MeshStem)
                    && string.IsNullOrWhiteSpace(b.ResolveAssetPrefix()))
                    missing.Add("meshStem (cannot derive asset prefix from this stem)");
                if (missing.Count == 0) continue;
                skeletonCount++;
                var label = !string.IsNullOrWhiteSpace(b.Name) ? b.Name
                          : !string.IsNullOrWhiteSpace(b.Id)   ? b.Id
                          : ("building#" + i);
                sb.Append("  - \"").Append(label).Append("\" missing: ")
                  .Append(string.Join(", ", missing)).Append('\n');
            }
            return skeletonCount > 0 ? sb.ToString().TrimEnd('\n') : null;
        }

        BuildingTemplate ResolveBuildingTemplate(string templateId)
        {
            if (string.IsNullOrWhiteSpace(templateId)) return null;
            var trimmed = templateId.Trim();

            if (BuildingTemplateCatalog == null)
            {
                LogLine("  warn: templateId='" + trimmed + "' looks like a Vanilla DA path"
                    + " but BuildingTemplateCatalog is not configured - skipping");
                return null;
            }

            var inspector = new VanillaBuildingTemplateInspector
            {
                Catalog = BuildingTemplateCatalog,
                Log     = msg => LogLine("  " + msg),
            };
            try
            {
                var inspection = inspector.Inspect(trimmed);
                if (!string.IsNullOrEmpty(inspection.Error))
                {
                    LogLine("  warn: template inspection failed for '" + trimmed
                        + "': " + inspection.Error + " - skipping");
                    return null;
                }
                foreach (var w in inspection.Warnings ?? new List<string>())
                    LogLine("  warn: " + w);
                if (string.IsNullOrEmpty(inspection.MeshStem) || string.IsNullOrEmpty(inspection.MeshPath))
                {
                    LogLine("  warn: template '" + trimmed + "' has no Mesh ref"
                        + " - cannot clone, skipping");
                    return null;
                }
                return BuildingTemplate.FromInspection(inspection);
            }
            catch (Exception ex)
            {
                LogLine("  warn: template resolution exception for '" + trimmed
                    + "': " + ex.Message + " - skipping");
                return null;
            }
        }

        // null rows -> null (RecipePatcher treats as pass-through to vanilla);
        // an empty list means the user explicitly cleared the cost editor.
        static List<(string ItemPath, int Count)> ToTupleList(List<RecipeCostEntry> rows)
        {
            if (rows == null) return null;
            var list = new List<(string, int)>(rows.Count);
            foreach (var r in rows)
            {
                if (r == null) continue;
                if (string.IsNullOrWhiteSpace(r.ItemPath)) continue;
                list.Add((r.ItemPath, r.Count));
            }
            return list;
        }

        static BuildingInputs BuildBuildingInputs(CustomBuilding b, BuildingTemplate template, string usmapPath, WindrosePaths paths, string profileId, Action<string> log)
        {
            var resolvedCookedFolder = paths.ResolveProfileRelativeFolder(profileId, b.CookedFolderPath);

            var inspector = new CookedFolderInspector
            {
                UsmapPath = usmapPath,
                Log = log,
            };
            var inspection = inspector.Inspect(resolvedCookedFolder, b.MeshStem);
            if (inspection.MeshSlots == null || inspection.MeshSlots.Count == 0)
                throw new InvalidOperationException(
                    "Mesh '" + b.MeshStem + "' has no material slots (or could not be read) - check the cooked folder");

            var inputs = new BuildingInputs
            {
                BuildingId        = b.Id,
                AssetPrefix       = b.ResolveAssetPrefix(),
                CookedFolderPath  = resolvedCookedFolder,
                MeshStem          = b.MeshStem,
                IconStem          = b.IconStem,
                DisplayName       = b.Name,
                Description       = b.Description,
                MeshSlots         = new List<MeshSlotInput>(),
            };

            foreach (var s in inspection.MeshSlots)
            {
                CustomBuildingSlot ov = null;
                if (b.Slots != null)
                {
                    if (!b.Slots.TryGetValue(s.Index.ToString(System.Globalization.CultureInfo.InvariantCulture), out ov)
                        && !b.Slots.TryGetValue(s.SlotName ?? "", out ov))
                    {
                        ov = null;
                    }
                }

                inputs.MeshSlots.Add(new MeshSlotInput
                {
                    Index                    = s.Index,
                    SlotName                 = s.SlotName,
                    UserMaterialStem         = s.UserMaterialStem,
                    UserMaterialPath         = s.UserMaterialPath,
                    VanillaMaterialParentPath = ov?.VanillaMaterialParentPath,
                    ScalarParams             = ov?.ScalarParams,
                    VectorParams             = ov?.VectorParams,
                    TextureParams            = ov?.TextureParams,
                });
            }

            return inputs;
        }

        static bool HasBuyerConfiguration(Profile profile)
        {
            if (profile.BuyerRecipes != null && profile.BuyerRecipes.Count > 0) return true;
            if (profile.BuyerLists != null)
            {
                foreach (var kv in profile.BuyerLists)
                {
                    var v = kv.Value;
                    if (v == null) continue;
                    if (v.AddedRecipeIds != null && v.AddedRecipeIds.Count > 0) return true;
                    if (v.RemovedRecipeIds != null && v.RemovedRecipeIds.Count > 0) return true;
                    if (v.RecipeOrder != null && v.RecipeOrder.Count > 0) return true;
                }
            }
            return false;
        }

        static bool HasSellerConfiguration(Profile profile)
        {
            if (profile.SellerRecipes != null && profile.SellerRecipes.Count > 0) return true;
            if (profile.SellerLists != null)
            {
                foreach (var kv in profile.SellerLists)
                {
                    var v = kv.Value;
                    if (v == null) continue;
                    if (v.AddedRecipeIds != null && v.AddedRecipeIds.Count > 0) return true;
                    if (v.RemovedRecipeIds != null && v.RemovedRecipeIds.Count > 0) return true;
                    if (v.RecipeOrder != null && v.RecipeOrder.Count > 0) return true;
                }
            }
            return false;
        }

        static bool HasBellLimitsConfiguration(Profile profile)
        {
            var b = profile.Globals != null ? profile.Globals.FastTravelBells : null;
            if (b == null) return false;
            if (b.BellCap.HasValue && b.BellCap.Value != BellLimitsPatcher.VanillaBellCap)
                return true;
            if (b.SignalFireCap.HasValue && b.SignalFireCap.Value != BellLimitsPatcher.VanillaSignalFireCap)
                return true;
            return false;
        }

        static bool HasEquipmentSlotsConfiguration(Profile profile)
        {
            var e = profile.Globals != null ? profile.Globals.EquipmentSlots : null;
            if (e == null) return false;
            if (e.RingSlots.HasValue && e.RingSlots.Value != InventorySlotsPatcher.VanillaRingSlots)
                return true;
            if (e.NecklaceSlots.HasValue && e.NecklaceSlots.Value != InventorySlotsPatcher.VanillaNecklaceSlots)
                return true;
            return false;
        }

        static bool HasShipSlotsConfiguration(Profile profile)
        {
            var s = profile.Globals != null ? profile.Globals.ShipSlots : null;
            if (s == null) return false;
            if (s.CargoMultiplier.HasValue
                && Math.Abs(s.CargoMultiplier.Value - ShipSlotsPatcher.VanillaCargoMultiplier) > 1e-9)
                return true;
            if (s.CombatOrderSlots.HasValue
                && s.CombatOrderSlots.Value != ShipSlotsPatcher.VanillaCombatOrders)
                return true;
            return false;
        }

        public static string SanitizeForFileName(string name)
        {
            if (string.IsNullOrEmpty(name)) return "Untitled";
            var sb = new StringBuilder(name.Length);
            foreach (var c in name)
            {
                if (char.IsLetterOrDigit(c) || c == '-' || c == '_') sb.Append(c);
                else if (c == ' ') sb.Append('-');
            }
            var raw = sb.ToString();
            if (string.IsNullOrEmpty(raw)) return "Untitled";
            var collapsed = new StringBuilder(raw.Length);
            char prev = '\0';
            foreach (var c in raw)
            {
                if ((c == '-' || c == '_') && c == prev) continue;
                collapsed.Append(c);
                prev = c;
            }
            return collapsed.ToString().Trim('-', '_');
        }
    }
}
