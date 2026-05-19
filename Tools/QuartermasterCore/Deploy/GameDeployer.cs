using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Windrose.Quartermaster.Core.BuildingCreator;

namespace Windrose.Quartermaster.Core.Deploy
{
    // Owns writes into the live game's <R5>/Binaries/Win64 folder:
    //
    //   1) dxgi.dll        - the inject-proxy our DLL hook lives in
    //   2) dxgi_org.dll    - the renamed copy of C:\Windows\System32\dxgi.dll
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
    // unless we can prove it's our proxy (dxgi_org.dll alongside). This
    // matches the deploy.bat guard.
    public sealed class GameDeployer
    {
        public Action<string> Log;

        // <Mod>/Tools/DllProxy/dxgi/dxgi.dll - the freshly built proxy we
        // copy to the game. Dev workflow: the user runs build.bat once.
        // Ship workflow (later): bundled with the installed GUI.
        readonly string _dllSourcePath;

        // <Game>/R5/Binaries/Win64/ - target for all three files we own.
        readonly string _gameWin64Dir;

        public GameDeployer(string modRoot, string gameWin64Dir = null)
        {
            if (string.IsNullOrEmpty(modRoot)) throw new ArgumentNullException("modRoot");
            _dllSourcePath = Path.Combine(modRoot, "Tools", "DllProxy", "dxgi", "dxgi.dll");
            _gameWin64Dir = !string.IsNullOrEmpty(gameWin64Dir)
                ? gameWin64Dir
                : SteamLocator.FindBinariesWin64Dir();
        }

        public string DllSourcePath => _dllSourcePath;
        public string GameWin64Dir  => _gameWin64Dir;

        public string TargetDllPath()      => Path.Combine(_gameWin64Dir, "dxgi.dll");
        public string TargetDllOrgPath()   => Path.Combine(_gameWin64Dir, "dxgi_org.dll");
        public string TargetItemsJsonPath() => Path.Combine(_gameWin64Dir, "qm_items.json");

        // Idempotent install of dxgi.dll + dxgi_org.dll. Returns true on
        // success, throws InvalidOperationException if the guard refuses
        // (= an unknown dxgi.dll is already there without our renamer
        // alongside). The latter never recovers automatically: user has
        // to investigate / remove the foreign file manually.
        //
        // Always re-copies our dxgi.dll over an existing proxy to ensure
        // the deployed binary matches the current build - we don't want
        // the game running against a stale DLL if the user rebuilt but
        // didn't redeploy.
        public bool EnsureDllInstalled()
        {
            if (!File.Exists(_dllSourcePath))
            {
                throw new InvalidOperationException(
                    "dxgi.dll source not found at " + _dllSourcePath
                    + " - build it first via Tools/DllProxy/dxgi/build.bat.");
            }

            var targetDll = TargetDllPath();
            var targetOrg = TargetDllOrgPath();

            // Guard: refuse to overwrite an unknown dxgi.dll (could be the
            // game's own shipped DLL, or another mod's proxy). Only our
            // own deploys leave a dxgi_org.dll alongside.
            if (File.Exists(targetDll) && !File.Exists(targetOrg))
            {
                throw new InvalidOperationException(
                    "Refusing to overwrite existing dxgi.dll at " + targetDll
                    + " - no dxgi_org.dll alongside, so it's probably not our proxy. "
                    + "Investigate or remove it manually, then retry.");
            }

            // Renamer: copy the Windows system dxgi.dll to dxgi_org.dll
            // (only if not present yet - we never replace it once it's
            // there, the system DLL never changes meaningfully).
            if (!File.Exists(targetOrg))
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
                LogLine("Copying " + sysDxgi + " -> " + targetOrg);
                File.Copy(sysDxgi, targetOrg, overwrite: false);
            }

            // Proxy: always overwrite so users running an older deployed
            // build pick up the latest after a rebuild + click Build.
            LogLine("Copying " + _dllSourcePath + " -> " + targetDll);
            File.Copy(_dllSourcePath, targetDll, overwrite: true);

            return true;
        }

        // Writes qm_items.json next to the DLL with the given buildings.
        // Empty list = writes a JSON with empty items array; DLL goes idle
        // (Etappe A.1). Always overwrites the target file - no merge.
        //
        // tabPurityFilter defaults to "BuildingDecoration" for the current
        // Painting template; later when we have multiple templates spanning
        // different categories we'll derive this from the buildings list.
        public void WriteItemsJson(
            IList<BuildingPatchResult> buildings,
            string tabPurityFilter = "BuildingDecoration")
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

        // Full uninstall: remove dxgi.dll, dxgi_org.dll, qm_items.json,
        // and (optionally) the pak triple. Idempotent - missing files are
        // silently skipped so the caller can run this safely on a partial
        // install. The pak triple removal is opt-in because the pak might
        // be shared with other Quartermaster features (loot, items, etc.).
        public CleanupResult CleanupGame(string pakBasename = null)
        {
            var result = new CleanupResult();
            TryDelete(TargetItemsJsonPath(), result);
            TryDelete(TargetDllPath(),       result);
            TryDelete(TargetDllOrgPath(),    result);
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
