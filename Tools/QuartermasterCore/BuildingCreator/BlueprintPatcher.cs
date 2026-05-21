using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using UAssetAPI;
using UAssetAPI.ExportTypes;
using UAssetAPI.PropertyTypes.Objects;
using UAssetAPI.UnrealTypes;
using UAssetAPI.Unversioned;

namespace Windrose.Quartermaster.Core.BuildingCreator
{
    // Etappe J: Clones a vanilla "fire building" Blueprint (e.g.
    // BP_BuildingBlock_FloorTorch_C) under our mod path so we can plug it
    // into a building's DA as ItemClass. The clone inherits everything
    // from the vanilla BP - including the NiagaraComponent (flame FX), the
    // ChildActor for BP_PointLight_TorchFire (flickering light via the
    // MI_LF_Dimming_FireSmall LightFunctionMaterial), and the AudioComponent
    // (ambient loop SFX) - via the SCS-Components serialized in the cooked
    // .uasset/.uexp. We don't need to touch any of those component refs:
    // they live as soft-object/class pointers to vanilla pak content which
    // stays mounted, so the cloned BP picks them up at runtime by-path.
    //
    // What this patcher DOES touch:
    //   - The BP's own self-name and package path in the NameMap (so the
    //     emitted .uasset/.uexp ends up under /Game/Quartermaster/Items/
    //     instead of /Game/Gameplay/Building/Actors/Furniture/).
    //   - The "_C" class-name variant (cooked BPs reference themselves by
    //     both the package stem and the "<stem>_C" class name).
    //   - The CDO name "Default__<stem>_C" if present.
    //   - The FolderName header field via BuildingPatcher.NormalizeAssetSelfPath
    //     so the IoStore resolver finds the package at the new path.
    //   - Etappe J v3: the SOURCE-VANILLA MESH ref baked into the BP's
    //     StaticMeshComponent SCS-Node default value (e.g. SM_TorchT01_01).
    //     The vanilla BP's StaticMesh export carries an ObjectProperty that
    //     resolves through the NameMap to SM_TorchT01_01 + its package path.
    //     We rewrite those FName entries to the user-cooked mesh stem +
    //     path so the cloned BP renders the user's mesh instead of the
    //     vanilla torch. Without this the vanilla torch was rendered ON TOP
    //     of the user mesh (in fact replacing it - empirically verified by
    //     the user's screenshot in the conversation). This is also why
    //     Stage() now runs PER BUILDING (not per preset) - each building
    //     has a different user mesh, so each building gets its own BP clone.
    //
    // What this patcher DOES NOT touch:
    //   - SCS-Component default values (NiagaraComponent.RelativeLocation,
    //     ChildActor offsets, etc.). Phase 1 ships with the vanilla position
    //     - the flame ends up at the same Z-height as the vanilla torch tip
    //     (~150 cm). Phase 2 will read a "flame" socket from the user's
    //     StaticMesh and overwrite these offsets per-building; that needs
    //     a separate SCS-rewrite pass that's gated on the spike's success
    //     (the spike has verified socket parsing works; SCS write is the
    //     next step).
    //
    // Why a wrapper around DataAssetPatcher instead of using it directly:
    //   - DataAssetPatcher takes a dict of replacements; this class
    //     centralizes the BP-specific replacement set + the retoc to-legacy
    //     extraction so the orchestrator just calls Stage() and gets a
    //     ready-to-pak triplet in stagingItemsDir.
    //   - Future BP-specific quirks (e.g. SCS rewrite, ChildActor class-ref
    //     swap) land here without polluting DataAssetPatcher's general
    //     contract.
    public sealed class BlueprintPatcher
    {
        public Action<string> Log;

        // External-tool paths. The orchestrator resolves these once via
        // RetocResolver + UsmapLocator and assigns them before the first
        // Stage() call (same wiring pattern as BuildingPatcher).
        public string RetocExe;
        public string UsmapPath;
        public string VanillaPaksDir;
        public string AesKey;

        // Working dir for per-preset retoc-to-legacy intermediates. Each
        // Stage() call wipes its own subdir under this so repeat builds
        // don't accumulate stale extracts.
        public string TempDir;

