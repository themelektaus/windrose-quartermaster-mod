using System;
using System.Collections.Generic;
using System.IO;
using UAssetAPI;
using UAssetAPI.UnrealTypes;
using UAssetAPI.ExportTypes;
using UAssetAPI.PropertyTypes.Objects;
using UAssetAPI.Unversioned;

namespace Windrose.Quartermaster.Core
{
    // M3: clones the vanilla CUE_Shanti_10_<Variant> sound cue asset into
    // a new CUE_Shanti_<NewIndex>_<Variant> sound cue that plays the
    // user-supplied SWAV instead of vanilla MaggieMay.
    //
    // Clone steps (per cue):
    //   1. NameMap rewrites via SetNameReference - 4 entries renamed
    //      (self-stem, self-package-path, MaggieMay stem, MaggieMay path).
    //   2. .uasset header FolderName overwritten to the new self path.
    //   3. SoundCue Export[0] surgery - "minimal cue" reshape:
    //        a) FirstNode redirected from the 15-way SoundNodeRandom
    //           (vanilla picks 1 of 15 mixer variants, each with delays
    //           + ShipsChatter layering = up to 163s total Duration)
    //           to a single SoundNodeWavePlayer whose SoundWaveAssetPtr
    //           targets the renamed SWAV. The orphaned 60+ nodes stay
    //           in the file but are never reached at runtime.
    //        b) Duration shrunk from the vanilla 163s to the user
    //           audio's length + a small pad. UR5ShipAudioComponent
    //           uses this to gate when a new shanty can be picked, so
    //           leaving it at 163s would mean ~3 minutes of silence
    //           after a short user track finishes.
    //        c) bHasDelayNode flipped to False (no SoundNodeDelay in
    //           our minimal subtree, so this matches reality and lets
    //           the engine optimise the playback path).
    //
    // Four FName replacements per cue (Voice flavor):
    //   CUE_Shanti_10_<Variant>_VoicePlayer
    //     -> CUE_Shanti_<NewIndex>_<Variant>_VoicePlayer
    //   /Game/Audio/Game/Music/Shanti/Ships/<Variant>/CUE_Shanti_10_<Variant>_VoicePlayer
    //     -> /Game/Audio/Game/Music/Shanti/Ships/<Variant>/CUE_Shanti_<NewIndex>_<Variant>_VoicePlayer
    //   SWAV_Shanti_MaggieMay
    //     -> SWAV_Shanti_<NewSwavStem>
    //   /Game/Audio/Game/Music/Shanti/SWAV/SWAV_Shanti_MaggieMay
    //     -> /Game/Audio/Game/Music/Shanti/SWAV/SWAV_Shanti_<NewSwavStem>
    //
    // NoPlayer flavor uses .../VoiceNoPlayer/ in the path and drops the
    // "_<Variant>_VoicePlayer" segment from the cue stem.
    public sealed class ShipMusicAddCueCloner
    {
        public Action<string> Log;

        // The exact UE version we read/write. Tied to Windrose 5.6 just like
        // every other UAssetAPI-based patcher in this project.
        const EngineVersion Ue = EngineVersion.VER_UE5_6;

        // Vanilla template constants. Recon confirmed all four variants
        // (Large/Medium/Small VoicePlayer + NoPlayer) of cue 10 reference
        // SWAV_Shanti_MaggieMay.
        public const string TemplateCueIndex = "10";
        public const string TemplateSwavStem = "SWAV_Shanti_MaggieMay";
        public const string TemplateSwavPath =
            "/Game/Audio/Game/Music/Shanti/SWAV/SWAV_Shanti_MaggieMay";

        // Source-tree-relative path templates for the vanilla cue assets.
        // Caller composes the absolute path by prefixing the staging dir
        // where retoc to-legacy dropped the vanilla extract.
        public const string CueRelDirLarge   = "R5/Content/Audio/Game/Music/Shanti/Ships/Large";
        public const string CueRelDirMedium  = "R5/Content/Audio/Game/Music/Shanti/Ships/Medium";
        public const string CueRelDirSmall   = "R5/Content/Audio/Game/Music/Shanti/Ships/Small";
        public const string CueRelDirNoPlayer= "R5/Content/Audio/Game/Music/Shanti/VoiceNoPlayer";

