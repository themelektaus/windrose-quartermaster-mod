using System;
using System.Diagnostics;
using System.IO;
using System.Text;

namespace Windrose.Quartermaster.Core
{
    public sealed class BinkAudioEncoder
    {
        public Action<string> Log;

        // 0 = best quality, 9 = worst.
        public int Quality = 2;

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

        public byte[] Encode(string wavPath)
        {
            if (string.IsNullOrEmpty(wavPath))
                throw new ArgumentNullException("wavPath");
            if (!File.Exists(wavPath))
                throw new FileNotFoundException("WAV not found: " + wavPath);

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

                // 'UEBA' tag stored in reverse byte order: 'A','B','E','U'.
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
                catch { }
            }
        }

        void LogLine(string msg) { if (Log != null) Log(msg); }
    }
}
