using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using static Windrose.Quartermaster.Core.R5Json;

namespace Windrose.Quartermaster.Core
{
    // Scales the player ship cannons' reload time. The reload value lives in the
    // loose R5CannonParams .json files (CannonAimingData.ReloadTime), NOT in the
    // DA_BatteryManagerParams uassets - patching those had no in-game effect.
    //
    // PLAYER-ONLY INVARIANT: only DA_Cannon_*.json are patched. DA_AI_Cannon_*.json
    // are the enemy/NPC cannons and are deliberately left vanilla, so the slider
    // never speeds up enemy ships. The DA_Cannon_* glob already excludes the
    // DA_AI_Cannon_* names; the explicit guard below makes the contract enforced.
    public sealed class CannonReloadPatcher
    {
        const string CannonsVanillaRoot = "R5/Content/Gameplay/Water/Character/Guns/Cannons";
        const string AimingDataProp = "CannonAimingData";
        const string ReloadTimeProp = "ReloadTime";

        public CannonReloadPatchResult PatchToDirectory(
            string vanillaCannonsDir, string outDir, double multiplier)
        {
            if (string.IsNullOrEmpty(vanillaCannonsDir)) throw new ArgumentNullException("vanillaCannonsDir");
            if (string.IsNullOrEmpty(outDir)) throw new ArgumentNullException("outDir");
            if (!Directory.Exists(vanillaCannonsDir))
                throw new DirectoryNotFoundException(
                    "Vanilla cannon params not found: " + vanillaCannonsDir
                    + " - re-run setup to extract the 'Cannon params' source.");
            if (!(multiplier > 0.0))
                throw new ArgumentException("multiplier must be > 0", "multiplier");

            Directory.CreateDirectory(outDir);
            var result = new CannonReloadPatchResult { Multiplier = multiplier };

            if (Math.Abs(multiplier - 1.0) < 1e-9)
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

                var aiming = root[AimingDataProp] as JsonObject;
                if (aiming == null || !(aiming[ReloadTimeProp] is JsonValue rt))
                {
                    result.Skipped++;
                    continue;
                }

                double vanillaSeconds;
                if (!rt.TryGetValue<double>(out vanillaSeconds))
                {
                    result.Skipped++;
                    continue;
                }

                // Round to 3 decimals so 11 * 0.1 reads as 1.1, not 1.1000000001.
                var newSeconds = Math.Round(vanillaSeconds * multiplier, 3);
                if (newSeconds < 0.05) newSeconds = 0.05;
                if (Math.Abs(newSeconds - vanillaSeconds) < 1e-9)
                {
                    result.Skipped++;
                    continue;
                }

                aiming[ReloadTimeProp] = JsonValue.Create(newSeconds);

                var rel = path.Substring(rootFull.Length).TrimStart(
                    Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                var outPath = Path.Combine(outDir,
                    CannonsVanillaRoot.Replace('/', Path.DirectorySeparatorChar),
                    rel);
                Directory.CreateDirectory(Path.GetDirectoryName(outPath));
                File.WriteAllBytes(outPath, SerializeWithTabsAndCrlf(root));

                result.Written++;
                result.PatchedCannons.Add(new CannonReloadAssetResult
                {
                    Stem = stem,
                    VanillaSeconds = vanillaSeconds,
                    EffectiveSeconds = newSeconds,
                });
            }

            return result;
        }
    }

    public sealed class CannonReloadPatchResult
    {
        public double Multiplier;
        public int Scanned;
        public int Written;
        public int Skipped;
        public int SkippedAi;
        public List<CannonReloadAssetResult> PatchedCannons = new List<CannonReloadAssetResult>();
    }

    public sealed class CannonReloadAssetResult
    {
        public string Stem;
        public double VanillaSeconds;
        public double EffectiveSeconds;
    }
}
