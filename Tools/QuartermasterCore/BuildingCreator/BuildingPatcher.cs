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
    public sealed class BuildingPatcher
    {
        public Action<string> Log;

        public string RetocExe;
        public string UsmapPath;
        public string VanillaPaksDir;
        public string AesKey;

        public string TempDir;

        public BuildingPatchResult Patch(
            BuildingTemplate template,
            BuildingInputs inputs,
            string stagingItemsDir,
            string profileId = null)
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
                DisplayName     = inputs.DisplayName,
                Description     = inputs.Description,
            };

            LogLine("=== [" + inputs.BuildingId + "] Step 1: stage user-cooked assets ===");
            StageCookedAssets(inputs, stagingItemsDir, result);

            LogLine("=== [" + inputs.BuildingId + "] Step 2: rewrite mesh material slots ===");
            PatchMeshMaterialSlots(inputs, stagingItemsDir, result);

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

            LogLine("=== [" + inputs.BuildingId + "] Step 5: extract Vanilla DA ===");
            var legacyDaPath = ExtractVanillaAsset(template.VanillaDaStem, perBuildingTemp, "da");

            LogLine("=== [" + inputs.BuildingId + "] Step 6: clone + patch DA ===");
            PatchDataAsset(template, inputs, legacyDaPath, stagingItemsDir, result);

            LogLine("=== [" + inputs.BuildingId + "] Step 7: rewrite inline FText keys ===");
            RewriteInlineFTextKeys(template, inputs, stagingItemsDir, result, profileId);

            LogLine("[OK] Building '" + inputs.BuildingId + "' patched: "
                + result.StagedFiles.Count + " files staged"
                + (result.Warnings.Count > 0 ? ", " + result.Warnings.Count + " warning(s)" : ""));

            return result;
        }

        void RewriteInlineFTextKeys(BuildingTemplate template, BuildingInputs inputs,
                                    string stagingItemsDir, BuildingPatchResult result,
                                    string profileId)
        {
            _ = profileId;

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

            // Empty values are intentional: an empty SourceString shows a blank tooltip line, not <MISSING_STRING>.
            var displayMap = new Dictionary<string, string>(StringComparer.Ordinal);
            if (!string.IsNullOrWhiteSpace(template.VanillaNameKey))
            {
                displayMap[template.VanillaNameKey] = inputs.DisplayName ?? string.Empty;
            }
            if (!string.IsNullOrWhiteSpace(template.VanillaDescriptionKey))
            {
                displayMap[template.VanillaDescriptionKey] = inputs.Description ?? string.Empty;
            }

            if (displayMap.Count == 0)
            {
                LogLine("  (no FText keys to rewrite)");
                return;
            }

            var rewriter = new FTextKeyRewriter { Log = LogLine };
            var pr = rewriter.Patch(outDaFile, UsmapPath, displayMap);

            if (pr.Missed != null && pr.Missed.Count > 0)
            {
                foreach (var m in pr.Missed)
                {
                    result.Warnings.Add(
                        "FText key '" + m + "' not found in DA body (template "
                        + template.Id + "). In-game text falls back to whatever "
                        + "the cloned DA already carried; check that the template "
                        + "declaration matches what the vanilla DA actually has.");
                }
            }
        }

        void StageCookedAssets(BuildingInputs inputs, string stagingItemsDir, BuildingPatchResult result)
        {
            if (!Directory.Exists(inputs.CookedFolderPath))
                throw new DirectoryNotFoundException(
                    "Cooked folder not found: " + inputs.CookedFolderPath
                    + " - cook the user assets in the UE editor first.");

            // Skip user-cooked MIs the mesh references: they crash shipping and get replaced by Vanilla-MI clones in Step 4.
            var skipStems = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (inputs.MeshSlots != null)
            {
                foreach (var s in inputs.MeshSlots)
                {
                    if (!string.IsNullOrWhiteSpace(s.UserMaterialStem))
                        skipStems.Add(s.UserMaterialStem);
                }
            }

            // Allowlist user-referenced stems so they stage even if they don't match the AssetPrefix filter (e.g. shared default textures).
            var allowStems = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (!string.IsNullOrWhiteSpace(inputs.MeshStem))
                allowStems.Add(inputs.MeshStem);
            if (!string.IsNullOrWhiteSpace(inputs.IconStem))
                allowStems.Add(inputs.IconStem);
            if (inputs.MeshSlots != null)
            {
                foreach (var s in inputs.MeshSlots)
                {
                    if (s.TextureParams == null) continue;
                    foreach (var kv in s.TextureParams)
                    {
                        if (!string.IsNullOrWhiteSpace(kv.Value))
                            allowStems.Add(kv.Value);
                    }
                }
            }

            var stagedUserAssets = new List<string>();

            int copied = 0;
            int preexisting = 0;
            int skipped = 0;
            int rejected = 0;
            var rejectedSample = new List<string>();
            foreach (var f in Directory.GetFiles(inputs.CookedFolderPath))
            {
                var name = Path.GetFileName(f);
                var stem = Path.GetFileNameWithoutExtension(name);

                // Match the prefix as a NAME COMPONENT inside the stem (boundary '_' or start/end), not via StartsWith. Empty prefix => take everything.
                bool prefixOk = string.IsNullOrEmpty(inputs.AssetPrefix)
                    || StemContainsPrefixAsComponent(stem, inputs.AssetPrefix);
                bool allowed = prefixOk || allowStems.Contains(stem);
                if (!allowed)
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

                // First building to reach a file wins; re-copying a mesh would silently clobber Step 2's NameMap rewrite and render it unmaterialed.
                if (File.Exists(dst))
                {
                    preexisting++;
                    result.StagedFiles.Add(name);
                    continue;
                }

                File.Copy(f, dst, overwrite: false);
                LogLine("  [copy] " + name + (prefixOk ? "" : "  (allowlisted: user-referenced)"));
                result.StagedFiles.Add(name);
                copied++;

                if (string.Equals(Path.GetExtension(name), ".uasset", StringComparison.OrdinalIgnoreCase))
                    stagedUserAssets.Add(dst);
            }

            // Both 0 = nothing matched and no earlier building staged one: the real error state (preexisting > 0 is fine).
            if (copied == 0 && preexisting == 0)
            {
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
                + (preexisting > 0 ? " (" + preexisting + " already staged by an earlier building - shared cooked folder)" : "")
                + (skipped > 0 ? " (" + skipped + " user-cooked material(s) skipped)" : "")
                + (rejected > 0 ? " (" + rejected + " file(s) didn't match prefix"
                    + (rejectedSample.Count > 0 ? ", e.g. " + string.Join(", ", rejectedSample) : "") + ")" : ""));

            // The iostore loader silently fails to resolve a package when its internal FolderName disagrees with the staged top-level path, so rewrite self-refs before retoc-to-zen.
            int normalized = 0;
            foreach (var uassetPath in stagedUserAssets)
            {
                if (NormalizeStagedUserAssetSelfPath(uassetPath, result)) normalized++;
            }
            if (normalized > 0)
                LogLine("[OK] " + normalized + " staged file(s) self-path normalized to " + WindrosePaths.ModItemsPackagePath + "<stem>");
        }

        bool NormalizeStagedUserAssetSelfPath(string stagedUassetPath, BuildingPatchResult result)
        {
            bool changed = NormalizeAssetSelfPath(stagedUassetPath, UsmapPath, LogLine, out var error);
            if (!changed && error != null)
            {
                // Not every staged file is a parseable Zen package (retoc emits sidecars); surface as a warning instead of failing the build.
                result.Warnings.Add(
                    "FolderName normalize failed for " + Path.GetFileName(stagedUassetPath)
                    + ": " + error
                    + " (asset will be staged with original self-path; in-game load may fail).");
            }
            return changed;
        }

        // Static counterpart for DefaultTextureProvider (no live BuildingPatcher instance). On false, `error` is null for no-op-already-correct, else a type+message string.
        public static bool NormalizeAssetSelfPath(string stagedUassetPath, string usmapPath, Action<string> log, out string error)
        {
            error = null;
            var stem = Path.GetFileNameWithoutExtension(stagedUassetPath);
            var targetFolderName = WindrosePaths.ModItemsPackagePath + stem;

            try
            {
                var mapping = new Usmap(usmapPath);
                var asset = new UAsset(stagedUassetPath, EngineVersion.VER_UE5_6, mapping);

                var currentFolderName = asset.FolderName?.Value;
                if (string.IsNullOrEmpty(currentFolderName)
                    || string.Equals(currentFolderName, targetFolderName, StringComparison.Ordinal))
                {
                    return false;
                }

                var names = asset.GetNameMapIndexList();
                int renamedSelfEntries = 0;
                for (int i = 0; i < names.Count; i++)
                {
                    var entry = names[i];
                    if (entry == null || entry.Value == null) continue;
                    if (string.Equals(entry.Value, currentFolderName, StringComparison.Ordinal))
                    {
                        asset.SetNameReference(i, new FString(targetFolderName, entry.Encoding));
                        renamedSelfEntries++;
                    }
                }

                asset.FolderName = FString.FromString(targetFolderName);
                asset.Write(stagedUassetPath);

                if (log != null) log("  [normalize] " + Path.GetFileName(stagedUassetPath)
                    + ": FolderName " + currentFolderName + " -> " + targetFolderName
                    + " (" + renamedSelfEntries + " NameMap self-entry rewrite(s))");
                return true;
            }
            catch (Exception ex)
            {
                error = ex.GetType().Name + " - " + ex.Message;
                if (log != null) log("  [normalize] WARN " + Path.GetFileName(stagedUassetPath) + ": " + error);
                return false;
            }
        }

        // True if `prefix` appears as a name component in `stem` (bounded by '_' or start/end). Case-insensitive.
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

        void PatchMeshMaterialSlots(BuildingInputs inputs,
                                    string stagingItemsDir, BuildingPatchResult result)
        {
            var meshFileName = inputs.MeshStem + ".uasset";
            var meshInStaging = Path.Combine(stagingItemsDir, meshFileName);
            if (!File.Exists(meshInStaging))
                throw new FileNotFoundException(
                    "Mesh not found in staging: " + meshInStaging
                    + " - expected the user-cooked SM_<prefix>.uasset at MeshStem='" + inputs.MeshStem + "'.");

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

            // Legal: mesh carries no user-cooked MI refs. Skip rather than throw (DataAssetPatcher rejects an empty replacements dict).
            if (meshReplacements.Count == 0)
            {
                LogLine("  (mesh has no user-cooked MI slots to rewrite - skipping NameMap pass)");
                return;
            }

            var meshPatcher = new DataAssetPatcher { Log = LogLine };
            var meshPr = meshPatcher.Patch(
                inputAssetPath:  meshInStaging,
                outputAssetPath: meshInStaging,
                usmapPath:       UsmapPath,
                replacements:    meshReplacements,
                newFolderName:   null,
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

        // retoc to-legacy --filter <stem>. Returns the absolute path to the extracted .uasset.
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

        void ClonePatchSlot(BuildingInputs inputs, MeshSlotInput slot,
                            string legacyMiPath,
                            string stagingItemsDir, BuildingPatchResult result)
        {
            var cloneStem = BuildSlotCloneStem(inputs, slot);
            var clonePath = BuildSlotClonePath(inputs, slot);
            var cloneFile = Path.Combine(stagingItemsDir, cloneStem + ".uasset");
            var vanillaStem = StemFromPath(slot.VanillaMaterialParentPath);

            // Inspect the Vanilla MI to learn the OLD texture stems so the NameMap rewrite knows which strings to swap for the user's overrides.
            var inspector = new MaterialInstanceInspector { UsmapPath = UsmapPath };
            var miData = inspector.Inspect(legacyMiPath)
                ?? throw new InvalidOperationException(
                    "Vanilla MI '" + vanillaStem + "' didn't parse as MaterialInstanceConstant");

            var matReplacements = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [vanillaStem] = cloneStem,
                [slot.VanillaMaterialParentPath] = clonePath,
            };

            int textureSkippedVanilla = 0;
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
                    if (string.Equals(existing.TextureStem, kv.Value, StringComparison.Ordinal))
                    {
                        // No-op: user value equals Vanilla. Writing it would redirect to a non-existent vanilla stem under the mod namespace.
                        textureSkippedVanilla++;
                        continue;
                    }
                    var newStem = kv.Value;
                    var newPath = WindrosePaths.ModItemsPackagePath + newStem;
                    if (!string.IsNullOrEmpty(existing.TextureStem))
                        matReplacements[existing.TextureStem] = newStem;
                    if (!string.IsNullOrEmpty(existing.TexturePath))
                        matReplacements[existing.TexturePath] = newPath;
                }
            }
            if (textureSkippedVanilla > 0)
                LogLine("  [slot " + slot.Index + "] skipped " + textureSkippedVanilla
                    + " texture override(s) matching vanilla defaults");

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

            int scalarOverrides = slot.ScalarParams?.Count ?? 0;
            int vectorOverrides = slot.VectorParams?.Count ?? 0;
            if (scalarOverrides == 0 && vectorOverrides == 0) return;

            const float EPS = 1e-4f;
            int scalarSkippedVanilla = 0, vectorSkippedVanilla = 0;

            var mapping = new Usmap(UsmapPath);
            var miAsset = new UAsset(cloneFile, EngineVersion.VER_UE5_6, mapping);
            int scalarHits = 0, vectorHits = 0;

            if (slot.ScalarParams != null)
            {
                foreach (var kv in slot.ScalarParams)
                {
                    var def = FindScalarParam(miData, kv.Key);
                    if (def != null && Math.Abs(def.Value - kv.Value) < EPS)
                    {
                        scalarSkippedVanilla++;
                        continue;
                    }
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
                    var def = FindVectorParam(miData, kv.Key);
                    if (def != null
                        && Math.Abs(def.R - rgba[0]) < EPS
                        && Math.Abs(def.G - rgba[1]) < EPS
                        && Math.Abs(def.B - rgba[2]) < EPS
                        && Math.Abs(def.A - rgba[3]) < EPS)
                    {
                        vectorSkippedVanilla++;
                        continue;
                    }
                    int h = PatchMiVectorParameter(miAsset, kv.Key, rgba[0], rgba[1], rgba[2], rgba[3]);
                    if (h == 0)
                        result.Warnings.Add(
                            "MI clone " + cloneStem + ": vector param '" + kv.Key
                            + "' not found in MI - override skipped");
                    vectorHits += h;
                }
            }

            if (scalarSkippedVanilla > 0 || vectorSkippedVanilla > 0)
                LogLine("  [slot " + slot.Index + "] skipped "
                    + scalarSkippedVanilla + " scalar + "
                    + vectorSkippedVanilla + " vector override(s) matching vanilla defaults");

            if (scalarHits > 0 || vectorHits > 0)
            {
                miAsset.Write(cloneFile);
                LogLine("  [OK] Slot " + slot.Index + " param overrides applied: "
                    + scalarHits + " scalar, " + vectorHits + " vector");
            }
        }

        // Returns null if the param was inherited from the parent master material rather than overridden in the MI itself.
        static MITextureParam FindTextureParam(MaterialInstanceData mi, string name)
        {
            if (mi?.Textures == null) return null;
            foreach (var t in mi.Textures)
                if (string.Equals(t.Name, name, System.StringComparison.Ordinal))
                    return t;
            return null;
        }

        static MIScalarParam FindScalarParam(MaterialInstanceData mi, string name)
        {
            if (mi?.Scalars == null) return null;
            foreach (var s in mi.Scalars)
                if (string.Equals(s.Name, name, System.StringComparison.Ordinal))
                    return s;
            return null;
        }

        static MIVectorParam FindVectorParam(MaterialInstanceData mi, string name)
        {
            if (mi?.Vectors == null) return null;
            foreach (var v in mi.Vectors)
                if (string.Equals(v.Name, name, System.StringComparison.Ordinal))
                    return v;
            return null;
        }

        static string StemFromPath(string path)
        {
            if (string.IsNullOrEmpty(path)) return path;
            int last = path.LastIndexOfAny(new[] { '/', '\\' });
            return last < 0 ? path : path.Substring(last + 1);
        }

        void PatchDataAsset(BuildingTemplate template, BuildingInputs inputs,
                            string legacyDaPath, string stagingItemsDir, BuildingPatchResult result)
        {
            var outDaStem = "DA_BI_" + inputs.BuildingId;
            var outDaPath = WindrosePaths.ModItemsPackagePath + outDaStem;
            var outDaFile = Path.Combine(stagingItemsDir, outDaStem + ".uasset");

            var outMeshStem = inputs.MeshStem;
            var outMeshPath = WindrosePaths.ModItemsPackagePath + outMeshStem;
            var outIconStem = inputs.IconStem;
            var outIconPath = WindrosePaths.ModItemsPackagePath + outIconStem;

            // BuildingId already carries the "QmBldg_" prefix - don't double-prefix.
            var outRecipeStem = "DA_RD_" + inputs.BuildingId;
            var outRecipePath = "/R5BusinessRules/Recipes/Building/Items/Decorations/" + outRecipeStem;

            // FText keys live inline in the DA body (not the NameMap); they're handled by Step 7's FTextKeyRewriter.
            var daReplacements = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [template.VanillaMeshStem] = outMeshStem,
                [template.VanillaMeshPath] = outMeshPath,

                [template.VanillaIconStem] = outIconStem,
                [template.VanillaIconPath] = outIconPath,

                [template.VanillaDaStem] = outDaStem,
                [template.VanillaDaPath] = outDaPath,
            };

            if (!string.IsNullOrEmpty(template.VanillaRecipeStem)
                && !string.IsNullOrEmpty(template.VanillaRecipePackagePath))
            {
                daReplacements[template.VanillaRecipeStem]        = outRecipeStem;
                daReplacements[template.VanillaRecipePackagePath] = outRecipePath;
            }

            // Later entries win on collision; the caller must not overlap with the built-in rewrites above.
            if (inputs.ExtraDaNameMapRewrites != null)
            {
                foreach (var kv in inputs.ExtraDaNameMapRewrites)
                {
                    if (kv.Key == null || kv.Value == null) continue;
                    daReplacements[kv.Key] = kv.Value;
                }
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

        // Keyed on BuildingId (not AssetPrefix) so two buildings sharing one prefix don't produce colliding MI clone paths.
        static string BuildSlotCloneStem(BuildingInputs inputs, MeshSlotInput slot)
            => "MI_" + inputs.BuildingId + "_slot" + slot.Index;

        static string BuildSlotClonePath(BuildingInputs inputs, MeshSlotInput slot)
            => WindrosePaths.ModItemsPackagePath + BuildSlotCloneStem(inputs, slot);

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

            // Re-validate even though the GUI gates Save, so a hand-edited profile JSON can't crash the patcher mid-flight.
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

    public sealed class BuildingInputs
    {
        // Drives the output DA stem (DA_BI_<BuildingId>) and the localization key.
        public string BuildingId;

        public string AssetPrefix;

        public string CookedFolderPath;

        public string MeshStem;

        public string IconStem;

        // Consumed by the orchestrator to synthesize the localization CSV, not by the patcher.
        public string DisplayName;
        public string Description;

        public List<MeshSlotInput> MeshSlots;

        // null = use the template's vanilla recipe defaults. Empty list = explicit free-build override.
        public List<(string ItemPath, int Count)> RecipeCost;

        // Extra DA NameMap rewrites merged before DataAssetPatcher runs (e.g. retargeting ItemClass FSoftClassPath). Null = none.
        public Dictionary<string, string> ExtraDaNameMapRewrites;
    }

    public sealed class MeshSlotInput
    {
        public int    Index;
        public string SlotName;

        // User-MI stem the cooked mesh references in this slot. May be null if no MI is bound (then no mesh-side rewrite, but the slot still gets a clone).
        public string UserMaterialStem;
        public string UserMaterialPath;

        public string VanillaMaterialParentPath;

        // Missing entries leave the cloned MI's value unchanged from Vanilla.
        public Dictionary<string, float>    ScalarParams;
        public Dictionary<string, float[]>  VectorParams;
        public Dictionary<string, string>   TextureParams;
    }

    public sealed class BuildingPatchResult
    {
        public string BuildingId;
        public string TemplateId;

        public string OutputDaStem;
        public string OutputDaPath;

        // Empty/null means the field was not filled and the FText.Base record was written with SourceString="" (blank tooltip line, not <MISSING_STRING>).
        public string DisplayName;
        public string Description;

        public List<string> StagedFiles;

        public List<string> Warnings;

        public string OutputRecipeStem;
        public string OutputRecipeJsonPath;
        public string NewRecipeTag;
        public int    RecipeCostRows;
        public bool   RecipeCostOverridden;
    }
}
