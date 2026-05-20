using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using CUE4Parse.Compression;
using CUE4Parse.Encryption.Aes;
using CUE4Parse.FileProvider;
using CUE4Parse.MappingsProvider;
using CUE4Parse.UE4.Objects.Core.Misc;
using CUE4Parse.UE4.Versions;

namespace Windrose.Quartermaster.Core.BuildingCreator
{
    // Indexes all MaterialInstanceConstant assets (MI_*.uasset) in the
    // Vanilla pak set. Built lazily on first use - the scan walks the
    // mounted CUE4Parse provider's virtual-file list (no I/O for the
    // individual MI bytes, just path enumeration).
    //
    // Subsequent extraction of a specific MI for Inspect-on-demand goes
    // via retoc to-legacy (see BuildingPatcher.ExtractVanillaAsset) -
    // CUE4Parse can decode IoStore but legacy-format files are what
    // UAssetAPI (and our MaterialInstanceInspector) expects.
    public sealed class VanillaMaterialCatalog
    {
        public string PaksDir;
        public string AesKey;
        public string UsmapPath;
        public Action<string> Log;

        readonly object _gate = new object();
        bool _built;
        List<VanillaMaterialEntry> _entries;

        // Convention: an entry's PackagePath is the "/Game/..." style path
        // (the UE virtual filename without extension), DisplayName is the
        // file stem (e.g. "MI_Paintings_01").
        public IReadOnlyList<VanillaMaterialEntry> All
        {
            get
            {
                EnsureBuilt();
                return _entries;
            }
        }

        public IReadOnlyList<VanillaMaterialEntry> Search(string query, int limit = 50)
        {
            EnsureBuilt();
            if (limit <= 0) limit = 50;
            if (string.IsNullOrWhiteSpace(query))
            {
                return _entries.Take(limit).ToList();
            }
            var q = query.Trim();
            // Case-insensitive substring match against both DisplayName
            // and PackagePath. Score by where the match starts so prefix
            // matches sort first.
            return _entries
                .Select(e => new
                {
                    Entry = e,
                    Score = ScoreMatch(e, q),
                })
                .Where(x => x.Score >= 0)
                .OrderBy(x => x.Score)
                .ThenBy(x => x.Entry.DisplayName, StringComparer.OrdinalIgnoreCase)
                .Take(limit)
                .Select(x => x.Entry)
                .ToList();
        }

        // Lower score = better match. -1 = no match.
        static int ScoreMatch(VanillaMaterialEntry e, string q)
        {
            int dn = e.DisplayName?.IndexOf(q, StringComparison.OrdinalIgnoreCase) ?? -1;
            int pp = e.PackagePath?.IndexOf(q, StringComparison.OrdinalIgnoreCase) ?? -1;
            if (dn < 0 && pp < 0) return -1;
            if (dn < 0) return 1000 + pp;
            return dn;
        }

        // Force a rebuild (e.g. after a vanilla-paks update). Otherwise
        // the catalog is built once per process lifetime.
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

        List<VanillaMaterialEntry> BuildIndex()
        {
            if (string.IsNullOrWhiteSpace(PaksDir))   throw new InvalidOperationException("VanillaMaterialCatalog.PaksDir not set");
            if (!Directory.Exists(PaksDir))           throw new InvalidOperationException("VanillaMaterialCatalog.PaksDir not found: " + PaksDir);
            if (string.IsNullOrWhiteSpace(AesKey))    throw new InvalidOperationException("VanillaMaterialCatalog.AesKey not set");
            if (string.IsNullOrWhiteSpace(UsmapPath)) throw new InvalidOperationException("VanillaMaterialCatalog.UsmapPath not set");
            if (!File.Exists(UsmapPath))              throw new InvalidOperationException("VanillaMaterialCatalog.UsmapPath not found: " + UsmapPath);

            EnsureOodle();

            LogLine("[catalog] indexing vanilla MIs from " + PaksDir);
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
            LogLine("[catalog] provider mounted: " + provider.Files.Count
                + " virtual files (+" + mounted + " vfs)");

            // Find all uasset entries whose stem starts with "MI_".
            // Filter on case-insensitive prefix to be tolerant.
            var entries = new List<VanillaMaterialEntry>(256);
            foreach (var kv in provider.Files)
            {
                var key = kv.Key;
                if (!key.EndsWith(".uasset", StringComparison.OrdinalIgnoreCase)) continue;

                int lastSlash = key.LastIndexOfAny(new[] { '/', '\\' });
                string fileName = lastSlash >= 0 ? key.Substring(lastSlash + 1) : key;
                if (!fileName.StartsWith("MI_", StringComparison.OrdinalIgnoreCase)) continue;

                var stem = fileName.Substring(0, fileName.Length - ".uasset".Length);

                // PackagePath: convert pak-internal "R5/Content/..." style
                // into UE's "/Game/..." path. Strip the leading
                // "<game>/Content/" prefix if present so the result lines
                // up with what the editor / patcher uses.
                var withoutExt = key.Substring(0, key.Length - ".uasset".Length).Replace('\\', '/');
                var pkgPath = ToGamePath(withoutExt);

                entries.Add(new VanillaMaterialEntry
                {
                    DisplayName = stem,
                    PackagePath = pkgPath,
                    PakRelativePath = withoutExt,
                });
            }

            entries.Sort((a, b) => string.Compare(a.DisplayName, b.DisplayName, StringComparison.OrdinalIgnoreCase));
            LogLine("[catalog] indexed " + entries.Count + " vanilla MI(s)");
            return entries;
        }

        // Strip the leading "<chunk>/Content/" prefix and prepend "/Game".
        // Example input:  "R5/Content/Environment/.../MI_Paintings_01"
        // Example output: "/Game/Environment/.../MI_Paintings_01"
        // Falls through cleanly if the input has no Content/ segment.
        static string ToGamePath(string pakInternal)
        {
            const string contentMarker = "/Content/";
            int idx = pakInternal.IndexOf(contentMarker, StringComparison.OrdinalIgnoreCase);
            if (idx < 0) return "/" + pakInternal.TrimStart('/');
            return "/Game" + pakInternal.Substring(idx + contentMarker.Length - 1);
        }

        // Mirror of BuildingItemExporter.EnsureOodle - the catalog can be
        // initialized before any other CUE4Parse user has primed Oodle.
        void EnsureOodle()
        {
            var here = AppContext.BaseDirectory;
            var dllPath = Path.Combine(here, OodleHelper.OodleFileName);
            if (!File.Exists(dllPath))
            {
                LogLine("[catalog] downloading Oodle DLL");
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

    // Single entry in the catalog. DisplayName == file stem
    // ("MI_Paintings_01"), PackagePath == UE virtual path
    // ("/Game/Environment/.../MI_Paintings_01").
    public sealed class VanillaMaterialEntry
    {
        public string DisplayName;
        public string PackagePath;
        public string PakRelativePath;  // for internal use (retoc filter lookup)
    }
}
