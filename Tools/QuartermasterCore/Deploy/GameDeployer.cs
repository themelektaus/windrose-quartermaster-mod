using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using Windrose.Quartermaster.Core.BuildingCreator;

namespace Windrose.Quartermaster.Core.Deploy
{
    // Owns writes into the live game's <R5>/Binaries/Win64 folder:
    //
    //   1) dxgi.dll        - the inject-proxy our DLL hook lives in
    //   2) dxgi_original.dll    - the renamed copy of C:\Windows\System32\dxgi.dll
    //                        we PE-forward to (required for the proxy to work)
    //   3) qm_items.json   - the runtime config the DLL reads at startup
    //
    // The pak triple (Quartermaster_<name>_P.{pak,ucas,utoc}) is NOT this
    // class's job - it gets shipped via BuildPipeline.OutputDir directly
    // into ~mods/. We only touch the Win64 dir here.
    //
    // Lifecycle (per Variant C - PENDING.md design point #13):
    //   - DLL stays permanently in Win64 once deployed (no per-build copy
    //     if already there; idempotent install).
    //   - qm_items.json is re-written every build to reflect the current
    //     profile's CustomBuildings. If buildings.Count == 0 we write an
    //     empty items array, which the DLL recognises and goes idle
    //     (Etappe A.1).
    //   - CleanupGame() is the explicit one-shot uninstall path (user
    //     opt-in, not auto-triggered by 'all buildings removed').
    //
    // Guard against clobbering: we never overwrite a pre-existing dxgi.dll
    // unless we can prove it's our proxy (dxgi_original.dll alongside). This
    // matches the deploy.bat guard.
    public sealed class GameDeployer
    {
        public Action<string> Log;

        // Source path probed at install time. Two locations in priority
        // order:
        //   1) <ModRoot>/Tools/DllProxy/dxgi/dxgi.dll - dev tree, freshly
        //      built via Tools/DllProxy/dxgi/build.bat. Wins when present
        //      so dev iteration always deploys the latest build.
        //   2) <ModRoot>/dxgi.dll - the seeded copy of the embedded
        //      resource Program.SeedDxgiDllIfMissing wrote on first
        //      launch (deployed EXEs only). Used by end users who
        //      installed the published EXE bundle and don't have the
        //      DllProxy source tree alongside.
        // EnsureDllInstalled() re-resolves at call time so the picked path
        // appears in the build log honestly.
        readonly string _modRoot;

        // <Game>/R5/Binaries/Win64/ - target for all three files we own.
        readonly string _gameWin64Dir;

        public GameDeployer(string modRoot, string gameWin64Dir = null)
        {
            if (string.IsNullOrEmpty(modRoot)) throw new ArgumentNullException("modRoot");
            _modRoot = modRoot;
            _gameWin64Dir = !string.IsNullOrEmpty(gameWin64Dir)
                ? gameWin64Dir
                : SteamLocator.FindBinariesWin64Dir();
        }

        // Probes the two known source locations and returns the first one
        // that exists, or the dev path (= the more informative error
        // target) if neither is present.
        string ResolveDllSourcePath()
        {
            var devPath = Path.Combine(_modRoot, "Tools", "DllProxy", "dxgi", "dxgi.dll");
            if (File.Exists(devPath)) return devPath;
            var seededPath = Path.Combine(_modRoot, "dxgi.dll");
            if (File.Exists(seededPath)) return seededPath;
            return devPath;
        }

        public string DllSourcePath => ResolveDllSourcePath();
        public string GameWin64Dir  => _gameWin64Dir;

        public string TargetDllPath()      => Path.Combine(_gameWin64Dir, "dxgi.dll");
        public string TargetDllOriginalPath()   => Path.Combine(_gameWin64Dir, "dxgi_original.dll");
        public string TargetItemsJsonPath() => Path.Combine(_gameWin64Dir, "qm_items.json");

