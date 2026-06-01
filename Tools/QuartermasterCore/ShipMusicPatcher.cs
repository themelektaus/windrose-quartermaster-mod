using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using UAssetAPI;
using UAssetAPI.UnrealTypes;
using UAssetAPI.ExportTypes;
using UAssetAPI.Unversioned;

namespace Windrose.Quartermaster.Core
{
    public sealed class ShipMusicPatcher
    {
        public Action<string> Log;

        public const int TemplatePropsSize = 0x38;
        public const int TemplateUexpSize = 8618;
        public const int TemplateBinkStart = 0x38;
        public const int TemplateBinkSize = 8542;
        public const int TemplateFooterSize = 20;
        public const int OffsetSamplingRate = 0x10;
        public const int OffsetDuration = 0x14;
        public const int OffsetTotalSamples = 0x18;
        public const string TemplateAssetStem = "Empty";

        public ShipMusicPatchResult PatchFromWav(
            string userWavPath,
            string templateUassetPath,
            string templateUexpPath,
            string encoderPath,
            string stagingRoot,
            ShipMusicSlots.SlotInfo slot,
            string usmapPath)
        {
            if (string.IsNullOrEmpty(userWavPath))
                throw new ArgumentNullException("userWavPath");
            if (string.IsNullOrEmpty(templateUassetPath))
                throw new ArgumentNullException("templateUassetPath");
            if (string.IsNullOrEmpty(templateUexpPath))
                throw new ArgumentNullException("templateUexpPath");
            if (string.IsNullOrEmpty(encoderPath))
                throw new ArgumentNullException("encoderPath");
            if (string.IsNullOrEmpty(stagingRoot))
                throw new ArgumentNullException("stagingRoot");
            if (slot == null) throw new ArgumentNullException("slot");
            if (string.IsNullOrEmpty(usmapPath))
                throw new ArgumentNullException("usmapPath");
            if (!File.Exists(userWavPath))
                throw new FileNotFoundException("User WAV not found: " + userWavPath);
            if (!File.Exists(templateUassetPath))
                throw new FileNotFoundException(
                    "Template .uasset not found: " + templateUassetPath
                    + " - expected under Tools/Templates/SoundWave_BinkInline.uasset.");
            if (!File.Exists(templateUexpPath))
                throw new FileNotFoundException(
                    "Template .uexp not found: " + templateUexpPath
                    + " - expected under Tools/Templates/SoundWave_BinkInline.uexp.");

            var wav = WavInfo.Read(userWavPath);
            LogLine("WAV info for " + slot.Stem + ": " + wav.Describe());
            if (wav.SampleRate != 44100)
                throw new InvalidOperationException(
                    "Ship-music currently requires 44.1 kHz WAV input (got "
                    + wav.SampleRate + " Hz). Resample your file (Audacity / "
                    + "ffmpeg: `ffmpeg -i in.wav -ar 44100 out.wav`).");
            if (wav.Channels != 2)
                throw new InvalidOperationException(
                    "Ship-music currently requires a stereo WAV (got "
                    + wav.Channels + "-channel input). Convert your file "
                    + "(Audacity / ffmpeg: `ffmpeg -i in.wav -ac 2 out.wav`).");

            var encoder = new BinkAudioEncoder(encoderPath) { Log = Log };
            var binkBuf = encoder.Encode(userWavPath);
            LogLine("Bink encoded: " + binkBuf.Length + " bytes");

            var templateUexp = File.ReadAllBytes(templateUexpPath);
            if (templateUexp.Length != TemplateUexpSize)
                throw new InvalidOperationException(
                    "Template .uexp size mismatch (got " + templateUexp.Length
                    + ", expected " + TemplateUexpSize + ") - the template "
                    + "under Tools/Templates/ has been replaced and the "
                    + "patcher constants need re-deriving.");
            if (templateUexp[TemplateBinkStart] != 0x41
                || templateUexp[TemplateBinkStart + 1] != 0x42
                || templateUexp[TemplateBinkStart + 2] != 0x45
                || templateUexp[TemplateBinkStart + 3] != 0x55)
                throw new InvalidOperationException(
                    "Template .uexp does not have the 'UEBA' tag at offset "
                    + "0x" + TemplateBinkStart.ToString("X") + " - constants "
                    + "are out of sync with the on-disk template.");

            var props = new byte[TemplatePropsSize];
            Buffer.BlockCopy(templateUexp, 0, props, 0, TemplatePropsSize);
            WriteFloatLE(props, OffsetDuration, wav.DurationSeconds);
            WriteFloatLE(props, OffsetTotalSamples, (float)wav.SampleCount);

            var footer = new byte[TemplateFooterSize];
            Buffer.BlockCopy(templateUexp,
                templateUexp.Length - TemplateFooterSize,
                footer, 0, TemplateFooterSize);

            var newUexp = new byte[props.Length + binkBuf.Length + footer.Length];
            int p = 0;
            Buffer.BlockCopy(props, 0, newUexp, p, props.Length); p += props.Length;
            Buffer.BlockCopy(binkBuf, 0, newUexp, p, binkBuf.Length); p += binkBuf.Length;
            Buffer.BlockCopy(footer, 0, newUexp, p, footer.Length);

            var destSubPath = slot.VirtualUassetPath
                .Replace('/', Path.DirectorySeparatorChar);
            var destUassetAbs = Path.Combine(stagingRoot, destSubPath);
            var destUexpAbs = Path.ChangeExtension(destUassetAbs, ".uexp");
            var destDir = Path.GetDirectoryName(destUassetAbs);
            if (!string.IsNullOrEmpty(destDir))
                Directory.CreateDirectory(destDir);

            // UAssetAPI's constructor reads the .uexp sibling immediately, so
            // both files must be staged before opening (else an opaque error).
            File.Copy(templateUassetPath, destUassetAbs, overwrite: true);
            File.Copy(templateUexpPath, destUexpAbs, overwrite: true);
            LogLine("Loading usmap: " + usmapPath);
            var mappings = new Usmap(usmapPath);
            LogLine("Loading template uasset: " + destUassetAbs);
            var asset = new UAsset(destUassetAbs, UAssetIo.Ue, mappings);

            var vanillaPackagePath = "/Game/" + slot.VirtualUassetPath
                .Replace("R5/Content/", "", StringComparison.Ordinal)
                .Replace(".uasset", "", StringComparison.Ordinal);

            int renamed = 0;
            var names = asset.GetNameMapIndexList();
            for (int i = 0; i < names.Count; i++)
            {
                var entry = names[i];
                if (entry == null) continue;
                if (string.Equals(entry.Value, TemplateAssetStem, StringComparison.Ordinal))
                {
                    asset.SetNameReference(i, new FString(slot.Stem, entry.Encoding));
                    LogLine("  NameMap[" + i + "]: " + TemplateAssetStem + " -> " + slot.Stem);
                    renamed++;
                }
                else if (string.Equals(entry.Value, "/Game/" + TemplateAssetStem, StringComparison.Ordinal))
                {
                    asset.SetNameReference(i, new FString(vanillaPackagePath, entry.Encoding));
                    LogLine("  NameMap[" + i + "]: /Game/" + TemplateAssetStem
                        + " -> " + vanillaPackagePath);
                    renamed++;
                }
            }
            // FolderName lives outside the NameMap; set it so the package path
            // matches where the asset lands on disk.
            asset.FolderName = FString.FromString(vanillaPackagePath);

            int retargetedExports = 0;
            for (int i = 0; i < asset.Exports.Count; i++)
            {
                if (asset.Exports[i] is NormalExport ne)
                {
                    var on = ne.ObjectName;
                    if (on != null && on.Value != null
                        && string.Equals(on.Value.Value, TemplateAssetStem, StringComparison.Ordinal))
                    {
                        ne.ObjectName = FName.FromString(asset, slot.Stem);
                        retargetedExports++;
                    }
                }
            }

            if (renamed == 0 && retargetedExports == 0)
            {
                throw new InvalidOperationException(
                    "Template .uasset has no '" + TemplateAssetStem
                    + "' NameMap entry to rename - did the template change?");
            }

            // The DataResource size must track the new bink length, or retoc
            // runs off the end of the .uexp ("failed to fill whole buffer").
            var dataResources = asset.DataResources;
            int dataResourcesPatched = 0;
            if (dataResources != null && dataResources.Count > 0)
            {
                for (int i = 0; i < dataResources.Count; i++)
                {
                    var r = dataResources[i];
                    if (r.SerialSize == TemplateBinkSize && r.RawSize == TemplateBinkSize)
                    {
                        r.SerialSize = binkBuf.Length;
                        r.RawSize = binkBuf.Length;
                        dataResources[i] = r;
                        dataResourcesPatched++;
                        LogLine("  DataResources[" + i + "]: SerialSize/RawSize "
                            + TemplateBinkSize + " -> " + binkBuf.Length);
                    }
                }
                if (dataResourcesPatched == 0)
                    throw new InvalidOperationException(
                        "Template .uasset has DataResources but none with the "
                        + "expected SerialSize=" + TemplateBinkSize
                        + " - the template under Tools/Templates/ has been "
                        + "replaced and the patcher constants need re-deriving.");
            }

            long uassetApiUexpSize;
            LogLine("Writing template uasset: " + destUassetAbs);
            asset.Write(destUassetAbs);

            uassetApiUexpSize = new FileInfo(destUexpAbs).Length;

            File.WriteAllBytes(destUexpAbs, newUexp);
            LogLine("Wrote patched .uexp: " + newUexp.Length + " bytes");

            // SerialSize is the .uexp size minus the 4-byte package magic.
            long oldSerialSize = uassetApiUexpSize - 4;
            long newSerialSize = (long)newUexp.Length - 4;
            PatchSerialSizeInUasset(destUassetAbs, oldSerialSize, newSerialSize);
            LogLine("Patched Exports[0].SerialSize: "
                + oldSerialSize + " -> " + newSerialSize);

            return new ShipMusicPatchResult
            {
                SlotStem = slot.Stem,
                SlotTitle = slot.Title,
                OriginalUserStem = Path.GetFileNameWithoutExtension(userWavPath),
                NameMapEntriesRenamed = renamed,
                ExportsRetargeted = retargetedExports,
                DataResourcesPatched = dataResourcesPatched,
                NumChannels = wav.Channels,
                SampleRate = wav.SampleRate,
                DurationSeconds = wav.DurationSeconds,
                UbulkSize = 0,
                BinkBytes = binkBuf.Length,
                NewUexpSize = newUexp.Length,
            };
        }

