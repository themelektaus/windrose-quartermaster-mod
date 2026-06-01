using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Windrose.Quartermaster.Core
{
    public sealed class AudioPreprocessor
    {
        public static readonly HashSet<string> SupportedExtensions = new(
            StringComparer.OrdinalIgnoreCase)
        {
            ".wav", ".mp3", ".ogg", ".flac", ".m4a", ".aac", ".opus",
        };

        public static bool IsSupportedExtension(string filename)
        {
            if (string.IsNullOrEmpty(filename)) return false;
            var ext = Path.GetExtension(filename);
            return !string.IsNullOrEmpty(ext) && SupportedExtensions.Contains(ext);
        }

        public static string SupportedExtensionsList()
        {
            var sorted = new List<string>(SupportedExtensions);
            sorted.Sort(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < sorted.Count; i++)
                sorted[i] = sorted[i].TrimStart('.');
            return string.Join(", ", sorted);
        }

        public sealed class Result
        {
            public string OutputWavPath;
            public bool WasTranscoded;
            public string SourceFormat;
        }

        // Never deletes sourcePath; overwrites targetWavPath.
        public static async Task<Result> PreprocessAsync(
            WindrosePaths paths,
            string sourcePath,
            string targetWavPath,
            Action<string> log,
            CancellationToken ct = default)
        {
            if (paths == null) throw new ArgumentNullException("paths");
            if (string.IsNullOrEmpty(sourcePath))
                throw new ArgumentNullException("sourcePath");
            if (string.IsNullOrEmpty(targetWavPath))
                throw new ArgumentNullException("targetWavPath");
            if (!File.Exists(sourcePath))
                throw new FileNotFoundException("Audio source not found: " + sourcePath);

            var ext = Path.GetExtension(sourcePath).TrimStart('.').ToLowerInvariant();
            if (!IsSupportedExtension(sourcePath))
                throw new InvalidOperationException(
                    "Unsupported audio format: ." + ext + ". Allowed formats: "
                    + SupportedExtensionsList() + ".");

            if (string.Equals(ext, "wav", StringComparison.OrdinalIgnoreCase))
            {
                if (TryShortCircuitWav(sourcePath, targetWavPath, log))
                {
                    return new Result
                    {
                        OutputWavPath = targetWavPath,
                        WasTranscoded = false,
                        SourceFormat = ext,
                    };
                }
            }

            var ffmpeg = paths.FfmpegPath;
            if (!File.Exists(ffmpeg))
                throw new InvalidOperationException(
                    "ffmpeg.exe is required to convert ." + ext + " uploads but was not "
                    + "found at " + ffmpeg + ". Open the setup overlay and run the "
                    + "\"ffmpeg\" step (one-time ~190 MB download), or drop a ready "
                    + "ffmpeg.exe at that path. As a workaround you can upload a "
                    + ".wav file (44.1 kHz / Stereo / 16-bit PCM) instead - that "
                    + "path does not need ffmpeg.");

            var targetDir = Path.GetDirectoryName(targetWavPath);
            if (!string.IsNullOrEmpty(targetDir))
                Directory.CreateDirectory(targetDir);

            // ffmpeg into a sibling temp, then promote, so a mid-encode
            // crash can't leave a half-written WAV at the final path.
            var tempOut = targetWavPath + ".tmp-" + Guid.NewGuid().ToString("N") + ".wav";
            try
            {
                Log(log, "ffmpeg ." + ext + " -> WAV (44.1 kHz, Stereo, 16-bit PCM)");

                var psi = new ProcessStartInfo
                {
                    FileName = ffmpeg,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                };
                psi.ArgumentList.Add("-y");
                psi.ArgumentList.Add("-nostdin");
                psi.ArgumentList.Add("-loglevel"); psi.ArgumentList.Add("error");
                psi.ArgumentList.Add("-i"); psi.ArgumentList.Add(sourcePath);
                psi.ArgumentList.Add("-vn");
                psi.ArgumentList.Add("-ac"); psi.ArgumentList.Add("2");
                psi.ArgumentList.Add("-ar"); psi.ArgumentList.Add("44100");
                psi.ArgumentList.Add("-sample_fmt"); psi.ArgumentList.Add("s16");
                psi.ArgumentList.Add("-f"); psi.ArgumentList.Add("wav");
                psi.ArgumentList.Add(tempOut);

                var stderr = new StringBuilder();
                using (var p = new Process())
                {
                    p.StartInfo = psi;
                    p.ErrorDataReceived += (s, e) =>
                    {
                        if (e.Data != null) stderr.AppendLine(e.Data);
                    };
                    p.Start();
                    p.BeginErrorReadLine();
                    // Drain stdout to avoid a full-pipe back-pressure deadlock.
                    _ = p.StandardOutput.ReadToEndAsync(ct);
                    await p.WaitForExitAsync(ct).ConfigureAwait(false);

                    if (p.ExitCode != 0)
                    {
                        var err = stderr.ToString().Trim();
                        if (err.Length > 800) err = err.Substring(0, 800) + " ...";
                        throw new InvalidOperationException(
                            "ffmpeg failed to convert ." + ext + " to WAV (exit "
                            + p.ExitCode + ")"
                            + (err.Length > 0 ? ": " + err : "") + ".");
                    }
                }

                if (!File.Exists(tempOut))
                    throw new InvalidOperationException(
                        "ffmpeg reported success but produced no output WAV.");

                if (File.Exists(targetWavPath))
                {
                    try { File.Delete(targetWavPath); } catch { }
                }
                File.Move(tempOut, targetWavPath);

                Log(log, "Converted ." + ext + " to "
                    + FormatMb(new FileInfo(targetWavPath).Length) + " WAV");

                return new Result
                {
                    OutputWavPath = targetWavPath,
                    WasTranscoded = true,
                    SourceFormat = ext,
                };
            }
            finally
            {
                try { if (File.Exists(tempOut)) File.Delete(tempOut); }
                catch { }
            }
        }

        static bool TryShortCircuitWav(string sourceWav, string targetWav, Action<string> log)
        {
            try
            {
                var info = WavInfo.Read(sourceWav);
                if (info.SampleRate == 44100
                    && info.Channels == 2
                    && info.BitsPerSample == 16
                    && info.Format == 1)
                {
                    var targetDir = Path.GetDirectoryName(targetWav);
                    if (!string.IsNullOrEmpty(targetDir))
                        Directory.CreateDirectory(targetDir);
                    File.Copy(sourceWav, targetWav, overwrite: true);
                    Log(log, "WAV already 44.1 kHz / Stereo / 16-bit PCM - no transcode needed");
                    return true;
                }
            }
            catch
            {
            }
            return false;
        }

        // When gain ~= 1.0 returns sourceWavPath unchanged (caller must NOT
        // delete it); otherwise returns a fresh temp WAV the caller owns.
        public static async Task<string> ApplyGainAsync(
            WindrosePaths paths,
            string sourceWavPath,
            double gain,
            Action<string> log,
            CancellationToken ct = default)
        {
            if (paths == null) throw new ArgumentNullException("paths");
            if (string.IsNullOrEmpty(sourceWavPath))
                throw new ArgumentNullException("sourceWavPath");
            if (!File.Exists(sourceWavPath))
                throw new FileNotFoundException("Source WAV not found: " + sourceWavPath);

            if (gain < 0.0) gain = 0.0;
            if (Math.Abs(gain - 1.0) < 1e-4)
            {
                return sourceWavPath;
            }

            var ffmpeg = paths.FfmpegPath;
            if (!File.Exists(ffmpeg))
                throw new InvalidOperationException(
                    "ffmpeg.exe is required to apply audio gain but was not found at "
                    + ffmpeg + ". Open the setup overlay and run the \"ffmpeg\" step "
                    + "(one-time ~190 MB download).");

            // Invariant culture: ffmpeg's filter parser rejects a locale comma.
            var gainStr = gain.ToString("0.######",
                System.Globalization.CultureInfo.InvariantCulture);

            var tempOut = Path.Combine(Path.GetTempPath(),
                "qm_gain_" + Guid.NewGuid().ToString("N") + ".wav");

            var psi = new ProcessStartInfo
            {
                FileName = ffmpeg,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            psi.ArgumentList.Add("-y");
            psi.ArgumentList.Add("-nostdin");
            psi.ArgumentList.Add("-loglevel"); psi.ArgumentList.Add("error");
            psi.ArgumentList.Add("-i"); psi.ArgumentList.Add(sourceWavPath);
            psi.ArgumentList.Add("-vn");
            psi.ArgumentList.Add("-filter:a"); psi.ArgumentList.Add("volume=" + gainStr);
            psi.ArgumentList.Add("-ac"); psi.ArgumentList.Add("2");
            psi.ArgumentList.Add("-ar"); psi.ArgumentList.Add("44100");
            psi.ArgumentList.Add("-sample_fmt"); psi.ArgumentList.Add("s16");
            psi.ArgumentList.Add("-f"); psi.ArgumentList.Add("wav");
            psi.ArgumentList.Add(tempOut);

            Log(log, "ffmpeg apply gain volume=" + gainStr
                + " -> " + Path.GetFileName(tempOut));

            var stderr = new StringBuilder();
            using (var p = new Process())
            {
                p.StartInfo = psi;
                p.ErrorDataReceived += (s, e) =>
                {
                    if (e.Data != null) stderr.AppendLine(e.Data);
                };
                p.Start();
                p.BeginErrorReadLine();
                _ = p.StandardOutput.ReadToEndAsync(ct);
                await p.WaitForExitAsync(ct).ConfigureAwait(false);

                if (p.ExitCode != 0)
                {
                    var err = stderr.ToString().Trim();
                    if (err.Length > 800) err = err.Substring(0, 800) + " ...";
                    try { if (File.Exists(tempOut)) File.Delete(tempOut); }
                    catch { }
                    throw new InvalidOperationException(
                        "ffmpeg failed to apply volume=" + gainStr + " gain (exit "
                        + p.ExitCode + ")"
                        + (err.Length > 0 ? ": " + err : "") + ".");
                }
            }

            if (!File.Exists(tempOut))
                throw new InvalidOperationException(
                    "ffmpeg reported success but produced no gain-adjusted WAV.");

            return tempOut;
        }

        public static async Task<string> GenerateSilenceAsync(
            WindrosePaths paths,
            double durationSec,
            Action<string> log,
            CancellationToken ct = default)
        {
            if (paths == null) throw new ArgumentNullException("paths");
            if (durationSec <= 0.0)
                throw new ArgumentOutOfRangeException("durationSec",
                    "Silence duration must be > 0 seconds.");

            var ffmpeg = paths.FfmpegPath;
            if (!File.Exists(ffmpeg))
                throw new InvalidOperationException(
                    "ffmpeg.exe is required to synthesize a silence WAV but was not found at "
                    + ffmpeg + ". Open the setup overlay and run the \"ffmpeg\" step "
                    + "(one-time ~190 MB download).");

            var durStr = durationSec.ToString("0.###",
                System.Globalization.CultureInfo.InvariantCulture);

            var tempOut = Path.Combine(Path.GetTempPath(),
                "qm_silence_" + Guid.NewGuid().ToString("N") + ".wav");

            var psi = new ProcessStartInfo
            {
                FileName = ffmpeg,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            psi.ArgumentList.Add("-y");
            psi.ArgumentList.Add("-nostdin");
            psi.ArgumentList.Add("-loglevel"); psi.ArgumentList.Add("error");
            psi.ArgumentList.Add("-f"); psi.ArgumentList.Add("lavfi");
            psi.ArgumentList.Add("-i");
            psi.ArgumentList.Add("anullsrc=channel_layout=stereo:sample_rate=44100");
            psi.ArgumentList.Add("-t"); psi.ArgumentList.Add(durStr);
            psi.ArgumentList.Add("-ac"); psi.ArgumentList.Add("2");
            psi.ArgumentList.Add("-ar"); psi.ArgumentList.Add("44100");
            psi.ArgumentList.Add("-sample_fmt"); psi.ArgumentList.Add("s16");
            psi.ArgumentList.Add("-f"); psi.ArgumentList.Add("wav");
            psi.ArgumentList.Add(tempOut);

            Log(log, "ffmpeg generate silence " + durStr + "s -> "
                + Path.GetFileName(tempOut));

            var stderr = new StringBuilder();
            using (var p = new Process())
            {
                p.StartInfo = psi;
                p.ErrorDataReceived += (s, e) =>
                {
                    if (e.Data != null) stderr.AppendLine(e.Data);
                };
                p.Start();
                p.BeginErrorReadLine();
                _ = p.StandardOutput.ReadToEndAsync(ct);
                await p.WaitForExitAsync(ct).ConfigureAwait(false);

                if (p.ExitCode != 0)
                {
                    var err = stderr.ToString().Trim();
                    if (err.Length > 800) err = err.Substring(0, 800) + " ...";
                    try { if (File.Exists(tempOut)) File.Delete(tempOut); }
                    catch { }
                    throw new InvalidOperationException(
                        "ffmpeg failed to synthesize silence (exit "
                        + p.ExitCode + ")"
                        + (err.Length > 0 ? ": " + err : "") + ".");
                }
            }

            if (!File.Exists(tempOut))
                throw new InvalidOperationException(
                    "ffmpeg reported success but produced no silence WAV.");

            return tempOut;
        }

        static string FormatMb(long bytes)
        {
            return (bytes / (1024.0 * 1024.0)).ToString("0.0",
                System.Globalization.CultureInfo.InvariantCulture) + " MB";
        }

        static void Log(Action<string> log, string msg)
        {
            if (log != null) log(msg);
        }
    }
}
