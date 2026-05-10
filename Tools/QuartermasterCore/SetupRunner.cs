using System;
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

            // Both subtrees (InventoryItems + LootTables) must be present;
            // a half-extracted Vanilla/ from a previous run shouldn't be
            // treated as ready.
            status.HasVanillaInventoryItems =
                Directory.Exists(_paths.VanillaInventoryItems) &&
                Directory.EnumerateFiles(_paths.VanillaInventoryItems, "*.json", SearchOption.AllDirectories).Any();
            status.HasVanillaLootTables =
                Directory.Exists(_paths.VanillaLootTables) &&
                Directory.EnumerateFiles(_paths.VanillaLootTables, "*.json", SearchOption.AllDirectories).Any();
            status.HasVanillaSources = status.HasVanillaInventoryItems && status.HasVanillaLootTables;

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
            status.HasIconExtractor = File.Exists(Path.Combine(
                _paths.ModRoot, "Tools", "IconExtractor", "publish", "IconExtractor.exe"));

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

            // Dump step - needed when Sources/Vanilla is empty, or when
            // ForceAll is set.
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
        public bool HasVanillaSources;        // = InventoryItems && LootTables
        public bool HasVanillaInventoryItems;
        public bool HasVanillaLootTables;
        public bool HasIcons;
        public string IconsDir;
        public bool HasUsmap;
        public string UsmapPath;
        public bool HasRepak;
        public bool HasIconExtractor;
        public bool HasVanillaPak;
        public string VanillaPakPath;
        public string VanillaPakError;
    }
}
