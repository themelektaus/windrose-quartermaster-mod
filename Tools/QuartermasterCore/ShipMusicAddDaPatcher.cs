using System;
using System.Collections.Generic;
using System.IO;
using UAssetAPI;
using UAssetAPI.UnrealTypes;
using UAssetAPI.ExportTypes;
using UAssetAPI.PropertyTypes.Objects;
using UAssetAPI.PropertyTypes.Structs;
using UAssetAPI.Unversioned;

namespace Windrose.Quartermaster.Core
{
    // M2: append one or more track slots to a vanilla
    // DA_<ShipType>_AudioParams DataAsset. Each appended slot adds:
    //
    //   * 2 Imports per slot to the asset's ImportMap:
    //       Package (ClassPackage=/Script/CoreUObject, ClassName=Package,
    //                ObjectName=/Game/Audio/.../CUE_Shanti_N_*, Outer=0)
    //       SoundCue (ClassPackage=/Script/Engine, ClassName=SoundCue,
    //                 ObjectName=CUE_Shanti_N_*, Outer=-<previous package>)
    //   * 1 R5ShantyCuData StructProperty in Export[0].Shanty.Cues with
    //       AutonomousShantySound (Object ref to the VoicePlayer cue)
    //       SimulatedShantySound (Object ref to the VoiceNoPlayer cue)
    //
    // The DA must already have its Shanty.Cues populated with vanilla
    // entries 1..10. We do NOT touch existing slots; we only extend the
    // array. UAssetAPI handles offset/header recomputation on Write().
    //
    // DA -> VoicePlayer-flavor mapping (recon-verified, see
    // .build-tmp/shanties-recon/):
    //   DA_Brig_AudioParams         -> Medium VoicePlayer + NoPlayer
    //   DA_Frigate_AudioParams      -> Large  VoicePlayer + NoPlayer
    //   DA_FrigateNoCrue_AudioParams-> Large  VoicePlayer + NoPlayer
    //   DA_Ketch_AudioParams        -> Small  VoicePlayer + NoPlayer
    public sealed class ShipMusicAddDaPatcher
    {
        public Action<string> Log;

        const EngineVersion Ue = EngineVersion.VER_UE5_6;

        // Vanilla virtual paths for the 4 DataAssets the game ships with.
        // Used by the build pipeline to filter retoc to-legacy + know where
        // to drop the patched output back into the IoStore staging tree.
        public const string DaRelDir =
            "R5/Content/Gameplay/Water/Character/Params/Audio";