        // Stages one vanilla BP as a cloned triplet under stagingItemsDir,
        // PER BUILDING. The clone stem is derived from buildingId
        // (BP_QmFlaming_<BuildingId>) so two buildings with the same flame
        // preset still get distinct clones - each with its own user-mesh
        // rewritten into the StaticMeshComponent SCS-Node defaults.
        //
        // userMeshStem / userMeshPath identify the user-cooked StaticMesh
        // the building displays. The patcher swaps the BP's hardcoded
        // vanilla-mesh refs (preset.SourceVanillaMeshStem +
        // preset.SourceVanillaMeshPath) for these via the NameMap rewrite -
        // the BP's StaticMeshComponent then resolves to the user mesh
        // at runtime instead of the vanilla torch mesh.
        public BlueprintStageResult Stage(
            FlamePresetCatalog.FlamePreset preset,
            string buildingId,
            string userMeshStem,
            string userMeshPath,
            string stagingItemsDir)
        {
            if (preset == null) throw new ArgumentNullException("preset");
            if (string.IsNullOrWhiteSpace(buildingId)) throw new ArgumentNullException("buildingId");
            if (string.IsNullOrWhiteSpace(userMeshStem)) throw new ArgumentNullException("userMeshStem");
            if (string.IsNullOrWhiteSpace(userMeshPath)) throw new ArgumentNullException("userMeshPath");
            if (string.IsNullOrEmpty(stagingItemsDir)) throw new ArgumentNullException("stagingItemsDir");
            EnsureToolingReady();

            Directory.CreateDirectory(stagingItemsDir);

            var cloneStem  = FlamePresetCatalog.FlamePreset.ClonedBpStemFor(buildingId);
            var clonePath  = FlamePresetCatalog.FlamePreset.ClonedPackagePathFor(buildingId);
            var classPath  = FlamePresetCatalog.FlamePreset.ClonedClassPathFor(buildingId);
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

            LogLine("=== [Flame:" + preset.Id + ":" + buildingId + "] Step 1: extract vanilla BP '"
                + preset.VanillaBpStem + "' ===");
            var perBuildingTemp = Path.Combine(TempDir ?? Path.GetTempPath(),
                "qm-flame-" + preset.Id + "-" + buildingId);
            if (Directory.Exists(perBuildingTemp)) Directory.Delete(perBuildingTemp, true);
            Directory.CreateDirectory(perBuildingTemp);

            var legacyBpPath = ExtractVanillaBlueprint(preset.VanillaBpStem, perBuildingTemp);

            LogLine("=== [Flame:" + preset.Id + ":" + buildingId + "] Step 2: rewrite NameMap and FolderName ===");

            // Replacement set. We rewrite three flavours of the BP's own
            // identity so all internal cross-refs and the cooked-load
            // resolver agree on the new path:
            //   - bare stem:  "BP_BuildingBlock_FloorTorch"
            //   - class name: "BP_BuildingBlock_FloorTorch_C"
            //   - full path:  "/Game/.../BP_BuildingBlock_FloorTorch"
            //   - CDO name:   "Default__BP_BuildingBlock_FloorTorch_C"
            //
            // Plus (Etappe J v3): redirect the vanilla mesh refs baked into
            // the BP's StaticMeshComponent SCS-Node default to the user
            // mesh so the rendered actor uses the user's mesh.
            //
            // The CDO entry is optional - some BPs cook without it inline
            // in the NameMap when the CDO export references its own
            // ObjectName via FName.Number suffix. requireAllHits=false
            // turns missing entries into warnings instead of fatal.
            var replacements = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [preset.VanillaBpStem]                  = cloneStem,
                [preset.VanillaBpStem + "_C"]           = cloneStem + "_C",
                [preset.VanillaBpPath]                  = clonePath,
                ["Default__" + preset.VanillaBpStem + "_C"] = "Default__" + cloneStem + "_C",
            };

            // Etappe J v3: vanilla -> user mesh rewrite. Both flavours
            // (stem + full path) must move; otherwise the BP's NameMap
            // resolves only one half and the StaticMesh ObjectProperty
            // points at a half-cooked path that fails to load.
            if (!string.IsNullOrEmpty(preset.SourceVanillaMeshStem)
                && !string.IsNullOrEmpty(preset.SourceVanillaMeshPath))
            {
                replacements[preset.SourceVanillaMeshStem] = userMeshStem;
                replacements[preset.SourceVanillaMeshPath] = userMeshPath;
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
                // CDO miss is fine (NameMap might not carry it inline).
                // Stem / class-name / path miss is alarming - log loud.
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

            return result;
        }

        // -----------------------------------------------------------------
        // Historical note (Etappe J v1 -> v2): An earlier revision exposed
        // a `RewriteItemClassOnDataAsset(...)` helper that opened the
        // staged DA after the BuildingPatcher run, located the main
        // NormalExport, and added/replaced a SoftObjectProperty named
        // "ItemClass" via UAssetAPI's property-level API.
        //
        // That approach hit a blocker:
        //
        //   R5BuildingItem DAs surface in UAssetAPI as a single RawExport
        //   (NOT NormalExport), because the trailing CollisionApproximation
        //   property uses a custom C++ Serialize() the unversioned-property
        //   walker can't pass. So the SoftObjectPropertyData add-or-replace
        //   never had a NormalExport to operate on - the call returned
        //   "No NormalExport found in DA - cannot write ItemClass" for
        //   every flame-enabled building.
        //
        // The v2 fix bypasses property-level mutation entirely:
        //
        //   1. When FlamePresetId is set on a building, the orchestrator
        //      OVERRIDES the user's chosen template with the FlamePreset's
        //      source flame-DA (e.g. DA_BI_FloorTorch). The vanilla flame
        //      DA already has ItemClass set + its FSoftClassPath FName
        //      entries in the NameMap.
        //   2. The orchestrator adds extra NameMap rewrites to
        //      BuildingInputs.ExtraDaNameMapRewrites that redirect those
        //      vanilla BP FName entries to our cloned BP's stem + path.
        //   3. BuildingPatcher's existing DataAssetPatcher run picks them
        //      up alongside the regular mesh/icon/recipe rewrites - one
        //      single open/rewrite/write pass, no NormalExport needed.
        //
        // See FlamePresetCatalog.FlamePreset.ApplyTo() and the buildings-
        // step body in BuildPipeline.cs for the wiring.
        // -----------------------------------------------------------------

        // -----------------------------------------------------------------
        // Internal helpers.
        // -----------------------------------------------------------------

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
        // Etappe J v3: BP-Clones are now per-building, so the result
        // carries the building id alongside the preset id.
        public string BuildingId;
        public string VanillaBpStem;
        public string ClonedBpStem;
        public string ClonedClassPath;

        // True if a previous Stage() call had already produced the clone -
        // the second call is a no-op (idempotency). Per-building staging
        // means this is only true on re-runs with the same BuildingId
        // (not across buildings).
        public bool AlreadyStaged;

        public int NameMapRenames;
        public int ExportsRetargeted;

        public string StagedAssetPath;
        public string StagedUexpPath;

        public List<string> Warnings;
    }
}
