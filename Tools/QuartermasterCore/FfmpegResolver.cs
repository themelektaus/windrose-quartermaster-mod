using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Windrose.Quartermaster.Core
{
    // Resolves a working ffmpeg.exe path on demand, downloading a
    // portable build into the workspace root on first use if none is
    // present. The ship-music audio preprocessor calls this once before
    // transcoding any non-WAV upload (mp3/ogg/flac/m4a/aac/opus) into
    // the 44.1 kHz stereo 16-bit PCM WAV the Bink encoder accepts.
    //
    // Source: BtbN/FFmpeg-Builds on GitHub Releases (LGPL variant),
    // ~190 MB ZIP. The archive ships ffmpeg.exe, ffprobe.exe, ffplay.exe
    // and docs under ffmpeg-master-latest-win64-lgpl/bin/ - we extract
    // only ffmpeg.exe (~170 MB on disk, statically linked) and discard
    // the rest. The result lands at <ModRoot>/ffmpeg.exe and is
    // gitignored. GitHub Releases serve via GitHub's CDN, which is
    // consistently faster worldwide than gyan.dev's single-host origin
    // (measured ~9 MB/s vs ~270 KB/s from this workspace).
    //
    // The LGPL variant excludes x264/x265 and other GPL-restricted
    // encoders; it still includes every audio codec the ship-music
    // pipeline needs (mp3, aac, opus, flac, vorbis/ogg, alac/m4a, pcm).
    //
    // Thread safety: a process-wide lock prevents two concurrent uploads
    // from triggering parallel downloads. Cross-process safety is not
    // attempted (the build pipeline runs single-threaded and the upload
    // endpoint is the only other caller); if two Quartermaster instances
    // ever race the second one would re-download and overwrite, which is
    // wasteful but not harmful.
    public static class FfmpegResolver
    {
        // BtbN/FFmpeg-Builds "latest" tag, refreshed nightly from ffmpeg
        // master. The tag is stable (always points at the newest build)
        // so we don't have to chase version-stamped URLs. LGPL variant
        // since we only transcode audio.
        const string DownloadUrl =
            "https://github.com/BtbN/FFmpeg-Builds/releases/download/latest/ffmpeg-master-latest-win64-lgpl.zip";

        // In-process lock so concurrent upload endpoints don't race on
        // the same disk write.
        static readonly SemaphoreSlim _gate = new SemaphoreSlim(1, 1);

        // Returns the absolute path to a verified-working ffmpeg.exe,
        // downloading it on first use. Throws InvalidOperationException
        // (with a user-readable message) on any unrecoverable error so
        // the upload endpoint can surface it as a 400 / 500.
        //
        // The `log` callback receives progress messages ("Downloading
        // ffmpeg...", "Extracting...", "Verified") which the endpoint
        // can pipe into the build log or upload-response stream.
        public static async Task<string> ResolveAsync(
            WindrosePaths paths,
            Action<string> log,
            CancellationToken ct = default)
        {
            if (paths == null) throw new ArgumentNullException("paths");
            var dest = paths.FfmpegPath;

            // Fast path: file already there and works. Skip the lock so
            // 100 concurrent uploads don't queue up on the SemaphoreSlim
            // when nothing actually needs doing.
            if (File.Exists(dest) && await TryVerifyAsync(dest, ct).ConfigureAwait(false))
                return dest;

            await _gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                // Re-check inside the lock - another caller may have
                // downloaded while we were waiting.
                if (File.Exists(dest) && await TryVerifyAsync(dest, ct).ConfigureAwait(false))
                    return dest;

                Log(log, "Preparing ffmpeg (one-time download, ~190 MB)...");
                await DownloadAndExtractAsync(dest, log, ct).ConfigureAwait(false);

                if (!await TryVerifyAsync(dest, ct).ConfigureAwait(false))
                    throw new InvalidOperationException(
                        "Downloaded ffmpeg.exe but it failed `-version` check. "
                        + "Delete " + dest + " and retry, or drop a known-good "
                        + "ffmpeg.exe there manually.");

                Log(log, "ffmpeg ready at " + dest);
                return dest;
            }
            finally
            {
                _gate.Release();
            }
        }

        // Synchronous fast-path probe used by callers that only want to
        // know whether ffmpeg is already cached locally (e.g. the
        // upload-side validator that picks between "this is already a
        // WAV, no ffmpeg needed" and "needs transcoding").
        public static bool IsCached(WindrosePaths paths)
        {
            if (paths == null) throw new ArgumentNullException("paths");
            return File.Exists(paths.FfmpegPath);
        }

        static async Task DownloadAndExtractAsync(
            string destExe,
            Action<string> log,
            CancellationToken ct)
        {
            var tempZip = Path.Combine(Path.GetTempPath(),
                "qm_ffmpeg_" + Guid.NewGuid().ToString("N") + ".zip");
            try
            {
                // 1. Download the ZIP.
                Log(log, "Downloading " + DownloadUrl);
                using (var http = new HttpClient())
                {
                    http.Timeout = TimeSpan.FromMinutes(10);
                    // Force a non-default UA - GitHub's CDN is fine with
                    // the empty default, but other proxies / mirrors in
                    // a corporate-network path may 403 it, and the
                    // explicit identification is friendlier in server
                    // logs.
                    http.DefaultRequestHeaders.UserAgent.ParseAdd(
                        "Quartermaster/1.0 (ship-music ffmpeg auto-download)");

                    using (var resp = await http.GetAsync(DownloadUrl,
                        HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false))
                    {
                        resp.EnsureSuccessStatusCode();
                        var totalBytes = resp.Content.Headers.ContentLength ?? -1L;
                        using (var src = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false))
                        using (var dst = new FileStream(tempZip, FileMode.Create, FileAccess.Write, FileShare.None))
                        {
                            await CopyWithProgressAsync(src, dst, totalBytes, log, ct).ConfigureAwait(false);
                        }
                    }
                }

                // 2. Extract just ffmpeg.exe. The BtbN LGPL build ships
                //    as ffmpeg-master-latest-win64-lgpl/bin/ffmpeg.exe
                //    plus docs, presets, ffprobe, ffplay - we want the
                //    one file and nothing else. ZipArchive lets us pick
                //    a single entry without exploding the whole archive
                //    to disk.
                Log(log, "Extracting ffmpeg.exe...");
                var extracted = false;
                using (var zip = ZipFile.OpenRead(tempZip))
                {
                    foreach (var entry in zip.Entries)
                    {
                        // Match by basename so we don't depend on the
                        // version-stamped folder prefix. The archive only
                        // contains one ffmpeg.exe.
                        var name = entry.Name;
                        if (string.Equals(name, "ffmpeg.exe", StringComparison.OrdinalIgnoreCase))
                        {
                            // Atomic-ish write: extract to .tmp, then
                            // File.Move (overwrite) so a partial write
                            // can never leave a half-extracted exe in
                            // place.
                            var tmpExe = destExe + ".tmp-" + Guid.NewGuid().ToString("N");
                            entry.ExtractToFile(tmpExe, overwrite: true);
                            if (File.Exists(destExe))
                            {
                                try { File.Delete(destExe); }
                                catch (Exception ex)
                                {
                                    throw new InvalidOperationException(
                                        "Could not replace existing ffmpeg.exe at "
                                        + destExe + ": " + ex.Message
                                        + " (is another process using it?)", ex);
                                }
                            }
                            File.Move(tmpExe, destExe);
                            extracted = true;
                            break;
                        }
                    }
                }
                if (!extracted)
                    throw new InvalidOperationException(
                        "ffmpeg.exe not found inside the downloaded ZIP. "
                        + "The BtbN build layout may have changed - try a "
                        + "manual install (drop ffmpeg.exe at "
                        + destExe + ").");
            }
            finally
            {
                try { if (File.Exists(tempZip)) File.Delete(tempZip); }
                catch { /* best-effort temp cleanup */ }
            }
        }

        // Streams `src` into `dst` while reporting progress every ~4 MB
        // to the build log. Keeps the user reassured during the ~45-MB
        // download without spamming hundreds of lines.
        static async Task CopyWithProgressAsync(
            Stream src, Stream dst,
            long totalBytes,
            Action<string> log,
            CancellationToken ct)
        {
            const int bufSize = 81920;
            var buf = new byte[bufSize];
            long copied = 0;
            long nextReport = 4L * 1024 * 1024;
            int read;
            while ((read = await src.ReadAsync(buf.AsMemory(0, bufSize), ct).ConfigureAwait(false)) > 0)
            {
                await dst.WriteAsync(buf.AsMemory(0, read), ct).ConfigureAwait(false);
                copied += read;
                if (copied >= nextReport)
                {
                    if (totalBytes > 0)
                    {
                        var pct = (int)Math.Round(100.0 * copied / totalBytes);
                        Log(log, "  " + FormatMb(copied) + " / " + FormatMb(totalBytes)
                              + " (" + pct + "%)");
                    }
                    else
                    {
                        Log(log, "  " + FormatMb(copied) + " downloaded");
                    }
                    nextReport += 4L * 1024 * 1024;
                }
            }
        }

        static string FormatMb(long bytes)
        {
            return (bytes / (1024.0 * 1024.0)).ToString("0.0",
                System.Globalization.CultureInfo.InvariantCulture) + " MB";
        }

        // Runs `ffmpeg -version` and returns true if the process exits 0
        // and stdout starts with "ffmpeg version". Catches all exceptions
        // so a corrupted exe (zero-byte / partial download leftover)
        // returns false instead of bubbling up.
        static async Task<bool> TryVerifyAsync(string exePath, CancellationToken ct)
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = exePath,
                    Arguments = "-version",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                };
                using (var p = Process.Start(psi))
                {
                    if (p == null) return false;
                    // Give it 10s - cold-start of a fresh exe on Windows
                    // can stall briefly while AV scans it.
                    var stdoutTask = p.StandardOutput.ReadToEndAsync(ct);
                    var exitTask = p.WaitForExitAsync(ct);
                    var completed = await Task.WhenAny(exitTask, Task.Delay(TimeSpan.FromSeconds(10), ct))
                        .ConfigureAwait(false);
                    if (completed != exitTask)
                    {
                        try { p.Kill(entireProcessTree: true); } catch { }
                        return false;
                    }
                    if (p.ExitCode != 0) return false;
                    var stdout = await stdoutTask.ConfigureAwait(false);
                    return stdout != null && stdout.StartsWith("ffmpeg version",
                        StringComparison.OrdinalIgnoreCase);
                }
            }
            catch
            {
                return false;
            }
        }

        static void Log(Action<string> log, string msg)
        {
            if (log != null) log(msg);
        }
    }
}
