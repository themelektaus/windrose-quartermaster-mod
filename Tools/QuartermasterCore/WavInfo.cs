using System;
using System.IO;

namespace Windrose.Quartermaster.Core
{
    // Lightweight RIFF/WAVE header inspector. Reads just enough of the
    // first kilobyte of a .wav file to expose channel count / sample
    // rate / total sample count / duration to ShipMusicPatcher - the
    // patcher needs these to populate the USoundWave's unversioned
    // properties (NumChannels, SampleRate, Duration) so the engine
    // builds correct streaming metadata for the cooked SoundWave.
    //
    // We don't decode PCM here; that's the Bink encoder CLI's job
    // (it has its own RIFF reader). Keeping this scanner separate
    // means a hard-validate pass ("is this a 16-bit PCM WAV at a
    // supported rate?") can happen on the WAV the moment the user
    // uploads it, before queueing a slot for build.
    public static class WavInfo
    {
        public sealed class Info
        {
            public int Channels;       // 1 or 2 in practice
            public int SampleRate;     // 22050 / 44100 / 48000 typical
            public int BitsPerSample;  // must be 16 for the Bink encoder
            public int Format;         // 1 = PCM (WAVE_FORMAT_PCM)
            public long SampleCount;   // per-channel sample frames
            public float DurationSeconds; // SampleCount / SampleRate

            public string Describe()
            {
                return SampleRate + " Hz, "
                    + (Channels == 1 ? "Mono" : Channels == 2 ? "Stereo" : Channels + " ch")
                    + ", " + BitsPerSample + "-bit, "
                    + DurationSeconds.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture)
                    + "s";
            }
        }

        // Throws InvalidOperationException with a user-readable message
        // for anything the Bink encoder won't accept. Callers (the upload
        // endpoint and the patcher) propagate the message to the GUI.
        public static Info Read(string wavPath)
        {
            if (string.IsNullOrEmpty(wavPath))
                throw new ArgumentNullException("wavPath");
            if (!File.Exists(wavPath))
                throw new FileNotFoundException("WAV not found: " + wavPath);

            using (var fs = new FileStream(wavPath, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (var br = new BinaryReader(fs))
            {
                // RIFF header: 'RIFF' + size + 'WAVE'
                var riff = br.ReadBytes(4);
                if (riff.Length != 4 || riff[0] != (byte)'R' || riff[1] != (byte)'I'
                    || riff[2] != (byte)'F' || riff[3] != (byte)'F')
                    throw new InvalidOperationException(
                        "Not a RIFF file: " + Path.GetFileName(wavPath)
                        + " - the ship-music slot expects an uncompressed .wav.");
                br.ReadInt32(); // riff chunk size, ignore
                var wave = br.ReadBytes(4);
                if (wave.Length != 4 || wave[0] != (byte)'W' || wave[1] != (byte)'A'
                    || wave[2] != (byte)'V' || wave[3] != (byte)'E')
                    throw new InvalidOperationException(
                        "Not a WAVE container: " + Path.GetFileName(wavPath));

                int format = 0, channels = 0, sampleRate = 0, bitsPerSample = 0;
                long dataBytes = 0;
                bool sawFmt = false, sawData = false;

                while (fs.Position < fs.Length)
                {
                    var tag = br.ReadBytes(4);
                    if (tag.Length != 4) break;
                    var size = br.ReadUInt32();
                    var chunkEnd = fs.Position + size + (size & 1u); // RIFF pads odd sizes
                    if (tag[0] == (byte)'f' && tag[1] == (byte)'m' && tag[2] == (byte)'t' && tag[3] == (byte)' ')
                    {
                        if (size < 16)
                            throw new InvalidOperationException(
                                "WAV fmt chunk is too short (" + size + " bytes).");
                        format = br.ReadUInt16();
                        channels = br.ReadUInt16();
                        sampleRate = br.ReadInt32();
                        br.ReadInt32(); // ByteRate
                        br.ReadInt16(); // BlockAlign
                        bitsPerSample = br.ReadUInt16();
                        sawFmt = true;
                        // Skip any extension bytes (WAVE_FORMAT_EXTENSIBLE)
                        fs.Position = chunkEnd;
                    }
                    else if (tag[0] == (byte)'d' && tag[1] == (byte)'a' && tag[2] == (byte)'t' && tag[3] == (byte)'a')
                    {
                        dataBytes = size;
                        sawData = true;
                        break; // we have everything we need; bail before walking PCM
                    }
                    else
                    {
                        fs.Position = chunkEnd;
                    }
                }

                if (!sawFmt)
                    throw new InvalidOperationException("WAV is missing the fmt chunk.");
                if (!sawData)
                    throw new InvalidOperationException("WAV is missing the data chunk.");
                if (format != 1)
                    throw new InvalidOperationException(
                        "Only uncompressed PCM WAVs are supported (format=" + format
                        + "). Re-export from your audio tool as 16-bit PCM.");
                if (bitsPerSample != 16)
                    throw new InvalidOperationException(
                        "Only 16-bit PCM WAVs are supported (got " + bitsPerSample
                        + "-bit). Re-export as 16-bit PCM.");
                if (channels < 1 || channels > 2)
                    throw new InvalidOperationException(
                        "Only mono or stereo WAVs are supported (got " + channels + " channels).");
                if (sampleRate != 22050 && sampleRate != 44100 && sampleRate != 48000)
                    throw new InvalidOperationException(
                        "Sample rate must be 22050, 44100, or 48000 Hz (got " + sampleRate + " Hz). "
                        + "Resample your file before uploading.");

                long bytesPerFrame = channels * (bitsPerSample / 8);
                long sampleCount = dataBytes / bytesPerFrame;

                return new Info
                {
                    Channels = channels,
                    SampleRate = sampleRate,
                    BitsPerSample = bitsPerSample,
                    Format = format,
                    SampleCount = sampleCount,
                    DurationSeconds = (float)((double)sampleCount / (double)sampleRate),
                };
            }
        }
    }
}
