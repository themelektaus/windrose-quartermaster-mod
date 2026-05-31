using System;
using System.IO;
using System.Text.Json;

namespace Windrose.Quartermaster.Core
{
    // Persisted user override for the Windrose game install location.
    // The default code path uses SteamLocator to discover the install via
    // the Steam registry + libraryfolders.vdf, which only works for Steam
    // installs. Users on Epic / GOG / dedicated-server / portable setups
    // can use this override to point Quartermaster at any folder layout
    // that mirrors the Steam tree:
    //   <GameRoot>/R5/Binaries/Win64/Windrose*.exe (client or dedicated server)
    //   <GameRoot>/R5/Content/Paks/pakchunk0-Windows(.pak | Server.pak)
    //   <GameRoot>/R5/Content/Paks/~mods/                        (created on demand)
    //
    // Persistence layout (next to the rest of the per-user data so it
    // travels with a portable install):
    //   <DataRoot>/game-install.json
    //     {
    //       "gameRoot": "E:\\Games\\steamapps\\common\\Windrose"
    //     }
    //
    // Null / empty gameRoot is treated as "no override" (= fall back to
    // SteamLocator). The Validate() helper enforces shape correctness
    // before saving so a typo in the GUI can't silently break later
    // builds; the static FindVanillaPak/FindModsDir/FindBinariesWin64Dir
    // call sites consult the override transparently so no caller needs
    // to know about the new fallback.
    public static class GameInstallOverride
    {
        // The data-root configured at app startup (Program.cs CreateWebApp).
        // Until set, Load/Save are no-ops so headless CLI / unit-test paths
        // that don't have a data root still work via plain SteamLocator.
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

        // Absolute path to the persisted override JSON. Returns null if the
        // data root hasn't been configured yet (e.g. CLI smoke tests).
        public static string GetOverrideFilePath()
        {
            lock (s_lock)
            {
                if (string.IsNullOrEmpty(s_dataRoot)) return null;
                return Path.Combine(s_dataRoot, "game-install.json");
            }
        }

        // Returns the persisted gameRoot or null if no override exists / the
        // file is missing / unreadable. Never throws - any IO error is
        // treated as "no override" so a broken JSON falls through to Steam.
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

        // Persists the override. Empty / null gameRoot deletes the file
        // (= back to Steam auto-detect). Throws when the data root hasn't
        // been configured so callers get a clear failure shape.
        public static void SaveGameRoot(string gameRoot)
        {
            var path = GetOverrideFilePath();
            if (string.IsNullOrEmpty(path))
                throw new InvalidOperationException(
                    "GameInstallOverride.ConfigureDataRoot was never called - cannot persist override.");

            if (string.IsNullOrWhiteSpace(gameRoot))
            {
                try { if (File.Exists(path)) File.Delete(path); } catch { /* best effort */ }
                return;
            }

            var normalized = Path.GetFullPath(gameRoot.Trim());
            var json = JsonSerializer.Serialize(new { gameRoot = normalized },
                new JsonSerializerOptions { WriteIndented = true });
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(path, json);
        }

        // Wipes the persisted override (back to Steam auto-detect).
        public static void Clear()
        {
            SaveGameRoot(null);
        }

        // Validates a candidate game-root path: it must contain at least one
        // Windrose*.exe under R5\Binaries\Win64\ (covers client
        // 'Windrose-Win64-Shipping.exe', dedicated-server
        // 'WindroseServer-Win64-Shipping.exe', and any future renames) AND
        // at least one of the known vanilla pak filenames under
        // R5\Content\Paks\. Returns (true, null) on success or
        // (false, errorMessage) when invalid. Used by the GUI endpoint to
        // give the user immediate feedback before persisting.
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

        // Resolves the override gameRoot to the same absolute vanilla-pak path
        // that SteamLocator.FindVanillaPak() would produce - or null if the
        // override is unset / invalid. Never throws. Used by SteamLocator
        // when consulting the override first.
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

        // Convenience accessor returning the override gameRoot directly
        // (no validation). Null = no override set.
        public static string GetGameRoot()
        {
            return LoadGameRoot();
        }
    }
}
