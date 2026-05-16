using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Windrose.Quartermaster.Core
{
    // Converts arbitrary user-uploaded audio (mp3 / ogg / flac / m4a /
    // aac / opus / wav) into the strict 44.1 kHz stereo 16-bit PCM WAV
    // the Bink encoder accepts. Used by the ship-music upload endpoint
    // so users can drop common-format audio files instead of having to
    // pre-convert with Audacity or ffmpeg themselves.
    //
    // Strategy:
    //   - For .wav inputs that already match the target spec (44.1 kHz /
    //     stereo / 16-bit PCM), skip ffmpeg entirely and just rename /
    //     copy. Avoids the ~190 MB ffmpeg download on systems where users
    //     only ever upload correctly-formatted WAVs.
    //   - For everything else, shell out to ffmpeg.exe with
    //     `-ar 44100 -ac 2 -sample_fmt s16` to force the target spec.
    //     ffmpeg picks the right decoder from the input file's magic
    //     bytes, so .mp3 named .opus still works.
    //
    // The preprocessor expects ffmpeg.exe to be already staged in the
    // workspace root - it does NOT download on demand. ffmpeg is pulled
    // in as a setup step (SetupRunner.Run -> "ffmpeg" step) so the
    // ~190 MB download happens up-front during first-time setup rather
    // than surprising the user mid-upload. If ffmpeg is missing here,
    // we throw a user-readable error with a hint to re-run setup; the
    // upload endpoint surfaces this as a 400 with the message intact.
    public sealed class AudioPreprocessor
    {
        // File extensions we'll accept. Everything else gets a "this
        // format isn't supported" error at the upload endpoint before
        // we even invoke the preprocessor. The list mirrors what
        // ffmpeg.exe's BtbN LGPL build can decode reliably (no exotic
        // tracker formats, no MKV/MP4 video containers - audio-only).
        public static readonly HashSet<string> SupportedExtensions = new(
            StringComparer.OrdinalIgnoreCase)
        {
            ".wav", ".mp3", ".ogg", ".flac", ".m4a", ".aac", ".opus",
        };

        // True if `path` has an extension we route through the
        // preprocessor. Filename-only check, doesn't open the file.
        public static bool IsSupportedExtension(string filename)
        {
            if (string.IsNullOrEmpty(filename)) return false;
            var ext = Path.GetExtension(filename);
            return !string.IsNullOrEmpty(ext) && SupportedExtensions.Contains(ext);
        }

        // Human-readable list ("wav, mp3, ogg, flac, m4a, aac, opus")
        // for error messages.
        public static string SupportedExtensionsList()
        {
            var sorted = new List<string>(SupportedExtensions);
            sorted.Sort(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < sorted.Count; i++)
                sorted[i] = sorted[i].TrimStart('.');
            return string.Join(", ", sorted);
        }

        // Result of a preprocess pass. WasTranscoded is true when
        // ffmpeg actually ran; false when the input was already a
        // matching WAV and we short-circuited. Used by the endpoint to
        // surface "Converted from MP3" feedback in the upload response.
        public sealed class Result
        {
            public string OutputWavPath;
            public bool WasTranscoded;
            public string SourceFormat; // file extension without the dot
        }

        // Convert `sourcePath` into a target-spec WAV at `targetWavPath`,
        // overwriting any existing file there. Returns metadata about
        // what happened. Throws InvalidOperationException with a
        // user-readable message on ffmpeg failure (stderr attached).
        //
        // The caller owns lifecycle of both paths: it picks where the
        // source landed (typically a temp file) and where the cleaned
        // WAV should end up (typically Profiles/<id>/ShipMusic/<slot>/
        // audio.wav). We never delete the source.
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

            // Fast path: WAV input that already matches the target spec.
            // Avoids the ffmpeg download on systems where users only
            // upload pre-converted WAVs.
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
                // WAV but wrong spec - fall through to ffmpeg resample.
            }

            // Locate the pre-staged ffmpeg.exe. The setup overlay's
            // "ffmpeg" step puts it at paths.FfmpegPath; if the user
            // skipped the step (or the download failed), we fail fast
            // here with a clear hint rather than trying to download
            // synchronously inside an HTTP upload handler.
            var ffmpeg = paths.FfmpegPath;
            if (!File.Exists(ffmpeg))
                throw new InvalidOperationException(
                    "ffmpeg.exe is required to convert ." + ext + " uploads but was not "
                    + "found at " + ffmpeg + ". Open the setup overlay and run the "
                    + "\"ffmpeg\" step (one-time ~190 MB download), or drop a ready "
                    + "ffmpeg.exe at that path. As a workaround you can upload a "
                    + ".wav file (44.1 kHz / Stereo / 16-bit PCM) instead - that "
                    + "path does not need ffmpeg.");

            // Ensure the target directory exists - the upload endpoint
            // already does this, but the preprocessor is a public API
            // and a caller that uses it from a script shouldn't have to
            // remember.
            var targetDir = Path.GetDirectoryName(targetWavPath);
            if (!string.IsNullOrEmpty(targetDir))
                Directory.CreateDirectory(targetDir);

            // Atomic-ish write: ffmpeg into a sibling .tmp file, then
            // overwrite. ffmpeg's own output is atomic per-frame, but
            // a mid-encode crash would leave a half-written .wav at the
            // final path otherwise.
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
                // -y          overwrite output without prompting
                // -nostdin    don't try to read from a terminal (defensive
                //             for headless / service hosts)
                // -loglevel error  silence the chatty banner; we want
                //             only true errors on stderr
                // -i <src>    input
                // -vn         drop any embedded video / cover art tracks
                // -ac 2       force stereo (downmix mono / 5.1)
                // -ar 44100   force 44.1 kHz (resample if needed)
                // -sample_fmt s16  force 16-bit PCM (truncate from 24/32)
                // -f wav      force RIFF/WAVE output container even if
                //             the user picked a non-.wav target name
                // <dst>       output path
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
                    // Drain stdout to avoid back-pressure deadlock; ffmpeg
                    // doesn't print to stdout in our config but the pipe
                    // is open.
                    _ = p.StandardOutput.ReadToEndAsync(ct);
                    await p.WaitForExitAsync(ct).ConfigureAwait(false);

                    if (p.ExitCode != 0)
                    {
                        var err = stderr.ToString().Trim();
                        // Trim very long ffmpeg error dumps so the GUI's
                        // toast doesn't become a wall of text.
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

                // Promote temp -> final. Replace any leftover from a
                // prior failed upload.
                if (File.Exists(targetWavPath))
                {
                    try { File.Delete(targetWavPath); } catch { /* fall through */ }
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
                catch { /* best-effort temp cleanup */ }
            }
        }

        // If `sourceWav` is already a 44.1 kHz stereo 16-bit PCM WAV,
        // copy it to `targetWav` and return true. Anything else (wrong
        // rate / channels / bit depth, non-PCM compression, malformed
        // RIFF) returns false so the caller can fall back to ffmpeg
        // resampling. We don't move-in-place because the source may be
        // outside the profile's storage (e.g. an IFormFile-flushed
        // temp file).
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
                // Malformed WAV header etc. - let ffmpeg have a go at
                // it; it's much more tolerant than WavInfo's strict
                // RIFF reader.
            }
            return false;
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
