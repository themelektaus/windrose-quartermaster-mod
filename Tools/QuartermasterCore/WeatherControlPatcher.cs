using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UAssetAPI;
using UAssetAPI.ExportTypes;
using UAssetAPI.PropertyTypes.Objects;
using UAssetAPI.PropertyTypes.Structs;
using UAssetAPI.UnrealTypes;
using UAssetAPI.Unversioned;

namespace Windrose.Quartermaster.Core
{
    // Stage 3: per-weather "Weather Control" ConsumableData clones.
    //
    // Background (proven in the WeatherControl PoC): a Weather item is a rum-bottle
    // consumable whose `ConsumableData` field points at a CLONE of the vanilla
    // DA_ConsumableAbilityData_Potion_RumBottle. The clone is byte-identical except
    //   (a) it is renamed into our namespace so the dxgi DLL can match it by name,
    //   (b) its CooldownConsumableAbilityTags bucket is emptied (no cooldown),
    //   (c) SpendCount is forced to 0 so the bottle is NOT consumed on use, and
    //   (d) EffectsOnSpend is emptied so the vanilla rum buff is removed - the only
    //       effect left is the weather change, which the dxgi DLL applies by reading
    //       the clone name (no data-side weather effect exists; new gameplay tags are
    //       hard-rejected by the R5BLGameplayTag marshaller).
    // The DLL substring-matches the clone name against qm_weather_trigger.txt and
    // sets the live weather on use.
    //
    // Why rum, not the boar whistle: the rum's Food consume-ability spawns no boar
    // and has no ability-side cooldown GE, so there is nothing to strip at runtime.
    // The Params_0 (ConsumableData) offset the DLL reads lives in the shared
    // R5ConsumeAbility base class, so the trigger fires identically for any base.
    //
    // The DISCRIMINATOR is the clone NAME, so each distinct weather gets its own
    // clone (e.g. ..._QmWeatherControl_Storm). Multiple items that pick the same
    // weather share one clone. This patcher extracts the vanilla source once and
    // stages one clone per requested weather into the IoStore composite.
    public sealed class WeatherControlPatcher
    {
        public Action<string> Log;

        public const string SourceStem        = "DA_ConsumableAbilityData_Potion_RumBottle";
        public const string SourcePackagePath = "/Game/Gameplay/ItemsLogic/Consumables/Food/ConsumeAbilityData/DA_ConsumableAbilityData_Potion_RumBottle";

        // Clone identity: stem prefix + weather name; package dir is our namespace.
        public const string CloneStemPrefix   = "DA_ConsumableAbilityData_QmWeatherControl_";
        public const string ClonePackageDir   = "/Game/Quartermaster/Consumables/";
        // The token the DLL substring-matches (the clone stem contains it).
        public const string TriggerTokenPrefix = "QmWeatherControl_";

        public const int WeatherMin = 0;
        public const int WeatherMax = 13;

        // id -> name; index IS the weather id (must match qm_weather.cpp WeatherName).
        static readonly string[] WeatherNames =
        {
            "Sunny", "Cloudy", "Fog", "Mist", "Rain", "RainHeavy", "Storm",
            "Windy", "HighPressure", "Rainbow", "Overcast", "AshlandsFog",
            "TortugaMist", "Default",
        };

        public static bool   IsValidWeatherId(int id) => id >= WeatherMin && id <= WeatherMax;
        public static string WeatherName(int id)      => IsValidWeatherId(id) ? WeatherNames[id] : null;

        public static IReadOnlyList<string> AllWeatherNames => WeatherNames;

        // "DA_ConsumableAbilityData_QmWeatherControl_Storm"
        public static string CloneStemForWeather(int id)
        {
            var n = WeatherName(id);
            return n == null ? null : CloneStemPrefix + n;
        }

        // "QmWeatherControl_Storm" - the substring the DLL matches on the clone name.
        public static string TriggerTokenForWeather(int id)
        {
            var n = WeatherName(id);
            return n == null ? null : TriggerTokenPrefix + n;
        }

        // The item JSON `ConsumableData` value:
        // "/Game/Quartermaster/Consumables/<stem>.<stem>"
        public static string ConsumableDataRefForWeather(int id)
        {
            var stem = CloneStemForWeather(id);
            return stem == null ? null : ClonePackageDir + stem + "." + stem;
        }

