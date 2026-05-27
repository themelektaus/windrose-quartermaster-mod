using System;
using System.Collections.Generic;
using System.IO;
using UAssetAPI;
using UAssetAPI.UnrealTypes;
using UAssetAPI.Unversioned;

namespace Windrose.Quartermaster.Core
{
    // M3: clones the vanilla CUE_Shanti_10_<Variant> sound cue asset into
    // a new CUE_Shanti_<NewIndex>_<Variant> sound cue that plays the
    // user-supplied SWAV instead of vanilla MaggieMay.
    //
    // The clone is done purely via NameMap rewrites (UAssetAPI's
    // SetNameReference) - no structural changes to the 62-export graph
    // (SoundNodeDelay, SoundNodeWavePlayer, ShipsChatter cross-refs).
    // Empirical recon (.build-tmp/shanties-recon/) confirms that the
    // reference mod Extra Sea Shanties uses the same approach: each new
    // slot's cue is a byte-equivalent clone of vanilla cue 10 with just
    // four FName strings repointed.
    //
    // Why CUE_Shanti_10 as template? It's the same source asset that the
    // reference mod cloned (verified by string-diffing both .uasset binaries
    // against vanilla cue 1..10). Cues 1-9 have the same structure and
    // would work equivalently, but staying on 10 makes vanilla-vs-mod
    // diffing easier when debugging.
    //
    // Four FName replacements per cue (Voice flavor):
    //   CUE_Shanti_10_<Variant>_VoicePlayer
    //     -> CUE_Shanti_<NewIndex>_<Variant>_VoicePlayer
    //   /Game/Audio/Game/Music/Shanti/Ships/<Variant>/CUE_Shanti_10_<Variant>_VoicePlayer
    //     -> /Game/Audio/Game/Music/Shanti/Ships/<Variant>/CUE_Shanti_<NewIndex>_<Variant>_VoicePlayer
    //   SWAV_Shanti_MaggieMay
    //     -> SWAV_Shanti_<NewSwavStem>
    //   /Game/Audio/Game/Music/Shanti/SWAV/SWAV_Shanti_MaggieMay
    //     -> /Game/Audio/Game/Music/Shanti/SWAV/SWAV_Shanti_<NewSwavStem>
    //
    // NoPlayer flavor uses .../VoiceNoPlayer/ in the path and drops the
    // "_<Variant>_VoicePlayer" segment from the cue stem.
    public sealed class ShipMusicAddCueCloner
    {
        public Action<string> Log;

        // The exact UE version we read/write. Tied to Windrose 5.6 just like
        // every other UAssetAPI-based patcher in this project.
        const EngineVersion Ue = EngineVersion.VER_UE5_6;

        // Vanilla template constants. Recon confirmed all four variants
        // (Large/Medium/Small VoicePlayer + NoPlayer) of cue 10 reference
        // SWAV_Shanti_MaggieMay.
        public const string TemplateCueIndex = "10";
        public const string TemplateSwavStem = "SWAV_Shanti_MaggieMay";
        public const string TemplateSwavPath =
            "/Game/Audio/Game/Music/Shanti/SWAV/SWAV_Shanti_MaggieMay";

        // Source-tree-relative path templates for the vanilla cue assets.
        // Caller composes the absolute path by prefixing the staging dir
        // where retoc to-legacy dropped the vanilla extract.
        public const string CueRelDirLarge   = "R5/Content/Audio/Game/Music/Shanti/Ships/Large";
        public const string CueRelDirMedium  = "R5/Content/Audio/Game/Music/Shanti/Ships/Medium";
        public const string CueRelDirSmall   = "R5/Content/Audio/Game/Music/Shanti/Ships/Small";
        public const string CueRelDirNoPlayer= "R5/Content/Audio/Game/Music/Shanti/VoiceNoPlayer";

        // Inputs:
        //   inputUassetPath   - vanilla CUE_Shanti_10_<flavor>.uasset (the
        //                       sibling .uexp is implicit).
        //   outputUassetPath  - new CUE_Shanti_<newIndex>_<flavor>.uasset
        //                       to be written (sibling .uexp written by
        //                       UAssetAPI in the same call).
        //   usmapPath         - shared .usmap mappings (UE5 unversioned).
        //   flavor            - "Large" / "Medium" / "Small" / "NoPlayer".
        //   newIndex          - the new track index as string, e.g. "11".
        //   newSwavStem       - the SWAV stem name (without "SWAV_Shanti_"
        //                       prefix), e.g. "MyTrack" -> binds the cue
        //                       to SWAV_Shanti_MyTrack.
        public ShipMusicAddCueCloneResult Clone(
            string inputUassetPath, string outputUassetPath, string usmapPath,
            string flavor, string newIndex, string newSwavStem)
        {
            if (string.IsNullOrEmpty(inputUassetPath))  throw new ArgumentNullException("inputUassetPath");
            if (string.IsNullOrEmpty(outputUassetPath)) throw new ArgumentNullException("outputUassetPath");
            if (string.IsNullOrEmpty(usmapPath))        throw new ArgumentNullException("usmapPath");
            if (string.IsNullOrEmpty(flavor))           throw new ArgumentNullException("flavor");
            if (string.IsNullOrEmpty(newIndex))         throw new ArgumentNullException("newIndex");
            if (string.IsNullOrEmpty(newSwavStem))      throw new ArgumentNullException("newSwavStem");
            if (!File.Exists(inputUassetPath))
                throw new FileNotFoundException("Vanilla cue not found: " + inputUassetPath);
            if (!File.Exists(usmapPath))
                throw new FileNotFoundException("Usmap not found: " + usmapPath);

            var rules = BuildReplacementRules(flavor, newIndex, newSwavStem);
            var newSwav      = "SWAV_Shanti_" + newSwavStem;

            LogLine("Loading usmap: " + usmapPath);
            var mappings = new Usmap(usmapPath);

            LogLine("Loading cue: " + inputUassetPath + " (flavor=" + flavor + ")");
            var asset = new UAsset(inputUassetPath, Ue, mappings);

            int hits = ApplyRules(asset, rules);
            if (hits != rules.Count)
            {
                throw new InvalidOperationException(
                    "Cue NameMap rename incomplete - matched " + hits + "/" + rules.Count
                    + " expected entries. Did the source asset stem drift away from "
                    + "CUE_Shanti_" + TemplateCueIndex + " / " + TemplateSwavStem + "? "
                    + "Vanilla asset: " + inputUassetPath);
            }

            var outDir = Path.GetDirectoryName(outputUassetPath);
            if (!string.IsNullOrEmpty(outDir)) Directory.CreateDirectory(outDir);

            LogLine("Writing cue clone: " + outputUassetPath);
            asset.Write(outputUassetPath);

            var newCueStem = SelfStem(flavor, newIndex);
            return new ShipMusicAddCueCloneResult
            {
                Flavor          = flavor,
                NewIndex        = newIndex,
                NewCueStem      = newCueStem,
                NewSwavStem     = newSwav,
                OutputUassetPath= outputUassetPath,
                OutputUexpPath  = Path.ChangeExtension(outputUassetPath, ".uexp"),
                NameMapHits     = hits,
            };
        }

