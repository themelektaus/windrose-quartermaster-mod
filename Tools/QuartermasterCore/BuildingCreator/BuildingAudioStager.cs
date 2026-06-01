using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using UAssetAPI;
using UAssetAPI.UnrealTypes;
using UAssetAPI.ExportTypes;
using UAssetAPI.PropertyTypes.Objects;
using UAssetAPI.PropertyTypes.Structs;
using UAssetAPI.Unversioned;

namespace Windrose.Quartermaster.Core.BuildingCreator
{
    public sealed class BuildingAudioStager
    {
        public Action<string> Log;

        public string BinkEncoderPath;
        public string SwavTemplateUasset;
        public string SwavTemplateUexp;
        public string UsmapPath;
        public string RetocExe;
        public string VanillaPaksDir;
        public string AesKey;
        public string TempDir;


        public const string VanillaSoundStem      = "MS_Building_Clock_LP";
        public const string VanillaSoundPath      = "/Game/Audio/Game/Building/Loops/MS_Building_Clock_LP";
        public const string VanillaSoundClassName = "MetaSoundSource";
        public const string VanillaSoundClassPkg  = "/Script/MetasoundEngine";
        public const string TargetSoundClassName  = "SoundCue";
        public const string TargetSoundClassPkg   = "/Script/Engine";

        public const string VanillaAtnStem = "ATN_Building";
        public const string VanillaAtnPath = "/Game/Audio/Game/Building/ATN_Building";

        public const string CueDonorAtnStem = "ATN_Shanti_VoiceNoPlayer";
        public const string CueDonorAtnPath = "/Game/Audio/Game/Music/Shanti/ATN_Shanti_VoiceNoPlayer";

        const double DefaultRangeMeters = 15.0;
        const double DefaultVolume      = 0.45;

        public BuildingAudioStageResult Stage(
            string buildingId, string userWavPath, string stagingItemsDir,
            double rangeMeters = 0, double volume = 0)
        {
            if (string.IsNullOrEmpty(buildingId)) throw new ArgumentNullException("buildingId");
            if (string.IsNullOrEmpty(userWavPath)) throw new ArgumentNullException("userWavPath");
            if (!File.Exists(userWavPath))
                throw new FileNotFoundException("User WAV not found: " + userWavPath);
            EnsureTooling();
            Directory.CreateDirectory(stagingItemsDir);

            // rangeMeters/volume of 0 means "unset"; apply defaults.
            double effectiveRange  = rangeMeters > 0 ? rangeMeters : DefaultRangeMeters;
            if (effectiveRange < 1.0)    effectiveRange = 1.0;
            if (effectiveRange > 1000.0) effectiveRange = 1000.0;
            double effectiveVolume = volume      > 0 ? volume      : DefaultVolume;
            if (effectiveVolume < 0.0)  effectiveVolume = 0.0;
            if (effectiveVolume > 1.0)  effectiveVolume = 1.0;

            var swavStem = "SWAV_QmBldgAudio_" + buildingId;
            var cueStem  = "CUE_QmBldgAudio_"  + buildingId;
            var atnStem  = "ATN_QmBldgAudio_"  + buildingId;
            var swavPackagePath = WindrosePaths.ModItemsPackagePath + swavStem;
            var cuePackagePath  = WindrosePaths.ModItemsPackagePath + cueStem;
            var atnPackagePath  = WindrosePaths.ModItemsPackagePath + atnStem;

            LogLine("=== [Audio:" + buildingId + "] staging SWAV+Cue+ATN (range="
                + Fmt(effectiveRange) + "m, volume=" + Fmt(effectiveVolume) + " abs) ===");

            var swavUassetOut = Path.Combine(stagingItemsDir, swavStem + ".uasset");
            var swavUexpOut   = Path.Combine(stagingItemsDir, swavStem + ".uexp");
            var swavInfo = StageSwav(userWavPath, swavStem, swavPackagePath, swavUassetOut, swavUexpOut);

            var atnUassetOut = Path.Combine(stagingItemsDir, atnStem + ".uasset");
            StageAtn(atnStem, atnPackagePath, atnUassetOut, effectiveRange);

            var cueUassetOut = Path.Combine(stagingItemsDir, cueStem + ".uasset");
            StageCue(swavStem, swavPackagePath, cueStem, cuePackagePath,
                atnStem, atnPackagePath,
                cueUassetOut, swavInfo.DurationSeconds,
                effectiveVolume, effectiveRange);

            return new BuildingAudioStageResult
            {
                BuildingId        = buildingId,
                SwavStem          = swavStem,
                SwavPackagePath   = swavPackagePath,
                CueStem           = cueStem,
                CuePackagePath    = cuePackagePath,
                AtnStem           = atnStem,
                AtnPackagePath    = atnPackagePath,
                DurationSeconds   = swavInfo.DurationSeconds,
                RangeMeters       = effectiveRange,
                Volume            = effectiveVolume,
                StagedSwavUasset  = swavUassetOut,
                StagedSwavUexp    = swavUexpOut,
                StagedCueUasset   = cueUassetOut,
                StagedAtnUasset   = atnUassetOut,
            };
        }

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

