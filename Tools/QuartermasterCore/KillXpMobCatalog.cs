using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.RegularExpressions;
using CUE4Parse.Compression;
using CUE4Parse.Encryption.Aes;
using CUE4Parse.FileProvider;
using CUE4Parse.MappingsProvider;
using CUE4Parse.UE4.Objects.Core.Misc;
using CUE4Parse.UE4.Versions;
using Windrose.Quartermaster.Core.BuildingCreator;

namespace Windrose.Quartermaster.Core
{
    // Builds the XP-for-kills keyword catalog for the GUI by enumerating the vanilla
    // BP_Mob_*.uasset blueprint names straight from the mounted pak (CUE4Parse path
    // enumeration only - no per-asset I/O), so the list auto-tracks game updates.
    //
    // The DLL (qm_killxp) matches a config keyword case-insensitively as a SUBSTRING
    // of the killed pawn's runtime UClass name (e.g. "Mob_Boar" matches
    // "BP_Mob_BoarF_C") and the LONGEST matching keyword wins. Each killable pawn
    // class therefore becomes one catalog row whose keyword is the asset stem without
    // the "BP_" prefix; a base class (BP_Mob_Boar -> "Mob_Boar") naturally covers its
    // variants via substring, while a longer variant row overrides it. Non-pawn
    // BP_Mob_* assets (AI controllers, weapons, projectiles, effect zones, abstract
    // bases, friendly/player allies, test/onboarding maps) are filtered out - they are
    // never kill victims.
    public sealed class KillXpMobCatalog
    {
        public string PaksDir;
        public string AesKey;
        public string UsmapPath;
        public Action<string> Log;

        readonly object _gate = new object();
        bool _built;
        List<KillXpMobKeyword> _entries;

        public IReadOnlyList<KillXpMobKeyword> All
        {
            get { EnsureBuilt(); return _entries; }
        }

        public void Invalidate()
        {
            lock (_gate) { _built = false; _entries = null; }
        }

