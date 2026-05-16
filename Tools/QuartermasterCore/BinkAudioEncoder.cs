using System;
using System.Diagnostics;
using System.IO;
using System.Text;

namespace Windrose.Quartermaster.Core
{
    // Thin process wrapper around `binkaudioenc.exe` - the in-tree CLI
    // (Tools/BinkAudioEnc/binkaudioenc.cpp) that links Epic's static Bink
    // encoder library and produces a raw `[BinkAudioFileHeader][SeekTable]
    // [Frames]` buffer from a 16-bit PCM WAV. The output is byte-identical
    // to what AudioFormatBink::Cook() produces inside the UE5 Editor.
    //
    // We use the encoder for the ShipMusic feature: a user-supplied .wav
    // gets compressed and spliced into a vanilla SoundWave template so the
    // engine can stream it under one of the 10 sea-shanty asset slots.
    public sealed class BinkAudioEncoder
    {
        public Action<string> Log;

        // Quality 0..9, where 0 = best and 9 = worst. The CLI defaults to
        // 4 (the UE Editor's middle preset); 2 gives audibly better quality
        // for music at modest bitrate cost and we pick that for shanties.
        // Public so tests can override; the build pipeline sticks with the
        // default.
        public int Quality = 2;

        // Absolute path to the encoder executable. The build pipeline
        // resolves this from `<app-root>/Tools/binkaudioenc.exe`.
        public string EncoderPath;

        public BinkAudioEncoder(string encoderPath)
        {
            if (string.IsNullOrEmpty(encoderPath))
                throw new ArgumentNullException("encoderPath");
            if (!File.Exists(encoderPath))
                throw new FileNotFoundException(
                    "Bink encoder not found at " + encoderPath
                    + " - the encoder ships next to the app under Tools/. "
                    + "Reinstall Quartermaster or rebuild binkaudioenc.exe via "
                    + "Tools/BinkAudioEnc/build.bat.");
            EncoderPath = encoderPath;
        }

        // Encode a 16-bit PCM WAV to a Bink-Audio buffer. Returns the raw
        // bytes of the encoder output (header + seek-table + frames). The
        // caller splices these into a USoundWave template's .uexp.
        //
        // Throws on encoder failure with the stderr message attached so
        // the GUI can surface a clear error ("Only 16-bit PCM supported.
        // Got 24-bit." etc.).
        public byte[] Encode(string wavPath)
        {
            if (string.IsNullOrEmpty(wavPath))
                throw new ArgumentNullException("wavPath");
            if (!File.Exists(wavPath))
                throw new FileNotFoundException("WAV not found: " + wavPath);

            // Output to a sibling .binka temp file - the encoder writes
            // direct to disk, no stdout-piping support. We read it back in
            // memory and clean up.
            var tempOut = Path.Combine(Path.GetTempPath(),
                "qm_binka_" + Guid.NewGuid().ToString("N") + ".binka");

            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = EncoderPath,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                };
                psi.ArgumentList.Add(wavPath);
                psi.ArgumentList.Add(tempOut);
                psi.ArgumentList.Add("-q");
                psi.ArgumentList.Add(Quality.ToString());

                LogLine("binkaudioenc.exe -q " + Quality + " "
                    + Path.GetFileName(wavPath) + " -> "
                    + Path.GetFileName(tempOut));

                var stdout = new StringBuilder();
                var stderr = new StringBuilder();
                using (var p = new Process())
                {
                    p.StartInfo = psi;
                    p.OutputDataReceived += (s, e) =>
                    {
                        if (e.Data != null) stdout.AppendLine(e.Data);
                    };
                    p.ErrorDataReceived += (s, e) =>
                    {
                        if (e.Data != null) stderr.AppendLine(e.Data);
                    };
                    p.Start();
                    p.BeginOutputReadLine();
                    p.BeginErrorReadLine();
                    p.WaitForExit();

                    if (p.ExitCode != 0)
                    {
                        throw new InvalidOperationException(
                            "binkaudioenc.exe exited with code " + p.ExitCode
                            + (stderr.Length > 0 ? ": " + stderr.ToString().Trim() : "")
                            + (stdout.Length > 0 ? " (stdout: " + stdout.ToString().Trim() + ")" : ""));
                    }
                }

                // Surface the encoder's "OK" summary line to the build log
                // so the user can see "44.1kHz Stereo, 225s, quality=2".
                var stdoutText = stdout.ToString();
                if (!string.IsNullOrWhiteSpace(stdoutText))
                {
                    foreach (var line in stdoutText.Split('\n'))
                    {
                        var t = line.Trim();
                        if (t.Length > 0) LogLine("  " + t);
                    }
                }

                if (!File.Exists(tempOut))
                    throw new InvalidOperationException(
                        "binkaudioenc.exe reported success but produced no output file.");

                var bytes = File.ReadAllBytes(tempOut);
                if (bytes.Length < 28)
                    throw new InvalidOperationException(
                        "binkaudioenc.exe output is too short (" + bytes.Length
                        + " bytes) - expected at least a 28-byte BinkAudioFileHeader.");

                // Sanity: first 4 bytes must be the 'UEBA' tag (memory
                // order 'A','B','E','U'). Anything else means the encoder
                // produced something unexpected and we'd corrupt the
                // template downstream.
                if (bytes[0] != 0x41 || bytes[1] != 0x42
                    || bytes[2] != 0x45 || bytes[3] != 0x55)
                    throw new InvalidOperationException(
                        "binkaudioenc.exe output does not start with 'UEBA' tag - "
                        + "got " + bytes[0].ToString("X2") + " " + bytes[1].ToString("X2")
                        + " " + bytes[2].ToString("X2") + " " + bytes[3].ToString("X2") + ".");

                return bytes;
            }
            finally
            {
                try { if (File.Exists(tempOut)) File.Delete(tempOut); }
                catch { /* leave temp behind on Windows-locked edge case */ }
            }
        }

        void LogLine(string msg) { if (Log != null) Log(msg); }
    }
}
