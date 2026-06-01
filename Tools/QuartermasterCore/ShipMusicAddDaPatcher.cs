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
    public sealed class ShipMusicAddDaPatcher
    {
        public Action<string> Log;

        const EngineVersion Ue = EngineVersion.VER_UE5_6;

        public const string DaRelDir =
            "R5/Content/Gameplay/Water/Character/Params/Audio";

        // excludedIndices are 0-based positions in the original vanilla
        // Cues array, applied before slots are appended.
        public ShipMusicAddDaPatchResult Patch(
            string inputDaPath, string outputDaPath, string usmapPath,
            IReadOnlyCollection<int> excludedIndices,
            IReadOnlyList<ShipMusicAddSlotRef> slots)
        {
            if (string.IsNullOrEmpty(inputDaPath))  throw new ArgumentNullException("inputDaPath");
            if (string.IsNullOrEmpty(outputDaPath)) throw new ArgumentNullException("outputDaPath");
            if (string.IsNullOrEmpty(usmapPath))    throw new ArgumentNullException("usmapPath");
            bool hasExcludes = excludedIndices != null && excludedIndices.Count > 0;
            bool hasSlots = slots != null && slots.Count > 0;
            if (!hasExcludes && !hasSlots)
                throw new ArgumentException(
                    "Patch needs at least one excluded index or one new slot - "
                    + "calling with both empty would just rewrite vanilla bytes.");
            if (!File.Exists(inputDaPath))
                throw new FileNotFoundException("Vanilla DA not found: " + inputDaPath);
            if (!File.Exists(usmapPath))
                throw new FileNotFoundException("Usmap not found: " + usmapPath);

            LogLine("Loading usmap: " + usmapPath);
            var mappings = new Usmap(usmapPath);

            LogLine("Loading DA: " + inputDaPath);
            var asset = new UAsset(inputDaPath, Ue, mappings);

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

            var excludeSet = hasExcludes
                ? new HashSet<int>(excludedIndices)
                : new HashSet<int>();
            var keptVanilla = new List<PropertyData>(cues.Value.Length);
            int droppedCount = 0;
            for (int i = 0; i < cues.Value.Length; i++)
            {
                if (excludeSet.Contains(i))
                {
                    droppedCount++;
                    LogLine("  -slot vanilla index " + i + " excluded (removed from Cues)");
                    continue;
                }
                keptVanilla.Add(cues.Value[i]);
            }
            int slotsCount = hasSlots ? slots.Count : 0;
            if (keptVanilla.Count == 0 && slotsCount == 0)
                throw new InvalidOperationException(
                    "Exclusion would leave " + Path.GetFileName(inputDaPath)
                    + " with an empty Cues array and no replacements - "
                    + "the engine would crash. Refuse to write.");

            // StructProperty array elements share the array's Name, so reuse an
            // existing entry as template; use the original tail even if excluded.
            var template = cues.Value[cues.Value.Length - 1] as StructPropertyData
                ?? throw new InvalidOperationException("Last existing cue is not StructProperty");

            var perSlotResults = new List<ShipMusicAddSlotApplied>(slotsCount);
            var newCues = new PropertyData[keptVanilla.Count + slotsCount];
            for (int i = 0; i < keptVanilla.Count; i++) newCues[i] = keptVanilla[i];

            int writeIdx = keptVanilla.Count;
            if (hasSlots) foreach (var slot in slots)
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
                Excluded = droppedCount,
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

        // The string-overload auto-registers the FName strings into the
        // NameMap. Returns a 1-based index; caller negates it for FPackageIndex.
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

    public sealed class ShipMusicAddSlotRef
    {
        public string VoiceCueStem;
        public string NoPlayerCueStem;
        public string VoiceCuePackagePath;
        public string NoPlayerCuePackagePath;
    }

    public sealed class ShipMusicAddDaPatchResult
    {
        public int BeforeCues;
        public int AfterCues;
        public int Excluded;
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
        public int VoicePkgImport;
        public int NoPlayerPkgImport;
        public int VoiceObjImport;
        public int NoPlayerObjImport;
    }
}
