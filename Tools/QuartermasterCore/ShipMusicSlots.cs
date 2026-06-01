using System;
using System.Collections.Generic;

namespace Windrose.Quartermaster.Core
{
    public static class ShipMusicSlots
    {
        public sealed class SlotInfo
        {
            public string Stem;
            public string VirtualUassetPath;
            public string Title;
        }

        public const string ContentBase =
            "R5/Content/Audio/Game/Music/Shanti/SWAV/";

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
