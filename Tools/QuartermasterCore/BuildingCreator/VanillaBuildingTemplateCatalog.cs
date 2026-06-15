using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using CUE4Parse.Compression;
using CUE4Parse.Encryption.Aes;
using CUE4Parse.FileProvider;
using CUE4Parse.MappingsProvider;
using CUE4Parse.MappingsProvider.Usmap;
using CUE4Parse.UE4.Objects.Core.Misc;
using CUE4Parse.UE4.Versions;

namespace Windrose.Quartermaster.Core.BuildingCreator
{
    // Indexes vanilla DA_BI_* building DataAssets by path only (cheap); per-DA metadata comes from VanillaBuildingTemplateInspector on demand.
    public sealed class VanillaBuildingTemplateCatalog
    {
        public string PaksDir;
        public string AesKey;
        public string UsmapPath;
        public Action<string> Log;

        readonly object _gate = new object();
        bool _built;
        List<VanillaBuildingTemplateEntry> _entries;
        // Kept alive after the path scan so the inspector shares one mounted Vfs instead of remounting per inspect.
        DefaultFileProvider _provider;

        public DefaultFileProvider Provider
        {
            get
            {
                EnsureBuilt();
                return _provider;
            }
        }

        public IReadOnlyList<VanillaBuildingTemplateEntry> All
        {
            get
            {
                EnsureBuilt();
                return _entries;
            }
        }

        public IReadOnlyList<string> Categories
        {
            get
            {
                EnsureBuilt();
                return _entries
                    .Select(e => e.Category)
                    .Where(c => !string.IsNullOrEmpty(c))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(c => c, StringComparer.OrdinalIgnoreCase)
                    .ToList();
            }
        }

        public IReadOnlyList<VanillaBuildingTemplateEntry> Search(string query, string category, int limit = 100)
        {
            EnsureBuilt();
            if (limit <= 0) limit = 100;

            IEnumerable<VanillaBuildingTemplateEntry> source = _entries;
            if (!string.IsNullOrWhiteSpace(category))
            {
                var cat = category.Trim();
                source = source.Where(e => string.Equals(e.Category, cat, StringComparison.OrdinalIgnoreCase));
            }
            if (string.IsNullOrWhiteSpace(query))
            {
                return source.Take(limit).ToList();
            }
            var q = query.Trim();
            return source
                .Select(e => new { Entry = e, Score = ScoreMatch(e, q) })
                .Where(x => x.Score >= 0)
                .OrderBy(x => x.Score)
                .ThenBy(x => x.Entry.DisplayName, StringComparer.OrdinalIgnoreCase)
                .Take(limit)
                .Select(x => x.Entry)
                .ToList();
        }

        public VanillaBuildingTemplateEntry GetById(string id)
        {
            if (string.IsNullOrWhiteSpace(id)) return null;
            EnsureBuilt();
            return _entries.FirstOrDefault(e =>
                string.Equals(e.Id, id, StringComparison.OrdinalIgnoreCase));
        }

        // Lower score = better match. -1 = no match.
        static int ScoreMatch(VanillaBuildingTemplateEntry e, string q)
        {
            int dn = e.DisplayName?.IndexOf(q, StringComparison.OrdinalIgnoreCase) ?? -1;
            int cat = e.Category?.IndexOf(q, StringComparison.OrdinalIgnoreCase) ?? -1;
            int pp = e.PackagePath?.IndexOf(q, StringComparison.OrdinalIgnoreCase) ?? -1;
            if (dn < 0 && cat < 0 && pp < 0) return -1;
            if (dn >= 0) return dn;
            if (cat >= 0) return 500 + cat;
            return 1000 + pp;
        }

        public void Invalidate()
        {
            lock (_gate)
            {
                _built = false;
                _entries = null;
            }
        }

        void EnsureBuilt()
        {
            if (_built) return;
            lock (_gate)
            {
                if (_built) return;
                _entries = BuildIndex();
                _built = true;
            }
        }

