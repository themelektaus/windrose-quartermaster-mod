using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Windrose.Quartermaster.Core
{
    // Disk persistence for Profiles. All profiles live in Profiles/<id>.json
    // (gitignored, freely editable from the GUI).
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
            AddFromDir(_paths.Profiles, seen: seen, sink: result);
            return result;
        }

        public Profile Load(string id)
        {
            if (string.IsNullOrEmpty(id)) return null;
            return LoadAll().FirstOrDefault(p => string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase));
        }

        // Persists a profile to Profiles/<id>.json.
        public void Save(Profile profile)
        {
            if (profile == null) throw new ArgumentNullException("profile");
            if (string.IsNullOrEmpty(profile.Id)) throw new ArgumentException("Profile.Id is required");
            if (string.IsNullOrEmpty(profile.Name)) throw new ArgumentException("Profile.Name is required");

            Directory.CreateDirectory(_paths.Profiles);
            profile.ModifiedAt = DateTimeOffset.Now;
            if (profile.CreatedAt == default(DateTimeOffset)) profile.CreatedAt = profile.ModifiedAt;

            var path = Path.Combine(_paths.Profiles, profile.Id + ".json");
            var json = JsonSerializer.Serialize(profile, JsonOpts);
            File.WriteAllText(path, json);
        }

        // Removes a profile. Returns true if a file was deleted, false if the
        // id wasn't found.
        public bool Delete(string id)
        {
            if (string.IsNullOrEmpty(id)) return false;
            var path = Path.Combine(_paths.Profiles, id + ".json");
            if (!File.Exists(path)) return false;
            File.Delete(path);
            return true;
        }

        void AddFromDir(string dir, HashSet<string> seen, List<Profile> sink)
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
                sink.Add(profile);
            }
        }
    }
}
