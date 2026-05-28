using System;
using System.IO;
using UAssetAPI;
using UAssetAPI.UnrealTypes;
using UAssetAPI.ExportTypes;
using UAssetAPI.PropertyTypes.Objects;
using UAssetAPI.Unversioned;

namespace Windrose.Quartermaster.Core
{
    // Per-slot SoundCue VolumeMultiplier patcher for vanilla shanty
    // OVERRIDE slots. Distinct from ShipMusicAddCueCloner: that one
    // clones cue 10 under a new name (for added tracks). This one
    // keeps the cue at its vanilla path/name and only scales the
    // existing VolumeMultiplier by a user-supplied factor.
    //
    // Why a separate patcher: override slots replace the SWAV under
    // the vanilla cue's path. The cue itself is unchanged unless the
    // user pulls the volume slider off 1.0 - then we extract the 4
    // vanilla CUE_Shanti_<n>_* variants (Large/Medium/Small VoicePlayer
    // + NoPlayer), multiply each VolumeMultiplier by the user factor,
    // and write them back at the same vanilla path so the mod-pak
    // overrides the cue. The DA never needs updating because the cue
    // keeps its original name.
    //
    // Vanilla VolumeMultiplier values (verified by recon, consistent
    // across all 10 shanties):
    //   Large/Medium/Small VoicePlayer:  0.45
    //   NoPlayer:                        0.50
    // The patcher reads the actual current value and multiplies, so
    // future content updates that change the vanilla baseline still
    // produce the right relative volume.
    public sealed class ShipMusicOverrideCuePatcher
    {
        public Action<string> Log;

        const EngineVersion Ue = EngineVersion.VER_UE5_6;

        // Patches Export[0].VolumeMultiplier of the vanilla cue in place.
        // Returns the (oldValue, newValue) pair for logging. The cue's
        // NameMap + FolderName + graph stay verbatim - the IoStore
        // staging tree drops the file at its vanilla path so the mod-pak
        // overrides the SWAV-bound cue.
        //
        //   inputUassetPath  - vanilla CUE_Shanti_<n>_*.uasset (sibling .uexp
        //                      lives next to it on disk; UAssetAPI reads
        //                      both via the .uasset constructor).
        //   outputUassetPath - destination .uasset path (typically the
        //                      same path under the staging tree).
        //   usmapPath        - shared .usmap mappings (UE5 unversioned).
        //   userVolumeMultiplier - factor applied to existing
        //                      VolumeMultiplier. Clamped to [0.01, 2.0].
        public Patched Patch(
            string inputUassetPath, string outputUassetPath, string usmapPath,
            double userVolumeMultiplier)
        {
            if (string.IsNullOrEmpty(inputUassetPath))  throw new ArgumentNullException("inputUassetPath");
            if (string.IsNullOrEmpty(outputUassetPath)) throw new ArgumentNullException("outputUassetPath");
            if (string.IsNullOrEmpty(usmapPath))        throw new ArgumentNullException("usmapPath");
            if (!File.Exists(inputUassetPath))
                throw new FileNotFoundException("Vanilla cue not found: " + inputUassetPath);
            if (!File.Exists(usmapPath))
                throw new FileNotFoundException("Usmap not found: " + usmapPath);

            double clamped = userVolumeMultiplier;
            if (clamped < 0.01) clamped = 0.01;
            if (clamped > 2.0)  clamped = 2.0;

            LogLine("Loading cue: " + inputUassetPath);
            var mappings = new Usmap(usmapPath);
            var asset = new UAsset(inputUassetPath, Ue, mappings);

            if (asset.Exports.Count == 0)
                throw new InvalidOperationException(
                    "Cue asset has no exports: " + inputUassetPath);
            var cueExp = asset.Exports[0] as NormalExport;
            if (cueExp == null)
                throw new InvalidOperationException(
                    "Export[0] is not a NormalExport: " + inputUassetPath);

            FloatPropertyData volProp = null;
            foreach (var p in cueExp.Data)
            {
                if (p?.Name?.Value?.Value == "VolumeMultiplier"
                    && p is FloatPropertyData vp)
                {
                    volProp = vp;
                    break;
                }
            }
            if (volProp == null)
                throw new InvalidOperationException(
                    "SoundCue.VolumeMultiplier property missing in "
                    + inputUassetPath + " - vanilla cue layout drifted?");

            float oldVol = volProp.Value;
            float newVol = (float)(oldVol * clamped);
            volProp.Value = newVol;

            var outDir = Path.GetDirectoryName(outputUassetPath);
            if (!string.IsNullOrEmpty(outDir)) Directory.CreateDirectory(outDir);

            LogLine("  VolumeMultiplier: "
                + oldVol.ToString(System.Globalization.CultureInfo.InvariantCulture)
                + " * " + clamped.ToString(System.Globalization.CultureInfo.InvariantCulture)
                + " -> " + newVol.ToString(System.Globalization.CultureInfo.InvariantCulture));

            asset.Write(outputUassetPath);
            return new Patched
            {
                InputPath        = inputUassetPath,
                OutputPath       = outputUassetPath,
                OldVolume        = oldVol,
                NewVolume        = newVol,
                UserMultiplier   = clamped,
            };
        }

        void LogLine(string msg) { if (Log != null) Log(msg); }

        public sealed class Patched
        {
            public string InputPath;
            public string OutputPath;
            public float OldVolume;
            public float NewVolume;
            public double UserMultiplier;
        }
    }
}
