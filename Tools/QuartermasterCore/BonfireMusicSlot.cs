namespace Windrose.Quartermaster.Core
{
    public static class BonfireMusicSlot
    {
        public const string Stem = "SWAV_Music_BuildingCenter_v3";

        public const string Title = "The Hearth";

        public const string VirtualUassetPath =
            "R5/Content/Audio/Game/Music/SWAV_Music_BuildingCenter_v3.uasset";

        public static ShipMusicSlots.SlotInfo ToSlotInfo()
        {
            return new ShipMusicSlots.SlotInfo
            {
                Stem = Stem,
                Title = Title,
                VirtualUassetPath = VirtualUassetPath,
            };
        }
    }
}