        // Scans for the unique int64 SerialSize value rather than a fixed
        // field offset, which shifts between UE versions.
        static void PatchSerialSizeInUasset(string uassetPath, long oldValue, long newValue)
        {
            var bytes = File.ReadAllBytes(uassetPath);

            var needle = BitConverter.GetBytes(oldValue);
            if (!BitConverter.IsLittleEndian)
                Array.Reverse(needle);

            int matchOffset = -1;
            int matchCount = 0;
            for (int i = 0; i + needle.Length <= bytes.Length; i++)
            {
                bool ok = true;
                for (int j = 0; j < needle.Length; j++)
                {
                    if (bytes[i + j] != needle[j]) { ok = false; break; }
                }
                if (ok)
                {
                    matchOffset = i;
                    matchCount++;
                    if (matchCount > 1) break;
                }
            }
            if (matchCount == 0)
            {
                throw new InvalidOperationException(
                    "Could not find SerialSize=" + oldValue
                    + " in .uasset bytes - UAssetAPI's emitted SerialSize "
                    + "did not match the on-disk .uexp size.");
            }
            if (matchCount > 1)
            {
                throw new InvalidOperationException(
                    "Found " + matchCount + " occurrences of SerialSize="
                    + oldValue + " in .uasset bytes; expected exactly one. "
                    + "The patcher would not know which int64 to patch.");
            }

            var newBytes = BitConverter.GetBytes(newValue);
            if (!BitConverter.IsLittleEndian)
                Array.Reverse(newBytes);
            Buffer.BlockCopy(newBytes, 0, bytes, matchOffset, newBytes.Length);
            File.WriteAllBytes(uassetPath, bytes);
        }

