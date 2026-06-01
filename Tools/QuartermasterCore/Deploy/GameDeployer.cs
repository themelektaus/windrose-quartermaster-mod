using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using Windrose.Quartermaster.Core.BuildingCreator;

namespace Windrose.Quartermaster.Core.Deploy
{
    public sealed class GameDeployer
    {
        public Action<string> Log;

        readonly string _modRoot;
        readonly string _gameWin64Dir;

        public GameDeployer(string modRoot, string gameWin64Dir = null)
        {
            if (string.IsNullOrEmpty(modRoot)) throw new ArgumentNullException("modRoot");
            _modRoot = modRoot;
            _gameWin64Dir = !string.IsNullOrEmpty(gameWin64Dir)
                ? gameWin64Dir
                : SteamLocator.FindBinariesWin64Dir();
        }

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
        // Presence of this marker (not its contents) is the proof we own the adjacent dxgi.dll.
        public string TargetDllMarkerPath() => Path.Combine(_gameWin64Dir, "dxgi.dll.qm");

        bool IsOurProxyAtTarget()
        {
            return File.Exists(TargetDllMarkerPath());
        }

        public string TargetItemsJsonPath(string profileSafeName)
        {
            if (string.IsNullOrEmpty(profileSafeName))
                throw new ArgumentNullException(nameof(profileSafeName));
            return Path.Combine(_gameWin64Dir, "qm_items_" + profileSafeName + ".json");
        }

        public IList<string> EnumerateProfileItemsJsonPaths()
        {
            if (!Directory.Exists(_gameWin64Dir)) return Array.Empty<string>();
            return Directory.GetFiles(_gameWin64Dir, "qm_items_*.json", SearchOption.TopDirectoryOnly);
        }

        public bool EnsureDllInstalled()
        {
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
            var targetMarker = TargetDllMarkerPath();

            if (File.Exists(targetDll) && !IsOurProxyAtTarget())
            {
                throw new InvalidOperationException(
                    "Refusing to overwrite existing dxgi.dll at " + targetDll
                    + " - no dxgi.dll.qm marker alongside, "
                    + "so it's probably not our proxy. "
                    + "Investigate or remove it manually, then retry.");
            }

            LogLine("Copying " + dllSourcePath + " -> " + targetDll);
            File.Copy(dllSourcePath, targetDll, overwrite: true);

            var markerBody =
                "Quartermaster dxgi.dll proxy marker\n"
                + "Installed: " + DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ssZ") + "\n"
                + "Source:    " + dllSourcePath + "\n";
            LogLine("Writing marker -> " + targetMarker);
            File.WriteAllText(targetMarker, markerBody, new UTF8Encoding(false));

            return true;
        }

        // Empty/null buildings deletes the JSON; non-empty overwrites (no merge).
        public void WriteItemsJson(
            string profileSafeName,
            IList<BuildingPatchResult> buildings,
            string tabPurityFilter = "BuildingBrushes")
        {
            if (string.IsNullOrEmpty(profileSafeName))
                throw new ArgumentNullException(nameof(profileSafeName));

            var path = TargetItemsJsonPath(profileSafeName);
            int count = buildings != null ? buildings.Count : 0;
            if (count == 0)
            {
                if (File.Exists(path))
                {
                    LogLine("Removing empty qm_items_" + profileSafeName + ".json -> " + path);
                    File.Delete(path);
                }
                return;
            }
            var body = BuildItemsJson(buildings, tabPurityFilter);
            LogLine("Writing qm_items_" + profileSafeName + ".json (" + count + " building(s)) -> " + path);
            File.WriteAllText(path, body, new UTF8Encoding(false));
        }

        public bool RemoveItemsJson(string profileSafeName)
        {
            if (string.IsNullOrEmpty(profileSafeName))
                throw new ArgumentNullException(nameof(profileSafeName));
            var path = TargetItemsJsonPath(profileSafeName);
            if (!File.Exists(path)) return false;
            LogLine("Removing qm_items_" + profileSafeName + ".json -> " + path);
            File.Delete(path);
            return true;
        }

        public bool RemoveDllIfNoProfilesLeft(Action<string> deleter = null)
        {
            if (!Directory.Exists(_gameWin64Dir)) return false;

            if (EnumerateProfileItemsJsonPaths().Count > 0) return false;

            var targetDll = TargetDllPath();
            var targetMarker = TargetDllMarkerPath();

            bool dllExists = File.Exists(targetDll);
            bool markerExists = File.Exists(targetMarker);

            if (!dllExists && !markerExists) return false;

            if (dllExists && !markerExists)
            {
                LogLine("Skipping DLL cleanup: dxgi.dll exists at " + targetDll
                    + " but no dxgi.dll.qm marker alongside (not our proxy).");
                return false;
            }

            if (deleter == null) deleter = File.Delete;

            bool removedAny = false;
            if (dllExists)
            {
                LogLine("Removing dxgi.dll (no profile JSONs left) -> " + targetDll);
                deleter(targetDll);
                removedAny = true;
            }
            if (markerExists)
            {
                LogLine("Removing dxgi.dll.qm marker -> " + targetMarker);
                deleter(targetMarker);
                removedAny = true;
            }
            return removedAny;
        }

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
                    : WindrosePaths.ModItemsPackagePath + (b.OutputDaStem ?? "");
                var assetName = b.OutputDaStem ?? "";
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

        public CleanupResult CleanupGame(string pakBasename = null)
        {
            var result = new CleanupResult();
            foreach (var jsonPath in EnumerateProfileItemsJsonPaths())
            {
                TryDelete(jsonPath, result);
            }
            TryDelete(TargetDllPath(),       result);
            TryDelete(TargetDllMarkerPath(), result);
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