        // Inputs:
        //   inputDaPath   - vanilla DA_<Name>_AudioParams.uasset (sibling
        //                   .uexp implicit, both must exist).
        //   outputDaPath  - target path; can equal input for in-place.
        //   usmapPath     - shared UE5 unversioned-properties mapping.
        //   slots         - new slots to append. Indices start at the first
        //                   free index (typically 11 if vanilla has 10).
        //                   Each slot has voice + noplayer cue stem to
        //                   reference (must match the cue clones produced
        //                   by ShipMusicAddCueCloner).
        public ShipMusicAddDaPatchResult Patch(
            string inputDaPath, string outputDaPath, string usmapPath,
            IReadOnlyList<ShipMusicAddSlotRef> slots)
        {
            if (string.IsNullOrEmpty(inputDaPath))  throw new ArgumentNullException("inputDaPath");
            if (string.IsNullOrEmpty(outputDaPath)) throw new ArgumentNullException("outputDaPath");
            if (string.IsNullOrEmpty(usmapPath))    throw new ArgumentNullException("usmapPath");
            if (slots == null || slots.Count == 0)
                throw new ArgumentException("At least one slot is required");
            if (!File.Exists(inputDaPath))
                throw new FileNotFoundException("Vanilla DA not found: " + inputDaPath);
            if (!File.Exists(usmapPath))
                throw new FileNotFoundException("Usmap not found: " + usmapPath);

            LogLine("Loading usmap: " + usmapPath);
            var mappings = new Usmap(usmapPath);

            LogLine("Loading DA: " + inputDaPath);
            var asset = new UAsset(inputDaPath, Ue, mappings);

            // Locate the Shanty.Cues array on the single NormalExport.
            var ne = asset.Exports[0] as NormalExport
                ?? throw new InvalidOperationException(
                    "Export[0] is not a NormalExport in " + inputDaPath
                    + " - the DA might be Zen-source or corrupted");
            var cues = FindCuesArray(ne)
                ?? throw new InvalidOperationException(
                    "Shanty.Cues array not found on Export[0] of " + inputDaPath);

            int beforeImports = asset.Imports.Count;
            int beforeNameMap = asset.GetNameMapIndexList().Count;
            int beforeCues = cues.Value.Length;
            LogLine("Before: NameMap=" + beforeNameMap + " Imports=" + beforeImports + " Cues=" + beforeCues);

            // Clone the last vanilla entry to inherit its element-name
            // (StructProperty array elements share the array's Name).
            var template = cues.Value[cues.Value.Length - 1] as StructPropertyData
                ?? throw new InvalidOperationException("Last existing cue is not StructProperty");

            var perSlotResults = new List<ShipMusicAddSlotApplied>(slots.Count);
            var newCues = new PropertyData[cues.Value.Length + slots.Count];
            cues.Value.CopyTo(newCues, 0);

            int writeIdx = cues.Value.Length;
            foreach (var slot in slots)
            {
                if (slot == null) throw new ArgumentException("Null slot in list");
                if (string.IsNullOrEmpty(slot.VoiceCueStem))
                    throw new ArgumentException("VoiceCueStem is required for new slot");
                if (string.IsNullOrEmpty(slot.NoPlayerCueStem))
                    throw new ArgumentException("NoPlayerCueStem is required for new slot");
                if (string.IsNullOrEmpty(slot.VoiceCuePackagePath))
                    throw new ArgumentException("VoiceCuePackagePath is required");
                if (string.IsNullOrEmpty(slot.NoPlayerCuePackagePath))
                    throw new ArgumentException("NoPlayerCuePackagePath is required");

                int voicePkg     = AddImport(asset, "/Script/CoreUObject", "Package",  slot.VoiceCuePackagePath,    0);
                int noplayerPkg  = AddImport(asset, "/Script/CoreUObject", "Package",  slot.NoPlayerCuePackagePath, 0);
                int voiceObj     = AddImport(asset, "/Script/Engine",      "SoundCue", slot.VoiceCueStem,           -voicePkg);
                int noplayerObj  = AddImport(asset, "/Script/Engine",      "SoundCue", slot.NoPlayerCueStem,        -noplayerPkg);

                var entry = new StructPropertyData(template.Name)
                {
                    StructType = template.StructType,
                    StructGUID = template.StructGUID,
                    Value = new List<PropertyData>
                    {
                        new ObjectPropertyData(FName.FromString(asset, "AutonomousShantySound"))
                            { Value = new FPackageIndex(-voiceObj) },
                        new ObjectPropertyData(FName.FromString(asset, "SimulatedShantySound"))
                            { Value = new FPackageIndex(-noplayerObj) },
                    },
                };
                newCues[writeIdx++] = entry;

                perSlotResults.Add(new ShipMusicAddSlotApplied
                {
                    VoiceCueStem      = slot.VoiceCueStem,
                    NoPlayerCueStem   = slot.NoPlayerCueStem,
                    VoicePkgImport    = voicePkg,
                    NoPlayerPkgImport = noplayerPkg,
                    VoiceObjImport    = voiceObj,
                    NoPlayerObjImport = noplayerObj,
                });

                LogLine("  +slot voice='" + slot.VoiceCueStem + "' noplayer='" + slot.NoPlayerCueStem
                        + "' imports voicePkg=-" + voicePkg + " noplayerPkg=-" + noplayerPkg
                        + " voiceObj=-" + voiceObj + " noplayerObj=-" + noplayerObj);
            }

            cues.Value = newCues;

            var outDir = Path.GetDirectoryName(outputDaPath);
            if (!string.IsNullOrEmpty(outDir)) Directory.CreateDirectory(outDir);

            LogLine("Writing DA: " + outputDaPath);
            asset.Write(outputDaPath);

            int afterImports = asset.Imports.Count;
            int afterNameMap = asset.GetNameMapIndexList().Count;
            LogLine("After:  NameMap=" + afterNameMap + " Imports=" + afterImports + " Cues=" + cues.Value.Length);

            return new ShipMusicAddDaPatchResult
            {
                BeforeCues = beforeCues,
                AfterCues = cues.Value.Length,
                BeforeImports = beforeImports,
                AfterImports = afterImports,
                BeforeNameMap = beforeNameMap,
                AfterNameMap = afterNameMap,
                SlotsApplied = perSlotResults,
            };
        }

        static ArrayPropertyData FindCuesArray(NormalExport ne)
        {
            foreach (var p in ne.Data)
            {
                if (p is StructPropertyData shanty && shanty.Name?.Value?.Value == "Shanty")
                {
                    foreach (var inner in shanty.Value)
                        if (inner is ArrayPropertyData cues && cues.Name?.Value?.Value == "Cues")
                            return cues;
                    return null;
                }
            }
            return null;
        }

        // Constructs an Import via the string-overload (which auto-registers
        // the FName strings into the asset's NameMap as a side effect) and
        // appends it to ImportMap. Returns the 1-based positive index that
        // FPackageIndex uses (caller negates it when referring to it).
        static int AddImport(UAsset asset, string classPackage, string className, string objectName, int outerNeg)
        {
            var im = new Import(classPackage, className, new FPackageIndex(outerNeg), objectName, false, asset);
            asset.Imports.Add(im);
            return asset.Imports.Count;
        }

        void LogLine(string msg)
        {
            if (Log != null) Log(msg);
        }
    }

    // Input description: per new slot, the four facts the patcher needs.
    // Object-names are the cue stems; package paths are the full virtual
    // /Game/... paths under which the cue assets will be packed.
    public sealed class ShipMusicAddSlotRef
    {
        public string VoiceCueStem;          // e.g. CUE_Shanti_11_Large_VoicePlayer
        public string NoPlayerCueStem;       // e.g. CUE_Shanti_11_VoiceNoPlayer
        public string VoiceCuePackagePath;   // e.g. /Game/Audio/Game/Music/Shanti/Ships/Large/CUE_Shanti_11_Large_VoicePlayer
        public string NoPlayerCuePackagePath;// e.g. /Game/Audio/Game/Music/Shanti/VoiceNoPlayer/CUE_Shanti_11_VoiceNoPlayer
    }

    public sealed class ShipMusicAddDaPatchResult
    {
        public int BeforeCues;
        public int AfterCues;
        public int BeforeImports;
        public int AfterImports;
        public int BeforeNameMap;
        public int AfterNameMap;
        public IReadOnlyList<ShipMusicAddSlotApplied> SlotsApplied;
    }

    public sealed class ShipMusicAddSlotApplied
    {
        public string VoiceCueStem;
        public string NoPlayerCueStem;
        public int VoicePkgImport;     // 1-based, positive
        public int NoPlayerPkgImport;
        public int VoiceObjImport;
        public int NoPlayerObjImport;
    }
}
