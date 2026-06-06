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
    // Stage 3: per-weather "Weather Whistle" ConsumableData clones.
    //
    // Background (proven in the WeatherControl PoC): a Weather Whistle item is a
    // boar-whistle item whose `ConsumableData` field points at a CLONE of the
    // vanilla DA_ConsumableAbilityData_SpawnerBoar. The clone is byte-identical
    // except (a) it is renamed into our namespace so the dxgi DLL can match it by
    // name, and (b) its CooldownConsumableAbilityTags bucket is emptied so our
    // whistle has no cooldown. The DLL substring-matches the clone name against
    // qm_weather_trigger.txt and sets the live weather on use.
    //
    // The DISCRIMINATOR is the clone NAME, so each distinct weather gets its own
    // clone (e.g. ..._QmWeatherWhistle_Storm). Multiple items that pick the same
    // weather share one clone. This patcher extracts the vanilla source once and
    // stages one clone per requested weather into the IoStore composite.
    public sealed class WeatherWhistlePatcher
    {
        public Action<string> Log;

        public const string SourceStem        = "DA_ConsumableAbilityData_SpawnerBoar";
        public const string SourcePackagePath = "/Game/Gameplay/ItemsLogic/Consumables/Spawner/DA_ConsumableAbilityData_SpawnerBoar";

        // Clone identity: stem prefix + weather name; package dir is our namespace.
        public const string CloneStemPrefix   = "DA_ConsumableAbilityData_QmWeatherWhistle_";
        public const string ClonePackageDir   = "/Game/Quartermaster/Consumables/";
        // The token the DLL substring-matches (the clone stem contains it).
        public const string TriggerTokenPrefix = "QmWeatherWhistle_";

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

        // "DA_ConsumableAbilityData_QmWeatherWhistle_Storm"
        public static string CloneStemForWeather(int id)
        {
            var n = WeatherName(id);
            return n == null ? null : CloneStemPrefix + n;
        }

        // "QmWeatherWhistle_Storm" - the substring the DLL matches on the clone name.
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
        public WeatherWhistleStageResult StageClones(
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

            var result = new WeatherWhistleStageResult();

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

            LogLine("WeatherWhistle: extracting " + SourceStem + " (" + ids.Count + " weather clone(s))");
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
                        "WeatherWhistle clone for weather " + id + " (" + WeatherName(id)
                        + ") produced 0 renames - clone identity did not move; aborting to avoid"
                        + " shipping a duplicate of the vanilla asset.");

                CopySidecars(sourceAsset, outAsset);

                int cleared = ClearCooldownTags(outAsset, usmapPath);

                LogLine("  clone[" + WeatherName(id) + "]: " + cloneStem
                        + " (renames=" + (pr.NameMapEntriesRenamed) + ", cooldownBucketsCleared=" + cleared + ")");

                result.Clones.Add(new WeatherWhistleClone
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

        // Empty every CooldownConsumableAbilityTags container in the asset, so R5's
        // data-side cooldown gate finds no tag to wait on (-> our whistle has no
        // cooldown). Returns the number of containers emptied.
        static int ClearCooldownTags(string assetPath, string usmapPath)
        {
            var mappings = new Usmap(usmapPath);
            var asset = new UAsset(assetPath, EngineVersion.VER_UE5_6, mappings);
            int cleared = 0;
            foreach (var ex in asset.Exports)
                if (ex is NormalExport ne)
                    cleared += ClearTagContainers(ne.Data);
            if (cleared > 0) asset.Write(assetPath);
            return cleared;
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

    public sealed class WeatherWhistleStageResult
    {
        public List<WeatherWhistleClone> Clones = new List<WeatherWhistleClone>();
    }

    public sealed class WeatherWhistleClone
    {
        public int    WeatherId;
        public string WeatherName;
        public string CloneStem;
        public string TriggerToken;       // DLL substring token, e.g. "QmWeatherWhistle_Storm"
        public string ConsumableDataRef;  // item JSON ConsumableData value
    }
}
