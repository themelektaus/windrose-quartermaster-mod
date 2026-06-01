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
    public sealed class ShipMusicAddCueCloner
    {
        public Action<string> Log;


        public const string TemplateCueIndex = "10";
        public const string TemplateSwavStem = "SWAV_Shanti_MaggieMay";
        public const string TemplateSwavPath =
            "/Game/Audio/Game/Music/Shanti/SWAV/SWAV_Shanti_MaggieMay";

        public const string CueRelDirLarge   = "R5/Content/Audio/Game/Music/Shanti/Ships/Large";
        public const string CueRelDirMedium  = "R5/Content/Audio/Game/Music/Shanti/Ships/Medium";
        public const string CueRelDirSmall   = "R5/Content/Audio/Game/Music/Shanti/Ships/Small";
        public const string CueRelDirNoPlayer= "R5/Content/Audio/Game/Music/Shanti/VoiceNoPlayer";

        public ShipMusicAddCueCloneResult Clone(
            string inputUassetPath, string outputUassetPath, string usmapPath,
            string flavor, string newIndex, string newSwavStem,
            float audioDurationSec, double userVolumeAbsolute = 0.45)
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
            var asset = new UAsset(inputUassetPath, UAssetIo.Ue, mappings);

            int hits = ApplyRules(asset, rules);
            if (hits != rules.Count)
            {
                throw new InvalidOperationException(
                    "Cue NameMap rename incomplete - matched " + hits + "/" + rules.Count
                    + " expected entries. Did the source asset stem drift away from "
                    + "CUE_Shanti_" + TemplateCueIndex + " / " + TemplateSwavStem + "? "
                    + "Vanilla asset: " + inputUassetPath);
            }

            // FolderName lives outside the NameMap; without this the clone
            // self-identifies as the vanilla package and import resolution fails.
            var oldFolderName = asset.FolderName?.Value;
            LogLine("  FolderName: " + (oldFolderName ?? "<null>") + " -> " + newSelfPath);
            asset.FolderName = FString.FromString(newSelfPath);

            double clampedVol = userVolumeAbsolute;
            if (clampedVol < 0.0) clampedVol = 0.0;
            if (clampedVol > 1.0) clampedVol = 1.0;
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

        public static string VanillaCueStem(string flavor)
        {
            if (flavor == "NoPlayer") return "CUE_Shanti_" + TemplateCueIndex + "_VoiceNoPlayer";
            return "CUE_Shanti_" + TemplateCueIndex + "_" + flavor + "_VoicePlayer";
        }

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

        public static string SelfStem(string flavor, string newIndex)
        {
            if (flavor == "NoPlayer") return "CUE_Shanti_" + newIndex + "_VoiceNoPlayer";
            return "CUE_Shanti_" + newIndex + "_" + flavor + "_VoicePlayer";
        }

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

        // Must keep the Random -> Mixer -> Delay -> WavePlayer routing stack:
        // bypassing FirstNode straight to a leaf WavePlayer plays silent ingame.
        void ReshapeToMinimal(UAsset asset, string newSwavStem, float audioDurationSec,
            double userVolumeAbsolute)
        {
            if (asset.Exports.Count == 0)
                throw new InvalidOperationException("Cue asset has no exports");
            var cueExp = asset.Exports[0] as NormalExport;
            if (cueExp == null)
                throw new InvalidOperationException("Export[0] is not a NormalExport");

            var ci = cueExp.ClassIndex;
            var className = "?";
            if (ci.IsImport())
                className = asset.Imports[ci.Index * -1 - 1].ObjectName.Value.Value;
            if (className != "SoundCue")
                throw new InvalidOperationException(
                    "Export[0] class is '" + className + "', expected 'SoundCue' - "
                    + "the vanilla cue template layout drifted, the minimal-cue surgery "
                    + "needs the SoundCue at Export[0].");

            const float DurationPadSec = 0.5f;
            float newDuration = audioDurationSec + DurationPadSec;

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
            if (delayFlagProp != null)
                LogLine("  bHasDelayNode: " + delayFlagProp.Value + " (unchanged, zero-length Delay still present)");

            if (volProp == null)
                throw new InvalidOperationException(
                    "SoundCue.VolumeMultiplier property missing - vanilla cue template "
                    + "layout may have drifted.");
            float oldVol = volProp.Value;
            float newVol = (float)userVolumeAbsolute;
            volProp.Value = newVol;
            LogLine("  VolumeMultiplier: " + oldVol.ToString(System.Globalization.CultureInfo.InvariantCulture)
                + " -> " + newVol.ToString(System.Globalization.CultureInfo.InvariantCulture)
                + " (absolute, user slider value)");

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

        static string ImportClassName(UAsset asset, NormalExport e)
        {
            if (e == null) return "?";
            var ci = e.ClassIndex;
            if (ci == null || ci.Index == 0) return "?";
            if (ci.IsImport())
                return asset.Imports[ci.Index * -1 - 1].ObjectName.Value.Value;
            return "?";
        }

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
        public string NewCueStem;
        public string NewSwavStem;
        public string OutputUassetPath;
        public string OutputUexpPath;
        public int NameMapHits;
    }
}
