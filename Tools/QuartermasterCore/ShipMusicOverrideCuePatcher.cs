using System;
using System.IO;
using UAssetAPI;
using UAssetAPI.UnrealTypes;
using UAssetAPI.ExportTypes;
using UAssetAPI.PropertyTypes.Objects;
using UAssetAPI.Unversioned;

namespace Windrose.Quartermaster.Core
{
    public sealed class ShipMusicOverrideCuePatcher
    {
        public Action<string> Log;

        const EngineVersion Ue = EngineVersion.VER_UE5_6;

        public Patched Patch(
            string inputUassetPath, string outputUassetPath, string usmapPath,
            double userVolumeAbsolute)
        {
            if (string.IsNullOrEmpty(inputUassetPath))  throw new ArgumentNullException("inputUassetPath");
            if (string.IsNullOrEmpty(outputUassetPath)) throw new ArgumentNullException("outputUassetPath");
            if (string.IsNullOrEmpty(usmapPath))        throw new ArgumentNullException("usmapPath");
            if (!File.Exists(inputUassetPath))
                throw new FileNotFoundException("Vanilla cue not found: " + inputUassetPath);
            if (!File.Exists(usmapPath))
                throw new FileNotFoundException("Usmap not found: " + usmapPath);

            double clamped = userVolumeAbsolute;
            if (clamped < 0.0) clamped = 0.0;
            if (clamped > 1.0) clamped = 1.0;

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
            float newVol = (float)clamped;
            volProp.Value = newVol;

            var outDir = Path.GetDirectoryName(outputUassetPath);
            if (!string.IsNullOrEmpty(outDir)) Directory.CreateDirectory(outDir);

            LogLine("  VolumeMultiplier: "
                + oldVol.ToString(System.Globalization.CultureInfo.InvariantCulture)
                + " -> " + newVol.ToString(System.Globalization.CultureInfo.InvariantCulture)
                + " (absolute, user slider value)");

            asset.Write(outputUassetPath);
            return new Patched
            {
                InputPath      = inputUassetPath,
                OutputPath     = outputUassetPath,
                OldVolume      = oldVol,
                NewVolume      = newVol,
                UserAbsolute   = clamped,
            };
        }

        void LogLine(string msg) { if (Log != null) Log(msg); }

        public sealed class Patched
        {
            public string InputPath;
            public string OutputPath;
            public float OldVolume;
            public float NewVolume;
            public double UserAbsolute;
        }
    }
}
