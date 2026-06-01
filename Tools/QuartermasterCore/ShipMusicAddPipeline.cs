using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Windrose.Quartermaster.Core
{
    public static class ShipMusicAddPipelineHelper
    {
        public static readonly string[] DaStems = new[]
        {
            "DA_Brig_AudioParams",
            "DA_Frigate_AudioParams",
            "DA_FrigateNoCrue_AudioParams",
            "DA_Ketch_AudioParams",
        };

        public static readonly string[] Flavors = new[] { "Large", "Medium", "Small", "NoPlayer" };

        public static string VanillaCueRelPath(string flavor)
        {
            return ShipMusicAddCueCloner.CueRelDir(flavor) + "/"
                 + ShipMusicAddCueCloner.VanillaCueStem(flavor) + ".uasset";
        }

        public static string NewCueRelPath(string flavor, string newIndex)
        {
            return ShipMusicAddCueCloner.CueRelDir(flavor) + "/"
                 + ShipMusicAddCueCloner.SelfStem(flavor, newIndex) + ".uasset";
        }

        public static string DaRelPath(string daStem)
        {
            return ShipMusicAddDaPatcher.DaRelDir + "/" + daStem + ".uasset";
        }

        public static ShipMusicAddSlotRef BuildSlotRef(string voiceFlavor, string newIndex)
        {
            var voiceStem    = ShipMusicAddCueCloner.SelfStem(voiceFlavor, newIndex);
            var voicePkgPath = "/Game/Audio/Game/Music/Shanti/Ships/" + voiceFlavor
                             + "/" + voiceStem;
            var noplayerStem    = ShipMusicAddCueCloner.SelfStem("NoPlayer", newIndex);
            var noplayerPkgPath = "/Game/Audio/Game/Music/Shanti/VoiceNoPlayer/" + noplayerStem;
            return new ShipMusicAddSlotRef
            {
                VoiceCueStem           = voiceStem,
                NoPlayerCueStem        = noplayerStem,
                VoiceCuePackagePath    = voicePkgPath,
                NoPlayerCuePackagePath = noplayerPkgPath,
            };
        }

        public static string VoiceFlavorForDa(string daStem)
        {
            switch (daStem)
            {
                case "DA_Brig_AudioParams":          return "Medium";
                case "DA_Frigate_AudioParams":       return "Large";
                case "DA_FrigateNoCrue_AudioParams": return "Large";
                case "DA_Ketch_AudioParams":         return "Small";
                default: throw new ArgumentException("Unknown DA stem: " + daStem);
            }
        }

        public static IEnumerable<string> AllFilters()
        {
            foreach (var da in DaStems) yield return da;
            foreach (var f in Flavors) yield return ShipMusicAddCueCloner.VanillaCueStem(f);
        }

        public static IEnumerable<string> Filters(bool includeCueTemplates)
        {
            foreach (var da in DaStems) yield return da;
            if (includeCueTemplates)
                foreach (var f in Flavors) yield return ShipMusicAddCueCloner.VanillaCueStem(f);
        }
    }

    public sealed class ShipMusicAddJob
    {
        // Reused as both the storage subdir name and the SWAV stem suffix.
        public string TrackKey;

        public string NewIndex;
        public string UserWavPath;
        public string Title;
        public string OriginalFilename;
        public double UserVolume;
    }

    public sealed class ShipMusicAddResult
    {
        public bool Enabled;
        public List<ShipMusicAddTrackResult> TrackResults;
        public List<int> ExcludedSlotIndices;
        public string PakPath;
        public string UcasPath;
        public string UtocPath;
    }

    public sealed class ShipMusicAddTrackResult
    {
        public string TrackKey;
        public string NewIndex;
        public string Title;
        public string OriginalFilename;

        public string SwavStem;
        public string SwavVirtualPath;
        public long BinkBytes;
        public float DurationSeconds;
        public int SampleRate;
        public int Channels;

        public IReadOnlyList<string> CueStemsCreated;
    }
}