        List<VanillaBuildingTemplateEntry> BuildIndex()
        {
            if (string.IsNullOrWhiteSpace(PaksDir))   throw new InvalidOperationException("VanillaBuildingTemplateCatalog.PaksDir not set");
            if (!Directory.Exists(PaksDir))           throw new InvalidOperationException("VanillaBuildingTemplateCatalog.PaksDir not found: " + PaksDir);
            if (string.IsNullOrWhiteSpace(AesKey))    throw new InvalidOperationException("VanillaBuildingTemplateCatalog.AesKey not set");
            if (string.IsNullOrWhiteSpace(UsmapPath)) throw new InvalidOperationException("VanillaBuildingTemplateCatalog.UsmapPath not set");
            if (!File.Exists(UsmapPath))              throw new InvalidOperationException("VanillaBuildingTemplateCatalog.UsmapPath not found: " + UsmapPath);

            EnsureOodle();

            LogLine("[building-catalog] indexing vanilla DAs from " + PaksDir);
            var provider = new DefaultFileProvider(
                PaksDir,
                SearchOption.TopDirectoryOnly,
                new VersionContainer(EGame.GAME_UE5_6));
            provider.MappingsContainer = new FileUsmapTypeMappingsProvider(UsmapPath);
            provider.Initialize();

            var aes = new FAesKey(AesKey);
            var seenGuids = new HashSet<FGuid> { new FGuid() };
            foreach (var v in provider.UnloadedVfs) seenGuids.Add(v.EncryptionKeyGuid);
            foreach (var g in seenGuids) provider.SubmitKey(g, aes);

            int mounted = provider.Mount();
            LogLine("[building-catalog] provider mounted: " + provider.Files.Count
                + " virtual files (+" + mounted + " vfs)");

            _provider = provider;

            // These folders hold R5BuildingBrush assets, not R5BuildingItem; cloning them as Item templates fails at game-load.
            var excludedFolders = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "BuildingBrushes",
                "Houses",
                "DecorationBrushes",
            };

            const string gameplayBuildingMarker = "/Gameplay/Building/";

            var entries = new List<VanillaBuildingTemplateEntry>(900);
            foreach (var kv in provider.Files)
            {
                var key = kv.Key.Replace('\\', '/');
                if (!key.EndsWith(".uasset", StringComparison.OrdinalIgnoreCase)) continue;

                if (key.IndexOf(gameplayBuildingMarker, StringComparison.OrdinalIgnoreCase) < 0) continue;

                int lastSlash = key.LastIndexOf('/');
                string fileName = lastSlash >= 0 ? key.Substring(lastSlash + 1) : key;
                if (!fileName.StartsWith("DA_BI_", StringComparison.OrdinalIgnoreCase)) continue;

                var stem = fileName.Substring(0, fileName.Length - ".uasset".Length);

                string parentFolder = "";
                if (lastSlash > 0)
                {
                    int prevSlash = key.LastIndexOf('/', lastSlash - 1);
                    if (prevSlash >= 0)
                    {
                        parentFolder = key.Substring(prevSlash + 1, lastSlash - prevSlash - 1);
                    }
                }

                if (excludedFolders.Contains(parentFolder)) continue;

                var withoutExt = key.Substring(0, key.Length - ".uasset".Length);
                var pkgPath = ToGamePath(withoutExt);

                entries.Add(new VanillaBuildingTemplateEntry
                {
                    Id              = pkgPath,
                    DisplayName     = stem,
                    Category        = parentFolder,
                    PackagePath     = pkgPath,
                    PakRelativePath = withoutExt,
                });
            }

            entries.Sort((a, b) =>
            {
                int c = string.Compare(a.Category, b.Category, StringComparison.OrdinalIgnoreCase);
                if (c != 0) return c;
                return string.Compare(a.DisplayName, b.DisplayName, StringComparison.OrdinalIgnoreCase);
            });
            LogLine("[building-catalog] indexed " + entries.Count + " vanilla DA_BI_* template(s)");
            return entries;
        }

        // Strip the leading "<chunk>/Content/" prefix and prepend "/Game".
        static string ToGamePath(string pakInternal)
        {
            const string contentMarker = "/Content/";
            int idx = pakInternal.IndexOf(contentMarker, StringComparison.OrdinalIgnoreCase);
            if (idx < 0) return "/" + pakInternal.TrimStart('/');
            return "/Game" + pakInternal.Substring(idx + contentMarker.Length - 1);
        }

        void EnsureOodle()
        {
            var here = WindrosePaths.ResolveNativeDllDir();
            var dllPath = Path.Combine(here, OodleHelper.OodleFileName);
            if (!File.Exists(dllPath))
            {
                LogLine("[building-catalog] downloading Oodle DLL");
                using (var http = new HttpClient { Timeout = TimeSpan.FromMinutes(2) })
                {
                    var ok = OodleHelper.DownloadOodleDllFromOodleUEAsync(http, dllPath)
                        .GetAwaiter().GetResult();
                    if (!ok || !File.Exists(dllPath))
                        throw new InvalidOperationException("Failed to download Oodle DLL");
                }
            }
            OodleHelper.Initialize(dllPath);
        }

        void LogLine(string msg) { if (Log != null) Log(msg); }
    }

    public sealed class VanillaBuildingTemplateEntry
    {
        public string Id;               // = PackagePath
        public string DisplayName;
        public string Category;
        public string PackagePath;
        public string PakRelativePath;
    }
}
