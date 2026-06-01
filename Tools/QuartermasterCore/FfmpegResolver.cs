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
    public static class FfmpegResolver
    {
        const string DownloadUrl =
            "https://github.com/BtbN/FFmpeg-Builds/releases/download/latest/ffmpeg-master-latest-win64-lgpl.zip";

        static readonly SemaphoreSlim _gate = new SemaphoreSlim(1, 1);

        public static async Task<string> ResolveAsync(
            WindrosePaths paths,
            Action<string> log,
            CancellationToken ct = default)
        {
            if (paths == null) throw new ArgumentNullException("paths");
            var dest = paths.FfmpegPath;

            if (File.Exists(dest) && await TryVerifyAsync(dest, ct).ConfigureAwait(false))
                return dest;

            await _gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
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
                Log(log, "Downloading " + DownloadUrl);
                using (var http = new HttpClient())
                {
                    http.Timeout = TimeSpan.FromMinutes(10);
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

                Log(log, "Extracting ffmpeg.exe...");
                var extracted = false;
                using (var zip = ZipFile.OpenRead(tempZip))
                {
                    foreach (var entry in zip.Entries)
                    {
                        var name = entry.Name;
                        if (string.Equals(name, "ffmpeg.exe", StringComparison.OrdinalIgnoreCase))
                        {
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
                catch { }
            }
        }

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