        // Convenience: returns the vanilla cue stem for a flavor. Used by
        // the pipeline to compose the template path.
        public static string VanillaCueStem(string flavor)
        {
            if (flavor == "NoPlayer") return "CUE_Shanti_" + TemplateCueIndex + "_VoiceNoPlayer";
            return "CUE_Shanti_" + TemplateCueIndex + "_" + flavor + "_VoicePlayer";
        }

        // Returns the staging-relative directory for a flavor (where the
        // cue asset belongs in /Game/...).
        public static string CueRelDir(string flavor)
        {
            switch (flavor)
            {
                case "Large":    return CueRelDirLarge;
                case "Medium":   return CueRelDirMedium;
                case "Small":    return CueRelDirSmall;
                case "NoPlayer": return CueRelDirNoPlayer;
                default: throw new ArgumentException("Unknown flavor: " + flavor);
            }
        }

        // Returns the cue stem for a (flavor, newIndex) pair.
        public static string SelfStem(string flavor, string newIndex)
        {
            if (flavor == "NoPlayer") return "CUE_Shanti_" + newIndex + "_VoiceNoPlayer";
            return "CUE_Shanti_" + newIndex + "_" + flavor + "_VoicePlayer";
        }

        Dictionary<string, string> BuildReplacementRules(string flavor, string newIndex, string newSwavStem)
        {
            string vanillaSelf, vanillaSelfPath, newSelf, newSelfPath;
            if (flavor == "NoPlayer")
            {
                vanillaSelf     = "CUE_Shanti_" + TemplateCueIndex + "_VoiceNoPlayer";
                vanillaSelfPath = "/Game/Audio/Game/Music/Shanti/VoiceNoPlayer/" + vanillaSelf;
                newSelf         = "CUE_Shanti_" + newIndex + "_VoiceNoPlayer";
                newSelfPath     = "/Game/Audio/Game/Music/Shanti/VoiceNoPlayer/" + newSelf;
            }
            else
            {
                vanillaSelf     = "CUE_Shanti_" + TemplateCueIndex + "_" + flavor + "_VoicePlayer";
                vanillaSelfPath = "/Game/Audio/Game/Music/Shanti/Ships/" + flavor + "/" + vanillaSelf;
                newSelf         = "CUE_Shanti_" + newIndex + "_" + flavor + "_VoicePlayer";
                newSelfPath     = "/Game/Audio/Game/Music/Shanti/Ships/" + flavor + "/" + newSelf;
            }
            var newSwav     = "SWAV_Shanti_" + newSwavStem;
            var newSwavPath = "/Game/Audio/Game/Music/Shanti/SWAV/" + newSwav;
            return new Dictionary<string, string>(StringComparer.Ordinal)
            {
                { vanillaSelf,     newSelf },
                { vanillaSelfPath, newSelfPath },
                { TemplateSwavStem, newSwav },
                { TemplateSwavPath, newSwavPath },
            };
        }

        int ApplyRules(UAsset asset, Dictionary<string, string> rules)
        {
            int hits = 0;
            var nm = asset.GetNameMapIndexList();
            for (int i = 0; i < nm.Count; i++)
            {
                var current = nm[i].Value;
                if (rules.TryGetValue(current, out var replacement))
                {
                    asset.SetNameReference(i, FString.FromString(replacement));
                    LogLine("  NameMap[" + i + "] '" + current + "' -> '" + replacement + "'");
                    hits++;
                }
            }
            return hits;
        }

        void LogLine(string msg)
        {
            if (Log != null) Log(msg);
        }
    }

    public sealed class ShipMusicAddCueCloneResult
    {
        public string Flavor;
        public string NewIndex;
        public string NewCueStem;     // e.g. CUE_Shanti_11_Large_VoicePlayer
        public string NewSwavStem;    // e.g. SWAV_Shanti_MyTrack
        public string OutputUassetPath;
        public string OutputUexpPath;
        public int NameMapHits;
    }
}
