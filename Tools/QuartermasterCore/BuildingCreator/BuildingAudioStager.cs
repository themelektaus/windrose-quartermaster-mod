using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using UAssetAPI;
using UAssetAPI.UnrealTypes;
using UAssetAPI.ExportTypes;
using UAssetAPI.PropertyTypes.Objects;
using UAssetAPI.Unversioned;

namespace Windrose.Quartermaster.Core.BuildingCreator
{
    // Phase B of the Audio component preset: stages a per-building
    // SoundWave (SWAV) + looping SoundCue under the mod's output namespace
    // so the cloned PendulumClock BP can play USER-supplied audio instead
    // of the vanilla MS_Building_Clock_LP Tick-Tack loop.
    //
    // The flow per building:
    //
    //   1. User WAV (already preprocessed to 44.1 kHz / stereo / 16-bit
    //      PCM by AudioPreprocessor) is encoded to Bink Audio via the in-
    //      tree binkaudioenc.exe and spliced into the SoundWave_BinkInline
    //      template, producing SWAV_QmBldgAudio_<buildingId>.uasset/uexp
    //      at FolderName /Game/Quartermaster/SWAV_QmBldgAudio_<buildingId>.
    //
    //   2. Vanilla CUE_Shanti_10_VoiceNoPlayer (the smallest of the four
    //      vanilla cue 10 variants - we own the extract recipe via
    //      ShipMusicAddCueCloner.CueRelDirNoPlayer) is cloned and reshaped
    //      to a minimal one-leaf graph (Random -> Mixer -> Delay ->
    //      WavePlayer with bLooping=true) pointing at our SWAV. Written as
    //      CUE_QmBldgAudio_<buildingId>.uasset/uexp at FolderName
    //      /Game/Quartermaster/CUE_QmBldgAudio_<buildingId>.
    //
    //   3. The cloned BP_QmAudio_<buildingId> needs to have its
    //      AudioComponent.Sound import retargeted from MS_Building_Clock_LP
    //      (MetaSoundSource) to our CUE_QmBldgAudio (SoundCue). The class
    //      + class-package FName entries also flip from MetaSoundSource /
    //      /Script/MetasoundEngine to SoundCue / /Script/Engine. All four
    //      rewrites happen via the BP's NameMap which BlueprintPatcher
    //      already opens during Stage() - this stager EXPOSES the rewrite
    //      set so the BlueprintPatcher can apply it in the same pass.
    //
    // We deliberately don't add an AttenuationSettings property to the
    // AudioComponent: setting bOverrideAttenuation+AttenuationOverrides on
    // an existing component export needs the FSoundAttenuationSettings
    // unversioned-property layout which is large (>30 fields) and not
    // worth the maintenance. Instead, the per-building range is encoded
    // on the SoundCue itself by setting bOverrideAttenuation=true +
    // AttenuationOverrides.FalloffDistance=<range_cm> at the cue level
    // (TODO once recon validates that path).
    public sealed class BuildingAudioStager
    {
        public Action<string> Log;

        // External tooling - the orchestrator wires these once per build.
        public string BinkEncoderPath;       // Tools/binkaudioenc.exe
        public string SwavTemplateUasset;    // Tools/Templates/SoundWave_BinkInline.uasset
        public string SwavTemplateUexp;
        public string UsmapPath;
        public string RetocExe;
        public string VanillaPaksDir;
        public string AesKey;
        public string TempDir;

        const EngineVersion Ue = EngineVersion.VER_UE5_6;

        // The vanilla NameMap entries on the PendulumClock BP that point
        // at MS_Building_Clock_LP. Verified via clock-bp-full.txt recon
        // dump (.build-tmp/audio-recon/).
        public const string VanillaSoundStem      = "MS_Building_Clock_LP";
        public const string VanillaSoundPath      = "/Game/Audio/Game/Building/Loops/MS_Building_Clock_LP";
        public const string VanillaSoundClassName = "MetaSoundSource";
        public const string VanillaSoundClassPkg  = "/Script/MetasoundEngine";
        public const string TargetSoundClassName  = "SoundCue";
        public const string TargetSoundClassPkg   = "/Script/Engine";

