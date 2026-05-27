using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Windrose.Quartermaster.Core
{
    // M4 orchestrator: turns N user-supplied tracks into the asset graph
    // that ShipMusicAddDaPatcher + ShipMusicAddCueCloner need, then runs
    // both during the IoStore composite's AfterExtract callback.
    //
    // The composite source's retoc to-legacy step is invoked with a
    // multi --filter that extracts:
    //   - 4 vanilla DA_<ShipType>_AudioParams.uasset+.uexp (we patch these)
    //   - 4 vanilla CUE_Shanti_10_<flavor>(_VoicePlayer) cue assets that
    //     serve as clone templates for each user track.
    //
    // AfterExtract then:
    //   1. For each user track and each of the 4 flavors, runs
    //      ShipMusicAddCueCloner to drop a per-track per-flavor cue clone
    //      at /Game/.../CUE_Shanti_<N>_<flavor>(_VoicePlayer).
    //   2. Deletes the four vanilla CUE_Shanti_10_* template files from the
    //      staging tree - we don't want our mod-pak to re-ship them
    //      unchanged (it would overwrite vanilla with identical content,
    //      potentially fighting other mods that legitimately replace cue
    //      10).
    //   3. For each of the 4 DAs, runs ShipMusicAddDaPatcher with the
    //      full set of slot refs (one per user track), writing back to
    //      the same staging path.
    //
    // The SWAV assets themselves (one per track) ride in SEPARATE pre-staged
    // IoStoreCompositeSources (BuildPipeline.cs adds them next to the SWAV
    // overrides), reusing ShipMusicPatcher's WAV->Bink+template-splice
    // pipeline.
    public static class ShipMusicAddPipelineHelper
    {
        // The four shipped ship-type DataAssets that carry Shanty.Cues.
        // Filenames map to retoc to-legacy --filter stems.
        public static readonly string[] DaStems = new[]
        {
            "DA_Brig_AudioParams",
            "DA_Frigate_AudioParams",
            "DA_FrigateNoCrue_AudioParams",
            "DA_Ketch_AudioParams",
        };

        // The four flavors we clone per added track. Brig pulls from Medium,
        // Frigate / FrigateNoCrue from Large, Ketch from Small; NoPlayer is
        // shared across all DAs (every DA references CUE_Shanti_N_VoiceNoPlayer).
        public static readonly string[] Flavors = new[] { "Large", "Medium", "Small", "NoPlayer" };

        // Returns the staging-tree-relative path for a vanilla CUE_Shanti_10
        // template (the one ShipMusicAddCueCloner reads as its source).
        public static string VanillaCueRelPath(string flavor)
        {
            return ShipMusicAddCueCloner.CueRelDir(flavor) + "/"
                 + ShipMusicAddCueCloner.VanillaCueStem(flavor) + ".uasset";
        }

        // Returns the staging-tree-relative path for a NEW per-track cue
        // clone at the given new index.
        public static string NewCueRelPath(string flavor, string newIndex)
        {
            return ShipMusicAddCueCloner.CueRelDir(flavor) + "/"
                 + ShipMusicAddCueCloner.SelfStem(flavor, newIndex) + ".uasset";
        }

        // Returns the staging-tree-relative path for a DA_<ShipType>_AudioParams.
        public static string DaRelPath(string daStem)
        {
            return ShipMusicAddDaPatcher.DaRelDir + "/" + daStem + ".uasset";
        }

        // Composes a slot ref (used by ShipMusicAddDaPatcher) from the
        // (flavor, newIndex, newSwavStem) tuple. Returns the 4 fully-formed
        // import target strings the DA patcher needs to register on each
        // appended R5ShantyCuData.
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

        // Computes the (voiceFlavor) that a given DA cares about. Empirically
        // verified against the vanilla extracts (see plan).
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

        // The set of filter stems we feed to a single retoc to-legacy call.
        // Both the 4 DAs and the 4 cue templates land in one staging tree
        // pass.
        public static IEnumerable<string> AllFilters()
        {
            foreach (var da in DaStems) yield return da;
            foreach (var f in Flavors) yield return ShipMusicAddCueCloner.VanillaCueStem(f);
        }
    }

    // One scheduled added track (positional index = list order; first
    // entry takes slot 11, second slot 12, ...). Resolved by
    // BuildPipeline.ResolveShipMusicAddJobs from the per-profile config.
    public sealed class ShipMusicAddJob
    {
        // Stable filesystem key. Matches the on-disk subdir under
        // Profiles/<id>/ShipMusicAdd/<TrackKey>/ and forms the SWAV stem
        // suffix (SWAV_Shanti_<TrackKey>).
        public string TrackKey;

        // Slot index the track occupies in Shanty.Cues, expressed as a
        // string (UAssetAPI cue stems use the literal "11", "12", ...).
        public string NewIndex;

        // Absolute on-disk path to the user's source WAV.
        public string UserWavPath;

        // Display-only metadata.
        public string Title;
        public string OriginalFilename;
    }

    public sealed class ShipMusicAddResult
    {
        public bool Enabled;
        public List<ShipMusicAddTrackResult> TrackResults;
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

        // Filled by the SWAV-build pre-staged source (re-uses
        // ShipMusicPatcher's PatchFromWav).
        public string SwavStem;             // e.g. SWAV_Shanti_MyTrack
        public string SwavVirtualPath;      // e.g. R5/Content/Audio/.../SWAV_Shanti_MyTrack.uasset
        public long BinkBytes;              // size of the encoded Bink audio payload
        public float DurationSeconds;
        public int SampleRate;
        public int Channels;

        // Filled by the shared cue-clone + DA-patch source.
        public IReadOnlyList<string> CueStemsCreated;  // 4 entries per track: Large/Medium/Small VoicePlayer + NoPlayer
    }
}
