using System.IO;
using CUE4Parse.FileProvider;
using CUE4Parse.MappingsProvider;
using CUE4Parse.UE4.Assets;
using CUE4Parse.UE4.Readers;
using CUE4Parse.UE4.Versions;

namespace Windrose.Quartermaster.Core
{
    // Helpers for reading a standalone legacy .uasset/.uexp pair with CUE4Parse
    // (the project's robust cooked-asset reader) without mounting any paks - only
    // the usmap mappings are needed to deserialize unversioned properties. Used by
    // LandFastTravelPatcher to analyze and verify the fast-travel-bell DataAssets,
    // which UAssetAPI cannot parse (custom-serialized R5CollisionApproximation).
    static class UAssetCue
    {
        public const EGame Game = EGame.GAME_UE5_6;

        // A provider that carries only the usmap mappings (no mounted paks). CUE4Parse
        // reads unversioned property schemas from provider.MappingsForGame.
        public static IFileProvider MappingsProvider(string usmapPath)
        {
            var tmp = Path.Combine(Path.GetTempPath(), "qm-lft-mapsonly");
            Directory.CreateDirectory(tmp);
            var provider = new DefaultFileProvider(tmp, SearchOption.TopDirectoryOnly,
                new VersionContainer(Game));
            provider.MappingsContainer = new FileUsmapTypeMappingsProvider(usmapPath);
            return provider;
        }

        public static Package LoadStandalone(string uassetPath, string usmapPath)
        {
            var uexpPath = Path.ChangeExtension(uassetPath, ".uexp");
            var versions = new VersionContainer(Game);
            var uassetAr = new FByteArchive(uassetPath, File.ReadAllBytes(uassetPath), versions);
            var uexpAr = new FByteArchive(uexpPath, File.ReadAllBytes(uexpPath), versions);
            return new Package(uassetAr, uexpAr, (FArchive)null, (FArchive)null,
                MappingsProvider(usmapPath), useLazySerialization: false);
        }
    }
}