        SwavBuildInfo StageSwav(string userWavPath, string swavStem, string swavPackagePath,
            string outUasset, string outUexp)
        {
            var wav = WavInfo.Read(userWavPath);
            LogLine("  WAV " + Path.GetFileName(userWavPath) + ": " + wav.Describe());
            if (wav.SampleRate != 44100 || wav.Channels != 2 || wav.BitsPerSample != 16)
                throw new InvalidOperationException(
                    "User WAV must be 44.1 kHz / Stereo / 16-bit PCM (got "
                    + wav.Describe() + ") - the audio preprocessor should have "
                    + "conditioned this already.");

            var encoder = new BinkAudioEncoder(BinkEncoderPath) { Log = Log };
            var binkBuf = encoder.Encode(userWavPath);
            LogLine("  Bink encoded: " + binkBuf.Length + " bytes");

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

            File.Copy(SwavTemplateUasset, outUasset, overwrite: true);
            File.Copy(SwavTemplateUexp,   outUexp,   overwrite: true);
            var mappings = new Usmap(UsmapPath);
            var asset = new UAsset(outUasset, UAssetIo.Ue, mappings);

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

        void StageCue(
            string swavStem, string swavPackagePath,
            string cueStem,  string cuePackagePath,
            string atnStem,  string atnPackagePath,
            string outUasset, float audioDurationSec,
            double volume, double rangeMeters)
        {
            var perBuildingTemp = Path.Combine(TempDir ?? Path.GetTempPath(),
                "qm-bldgaudio-cue-" + cueStem);
            if (Directory.Exists(perBuildingTemp)) Directory.Delete(perBuildingTemp, true);
            Directory.CreateDirectory(perBuildingTemp);

            const string vanillaCueStem = "CUE_Shanti_10_VoiceNoPlayer";
            var legacyPath = ExtractVanilla(vanillaCueStem, perBuildingTemp);

            LogLine("  Cloning cue " + vanillaCueStem + " -> " + cueStem);
            var mappings = new Usmap(UsmapPath);
            var asset = new UAsset(legacyPath, UAssetIo.Ue, mappings);

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
                { CueDonorAtnStem,
                  atnStem },
                { CueDonorAtnPath,
                  atnPackagePath },
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

            ReshapeMinimalAndLoop(asset, swavStem, /*durationSec*/ 600f);

            ApplyVolumeAndRangeToCue(asset, volume, rangeMeters);

            asset.Write(outUasset);
            LogLine("  Cue staged: " + cueStem + " (" + hits + " NameMap rewrites, "
                + "volume=" + Fmt(volume) + ", range=" + Fmt(rangeMeters) + "m)");
        }

        void StageAtn(string atnStem, string atnPackagePath,
            string outUasset, double rangeMeters)
        {
            var perBuildingTemp = Path.Combine(TempDir ?? Path.GetTempPath(),
                "qm-bldgaudio-atn-" + atnStem);
            if (Directory.Exists(perBuildingTemp)) Directory.Delete(perBuildingTemp, true);
            Directory.CreateDirectory(perBuildingTemp);

            var legacyPath = ExtractVanilla(VanillaAtnStem, perBuildingTemp);

            LogLine("  Cloning attenuation " + VanillaAtnStem + " -> " + atnStem);
            var mappings = new Usmap(UsmapPath);
            var asset = new UAsset(legacyPath, UAssetIo.Ue, mappings);

            var rewrites = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                { VanillaAtnStem, atnStem },
                { VanillaAtnPath, atnPackagePath },
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
                    "ATN NameMap rewrite incomplete (" + hits + "/" + rewrites.Count
                    + ") - vanilla ATN_Building drifted?");
            asset.FolderName = FString.FromString(atnPackagePath);

            // FalloffDistance is in centimeters.
            float falloffCm = (float)(rangeMeters * 100.0);
            PatchAtnFalloff(asset, falloffCm);

            asset.Write(outUasset);
            LogLine("  ATN staged: " + atnStem + " (" + hits + " NameMap rewrites, FalloffDistance="
                + Fmt(falloffCm) + "cm)");
        }

