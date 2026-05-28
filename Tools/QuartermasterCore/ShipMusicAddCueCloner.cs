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
        // userVolumeMultiplier - factor applied on top of the vanilla
        //                     VolumeMultiplier carried by the source cue
        //                     (0.45 for *_VoicePlayer flavors, 0.5 for
        //                     NoPlayer; consistent across all 10 vanilla
        //                     shanties as of Windrose 5.6). 1.0 = parity
        //                     with vanilla; 0.8 = "added track default"
        //                     (a touch quieter than vanilla so new
        //                     uploads don't surprise the listener); > 1.0
        //                     louder. Clamped to [0.01, 2.0].
        public ShipMusicAddCueCloneResult Clone(
            string inputUassetPath, string outputUassetPath, string usmapPath,
            string flavor, string newIndex, string newSwavStem,
            float audioDurationSec, double userVolumeMultiplier = 1.0)
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
            //
            // Volume multiplier piggybacks on the same pass: while we're
            // touching cueExp.Data anyway, scale the existing
            // VolumeMultiplier by the user-supplied factor.
            double clampedVol = userVolumeMultiplier;
            if (clampedVol < 0.01) clampedVol = 0.01;
            if (clampedVol > 2.0) clampedVol = 2.0;
            ReshapeToMinimal(asset, newSwav, audioDurationSec, clampedVol);

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

        // Reduces the vanilla cue graph IN-PLACE so the user's audio plays
        // back-to-back without the vanilla 15-way Random / ShipsChatter
        // overlay / inter-shanty Delays. We deliberately KEEP the canonical
        // routing stack (Random -> Mixer -> Delay -> WavePlayer) intact -
        // an earlier version of this method bypassed FirstNode directly to
        // a leaf SoundNodeWavePlayer, which produced silence ingame. The
        // engine seems to require the full Cue node hierarchy (or at least
        // a Mixer/Random/Attenuation wrap) for the SoundClass /
        // AttenuationSettings routing to kick in; a raw WavePlayer-as-root
        // gets loaded but never reaches the Music submix.
        //
        // The reduction shrinks all branching arrays to a single element
        // (Random.ChildNodes 15 -> 1, Random.Weights 15 -> 1, first
        // Mixer.ChildNodes 2 -> 1 dropping the ShipsChatter overlay,
        // first Mixer.InputVolume 2 -> 1), zeros the first Delay's
        // DelayMin/DelayMax so the user track starts immediately, and
        // shrinks SoundCue.Duration to (audioDurationSec + 0.5s pad) so
        // UR5ShipAudioComponent picks the next shanty right after the
        // user audio finishes instead of after the vanilla 163s timeout.
        //
        // Orphaned exports (14 unused Mixers, 14 unused Delays, all
        // ShipsChatter WavePlayers, all but one MaggieMay WavePlayer)
        // stay in the file - removing them would require renumbering
        // every FPackageIndex in the asset, which UAssetAPI can't do
        // cheaply, and the cost in disk space is < 5 KB per cue.
        void ReshapeToMinimal(UAsset asset, string newSwavStem, float audioDurationSec,
            double userVolumeMultiplier)
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

            // Pad the duration a little so the audio fade-out has room
            // before the engine considers the cue spent. 0.5s matches the
            // typical SoundConcurrency release time and is short enough
            // to feel "back-to-back" between tracks.
            const float DurationPadSec = 0.5f;
            float newDuration = audioDurationSec + DurationPadSec;

            // Step 1: SoundCue.Duration + bHasDelayNode + VolumeMultiplier
            // (we leave FirstNode unchanged - it still points at the
            // Random root). VolumeMultiplier is the vanilla 0.45 (voice)
            // or 0.5 (NoPlayer) from the source cue; we multiply by the
            // user factor (already clamped in the caller).
            FloatPropertyData durProp = null;
            ObjectPropertyData firstNodeProp = null;
            BoolPropertyData delayFlagProp = null;
            FloatPropertyData volProp = null;
            foreach (var p in cueExp.Data)
            {
                var n = p?.Name?.Value?.Value;
                if (n == "Duration" && p is FloatPropertyData fp) durProp = fp;
                else if (n == "FirstNode" && p is ObjectPropertyData fop) firstNodeProp = fop;
                else if (n == "bHasDelayNode" && p is BoolPropertyData bp) delayFlagProp = bp;
                else if (n == "VolumeMultiplier" && p is FloatPropertyData vp) volProp = vp;
            }
            if (firstNodeProp == null || firstNodeProp.Value == null || !firstNodeProp.Value.IsExport())
                throw new InvalidOperationException(
                    "SoundCue.FirstNode missing or not an export reference - vanilla cue "
                    + "template layout may have drifted.");
            if (durProp == null)
                throw new InvalidOperationException(
                    "SoundCue.Duration property missing - vanilla cue template layout "
                    + "may have drifted.");

            LogLine("  Duration: " + durProp.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)
                + " -> " + newDuration.ToString(System.Globalization.CultureInfo.InvariantCulture)
                + " (user audio " + audioDurationSec.ToString(System.Globalization.CultureInfo.InvariantCulture)
                + "s + " + DurationPadSec.ToString(System.Globalization.CultureInfo.InvariantCulture) + "s pad)");
            durProp.Value = newDuration;
            // bHasDelayNode stays true - we keep one (zero-length) Delay
            // node in the subtree, so the flag still matches reality.
            if (delayFlagProp != null)
                LogLine("  bHasDelayNode: " + delayFlagProp.Value + " (unchanged, zero-length Delay still present)");

            // VolumeMultiplier: existing value comes from the vanilla
            // template (0.45 / 0.5 depending on flavor). Multiply by user
            // factor and write back. Missing property is a hard error -
            // means vanilla cue layout drifted and the build would be
            // silently wrong (user expects 80%, gets 100%).
            if (volProp == null)
                throw new InvalidOperationException(
                    "SoundCue.VolumeMultiplier property missing - vanilla cue template "
                    + "layout may have drifted.");
            float oldVol = volProp.Value;
            float newVol = (float)(oldVol * userVolumeMultiplier);
            volProp.Value = newVol;
            LogLine("  VolumeMultiplier: " + oldVol.ToString(System.Globalization.CultureInfo.InvariantCulture)
                + " * " + userVolumeMultiplier.ToString(System.Globalization.CultureInfo.InvariantCulture)
                + " -> " + newVol.ToString(System.Globalization.CultureInfo.InvariantCulture));

            // Step 2: Random -> reduce ChildNodes + Weights to 1 entry.
            int randomIdx0 = firstNodeProp.Value.Index - 1;
            var randomExp = asset.Exports[randomIdx0] as NormalExport;
            string randomClass = ImportClassName(asset, randomExp);
            if (randomClass != "SoundNodeRandom")
                throw new InvalidOperationException(
                    "FirstNode -> Export[" + randomIdx0 + "] class is '" + randomClass
                    + "', expected 'SoundNodeRandom' - vanilla cue template layout drifted.");

            int firstMixerExportIdx0 = ReduceArrayToFirst(randomExp, "ChildNodes", out var firstChildPi);
            ReduceArrayToFirst(randomExp, "Weights", out _);
            LogLine("  Random[" + randomIdx0 + "].ChildNodes -> 1, .Weights -> 1");
            if (firstChildPi == null || !firstChildPi.IsExport())
                throw new InvalidOperationException(
                    "Random.ChildNodes[0] is not an export ref - vanilla cue layout drifted.");

            // Step 3: Mixer -> reduce ChildNodes + InputVolume to 1 entry
            // (drops the ShipsChatter overlay sibling).
            int mixerIdx0 = firstChildPi.Index - 1;
            var mixerExp = asset.Exports[mixerIdx0] as NormalExport;
            string mixerClass = ImportClassName(asset, mixerExp);
            if (mixerClass != "SoundNodeMixer")
                throw new InvalidOperationException(
                    "Random.ChildNodes[0] -> Export[" + mixerIdx0 + "] class is '" + mixerClass
                    + "', expected 'SoundNodeMixer' - vanilla cue template layout drifted.");

            int firstMixerChildIdx0 = ReduceArrayToFirst(mixerExp, "ChildNodes", out var firstMixerChildPi);
            ReduceArrayToFirst(mixerExp, "InputVolume", out _);
            LogLine("  Mixer[" + mixerIdx0 + "].ChildNodes -> 1 (dropped ShipsChatter sibling), .InputVolume -> 1");
            if (firstMixerChildPi == null || !firstMixerChildPi.IsExport())
                throw new InvalidOperationException(
                    "Mixer.ChildNodes[0] is not an export ref - vanilla cue layout drifted.");

            // Step 4: Delay -> zero DelayMin/DelayMax so the audio starts
            // right when the cue plays.
            int delayIdx0 = firstMixerChildPi.Index - 1;
            var delayExp = asset.Exports[delayIdx0] as NormalExport;
            string delayClass = ImportClassName(asset, delayExp);
            if (delayClass != "SoundNodeDelay")
                throw new InvalidOperationException(
                    "Mixer.ChildNodes[0] -> Export[" + delayIdx0 + "] class is '" + delayClass
                    + "', expected 'SoundNodeDelay' - vanilla cue template layout drifted.");

            int delayTouched = 0;
            foreach (var p in delayExp.Data)
            {
                var n = p?.Name?.Value?.Value;
                if ((n == "DelayMin" || n == "DelayMax") && p is FloatPropertyData fp)
                {
                    LogLine("  Delay[" + delayIdx0 + "]." + n + ": "
                        + fp.Value.ToString(System.Globalization.CultureInfo.InvariantCulture) + " -> 0");
                    fp.Value = 0f;
                    delayTouched++;
                }
            }
            if (delayTouched != 2)
                throw new InvalidOperationException(
                    "Delay surgery touched " + delayTouched + "/2 expected properties "
                    + "(DelayMin, DelayMax) - vanilla cue template layout drifted.");

            // Step 5: Sanity that the surviving WavePlayer (Delay.ChildNodes[0])
            // really targets our renamed SWAV. If the NameMap rewrite missed,
            // we'd hear vanilla MaggieMay or silence; fail loud here.
            FPackageIndex wavePi = null;
            foreach (var p in delayExp.Data)
            {
                if (p?.Name?.Value?.Value == "ChildNodes" && p is ArrayPropertyData ap
                    && ap.Value.Length > 0 && ap.Value[0] is ObjectPropertyData op2)
                {
                    wavePi = op2.Value;
                    break;
                }
            }
            if (wavePi == null || !wavePi.IsExport())
                throw new InvalidOperationException(
                    "Delay.ChildNodes[0] is missing or not an export ref - vanilla cue layout drifted.");
            int waveIdx0 = wavePi.Index - 1;
            var waveExp = asset.Exports[waveIdx0] as NormalExport;
            string waveClass = ImportClassName(asset, waveExp);
            if (waveClass != "SoundNodeWavePlayer")
                throw new InvalidOperationException(
                    "Delay.ChildNodes[0] -> Export[" + waveIdx0 + "] class is '" + waveClass
                    + "', expected 'SoundNodeWavePlayer' - vanilla cue template layout drifted.");
            string boundAsset = "<none>";
            foreach (var p in waveExp.Data)
            {
                if (p is SoftObjectPropertyData so
                    && so.Name?.Value?.Value == "SoundWaveAssetPtr")
                {
                    boundAsset = so.Value.AssetPath.AssetName?.Value?.Value ?? "<null>";
                    break;
                }
            }
            if (boundAsset != newSwavStem)
                throw new InvalidOperationException(
                    "Surviving WavePlayer Export[" + waveIdx0 + "] is bound to '"
                    + boundAsset + "' but the cue was supposed to target '" + newSwavStem
                    + "' - the NameMap rewrite missed a SoundWaveAssetPtr, or the vanilla "
                    + "graph's first Random branch points at ShipsChatter instead of Shanti.");
            LogLine("  WavePlayer[" + waveIdx0 + "].SoundWaveAssetPtr -> '" + boundAsset + "' (matches)");
        }

        // Looks up the class name (FName from Imports) for a NormalExport.
        // Returns "?" when the class is itself an export (rare in cues).
        static string ImportClassName(UAsset asset, NormalExport e)
        {
            if (e == null) return "?";
            var ci = e.ClassIndex;
            if (ci == null || ci.Index == 0) return "?";
            if (ci.IsImport())
                return asset.Imports[ci.Index * -1 - 1].ObjectName.Value.Value;
            return "?";
        }

        // Shrinks the named ArrayProperty on the export to a single entry
        // (the first one). Returns the original first-element index for the
        // caller's benefit, plus the first child's FPackageIndex (when the
        // array carries object references).
        int ReduceArrayToFirst(NormalExport exp, string arrayPropName, out FPackageIndex firstObjPi)
        {
            firstObjPi = null;
            foreach (var p in exp.Data)
            {
                if (p?.Name?.Value?.Value == arrayPropName && p is ArrayPropertyData ap)
                {
                    if (ap.Value.Length == 0)
                        throw new InvalidOperationException(
                            "Array property '" + arrayPropName + "' on export '"
                            + exp.ObjectName.Value.Value + "' is empty - cannot reduce.");
                    if (ap.Value[0] is ObjectPropertyData op)
                        firstObjPi = op.Value;
                    ap.Value = new[] { ap.Value[0] };
                    return 0;
                }
            }
            throw new InvalidOperationException(
                "Array property '" + arrayPropName + "' not found on export '"
                + exp.ObjectName.Value.Value + "' - vanilla cue layout drifted.");
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
