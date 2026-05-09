using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Windrose.Quartermaster.Core
{
    // Disk persistence for Profiles. Built-in profiles live in
    // Profiles/_builtin/ (tracked in git, read-only) and user profiles in
    // Profiles/<id>.json (gitignored, freely editable from the GUI).
    //
    // The two pools never collide because IDs are GUIDs; if a future user
    // accidentally creates a file with the same id as a builtin, the builtin
    // (loaded first) wins -- LoadAll() returns it once, the user copy is
    // ignored.
    public sealed class ProfileStore
    {
        public static readonly JsonSerializerOptions JsonOpts = BuildJsonOptions();

        static JsonSerializerOptions BuildJsonOptions()
        {
            var opts = new JsonSerializerOptions
            {
                IncludeFields = true,
                WriteIndented = true,
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            };
            return opts;
        }

        readonly WindrosePaths _paths;

        public ProfileStore(WindrosePaths paths)
        {
            if (paths == null) throw new ArgumentNullException("paths");
            _paths = paths;
        }

        public List<Profile> LoadAll()
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var result = new List<Profile>();
            AddFromDir(_paths.ProfilesBuiltin, isBuiltin: true,  seen: seen, sink: result);
            AddFromDir(_paths.Profiles,        isBuiltin: false, seen: seen, sink: result);
            return result;
        }

        public Profile Load(string id)
        {
            if (string.IsNullOrEmpty(id)) return null;
            return LoadAll().FirstOrDefault(p => string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase));
        }

        // Persists a user profile to Profiles/<id>.json. Refuses to write
        // builtins -- those are git-tracked and edited by hand.
        public void Save(Profile profile)
        {
            if (profile == null) throw new ArgumentNullException("profile");
            if (string.IsNullOrEmpty(profile.Id)) throw new ArgumentException("Profile.Id is required");
            if (string.IsNullOrEmpty(profile.Name)) throw new ArgumentException("Profile.Name is required");
            if (profile.IsBuiltin)
                throw new InvalidOperationException("Builtin profiles are read-only; clone via Duplicate first");

            // Don't allow IDs that resolve to a builtin -- the user can't
            // shadow one, otherwise the read path becomes ambiguous.
            if (IsBuiltinId(profile.Id))
                throw new InvalidOperationException(
                    "Profile.Id collides with a builtin profile; pick a different id");

            Directory.CreateDirectory(_paths.Profiles);
            profile.ModifiedAt = DateTimeOffset.Now;
            if (profile.CreatedAt == default(DateTimeOffset)) profile.CreatedAt = profile.ModifiedAt;

            var path = Path.Combine(_paths.Profiles, profile.Id + ".json");
            var json = JsonSerializer.Serialize(profile, JsonOpts);
            File.WriteAllText(path, json);
        }

        // Removes a user profile. Returns true if a file was deleted, false
        // if the id wasn't found. Refuses to delete builtins.
        public bool Delete(string id)
        {
            if (string.IsNullOrEmpty(id)) return false;
            if (IsBuiltinId(id))
                throw new InvalidOperationException("Builtin profiles cannot be deleted");
            var path = Path.Combine(_paths.Profiles, id + ".json");
            if (!File.Exists(path)) return false;
            File.Delete(path);
            return true;
        }

        bool IsBuiltinId(string id)
        {
            // Builtin filenames are slugs (e.g. x2.json), user filenames are
            // GUIDs ((<id>.json). The id-vs-filename relationship is therefore
            // direct only for user profiles. Walk the dir and match on JSON
            // content to detect builtin collisions reliably.
            if (!Directory.Exists(_paths.ProfilesBuiltin)) return false;
            foreach (var path in Directory.EnumerateFiles(_paths.ProfilesBuiltin, "*.json", SearchOption.TopDirectoryOnly))
            {
                try
                {
                    var content = File.ReadAllText(path);
                    var profile = JsonSerializer.Deserialize<Profile>(content, JsonOpts);
                    if (profile != null && string.Equals(profile.Id, id, StringComparison.OrdinalIgnoreCase))
                        return true;
                }
                catch { /* skip malformed file */ }
            }
            return false;
        }

        void AddFromDir(string dir, bool isBuiltin, HashSet<string> seen, List<Profile> sink)
        {
            if (!Directory.Exists(dir)) return;
            foreach (var path in Directory.EnumerateFiles(dir, "*.json", SearchOption.TopDirectoryOnly))
            {
                Profile profile;
                try
                {
                    var content = File.ReadAllText(path);
                    profile = JsonSerializer.Deserialize<Profile>(content, JsonOpts);
                }
                catch (Exception)
                {
                    // Bad JSON: skip. Caller can list files separately if it
                    // wants to surface "broken profile" warnings in the GUI.
                    continue;
                }
                if (profile == null || string.IsNullOrEmpty(profile.Id)) continue;
                if (!seen.Add(profile.Id)) continue;
                profile.IsBuiltin = isBuiltin;
                sink.Add(profile);
            }
        }
    }
}
