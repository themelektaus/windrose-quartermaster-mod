using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Windrose.Quartermaster.Core
{
    // Probes the mod root for the artifacts the GUI needs at runtime
    // (vanilla JSONs + icons + .usmap + repak + IconExtractor build) and
    // can run the missing pieces end-to-end with a single Run() call.
    //
    // The Log callback receives every line of progress so the GUI can
    // forward them over SSE without buffering. Each step is also
    // bracketed with [step:start name=...] / [step:end name=... ok=...]
    // markers so the frontend can render them as collapsible sections
    // without parsing free-form text.
    //
    // Which vanilla sources count as "required" is declared in
    // VanillaSourceManifest. Adding a new feature that needs a new pak
    // subtree extracted = append one manifest entry. The probe + the
    // dumper + the setup overlay all pick it up automatically, so users
    // upgrading from an older Quartermaster version (whose Sources/Vanilla/
    // predates the new entry) will see the setup overlay re-open
    // automatically on next boot until they top up the missing subtree.
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

            // Iterate the manifest - the canonical list of "what we need
            // on disk". One entry per required vanilla subtree / file.
            // If a future Quartermaster version adds a new requirement,
            // every existing user's Sources/Vanilla/ probes as not-ready
            // here, which kicks IsReady to false, which makes boot.js
            // open the setup overlay automatically. They run setup, the
            // dumper tops up just the missing entry, done.
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

            // Icons: at least one .png present is good enough - a partial
            // run from a previous failed extraction shouldn't trigger a
            // re-run, but we'll surface it via the count.
            var iconsDir = Path.Combine(_paths.ModRoot, "Icons");
            status.IconsDir = iconsDir;
            status.HasIcons = Directory.Exists(iconsDir) &&
                Directory.EnumerateFiles(iconsDir, "*.png", SearchOption.TopDirectoryOnly).Any();

            string usmap;
            status.HasUsmap = UsmapLocator.TryFind(_paths.ModRoot, out usmap);
            status.UsmapPath = usmap;

            status.HasRepak = File.Exists(Path.Combine(_paths.ModRoot, "repak.exe"));
            // IconExtractor used to be a sibling EXE under
            // Tools/IconExtractor/publish/; it's now a library linked into
            // QuartermasterCore directly, so "is the tool present" is
            // tautologically true as long as the host is running.
            status.HasIconExtractor = true;

            // ffmpeg is the audio transcoder used by the ship-music tab to
            // convert mp3 / ogg / flac / m4a / aac / opus uploads to the
            // 44.1 kHz stereo 16-bit PCM WAV the Bink encoder accepts.
            // It's a ~190 MB one-time auto-download (BtbN LGPL build)
            // and lives at <ModRoot>/ffmpeg.exe. Optional: a user who only
            // uploads correctly-formatted WAVs never needs it, so absence
            // does NOT block IsReady (we don't want the setup overlay to
            // re-open just because ffmpeg is missing). But we surface it
            // as a setup row so first-time users can have it staged
            // ahead-of-time in one click.
            status.FfmpegPath = _paths.FfmpegPath;
            status.HasFfmpeg = FfmpegResolver.IsCached(_paths);

            // Steam-detected vanilla pak - don't throw, just report.
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

        // Runs the dump + icon extraction - whichever steps are missing
        // (or always, when ForceAll is set). Throws on the first failing
        // step so the SSE handler can surface it to the user.
        public bool ForceAll;

        public void Run()
        {
            var status = Probe();

            // The icon pipeline depends on the vanilla pak being present
            // (CUE4Parse mounts it). If Steam can't find it we have to
            // bail upfront - nothing we can do without that file.
            if (!status.HasVanillaPak && (ForceAll || !status.HasVanillaSources || !status.HasIcons))
            {
                throw new InvalidOperationException(
                    "Cannot run setup: " + status.VanillaPakError +
                    "\nInstall Windrose via Steam, or extract the JSONs / icons manually.");
            }

            // Dump step - needed when Sources/Vanilla is empty OR partial
            // (any manifest entry probes false), or when ForceAll is set.
            // Partial-Sources is the upgrade case: an older Quartermaster
            // dumped 3 of 6 subtrees, the new version added 3 more, the
            // user re-runs setup and the dumper tops up the missing ones.
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

            // Icons step - needs the .usmap, so probe again after the
            // dump (the dump never produces a usmap, but the user may
            // have dropped one in between Probe() and Run()).
            string usmap;
            if (!UsmapLocator.TryFind(_paths.ModRoot, out usmap))
            {
                throw new InvalidOperationException(
                    "Setup needs a .usmap file in " + _paths.ModRoot + ".\n\n" +
                    "Generate one with UE4SS' built-in dumper:\n" +
                    "  1. Start Windrose, load a save, walk around for 5-10 seconds.\n" +
                    "  2. Press Ctrl+Num6 (UE4SS Keybinds mod -> DumpUSMAP).\n" +
                    "  3. Copy the produced .usmap into " + _paths.ModRoot + ".\n\n" +
                    "Then click Re-run setup.");
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

            // ffmpeg step - downloads the ~190 MB portable build from
            // BtbN's GitHub releases to <ModRoot>/ffmpeg.exe. Pulled into setup so users
            // don't get a surprise download when they upload their first
            // mp3 in the ship-music tab. Failure here is logged but
            // doesn't abort setup: vanilla extraction + icons are the
            // hard prerequisites for everything else, ffmpeg is only
            // needed for non-WAV ship-music uploads. If the download
            // fails (offline / firewall), the user can either drop a
            // ffmpeg.exe manually at the workspace root or just stick
            // to WAV uploads.
            if (ForceAll || !status.HasFfmpeg)
            {
                StepStart("ffmpeg", "Downloading ffmpeg.exe (portable, for ship-music transcoding)");
                try
                {
                    // Block on the async resolver - we're already on a
                    // worker thread (SetupEndpoint funnels Run() into
                    // Task.Run) so GetAwaiter().GetResult() is fine and
                    // there's no SynchronizationContext to deadlock on.
                    FfmpegResolver.ResolveAsync(_paths, Log).GetAwaiter().GetResult();
                    StepEnd("ffmpeg", true, null);
                }
                catch (Exception ex)
                {
                    // Soft failure: log + mark step as failed, but don't
                    // throw. The configurator stays usable; user just
                    // can't upload mp3 / ogg / etc. until ffmpeg is
                    // present.
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
        public bool IsReady;          // both Sources + Icons populated
        public bool HasVanillaSources;        // every manifest entry probes Ok
        // Per-manifest-entry status, populated by Probe() in declaration
        // order. The setup overlay renders one row per entry so the user
        // sees exactly which vanilla subtree (or single file) is missing.
        public List<VanillaSourceStatus> Sources;
        public bool HasIcons;
        public string IconsDir;
        public bool HasUsmap;
        public string UsmapPath;
        public bool HasRepak;
        public bool HasIconExtractor;
        public bool HasVanillaPak;
        public string VanillaPakPath;
        public string VanillaPakError;
        // Portable ffmpeg.exe for ship-music transcoding (mp3/ogg/flac/
        // m4a/aac/opus -> WAV). Optional - WAV-only users never need it.
        // The setup overlay surfaces a check row so users can stage the
        // download up-front, but absence does NOT block IsReady.
        public bool HasFfmpeg;
        public string FfmpegPath;
    }

    // One probe result, mirroring a VanillaSourceManifestEntry. The fields
    // are carried into the /api/setup/status JSON response so the frontend
    // can render rows without re-knowing the manifest shape.
    public sealed class VanillaSourceStatus
    {
        public string Key;
        public string Label;
        public string Description;
        public string DiskPath;
        public bool Ok;
    }
}