        // Stage one ConsumableData clone per distinct weather id into stagingDir
        // (the shared IoStore composite legacy root). Extracts the vanilla source
        // ONCE (with the AES key, since the composite builder's to-legacy runs
        // keyless) into its own temp dir, then clones + clears cooldown per weather.
        // Returns one entry per staged clone. Idempotent per build (re-stages).
        public WeatherControlStageResult StageClones(
            string stagingDir,
            string retocExe,
            string usmapPath,
            string vanillaPaksDir,
            string aesKey,
            string tempDir,
            IEnumerable<int> weatherIds)
        {
            if (string.IsNullOrEmpty(stagingDir))     throw new ArgumentNullException(nameof(stagingDir));
            if (string.IsNullOrEmpty(retocExe))       throw new ArgumentNullException(nameof(retocExe));
            if (string.IsNullOrEmpty(usmapPath))      throw new ArgumentNullException(nameof(usmapPath));
            if (string.IsNullOrEmpty(vanillaPaksDir)) throw new ArgumentNullException(nameof(vanillaPaksDir));
            if (string.IsNullOrEmpty(tempDir))        throw new ArgumentNullException(nameof(tempDir));

            var result = new WeatherControlStageResult();

            // Distinct, valid weather ids (sorted for stable logs).
            var ids = (weatherIds ?? Enumerable.Empty<int>())
                .Where(IsValidWeatherId)
                .Distinct()
                .OrderBy(i => i)
                .ToList();
            if (ids.Count == 0) return result;

            // ---- extract the vanilla source ONCE (needs the AES key) -----------
            Directory.CreateDirectory(tempDir);
            var legacyExtractDir = Path.Combine(tempDir, "vanilla-cons");
            if (Directory.Exists(legacyExtractDir)) Directory.Delete(legacyExtractDir, true);
            Directory.CreateDirectory(legacyExtractDir);

            var extractArgs = new List<string>();
            if (!string.IsNullOrEmpty(aesKey)) { extractArgs.Add("--aes-key"); extractArgs.Add(aesKey); }
            extractArgs.Add("to-legacy");
            extractArgs.Add(vanillaPaksDir);
            extractArgs.Add(legacyExtractDir);
            extractArgs.Add("--version"); extractArgs.Add("UE5_6");
            extractArgs.Add("--filter");  extractArgs.Add(SourceStem);

            LogLine("WeatherControl: extracting " + SourceStem + " (" + ids.Count + " weather clone(s))");
            var r = ToolProcess.RunCapture(retocExe, extractArgs);
            if (r.ExitCode != 0)
                throw new InvalidOperationException(
                    "retoc to-legacy for " + SourceStem + " failed (exit " + r.ExitCode + ")\n" + r.ErrOrOut);

            var found = Directory.GetFiles(legacyExtractDir, SourceStem + ".uasset", SearchOption.AllDirectories);
            if (found.Length == 0)
                throw new InvalidOperationException(
                    "retoc to-legacy produced no " + SourceStem + ".uasset under " + legacyExtractDir
                    + " - the game container may have moved the asset, or the AES key is wrong.");
            var sourceAsset = found[0];

            // ---- one clone per distinct weather --------------------------------
            var consStageDir = Path.Combine(stagingDir, "R5", "Content", "Quartermaster", "Consumables");
            Directory.CreateDirectory(consStageDir);

            foreach (var id in ids)
            {
                var cloneStem = CloneStemForWeather(id);
                var clonePkg  = ClonePackageDir + cloneStem;
                var outAsset  = Path.Combine(consStageDir, cloneStem + ".uasset");

                var replacements = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    [SourceStem]        = cloneStem,
                    [SourcePackagePath] = clonePkg,
                };

                var pr = new DataAssetPatcher { Log = msg => LogLine("  " + msg) }.Patch(
                    inputAssetPath:  sourceAsset,
                    outputAssetPath: outAsset,
                    usmapPath:       usmapPath,
                    replacements:    replacements,
                    newFolderName:   clonePkg,
                    requireAllHits:  false);

                if (pr.NameMapEntriesRenamed == 0 && pr.ExportsRetargeted == 0)
                    throw new InvalidOperationException(
                        "WeatherControl clone for weather " + id + " (" + WeatherName(id)
                        + ") produced 0 renames - clone identity did not move; aborting to avoid"
                        + " shipping a duplicate of the vanilla asset.");

                CopySidecars(sourceAsset, outAsset);

                var ov = ApplyCloneOverrides(outAsset, usmapPath);

                LogLine("  clone[" + WeatherName(id) + "]: " + cloneStem
                        + " (renames=" + (pr.NameMapEntriesRenamed)
                        + ", cooldownBucketsCleared=" + ov.CooldownBucketsCleared
                        + ", spendCountZeroed=" + ov.SpendCountZeroed
                        + ", effectsOnSpendCleared=" + ov.EffectsOnSpendCleared + ")");

                result.Clones.Add(new WeatherControlClone
                {
                    WeatherId        = id,
                    WeatherName      = WeatherName(id),
                    CloneStem        = cloneStem,
                    TriggerToken     = TriggerTokenForWeather(id),
                    ConsumableDataRef = ConsumableDataRefForWeather(id),
                });
            }

            return result;
        }

        struct CloneOverrideResult
        {
            public int CooldownBucketsCleared;
            public int SpendCountZeroed;      // 1 = set existing, 2 = added (was at default)
            public int EffectsOnSpendCleared;
        }

