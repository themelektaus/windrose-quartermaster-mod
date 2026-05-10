using System;
using System.Diagnostics;
using System.IO;

namespace Windrose.Quartermaster.Core
{
    // Produces a UE5 IoStore mod triplet (.pak stub + .ucas + .utoc) that
    // overrides the auto-pickup magnet range. Exactly one asset is patched
    // -- the GA_Loot_AutoPickup Blueprint CDO -- with a serialized
    // MagnetRadius FloatProperty added or updated to <vanilla * multiplier>.
    //
    // The flow is:
    //   1. retoc to-legacy on the game's Paks dir, filtered to just our
    //      one asset, produces a tiny Legacy .uasset+.uexp pair plus a
    //      scriptobjects.bin (needed for the to-zen step).
    //   2. PickupBlueprintPatcher rewrites the Legacy .uasset with the
    //      patched property.
    //   3. retoc to-zen converts the patched Legacy back into an IoStore
    //      triplet that the engine reads at the same priority as the
    //      vanilla container -- but our _P-suffixed triplet sorts later
    //      in the mount order, so its asset wins.
    //
    // Vanilla baseline: MagnetRadius = 400.0f (4 m). The reference mod
    // we benchmarked against ships 800.0f (2x) and the in-game effect was
    // verified by the original mod author. Caller passes the already
    // multiplied value (we don't bake the 400 baseline in here in case
    // the game ever changes it).
    public sealed class PickupTripletBuilder
    {
        // The single asset path that carries MagnetRadius. Hardcoded
        // because it IS the only asset in the entire game with this
        // property -- if Windrose ever moves the auto-pickup logic to a
        // different Blueprint, we'd need to rediscover and update this.
        public const string PickupAssetVirtualPath =
            "R5/Content/Gameplay/Character/Player/GameplayAbilities/Loot/GA_Loot_AutoPickup.uasset";

        // Filter substring passed to retoc --filter. Matches just the
        // single asset's filename stem (no path component). Cheap full
        // game-Paks scan completes in ~200ms even with all 12 .utocs.
        public const string AssetFilterStem = "GA_Loot_AutoPickup";

        public Action<string> Log;