        // Stages the user WAV as a per-building SWAV + looping Cue, returns
        // the resulting refs the BlueprintPatcher needs to rewire the BP's
        // AudioComponent.Sound.
        public BuildingAudioStageResult Stage(
            string buildingId, string userWavPath, string stagingItemsDir)
        {
            if (string.IsNullOrEmpty(buildingId)) throw new ArgumentNullException("buildingId");
            if (string.IsNullOrEmpty(userWavPath)) throw new ArgumentNullException("userWavPath");
            if (!File.Exists(userWavPath))
                throw new FileNotFoundException("User WAV not found: " + userWavPath);
            EnsureTooling();
            Directory.CreateDirectory(stagingItemsDir);

            var swavStem = "SWAV_QmBldgAudio_" + buildingId;
            var cueStem  = "CUE_QmBldgAudio_"  + buildingId;
            // FolderName matches BuildingPatcher.NormalizeAssetSelfPath's
            // convention - all mod assets sit at /Game/Quartermaster/<stem>
            // regardless of which stagingItemsDir subfolder hosts them.
            var swavPackagePath = WindrosePaths.ModItemsPackagePath + swavStem;
            var cuePackagePath  = WindrosePaths.ModItemsPackagePath + cueStem;

            LogLine("=== [Audio:" + buildingId + "] staging SWAV+Cue ===");

            // 1. SWAV: Bink-encode + template splice + FolderName rewrite.
            //    We reuse the SoundWave_BinkInline template logic from
            //    ShipMusicPatcher inline - we don't want to depend on
            //    ShipMusicSlots.SlotInfo here because that's tied to the
            //    vanilla shanty roster.
            var swavUassetOut = Path.Combine(stagingItemsDir, swavStem + ".uasset");
            var swavUexpOut   = Path.Combine(stagingItemsDir, swavStem + ".uexp");
            var swavInfo = StageSwav(userWavPath, swavStem, swavPackagePath, swavUassetOut, swavUexpOut);

            // 2. SoundCue clone. Extract vanilla CUE_Shanti_10_VoiceNoPlayer
            //    (smallest cue 10 variant). Reshape minimal-cue with the
            //    SWAV as the WavePlayer source. Looping handled by setting
            //    bLooping=true on the surviving WavePlayer.
            var cueUassetOut = Path.Combine(stagingItemsDir, cueStem + ".uasset");
            StageCue(swavStem, swavPackagePath, cueStem, cuePackagePath,
                cueUassetOut, swavInfo.DurationSeconds);

            return new BuildingAudioStageResult
            {
                BuildingId        = buildingId,
                SwavStem          = swavStem,
                SwavPackagePath   = swavPackagePath,
                CueStem           = cueStem,
                CuePackagePath    = cuePackagePath,
                DurationSeconds   = swavInfo.DurationSeconds,
                StagedSwavUasset  = swavUassetOut,
                StagedSwavUexp    = swavUexpOut,
                StagedCueUasset   = cueUassetOut,
            };
        }

        // Returns the NameMap rewrite dictionary the BlueprintPatcher
        // should apply when staging the cloned BP. Four entries:
        //   - vanilla sound stem -> our cue stem
        //   - vanilla sound package path -> our cue package path
        //   - MetaSoundSource -> SoundCue (the Import.ClassName)
        //   - /Script/MetasoundEngine -> /Script/Engine (Import.ClassPackage)
        public static Dictionary<string, string> NameMapRewritesForBp(BuildingAudioStageResult stage)
        {
            if (stage == null) throw new ArgumentNullException("stage");
            return new Dictionary<string, string>(StringComparer.Ordinal)
            {
                { VanillaSoundStem,      stage.CueStem },
                { VanillaSoundPath,      stage.CuePackagePath },
                { VanillaSoundClassName, TargetSoundClassName },
                { VanillaSoundClassPkg,  TargetSoundClassPkg },
            };
        }

        sealed class SwavBuildInfo
        {
            public float DurationSeconds;
            public int SampleRate;
            public int Channels;
        }

