using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using UAssetAPI;
using UAssetAPI.ExportTypes;
using UAssetAPI.PropertyTypes.Objects;
using UAssetAPI.PropertyTypes.Structs;
using UAssetAPI.UnrealTypes;
using UAssetAPI.Unversioned;

namespace Windrose.Quartermaster.Core.BuildingCreator
{
    public sealed class BlueprintPatcher
    {
        public Action<string> Log;

        public string RetocExe;
        public string UsmapPath;
        public string VanillaPaksDir;
        public string AesKey;

        public string TempDir;

        public BlueprintStageResult Stage(
            ComponentPresetCatalog.ComponentPreset preset,
            string buildingId,
            string userMeshStem,
            string userMeshPath,
            string stagingItemsDir,
            StaticMeshSocketReader.Socket componentSocket = null,
            BuildingAudioStageResult audioStage = null)
        {
            if (preset == null) throw new ArgumentNullException("preset");
            if (string.IsNullOrWhiteSpace(buildingId)) throw new ArgumentNullException("buildingId");
            if (string.IsNullOrWhiteSpace(userMeshStem)) throw new ArgumentNullException("userMeshStem");
            if (string.IsNullOrWhiteSpace(userMeshPath)) throw new ArgumentNullException("userMeshPath");
            if (string.IsNullOrEmpty(stagingItemsDir)) throw new ArgumentNullException("stagingItemsDir");
            EnsureToolingReady();

            Directory.CreateDirectory(stagingItemsDir);

            var cloneStem  = ComponentPresetCatalog.ComponentPreset.ClonedBpStemFor(preset, buildingId);
            var clonePath  = ComponentPresetCatalog.ComponentPreset.ClonedPackagePathFor(preset, buildingId);
            var classPath  = ComponentPresetCatalog.ComponentPreset.ClonedClassPathFor(preset, buildingId);
            var stagedAsset = Path.Combine(stagingItemsDir, cloneStem + ".uasset");
            var stagedUexp  = Path.Combine(stagingItemsDir, cloneStem + ".uexp");

            var result = new BlueprintStageResult
            {
                PresetId        = preset.Id,
                BuildingId      = buildingId,
                VanillaBpStem   = preset.VanillaBpStem,
                ClonedBpStem    = cloneStem,
                ClonedClassPath = classPath,
                Warnings        = new List<string>(),
            };

            LogLine("=== [" + preset.Kind + ":" + preset.Id + ":" + buildingId + "] Step 1: extract vanilla BP '"
                + preset.VanillaBpStem + "' ===");
            var perBuildingTemp = Path.Combine(TempDir ?? Path.GetTempPath(),
                "qm-" + preset.Kind.ToString().ToLowerInvariant() + "-" + preset.Id + "-" + buildingId);
            if (Directory.Exists(perBuildingTemp)) Directory.Delete(perBuildingTemp, true);
            Directory.CreateDirectory(perBuildingTemp);

            var legacyBpPath = ExtractVanillaBlueprint(preset.VanillaBpStem, perBuildingTemp);

            LogLine("=== [" + preset.Kind + ":" + preset.Id + ":" + buildingId + "] Step 2: rewrite NameMap and FolderName ===");

            // All four identity flavours (stem, _C class, path, CDO) must move together.
            var replacements = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [preset.VanillaBpStem]                  = cloneStem,
                [preset.VanillaBpStem + "_C"]           = cloneStem + "_C",
                [preset.VanillaBpPath]                  = clonePath,
                ["Default__" + preset.VanillaBpStem + "_C"] = "Default__" + cloneStem + "_C",
            };

            // Stem and full path must both move, or the mesh ref half-resolves and fails to load.
            if (!string.IsNullOrEmpty(preset.SourceVanillaMeshStem)
                && !string.IsNullOrEmpty(preset.SourceVanillaMeshPath))
            {
                replacements[preset.SourceVanillaMeshStem] = userMeshStem;
                replacements[preset.SourceVanillaMeshPath] = userMeshPath;
            }

            if (preset.AdditionalSourceVanillaMeshes != null)
            {
                foreach (var extra in preset.AdditionalSourceVanillaMeshes)
                {
                    if (extra == null) continue;
                    if (!string.IsNullOrEmpty(extra.Stem))
                        replacements[extra.Stem] = userMeshStem;
                    if (!string.IsNullOrEmpty(extra.Path))
                        replacements[extra.Path] = userMeshPath;
                }
            }

            if (audioStage != null)
            {
                foreach (var kvp in BuildingAudioStager.NameMapRewritesForBp(audioStage))
                {
                    replacements[kvp.Key] = kvp.Value;
                }
                LogLine("  [Audio] BP NameMap will be retargeted to cue '"
                    + audioStage.CueStem + "' (SWAV stem '" + audioStage.SwavStem + "')");
            }

