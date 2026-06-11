using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Windrose.Quartermaster.Core.BuildingCreator;

namespace Windrose.Quartermaster.Core.Deploy
{
    public sealed class GameDeployer
    {
        public Action<string> Log;

        readonly string _modRoot;
        readonly string _gameWin64Dir;
        // All qm_* sidecars (sentinels, configs, profile JSONs) live in this
        // subfolder. Only dxgi.dll stays in the Win64 root: the proxy is
        // only picked up by the loader directly next to the EXE.
        readonly string _sidecarDir;

        public GameDeployer(string modRoot, string gameWin64Dir = null)
        {
            if (string.IsNullOrEmpty(modRoot)) throw new ArgumentNullException("modRoot");
            _modRoot = modRoot;
            _gameWin64Dir = !string.IsNullOrEmpty(gameWin64Dir)
                ? gameWin64Dir
                : SteamLocator.FindBinariesWin64Dir();
            _sidecarDir = Path.Combine(_gameWin64Dir, "Quartermaster");
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
        public string SidecarDir    => _sidecarDir;

        public string TargetDllPath()      => Path.Combine(_gameWin64Dir, "dxgi.dll");

        // Ownership proof lives inside the DLL itself: the PE version resource
        // (Tools/DllProxy/dxgi/version.rc) carries this ProductName. The old
        // dxgi.dll.qm sidecar marker is only honored as a legacy fallback for
        // proxies deployed before the resource existed, and removed on sight.
        const string DllProductName = "Quartermaster";
        string LegacyDllMarkerPath() => Path.Combine(_gameWin64Dir, "dxgi.dll.qm");

        static bool IsQuartermasterDll(string dllPath)
        {
            try
            {
                return string.Equals(
                    FileVersionInfo.GetVersionInfo(dllPath).ProductName,
                    DllProductName, StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }

        bool IsOurProxyAtTarget()
        {
            var targetDll = TargetDllPath();
            if (File.Exists(targetDll) && IsQuartermasterDll(targetDll)) return true;
            return File.Exists(LegacyDllMarkerPath());
        }

        void RemoveLegacyDllMarker()
        {
            var marker = LegacyDllMarkerPath();
            if (!File.Exists(marker)) return;
            try
            {
                File.Delete(marker);
                LogLine("Removed legacy dxgi.dll.qm marker (ownership is embedded in the DLL now).");
            }
            catch (Exception ex)
            {
                LogLine("WARNING: could not remove legacy marker " + marker + ": " + ex.Message);
            }
        }

        public string TargetItemsJsonPath(string profileSafeName)
        {
            if (string.IsNullOrEmpty(profileSafeName))
                throw new ArgumentNullException(nameof(profileSafeName));
            return Path.Combine(_sidecarDir, "qm_items_" + profileSafeName + ".json");
        }

        public IList<string> EnumerateProfileItemsJsonPaths()
        {
            if (!Directory.Exists(_sidecarDir)) return Array.Empty<string>();
            return Directory.GetFiles(_sidecarDir, "qm_items_*.json", SearchOption.TopDirectoryOnly);
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

            if (File.Exists(targetDll) && !IsOurProxyAtTarget())
            {
                throw new InvalidOperationException(
                    "Refusing to overwrite existing dxgi.dll at " + targetDll
                    + " - its version resource does not identify it as Quartermaster, "
                    + "so it's probably a foreign proxy (ReShade etc.). "
                    + "Investigate or remove it manually, then retry.");
            }

            LogLine("Copying " + dllSourcePath + " -> " + targetDll);
            File.Copy(dllSourcePath, targetDll, overwrite: true);

            RemoveLegacyDllMarker();

            MigrateLegacySidecars();

            return true;
        }

        void EnsureSidecarDir() => Directory.CreateDirectory(_sidecarDir);

        // Sidecars used to live directly in Win64; the DLL only reads the
        // Quartermaster subfolder now, so a stale root copy would silently
        // deactivate its feature. Move them once (an existing subfolder copy wins).
        public int MigrateLegacySidecars()
        {
            if (!Directory.Exists(_gameWin64Dir)) return 0;
            int moved = 0;
            foreach (var pattern in new[] { "qm_*.txt", "qm_*.json" })
            {
                foreach (var src in Directory.GetFiles(_gameWin64Dir, pattern, SearchOption.TopDirectoryOnly))
                {
                    try
                    {
                        var dst = Path.Combine(_sidecarDir, Path.GetFileName(src));
                        EnsureSidecarDir();
                        if (File.Exists(dst)) File.Delete(src);
                        else File.Move(src, dst);
                        moved++;
                        LogLine("Migrated legacy sidecar " + Path.GetFileName(src) + " -> " + _sidecarDir);
                    }
                    catch (Exception ex)
                    {
                        LogLine("Warning: could not migrate legacy sidecar " + src + ": " + ex.Message);
                    }
                }
            }
            return moved;
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
            EnsureSidecarDir();
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

        // LEGACY single global trigger file (pre per-profile). Older builds wrote
        // one shared qm_weather_trigger.txt, last-writer-wins, so building a
        // non-weather profile clobbered another deployed profile's weather. We no
        // longer write it; this accessor only exists so cleanup can purge a stale
        // copy. (It still matches the qm_weather_*.txt glob below, so the DLL keeps
        // reading it for back-compat.)
        public string TargetWeatherTriggerPath() => Path.Combine(_sidecarDir, "qm_weather_trigger.txt");

        // Per-profile weather trigger sidecar. Mirrors qm_items_<profile>.json: the
        // DLL globs qm_weather_*.txt and merges every profile's mappings, so two
        // deployed profiles that both use Weather Control items coexist.
        public string TargetProfileWeatherTriggerPath(string profileSafeName)
        {
            if (string.IsNullOrEmpty(profileSafeName))
                throw new ArgumentNullException(nameof(profileSafeName));
            return Path.Combine(_sidecarDir, "qm_weather_" + profileSafeName + ".txt");
        }

        // All deployed weather trigger files (per-profile qm_weather_<profile>.txt
        // plus any legacy qm_weather_trigger.txt - it matches the same glob). Used
        // for DLL-idle detection. Note: the permanent-pin qm_weather.txt has no
        // underscore after "weather" so it is NOT matched here.
        public IList<string> EnumerateProfileWeatherTriggerPaths()
        {
            if (!Directory.Exists(_sidecarDir)) return Array.Empty<string>();
            return Directory.GetFiles(_sidecarDir, "qm_weather_*.txt", SearchOption.TopDirectoryOnly);
        }

        // Writes one "<token> <weatherId>" line per Weather Control clone into this
        // profile's qm_weather_<profile>.txt. The DLL substring-matches the token
        // against the used ConsumableData name and sets that weather. Empty/null
        // removes only THIS profile's file (weather off for this profile) - other
        // profiles' files are untouched. Distinct by token (multiple items sharing
        // a weather collapse to one line).
        public void WriteWeatherTriggerConfig(string profileSafeName, IList<WeatherControlClone> clones)
        {
            var path = TargetProfileWeatherTriggerPath(profileSafeName);

            var lines = new List<WeatherControlClone>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            if (clones != null)
            {
                foreach (var c in clones)
                {
                    if (c == null || string.IsNullOrEmpty(c.TriggerToken)) continue;
                    if (seen.Add(c.TriggerToken)) lines.Add(c);
                }
            }

            if (lines.Count == 0)
            {
                if (File.Exists(path))
                {
                    LogLine("Removing qm_weather_" + profileSafeName + ".txt (no Weather Control items) -> " + path);
                    File.Delete(path);
                }
                return;
            }

            var sb = new StringBuilder();
            foreach (var c in lines)
                sb.Append(c.TriggerToken).Append(' ').Append(c.WeatherId).Append('\n');
            sb.Append("# Quartermaster Weather Control trigger config (auto-generated).\n");
            sb.Append("# Each active line: <ConsumableData-name substring> <weatherId 0..13>.\n");
            sb.Append("# weather ids: 0 Sunny 1 Cloudy 2 Fog 3 Mist 4 Rain 5 RainHeavy 6 Storm\n");
            sb.Append("#              7 Windy 8 HighPressure 9 Rainbow 10 Overcast 11 AshlandsFog\n");
            sb.Append("#              12 TortugaMist 13 Default. Lines starting with '#' are ignored.\n");

            LogLine("Writing qm_weather_" + profileSafeName + ".txt (" + lines.Count + " mapping(s)) -> " + path);
            EnsureSidecarDir();
            File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
        }

        // Per-profile XP-for-kills sidecar. Mirrors qm_weather_<profile>.txt: the DLL
        // globs qm_killxp_onkill*.txt and merges every profile's mappings (max wins on
        // a key collision), so two deployed profiles coexist. The trailing underscore
        // matches the qm_killxp_onkill_*.txt glob below (a manual qm_killxp_onkill.txt
        // has no underscore there, so it is not deployer-managed).
        public string TargetProfileKillXpPath(string profileSafeName)
        {
            if (string.IsNullOrEmpty(profileSafeName))
                throw new ArgumentNullException(nameof(profileSafeName));
            return Path.Combine(_sidecarDir, "qm_killxp_onkill_" + profileSafeName + ".txt");
        }

        // All deployed per-profile XP-for-kills sidecars. Used for DLL-idle detection
        // (this is a DLL-only feature, no pak, so a deployed file keeps the DLL alive).
        public IList<string> EnumerateProfileKillXpPaths()
        {
            if (!Directory.Exists(_sidecarDir)) return Array.Empty<string>();
            return Directory.GetFiles(_sidecarDir, "qm_killxp_onkill_*.txt", SearchOption.TopDirectoryOnly);
        }

        // Writes "default=N" + one "<keyword>=N" line per entry into this profile's
        // qm_killxp_onkill_<profile>.txt. The DLL parses it once at startup and grants
        // the flat XP on each enemy kill (longest matching keyword wins; default for
        // unmatched). Inactive (defaultXp <= 0 AND no keywords) removes only THIS
        // profile's file - other profiles' files are untouched. Values clamped to the
        // DLL-accepted 0..1000000; keys carrying '=' are dropped (would break parsing).
        public void WriteKillXpConfig(string profileSafeName, int defaultXp, IDictionary<string, int> keywords)
        {
            var path = TargetProfileKillXpPath(profileSafeName);

            int def = defaultXp < 0 ? 0 : (defaultXp > 1000000 ? 1000000 : defaultXp);
            var clean = new List<KeyValuePair<string, int>>();
            if (keywords != null)
            {
                foreach (var kv in keywords)
                {
                    var key = kv.Key?.Trim();
                    if (string.IsNullOrEmpty(key) || key.IndexOf('=') >= 0) continue;
                    int v = kv.Value < 0 ? 0 : (kv.Value > 1000000 ? 1000000 : kv.Value);
                    clean.Add(new KeyValuePair<string, int>(key, v));
                }
                clean.Sort((a, b) => string.CompareOrdinal(a.Key, b.Key));
            }

            // A keyword pinned to 0 only matters as an explicit suppress when there
            // is a positive default to suppress; with default 0 it grants nothing and
            // is noise. So "active" mirrors ResolveKillXpConfig: default > 0 OR any
            // keyword > 0. (Zero-value keywords are still written when active, so an
            // explicit "this enemy gives 0 despite the default" survives.)
            bool active = def > 0 || clean.Exists(kv => kv.Value > 0);
            if (!active)
            {
                if (File.Exists(path))
                {
                    LogLine("Removing qm_killxp_onkill_" + profileSafeName + ".txt (XP for Kills off) -> " + path);
                    File.Delete(path);
                }
                return;
            }

            var sb = new StringBuilder();
            sb.Append("# Quartermaster XP-for-kills config (auto-generated).\n");
            sb.Append("# default=N    : flat XP for any enemy not matched below (0 = vanilla).\n");
            sb.Append("# <keyword>=N  : flat XP for any pawn whose class name contains <keyword>\n");
            sb.Append("#               (case-insensitive substring; longest matching keyword wins).\n");
            sb.Append("default=").Append(def).Append('\n');
            foreach (var kv in clean)
                sb.Append(kv.Key).Append('=').Append(kv.Value).Append('\n');

            LogLine("Writing qm_killxp_onkill_" + profileSafeName + ".txt (default=" + def
                    + ", " + clean.Count + " keyword(s)) -> " + path);
            EnsureSidecarDir();
            File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
        }

        // Per-profile "Keep Shanties Playing" sentinel. The DLL only checks whether ANY
        // qm_shanty*.txt exists next to it (the content is ignored), so this is a pure
        // presence marker - mirroring the qm_killxp_onkill_<profile>.txt deploy. The
        // trailing underscore matches the qm_shanty_*.txt glob below (a manual
        // qm_shanty.txt has no underscore there, so it is not deployer-managed).
        public string TargetProfileShantyPath(string profileSafeName)
        {
            if (string.IsNullOrEmpty(profileSafeName))
                throw new ArgumentNullException(nameof(profileSafeName));
            return Path.Combine(_sidecarDir, "qm_shanty_" + profileSafeName + ".txt");
        }

        // All deployed per-profile shanty sentinels. Used for DLL-idle detection (this is
        // a DLL-only feature, no pak, so a deployed file keeps the DLL alive).
        public IList<string> EnumerateProfileShantyPaths()
        {
            if (!Directory.Exists(_sidecarDir)) return Array.Empty<string>();
            return Directory.GetFiles(_sidecarDir, "qm_shanty_*.txt", SearchOption.TopDirectoryOnly);
        }

        // Creates/removes this profile's qm_shanty_<profile>.txt. The DLL arms its
        // helm-leave keep-alive whenever any qm_shanty*.txt is present; the content is
        // ignored, so we write a short marker comment for humans. enabled=false removes
        // only THIS profile's file - other profiles' sentinels are untouched.
        public void WriteShantyConfig(string profileSafeName, bool enabled)
        {
            var path = TargetProfileShantyPath(profileSafeName);
            if (!enabled)
            {
                if (File.Exists(path))
                {
                    LogLine("Removing qm_shanty_" + profileSafeName + ".txt (Keep Shanties Playing off) -> " + path);
                    File.Delete(path);
                }
                return;
            }
            LogLine("Writing qm_shanty_" + profileSafeName + ".txt (Keep Shanties Playing on) -> " + path);
            EnsureSidecarDir();
            File.WriteAllText(path,
                "# Quartermaster: Keep Shanties Playing (auto-generated marker).\n"
                + "# The DLL arms its helm-leave shanty keep-alive when this file is present.\n",
                new UTF8Encoding(false));
        }

        // The deployed pak's source of truth: the full profile JSON ships next to
        // the feature sidecars, one qm_profile_<profile>.json per installed
        // Quartermaster_<profile>_P.pak. Its presence keeps the DLL alive (see
        // RemoveDllIfNoProfilesLeft) and feeds the in-game mod tab.
        public string TargetProfileJsonPath(string profileSafeName)
        {
            if (string.IsNullOrEmpty(profileSafeName))
                throw new ArgumentNullException(nameof(profileSafeName));
            return Path.Combine(_sidecarDir, "qm_profile_" + profileSafeName + ".json");
        }

        public IList<string> EnumerateProfileJsonPaths()
        {
            if (!Directory.Exists(_sidecarDir)) return Array.Empty<string>();
            return Directory.GetFiles(_sidecarDir, "qm_profile_*.json", SearchOption.TopDirectoryOnly);
        }

        public void WriteProfileJson(string profileSafeName, string profileJson)
        {
            if (string.IsNullOrEmpty(profileJson))
                throw new ArgumentNullException(nameof(profileJson));
            var path = TargetProfileJsonPath(profileSafeName);
            LogLine("Writing qm_profile_" + profileSafeName + ".json -> " + path);
            EnsureSidecarDir();
            File.WriteAllText(path, profileJson, new UTF8Encoding(false));
            RegenerateModsManifest();
        }

        public bool RemoveProfileJson(string profileSafeName)
        {
            var path = TargetProfileJsonPath(profileSafeName);
            if (!File.Exists(path)) return false;
            LogLine("Removing qm_profile_" + profileSafeName + ".json -> " + path);
            File.Delete(path);
            RegenerateModsManifest();
            return true;
        }

        // The in-game mod tab's render input: qm_modtab_mods.txt is the pre-merged
        // view over all installed qm_profile_*.json. Flush-left lines are mod
        // display names (sorted); indented lines are that mod's active-feature
        // detail rows (ProfileSummary); '#' lines are comments. Regenerated
        // whenever the profile set changes, so the DLL renders the list without
        // parsing profile JSONs.
        public string TargetModsManifestPath() => Path.Combine(_sidecarDir, "qm_modtab_mods.txt");

        public void RegenerateModsManifest()
        {
            var mods = new List<KeyValuePair<string, List<string>>>();
            foreach (var file in EnumerateProfileJsonPaths())
            {
                string name = null;
                List<string> details = null;
                try
                {
                    var profile = JsonSerializer.Deserialize<Profile>(File.ReadAllText(file), ProfileStore.JsonOpts);
                    if (profile != null)
                    {
                        name = profile.Name;
                        details = ProfileSummary.Lines(profile);
                    }
                }
                catch (Exception)
                {
                    // Unreadable profile JSON still gets a row via the file-stem fallback.
                }
                if (string.IsNullOrWhiteSpace(name))
                {
                    name = Path.GetFileNameWithoutExtension(file);
                    const string prefix = "qm_profile_";
                    if (name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                        name = name.Substring(prefix.Length);
                }
                mods.Add(new KeyValuePair<string, List<string>>(name.Trim(), details ?? new List<string>()));
            }
            mods.Sort((a, b) => StringComparer.OrdinalIgnoreCase.Compare(a.Key, b.Key));

            var path = TargetModsManifestPath();
            if (mods.Count == 0)
            {
                if (File.Exists(path))
                {
                    LogLine("Removing qm_modtab_mods.txt (no profile JSONs left) -> " + path);
                    File.Delete(path);
                }
                return;
            }

            var sb = new StringBuilder();
            sb.Append("# Quartermaster: installed modifications (auto-generated).\n");
            sb.Append("# Flush-left lines are mod names; indented lines are that mod's detail rows.\n");
            foreach (var m in mods)
            {
                sb.Append(m.Key).Append('\n');
                foreach (var d in m.Value) sb.Append("  ").Append(d).Append('\n');
            }
            LogLine("Writing qm_modtab_mods.txt (" + mods.Count + " mod(s)) -> " + path);
            EnsureSidecarDir();
            File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
        }

        public bool RemoveDllIfNoProfilesLeft(Action<string> deleter = null)
        {
            if (!Directory.Exists(_gameWin64Dir)) return false;

            // Every built pak deploys its profile JSON, and the DLL ships with
            // every build (it carries the in-game mod tab) - so the DLL stays as
            // long as any Quartermaster mod remains installed.
            if (EnumerateProfileJsonPaths().Count > 0) return false;

            if (EnumerateProfileItemsJsonPaths().Count > 0) return false;

            // Weather Control items need the DLL even with no building JSONs present.
            // Any deployed profile's qm_weather_*.txt (or a legacy
            // qm_weather_trigger.txt) keeps the DLL alive.
            if (EnumerateProfileWeatherTriggerPaths().Count > 0) return false;

            // XP for Kills is DLL-only (no pak): any deployed qm_killxp_onkill_*.txt
            // keeps the DLL alive too.
            if (EnumerateProfileKillXpPaths().Count > 0) return false;

            // Keep Shanties Playing is DLL-only (no pak): any deployed qm_shanty_*.txt
            // keeps the DLL alive too.
            if (EnumerateProfileShantyPaths().Count > 0) return false;

            var targetDll = TargetDllPath();
            var legacyMarker = LegacyDllMarkerPath();

            bool dllExists = File.Exists(targetDll);
            bool markerExists = File.Exists(legacyMarker);

            if (!dllExists && !markerExists) return false;

            if (dllExists && !IsOurProxyAtTarget())
            {
                LogLine("Skipping DLL cleanup: dxgi.dll exists at " + targetDll
                    + " but is not identified as Quartermaster (foreign proxy left alone).");
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
                LogLine("Removing legacy dxgi.dll.qm marker -> " + legacyMarker);
                deleter(legacyMarker);
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
            // Purge every qm_* sidecar: the Quartermaster subfolder plus any legacy
            // copies still sitting in the Win64 root, then the empty folder itself.
            foreach (var dir in new[] { _sidecarDir, _gameWin64Dir })
            {
                if (!Directory.Exists(dir)) continue;
                foreach (var pattern in new[] { "qm_*.txt", "qm_*.json" })
                {
                    foreach (var path in Directory.GetFiles(dir, pattern, SearchOption.TopDirectoryOnly))
                        TryDelete(path, result);
                }
            }
            try
            {
                if (Directory.Exists(_sidecarDir)
                    && !Directory.EnumerateFileSystemEntries(_sidecarDir).Any())
                {
                    Directory.Delete(_sidecarDir);
                    result.Removed.Add(_sidecarDir);
                }
            }
            catch (Exception ex)
            {
                result.Errors.Add(_sidecarDir + ": " + ex.Message);
            }
            if (!File.Exists(TargetDllPath()) || IsOurProxyAtTarget())
                TryDelete(TargetDllPath(), result);
            else
                LogLine("Skipping dxgi.dll: not identified as Quartermaster (foreign proxy left alone).");
            TryDelete(LegacyDllMarkerPath(), result);
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
