using System;
using System.IO;
using System.Text.Json;

namespace Windrose.Quartermaster.Core
{
    public static class GameInstallOverride
    {
        // Until configured, Load/Save are no-ops so headless CLI / unit-test paths still work via plain SteamLocator.
        static string s_dataRoot;
        static readonly object s_lock = new object();

        public static void ConfigureDataRoot(string dataRoot)
        {
            if (string.IsNullOrEmpty(dataRoot)) return;
            lock (s_lock)
            {
                s_dataRoot = Path.GetFullPath(dataRoot);
            }
        }

        public static string GetOverrideFilePath()
        {
            lock (s_lock)
            {
                if (string.IsNullOrEmpty(s_dataRoot)) return null;
                return Path.Combine(s_dataRoot, "game-install.json");
            }
        }

        // Never throws: any IO/parse error is treated as "no override" so a broken file falls through to Steam.
        public static string LoadGameRoot()
        {
            var path = GetOverrideFilePath();
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return null;
            try
            {
                var json = File.ReadAllText(path);
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.ValueKind != JsonValueKind.Object) return null;
                if (!doc.RootElement.TryGetProperty("gameRoot", out var gr)) return null;
                if (gr.ValueKind != JsonValueKind.String) return null;
                var s = gr.GetString();
                return string.IsNullOrWhiteSpace(s) ? null : s.Trim();
            }
            catch
            {
                return null;
            }
        }

        // Empty/null gameRoot deletes the file (= back to Steam auto-detect).
        public static void SaveGameRoot(string gameRoot)
        {
            var path = GetOverrideFilePath();
            if (string.IsNullOrEmpty(path))
                throw new InvalidOperationException(
                    "GameInstallOverride.ConfigureDataRoot was never called - cannot persist override.");

            if (string.IsNullOrWhiteSpace(gameRoot))
            {
                try { if (File.Exists(path)) File.Delete(path); } catch { }
                return;
            }

            var normalized = Path.GetFullPath(gameRoot.Trim());
            var json = JsonSerializer.Serialize(new { gameRoot = normalized },
                new JsonSerializerOptions { WriteIndented = true });
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(path, json);
        }

        public static void Clear()
        {
            SaveGameRoot(null);
        }

        public static (bool Ok, string Error) Validate(string gameRoot)
        {
            if (string.IsNullOrWhiteSpace(gameRoot))
                return (false, "Path is empty.");
            string full;
            try { full = Path.GetFullPath(gameRoot.Trim()); }
            catch (Exception ex) { return (false, "Invalid path: " + ex.Message); }
            if (!Directory.Exists(full))
                return (false, "Folder does not exist: " + full);

            var binariesDir = Path.Combine(full, "R5", "Binaries", "Win64");
            if (!Directory.Exists(binariesDir))
                return (false, "Missing binaries directory: " + binariesDir);
            string[] exes;
            try { exes = Directory.GetFiles(binariesDir, "Windrose*.exe"); }
            catch (Exception ex) { return (false, "Could not scan binaries directory: " + ex.Message); }
            if (exes.Length == 0)
                return (false, "Missing game executable (no Windrose*.exe found under): " + binariesDir);

            var paksDir = Path.Combine(full, "R5", "Content", "Paks");
            if (!Directory.Exists(paksDir))
                return (false, "Missing Paks directory: " + paksDir);
            bool anyPak = false;
            foreach (var name in SteamLocator.VanillaPakNames)
            {
                if (File.Exists(Path.Combine(paksDir, name))) { anyPak = true; break; }
            }
            if (!anyPak)
                return (false, "Could not find a Windrose vanilla pak under: " + paksDir);

            return (true, null);
        }

        public static string TryResolveVanillaPak()
        {
            var gameRoot = LoadGameRoot();
            if (string.IsNullOrEmpty(gameRoot)) return null;
            var paksDir = Path.Combine(gameRoot, "R5", "Content", "Paks");
            if (!Directory.Exists(paksDir)) return null;
            foreach (var name in SteamLocator.VanillaPakNames)
            {
                var candidate = Path.Combine(paksDir, name);
                if (File.Exists(candidate)) return Path.GetFullPath(candidate);
            }
            return null;
        }

        public static string GetGameRoot()
        {
            return LoadGameRoot();
        }
    }
}