        // Idempotent install of dxgi.dll + dxgi_original.dll. Returns true on
        // success, false when the platform doesn't support the inject (Linux
        // / Steam Deck - see below), throws InvalidOperationException if the
        // guard refuses (= an unknown dxgi.dll is already there without our
        // renamer alongside). The latter never recovers automatically: user
        // has to investigate / remove the foreign file manually.
        //
        // Always re-copies our dxgi.dll over an existing proxy to ensure
        // the deployed binary matches the current build - we don't want
        // the game running against a stale DLL if the user rebuilt but
        // didn't redeploy.
        public bool EnsureDllInstalled()
        {
            // Platform gate: the dxgi-proxy inject is a Windows-only PE
            // mechanism. Under Proton/Wine the renamer DLL would have to
            // come from a Wine-provided dxgi.dll (compatdata prefix or
            // Proton install) - not implemented yet. Build still produces
            // the pak (Stack/Loot/Recipes/Trade work via the pak alone);
            // only the inject-driven features (Custom Items + Buildings
            // into Vorgefertigte Strukturen tab) are skipped. Loud log
            // so the user understands why their painting doesn't appear.
            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                LogLine("DLL inject SKIPPED: dxgi-proxy is Windows-only "
                    + "(running on " + RuntimeInformation.OSDescription + "). "
                    + "Pak-based features (Stacks, Loot, Recipes, Trade) work via the pak alone; "
                    + "Custom Items / Buildings in the build menu need the dxgi.dll inject "
                    + "and won't appear in-game until Steam Deck support is added.");
                return false;
            }

            var dllSourcePath = ResolveDllSourcePath();
            if (!File.Exists(dllSourcePath))
            {
                throw new InvalidOperationException(
                    "dxgi.dll source not found at " + dllSourcePath
                    + " (and no seeded fallback at " + Path.Combine(_modRoot, "dxgi.dll") + ")"
                    + " - dev: build it via Tools/DllProxy/dxgi/build.bat;"
                    + " deployed: relaunch the EXE so the embedded copy seeds.");
            }

            var targetDll = TargetDllPath();
            var targetOriginal = TargetDllOriginalPath();

            // Guard: refuse to overwrite an unknown dxgi.dll (could be the
            // game's own shipped DLL, or another mod's proxy). Only our
            // own deploys leave a dxgi_original.dll alongside.
            if (File.Exists(targetDll) && !File.Exists(targetOriginal))
            {
                throw new InvalidOperationException(
                    "Refusing to overwrite existing dxgi.dll at " + targetDll
                    + " - no dxgi_original.dll alongside, so it's probably not our proxy. "
                    + "Investigate or remove it manually, then retry.");
            }