        public PickupTripletResult Build(PickupTripletRequest req)
        {
            if (req == null) throw new ArgumentNullException("req");
            if (string.IsNullOrEmpty(req.RetocExe)) throw new ArgumentException("RetocExe is required");
            if (string.IsNullOrEmpty(req.GamePaksDir)) throw new ArgumentException("GamePaksDir is required");
            if (string.IsNullOrEmpty(req.UsmapPath)) throw new ArgumentException("UsmapPath is required");
            if (string.IsNullOrEmpty(req.OutputPakPath)) throw new ArgumentException("OutputPakPath is required");
            if (req.MagnetRadius <= 0f) throw new ArgumentException("MagnetRadius must be > 0");
            if (!File.Exists(req.RetocExe))
                throw new FileNotFoundException("retoc.exe not found: " + req.RetocExe);
            if (!Directory.Exists(req.GamePaksDir))
                throw new DirectoryNotFoundException("Game Paks dir not found: " + req.GamePaksDir);
            if (!File.Exists(req.UsmapPath))
                throw new FileNotFoundException("Usmap not found: " + req.UsmapPath);

            // Output triplet base name: ".pak" / ".ucas" / ".utoc" siblings.
            var outDir = Path.GetDirectoryName(req.OutputPakPath);
            if (!string.IsNullOrEmpty(outDir)) Directory.CreateDirectory(outDir);
            if (!req.OutputPakPath.EndsWith(".pak", StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("OutputPakPath must end with .pak");
            var basePath = req.OutputPakPath.Substring(0, req.OutputPakPath.Length - ".pak".Length);
            var outPak  = basePath + ".pak";
            var outUcas = basePath + ".ucas";
            var outUtoc = basePath + ".utoc";

            // Pre-clear stale outputs so a half-finished run from before
            // doesn't pollute the new triplet (especially the .ucas, which
            // would otherwise not get re-created if to-zen failed earlier).
            if (req.Overwrite)
            {
                foreach (var p in new[] { outPak, outUcas, outUtoc })
                {
                    if (File.Exists(p)) File.Delete(p);
                }
            }
            else
            {
                foreach (var p in new[] { outPak, outUcas, outUtoc })
                {
                    if (File.Exists(p))
                        throw new IOException("Output already exists (overwrite=false): " + p);
                }
            }

            // Per-build temp tree: <BuildTmp>/<profileId>/_pickup/{legacy,zen}
            // Caller hands us the temp dir; we create the two subdirs.
            var tmpRoot = req.TempDir;
            if (string.IsNullOrEmpty(tmpRoot))
                tmpRoot = Path.Combine(Path.GetTempPath(), "windrose-pickup-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tmpRoot);
            var tmpLegacyDir = Path.Combine(tmpRoot, "legacy");
            // Wipe legacy/ before to-legacy so a stale tree doesn't leak
            // into the to-zen step (retoc to-zen scans the whole input dir).
            if (Directory.Exists(tmpLegacyDir)) Directory.Delete(tmpLegacyDir, true);
            Directory.CreateDirectory(tmpLegacyDir);

            // Step 1: extract vanilla as Legacy
            LogLine("retoc to-legacy: " + req.GamePaksDir + " --filter " + AssetFilterStem);
            RunRetoc(req.RetocExe, new[]
            {
                "to-legacy",
                req.GamePaksDir,
                tmpLegacyDir,
                "--filter", AssetFilterStem,
                "--version", "UE5_6",
            });

            // Sanity: retoc must have produced exactly the asset+uexp pair.
            var legacyAssetPath = Path.Combine(tmpLegacyDir,
                PickupAssetVirtualPath.Replace('/', Path.DirectorySeparatorChar));
            var legacyUexpPath  = Path.ChangeExtension(legacyAssetPath, ".uexp");
            if (!File.Exists(legacyAssetPath))
                throw new InvalidOperationException(
                    "retoc to-legacy did not produce the expected asset at "
                    + legacyAssetPath
                    + " -- the game container may have moved the asset, "
                    + "or the filter '" + AssetFilterStem + "' is wrong.");
            if (!File.Exists(legacyUexpPath))
                throw new InvalidOperationException(
                    "retoc to-legacy produced a uasset but no uexp at "
                    + legacyUexpPath);

            // Step 2: patch the Legacy asset in-place
            LogLine("Patching MagnetRadius -> " + req.MagnetRadius + " in " + Path.GetFileName(legacyAssetPath));
            var patcher = new PickupBlueprintPatcher();
            patcher.Log = Log;
            var patchResult = patcher.Patch(legacyAssetPath, legacyAssetPath, req.UsmapPath, req.MagnetRadius);

            // Step 3: pack as IoStore. retoc to-zen wants the .utoc target;
            // it auto-creates the sibling .pak and .ucas.
            LogLine("retoc to-zen: " + tmpLegacyDir + " -> " + outUtoc);
            RunRetoc(req.RetocExe, new[]
            {
                "to-zen",
                "--version", "UE5_6",
                tmpLegacyDir,
                outUtoc,
            });

            if (!File.Exists(outPak) || !File.Exists(outUcas) || !File.Exists(outUtoc))
            {
                throw new InvalidOperationException(
                    "retoc to-zen reported success but one or more triplet files are missing:\n"
                    + "  pak : " + outPak + " exists=" + File.Exists(outPak) + "\n"
                    + "  ucas: " + outUcas + " exists=" + File.Exists(outUcas) + "\n"
                    + "  utoc: " + outUtoc + " exists=" + File.Exists(outUtoc));
            }

            return new PickupTripletResult
            {
                PakPath  = outPak,
                UcasPath = outUcas,
                UtocPath = outUtoc,
                PakSize  = new FileInfo(outPak).Length,
                UcasSize = new FileInfo(outUcas).Length,
                UtocSize = new FileInfo(outUtoc).Length,
                MagnetRadius = req.MagnetRadius,
                PatchResult = patchResult,
                LegacyTempDir = tmpLegacyDir,
            };
        }

        void RunRetoc(string retocExe, string[] args)
        {
            var psi = new ProcessStartInfo
            {
                FileName = retocExe,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            foreach (var a in args) psi.ArgumentList.Add(a);

            var proc = Process.Start(psi);
            var stdout = proc.StandardOutput.ReadToEnd();
            var stderr = proc.StandardError.ReadToEnd();
            proc.WaitForExit();

            if (proc.ExitCode != 0)
            {
                throw new InvalidOperationException(
                    "retoc " + args[0] + " failed (exit " + proc.ExitCode + ")\n"
                    + (string.IsNullOrEmpty(stderr) ? stdout : stderr));
            }
        }

        void LogLine(string msg)
        {
            if (Log != null) Log(msg);
        }
    }

    public sealed class PickupTripletRequest
    {
        // Absolute path to retoc.exe (resolved via RetocResolver).
        public string RetocExe;
        // Game's Paks directory, e.g. <Steam>/.../Windrose/R5/Content/Paks.
        // Must contain global.utoc (retoc reads ScriptObjects from it).
        public string GamePaksDir;
        // Newest *.usmap in the mod root (UE5_6, UAssetAPI uses it for
        // unversioned-property layouts).
        public string UsmapPath;
        // Output triplet base path; must end with ".pak". The .ucas/.utoc
        // siblings are derived by extension swap.
        public string OutputPakPath;
        // Final MagnetRadius value to write (e.g. 800.0f for the 2x mod).
        // Vanilla baseline is 400.0f -- caller computes vanilla * multiplier.
        public float MagnetRadius;
        // Working dir for the per-build legacy/ sub-tree. Pipeline points
        // this at <BuildTmp>/<profileId>/_pickup so it's wiped together
        // with the rest of the per-profile temp space.
        public string TempDir;
        // Whether to delete pre-existing output triplet files. Defaults
        // to true via the calling pipeline; set false in tests that want
        // to fail loudly on stale leftovers.
        public bool Overwrite = true;
    }

    public sealed class PickupTripletResult
    {
        public string PakPath;
        public string UcasPath;
        public string UtocPath;
        public long PakSize;
        public long UcasSize;
        public long UtocSize;
        public float MagnetRadius;
        public PickupBlueprintPatchResult PatchResult;
        // Temp dir that holds the patched Legacy bytes; useful for
        // post-mortem when keepTemp=true on the build pipeline.
        public string LegacyTempDir;
    }
}