        // Mirror of ShipMusicPatcher.PatchFromWav but with explicit
        // FolderName / stem / output paths so the SWAV lands at our
        // /Game/Quartermaster/<stem> namespace instead of the vanilla
        // shanty content tree.
        SwavBuildInfo StageSwav(string userWavPath, string swavStem, string swavPackagePath,
            string outUasset, string outUexp)
        {
            // Read WAV (enforces 44.1k stereo 16-bit PCM up the call chain).
            var wav = WavInfo.Read(userWavPath);
            LogLine("  WAV " + Path.GetFileName(userWavPath) + ": " + wav.Describe());
            if (wav.SampleRate != 44100 || wav.Channels != 2 || wav.BitsPerSample != 16)
                throw new InvalidOperationException(
                    "User WAV must be 44.1 kHz / Stereo / 16-bit PCM (got "
                    + wav.Describe() + ") - the audio preprocessor should have "
                    + "conditioned this already.");

            // Bink encode.
            var encoder = new BinkAudioEncoder(BinkEncoderPath) { Log = Log };
            var binkBuf = encoder.Encode(userWavPath);
            LogLine("  Bink encoded: " + binkBuf.Length + " bytes");

            // Patch template props (Duration + TotalSamples).
            var templateUexp = File.ReadAllBytes(SwavTemplateUexp);
            if (templateUexp.Length != ShipMusicPatcher.TemplateUexpSize)
                throw new InvalidOperationException("SoundWave_BinkInline.uexp size mismatch ("
                    + templateUexp.Length + " vs " + ShipMusicPatcher.TemplateUexpSize + ")");

            var props = new byte[ShipMusicPatcher.TemplatePropsSize];
            Buffer.BlockCopy(templateUexp, 0, props, 0, ShipMusicPatcher.TemplatePropsSize);
            WriteFloatLE(props, ShipMusicPatcher.OffsetDuration, wav.DurationSeconds);
            WriteFloatLE(props, ShipMusicPatcher.OffsetTotalSamples, (float)wav.SampleCount);

            var footer = new byte[ShipMusicPatcher.TemplateFooterSize];
            Buffer.BlockCopy(templateUexp,
                templateUexp.Length - ShipMusicPatcher.TemplateFooterSize,
                footer, 0, ShipMusicPatcher.TemplateFooterSize);

            var newUexp = new byte[props.Length + binkBuf.Length + footer.Length];
            int p = 0;
            Buffer.BlockCopy(props, 0, newUexp, p, props.Length); p += props.Length;
            Buffer.BlockCopy(binkBuf, 0, newUexp, p, binkBuf.Length); p += binkBuf.Length;
            Buffer.BlockCopy(footer, 0, newUexp, p, footer.Length);

            // Copy template asset+uexp to destination, then UAssetAPI
            // rewrite to rename "Empty" -> our stem + set FolderName.
            File.Copy(SwavTemplateUasset, outUasset, overwrite: true);
            File.Copy(SwavTemplateUexp,   outUexp,   overwrite: true);
            var mappings = new Usmap(UsmapPath);
            var asset = new UAsset(outUasset, Ue, mappings);

            int renamed = 0;
            var names = asset.GetNameMapIndexList();
            for (int i = 0; i < names.Count; i++)
            {
                var entry = names[i];
                if (entry == null) continue;
                if (string.Equals(entry.Value, ShipMusicPatcher.TemplateAssetStem, StringComparison.Ordinal))
                {
                    asset.SetNameReference(i, new FString(swavStem, entry.Encoding));
                    renamed++;
                }
                else if (string.Equals(entry.Value, "/Game/" + ShipMusicPatcher.TemplateAssetStem, StringComparison.Ordinal))
                {
                    asset.SetNameReference(i, new FString(swavPackagePath, entry.Encoding));
                    renamed++;
                }
            }
            asset.FolderName = FString.FromString(swavPackagePath);

            // Retarget any export ObjectName still referring to "Empty".
            int retargetedExports = 0;
            for (int i = 0; i < asset.Exports.Count; i++)
            {
                if (asset.Exports[i] is NormalExport ne)
                {
                    var on = ne.ObjectName;
                    if (on != null && on.Value != null
                        && string.Equals(on.Value.Value, ShipMusicPatcher.TemplateAssetStem, StringComparison.Ordinal))
                    {
                        ne.ObjectName = FName.FromString(asset, swavStem);
                        retargetedExports++;
                    }
                }
            }
            if (renamed == 0 && retargetedExports == 0)
                throw new InvalidOperationException("SWAV template has no 'Empty' to rename - template drift");

            // Patch DataResources to match new bink size.
            var dataResources = asset.DataResources;
            int dataResourcesPatched = 0;
            if (dataResources != null)
            {
                for (int i = 0; i < dataResources.Count; i++)
                {
                    var r = dataResources[i];
                    if (r.SerialSize == ShipMusicPatcher.TemplateBinkSize && r.RawSize == ShipMusicPatcher.TemplateBinkSize)
                    {
                        r.SerialSize = binkBuf.Length;
                        r.RawSize    = binkBuf.Length;
                        dataResources[i] = r;
                        dataResourcesPatched++;
                    }
                }
            }

            asset.Write(outUasset);
            long uassetApiUexpSize = new FileInfo(outUexp).Length;
            File.WriteAllBytes(outUexp, newUexp);

            // SerialSize patch in .uasset for the new uexp size.
            long oldSerialSize = uassetApiUexpSize - 4;
            long newSerialSize = (long)newUexp.Length - 4;
            PatchSerialSizeInUasset(outUasset, oldSerialSize, newSerialSize);
            LogLine("  SWAV staged: " + swavStem + " (Duration=" + wav.DurationSeconds.ToString("0.##",
                System.Globalization.CultureInfo.InvariantCulture) + "s, bink=" + binkBuf.Length + " B, "
                + renamed + " NameMap renames, " + dataResourcesPatched + " DataResource patches)");

            return new SwavBuildInfo
            {
                DurationSeconds = wav.DurationSeconds,
                SampleRate = wav.SampleRate,
                Channels = wav.Channels,
            };
        }