            // Renamer: copy the Windows system dxgi.dll to dxgi_original.dll
            // (only if not present yet - we never replace it once it's
            // there, the system DLL never changes meaningfully).
            if (!File.Exists(targetOriginal))
            {
                var sysDxgi = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.System),
                    "dxgi.dll");
                if (!File.Exists(sysDxgi))
                {
                    throw new InvalidOperationException(
                        "System dxgi.dll not found at " + sysDxgi
                        + " - cannot create the renamer.");
                }
                LogLine("Copying " + sysDxgi + " -> " + targetOriginal);
                File.Copy(sysDxgi, targetOriginal, overwrite: false);
            }

            // Proxy: always overwrite so users running an older deployed
            // build pick up the latest after a rebuild + click Build.
            LogLine("Copying " + dllSourcePath + " -> " + targetDll);
            File.Copy(dllSourcePath, targetDll, overwrite: true);

            return true;
        }

        // Writes qm_items.json next to the DLL with the given buildings.
        // Empty list = writes a JSON with empty items array; DLL goes idle
        // (Etappe A.1). Always overwrites the target file - no merge.
        //
        // tabPurityFilter is a substring matched against the first item's
        // package path in each group of the resolved tab. ALL groups in the
        // tab must match for the inject to fire (purity-gate).
        //
        // Default "BuildingBrushes" routes every custom building to the
        // "Vorgefertigte Strukturen" tab (last tab in the build menu),
        // regardless of which vanilla template was cloned. The tab contains
        // both /BuildingBrushes/* and /Houses/* but the only observed group
        // there has its first item in /BuildingBrushes/* - so this substring
        // is enough to identify the tab. If we ever see Houses items in
        // their own group we'd need a broader filter (e.g. a tag probe).
        public void WriteItemsJson(
            IList<BuildingPatchResult> buildings,
            string tabPurityFilter = "BuildingBrushes")
        {
            var path = TargetItemsJsonPath();
            var body = BuildItemsJson(buildings, tabPurityFilter);
            LogLine("Writing qm_items.json (" + (buildings != null ? buildings.Count : 0)
                    + " building(s)) -> " + path);
            File.WriteAllText(path, body, new UTF8Encoding(false));
        }

        // Pure builder so tests/inspection can verify the wire format
        // without writing to disk.
        public static string BuildItemsJson(
            IList<BuildingPatchResult> buildings,
            string tabPurityFilter)
        {
            var sb = new StringBuilder();
            sb.Append("{\n");
            sb.Append("  \"tabPurityFilter\": \"")
              .Append(EscapeJsonString(tabPurityFilter ?? ""))
              .Append("\",\n");
            sb.Append("  \"items\": [");

            int n = buildings != null ? buildings.Count : 0;
            for (int i = 0; i < n; ++i)
            {
                var b = buildings[i];
                var packagePath = !string.IsNullOrEmpty(b.OutputDaPath)
                    ? b.OutputDaPath
                    : "/Game/Quartermaster/Items/" + (b.OutputDaStem ?? "");
                var assetName = b.OutputDaStem ?? "";
                // R5BuildingItem is the donor class our inject pipeline
                // expects. We don't yet have a template-driven class but
                // keep the field per InjectableItem schema for forward
                // compat.
                sb.Append(i == 0 ? "\n" : ",\n");
                sb.Append("    {\n");
                sb.Append("      \"name\":                    \"")
                  .Append(EscapeJsonString(b.BuildingId ?? "")).Append("\",\n");
                sb.Append("      \"className\":               \"R5BuildingItem\",\n");
                sb.Append("      \"assetName\":               \"")
                  .Append(EscapeJsonString(assetName)).Append("\",\n");
                sb.Append("      \"packagePath\":             \"")
                  .Append(EscapeJsonString(packagePath)).Append("\",\n");
                sb.Append("      \"targetCategorySubstring\": \"")
                  .Append(EscapeJsonString(tabPurityFilter ?? "")).Append("\"\n");
                sb.Append("    }");
            }
            if (n > 0) sb.Append("\n  ");
            sb.Append("]\n");
            sb.Append("}\n");
            return sb.ToString();
        }

        // Full uninstall: remove dxgi.dll, dxgi_original.dll, qm_items.json,
        // and (optionally) the pak triple. Idempotent - missing files are
        // silently skipped so the caller can run this safely on a partial
        // install. The pak triple removal is opt-in because the pak might
        // be shared with other Quartermaster features (loot, items, etc.).
        public CleanupResult CleanupGame(string pakBasename = null)
        {
            var result = new CleanupResult();
            TryDelete(TargetItemsJsonPath(), result);
            TryDelete(TargetDllPath(),       result);
            TryDelete(TargetDllOriginalPath(),    result);
            if (!string.IsNullOrEmpty(pakBasename))
            {
                string modsDir;
                try { modsDir = SteamLocator.FindModsDir(); }
                catch (Exception ex)
                {
                    result.Errors.Add("Cannot locate ~mods dir for pak cleanup: " + ex.Message);
                    return result;
                }
                foreach (var ext in new[] { ".pak", ".ucas", ".utoc" })
                {
                    TryDelete(Path.Combine(modsDir, pakBasename + ext), result);
                }
            }
            return result;
        }

        // ----------------------------------------------------------------
        // Helpers
        // ----------------------------------------------------------------

        void LogLine(string m) { if (Log != null) Log(m); }

        void TryDelete(string path, CleanupResult result)
        {
            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                    result.Removed.Add(path);
                    LogLine("Removed " + path);
                }
                else
                {
                    result.Missing.Add(path);
                }
            }
            catch (Exception ex)
            {
                result.Errors.Add(path + ": " + ex.Message);
            }
        }

        static string EscapeJsonString(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            var sb = new StringBuilder(s.Length);
            foreach (var c in s)
            {
                switch (c)
                {
                    case '"':  sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\b': sb.Append("\\b");  break;
                    case '\f': sb.Append("\\f");  break;
                    case '\n': sb.Append("\\n");  break;
                    case '\r': sb.Append("\\r");  break;
                    case '\t': sb.Append("\\t");  break;
                    default:
                        if (c < 0x20) sb.AppendFormat("\\u{0:X4}", (int)c);
                        else          sb.Append(c);
                        break;
                }
            }
            return sb.ToString();
        }

        public sealed class CleanupResult
        {
            public List<string> Removed = new List<string>();
            public List<string> Missing = new List<string>();
            public List<string> Errors  = new List<string>();
        }
    }
}