        // Inputs:
        //   inputUassetPath   - vanilla CUE_Shanti_10_<flavor>.uasset (the
        //                       sibling .uexp is implicit).
        //   outputUassetPath  - new CUE_Shanti_<newIndex>_<flavor>.uasset
        //                       to be written (sibling .uexp written by
        //                       UAssetAPI in the same call).
        //   usmapPath         - shared .usmap mappings (UE5 unversioned).
        //   flavor            - "Large" / "Medium" / "Small" / "NoPlayer".
        //   newIndex          - the new track index as string, e.g. "11".
        //   newSwavStem       - the SWAV stem name (without "SWAV_Shanti_"
        //                       prefix), e.g. "MyTrack" -> binds the cue
        //                       to SWAV_Shanti_MyTrack.
        // audioDurationSec  - the user-supplied SWAV's playback length in
        //                     seconds. Written into SoundCue.Duration so
        //                     UR5ShipAudioComponent picks the next cue
        //                     ~audioDurationSec seconds after this one
        //                     starts (instead of after the vanilla 163s
        //                     timeout). A small pad is added internally.
        public ShipMusicAddCueCloneResult Clone(
            string inputUassetPath, string outputUassetPath, string usmapPath,
            string flavor, string newIndex, string newSwavStem,
            float audioDurationSec)
        {
            if (string.IsNullOrEmpty(inputUassetPath))  throw new ArgumentNullException("inputUassetPath");
            if (string.IsNullOrEmpty(outputUassetPath)) throw new ArgumentNullException("outputUassetPath");
            if (string.IsNullOrEmpty(usmapPath))        throw new ArgumentNullException("usmapPath");
            if (string.IsNullOrEmpty(flavor))           throw new ArgumentNullException("flavor");
            if (string.IsNullOrEmpty(newIndex))         throw new ArgumentNullException("newIndex");
            if (string.IsNullOrEmpty(newSwavStem))      throw new ArgumentNullException("newSwavStem");
            if (audioDurationSec <= 0f)
                throw new ArgumentOutOfRangeException("audioDurationSec",
                    "audioDurationSec must be > 0 (was " + audioDurationSec.ToString(
                        System.Globalization.CultureInfo.InvariantCulture) + ")");
            if (!File.Exists(inputUassetPath))
                throw new FileNotFoundException("Vanilla cue not found: " + inputUassetPath);
            if (!File.Exists(usmapPath))
                throw new FileNotFoundException("Usmap not found: " + usmapPath);

            var rules = BuildReplacementRules(flavor, newIndex, newSwavStem);
            var newSwav      = "SWAV_Shanti_" + newSwavStem;
            var newSelfPath  = SelfPackagePath(flavor, newIndex);

            LogLine("Loading usmap: " + usmapPath);
            var mappings = new Usmap(usmapPath);

            LogLine("Loading cue: " + inputUassetPath + " (flavor=" + flavor + ")");
            var asset = new UAsset(inputUassetPath, Ue, mappings);

            int hits = ApplyRules(asset, rules);
            if (hits != rules.Count)
            {
                throw new InvalidOperationException(
                    "Cue NameMap rename incomplete - matched " + hits + "/" + rules.Count
                    + " expected entries. Did the source asset stem drift away from "
                    + "CUE_Shanti_" + TemplateCueIndex + " / " + TemplateSwavStem + "? "
                    + "Vanilla asset: " + inputUassetPath);
            }

            // The .uasset header carries a separate FolderName / PackageName
            // FString that lives OUTSIDE the NameMap. If we don't update it,
            // the cloned cue self-identifies as the vanilla CUE_Shanti_10_*
            // package, which makes the engine's import resolution fail when
            // the DataAsset asks for CUE_Shanti_<NewIndex>_* - the
            // ObjectProperty in DA.Shanty.Cues[N].AutonomousShantySound then
            // resolves to nullptr at OnRep time and trips R5Check
            // (R5ShipAudioComponent.cpp:1458). Mirrors the FolderName
            // override pattern from IconBakerPatcher + BuildingPatcher.
            var oldFolderName = asset.FolderName?.Value;
            LogLine("  FolderName: " + (oldFolderName ?? "<null>") + " -> " + newSelfPath);
            asset.FolderName = FString.FromString(newSelfPath);

            // Minimal-cue surgery on Export[0] (the SoundCue). See class
            // doc for the why - short version: vanilla cue 10's
            // SoundNodeRandom + delay graph keeps the cue "alive" for ~163s
            // even if the leaf WavePlayer's audio is 9s long. By bypassing
            // the graph and shrinking Duration we let the engine release
            // the cue right after the user's audio finishes.
            ReshapeToMinimal(asset, newSwav, audioDurationSec);

            var outDir = Path.GetDirectoryName(outputUassetPath);
            if (!string.IsNullOrEmpty(outDir)) Directory.CreateDirectory(outDir);

            LogLine("Writing cue clone: " + outputUassetPath);
            asset.Write(outputUassetPath);

            var newCueStem = SelfStem(flavor, newIndex);
            return new ShipMusicAddCueCloneResult
            {
                Flavor          = flavor,
                NewIndex        = newIndex,
                NewCueStem      = newCueStem,
                NewSwavStem     = newSwav,
                OutputUassetPath= outputUassetPath,
                OutputUexpPath  = Path.ChangeExtension(outputUassetPath, ".uexp"),
                NameMapHits     = hits,
            };
        }