        // Extract vanilla CUE_Shanti_10_VoiceNoPlayer, then run a
        // ShipMusicAddCueCloner-style reshape on it that:
        //   - Renames the cue self (stem + folder path) to our cue
        //   - Renames the SWAV ref (SWAV_Shanti_MaggieMay + its path) to
        //     our staged SWAV stem + path
        //   - Sets the surviving WavePlayer's bLooping property to true
        //   - Picks a Duration big enough to NOT trigger cue completion
        //     for a typical loop session (10 minutes - the BP's
        //     AudioComponent never restarts, it just plays our looped cue
        //     until the player walks away).
        void StageCue(
            string swavStem, string swavPackagePath,
            string cueStem,  string cuePackagePath,
            string outUasset, float audioDurationSec)
        {
            var perBuildingTemp = Path.Combine(TempDir ?? Path.GetTempPath(),
                "qm-bldgaudio-cue-" + cueStem);
            if (Directory.Exists(perBuildingTemp)) Directory.Delete(perBuildingTemp, true);
            Directory.CreateDirectory(perBuildingTemp);

            // Extract CUE_Shanti_10_VoiceNoPlayer via retoc.
            const string vanillaCueStem = "CUE_Shanti_10_VoiceNoPlayer";
            var legacyPath = ExtractVanilla(vanillaCueStem, perBuildingTemp);

            LogLine("  Cloning cue " + vanillaCueStem + " -> " + cueStem);
            var mappings = new Usmap(UsmapPath);
            var asset = new UAsset(legacyPath, Ue, mappings);

            // NameMap rewrites - 4 entries:
            //   CUE_Shanti_10_VoiceNoPlayer -> CUE_QmBldgAudio_<id>
            //   /Game/.../CUE_Shanti_10_VoiceNoPlayer -> /Game/Quartermaster/CUE_QmBldgAudio_<id>
            //   SWAV_Shanti_MaggieMay -> SWAV_QmBldgAudio_<id>
            //   /Game/.../SWAV_Shanti_MaggieMay -> /Game/Quartermaster/SWAV_QmBldgAudio_<id>
            var rewrites = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                { vanillaCueStem,
                  cueStem },
                { "/Game/Audio/Game/Music/Shanti/VoiceNoPlayer/" + vanillaCueStem,
                  cuePackagePath },
                { ShipMusicAddCueCloner.TemplateSwavStem,
                  swavStem },
                { ShipMusicAddCueCloner.TemplateSwavPath,
                  swavPackagePath },
            };
            int hits = 0;
            var names = asset.GetNameMapIndexList();
            for (int i = 0; i < names.Count; i++)
            {
                var current = names[i].Value;
                if (rewrites.TryGetValue(current, out var replacement))
                {
                    asset.SetNameReference(i, FString.FromString(replacement));
                    hits++;
                }
            }
            if (hits != rewrites.Count)
                throw new InvalidOperationException(
                    "Cue NameMap rewrite incomplete (" + hits + "/" + rewrites.Count
                    + ") - vanilla CUE_Shanti_10_VoiceNoPlayer drifted?");
            asset.FolderName = FString.FromString(cuePackagePath);

