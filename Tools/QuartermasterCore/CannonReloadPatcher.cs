using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using static Windrose.Quartermaster.Core.R5Json;

namespace Windrose.Quartermaster.Core
{
    // Scales the player ship cannons' reload time and/or firing range. Both values
    // live in the loose R5CannonParams .json files (CannonAimingData.ReloadTime and
    // ShotRangeInterval.Max), NOT in the DA_BatteryManagerParams uassets - patching
    // those had no in-game effect.
    //
    // Both dimensions are patched in a SINGLE pass per file: they share the same
    // .json, so running two independent patchers would make the second overwrite
    // the first's edit in the staging dir. reloadMultiplier / rangeMultiplier each
    // default to 1.0 (no-op for that dimension).
    //
    // PLAYER-ONLY INVARIANT: only DA_Cannon_*.json are patched. DA_AI_Cannon_*.json
    // are the enemy/NPC cannons and are deliberately left vanilla, so the sliders
    // never buff enemy ships. The DA_*Cannon_* glob enumerates both so the skip is
    // observable in the build log; the explicit guard below enforces the contract.
    public sealed class CannonReloadPatcher
    {
        const string CannonsVanillaRoot = "R5/Content/Gameplay/Water/Character/Guns/Cannons";
        const string AimingDataProp = "CannonAimingData";
        const string ReloadTimeProp = "ReloadTime";
        const string RangeIntervalProp = "ShotRangeInterval";
        const string RangeMaxProp = "Max";

        public CannonReloadPatchResult PatchToDirectory(
            string vanillaCannonsDir, string outDir,
            double reloadMultiplier, double rangeMultiplier)
        {
            if (string.IsNullOrEmpty(vanillaCannonsDir)) throw new ArgumentNullException("vanillaCannonsDir");
            if (string.IsNullOrEmpty(outDir)) throw new ArgumentNullException("outDir");
            if (!Directory.Exists(vanillaCannonsDir))
                throw new DirectoryNotFoundException(
                    "Vanilla cannon params not found: " + vanillaCannonsDir
                    + " - re-run setup to extract the 'Cannon params' source.");
            if (!(reloadMultiplier > 0.0))
                throw new ArgumentException("reloadMultiplier must be > 0", "reloadMultiplier");
            if (!(rangeMultiplier > 0.0))
                throw new ArgumentException("rangeMultiplier must be > 0", "rangeMultiplier");

            Directory.CreateDirectory(outDir);
            var result = new CannonReloadPatchResult
            {
                Multiplier = reloadMultiplier,
                RangeMultiplier = rangeMultiplier,
            };

            bool reloadActive = Math.Abs(reloadMultiplier - 1.0) > 1e-9;
            bool rangeActive = Math.Abs(rangeMultiplier - 1.0) > 1e-9;
            if (!reloadActive && !rangeActive)
                return result;

            // Enumerate BOTH player (DA_Cannon_*) and enemy (DA_AI_Cannon_*) cannon
            // DataAssets so the player-only skip is observable in the build log,
            // then enforce the player-only contract by skipping the AI ones.
            var rootFull = Path.GetFullPath(vanillaCannonsDir);
            foreach (var path in Directory.EnumerateFiles(rootFull, "DA_*Cannon_*.json", SearchOption.AllDirectories))
            {
                var stem = Path.GetFileNameWithoutExtension(path);

                // PLAYER-ONLY INVARIANT: never touch enemy cannons.
                if (stem.StartsWith("DA_AI_Cannon", StringComparison.OrdinalIgnoreCase))
                {
                    result.SkippedAi++;
                    continue;
                }

                result.Scanned++;
                JsonObject root;
                try
                {
                    var node = JsonNode.Parse(File.ReadAllText(path, Encoding.UTF8));
                    root = node as JsonObject;
                    if (root == null) { result.Skipped++; continue; }
                }
                catch (JsonException)
                {
                    result.Skipped++;
                    continue;
                }

                var asset = new CannonReloadAssetResult { Stem = stem };
                bool changed = false;

                // --- Reload time (CannonAimingData.ReloadTime, seconds) ---
                var aiming = root[AimingDataProp] as JsonObject;
                if (reloadActive
                    && aiming != null
                    && aiming[ReloadTimeProp] is JsonValue rt
                    && rt.TryGetValue<double>(out var vanillaSeconds))
                {
                    // Round to 3 decimals so 11 * 0.1 reads as 1.1, not 1.1000000001.
                    var newSeconds = Math.Round(vanillaSeconds * reloadMultiplier, 3);
                    if (newSeconds < 0.05) newSeconds = 0.05;
                    asset.VanillaSeconds = vanillaSeconds;
                    asset.EffectiveSeconds = newSeconds;
                    if (Math.Abs(newSeconds - vanillaSeconds) > 1e-9)
                    {
                        aiming[ReloadTimeProp] = JsonValue.Create(newSeconds);
                        result.ReloadPatched++;
                        changed = true;
                    }
                }

                // --- Firing range (ShotRangeInterval.Max, UE units) ---
                var rangeInterval = root[RangeIntervalProp] as JsonObject;
                if (rangeActive
                    && rangeInterval != null
                    && rangeInterval[RangeMaxProp] is JsonValue rmax
                    && rmax.TryGetValue<double>(out var vanillaMax))
                {
                    var newMax = (long)Math.Round(vanillaMax * rangeMultiplier);
                    if (newMax < 1) newMax = 1;
                    asset.VanillaRangeMax = (long)Math.Round(vanillaMax);
                    asset.EffectiveRangeMax = newMax;
                    if (newMax != (long)Math.Round(vanillaMax))
                    {
                        rangeInterval[RangeMaxProp] = JsonValue.Create(newMax);
                        result.RangePatched++;
                        changed = true;
                    }
                }

                if (!changed)
                {
                    result.Skipped++;
                    continue;
                }

                var rel = path.Substring(rootFull.Length).TrimStart(
                    Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                var outPath = Path.Combine(outDir,
                    CannonsVanillaRoot.Replace('/', Path.DirectorySeparatorChar),
                    rel);
                Directory.CreateDirectory(Path.GetDirectoryName(outPath));
                File.WriteAllBytes(outPath, SerializeWithTabsAndCrlf(root));

                result.Written++;
                result.PatchedCannons.Add(asset);
            }

            return result;
        }
    }

    public sealed class CannonReloadPatchResult
    {
        public double Multiplier;          // reload multiplier
        public double RangeMultiplier;
        public int Scanned;
        public int Written;
        public int Skipped;
        public int SkippedAi;
        public int ReloadPatched;          // files whose ReloadTime actually changed
        public int RangePatched;           // files whose ShotRangeInterval.Max actually changed
        public List<CannonReloadAssetResult> PatchedCannons = new List<CannonReloadAssetResult>();
    }

    public sealed class CannonReloadAssetResult
    {
        public string Stem;
        public double VanillaSeconds;
        public double EffectiveSeconds;
        public long VanillaRangeMax;
        public long EffectiveRangeMax;
    }
}
