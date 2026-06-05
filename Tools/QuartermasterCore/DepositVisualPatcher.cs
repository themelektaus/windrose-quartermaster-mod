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
    // "Deposit visuals" (the Iron / Sulfur "Visual Tweak" reference mods).
    //
    // Mechanism (derived from VANILLA, reproduces the reference mods exactly):
    // a deposit's MaterialInstanceConstant binds its base-colour via a named
    // TextureParameterValue ("Albedo") that points at a Texture2D import. The
    // reference mods re-point that one parameter at a DIFFERENT texture that already
    // ships in the game (e.g. iron -> a bright mushroom map; sulfur -> the real
    // sulfur albedo instead of the generic rock one), without bundling any new
    // texture. We do the same: rename the existing Albedo import slots (the Texture2D
    // import and its outer Package import) in place to the chosen target, then fix
    // the TextureStreamingData hint. retoc to-zen recomputes the package id from the
    // name, so the engine resolves the swapped texture at runtime.
    //
    // Why rename-in-place (not add-imports): vanilla deposit MIs carry exactly one
    // Package import per texture, so the Albedo import is unique and renaming it is
    // the minimal edit - it also matches the reference mods byte-for-byte (same
    // import count / layout). We assert that uniqueness and fail loudly otherwise.
    public sealed class DepositVisualPatcher
    {
        public Action<string> Log;

        // Applies every job against the just-extracted vanilla deposit MIs in the
        // IoStore staging tree. `stagingDir` is the composite legacy root retoc
        // to-legacy populated; `usmapPath` is the unversioned property map.
        public DepositVisualPatchResult Patch(string stagingDir, string usmapPath, IReadOnlyList<DepositVisualJob> jobs)
        {
            if (string.IsNullOrEmpty(stagingDir)) throw new ArgumentNullException("stagingDir");
            if (string.IsNullOrEmpty(usmapPath)) throw new ArgumentNullException("usmapPath");
            if (!File.Exists(usmapPath)) throw new FileNotFoundException("Usmap not found: " + usmapPath);
            if (jobs == null || jobs.Count == 0)
                throw new ArgumentException("Deposit visuals: no jobs supplied.", "jobs");

            var mappings = new Usmap(usmapPath);
            var result = new DepositVisualPatchResult { Applied = new List<string>() };

            foreach (var job in jobs)
            {
                var assetPath = Path.Combine(stagingDir,
                    job.AssetVirtualPath.Replace('/', Path.DirectorySeparatorChar));
                if (!File.Exists(assetPath))
                    throw new InvalidOperationException(
                        "Deposit visuals: expected the vanilla deposit material at " + assetPath
                        + " after retoc to-legacy, but it is missing - the game container may "
                        + "have moved it (filter '" + job.AssetStem + "').");
                if (!File.Exists(Path.ChangeExtension(assetPath, ".uexp")))
                    throw new FileNotFoundException(
                        "Deposit visuals: legacy uexp sibling not found for " + assetPath
                        + " - expected a uasset/uexp pair from `retoc to-legacy`.");

                if (RepointAlbedo(assetPath, mappings, job))
                {
                    result.AssetsPatched++;
                    result.Applied.Add(job.DepositKey + " -> " + job.TextureLabel);
                }
            }

            // A 0 here means every target was already at the chosen texture (benign,
            // idempotent re-run). A genuinely broken layout / missing param throws
            // earlier from RepointAlbedo, so we don't fail loudly on 0.
            if (result.AssetsPatched == 0)
                LogLine("Deposit visuals: nothing to swap - every selected deposit already "
                        + "used the chosen texture.");
            else
                LogLine("Deposit visuals: re-pointed " + result.AssetsPatched + " material(s): "
                        + string.Join("; ", result.Applied));
            return result;
        }

        // Renames the chosen texture-parameter's import slots to `job`'s target.
        // Returns false (no write) if the parameter already points at the target.
        bool RepointAlbedo(string assetPath, Usmap mappings, DepositVisualJob job)
        {
            var asset = new UAsset(assetPath, UAssetIo.Ue, mappings);

            var mi = asset.Exports.OfType<NormalExport>()
                .FirstOrDefault(e => e.GetExportClassType()?.Value?.Value == "MaterialInstanceConstant");
            if (mi == null)
                throw new InvalidOperationException(
                    "Deposit visuals: " + Path.GetFileName(assetPath)
                    + " has no MaterialInstanceConstant export.");

            var texArray = mi.Data.FirstOrDefault(p =>
                p.Name != null && p.Name.ToString() == "TextureParameterValues") as ArrayPropertyData;
            if (texArray?.Value == null)
                throw new InvalidOperationException(
                    "Deposit visuals: no TextureParameterValues array in "
                    + Path.GetFileName(assetPath) + " - the material layout changed.");

            ObjectPropertyData paramObj = null;
            foreach (var item in texArray.Value.OfType<StructPropertyData>())
            {
                var info = item.Value?.OfType<StructPropertyData>()
                    .FirstOrDefault(s => s.Name?.Value?.Value == "ParameterInfo");
                var pname = info?.Value?.OfType<NamePropertyData>()
                    .FirstOrDefault(n => n.Name?.Value?.Value == "Name")?.Value?.Value?.Value;
                if (pname == job.ParamName)
                {
                    paramObj = item.Value?.OfType<ObjectPropertyData>()
                        .FirstOrDefault(o => o.Name?.Value?.Value == "ParameterValue");
                    break;
                }
            }
            if (paramObj == null)
                throw new InvalidOperationException(
                    "Deposit visuals: texture parameter '" + job.ParamName + "' not found in "
                    + Path.GetFileName(assetPath) + " - the material layout changed.");

            var texImp = ResolveImport(asset, paramObj.Value);
            if (texImp == null)
                throw new InvalidOperationException(
                    "Deposit visuals: '" + job.ParamName + "' in " + Path.GetFileName(assetPath)
                    + " does not point at an import (cannot swap).");
            string oldStem = texImp.ObjectName?.Value?.Value;

            // Idempotent: re-running on an already-swapped asset is a no-op.
            if (string.Equals(oldStem, job.NewTextureStem, StringComparison.Ordinal))
            {
                LogLine("Deposit visuals: " + Path.GetFileNameWithoutExtension(assetPath)
                        + " already uses " + job.NewTextureStem + " - skipped.");
                return false;
            }

            int pkgImpIdx = texImp.OuterIndex != null && texImp.OuterIndex.Index < 0
                ? -texImp.OuterIndex.Index - 1 : -1;
            if (pkgImpIdx < 0 || pkgImpIdx >= asset.Imports.Count)
                throw new InvalidOperationException(
                    "Deposit visuals: texture import in " + Path.GetFileName(assetPath)
                    + " has no resolvable outer Package import.");
            var pkgImp = asset.Imports[pkgImpIdx];

            // The Package import must be private to this one Texture2D, or renaming it
            // would silently retarget another texture parameter too.
            int sharers = asset.Imports.Count(im => im.OuterIndex != null && im.OuterIndex.Index < 0
                                                    && (-im.OuterIndex.Index - 1) == pkgImpIdx);
            if (sharers != 1)
                throw new InvalidOperationException(
                    "Deposit visuals: the '" + job.ParamName + "' texture package import in "
                    + Path.GetFileName(assetPath) + " is shared by " + sharers
                    + " imports; refusing to rename in place to avoid corrupting another parameter.");

            texImp.ObjectName = FName.FromString(asset, job.NewTextureStem);
            pkgImp.ObjectName = FName.FromString(asset, job.NewTexturePackagePath);

            // TextureStreamingData carries literal texture-name hints; update the one(s)
            // that named the old albedo so the cooked hint matches the new binding.
            var streaming = mi.Data.FirstOrDefault(p =>
                p.Name != null && p.Name.ToString() == "TextureStreamingData") as ArrayPropertyData;
            int streamUpdated = 0;
            if (streaming?.Value != null)
            {
                foreach (var si in streaming.Value.OfType<StructPropertyData>())
                {
                    var tn = si.Value?.OfType<NamePropertyData>()
                        .FirstOrDefault(n => n.Name?.Value?.Value == "TextureName");
                    if (tn != null && string.Equals(tn.Value?.Value?.Value, oldStem, StringComparison.Ordinal))
                    {
                        tn.Value = FName.FromString(asset, job.NewTextureStem);
                        streamUpdated++;
                    }
                }
            }

            asset.Write(assetPath);
            LogLine("Deposit visuals: " + Path.GetFileNameWithoutExtension(assetPath)
                    + " - " + job.ParamName + " '" + oldStem + "' -> '" + job.NewTextureStem
                    + "' (streaming hints updated: " + streamUpdated + ")");
            return true;
        }

        static Import ResolveImport(UAsset asset, FPackageIndex idx)
        {
            if (idx == null || idx.Index >= 0) return null;
            int i = -idx.Index - 1;
            if (i < 0 || i >= asset.Imports.Count) return null;
            return asset.Imports[i];
        }

        void LogLine(string msg)
        {
            if (Log != null) Log(msg);
        }
    }

    // One Albedo-swap instruction. Built by the resolver from the profile + catalog.
    public sealed class DepositVisualJob
    {
        public string DepositKey;
        public string AssetStem;
        public string AssetVirtualPath;
        public string ParamName;
        public string NewTextureStem;
        public string NewTexturePackagePath;
        public string TextureLabel;
    }

    public sealed class DepositVisualPatchResult
    {
        public int AssetsPatched;
        public List<string> Applied;
    }
}
