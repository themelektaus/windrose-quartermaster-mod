using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UAssetAPI;
using UAssetAPI.UnrealTypes;
using UAssetAPI.PropertyTypes.Objects;
using UAssetAPI.PropertyTypes.Structs;
using UAssetAPI.ExportTypes;
using UAssetAPI.Unversioned;

namespace Windrose.Quartermaster.Core
{
    // Patches a UE5 Legacy .uasset+.uexp pair (a Niagara FX system after
    // retoc to-legacy) to silence its visual emitters by setting every
    // EmitterHandle.bIsEnabled = false on the NiagaraSystem export.
    //
    // The reference NoSmoke_3in1_P mod ships replacements for 7 vanilla
    // Niagara assets (campfires, furnaces, kilns) that disable the smoke /
    // flame emitters. Instead of adopting the reference bytes 1:1 we
    // self-bake from vanilla: extract via retoc to-legacy, walk every
    // EmitterHandle on the NiagaraSystem export, flip bIsEnabled to false,
    // re-pack via retoc to-zen. Roundtrip is byte-identical for unpatched
    // assets (verified during the POC), so this is a safe minimal-diff
    // patch that follows the same future-proof pattern as the pickup
    // blueprint patcher.
    //
    // Workflow context:
    //   game IoStore (.ucas)
    //     -> retoc to-legacy   (Zen package -> Legacy .uasset+.uexp)
    //     -> THIS CLASS        (set every EmitterHandle.bIsEnabled = false)
    //     -> retoc to-zen      (Legacy -> IoStore triplet .pak/.ucas/.utoc)
    public sealed class NoSmokePatcher
    {
        // Toggle -> list of vanilla asset paths (relative to the game's
        // content root, with leading "R5/Content/.../<name>.uasset"). The
        // composite builder uses these as the retoc --filter values AND
        // to find the patched bytes after extraction. Filenames (without
        // extension) double as the retoc filter stem.
        public static readonly Dictionary<NoSmokeCategory, string[]> CategoryAssets =
            new Dictionary<NoSmokeCategory, string[]>
            {
                {
                    NoSmokeCategory.Campfire, new[]
                    {
                        "R5/Content/FX/Particles/Environment/Fire/FX_Bonefire_Center.uasset",
                        "R5/Content/FX/Particles/Environment/Fire/FX_Campfire_smoldering.uasset",
                        "R5/Content/FX/Particles/Environment/Fire/FX_Campfire_stylized_small.uasset",
                    }
                },
                {
                    NoSmokeCategory.Furnace, new[]
                    {
                        "R5/Content/FX/Particles/Buildings/Craftstations/FX_Flame_Furnace_T1.uasset",
                        "R5/Content/FX/Particles/Buildings/Craftstations/FX_Flame_Furnace_T3.uasset",
                    }
                },
                {
                    NoSmokeCategory.Kiln, new[]
                    {
                        "R5/Content/FX/Particles/Buildings/Craftstations/FX_Smoke_Kiln_T3.uasset",
                        "R5/Content/FX/Particles/Buildings/Craftstations/FX_Smoke_Kiln_Dop_T3.uasset",
                    }
                },
            };

        public Action<string> Log;

        // Patches the asset in-place (input == output is fine). Returns the
        // count of EmitterHandles whose bIsEnabled was flipped from true
        // to false; handles that were already false (or that lacked a
        // bIsEnabled property) are not touched and not counted.
        public NoSmokePatchResult Patch(string assetPath, string usmapPath)
        {
            if (string.IsNullOrEmpty(assetPath))
                throw new ArgumentNullException("assetPath");
            if (string.IsNullOrEmpty(usmapPath))
                throw new ArgumentNullException("usmapPath");
            if (!File.Exists(assetPath))
                throw new FileNotFoundException("Legacy uasset not found: " + assetPath);
            if (!File.Exists(usmapPath))
                throw new FileNotFoundException("Usmap mappings not found: " + usmapPath);

            LogLine("Loading uasset: " + assetPath);
            var mappings = new Usmap(usmapPath);
            var asset = new UAsset(assetPath, EngineVersion.VER_UE5_6, mappings);

            // Walk every NiagaraSystem export (typically exactly one per
            // asset, named after the FX). Each carries an EmitterHandles
            // ArrayProperty with one StructProperty per emitter; the
            // bIsEnabled BoolProperty inside is the kill switch the engine
            // honours when spawning particles.
            int totalHandles = 0;
            int patchedHandles = 0;
            int niagaraSystems = 0;
            foreach (var exp in asset.Exports)
            {
                var className = exp.GetExportClassType().Value.Value.ToString();
                if (className != "NiagaraSystem") continue;
                var ne = exp as NormalExport;
                if (ne == null) continue;
                niagaraSystems++;

                var emHandles = ne.Data.OfType<ArrayPropertyData>()
                    .FirstOrDefault(p => p.Name != null
                                         && p.Name.Value != null
                                         && p.Name.Value.Value == "EmitterHandles");
                if (emHandles == null || emHandles.Value == null) continue;

                foreach (var item in emHandles.Value)
                {
                    var handle = item as StructPropertyData;
                    if (handle == null || handle.Value == null) continue;
                    totalHandles++;
                    var enabled = handle.Value.OfType<BoolPropertyData>()
                        .FirstOrDefault(p => p.Name != null
                                             && p.Name.Value != null
                                             && p.Name.Value.Value == "bIsEnabled");
                    if (enabled == null) continue;
                    if (!enabled.Value) continue;     // already disabled
                    enabled.Value = false;
                    patchedHandles++;
                }
            }

            if (niagaraSystems == 0)
            {
                throw new InvalidOperationException(
                    "No NiagaraSystem export found in " + assetPath
                    + " - expected at least one to disable emitters on.");
            }

            LogLine("NiagaraSystems: " + niagaraSystems
                    + ", EmitterHandles: " + totalHandles
                    + ", flipped to disabled: " + patchedHandles);

            asset.Write(assetPath);

            return new NoSmokePatchResult
            {
                NiagaraSystemCount = niagaraSystems,
                TotalHandles = totalHandles,
                FlippedHandles = patchedHandles,
            };
        }

        void LogLine(string msg)
        {
            if (Log != null) Log(msg);
        }
    }

    public enum NoSmokeCategory
    {
        Campfire,
        Furnace,
        Kiln,
    }

    public sealed class NoSmokePatchResult
    {
        // How many NiagaraSystem exports were inspected on this asset.
        // 0 indicates a pipeline mismatch (filter matched the wrong file)
        // and is treated as a hard error by Patch().
        public int NiagaraSystemCount;
        // Total EmitterHandles encountered across all NiagaraSystems.
        public int TotalHandles;
        // How many of those handles had bIsEnabled flipped from true to
        // false. Handles already at false are NOT counted.
        public int FlippedHandles;
    }
}