        // Convenience: returns the vanilla cue stem for a flavor. Used by
        // the pipeline to compose the template path.
        public static string VanillaCueStem(string flavor)
        {
            if (flavor == "NoPlayer") return "CUE_Shanti_" + TemplateCueIndex + "_VoiceNoPlayer";
            return "CUE_Shanti_" + TemplateCueIndex + "_" + flavor + "_VoicePlayer";
        }

        // Returns the staging-relative directory for a flavor (where the
        // cue asset belongs in /Game/...).
        public static string CueRelDir(string flavor)
        {
            switch (flavor)
            {
                case "Large":    return CueRelDirLarge;
                case "Medium":   return CueRelDirMedium;
                case "Small":    return CueRelDirSmall;
                case "NoPlayer": return CueRelDirNoPlayer;
                default: throw new ArgumentException("Unknown flavor: " + flavor);
            }
        }

        // Returns the cue stem for a (flavor, newIndex) pair.
        public static string SelfStem(string flavor, string newIndex)
        {
            if (flavor == "NoPlayer") return "CUE_Shanti_" + newIndex + "_VoiceNoPlayer";
            return "CUE_Shanti_" + newIndex + "_" + flavor + "_VoicePlayer";
        }

        // Returns the full /Game/... package path of the cloned cue (the value
        // we write to asset.FolderName so the .uasset header self-identifies
        // under the new path).
        public static string SelfPackagePath(string flavor, string newIndex)
        {
            var stem = SelfStem(flavor, newIndex);
            if (flavor == "NoPlayer")
                return "/Game/Audio/Game/Music/Shanti/VoiceNoPlayer/" + stem;
            return "/Game/Audio/Game/Music/Shanti/Ships/" + flavor + "/" + stem;
        }

        Dictionary<string, string> BuildReplacementRules(string flavor, string newIndex, string newSwavStem)
        {
            string vanillaSelf, vanillaSelfPath, newSelf, newSelfPath;
            if (flavor == "NoPlayer")
            {
                vanillaSelf     = "CUE_Shanti_" + TemplateCueIndex + "_VoiceNoPlayer";
                vanillaSelfPath = "/Game/Audio/Game/Music/Shanti/VoiceNoPlayer/" + vanillaSelf;
                newSelf         = "CUE_Shanti_" + newIndex + "_VoiceNoPlayer";
                newSelfPath     = "/Game/Audio/Game/Music/Shanti/VoiceNoPlayer/" + newSelf;
            }
            else
            {
                vanillaSelf     = "CUE_Shanti_" + TemplateCueIndex + "_" + flavor + "_VoicePlayer";
                vanillaSelfPath = "/Game/Audio/Game/Music/Shanti/Ships/" + flavor + "/" + vanillaSelf;
                newSelf         = "CUE_Shanti_" + newIndex + "_" + flavor + "_VoicePlayer";
                newSelfPath     = "/Game/Audio/Game/Music/Shanti/Ships/" + flavor + "/" + newSelf;
            }
            var newSwav     = "SWAV_Shanti_" + newSwavStem;
            var newSwavPath = "/Game/Audio/Game/Music/Shanti/SWAV/" + newSwav;
            return new Dictionary<string, string>(StringComparer.Ordinal)
            {
                { vanillaSelf,     newSelf },
                { vanillaSelfPath, newSelfPath },
                { TemplateSwavStem, newSwav },
                { TemplateSwavPath, newSwavPath },
            };
        }

