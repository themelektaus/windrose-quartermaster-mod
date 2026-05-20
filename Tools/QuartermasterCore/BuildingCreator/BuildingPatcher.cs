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
                BuildingId      = inputs.BuildingId,
                TemplateId      = template.Id,
                OutputDaStem    = "DA_BI_" + inputs.BuildingId,
                StagedFiles     = new List<string>(),
                Warnings        = new List<string>(),
                // Propagate user-supplied display text so the orchestrator
                // can feed it to the BuildingItems.csv synthesizer without
                // having to re-look-up the CustomBuilding from the profile.
                DisplayName     = inputs.DisplayName,
                Description     = inputs.Description,
            };

            // ---- Step 1: stage user-cooked assets ----------------------
            LogLine("=== [" + inputs.BuildingId + "] Step 1: stage user-cooked assets ===");
            StageCookedAssets(inputs, stagingItemsDir, result);

            // ---- Step 2: rewrite mesh material slots -------------------
            // Each mesh slot's user-MI ref (e.g. "MI_QmPainting_Canvas")
            // gets swapped for the slot's clone stem (e.g.
            // "MI_QmPainting_slot1"). The clones are produced in Step 4.
            LogLine("=== [" + inputs.BuildingId + "] Step 2: rewrite mesh material slots ===");
            PatchMeshMaterialSlots(inputs, stagingItemsDir, result);

            // ---- Step 3: extract Vanilla MIs ---------------------------
            // Each slot may pick a different Vanilla MI as parent. We
            // cache by VanillaMaterialParentPath so distinct picks each
            // run retoc once; slots that share a pick reuse the extract.
            LogLine("=== [" + inputs.BuildingId + "] Step 3: extract Vanilla MIs ===");
            var vanillaMiCache = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var slot in inputs.MeshSlots)
            {
                if (string.IsNullOrWhiteSpace(slot.VanillaMaterialParentPath)) continue;
                if (vanillaMiCache.ContainsKey(slot.VanillaMaterialParentPath)) continue;
                var vanillaStem = StemFromPath(slot.VanillaMaterialParentPath);
                var legacyMiPath = ExtractVanillaAsset(vanillaStem, perBuildingTemp, "mi-slot" + slot.Index);
                vanillaMiCache[slot.VanillaMaterialParentPath] = legacyMiPath;
            }

            // ---- Step 4: clone + patch per-slot MIs --------------------
            LogLine("=== [" + inputs.BuildingId + "] Step 4: clone + patch per-slot MIs ===");
            foreach (var slot in inputs.MeshSlots)
            {
                if (string.IsNullOrWhiteSpace(slot.VanillaMaterialParentPath))
                {
                    result.Warnings.Add("Slot " + slot.Index + " ('" + slot.SlotName + "') has no Vanilla parent picked - skipping clone");
                    continue;
                }
                ClonePatchSlot(inputs, slot, vanillaMiCache[slot.VanillaMaterialParentPath], stagingItemsDir, result);
            }

            // ---- Step 5: extract Vanilla DA ----------------------------
            LogLine("=== [" + inputs.BuildingId + "] Step 5: extract Vanilla DA ===");
            var legacyDaPath = ExtractVanillaAsset(template.VanillaDaStem, perBuildingTemp, "da");

            // ---- Step 6: clone + patch DA ------------------------------
            LogLine("=== [" + inputs.BuildingId + "] Step 6: clone + patch DA ===");
            PatchDataAsset(template, inputs, legacyDaPath, stagingItemsDir, result);

            // ---- Step 7: rewrite inline FText keys in the DA body ------
            // The vanilla DA carries its in-game display name / tooltip
            // as FText StringTableEntry records whose Key strings sit
            // inline in the export body (NOT in the NameMap). Step 6's
            // DataAssetPatcher only touches NameMap entries, so without
            // this step the patched DA still resolves to the vanilla
            // translation - the user's "My Painting" is never displayed.
            //
            // Patches each known FText key (template.VanillaNameKey,
            // template.VanillaDescriptionKey if set) to a same-byte-
            // length per-Building key. The orchestrator pairs those
            // new keys with the user-supplied display text by appending
            // matching rows to the BuildingItems.csv string-table.
            LogLine("=== [" + inputs.BuildingId + "] Step 7: rewrite inline FText keys ===");
            RewriteInlineFTextKeys(template, inputs, stagingItemsDir, result);

            LogLine("[OK] Building '" + inputs.BuildingId + "' patched: "
                + result.StagedFiles.Count + " files staged"
                + (result.Warnings.Count > 0 ? ", " + result.Warnings.Count + " warning(s)" : ""));

            return result;
        }

        // -----------------------------------------------------------------
        // Step 7: same-byte-length in-place rewrite of inline FText
        // StringTableEntry keys in the cloned DA's export body. Mirrors
        // the ItemCreator's CSV synthesis pattern, except the per-item key
        // for buildings lives in raw bytes (not in a JSON field) so we
        // need FTextKeyRewriter instead of a JsonObject edit.
        // -----------------------------------------------------------------
        void RewriteInlineFTextKeys(BuildingTemplate template, BuildingInputs inputs,
                                    string stagingItemsDir, BuildingPatchResult result)
        {
            if (string.IsNullOrWhiteSpace(template.VanillaNameKey)
                && string.IsNullOrWhiteSpace(template.VanillaDescriptionKey))
            {
                LogLine("  (template has no FText keys declared - nothing to rewrite)");
                return;
            }

            var outDaStem = "DA_BI_" + inputs.BuildingId;
            var outDaFile = Path.Combine(stagingItemsDir, outDaStem + ".uasset");
            if (!File.Exists(outDaFile))
            {
                result.Warnings.Add(
                    "FText rewrite: cloned DA not found at " + outDaFile
                    + " - Step 6 should have produced it");
                return;
            }

            var replacements = new Dictionary<string, string>(StringComparer.Ordinal);
            string newNameKey = null;
            string newDescKey = null;
            if (!string.IsNullOrWhiteSpace(template.VanillaNameKey))
            {
                newNameKey = BuildingFTextKey.Build(template.VanillaNameKey, inputs.BuildingId, "_Name");
                replacements[template.VanillaNameKey] = newNameKey;
            }
            if (!string.IsNullOrWhiteSpace(template.VanillaDescriptionKey))
            {
                newDescKey = BuildingFTextKey.Build(template.VanillaDescriptionKey, inputs.BuildingId, "_Description");
                replacements[template.VanillaDescriptionKey] = newDescKey;
            }

            var rewriter = new FTextKeyRewriter { Log = LogLine };
            var pr = rewriter.Patch(outDaFile, UsmapPath, replacements);

            // Surface dead-letter keys (vanilla bytes not present in body)
            // as warnings. Worth knowing about: it means the template's
            // declared key doesn't actually appear in the extracted DA,
            // so the in-game text will keep using whatever the cloned
            // DA had (and the BuildingItems.csv row we synthesise will
            // be orphaned).
            if (pr.Missed != null && pr.Missed.Count > 0)
            {
                foreach (var m in pr.Missed)
                {
                    result.Warnings.Add(
                        "FText key '" + m + "' not found in DA body (template "
                        + template.Id + "). In-game text may keep the vanilla "
                        + "value; check that the template declaration matches "
                        + "what the vanilla DA actually carries.");
                }
            }

            // Stash the keys + display text on the result so the orchestrator's
            // CSV-synthesis step pairs them automatically.
            if (newNameKey != null && pr.PerKeyHits != null
                && pr.PerKeyHits.TryGetValue(template.VanillaNameKey, out var nameHits) && nameHits > 0)
            {
                result.OutputNameKey = newNameKey;
            }
            if (newDescKey != null && pr.PerKeyHits != null
                && pr.PerKeyHits.TryGetValue(template.VanillaDescriptionKey, out var descHits) && descHits > 0)
            {
                result.OutputDescriptionKey = newDescKey;
            }
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

            // Greedy-match by asset-prefix (Punkt 7 of the planning doc).
            // Skip-set: every user-cooked MI the mesh references (each
            // slot's UserMaterialStem). Those crash shipping per the
            // spike bisect, the patcher replaces them with Vanilla-MI
            // clones in Step 4.
            var skipStems = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (inputs.MeshSlots != null)
            {
                foreach (var s in inputs.MeshSlots)
                {
                    if (!string.IsNullOrWhiteSpace(s.UserMaterialStem))
                        skipStems.Add(s.UserMaterialStem);
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
        // per-slot user-MI refs for the cloned MI stems that Step 4 will
        // generate. Slot-by-slot lookup comes from inputs.MeshSlots
        // (which originates from CookedFolderInspector reading the mesh's
        // StaticMaterials array).
        // -----------------------------------------------------------------
        void PatchMeshMaterialSlots(BuildingInputs inputs,
                                    string stagingItemsDir, BuildingPatchResult result)
        {
            var meshFileName = inputs.MeshStem + ".uasset";
            var meshInStaging = Path.Combine(stagingItemsDir, meshFileName);
            if (!File.Exists(meshInStaging))
                throw new FileNotFoundException(
                    "Mesh not found in staging: " + meshInStaging
                    + " - expected the user-cooked SM_<prefix>.uasset at MeshStem='" + inputs.MeshStem + "'.");

            // Build replacements per slot: every user-cooked slot's
            // UserMaterialStem -> CloneStem. Slots without a UserMaterialStem
            // (rare: mesh has empty slot) are skipped silently here; they
            // still get a clone but the mesh won't reference it.
            var meshReplacements = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var slot in inputs.MeshSlots)
            {
                if (string.IsNullOrWhiteSpace(slot.UserMaterialStem)) continue;
                var cloneStem = BuildSlotCloneStem(inputs, slot);
                var clonePath = BuildSlotClonePath(inputs, slot);
                meshReplacements[slot.UserMaterialStem] = cloneStem;
                if (!string.IsNullOrWhiteSpace(slot.UserMaterialPath))
                    meshReplacements[slot.UserMaterialPath] = clonePath;
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
        // Step 4 (Etappe G mesh-driven): clone the user-picked Vanilla MI
        // under the slot's clone path. Rewrite NameMap so:
        //   - self-name+path point at the clone
        //   - each texture param the user overrode points at the user's
        //     texture stem (under /Game/Quartermaster/Items/)
        // Then patch Scalar/Vector parameter values via UAssetAPI for
        // any params the user overrode. Texture stems get rewritten via
        // the NameMap path (DataAssetPatcher); scalars/vectors via direct
        // UAssetAPI struct edits.
        // -----------------------------------------------------------------
        void ClonePatchSlot(BuildingInputs inputs, MeshSlotInput slot,
                            string legacyMiPath,
                            string stagingItemsDir, BuildingPatchResult result)
        {
            var cloneStem = BuildSlotCloneStem(inputs, slot);
            var clonePath = BuildSlotClonePath(inputs, slot);
            var cloneFile = Path.Combine(stagingItemsDir, cloneStem + ".uasset");
            var vanillaStem = StemFromPath(slot.VanillaMaterialParentPath);

            // We first inspect the Vanilla MI to learn its texture-param
            // mappings (param-name -> existing texture-stem). The
            // DataAssetPatcher rewrites textures via NameMap entries, so
            // we need to know the OLD stem (from the Vanilla MI) to swap
            // for the NEW stem (user's override). Without this peek the
            // patcher wouldn't know which strings to replace.
            var inspector = new MaterialInstanceInspector { UsmapPath = UsmapPath };
            var miData = inspector.Inspect(legacyMiPath)
                ?? throw new InvalidOperationException(
                    "Vanilla MI '" + vanillaStem + "' didn't parse as MaterialInstanceConstant");

            // Self-name + path: needed so the clone packs under the new
            // /Game/Quartermaster/Items/ location.
            var matReplacements = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [vanillaStem] = cloneStem,
                [slot.VanillaMaterialParentPath] = clonePath,
            };

            // Per-texture-param user overrides: look up the Vanilla
            // texture-ref this param currently carries and rewrite to
            // the user's texture stem. The user's texture must exist
            // under /Game/Quartermaster/Items/<stem> in staging (the
            // cooked-folder step already copied it there).
            if (slot.TextureParams != null)
            {
                foreach (var kv in slot.TextureParams)
                {
                    if (string.IsNullOrWhiteSpace(kv.Value)) continue;
                    var existing = FindTextureParam(miData, kv.Key);
                    if (existing == null)
                    {
                        result.Warnings.Add(
                            "Slot " + slot.Index + " ('" + slot.SlotName + "'): texture param '"
                            + kv.Key + "' not found in Vanilla MI '" + vanillaStem + "' - skipping override");
                        continue;
                    }
                    var newStem = kv.Value;
                    var newPath = "/Game/Quartermaster/Items/" + newStem;
                    if (!string.IsNullOrEmpty(existing.TextureStem))
                        matReplacements[existing.TextureStem] = newStem;
                    if (!string.IsNullOrEmpty(existing.TexturePath))
                        matReplacements[existing.TexturePath] = newPath;
                }
            }

            LogLine("  [slot " + slot.Index + " '" + slot.SlotName + "'] cloning " + vanillaStem + " -> " + cloneStem);

            var patcher = new DataAssetPatcher { Log = LogLine };
            var pr = patcher.Patch(
                inputAssetPath:  legacyMiPath,
                outputAssetPath: cloneFile,
                usmapPath:       UsmapPath,
                replacements:    matReplacements,
                newFolderName:   clonePath,
                requireAllHits:  false);

            LogLine("  [OK] Slot " + slot.Index + " NameMap patched: " + pr.NameMapEntriesRenamed
                + " renames, " + pr.ExportsRetargeted + " export retargets");

            result.StagedFiles.Add(cloneStem + ".uasset");
            result.StagedFiles.Add(cloneStem + ".uexp");

            if (pr.MissedReplacements != null && pr.MissedReplacements.Count > 0)
            {
                result.Warnings.Add(
                    "MI clone " + cloneStem + ": " + pr.MissedReplacements.Count
                    + " NameMap replacement(s) didn't match - "
                    + string.Join(", ", pr.MissedReplacements));
            }

            // Scalar + Vector overrides via UAssetAPI struct edits.
            int scalarOverrides = slot.ScalarParams?.Count ?? 0;
            int vectorOverrides = slot.VectorParams?.Count ?? 0;
            if (scalarOverrides == 0 && vectorOverrides == 0) return;

            var mapping = new Usmap(UsmapPath);
            var miAsset = new UAsset(cloneFile, EngineVersion.VER_UE5_6, mapping);
            int scalarHits = 0, vectorHits = 0;

            if (slot.ScalarParams != null)
            {
                foreach (var kv in slot.ScalarParams)
                {
                    int h = PatchMiScalarParameter(miAsset, kv.Key, kv.Value);
                    if (h == 0)
                        result.Warnings.Add(
                            "MI clone " + cloneStem + ": scalar param '" + kv.Key
                            + "' not found in MI - override skipped");
                    scalarHits += h;
                }
            }
            if (slot.VectorParams != null)
            {
                foreach (var kv in slot.VectorParams)
                {
                    var rgba = kv.Value;
                    if (rgba == null || rgba.Length < 4) continue;
                    int h = PatchMiVectorParameter(miAsset, kv.Key, rgba[0], rgba[1], rgba[2], rgba[3]);
                    if (h == 0)
                        result.Warnings.Add(
                            "MI clone " + cloneStem + ": vector param '" + kv.Key
                            + "' not found in MI - override skipped");
                    vectorHits += h;
                }
            }

            if (scalarHits > 0 || vectorHits > 0)
            {
                miAsset.Write(cloneFile);
                LogLine("  [OK] Slot " + slot.Index + " param overrides applied: "
                    + scalarHits + " scalar, " + vectorHits + " vector");
            }
        }

        // Find a texture-param entry by name in the inspected MI.
        // Returns null if not present (param was inherited from parent
        // master material rather than overridden in the MI itself).
        static MITextureParam FindTextureParam(MaterialInstanceData mi, string name)
        {
            if (mi?.Textures == null) return null;
            foreach (var t in mi.Textures)
                if (string.Equals(t.Name, name, System.StringComparison.Ordinal))
                    return t;
            return null;
        }

        // Strip "/Game/.../" -> just the trailing stem.
        static string StemFromPath(string path)
        {
            if (string.IsNullOrEmpty(path)) return path;
            int last = path.LastIndexOfAny(new[] { '/', '\\' });
            return last < 0 ? path : path.Substring(last + 1);
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

            // Etappe H2: per-building recipe clone target. The vanilla
            // building DA's NameMap references the recipe DA both by full
            // package path AND by bare stem - both entries must move to
            // point at our cloned recipe under the same vanilla folder
            // structure (UE resolves them relative to that path).
            var outRecipeStem = "DA_RD_Qm" + inputs.BuildingId;
            var outRecipePath = "/R5BusinessRules/Recipes/Building/Items/Decorations/" + outRecipeStem;

            // NameMap-rewrite covers asset path / stem refs only - the
            // vanilla FText *keys* (template.VanillaNameKey /
            // VanillaDescriptionKey) live inline in the DA's RawExport
            // body and are NOT in the NameMap, so they're handled by
            // Step 7's FTextKeyRewriter instead.
            var daReplacements = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [template.VanillaMeshStem] = outMeshStem,
                [template.VanillaMeshPath] = outMeshPath,

                [template.VanillaIconStem] = outIconStem,
                [template.VanillaIconPath] = outIconPath,

                [template.VanillaDaStem] = outDaStem,
                [template.VanillaDaPath] = outDaPath,
            };

            // Add recipe rewrites only when the template carries the
            // linkage. Defensive: keeps the patch behaviour identical
            // for any future template that has no recipe (free-build
            // brushes, decoratives without RecipeCost, ...).
            if (!string.IsNullOrEmpty(template.VanillaRecipeStem)
                && !string.IsNullOrEmpty(template.VanillaRecipePackagePath))
            {
                daReplacements[template.VanillaRecipeStem]        = outRecipeStem;
                daReplacements[template.VanillaRecipePackagePath] = outRecipePath;
            }

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

            // Surface the recipe-clone identity so the orchestrator's
            // RecipePatcher step (Etappe H2) knows what stem to emit
            // and matches the NameMap rewrites we just committed.
            if (!string.IsNullOrEmpty(template.VanillaRecipeStem))
            {
                result.OutputRecipeStem = outRecipeStem;
            }

            if (pr.MissedReplacements != null && pr.MissedReplacements.Count > 0)
            {
                result.Warnings.Add(
                    "DA " + outDaStem + ": " + pr.MissedReplacements.Count
                    + " replacement key(s) didn't match - "
                    + string.Join(", ", pr.MissedReplacements));
            }
        }

        // -----------------------------------------------------------------
        // Slot clone stem/path naming helper (Etappe G mesh-driven).
        //
        // Old (template-driven): "MI_<AssetPrefix>_<SlotName>" with the
        // template's hardcoded SlotName ("Canvas"/"Frame"). The mesh
        // had to carry "M_<AssetPrefix>_<SlotName>" so the rewrite
        // could find it.
        //
        // New (mesh-driven): "MI_<AssetPrefix>_slot<Index>". Slot
        // indices are stable across cook iterations even if the user
        // renames slots in the editor, so the clone stem stays
        // deterministic. The mesh's existing user-MI ref is looked up
        // by stem from CookedFolderInspector - no naming convention
        // assumed.
        // -----------------------------------------------------------------
        static string BuildSlotCloneStem(BuildingInputs inputs, MeshSlotInput slot)
            => "MI_" + inputs.AssetPrefix + "_slot" + slot.Index;

        static string BuildSlotClonePath(BuildingInputs inputs, MeshSlotInput slot)
            => "/Game/Quartermaster/Items/" + BuildSlotCloneStem(inputs, slot);

        // -----------------------------------------------------------------
        // UAssetAPI helper: locate a ScalarParameterValues entry by
        // ParameterInfo.Name and overwrite its embedded float value.
        // Mirrors PatchMiVectorParameter (same walk, simpler value).
        // -----------------------------------------------------------------
        static int PatchMiScalarParameter(UAsset asset, string paramName, float value)
        {
            int hits = 0;
            foreach (var ex in asset.Exports)
            {
                if (!(ex is NormalExport ne)) continue;
                foreach (var prop in ne.Data)
                {
                    if (!(prop is ArrayPropertyData arr)) continue;
                    if (arr.Name?.Value?.Value != "ScalarParameterValues") continue;
                    if (arr.Value == null) continue;

                    foreach (var item in arr.Value)
                    {
                        if (!(item is StructPropertyData entry)) continue;
                        if (entry.Value == null) continue;

                        string foundName = null;
                        FloatPropertyData paramValueFloat = null;
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
                            else if (sub is FloatPropertyData fp && sub.Name?.Value?.Value == "ParameterValue")
                            {
                                paramValueFloat = fp;
                            }
                        }
                        if (foundName != paramName || paramValueFloat == null) continue;

                        paramValueFloat.Value = value;
                        hits++;
                    }
                }
            }
            return hits;
        }

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
                throw new ArgumentException("BuildingInputs.AssetPrefix is required");
            if (string.IsNullOrWhiteSpace(inputs.CookedFolderPath))
                throw new ArgumentException("BuildingInputs.CookedFolderPath is required");
            if (string.IsNullOrWhiteSpace(inputs.MeshStem))
                throw new ArgumentException("BuildingInputs.MeshStem is required (expected user-cooked SM_<prefix> filename in CookedFolderPath)");
            if (string.IsNullOrWhiteSpace(inputs.IconStem))
                throw new ArgumentException("BuildingInputs.IconStem is required");
            if (inputs.MeshSlots == null || inputs.MeshSlots.Count == 0)
                throw new ArgumentException("BuildingInputs.MeshSlots is required (mesh has no material slots, or orchestrator forgot to feed inspector output)");

            // Each slot needs a Vanilla parent picked. The GUI gates
            // Save on this too but we re-validate so a hand-edited
            // profile JSON can't crash the patcher mid-flight.
            for (int i = 0; i < inputs.MeshSlots.Count; i++)
            {
                var s = inputs.MeshSlots[i];
                if (string.IsNullOrWhiteSpace(s.VanillaMaterialParentPath))
                {
                    throw new ArgumentException(
                        "Building '" + inputs.BuildingId + "' slot " + s.Index + " ('" + s.SlotName
                        + "') has no VanillaMaterialParentPath set - pick a Vanilla MI parent in the GUI");
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
    // Per-building input bundle (Etappe G mesh-driven).
    //
    // The orchestrator fills this from the Profile's CustomBuilding plus
    // the mesh inspection (which yields the slot list with user-MI refs).
    // The patcher no longer needs default-VT texture names - any param
    // the user doesn't override stays at its Vanilla value.
    // -------------------------------------------------------------------
    public sealed class BuildingInputs
    {
        // Unique id for this Building. Drives the output DA stem
        // (DA_BI_<BuildingId>) and the localization key (Decoration_<BuildingId>_Name).
        public string BuildingId;

        // Asset-prefix the user picked. Used to filter the cooked folder
        // (only files matching as a name component get staged) and to
        // drive the clone stem (MI_<AssetPrefix>_slot<Index>).
        public string AssetPrefix;

        // Absolute path to the user's cooked-output folder.
        public string CookedFolderPath;

        // User-cooked mesh stem (e.g. "SM_QmPainting_01").
        public string MeshStem;

        // User-cooked icon stem (e.g. "T_QmPainting_Icon").
        public string IconStem;

        // Human-facing strings - used by the orchestrator to synthesize
        // the localization CSV (NOT consumed by the patcher itself).
        public string DisplayName;
        public string Description;

        // Mesh-derived slot list with per-slot user-config. Length
        // matches the user-cooked mesh's StaticMaterials array. The
        // orchestrator builds this by feeding the cooked folder
        // through CookedFolderInspector + merging the Profile's
        // CustomBuildingSlot overrides on top.
        public List<MeshSlotInput> MeshSlots;

        // Etappe H2: user-edited build cost. null = use the template's
        // vanilla recipe defaults (pass-through). Empty list = explicit
        // "free build" override (engine accepts a recipe with empty
        // RecipeCost). Items beyond the catalog are non-fatal warnings.
        public List<(string ItemPath, int Count)> RecipeCost;
    }

    // Per-slot user input (Etappe G mesh-driven).
    //
    // Each entry maps one mesh material slot to:
    //   - the Vanilla MI the user picked as parent (required)
    //   - the user-MI stem the mesh originally referenced (so the
    //     mesh NameMap rewrite knows what string to swap)
    //   - per-param overrides for the picked Vanilla MI
    public sealed class MeshSlotInput
    {
        public int    Index;       // mesh slot index (0..N-1)
        public string SlotName;    // mesh slot name (e.g. "lambert1")

        // User-MI stem the cooked mesh references in this slot
        // (e.g. "MI_QmPainting_Canvas"). May be null if the cooked mesh
        // has no MI bound to this slot - in that case the patcher logs
        // a warning, the slot still gets a clone but the mesh's
        // material ref won't be rewritten.
        public string UserMaterialStem;
        public string UserMaterialPath;

        // User-picked Vanilla MI to clone for this slot
        // (e.g. "/Game/Environment/.../MI_Paintings_01"). REQUIRED.
        public string VanillaMaterialParentPath;

        // Param overrides. Keys are MI param names; missing entries
        // leave the cloned MI's value unchanged from Vanilla.
        public Dictionary<string, float>    ScalarParams;
        public Dictionary<string, float[]>  VectorParams;
        public Dictionary<string, string>   TextureParams;
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
        // FText StringTableEntry keys the binary rewriter actually
        // committed to the cloned DA's RawExport body. Null when the
        // vanilla key wasn't found in the body (template + extracted
        // DA mismatch) - the orchestrator skips the CSV-row synthesis
        // for the unset slot so the engine keeps using whatever the
        // cloned DA already carried for that field.
        public string OutputNameKey;
        public string OutputDescriptionKey;

        // User-supplied display strings echoed back from BuildingInputs
        // so the BuildingItems.csv synthesizer can pair them with the
        // OutputNameKey / OutputDescriptionKey without a second profile
        // lookup. Stored verbatim - empty / null is treated as
        // "user didn't fill the field, emit an empty CSV row" (mirrors
        // ItemCreator behaviour for the InventoryItems table).
        public string DisplayName;
        public string Description;

        // Filenames staged into stagingItemsDir (for sanity-check + log).
        public List<string> StagedFiles;

        // Non-fatal warnings (missed replacement keys, unmatched vector
        // params, etc.). Surfaced into the SSE stream so the user knows
        // their template + cook may have drifted.
        public List<string> Warnings;

        // Etappe H2: recipe artefacts. Empty / null when the building's
        // template doesn't carry a vanilla recipe linkage (defensive -
        // every shipped template has one today).
        public string OutputRecipeStem;        // "DA_RD_Qm<BuildingId>"
        public string OutputRecipeJsonPath;    // absolute on-disk path written
        public string NewRecipeTag;            // "RecipeData.QM.<BuildingId>"
        public int    RecipeCostRows;          // rows actually written (vanilla or user)
        public bool   RecipeCostOverridden;    // user-list applied vs vanilla pass-through
    }
}
