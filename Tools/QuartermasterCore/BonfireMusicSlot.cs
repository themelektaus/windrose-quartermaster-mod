namespace Windrose.Quartermaster.Core
{
    // Single-slot catalog for the vanilla bonfire / building-center
    // hearth theme. Mirrors ShipMusicSlots.SlotInfo but there's only
    // one slot to overwrite, so we expose constants + a factory that
    // returns a SlotInfo the ShipMusicPatcher can consume directly.
    //
    // The asset is referenced by MS_Music_BuildingCenter (a UE5
    // MetaSound) and gets played by BP_BuildingCenter when the player
    // enters the comfort zone. Swapping the SWAV bytes alone propagates
    // to every playback context the MetaSound uses without us having to
    // touch the MetaSound graph or the MIX snapshot.
    public static class BonfireMusicSlot
    {
        // SWAV stem (filename without extension). Used as the dictionary
        // / NameMap rename target inside the SoundWave_BinkInline
        // template and as the per-profile storage subdirectory name.
        public const string Stem = "SWAV_Music_BuildingCenter_v3";

        // Human-friendly title for GUI rendering.
        public const string Title = "The Hearth";

        // Virtual asset path under R5/Content/. The build pipeline drops
        // the cooked triplet at this exact path inside the IoStore
        // staging tree so retoc to-zen lands it where the engine
        // resolves the file at runtime.
        public const string VirtualUassetPath =
            "R5/Content/Audio/Game/Music/SWAV_Music_BuildingCenter_v3.uasset";

        // Returns a ShipMusicSlots.SlotInfo wrapper suitable for handing
        // to the existing ShipMusicPatcher. The patcher only cares about
        // Stem (for NameMap rename + FolderName rewrite) + the virtual
        // path (for staging-dir placement); it doesn't carry any
        // shanty-specific state.
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
