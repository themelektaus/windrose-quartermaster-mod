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
    // Extracted from .build-tmp/da-patch-test/Program.cs - the validated
    // QmPainting build pipeline, generalised so the GUI can invoke it
    // per-Building. Pipeline steps:
    //
    //   1) Stage user-cooked assets (mesh + icon + user textures) from
    //      CookedFolderPath into stagingItemsDir, skipping any user-cooked
    //      materials/MIs (those crash shipping - empirically bisected).
    //   2) Rewrite the user-cooked mesh's NameMap so its per-slot material
    //      refs (M_<prefix>_<SlotName>) point at the cloned-MI stems we'll
    //      generate in step 4.
    //   3) retoc to-legacy --filter <VanillaMaterialStem> to extract the
    //      Vanilla MI (per template - usually one MI shared across all
    //      slots, but we extract once and clone N times).
    //   4) Per slot: DataAssetPatcher renames the Vanilla MI's NameMap so
    //      it lives under our mod path (/Game/Quartermaster/Items/MI_*),
    //      with texture refs swapped to user-custom (if provided) or to
    //      the shared default-VT names. Then patch VectorParameterValues
    //      in-place via UAssetAPI for any template-declared overrides.
    //   5) retoc to-legacy --filter <VanillaDaStem> to extract the Vanilla
    //      DA.
    //   6) DataAssetPatcher rewrites the DA's NameMap so it points at the
    //      user-cooked mesh + icon + the synthesized localization key.
    //
    // What's NOT in this class (orchestrator's responsibility):
    //   - Deleting the staging dir before-hand (we ADD into an existing
    //     dir so multiple buildings + shared defaults can co-exist).
    //   - Shipping the shared default VT textures (T_QmPainting_White /
    //     NormalFlat / MTRMDefault). Those land in staging once per build,
    //     not once per building.
    //   - retoc to-zen (pak build). One pak for the whole profile.
    //   - The CSV-Synthese-Pattern for DisplayName/Description
    //     localization. Mirrors ItemCreatorPatcher's StringTable approach,
    //     handled by the orchestrator.
    //   - GameDeployer (.pak triple + dxgi.dll + qm_items.json).
    public sealed class BuildingPatcher
    {
        public Action<string> Log;

        // External-tool paths. The orchestrator resolves these once via
        // RetocResolver + UsmapLocator and hands them to every per-building
        // patch call.
        public string RetocExe;
        public string UsmapPath;
        public string VanillaPaksDir;
        public string AesKey;

        // Working dir for retoc-to-legacy intermediates. Each Patch() call
        // creates per-building subdirs underneath this (so two parallel
        // calls don't trample each other).
        public string TempDir;

        // Per-building entry point. Writes patched assets into
        // stagingItemsDir (which the orchestrator typically points at
        // <staging>/R5/Content/Quartermaster/Items/).
        //
        // Returns a structured result the orchestrator can fold into the
        // SSE-streamed Build report.
        public BuildingPatchResult Patch(
            BuildingTemplate template,
            BuildingInputs inputs,
            string stagingItemsDir)
        {
            if (template == null)            throw new ArgumentNullException("template");
            if (inputs == null)              throw new ArgumentNullException("inputs");
            if (string.IsNullOrEmpty(stagingItemsDir)) throw new ArgumentNullException("stagingItemsDir");

            EnsureToolingReady();
            ValidateInputs(template, inputs);

            Directory.CreateDirectory(stagingItemsDir);
            var perBuildingTemp = Path.Combine(TempDir ?? Path.GetTempPath(), "qm-building-" + inputs.BuildingId);
            if (Directory.Exists(perBuildingTemp)) Directory.Delete(perBuildingTemp, true);
            Directory.CreateDirectory(perBuildingTemp);

            var result = new BuildingPatchResult
            {
                BuildingId   = inputs.BuildingId,
                TemplateId   = template.Id,
                OutputDaStem = "DA_BI_" + inputs.BuildingId,
                StagedFiles  = new List<string>(),
                Warnings     = new List<string>(),
            };

            // ---- Step 1: stage user-cooked assets ----------------------
            LogLine("=== [" + inputs.BuildingId + "] Step 1: stage user-cooked assets ===");
            StageCookedAssets(inputs, stagingItemsDir, result);

            // ---- Step 2: rewrite mesh material slots -------------------
            LogLine("=== [" + inputs.BuildingId + "] Step 2: rewrite mesh material slots ===");
            PatchMeshMaterialSlots(template, inputs, stagingItemsDir, result);

            // ---- Step 3: extract Vanilla MI ----------------------------
            // Templates can in theory have multiple distinct Vanilla MIs
            // (one per slot), but in practice Painting reuses
            // MI_Paintings_01 across both slots. We cache by stem so we
            // only retoc-extract each distinct MI once per Patch() call.
            LogLine("=== [" + inputs.BuildingId + "] Step 3: extract Vanilla MIs ===");
            var vanillaMiCache = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var slot in template.Slots)
            {
                if (vanillaMiCache.ContainsKey(slot.VanillaMaterialStem)) continue;
                var legacyMiPath = ExtractVanillaAsset(slot.VanillaMaterialStem, perBuildingTemp, "mi-" + slot.SlotName);
                vanillaMiCache[slot.VanillaMaterialStem] = legacyMiPath;
            }

            // ---- Step 4: clone + patch + tint per-slot MIs -------------
            LogLine("=== [" + inputs.BuildingId + "] Step 4: clone + patch + tint per-slot MIs ===");
            foreach (var slot in template.Slots)
            {
                ClonePatchAndTintSlot(template, inputs, slot, vanillaMiCache[slot.VanillaMaterialStem], stagingItemsDir, result);
            }

            // ---- Step 5: extract Vanilla DA ----------------------------
            LogLine("=== [" + inputs.BuildingId + "] Step 5: extract Vanilla DA ===");
            var legacyDaPath = ExtractVanillaAsset(template.VanillaDaStem, perBuildingTemp, "da");

            // ---- Step 6: clone + patch DA ------------------------------
            LogLine("=== [" + inputs.BuildingId + "] Step 6: clone + patch DA ===");
            PatchDataAsset(template, inputs, legacyDaPath, stagingItemsDir, result);

            LogLine("[OK] Building '" + inputs.BuildingId + "' patched: "
                + result.StagedFiles.Count + " files staged"
                + (result.Warnings.Count > 0 ? ", " + result.Warnings.Count + " warning(s)" : ""));

            return result;
        }

        // -----------------------------------------------------------------
        // Step 1: copy user-cooked files into staging, skipping the known
        // crash-trigger user-cooked materials/MIs that the GUI will replace
        // with Vanilla-MI clones (Step 4).
        // -----------------------------------------------------------------
        void StageCookedAssets(BuildingInputs inputs, string stagingItemsDir, BuildingPatchResult result)
        {
            if (!Directory.Exists(inputs.CookedFolderPath))
                throw new DirectoryNotFoundException(
                    "Cooked folder not found: " + inputs.CookedFolderPath
                    + " - cook /Game/Quartermaster/Items/ in the UE editor first.");

            // The cooked-folder is the project-relative path the user
            // picked. We greedy-match by asset-prefix (Punkt 7 of the
            // planning doc: "User gibt Asset-Stamm-Praefix").
            //
            // Skip-set is computed from the per-slot user material stems:
            // those are the only user-cooked files we know upfront will
            // crash the game. Everything else (mesh, icon, textures) goes
            // through.
            var skipStems = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (inputs.SkipUserCookedMaterialStems != null)
            {
                foreach (var s in inputs.SkipUserCookedMaterialStems)
                {
                    if (!string.IsNullOrWhiteSpace(s)) skipStems.Add(s);
                }
            }

            int copied = 0;
            int skipped = 0;
            int rejected = 0;
            var rejectedSample = new List<string>();
            foreach (var f in Directory.GetFiles(inputs.CookedFolderPath))
            {
                var name = Path.GetFileName(f);
                var stem = Path.GetFileNameWithoutExtension(name);

                // Asset-prefix filter: match the user prefix as a NAME
                // COMPONENT inside the stem - not via StartsWith. UE files
                // are named with a type prefix (SM_/T_/M_/MI_/DA_/...) +
                // user project token + suffix, e.g.:
                //   SM_QmPainting_01  T_QmPainting_Icon  M_QmPainting_Canvas
                // The project token "QmPainting" is the second component,
                // so the original StartsWith check rejected every file.
                // Boundary-rule:
                //   - left of the match must be start-of-stem or '_'
                //   - right of the match must be end-of-stem or '_' or '.'
                // Empty prefix => take everything.
                if (!string.IsNullOrEmpty(inputs.AssetPrefix) &&
                    !StemContainsPrefixAsComponent(stem, inputs.AssetPrefix))
                {
                    rejected++;
                    if (rejectedSample.Count < 5) rejectedSample.Add(name);
                    continue;
                }

                if (skipStems.Contains(stem))
                {
                    LogLine("  [SKIP] " + name + "  (user-cooked custom material - crashes shipping, replaced by Vanilla-MI clone)");
                    skipped++;
                    continue;
                }

                var dst = Path.Combine(stagingItemsDir, name);
                File.Copy(f, dst, overwrite: true);
                LogLine("  [copy] " + name);
                result.StagedFiles.Add(name);
                copied++;
            }

            if (copied == 0)
            {
                // Build a directory-listing snapshot so the user can see
                // immediately what's actually in the folder vs what they
                // typed as prefix.
                var allFiles = Directory.GetFiles(inputs.CookedFolderPath);
                var sample = new List<string>();
                foreach (var f in allFiles)
                {
                    if (sample.Count >= 10) break;
                    sample.Add(Path.GetFileName(f));
                }
                var sampleMsg = allFiles.Length == 0
                    ? "(folder is empty)"
                    : string.Join(", ", sample) + (allFiles.Length > sample.Count ? ", ..." : "");

                throw new InvalidOperationException(
                    "No files matched asset-prefix '" + (inputs.AssetPrefix ?? "<empty>")
                    + "' in cooked folder " + inputs.CookedFolderPath
                    + " - check the prefix or re-cook the assets. "
                    + "Files in folder: " + sampleMsg);
            }
            LogLine("[OK] " + copied + " file(s) staged"
                + (skipped > 0 ? " (" + skipped + " user-cooked material(s) skipped)" : "")
                + (rejected > 0 ? " (" + rejected + " file(s) didn't match prefix"
                    + (rejectedSample.Count > 0 ? ", e.g. " + string.Join(", ", rejectedSample) : "") + ")" : ""));
        }

        // Returns true if `prefix` appears as a name component in `stem`:
        // i.e. surrounded by '_' (or start/end of stem). Case-insensitive.
        // Matches "QmPainting" against "SM_QmPainting_01", "T_QmPainting",
        // "DA_BI_QmPainting_01", "QmPainting_01", "QmPainting"; rejects
        // "SM_QmPaintingTest" (no right boundary) and "SomethingQmPainting"
        // (no left boundary).
        static bool StemContainsPrefixAsComponent(string stem, string prefix)
        {
            if (string.IsNullOrEmpty(stem) || string.IsNullOrEmpty(prefix)) return false;
            int from = 0;
            while (from <= stem.Length - prefix.Length)
            {
                int idx = stem.IndexOf(prefix, from, StringComparison.OrdinalIgnoreCase);
                if (idx < 0) return false;
                bool leftOk  = idx == 0 || stem[idx - 1] == '_';
                int end = idx + prefix.Length;
                bool rightOk = end == stem.Length || stem[end] == '_' || stem[end] == '.';
                if (leftOk && rightOk) return true;
                from = idx + 1;
            }
            return false;
        }

        // -----------------------------------------------------------------
        // Step 2: in-place patch the user-cooked mesh's NameMap to swap its
        // per-slot material refs (M_<prefix>_<SlotName>) for the cloned MI
        // stems (MI_<prefix>_<SlotName>) that Step 4 will generate.
        // -----------------------------------------------------------------
        void PatchMeshMaterialSlots(BuildingTemplate template, BuildingInputs inputs,
                                    string stagingItemsDir, BuildingPatchResult result)
        {
            var meshFileName = inputs.MeshStem + ".uasset";
            var meshInStaging = Path.Combine(stagingItemsDir, meshFileName);
            if (!File.Exists(meshInStaging))
                throw new FileNotFoundException(
                    "Mesh not found in staging: " + meshInStaging
                    + " - expected the user-cooked SM_<prefix>.uasset at MeshStem='" + inputs.MeshStem + "'.");

            // Build replacements per slot: every user-cooked slot material
            // ref (M_<prefix>_<SlotName>) -> MI_<prefix>_<SlotName>, both as
            // bare stem and full path.
            var meshReplacements = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var slot in template.Slots)
            {
                var userStem = BuildSlotUserMaterialStem(inputs, slot);
                var userPath = BuildSlotUserMaterialPath(inputs, slot);
                var cloneStem = BuildSlotCloneStem(inputs, slot);
                var clonePath = BuildSlotClonePath(inputs, slot);

                meshReplacements[userStem] = cloneStem;
                meshReplacements[userPath] = clonePath;
            }

            var meshPatcher = new DataAssetPatcher { Log = LogLine };
            var meshPr = meshPatcher.Patch(
                inputAssetPath:  meshInStaging,
                outputAssetPath: meshInStaging, // in-place
                usmapPath:       UsmapPath,
                replacements:    meshReplacements,
                newFolderName:   null, // mesh keeps its own FolderName
                requireAllHits:  false);

            LogLine("[OK] Mesh patched: " + meshPr.NameMapEntriesRenamed
                + " NameMap renames, " + meshPr.ExportsRetargeted + " export retargets");

            if (meshPr.MissedReplacements != null && meshPr.MissedReplacements.Count > 0)
            {
                result.Warnings.Add(
                    "Mesh " + meshFileName + ": " + meshPr.MissedReplacements.Count
                    + " replacement key(s) didn't match (likely an unused slot in the user mesh) - "
                    + string.Join(", ", meshPr.MissedReplacements));
            }
        }

        // -----------------------------------------------------------------
        // Step 3: retoc to-legacy --filter <stem> to extract a Vanilla
        // asset into <perBuildingTemp>/<subDirName>/. Returns the absolute
        // path to the extracted .uasset.
        // -----------------------------------------------------------------
        string ExtractVanillaAsset(string assetStem, string perBuildingTemp, string subDirName)
        {
            var outDir = Path.Combine(perBuildingTemp, "legacy-" + subDirName);
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
                throw new InvalidOperationException(
                    "retoc to-legacy failed for '" + assetStem + "' (exit " + rc + ")");

            var found = Directory.GetFiles(outDir, assetStem + ".uasset", SearchOption.AllDirectories);
            if (found.Length == 0)
                throw new InvalidOperationException(
                    "retoc to-legacy produced no " + assetStem + ".uasset under " + outDir);

            LogLine("  [extract] " + assetStem + " -> " + found[0]);
            return found[0];
        }

        // -----------------------------------------------------------------
        // Step 4: clone the extracted Vanilla MI under the slot's clone
        // path, swap its texture refs (Albedo/Normal/MTRM) to either the
        // user-custom texture or the shared default-VT names, then patch
        // template-declared VectorParameterValues in-place via UAssetAPI.
        // -----------------------------------------------------------------
        void ClonePatchAndTintSlot(BuildingTemplate template, BuildingInputs inputs,
                                   MaterialSlotTemplate slot, string legacyMiPath,
                                   string stagingItemsDir, BuildingPatchResult result)
        {
            var cloneStem  = BuildSlotCloneStem(inputs, slot);
            var clonePath  = BuildSlotClonePath(inputs, slot);
            var cloneFile  = Path.Combine(stagingItemsDir, cloneStem + ".uasset");

            inputs.SlotInputs.TryGetValue(slot.SlotName, out var slotInputs);

            // Per-slot replacements: self-name+path, plus the three texture
            // refs swapped to user-custom OR shared defaults.
            var matReplacements = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [slot.VanillaMaterialStem] = cloneStem,
                [slot.VanillaMaterialPath] = clonePath,
            };

            // Albedo
            var albedoStem = (slotInputs != null && !string.IsNullOrWhiteSpace(slotInputs.CustomAlbedoStem))
                ? slotInputs.CustomAlbedoStem : inputs.DefaultAlbedoStem;
            var albedoPath = (slotInputs != null && !string.IsNullOrWhiteSpace(slotInputs.CustomAlbedoPath))
                ? slotInputs.CustomAlbedoPath : inputs.DefaultAlbedoPath;
            matReplacements[slot.VanillaAlbedoStem] = albedoStem;
            matReplacements[slot.VanillaAlbedoPath] = albedoPath;

            // Normal
            var normalStem = (slotInputs != null && !string.IsNullOrWhiteSpace(slotInputs.CustomNormalStem))
                ? slotInputs.CustomNormalStem : inputs.DefaultNormalStem;
            var normalPath = (slotInputs != null && !string.IsNullOrWhiteSpace(slotInputs.CustomNormalPath))
                ? slotInputs.CustomNormalPath : inputs.DefaultNormalPath;
            matReplacements[slot.VanillaNormalStem] = normalStem;
            matReplacements[slot.VanillaNormalPath] = normalPath;

            // MTRM
            var mtrmStem = (slotInputs != null && !string.IsNullOrWhiteSpace(slotInputs.CustomMtrmStem))
                ? slotInputs.CustomMtrmStem : inputs.DefaultMtrmStem;
            var mtrmPath = (slotInputs != null && !string.IsNullOrWhiteSpace(slotInputs.CustomMtrmPath))
                ? slotInputs.CustomMtrmPath : inputs.DefaultMtrmPath;
            matReplacements[slot.VanillaMtrmStem] = mtrmStem;
            matReplacements[slot.VanillaMtrmPath] = mtrmPath;

            LogLine("  [slot " + slot.SlotName + "] cloning " + slot.VanillaMaterialStem + " -> " + cloneStem);
            LogLine("    Albedo -> " + albedoStem);
            LogLine("    Normal -> " + normalStem);
            LogLine("    MTRM   -> " + mtrmStem);

            var patcher = new DataAssetPatcher { Log = LogLine };
            var pr = patcher.Patch(
                inputAssetPath:  legacyMiPath,
                outputAssetPath: cloneFile,
                usmapPath:       UsmapPath,
                replacements:    matReplacements,
                newFolderName:   clonePath,
                requireAllHits:  false);

            LogLine("  [OK] Slot '" + slot.SlotName + "' patched: " + pr.NameMapEntriesRenamed
                + " NameMap renames, " + pr.ExportsRetargeted + " export retargets");

            result.StagedFiles.Add(cloneStem + ".uasset");
            result.StagedFiles.Add(cloneStem + ".uexp");

            if (pr.MissedReplacements != null && pr.MissedReplacements.Count > 0)
            {
                result.Warnings.Add(
                    "MI clone " + cloneStem + ": " + pr.MissedReplacements.Count
                    + " replacement key(s) didn't match - "
                    + string.Join(", ", pr.MissedReplacements));
            }

            // Vector-parameter overrides (Edge Color / AO Color / ...)
            if (slot.VectorOverrides != null && slot.VectorOverrides.Count > 0)
            {
                var mapping = new Usmap(UsmapPath);
                var miAsset = new UAsset(cloneFile, EngineVersion.VER_UE5_6, mapping);
                int hits = 0;
                foreach (var vo in slot.VectorOverrides)
                {
                    hits += PatchMiVectorParameter(miAsset, vo.Name, vo.R, vo.G, vo.B, vo.A);
                }
                if (hits > 0)
                {
                    miAsset.Write(cloneFile);
                    LogLine("  [OK] Slot '" + slot.SlotName + "' vector params patched (" + hits + " override(s))");
                }
                else
                {
                    LogLine("  [!] Slot '" + slot.SlotName + "' - no vector params matched (template intent vs MI shape drift?)");
                    result.Warnings.Add(
                        "MI clone " + cloneStem + ": "
                        + slot.VectorOverrides.Count + " vector override(s) declared in template, none matched in MI.");
                }
            }
        }

        // -----------------------------------------------------------------
        // Step 6: clone the Vanilla DA under the Building's output DA stem,
        // rewriting NameMap entries so mesh / icon / self-refs and the
        // localization key all point at our mod assets.
        // -----------------------------------------------------------------
        void PatchDataAsset(BuildingTemplate template, BuildingInputs inputs,
                            string legacyDaPath, string stagingItemsDir, BuildingPatchResult result)
        {
            var outDaStem = "DA_BI_" + inputs.BuildingId;
            var outDaPath = "/Game/Quartermaster/Items/" + outDaStem;
            var outDaFile = Path.Combine(stagingItemsDir, outDaStem + ".uasset");

            // Output mesh / icon stems (the ones the user-cooked assets
            // actually carry). Note: per the spike convention these don't
            // necessarily share inputs.BuildingId - the icon is typically
            // T_<AssetPrefix>_Icon, the mesh SM_<AssetPrefix>_<n>.
            var outMeshStem = inputs.MeshStem;
            var outMeshPath = "/Game/Quartermaster/Items/" + outMeshStem;
            var outIconStem = inputs.IconStem;
            var outIconPath = "/Game/Quartermaster/Items/" + outIconStem;
            var outNameKey  = "Decoration_" + inputs.BuildingId + "_Name";

            var daReplacements = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [template.VanillaMeshStem] = outMeshStem,
                [template.VanillaMeshPath] = outMeshPath,

                [template.VanillaIconStem] = outIconStem,
                [template.VanillaIconPath] = outIconPath,

                [template.VanillaDaStem] = outDaStem,
                [template.VanillaDaPath] = outDaPath,

                [template.VanillaNameKey] = outNameKey,
            };

            var patcher = new DataAssetPatcher { Log = LogLine };
            var pr = patcher.Patch(
                inputAssetPath:  legacyDaPath,
                outputAssetPath: outDaFile,
                usmapPath:       UsmapPath,
                replacements:    daReplacements,
                newFolderName:   outDaPath,
                requireAllHits:  false);

            LogLine("[OK] DA patched: " + pr.NameMapEntriesRenamed
                + " NameMap renames, " + pr.ExportsRetargeted + " export retargets");

            result.StagedFiles.Add(outDaStem + ".uasset");
            result.StagedFiles.Add(outDaStem + ".uexp");
            result.OutputDaStem = outDaStem;
            result.OutputDaPath = outDaPath;
            result.OutputNameKey = outNameKey;

            if (pr.MissedReplacements != null && pr.MissedReplacements.Count > 0)
            {
                result.Warnings.Add(
                    "DA " + outDaStem + ": " + pr.MissedReplacements.Count
                    + " replacement key(s) didn't match - "
                    + string.Join(", ", pr.MissedReplacements));
            }
        }

        // -----------------------------------------------------------------
        // Slot stem/path naming helpers. Centralised so the mesh-patch and
        // MI-clone code agree on the spelling.
        //
        // User-cooked mesh's slot ref: "M_<AssetPrefix>_<SlotName>"
        //   e.g. AssetPrefix="QmPainting", SlotName="Canvas"
        //   -> "M_QmPainting_Canvas"
        //
        // MI clone we generate:        "MI_<AssetPrefix>_<SlotName>"
        //   -> "MI_QmPainting_Canvas"
        // -----------------------------------------------------------------
        static string BuildSlotUserMaterialStem(BuildingInputs inputs, MaterialSlotTemplate slot)
            => "M_" + inputs.AssetPrefix + "_" + slot.SlotName;

        static string BuildSlotUserMaterialPath(BuildingInputs inputs, MaterialSlotTemplate slot)
            => "/Game/Quartermaster/Items/" + BuildSlotUserMaterialStem(inputs, slot);

        static string BuildSlotCloneStem(BuildingInputs inputs, MaterialSlotTemplate slot)
            => "MI_" + inputs.AssetPrefix + "_" + slot.SlotName;

        static string BuildSlotClonePath(BuildingInputs inputs, MaterialSlotTemplate slot)
            => "/Game/Quartermaster/Items/" + BuildSlotCloneStem(inputs, slot);

        // -----------------------------------------------------------------
        // UAssetAPI helper: locate a VectorParameterValues entry by
        // ParameterInfo.Name and overwrite its embedded LinearColor.
        // Extracted from Program.cs:PatchMiVectorParameter unchanged.
        // -----------------------------------------------------------------
        static int PatchMiVectorParameter(UAsset asset, string paramName,
            float r, float g, float b, float a)
        {
            int hits = 0;
            foreach (var ex in asset.Exports)
            {
                if (!(ex is NormalExport ne)) continue;
                foreach (var prop in ne.Data)
                {
                    if (!(prop is ArrayPropertyData arr)) continue;
                    if (arr.Name?.Value?.Value != "VectorParameterValues") continue;
                    if (arr.Value == null) continue;

                    foreach (var item in arr.Value)
                    {
                        if (!(item is StructPropertyData entry)) continue;
                        if (entry.Value == null) continue;

                        string foundName = null;
                        StructPropertyData paramValueStruct = null;
                        foreach (var sub in entry.Value)
                        {
                            if (sub is StructPropertyData sps && sub.Name?.Value?.Value == "ParameterInfo")
                            {
                                foreach (var pis in sps.Value ?? new List<PropertyData>())
                                {
                                    if (pis is NamePropertyData np && pis.Name?.Value?.Value == "Name")
                                    {
                                        foundName = np.Value?.Value?.Value;
                                    }
                                }
                            }
                            else if (sub is StructPropertyData pvs && sub.Name?.Value?.Value == "ParameterValue")
                            {
                                paramValueStruct = pvs;
                            }
                        }
                        if (foundName != paramName || paramValueStruct == null) continue;

                        foreach (var inner in paramValueStruct.Value ?? new List<PropertyData>())
                        {
                            if (inner is LinearColorPropertyData lc)
                            {
                                lc.Value = new FLinearColor(r, g, b, a);
                                hits++;
                            }
                        }
                    }
                }
            }
            return hits;
        }

        // -----------------------------------------------------------------
        // Validation: catch caller errors before we touch disk.
        // -----------------------------------------------------------------
        void EnsureToolingReady()
        {
            if (string.IsNullOrEmpty(RetocExe) || !File.Exists(RetocExe))
                throw new InvalidOperationException("BuildingPatcher.RetocExe not set or not found: " + RetocExe);
            if (string.IsNullOrEmpty(UsmapPath) || !File.Exists(UsmapPath))
                throw new InvalidOperationException("BuildingPatcher.UsmapPath not set or not found: " + UsmapPath);
            if (string.IsNullOrEmpty(VanillaPaksDir) || !Directory.Exists(VanillaPaksDir))
                throw new InvalidOperationException("BuildingPatcher.VanillaPaksDir not set or not found: " + VanillaPaksDir);
            if (string.IsNullOrEmpty(AesKey))
                throw new InvalidOperationException("BuildingPatcher.AesKey not set");
        }

        void ValidateInputs(BuildingTemplate template, BuildingInputs inputs)
        {
            if (string.IsNullOrWhiteSpace(inputs.BuildingId))
                throw new ArgumentException("BuildingInputs.BuildingId is required");
            if (string.IsNullOrWhiteSpace(inputs.AssetPrefix))
                throw new ArgumentException("BuildingInputs.AssetPrefix is required (used to derive M_<prefix>_<slot> / MI_<prefix>_<slot>)");
            if (string.IsNullOrWhiteSpace(inputs.CookedFolderPath))
                throw new ArgumentException("BuildingInputs.CookedFolderPath is required");
            if (string.IsNullOrWhiteSpace(inputs.MeshStem))
                throw new ArgumentException("BuildingInputs.MeshStem is required (expected user-cooked SM_<prefix> filename in CookedFolderPath)");
            if (string.IsNullOrWhiteSpace(inputs.IconStem))
                throw new ArgumentException("BuildingInputs.IconStem is required");

            // Defaults must always be set - shared VT default-texture
            // names are shipped by the GUI regardless of any user-custom
            // overrides. The orchestrator will set these once per Build()
            // call before invoking Patch() per-Building.
            if (string.IsNullOrWhiteSpace(inputs.DefaultAlbedoStem) || string.IsNullOrWhiteSpace(inputs.DefaultAlbedoPath))
                throw new ArgumentException("BuildingInputs.DefaultAlbedoStem/Path required (shared default VT)");
            if (string.IsNullOrWhiteSpace(inputs.DefaultNormalStem) || string.IsNullOrWhiteSpace(inputs.DefaultNormalPath))
                throw new ArgumentException("BuildingInputs.DefaultNormalStem/Path required (shared default VT)");
            if (string.IsNullOrWhiteSpace(inputs.DefaultMtrmStem) || string.IsNullOrWhiteSpace(inputs.DefaultMtrmPath))
                throw new ArgumentException("BuildingInputs.DefaultMtrmStem/Path required (shared default VT)");

            // Per template: if a slot demands a user albedo, verify it's
            // there.
            foreach (var slot in template.Slots ?? new List<MaterialSlotTemplate>())
            {
                if (!slot.UserAlbedoRequired) continue;
                if (inputs.SlotInputs == null ||
                    !inputs.SlotInputs.TryGetValue(slot.SlotName, out var si) ||
                    si == null ||
                    string.IsNullOrWhiteSpace(si.CustomAlbedoStem))
                {
                    throw new ArgumentException(
                        "Template '" + template.Id + "' slot '" + slot.SlotName
                        + "' requires a user-supplied albedo texture (BuildingInputs.SlotInputs['" + slot.SlotName + "'].CustomAlbedoStem)");
                }
            }
        }

        // -----------------------------------------------------------------
        // Process runner. Mirrors the spike's RunProcess; routes
        // stdout/stderr through our Log so the GUI's SSE stream can
        // forward retoc lines verbatim.
        // -----------------------------------------------------------------
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

            var proc = Process.Start(psi);
            proc.OutputDataReceived += (s, e) => { if (e.Data != null) LogLine("  " + e.Data); };
            proc.ErrorDataReceived  += (s, e) => { if (e.Data != null) LogLine("  " + e.Data); };
            proc.BeginOutputReadLine();
            proc.BeginErrorReadLine();
            proc.WaitForExit();
            return proc.ExitCode;
        }

        void LogLine(string msg) { if (Log != null) Log(msg); }
    }

    // -------------------------------------------------------------------
    // Per-building input bundle. The orchestrator fills this in from the
    // Profile's BuildingDto plus the shared default-texture names (the
    // ones the GUI ships once per Build()).
    // -------------------------------------------------------------------
    public sealed class BuildingInputs
    {
        // Unique id for this Building. Drives the output DA stem
        // (DA_BI_<BuildingId>) and the localization key (Decoration_<BuildingId>_Name).
        public string BuildingId;

        // Asset-prefix the user picked. Drives the per-slot user material
        // stem (M_<AssetPrefix>_<SlotName>) and the MI clone stem
        // (MI_<AssetPrefix>_<SlotName>). Also used to filter the cooked
        // folder (only files with this prefix get staged).
        public string AssetPrefix;

        // Absolute path to the user's cooked-output folder for this
        // building's assets (typically the per-project
        // Saved/Cooked/Windows/<Project>/Content/Quartermaster/Items/).
        public string CookedFolderPath;

        // User-cooked mesh stem (e.g. "SM_QmPainting_01"). The file must
        // exist as <CookedFolderPath>/<MeshStem>.uasset.
        public string MeshStem;

        // User-cooked icon stem (e.g. "T_QmPainting_Icon"). Same path
        // requirement as the mesh.
        public string IconStem;

        // Human-facing strings - used by the orchestrator to synthesize
        // the localization CSV (NOT consumed by the patcher itself).
        public string DisplayName;
        public string Description;

        // User-cooked custom material stems that the staging step must
        // SKIP because they crash the shipping game. Typically computed
        // from the template's slots as M_<AssetPrefix>_<SlotName>.
        public List<string> SkipUserCookedMaterialStems;

        // Per-slot user inputs, keyed by MaterialSlotTemplate.SlotName.
        // Slots not present here use the shared defaults for all three
        // texture channels.
        public Dictionary<string, BuildingSlotInputs> SlotInputs;

        // Shared default-VT texture names the GUI ships once per build
        // (T_QmPainting_White / NormalFlat / MTRMDefault). The patcher
        // doesn't ship these files - it only emits NameMap refs that
        // point at them. The orchestrator is responsible for putting the
        // actual .uasset+.uexp+.ubulk into staging.
        public string DefaultAlbedoStem;
        public string DefaultAlbedoPath;
        public string DefaultNormalStem;
        public string DefaultNormalPath;
        public string DefaultMtrmStem;
        public string DefaultMtrmPath;
    }

    // Per-slot user input. Each field is optional; nulls/blanks fall back
    // to the shared defaults from BuildingInputs.
    public sealed class BuildingSlotInputs
    {
        public string CustomAlbedoStem;
        public string CustomAlbedoPath;
        public string CustomNormalStem;
        public string CustomNormalPath;
        public string CustomMtrmStem;
        public string CustomMtrmPath;
    }

    // Per-building result, returned to the orchestrator so it can fold
    // counts/warnings into the build report.
    public sealed class BuildingPatchResult
    {
        public string BuildingId;
        public string TemplateId;

        // Output asset identity (post-clone). The orchestrator uses these
        // when writing qm_items.json so the DLL knows what to inject.
        public string OutputDaStem;
        public string OutputDaPath;
        public string OutputNameKey;

        // Filenames staged into stagingItemsDir (for sanity-check + log).
        public List<string> StagedFiles;

        // Non-fatal warnings (missed replacement keys, unmatched vector
        // params, etc.). Surfaced into the SSE stream so the user knows
        // their template + cook may have drifted.
        public List<string> Warnings;
    }
}
