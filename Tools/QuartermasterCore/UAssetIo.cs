using UAssetAPI.UnrealTypes;

namespace Windrose.Quartermaster.Core
{
    static class UAssetIo
    {
        // Engine version every UAsset/Usmap load pins to. Single source of truth
        // so a game engine bump is a one-line change.
        public const EngineVersion Ue = EngineVersion.VER_UE5_6;
    }
}