            // Reshape minimal cue graph + set bLooping=true on the surviving
            // WavePlayer + bump Duration so the cue stays alive long enough
            // to loop multiple times (10 minutes = 600s).
            ReshapeMinimalAndLoop(asset, swavStem, /*durationSec*/ 600f);

            asset.Write(outUasset);
            LogLine("  Cue staged: " + cueStem + " (" + hits + " NameMap rewrites)");
        }

        // Reshape the cue's graph to a single-leaf:
        //   Random -> Mixer (one child only) -> Delay (DelayMin=Max=0) ->
        //   WavePlayer (bLooping=true, SoundWaveAssetPtr -> our SWAV)
        //
        // Mirrors ShipMusicAddCueCloner.ReshapeToMinimal with two diffs:
        //   - sets bLooping=true on the WavePlayer
        //   - takes a huge Duration so the cue never times out during loop
        void ReshapeMinimalAndLoop(UAsset asset, string swavStem, float durationSec)
        {
            if (asset.Exports.Count == 0)
                throw new InvalidOperationException("Cue asset has no exports");
            var cueExp = asset.Exports[0] as NormalExport;
            if (cueExp == null)
                throw new InvalidOperationException("Cue Export[0] is not a NormalExport");

            FloatPropertyData durProp = null;
            ObjectPropertyData firstNodeProp = null;
            foreach (var p in cueExp.Data)
            {
                var n = p?.Name?.Value?.Value;
                if (n == "Duration" && p is FloatPropertyData fp) durProp = fp;
                else if (n == "FirstNode" && p is ObjectPropertyData fop) firstNodeProp = fop;
            }
            if (firstNodeProp == null || firstNodeProp.Value == null || !firstNodeProp.Value.IsExport())
                throw new InvalidOperationException("SoundCue.FirstNode missing");
            if (durProp != null) durProp.Value = durationSec;

            // Random: reduce ChildNodes + Weights to 1 entry.
            int randomIdx0 = firstNodeProp.Value.Index - 1;
            var randomExp = asset.Exports[randomIdx0] as NormalExport;
            ReduceArrayToFirst(randomExp, "ChildNodes", out var firstChildPi);
            ReduceArrayToFirst(randomExp, "Weights", out _);
            if (firstChildPi == null || !firstChildPi.IsExport())
                throw new InvalidOperationException("Random.ChildNodes[0] not an export ref");

            int mixerIdx0 = firstChildPi.Index - 1;
            var mixerExp = asset.Exports[mixerIdx0] as NormalExport;
            ReduceArrayToFirst(mixerExp, "ChildNodes", out var firstMixerChildPi);
            ReduceArrayToFirst(mixerExp, "InputVolume", out _);

            int delayIdx0 = firstMixerChildPi.Index - 1;
            var delayExp = asset.Exports[delayIdx0] as NormalExport;
            foreach (var p in delayExp.Data)
            {
                var n = p?.Name?.Value?.Value;
                if ((n == "DelayMin" || n == "DelayMax") && p is FloatPropertyData fp)
                    fp.Value = 0f;
            }

            FPackageIndex wavePi = null;
            foreach (var p in delayExp.Data)
            {
                if (p?.Name?.Value?.Value == "ChildNodes" && p is ArrayPropertyData ap
                    && ap.Value.Length > 0 && ap.Value[0] is ObjectPropertyData op)
                {
                    wavePi = op.Value;
                    break;
                }
            }
            if (wavePi == null || !wavePi.IsExport())
                throw new InvalidOperationException("Delay.ChildNodes[0] not an export ref");
            int waveIdx0 = wavePi.Index - 1;
            var waveExp = asset.Exports[waveIdx0] as NormalExport;

            // Verify the WavePlayer is bound to our renamed SWAV (NameMap
            // rewrite should have hit it). Defensive check.
            string boundAsset = "<none>";
            foreach (var p in waveExp.Data)
            {
                if (p is SoftObjectPropertyData so && so.Name?.Value?.Value == "SoundWaveAssetPtr")
                {
                    boundAsset = so.Value.AssetPath.AssetName?.Value?.Value ?? "<null>";
                    break;
                }
            }
            if (boundAsset != swavStem)
                throw new InvalidOperationException(
                    "WavePlayer SoundWaveAssetPtr is '" + boundAsset
                    + "' but expected '" + swavStem + "' - SWAV NameMap rewrite missed");

            // Set bLooping=true on the WavePlayer. The vanilla CUE_Shanti_10
            // has bLooping=false (shanties play once); we flip it to true.
            // If the property is absent (default-false), add it.
            BoolPropertyData loopProp = null;
            foreach (var p in waveExp.Data)
            {
                if (p?.Name?.Value?.Value == "bLooping" && p is BoolPropertyData bp)
                {
                    loopProp = bp;
                    break;
                }
            }
            if (loopProp != null)
            {
                loopProp.Value = true;
            }
            else
            {
                var newLoop = new BoolPropertyData(FName.FromString(asset, "bLooping"))
                {
                    Value = true,
                };
                waveExp.Data.Add(newLoop);
            }
            LogLine("  WavePlayer[" + waveIdx0 + "].bLooping=true (loop enabled)");
        }

