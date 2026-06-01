using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Windrose.Quartermaster.Core
{
    public sealed class SetupRunner
    {
        readonly WindrosePaths _paths;

        public SetupRunner(WindrosePaths paths)
        {
            if (paths == null) throw new ArgumentNullException("paths");
            _paths = paths;
        }

        public Action<string> Log;

        public SetupStatus Probe()
        {
            var status = new SetupStatus();

            status.Sources = new List<VanillaSourceStatus>(VanillaSourceManifest.Entries.Length);
            var allOk = true;
            foreach (var entry in VanillaSourceManifest.Entries)
            {
                var ok = VanillaSourceManifest.Probe(entry, _paths);
                status.Sources.Add(new VanillaSourceStatus
                {
                    Key = entry.Key,
                    Label = entry.Label,
                    Description = entry.Description,
                    DiskPath = entry.DiskPath(_paths),
                    Ok = ok,
                });
                if (!ok) allOk = false;
            }
            status.HasVanillaSources = allOk;

            var iconsDir = Path.Combine(_paths.ModRoot, "Icons");
            status.IconsDir = iconsDir;
            status.HasIcons = Directory.Exists(iconsDir) &&
                Directory.EnumerateFiles(iconsDir, "*.png", SearchOption.TopDirectoryOnly).Any();

            string usmap;
            status.HasUsmap = UsmapLocator.TryFind(_paths.ModRoot, out usmap);
            status.UsmapPath = usmap;
            status.UsmapHint = status.HasUsmap ? null : UsmapLocator.MissingMessage(_paths.ModRoot);

            status.HasRepak = File.Exists(Path.Combine(_paths.ModRoot, "repak.exe"));
            status.HasIconExtractor = true;

            status.FfmpegPath = _paths.FfmpegPath;
            status.HasFfmpeg = FfmpegResolver.IsCached(_paths);

            try
            {
                status.VanillaPakPath = SteamLocator.FindVanillaPak();
                status.HasVanillaPak = true;
            }
            catch (Exception ex)
            {
                status.HasVanillaPak = false;
                status.VanillaPakError = ex.Message;
            }

            status.IsReady = status.HasVanillaSources && status.HasIcons;
            return status;
        }

        public bool ForceAll;

        public void Run()
        {
            var status = Probe();

            if (!status.HasVanillaPak && (ForceAll || !status.HasVanillaSources || !status.HasIcons))
            {
                throw new InvalidOperationException(
                    "Cannot run setup: " + status.VanillaPakError +
                    "\nInstall Windrose via Steam, or extract the JSONs / icons manually.");
            }

            if (ForceAll || !status.HasVanillaSources)
            {
                StepStart("dump", "Extracting vanilla item JSONs from the game pak");
                try
                {
                    var dumper = new VanillaDumper(_paths) { Log = Log };
                    dumper.Run();
                }
                catch (Exception ex)
                {
                    StepEnd("dump", false, ex.Message);
                    throw;
                }
                StepEnd("dump", true, null);
            }
            else
            {
                LogLine("[skip] Vanilla JSONs already present (" + _paths.Vanilla + ")");
            }

            // Re-probe for the usmap after the dump: the user may have dropped one
            // in between Probe() and Run().
            string usmap;
            if (!UsmapLocator.TryFind(_paths.ModRoot, out usmap))
            {
                throw new InvalidOperationException(
                    UsmapLocator.MissingMessage(_paths.ModRoot) + "\n\nThen click Re-run setup.");
            }

            if (ForceAll || !status.HasIcons)
            {
                StepStart("icons", "Extracting item icons + localized metadata");
                try
                {
                    var runner = new IconExtractionRunner(_paths) { Log = Log };
                    runner.Run();
                }
                catch (Exception ex)
                {
                    StepEnd("icons", false, ex.Message);
                    throw;
                }
                StepEnd("icons", true, null);
            }
            else
            {
                LogLine("[skip] Icons already present (" + status.IconsDir + ")");
            }

            if (ForceAll || !status.HasFfmpeg)
            {
                StepStart("ffmpeg", "Downloading ffmpeg.exe (portable, for ship-music transcoding)");
                try
                {
                    FfmpegResolver.ResolveAsync(_paths, Log).GetAwaiter().GetResult();
                    StepEnd("ffmpeg", true, null);
                }
                catch (Exception ex)
                {
                    // Soft failure: ffmpeg is only needed for non-WAV uploads, so log
                    // and mark the step failed without aborting setup.
                    LogLine("[!] ffmpeg download failed: " + ex.Message);
                    LogLine("[!] You can still upload .wav files in the ship-music tab.");
                    LogLine("[!] To enable mp3 / ogg / flac / m4a / aac / opus, drop an ffmpeg.exe at " + _paths.FfmpegPath + " or re-run setup with internet access.");
                    StepEnd("ffmpeg", false, ex.Message);
                }
            }
            else
            {
                LogLine("[skip] ffmpeg already present (" + _paths.FfmpegPath + ")");
            }
        }

        void StepStart(string name, string description)
        {
            LogLine("[step:start name=" + name + "] " + description);
        }
        void StepEnd(string name, bool ok, string error)
        {
            if (ok) LogLine("[step:end name=" + name + " ok=true]");
            else    LogLine("[step:end name=" + name + " ok=false] " + (error ?? string.Empty));
        }
        void LogLine(string msg)
        {
            if (Log != null) Log(msg);
        }
    }

    public sealed class SetupStatus
    {
        // True only when both Sources and Icons are populated; usmap/ffmpeg do NOT gate it.
        public bool IsReady;
        public bool HasVanillaSources;
        public List<VanillaSourceStatus> Sources;
        public bool HasIcons;
        public string IconsDir;
        public bool HasUsmap;
        public string UsmapPath;
        public string UsmapHint;
        public bool HasRepak;
        public bool HasIconExtractor;
        public bool HasVanillaPak;
        public string VanillaPakPath;
        public string VanillaPakError;
        public bool HasFfmpeg;
        public string FfmpegPath;
    }

    public sealed class VanillaSourceStatus
    {
        public string Key;
        public string Label;
        public string Description;
        public string DiskPath;
        public bool Ok;
    }
}
