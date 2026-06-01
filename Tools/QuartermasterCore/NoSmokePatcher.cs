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
    public sealed class NoSmokePatcher
    {
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
                    if (!enabled.Value) continue;
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
        public int NiagaraSystemCount;
        public int TotalHandles;
        public int FlippedHandles;
    }
}