        // Mutates Export[0] (the SoundCue) so that:
        //   - FirstNode points to a SoundNodeWavePlayer whose
        //     SoundWaveAssetPtr resolves to the newly-renamed SWAV (the
        //     user track). Searches the export table for the first
        //     WavePlayer matching that asset name.
        //   - Duration is set to the user audio's playback length plus a
        //     small pad so the engine treats the cue as fully consumed
        //     ~immediately after the wave finishes.
        //   - bHasDelayNode is cleared (no delay nodes in our subtree).
        void ReshapeToMinimal(UAsset asset, string newSwavStem, float audioDurationSec)
        {
            if (asset.Exports.Count == 0)
                throw new InvalidOperationException("Cue asset has no exports");
            var cueExp = asset.Exports[0] as NormalExport;
            if (cueExp == null)
                throw new InvalidOperationException("Export[0] is not a NormalExport");

            // Sanity: confirm class is SoundCue (catches future template
            // drift early instead of producing broken cues).
            var ci = cueExp.ClassIndex;
            var className = "?";
            if (ci.IsImport())
                className = asset.Imports[ci.Index * -1 - 1].ObjectName.Value.Value;
            if (className != "SoundCue")
                throw new InvalidOperationException(
                    "Export[0] class is '" + className + "', expected 'SoundCue' - "
                    + "the vanilla cue template layout drifted, the minimal-cue surgery "
                    + "needs the SoundCue at Export[0].");

            // Locate the WavePlayer leaf to redirect FirstNode to. We search
            // by SoundWaveAssetPtr -> AssetName match against the renamed
            // SWAV. Any of the (post-rename) MaggieMay WavePlayers will do,
            // but pick the first match for determinism.
            int? wpIdx = null;
            for (int i = 1; i < asset.Exports.Count; i++)
            {
                var e = asset.Exports[i] as NormalExport;
                if (e == null) continue;
                var eci = e.ClassIndex;
                string eClassName = eci.IsImport()
                    ? asset.Imports[eci.Index * -1 - 1].ObjectName.Value.Value
                    : "?";
                if (eClassName != "SoundNodeWavePlayer") continue;
                foreach (var p in e.Data)
                {
                    if (p is SoftObjectPropertyData so
                        && so.Name?.Value?.Value == "SoundWaveAssetPtr"
                        && so.Value != null
                        && so.Value.AssetPath.AssetName?.Value?.Value == newSwavStem)
                    {
                        wpIdx = i;
                        break;
                    }
                }
                if (wpIdx.HasValue) break;
            }
            if (!wpIdx.HasValue)
                throw new InvalidOperationException(
                    "No SoundNodeWavePlayer with SoundWaveAssetPtr -> '"
                    + newSwavStem + "' found - did the NameMap rewrite "
                    + "miss the SWAV reference, or does the template lack "
                    + "the expected MaggieMay leaf?");

            var targetPkgIdx = FPackageIndex.FromExport(wpIdx.Value);

            // Pad the duration a little so the audio fade-out has room
            // before the engine considers the cue spent. 0.5s matches the
            // typical SoundConcurrency release time and is short enough
            // to feel "back-to-back" between tracks.
            const float DurationPadSec = 0.5f;
            float newDuration = audioDurationSec + DurationPadSec;

            int touched = 0;
            for (int i = 0; i < cueExp.Data.Count; i++)
            {
                var p = cueExp.Data[i];
                var name = p?.Name?.Value?.Value;
                if (name == "FirstNode" && p is ObjectPropertyData op)
                {
                    var oldIdx = op.Value?.Index ?? 0;
                    op.Value = targetPkgIdx;
                    LogLine("  FirstNode: +" + oldIdx + " -> +" + targetPkgIdx.Index
                        + " (WavePlayer Export[" + wpIdx + "])");
                    touched++;
                }
                else if (name == "Duration" && p is FloatPropertyData fp)
                {
                    LogLine("  Duration: " + fp.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)
                        + " -> " + newDuration.ToString(System.Globalization.CultureInfo.InvariantCulture)
                        + " (user audio " + audioDurationSec.ToString(System.Globalization.CultureInfo.InvariantCulture)
                        + "s + " + DurationPadSec.ToString(System.Globalization.CultureInfo.InvariantCulture) + "s pad)");
                    fp.Value = newDuration;
                    touched++;
                }
                else if (name == "bHasDelayNode" && p is BoolPropertyData bp)
                {
                    LogLine("  bHasDelayNode: " + bp.Value + " -> false");
                    bp.Value = false;
                    touched++;
                }
            }

            if (touched != 3)
                throw new InvalidOperationException(
                    "Minimal-cue surgery touched " + touched + "/3 expected SoundCue "
                    + "properties (FirstNode, Duration, bHasDelayNode) - vanilla cue "
                    + "template layout may have drifted.");
        }

        int ApplyRules(UAsset asset, Dictionary<string, string> rules)
        {
            int hits = 0;
            var nm = asset.GetNameMapIndexList();
            for (int i = 0; i < nm.Count; i++)
            {
                var current = nm[i].Value;
                if (rules.TryGetValue(current, out var replacement))
                {
                    asset.SetNameReference(i, FString.FromString(replacement));
                    LogLine("  NameMap[" + i + "] '" + current + "' -> '" + replacement + "'");
                    hits++;
                }
            }
            return hits;
        }

        void LogLine(string msg)
        {
            if (Log != null) Log(msg);
        }
    }

    public sealed class ShipMusicAddCueCloneResult
    {
        public string Flavor;
        public string NewIndex;
        public string NewCueStem;     // e.g. CUE_Shanti_11_Large_VoicePlayer
        public string NewSwavStem;    // e.g. SWAV_Shanti_MyTrack
        public string OutputUassetPath;
        public string OutputUexpPath;
        public int NameMapHits;
    }
}