        // Shrinks the named ArrayProperty on the export to its first entry.
        static int ReduceArrayToFirst(NormalExport exp, string arrayPropName, out FPackageIndex firstObjPi)
        {
            firstObjPi = null;
            foreach (var p in exp.Data)
            {
                if (p?.Name?.Value?.Value == arrayPropName && p is ArrayPropertyData ap)
                {
                    if (ap.Value.Length == 0)
                        throw new InvalidOperationException(
                            "Array property '" + arrayPropName + "' empty on '"
                            + exp.ObjectName.Value.Value + "'");
                    if (ap.Value[0] is ObjectPropertyData op) firstObjPi = op.Value;
                    ap.Value = new[] { ap.Value[0] };
                    return 0;
                }
            }
            throw new InvalidOperationException(
                "Array property '" + arrayPropName + "' not found on '"
                + exp.ObjectName.Value.Value + "'");
        }

        string ExtractVanilla(string assetStem, string outDir)
        {
            Directory.CreateDirectory(outDir);
            var argv = new List<string>
            {
                "--aes-key", AesKey,
                "to-legacy",
                VanillaPaksDir, outDir,
                "--version", "UE5_6",
                "--filter", assetStem,
            };
            int rc = RunProcess(RetocExe, argv.ToArray());
            if (rc != 0)
                throw new InvalidOperationException(
                    "retoc to-legacy failed for '" + assetStem + "' (exit " + rc + ")");
            var found = Directory.GetFiles(outDir, assetStem + ".uasset", SearchOption.AllDirectories);
            if (found.Length == 0)
                throw new InvalidOperationException(
                    "retoc produced no " + assetStem + ".uasset");
            return found[0];
        }