        static void PatchAtnFalloff(UAsset asset, float falloffCm)
        {
            if (asset.Exports.Count == 0)
                throw new InvalidOperationException("ATN asset has no exports");
            var rootExp = asset.Exports[0] as NormalExport;
            if (rootExp == null)
                throw new InvalidOperationException("ATN Export[0] is not a NormalExport");

            StructPropertyData attnStruct = null;
            foreach (var p in rootExp.Data)
            {
                if (p?.Name?.Value?.Value == "Attenuation" && p is StructPropertyData sp)
                {
                    attnStruct = sp;
                    break;
                }
            }
            if (attnStruct == null || attnStruct.Value == null)
                throw new InvalidOperationException("ATN.Attenuation StructProperty not found");

            bool found = false;
            foreach (var child in attnStruct.Value)
            {
                if (child?.Name?.Value?.Value == "FalloffDistance" && child is FloatPropertyData fp)
                {
                    fp.Value = falloffCm;
                    found = true;
                    break;
                }
            }
            if (!found)
                throw new InvalidOperationException(
                    "ATN.Attenuation.FalloffDistance not found - layout drift in ATN_Building?");
        }

        static void ApplyVolumeAndRangeToCue(UAsset asset, double volume, double rangeMeters)
        {
            if (asset.Exports.Count == 0) return;
            var cueExp = asset.Exports[0] as NormalExport;
            if (cueExp == null) return;

            bool vmHit = false, mdHit = false;
            float falloffCm = (float)(rangeMeters * 100.0);
            foreach (var p in cueExp.Data)
            {
                var n = p?.Name?.Value?.Value;
                if (n == "VolumeMultiplier" && p is FloatPropertyData vmp)
                {
                    vmp.Value = (float)volume;
                    vmHit = true;
                }
                else if (n == "MaxDistance" && p is FloatPropertyData mdp)
                {
                    mdp.Value = falloffCm;
                    mdHit = true;
                }
            }
            // MaxDistance is informational (real falloff lives in the ATN); tolerate its absence.
            if (!vmHit)
                throw new InvalidOperationException(
                    "Cue.VolumeMultiplier property absent - donor drifted?");
            _ = mdHit;
        }

        static string Fmt(double d)
        {
            return d.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);
        }

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
        public string AtnStem;
        public string AtnPackagePath;
        public float DurationSeconds;
        public double RangeMeters;
        public double Volume;
        public string StagedSwavUasset;
        public string StagedSwavUexp;
        public string StagedCueUasset;
        public string StagedAtnUasset;
    }
}