        // Apply the three data-only clone overrides in a single load/save:
        //   - empty every CooldownConsumableAbilityTags container (no cooldown gate),
        //   - force SpendCount = 0 (bottle not consumed on use; add it if the vanilla
        //     asset left it at the class default, which DOES consume),
        //   - empty EffectsOnSpend (drop the vanilla rum buff; only weather remains).
        static CloneOverrideResult ApplyCloneOverrides(string assetPath, string usmapPath)
        {
            var mappings = new Usmap(usmapPath);
            var asset = new UAsset(assetPath, EngineVersion.VER_UE5_6, mappings);
            var r = new CloneOverrideResult();

            foreach (var ex in asset.Exports)
            {
                if (ex is not NormalExport ne) continue;
                r.CooldownBucketsCleared += ClearTagContainers(ne.Data);
                r.EffectsOnSpendCleared  += ClearNamedArray(ne.Data, "EffectsOnSpend");
                r.SpendCountZeroed       += ForceSpendCountZero(asset, ne.Data);
            }

            asset.Write(assetPath);
            return r;
        }

        static int ClearTagContainers(IList<PropertyData> props)
        {
            if (props == null) return 0;
            int cleared = 0;
            foreach (var p in props)
            {
                var name = p?.Name?.Value?.Value as string;
                if (p is GameplayTagContainerPropertyData gtc
                    && string.Equals(name, "CooldownConsumableAbilityTags", StringComparison.Ordinal))
                {
                    if (gtc.Value != null && gtc.Value.Length > 0)
                    {
                        gtc.Value = Array.Empty<FName>();
                        cleared++;
                    }
                }
                else if (p is StructPropertyData sp)
                {
                    cleared += ClearTagContainers(sp.Value);
                }
                else if (p is ArrayPropertyData ap)
                {
                    cleared += ClearTagContainers(ap.Value);
                }
            }
            return cleared;
        }

        // Recursively empty the first ArrayPropertyData with the given name. Returns
        // the number of arrays emptied (typically 1).
        static int ClearNamedArray(IList<PropertyData> props, string arrayName)
        {
            if (props == null) return 0;
            int cleared = 0;
            foreach (var p in props)
            {
                var name = p?.Name?.Value?.Value as string;
                if (p is ArrayPropertyData ap && string.Equals(name, arrayName, StringComparison.Ordinal))
                {
                    if (ap.Value != null && ap.Value.Length > 0)
                    {
                        ap.Value = Array.Empty<PropertyData>();
                        cleared++;
                    }
                }
                else if (p is StructPropertyData sp)
                {
                    cleared += ClearNamedArray(sp.Value, arrayName);
                }
                else if (p is ArrayPropertyData inner)
                {
                    cleared += ClearNamedArray(inner.Value, arrayName);
                }
            }
            return cleared;
        }

        // Force SpendCount = 0 inside the ConsumableActivationParams struct. The
        // vanilla rum asset omits SpendCount (class default consumes the item), so we
        // ADD it when absent. Returns 1 if an existing value was set, 2 if added, 0 if
        // the host struct was not found.
        static int ForceSpendCountZero(UAsset asset, IList<PropertyData> props)
        {
            if (props == null) return 0;
            foreach (var p in props)
            {
                var name = p?.Name?.Value?.Value as string;
                if (p is StructPropertyData sp)
                {
                    if (string.Equals(name, "ConsumableActivationParams", StringComparison.Ordinal))
                    {
                        var existing = sp.Value?
                            .OfType<IntPropertyData>()
                            .FirstOrDefault(ip => string.Equals(ip.Name?.Value?.Value as string, "SpendCount", StringComparison.Ordinal));
                        if (existing != null)
                        {
                            existing.Value = 0;
                            return 1;
                        }
                        var add = new IntPropertyData(FName.FromString(asset, "SpendCount")) { Value = 0 };
                        sp.Value.Add(add);
                        return 2;
                    }
                    var nested = ForceSpendCountZero(asset, sp.Value);
                    if (nested != 0) return nested;
                }
            }
            return 0;
        }

        // Copy .ubulk / .uptnl siblings the patcher doesn't rewrite (the source has
        // none today, but keep parity with the proven PoC for robustness).
        static void CopySidecars(string legacyAsset, string stagedAsset)
        {
            foreach (var ext in new[] { ".ubulk", ".uptnl" })
            {
                var src = Path.ChangeExtension(legacyAsset, ext);
                if (File.Exists(src))
                    File.Copy(src, Path.ChangeExtension(stagedAsset, ext), overwrite: true);
            }
        }

        void LogLine(string msg) { if (Log != null) Log(msg); }
    }

    public sealed class WeatherControlStageResult
    {
        public List<WeatherControlClone> Clones = new List<WeatherControlClone>();
    }

    public sealed class WeatherControlClone
    {
        public int    WeatherId;
        public string WeatherName;
        public string CloneStem;
        public string TriggerToken;       // DLL substring token, e.g. "QmWeatherControl_Storm"
        public string ConsumableDataRef;  // item JSON ConsumableData value
    }
}