            var patcher = new DataAssetPatcher { Log = LogLine };
            var patchResult = patcher.Patch(
                inputAssetPath:  legacyBpPath,
                outputAssetPath: stagedAsset,
                usmapPath:       UsmapPath,
                replacements:    replacements,
                newFolderName:   clonePath,
                requireAllHits:  false);

            result.NameMapRenames     = patchResult.NameMapEntriesRenamed;
            result.ExportsRetargeted  = patchResult.ExportsRetargeted;
            result.StagedAssetPath    = stagedAsset;
            result.StagedUexpPath     = stagedUexp;

            if (patchResult.MissedReplacements != null && patchResult.MissedReplacements.Count > 0)
            {
                // A CDO miss is benign; any other miss means the clone may not resolve.
                foreach (var miss in patchResult.MissedReplacements)
                {
                    if (miss.StartsWith("Default__", StringComparison.Ordinal))
                    {
                        LogLine("  (CDO NameMap entry '" + miss + "' absent - normal for some BPs)");
                    }
                    else
                    {
                        result.Warnings.Add("BP '" + preset.VanillaBpStem
                            + "': NameMap entry '" + miss + "' didn't match - the clone may"
                            + " not resolve at the new path");
                    }
                }
            }

            LogLine("[OK] BP cloned: " + result.NameMapRenames + " NameMap renames, "
                + result.ExportsRetargeted + " export retargets -> " + cloneStem
                + " (mesh rewritten to '" + userMeshStem + "')");

            if (componentSocket != null)
            {
                try
                {
                    var patched = PatchSocketTransform(stagedAsset, componentSocket);
                    LogLine("  [" + preset.Kind + "] socket '" + (componentSocket.Name ?? "<noname>")
                        + "' (X=" + Fmt(componentSocket.LocX) + " Y=" + Fmt(componentSocket.LocY) + " Z=" + Fmt(componentSocket.LocZ)
                        + " | Pitch=" + Fmt(componentSocket.Pitch) + " Yaw=" + Fmt(componentSocket.Yaw) + " Roll=" + Fmt(componentSocket.Roll)
                        + " | SX=" + Fmt(componentSocket.ScaleX) + " SY=" + Fmt(componentSocket.ScaleY) + " SZ=" + Fmt(componentSocket.ScaleZ)
                        + ") applied to " + patched + " component(s)");
                    result.ComponentsRetransformed = patched;
                }
                catch (Exception ex)
                {
                    var warn = preset.Kind + " socket transform patch failed: "
                        + ex.GetType().Name + ": " + ex.Message
                        + " - BP keeps vanilla component position";
                    result.Warnings.Add(warn);
                    LogLine("  warn: " + warn);
                }
            }

            return result;
        }

        // Overwrites existing transform properties only; never adds missing ones.
        int PatchSocketTransform(string assetPath, StaticMeshSocketReader.Socket socket)
        {
            if (string.IsNullOrEmpty(UsmapPath) || !File.Exists(UsmapPath))
                throw new InvalidOperationException("UsmapPath missing: " + UsmapPath);
            if (socket == null) return 0;

            var mappings = new Usmap(UsmapPath);
            var asset = new UAsset(assetPath, UAssetIo.Ue, mappings);

            int patched = 0;
            for (int i = 0; i < asset.Exports.Count; i++)
            {
                var ex = asset.Exports[i] as NormalExport;
                if (ex == null) continue;
                if (!IsFlameRelatedComponent(ex)) continue;

                bool touched = false;
                touched |= SetRelativeLocation(ex, socket.LocX, socket.LocY, socket.LocZ);
                touched |= SetRelativeRotation(ex, socket.Pitch, socket.Yaw, socket.Roll);
                touched |= SetRelativeScale3D(ex, socket.ScaleX, socket.ScaleY, socket.ScaleZ);
                if (touched)
                {
                    var classType = ex.GetExportClassType()?.Value?.Value ?? "<unknown>";
                    var name = ex.ObjectName?.Value?.Value ?? "<noname>";
                    LogLine("    component '" + name + "' (" + classType + ") transformed");
                    patched++;
                }
            }

            if (patched > 0)
                asset.Write(assetPath);
            return patched;
        }

        // Matched by class type / name, not export index, to survive a re-cooked BP's export reorder.
        static bool IsFlameRelatedComponent(NormalExport ex)
        {
            var classType = ex.GetExportClassType()?.Value?.Value ?? "";
            var name = ex.ObjectName?.Value?.Value ?? "";

            if (classType.IndexOf("Niagara", StringComparison.OrdinalIgnoreCase) >= 0) return true;
            if (classType.IndexOf("PointLight", StringComparison.OrdinalIgnoreCase) >= 0) return true;
            if (classType.IndexOf("Audio", StringComparison.OrdinalIgnoreCase) >= 0) return true;

            if (name.IndexOf("Light", StringComparison.OrdinalIgnoreCase) >= 0
                && name.IndexOf("Component", StringComparison.OrdinalIgnoreCase) >= 0)
                return true;
            if (name.IndexOf("Flame", StringComparison.OrdinalIgnoreCase) >= 0) return true;

            return false;
        }