        // Non-pawn BP_Mob_* assets - mirrors Tools/gen_mob_keywords.py classification.
        static readonly Regex Noise = new Regex(
            @"AIController|_Wpn_|MeleeWpn|RangeWpn|StatCorrection|_Projectile|AoEZone|" +
            @"GroundDamageZone|_Puddle|_Ribbon|HealZone|_Totem|_Task|LaunchSpline|" +
            @"ZoneVisualizer|BombThrow|ChannelingBeam|Throw_Projectile|GiantShaker_",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);
        static readonly Regex Abstract = new Regex(@"_Base$|_BaseActor$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        static readonly Regex Ally     = new Regex(@"_Friend|_Player$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        static readonly Regex TestMap  = new Regex(@"AutoTest|Gamescom|Onboarding|AITestMap|AITesMap|BlackMarks", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        static readonly Regex Quest    = new Regex(@"ForQuest", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        // Returns true for a real, player-killable pawn (and its quest one-offs);
        // false for everything filtered out above. `isQuest` flags ForQuest variants.
        static bool IsKillablePawn(string stem, out bool isQuest)
        {
            isQuest = false;
            if (Noise.IsMatch(stem) || Abstract.IsMatch(stem) || Ally.IsMatch(stem) || TestMap.IsMatch(stem))
                return false;
            isQuest = Quest.IsMatch(stem);
            return true;
        }

        static string CategoryOf(string stem)
        {
            // Order matters: a class can hit several tokens; the first wins.
            if (stem.IndexOf("Officer", StringComparison.OrdinalIgnoreCase) >= 0
                || stem.IndexOf("Crew", StringComparison.OrdinalIgnoreCase) >= 0) return "Crew";
            if (stem.IndexOf("Senkamati", StringComparison.OrdinalIgnoreCase) >= 0) return "Senkamati";
            if (stem.IndexOf("Blackbeard", StringComparison.OrdinalIgnoreCase) >= 0) return "Blackbeard";
            if (stem.IndexOf("Crab", StringComparison.OrdinalIgnoreCase) >= 0) return "Wildlife";
            if (stem.IndexOf("Drowned", StringComparison.OrdinalIgnoreCase) >= 0
                || stem.IndexOf("Zombie", StringComparison.OrdinalIgnoreCase) >= 0) return "Undead";
            if (stem.IndexOf("Giant", StringComparison.OrdinalIgnoreCase) >= 0) return "Giant";
            if (stem.IndexOf("Boar", StringComparison.OrdinalIgnoreCase) >= 0
                || stem.IndexOf("Dodo", StringComparison.OrdinalIgnoreCase) >= 0
                || stem.IndexOf("Wolf", StringComparison.OrdinalIgnoreCase) >= 0
                || stem.IndexOf("Goat", StringComparison.OrdinalIgnoreCase) >= 0
                || stem.IndexOf("Crocodile", StringComparison.OrdinalIgnoreCase) >= 0) return "Wildlife";
            return "Other";
        }

        static int SuggestedXpFor(string category)
        {
            switch (category)
            {
                case "Wildlife":   return 5;
                case "Undead":     return 8;
                case "Blackbeard": return 10;
                case "Crew":       return 20;
                case "Senkamati":  return 15;
                case "Giant":      return 50;
                default:           return 10;
            }
        }

        // "BP_Mob_Boar" -> "Mob_Boar"; "BP_Mob_Crew_Officer_Blackbeard" -> stays minus BP_.
        static string KeywordOf(string stem) =>
            stem.StartsWith("BP_", StringComparison.OrdinalIgnoreCase) ? stem.Substring(3) : stem;

        // "Mob_Crew_Officer_Blackbeard" -> "Crew Officer Blackbeard" (drop the Mob_
        // prefix, underscores to spaces). Auto-derived, deliberately plain.
        static string LabelOf(string keyword)
        {
            var s = keyword;
            if (s.StartsWith("Mob_", StringComparison.OrdinalIgnoreCase)) s = s.Substring(4);
            s = s.Replace('_', ' ').Trim();
            return s.Length == 0 ? keyword : s;
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

        List<KillXpMobKeyword> BuildIndex()
        {
            if (string.IsNullOrWhiteSpace(PaksDir))   throw new InvalidOperationException("KillXpMobCatalog.PaksDir not set");
            if (!Directory.Exists(PaksDir))           throw new InvalidOperationException("KillXpMobCatalog.PaksDir not found: " + PaksDir);
            if (string.IsNullOrWhiteSpace(AesKey))    throw new InvalidOperationException("KillXpMobCatalog.AesKey not set");
            if (string.IsNullOrWhiteSpace(UsmapPath)) throw new InvalidOperationException("KillXpMobCatalog.UsmapPath not set");
            if (!File.Exists(UsmapPath))              throw new InvalidOperationException("KillXpMobCatalog.UsmapPath not found: " + UsmapPath);

            EnsureOodle();

            LogLine("[killxp-catalog] indexing vanilla BP_Mob_* from " + PaksDir);
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
            LogLine("[killxp-catalog] provider mounted: " + provider.Files.Count
                + " virtual files (+" + mounted + " vfs)");

            // Distinct, killable BP_Mob_* asset stems (controllers, weapons, zones,
            // abstract bases, allies and test/onboarding maps are filtered out).
            var killable = new List<(string Stem, bool IsQuest)>();
            var seenStems = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var kv in provider.Files)
            {
                var key = kv.Key;
                if (!key.EndsWith(".uasset", StringComparison.OrdinalIgnoreCase)) continue;

                int lastSlash = key.LastIndexOfAny(new[] { '/', '\\' });
                string fileName = lastSlash >= 0 ? key.Substring(lastSlash + 1) : key;
                if (!fileName.StartsWith("BP_Mob_", StringComparison.OrdinalIgnoreCase)) continue;

                var stem = fileName.Substring(0, fileName.Length - ".uasset".Length);
                if (!seenStems.Add(stem)) continue;

                if (IsKillablePawn(stem, out bool isQuest))
                    killable.Add((stem, isQuest));
            }

            // Runtime UClass name = stem + "_C"; that is what the DLL substring-matches.
            // The preview is computed only over killable pawns - the kill grant fires
            // on enemy deaths, so allies / non-pawns a keyword's substring also hits are
            // never victims and would just be noise in the tooltip.
            var killableClassNames = killable.Select(k => k.Stem + "_C").ToList();

            var entries = new List<KillXpMobKeyword>(killable.Count);
            foreach (var (stem, isQuest) in killable)
            {
                var keyword = KeywordOf(stem);
                var category = isQuest ? "Quest" : CategoryOf(stem);
                var keywordLc = keyword.ToLowerInvariant();
                var matches = killableClassNames
                    .Where(c => c.ToLowerInvariant().Contains(keywordLc))
                    .OrderBy(c => c, StringComparer.OrdinalIgnoreCase)
                    .ToList();

                entries.Add(new KillXpMobKeyword
                {
                    Keyword      = keyword,
                    Label        = LabelOf(keyword),
                    Category     = category,
                    SuggestedXp  = SuggestedXpFor(isQuest ? CategoryOf(stem) : category),
                    MatchesPawns = matches,
                });
            }

            // Group by category, then by label, for a stable readable order.
            entries.Sort((a, b) =>
            {
                int c = string.Compare(a.Category, b.Category, StringComparison.OrdinalIgnoreCase);
                return c != 0 ? c : string.Compare(a.Label, b.Label, StringComparison.OrdinalIgnoreCase);
            });

            LogLine("[killxp-catalog] indexed " + entries.Count + " killable mob keyword(s)");
            return entries;
        }

        void EnsureOodle()
        {
            var here = WindrosePaths.ResolveNativeDllDir();
            var dllPath = Path.Combine(here, OodleHelper.OodleFileName);
            if (!File.Exists(dllPath))
            {
                LogLine("[killxp-catalog] downloading Oodle DLL");
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

    public sealed class KillXpMobKeyword
    {
        public string Keyword;
        public string Label;
        public string Category;
        public int SuggestedXp;
        public List<string> MatchesPawns;
    }
}
