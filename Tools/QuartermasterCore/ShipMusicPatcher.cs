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
    // Patches a user-supplied .wav into one of the 10 vanilla ship-music
    // SoundWave slots. We don't ask the user to cook anything in the UE5
    // Editor anymore - the in-tree binkaudioenc.exe (Tools/BinkAudioEnc/)
    // turns the WAV into a Bink-Audio buffer byte-identical to what the
    // editor's AudioFormatBink cooker produces, and a tiny pre-cooked
    // ForceInline USoundWave template (Tools/Templates/) gives us the
    // surrounding .uasset+.uexp wrapper. The patcher splices the new Bink
    // bytes into a copy of the template and renames the package so the
    // engine resolves the file under the vanilla shanty's asset path.
    //
    // The template was cooked once by hand from a 5-second 44.1 kHz stereo
    // PCM WAV (References/AudioEncoder project). Its layout is fixed and
    // mirrored in the constants below; if anyone ever recooks the
    // template, regenerate these constants from the new uexp.
    //
    // Pipeline (per slot):
    //   1. WavInfo.Read(wavPath) -> rate / channels / sampleCount / dur
    //      (input is strict 44.1 kHz stereo 16-bit PCM so we don't need
    //      to retouch the template's NumChannels/SampleRate fields).
    //   2. BinkAudioEncoder.Encode(wavPath) -> binkBuf
    //      [UEBA-header][seek-table][frames] - one contiguous buffer.
    //   3. Build new .uexp:
    //        propsPatched   = template[0x00..0x38] with Duration (f32@0x14)
    //                         and TotalSamples (f32@0x18) overwritten.
    //        newUexp        = propsPatched + binkBuf + template[len-20..]
    //                         (the trailing 16 bytes of opaque hash/
    //                         padding plus the 4-byte package magic
    //                         stay verbatim - cooked Windrose paks don't
    //                         validate this hash on load).
    //   4. Use UAssetAPI to rename NameMap "Empty" -> slot.Stem in a
    //      copy of the template .uasset. Write it out (UAssetAPI emits a
    //      placeholder .uexp at the same time; we immediately overwrite
    //      that with newUexp).
    //   5. Patch Exports[0].SerialSize in the written .uasset so it
    //      matches the new (larger) .uexp size. Locate the SerialSize
    //      field by scanning the export table for the int64 value that
    //      UAssetAPI wrote and overwriting it with the new total - this
    //      is robust against UE-version layout drift.
    //   6. Hand the patched triplet (.uasset+.uexp; no .ubulk for
    //      ForceInline assets) off to the IoStore composite staging dir
    //      under the vanilla slot's virtual path.
    public sealed class ShipMusicPatcher
    {
        public Action<string> Log;

        // Template byte-layout constants. Derived from the SoundWave_BinkInline
        // template under Tools/Templates/ (cooked in UE5.6 from a 5-second
        // 44.1 kHz stereo PCM WAV in ForceInline mode).
        //
        // Layout of SoundWave_BinkInline.uexp (8618 bytes):
        //   [0x0000 .. 0x0037] (56 B) UE5 unversioned-properties block
        //                              0x10: SamplingRate (u32 LE) = 44100
        //                              0x14: Duration     (f32 LE) = 5.0 s
        //                              0x18: TotalSamples (f32 LE) = 220500
        //   [0x0038 .. 0x2195] (8542 B) Bink Audio payload starting with
        //                              the 'UEBA' tag (memory: 'A','B','E','U')
        //   [0x2196 .. 0x21A9] (20 B) Trailing hash + package magic
        public const int TemplatePropsSize = 0x38;     // 56 bytes
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

            // 1. Inspect the WAV. We hard-restrict to 44.1 kHz stereo
            // 16-bit PCM in WavInfo so the template's NumChannels /
            // SampleRate properties don't need rewriting - only the
            // Duration and TotalSamples values change per upload.
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

            // 2. Encode WAV -> Bink. Returns the raw cooker buffer
            //    [BinkAudioFileHeader][SeekTable][Frames].
            var encoder = new BinkAudioEncoder(encoderPath) { Log = Log };
            var binkBuf = encoder.Encode(userWavPath);
            LogLine("Bink encoded: " + binkBuf.Length + " bytes");

            // 3. Build the new .uexp in memory from the template.
            var templateUexp = File.ReadAllBytes(templateUexpPath);
            if (templateUexp.Length != TemplateUexpSize)
                throw new InvalidOperationException(
                    "Template .uexp size mismatch (got " + templateUexp.Length
                    + ", expected " + TemplateUexpSize + ") - the template "
                    + "under Tools/Templates/ has been replaced and the "
                    + "patcher constants need re-deriving.");
            if (templateUexp[TemplateBinkStart] != 0x41 // 'A'
                || templateUexp[TemplateBinkStart + 1] != 0x42 // 'B'
                || templateUexp[TemplateBinkStart + 2] != 0x45 // 'E'
                || templateUexp[TemplateBinkStart + 3] != 0x55) // 'U'
                throw new InvalidOperationException(
                    "Template .uexp does not have the 'UEBA' tag at offset "
                    + "0x" + TemplateBinkStart.ToString("X") + " - constants "
                    + "are out of sync with the on-disk template.");

            // Copy the props block, patch Duration + TotalSamples to the
            // user WAV's values. NumChannels / SampleRate stay verbatim
            // (we enforce 44.1 kHz stereo above).
            var props = new byte[TemplatePropsSize];
            Buffer.BlockCopy(templateUexp, 0, props, 0, TemplatePropsSize);
            WriteFloatLE(props, OffsetDuration, wav.DurationSeconds);
            WriteFloatLE(props, OffsetTotalSamples, (float)wav.SampleCount);

            // Compose [props][bink][footer]. The 16-byte hash + 4-byte
            // PACKAGE_FILE_TAG at the tail of the template stay verbatim;
            // cooked Windrose paks load fine without recomputing them.
            var footer = new byte[TemplateFooterSize];
            Buffer.BlockCopy(templateUexp,
                templateUexp.Length - TemplateFooterSize,
                footer, 0, TemplateFooterSize);

            var newUexp = new byte[props.Length + binkBuf.Length + footer.Length];
            int p = 0;
            Buffer.BlockCopy(props, 0, newUexp, p, props.Length); p += props.Length;
            Buffer.BlockCopy(binkBuf, 0, newUexp, p, binkBuf.Length); p += binkBuf.Length;
            Buffer.BlockCopy(footer, 0, newUexp, p, footer.Length);

            // 4. Stage the destination triplet path. Convert the virtual
            // forward-slash path into platform-native and ensure the
            // parent dir exists.
            var destSubPath = slot.VirtualUassetPath
                .Replace('/', Path.DirectorySeparatorChar);
            var destUassetAbs = Path.Combine(stagingRoot, destSubPath);
            var destUexpAbs = Path.ChangeExtension(destUassetAbs, ".uexp");
            var destDir = Path.GetDirectoryName(destUassetAbs);
            if (!string.IsNullOrEmpty(destDir))
                Directory.CreateDirectory(destDir);

            // Copy the template .uasset to the destination, then re-open
            // it with UAssetAPI so we can rename the package's only
            // user-facing FName ("Empty") to the vanilla slot stem. The
            // template carries the package path "/Game/Empty" - UAssetAPI
            // updates both the asset name and the implicit package path
            // when we swap the NameMap entry.
            // Copy BOTH .uasset and .uexp into staging before opening with
            // UAssetAPI. UAssetAPI's UAsset constructor reads the .uexp
            // sibling immediately to populate export bodies; a missing
            // .uexp surfaces as an opaque "ReadBytes(-N)" exception. We'll
            // overwrite the .uexp with our patched payload (newUexp)
            // further down once UAssetAPI is done with the asset object.
            File.Copy(templateUassetPath, destUassetAbs, overwrite: true);
            File.Copy(templateUexpPath, destUexpAbs, overwrite: true);
            LogLine("Loading usmap: " + usmapPath);
            var mappings = new Usmap(usmapPath);
            LogLine("Loading template uasset: " + destUassetAbs);
            // The template was cooked with UE5.7's Editor (the source-build
            // of UE the user installed for the Bink encoder static lib).
            // The other patchers parse vanilla game assets cooked with
            // UE5.6 and use VER_UE5_6; this one is the only place we hit a
            // UE5.7-cooked package, so it's the only place we bump.
            var asset = new UAsset(destUassetAbs, EngineVersion.VER_UE5_7, mappings);

            // Compute the /Game/-prefixed package path that should sit
            // in FolderName + the corresponding NameMap entry, derived
            // from the slot's virtual content path. R5/Content/Foo/Bar
            // ? /Game/Foo/Bar; drop the .uasset suffix.
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
            // FolderName is stored separately from the NameMap in the
            // package file summary; set it explicitly so cooked-load
            // path resolution and asset-registry lookups (if the engine
            // does any in shipping builds) see a consistent package
            // path matching where we're dropping the asset on disk.
            asset.FolderName = FString.FromString(vanillaPackagePath);

            // Defensive: also retarget any NormalExport.ObjectName still
            // referring to the template stem after the name-map rename
            // (covers entries whose FName.Number is nonzero and would
            // otherwise resolve to "Empty_<n>").
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

            // Patch DataResources[0].SerialSize/RawSize. The package
            // summary's FObjectDataResource list carries explicit offsets
            // into the export body for the audio's "bulk-data" chunk
            // (UE5.6+ style FByteBulkData replacement). The template's
            // resource record points at offset 56 size 8542 - meaning the
            // 8542-byte Bink chunk that lives between the 56-byte unversioned
            // property block and the 16-byte trailing hash inside the
            // template's 8614-byte export body. Our new Bink chunk is much
            // bigger (binkBuf.Length bytes), so without patching this
            // record retoc reads only the original 8542 bytes from the
            // bink payload and then runs off the end of the .uexp while
            // expecting another resource at an offset further into the
            // stream - that's what triggers "failed to fill whole buffer".
            //
            // FObjectDataResource is a value type, so we copy-mutate-write
            // back via index assignment. SerialOffset (=56, start of bink
            // inside export) stays the same because the unversioned props
            // block in front of the bink is identical between template and
            // patched .uexp.
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

            // Capture the SerialSize value UAssetAPI is about to write,
            // so we can later locate-and-patch the byte field in the
            // written .uasset. Exports[0].SerialSize is set during
            // Write() based on what UAssetAPI itself emits for the .uexp
            // (template-size, not our newUexp-size). The value we capture
            // here matches the .uexp UAssetAPI emits.
            long uassetApiUexpSize; // computed below post-write
            LogLine("Writing template uasset: " + destUassetAbs);
            asset.Write(destUassetAbs);

            // UAssetAPI just wrote both files. Read the .uexp it produced
            // to know its size (= the SerialSize value UAssetAPI burned
            // into the .uasset).
            uassetApiUexpSize = new FileInfo(destUexpAbs).Length;

            // Overwrite the .uexp with our composed bink+template payload.
            File.WriteAllBytes(destUexpAbs, newUexp);
            LogLine("Wrote patched .uexp: " + newUexp.Length + " bytes");

            // 5. Patch Exports[0].SerialSize in the .uasset. UAssetAPI's
            // SerialSize is (.uexp size - 4 bytes for the package magic).
            // We compute the same delta for our newUexp and replace.
            long oldSerialSize = uassetApiUexpSize - 4;
            long newSerialSize = (long)newUexp.Length - 4;
            PatchSerialSizeInUasset(destUassetAbs, oldSerialSize, newSerialSize);
            LogLine("Patched Exports[0].SerialSize: "
                + oldSerialSize + " -> " + newSerialSize);

            // Reuse the original ShipMusicPatchResult contract for the
            // build report. UbulkSize == 0 because ForceInline assets
            // have no .ubulk sidecar; the GUI's slot-state badge already
            // handles that case.
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

        // Locate the int64 LE field within the .uasset bytes that matches
        // the UAssetAPI-emitted SerialSize, and replace it with the new
        // value in place. We scan a bounded window starting at the export
        // table offset (read from the file summary) so we don't accidentally
        // pattern-match the same int64 value somewhere unrelated. The
        // export table sits right after the import table, and Exports[0]'s
        // SerialSize lives at a known field offset within FObjectExport -
        // but that offset shifts between UE versions, so a scan with a
        // single expected match is more robust.
        static void PatchSerialSizeInUasset(string uassetPath, long oldValue, long newValue)
        {
            var bytes = File.ReadAllBytes(uassetPath);

            // The .uasset summary has a fixed-position field at offset 24
            // pointing to TotalHeaderSize... but the export offset itself
            // varies. We can read it via UAssetAPI but a direct scan of
            // the whole file for the unique int64 LE value is simpler and
            // still safe: the only place the literal byte pattern occurs
            // is the SerialSize field of Exports[0] (a template has a
            // single export, the SoundWave).
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

    // Per-slot patch outcome. The build pipeline aggregates one of these
    // per replaced shanty into a higher-level ShipMusicResult that also
    // carries the published triplet paths.
    public sealed class ShipMusicPatchResult
    {
        public string SlotStem;
        public string SlotTitle;
        public string OriginalUserStem;
        // Display-only echoes of the per-slot ProfileGlobals.ShipMusic.Songs
        // metadata - the pipeline copies these in after Patch() returns so
        // the build-response JSON can render "Custom: My Pirate Banger
        // (mysong.wav)" without re-loading the profile.
        public string OriginalFilename;
        public string DisplayName;
        public int NameMapEntriesRenamed;
        public int ExportsRetargeted;
        public int DataResourcesPatched;
        public int? NumChannels;
        public int? SampleRate;
        public float? DurationSeconds;
        public long UbulkSize;       // always 0 for ForceInline cooks
        public int BinkBytes;        // size of the bink encoder output
        public int NewUexpSize;      // total .uexp size we wrote

        // Convenience formatter for the build log + JSON response.
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