        static void WriteFloatLE(byte[] buf, int offset, float value)
        {
            var b = BitConverter.GetBytes(value);
            if (!BitConverter.IsLittleEndian) Array.Reverse(b);
            Buffer.BlockCopy(b, 0, buf, offset, 4);
        }

        void LogLine(string msg) { if (Log != null) Log(msg); }
    }

    public sealed class ShipMusicPatchResult
    {
        public string SlotStem;
        public string SlotTitle;
        public string OriginalUserStem;
        public string OriginalFilename;
        public int NameMapEntriesRenamed;
        public int ExportsRetargeted;
        public int DataResourcesPatched;
        public int? NumChannels;
        public int? SampleRate;
        public float? DurationSeconds;
        public long UbulkSize;
        public int BinkBytes;
        public int NewUexpSize;

        public string FormatDiagnostic()
        {
            var inv = CultureInfo.InvariantCulture;
            var parts = new List<string>();
            if (SampleRate.HasValue)
                parts.Add((SampleRate.Value / 1000.0).ToString("0.#", inv) + " kHz");
            if (NumChannels.HasValue)
                parts.Add(NumChannels.Value == 1 ? "Mono"
                    : NumChannels.Value == 2 ? "Stereo"
                    : NumChannels.Value + " ch");
            if (DurationSeconds.HasValue)
                parts.Add(DurationSeconds.Value.ToString("0.#", inv) + "s");
            return string.Join(", ", parts);
        }
    }
}