        int RunProcess(string exe, string[] argv)
        {
            var psi = new ProcessStartInfo
            {
                FileName = exe,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            foreach (var a in argv) psi.ArgumentList.Add(a);
            using var p = new Process { StartInfo = psi };
            p.OutputDataReceived += (_, e) => { if (e.Data != null) LogLine("    " + e.Data); };
            p.ErrorDataReceived  += (_, e) => { if (e.Data != null) LogLine("    " + e.Data); };
            p.Start();
            p.BeginOutputReadLine();
            p.BeginErrorReadLine();
            p.WaitForExit();
            return p.ExitCode;
        }

        void EnsureTooling()
        {
            if (string.IsNullOrEmpty(BinkEncoderPath) || !File.Exists(BinkEncoderPath))
                throw new InvalidOperationException("BinkEncoderPath missing: " + BinkEncoderPath);
            if (string.IsNullOrEmpty(SwavTemplateUasset) || !File.Exists(SwavTemplateUasset))
                throw new InvalidOperationException("SwavTemplateUasset missing: " + SwavTemplateUasset);
            if (string.IsNullOrEmpty(SwavTemplateUexp) || !File.Exists(SwavTemplateUexp))
                throw new InvalidOperationException("SwavTemplateUexp missing: " + SwavTemplateUexp);
            if (string.IsNullOrEmpty(UsmapPath) || !File.Exists(UsmapPath))
                throw new InvalidOperationException("UsmapPath missing: " + UsmapPath);
            if (string.IsNullOrEmpty(RetocExe) || !File.Exists(RetocExe))
                throw new InvalidOperationException("RetocExe missing: " + RetocExe);
            if (string.IsNullOrEmpty(VanillaPaksDir) || !Directory.Exists(VanillaPaksDir))
                throw new InvalidOperationException("VanillaPaksDir missing: " + VanillaPaksDir);
            if (string.IsNullOrEmpty(AesKey))
                throw new InvalidOperationException("AesKey missing");
        }

        static void WriteFloatLE(byte[] buf, int offset, float value)
        {
            var b = BitConverter.GetBytes(value);
            if (!BitConverter.IsLittleEndian) Array.Reverse(b);
            Buffer.BlockCopy(b, 0, buf, offset, 4);
        }

        // Borrowed from ShipMusicPatcher: scan the .uasset bytes for the
        // unique int64 LE value UAssetAPI wrote as Exports[0].SerialSize,
        // overwrite with the new value matching our composed .uexp size.
        static void PatchSerialSizeInUasset(string uassetPath, long oldValue, long newValue)
        {
            var bytes = File.ReadAllBytes(uassetPath);
            var needle = BitConverter.GetBytes(oldValue);
            if (!BitConverter.IsLittleEndian) Array.Reverse(needle);
            int matchOffset = -1, matchCount = 0;
            for (int i = 0; i + needle.Length <= bytes.Length; i++)
            {
                bool ok = true;
                for (int j = 0; j < needle.Length; j++)
                    if (bytes[i + j] != needle[j]) { ok = false; break; }
                if (ok) { matchOffset = i; matchCount++; if (matchCount > 1) break; }
            }
            if (matchCount != 1)
                throw new InvalidOperationException(
                    "SWAV SerialSize patch found " + matchCount + " matches for value="
                    + oldValue + " (expected 1)");
            var newBytes = BitConverter.GetBytes(newValue);
            if (!BitConverter.IsLittleEndian) Array.Reverse(newBytes);
            Buffer.BlockCopy(newBytes, 0, bytes, matchOffset, newBytes.Length);
            File.WriteAllBytes(uassetPath, bytes);
        }

        void LogLine(string s) { if (Log != null) Log(s); }
    }

    public sealed class BuildingAudioStageResult
    {
        public string BuildingId;
        public string SwavStem;
        public string SwavPackagePath;
        public string CueStem;
        public string CuePackagePath;
        public float DurationSeconds;
        public string StagedSwavUasset;
        public string StagedSwavUexp;
        public string StagedCueUasset;
    }
}
