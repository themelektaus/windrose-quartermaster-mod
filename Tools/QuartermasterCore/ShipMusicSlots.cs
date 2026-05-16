using System;
using System.Collections.Generic;

namespace Windrose.Quartermaster.Core
{
    // Catalog of the 10 vanilla sea-shanty SoundWave assets the ship-music
    // tab can replace. Single source of truth for both backend (patcher
    // locates virtual paths, validation rejects unknown slots) and frontend
    // (tab renders one card per entry with the human title).
    //
    // The slot identity is the SWAV stem (no .uasset suffix) - it stays
    // stable across UE versions and uniquely identifies the SoundWave
    // asset regardless of how the game's SoundCues route to it. Replacing
    // one SWAV propagates to all four playback contexts that reference
    // it (Small/Medium/Large ship + crew-only VoiceNoPlayer variant) for
    // free; we don't have to touch the Cue graphs at all.
    public static class ShipMusicSlots
    {
        public sealed class SlotInfo
        {
            // SWAV stem - filename without extension, e.g.
            // "SWAV_Shanti_DrunkenSailor". Used as the dictionary key in
            // ShipMusicGlobal.Songs and as the per-profile storage
            // subdirectory name under Profiles/<id>/ShipMusic/<stem>/.
            public string Stem;

            // Virtual asset path under R5/Content/. Mirrors the layout
            // PickaxeRangePatcher uses: tells the patcher where to drop
            // the user's cooked files inside the IoStore staging tree
            // so retoc to-zen lands them at the right in-game path.
            public string VirtualUassetPath;

            // Human-friendly title for the GUI card label, derived from
            // the song's actual name in the wild (the SWAV filename
            // mangles them into PascalCase but they're real shanties).
            public string Title;
        }

        public const string ContentBase =
            "R5/Content/Audio/Game/Music/Shanti/SWAV/";

        // Order matches CUE_Shanti_01..10_<Size>_VoicePlayer numbering as
        // observed in the vanilla audio tree dump. Keeping the same order
        // here makes it easier to cross-reference reports from the ingame
        // shanty queue with the GUI slot list.
        public static readonly IReadOnlyList<SlotInfo> All = new List<SlotInfo>
        {
            new SlotInfo {
                Stem = "SWAV_Shanti_BlowTheManDown",
                Title = "Blow The Man Down",
                VirtualUassetPath = ContentBase + "SWAV_Shanti_BlowTheManDown.uasset",
            },
            new SlotInfo {
                Stem = "SWAV_Shanti_BullyInTheAlley",
                Title = "Bully In The Alley",
                VirtualUassetPath = ContentBase + "SWAV_Shanti_BullyInTheAlley.uasset",
            },
            new SlotInfo {
                Stem = "SWAV_Shanti_DrunkenSailor",
                Title = "Drunken Sailor",
                VirtualUassetPath = ContentBase + "SWAV_Shanti_DrunkenSailor.uasset",
            },
            new SlotInfo {
                Stem = "SWAV_Shanti_GoodMorningLadies",
                Title = "Good Morning Ladies",
                VirtualUassetPath = ContentBase + "SWAV_Shanti_GoodMorningLadies.uasset",
            },
            new SlotInfo {
                Stem = "SWAV_Shanti_LeaveHerJohnny",
                Title = "Leave Her Johnny",
                VirtualUassetPath = ContentBase + "SWAV_Shanti_LeaveHerJohnny.uasset",
            },
            new SlotInfo {
                Stem = "SWAV_Shanti_MaggieMay",
                Title = "Maggie May",
                VirtualUassetPath = ContentBase + "SWAV_Shanti_MaggieMay.uasset",
            },
            new SlotInfo {
                Stem = "SWAV_Shanti_OldMaui",
                Title = "Old Maui",
                VirtualUassetPath = ContentBase + "SWAV_Shanti_OldMaui.uasset",
            },
            new SlotInfo {
                Stem = "SWAV_Shanti_RollingHome",
                Title = "Rolling Home",
                VirtualUassetPath = ContentBase + "SWAV_Shanti_RollingHome.uasset",
            },
            new SlotInfo {
                Stem = "SWAV_Shanti_TheBritishTars",
                Title = "The British Tars",
                VirtualUassetPath = ContentBase + "SWAV_Shanti_TheBritishTars.uasset",
            },
            new SlotInfo {
                Stem = "SWAV_Shanti_WhiskeyJohnny",
                Title = "Whiskey Johnny",
                VirtualUassetPath = ContentBase + "SWAV_Shanti_WhiskeyJohnny.uasset",
            },
        };

        // O(1) stem -> info lookup. Validation reject path: any stem the
        // user (or a tampered profile.json) supplies that isn't in this
        // dict gets rejected with a clear "unknown shanty slot" error.
        public static readonly IReadOnlyDictionary<string, SlotInfo> ByStem = BuildByStem();

        static Dictionary<string, SlotInfo> BuildByStem()
        {
            var d = new Dictionary<string, SlotInfo>(StringComparer.OrdinalIgnoreCase);
            foreach (var s in All) d[s.Stem] = s;
            return d;
        }

        public static bool IsKnown(string stem)
        {
            return !string.IsNullOrEmpty(stem) && ByStem.ContainsKey(stem);
        }
    }
}