        static bool SetRelativeLocation(NormalExport ex, double x, double y, double z)
        {
            foreach (var prop in ex.Data)
            {
                if (prop.Name?.Value?.Value != "RelativeLocation") continue;
                if (prop is StructPropertyData stp
                    && stp.Value != null && stp.Value.Count > 0
                    && stp.Value[0] is VectorPropertyData vp)
                {
                    vp.Value = new FVector(x, y, z);
                    return true;
                }
            }
            return false;
        }

        static bool SetRelativeRotation(NormalExport ex, double pitch, double yaw, double roll)
        {
            foreach (var prop in ex.Data)
            {
                if (prop.Name?.Value?.Value != "RelativeRotation") continue;
                if (prop is StructPropertyData stp
                    && stp.Value != null && stp.Value.Count > 0
                    && stp.Value[0] is RotatorPropertyData rp)
                {
                    rp.Value = new FRotator(pitch, yaw, roll);
                    return true;
                }
            }
            return false;
        }

        static bool SetRelativeScale3D(NormalExport ex, double sx, double sy, double sz)
        {
            foreach (var prop in ex.Data)
            {
                if (prop.Name?.Value?.Value != "RelativeScale3D") continue;
                if (prop is StructPropertyData stp
                    && stp.Value != null && stp.Value.Count > 0
                    && stp.Value[0] is VectorPropertyData vp)
                {
                    vp.Value = new FVector(sx, sy, sz);
                    return true;
                }
            }
            return false;
        }

        static string Fmt(double d)
        {
            return d.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);
        }

        string ExtractVanillaBlueprint(string assetStem, string perPresetTemp)
        {
            var outDir = Path.Combine(perPresetTemp, "legacy");
            if (Directory.Exists(outDir)) Directory.Delete(outDir, true);
            Directory.CreateDirectory(outDir);

            var argv = new List<string>
            {
                "--aes-key", AesKey,
                "to-legacy",
                VanillaPaksDir, outDir,
                "--version", "UE5_6",
                "--filter", assetStem,
            };
            int rc = RunProcess(RetocExe, argv.ToArray());
            if (rc != 0)
            {
                throw new InvalidOperationException(
                    "retoc to-legacy failed for BP '" + assetStem + "' (exit " + rc + ")");
            }

            var found = Directory.GetFiles(outDir, assetStem + ".uasset", SearchOption.AllDirectories);
            if (found.Length == 0)
            {
                throw new InvalidOperationException(
                    "retoc to-legacy produced no " + assetStem + ".uasset under " + outDir
                    + " - is the vanilla BP path right? (preset's VanillaBpPath might be stale)");
            }

            LogLine("  [extract] " + assetStem + " -> " + found[0]);
            return found[0];
        }

        int RunProcess(string exe, string[] argv)
        {
            var psi = new ProcessStartInfo
            {
                FileName = exe,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            foreach (var a in argv) psi.ArgumentList.Add(a);

            using var p = new Process { StartInfo = psi };
            p.OutputDataReceived += (_, e) => { if (e.Data != null) LogLine("    " + e.Data); };
            p.ErrorDataReceived  += (_, e) => { if (e.Data != null) LogLine("    " + e.Data); };
            p.Start();
            p.BeginOutputReadLine();
            p.BeginErrorReadLine();
            p.WaitForExit();
            return p.ExitCode;
        }

        void EnsureToolingReady()
        {
            if (string.IsNullOrEmpty(RetocExe) || !File.Exists(RetocExe))
                throw new InvalidOperationException("BlueprintPatcher: RetocExe not set or missing: " + RetocExe);
            if (string.IsNullOrEmpty(UsmapPath) || !File.Exists(UsmapPath))
                throw new InvalidOperationException("BlueprintPatcher: UsmapPath not set or missing: " + UsmapPath);
            if (string.IsNullOrEmpty(VanillaPaksDir) || !Directory.Exists(VanillaPaksDir))
                throw new InvalidOperationException("BlueprintPatcher: VanillaPaksDir not set or missing: " + VanillaPaksDir);
            if (string.IsNullOrEmpty(AesKey))
                throw new InvalidOperationException("BlueprintPatcher: AesKey not set");
        }

        void LogLine(string s)
        {
            if (Log != null) Log(s);
        }
    }

    public sealed class BlueprintStageResult
    {
        public string PresetId;
        public string BuildingId;
        public string VanillaBpStem;
        public string ClonedBpStem;
        public string ClonedClassPath;

        public bool AlreadyStaged;

        public int NameMapRenames;
        public int ExportsRetargeted;

        public int ComponentsRetransformed;

        public string StagedAssetPath;
        public string StagedUexpPath;

        public List<string> Warnings;
    }
}
