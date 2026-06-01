using System;
using System.IO;

namespace Windrose.Quartermaster.Core
{
    public static class WavInfo
    {
        public sealed class Info
        {
            public int Channels;
            public int SampleRate;
            public int BitsPerSample;
            public int Format;
            public long SampleCount;
            public float DurationSeconds;

            public string Describe()
            {
                return SampleRate + " Hz, "
                    + (Channels == 1 ? "Mono" : Channels == 2 ? "Stereo" : Channels + " ch")
                    + ", " + BitsPerSample + "-bit, "
                    + DurationSeconds.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture)
                    + "s";
            }
        }

        public static Info Read(string wavPath)
        {
            if (string.IsNullOrEmpty(wavPath))
                throw new ArgumentNullException("wavPath");
            if (!File.Exists(wavPath))
                throw new FileNotFoundException("WAV not found: " + wavPath);

            using (var fs = new FileStream(wavPath, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (var br = new BinaryReader(fs))
            {
                var riff = br.ReadBytes(4);
                if (riff.Length != 4 || riff[0] != (byte)'R' || riff[1] != (byte)'I'
                    || riff[2] != (byte)'F' || riff[3] != (byte)'F')
                    throw new InvalidOperationException(
                        "Not a RIFF file: " + Path.GetFileName(wavPath)
                        + " - the ship-music slot expects an uncompressed .wav.");
                br.ReadInt32();
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
                    // RIFF pads odd chunk sizes to even.
                    var chunkEnd = fs.Position + size + (size & 1u);
                    if (tag[0] == (byte)'f' && tag[1] == (byte)'m' && tag[2] == (byte)'t' && tag[3] == (byte)' ')
                    {
                        if (size < 16)
                            throw new InvalidOperationException(
                                "WAV fmt chunk is too short (" + size + " bytes).");
                        format = br.ReadUInt16();
                        channels = br.ReadUInt16();
                        sampleRate = br.ReadInt32();
                        br.ReadInt32();
                        br.ReadInt16();
                        bitsPerSample = br.ReadUInt16();
                        sawFmt = true;
                        fs.Position = chunkEnd;
                    }
                    else if (tag[0] == (byte)'d' && tag[1] == (byte)'a' && tag[2] == (byte)'t' && tag[3] == (byte)'a')
                    {
                        dataBytes = size;
                        sawData = true;
                        break;
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
