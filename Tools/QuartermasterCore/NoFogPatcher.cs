using System;
using System.Collections.Generic;
using System.IO;

namespace Windrose.Quartermaster.Core
{
    // "No Fog of War": clears the fog-of-war veil on the minimap and the
    // fullscreen world map. The effect is achieved by overriding exactly ONE
    // asset - the map material M_Map (/Game/UI/META/FullscreenMap/Assets/M_Map)
    // - and repointing a single import: the fog RenderTarget reference
    // RT_MapFog -> RT_MapFog_d. RT_MapFog_d does not exist as a cooked asset, so
    // the material samples a null/empty fog mask => nothing is ever veiled, while
    // the fog subsystem itself stays fully intact (no crash).
    //
    // Why the asset route (not the INI route): we used to flip bFogEnabled=True->
    // False in DefaultR5MapSettings.ini. That disables the fog subsystem itself,
    // and the game then dereferences the (now absent) fog RenderTarget during
    // HUD/minimap BeginPlay on world load => hard crash. Repointing the material's
    // import never disables the subsystem, so it does not crash.
    //
    // Why we derive the override from VANILLA (not from a third-party mod file):
    // the change is purely a name-table rename of two FName entries
    // (RT_MapFog and its package path /Game/.../RT_MapFog, each + the "_d"
    // suffix). UAssetAPI rewrites the affected name-table entries and re-flows
    // every downstream summary offset for us, so we patch the freshly-extracted
    // vanilla M_Map in place - shipping nothing but Windrose's own asset plus our
    // one-line edit. The material expression graph (.uexp) is left byte-identical
    // to vanilla (names are referenced by index, not by value, so the .uexp never
    // changes). Verified end-to-end: vanilla -> rename -> retoc to-zen -> to-legacy
    // round-trips to a material whose fog import is unresolvable exactly like the
    // reference mod's, with a byte-identical .uexp.
    public sealed class NoFogPatcher
    {
        // retoc --filter stem for the fullscreen-map material. NOTE: retoc filters
        // are substring matches, so this also pulls sibling assets like
        // M_MapFog_Brush; Patch removes that collateral so only M_Map ships.
        public const string AssetFilterStem = "M_Map";

        // Cooked virtual path (forward-slash) of the map material we patch.
        public const string AssetVirtualPath =
            "R5/Content/UI/META/FullscreenMap/Assets/M_Map.uasset";

        // The fog RenderTarget reference we repoint. The vanilla material imports
        // the live fog RT under both its short object name and its full package
        // path; both name-table entries are renamed to the (non-existent) "_d"
        // variant so the import resolves to null at runtime.
        const string VanillaRtShortName = "RT_MapFog";
        const string NoFogRtShortName   = "RT_MapFog_d";
        const string VanillaRtPackage   = "/Game/UI/META/FullscreenMap/Assets/RT_MapFog";
        const string NoFogRtPackage     = "/Game/UI/META/FullscreenMap/Assets/RT_MapFog_d";

        public Action<string> Log;

        // Patches the just-extracted vanilla M_Map material in the IoStore staging
        // tree: renames the fog RenderTarget import RT_MapFog -> RT_MapFog_d (both
        // the short name and its package path), then deletes any sibling assets the
        // substring filter dragged in (e.g. M_MapFog_Brush) so to-zen ships only
        // M_Map. `stagingDir` is the composite legacy root that retoc to-legacy
        // populated with the vanilla M_Map (existence- and rename-checked here as a
        // game-version sanity gate). `usmapPath` is the unversioned-property map
        // UAssetAPI needs to load the cooked material.
        public NoFogPatchResult Patch(string stagingDir, string usmapPath)
        {
            if (string.IsNullOrEmpty(stagingDir)) throw new ArgumentNullException("stagingDir");
            if (string.IsNullOrEmpty(usmapPath)) throw new ArgumentNullException("usmapPath");

            var relUasset = AssetVirtualPath.Replace('/', Path.DirectorySeparatorChar);
            var assetPath = Path.Combine(stagingDir, relUasset);

            // Sanity gate: the vanilla asset must have extracted here first.
            if (!File.Exists(assetPath))
                throw new InvalidOperationException(
                    "No Fog: expected the vanilla M_Map material at " + assetPath
                    + " after retoc to-legacy, but it is missing - the game container "
                    + "may have moved the asset (filter '" + AssetFilterStem
                    + "'); the fog import cannot be repointed.");

            var replacements = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                { VanillaRtShortName, NoFogRtShortName },
                { VanillaRtPackage,   NoFogRtPackage   },
            };

            // requireAllHits=true makes a renamed/moved fog RenderTarget fail the
            // build loudly (rather than silently shipping an unchanged material).
            var renamer = new DataAssetPatcher { Log = Log };
            renamer.Patch(assetPath, assetPath, usmapPath, replacements, requireAllHits: true);

            LogLine("NoFog: repointed M_Map fog RenderTarget import "
                    + VanillaRtShortName + " -> " + NoFogRtShortName
                    + " (derived from vanilla M_Map)");

            // Drop collateral the substring filter extracted (e.g. M_MapFog_Brush):
            // we only intend to ship M_Map, and re-shipping vanilla-identical
            // siblings just bloats the pak and risks needless overrides.
            int collateral = RemoveCollateral(
                Path.GetDirectoryName(assetPath), Path.GetFileName(AssetVirtualPath));

            return new NoFogPatchResult { AssetsReplaced = 1, CollateralRemoved = collateral };
        }

        // Removes every *.uasset/*.uexp/*.ubulk in the map-assets staging folder
        // except the one we patched (keepBaseName + its .uexp). Only the no-fog
        // source stages into this folder, so this cannot clobber another feature's
        // assets.
        int RemoveCollateral(string assetsDir, string keepBaseName)
        {
            if (string.IsNullOrEmpty(assetsDir) || !Directory.Exists(assetsDir)) return 0;
            var keepUasset = keepBaseName;                                  // M_Map.uasset
            var keepUexp   = Path.ChangeExtension(keepBaseName, ".uexp");   // M_Map.uexp
            int removed = 0;
            foreach (var path in Directory.GetFiles(assetsDir))
            {
                var name = Path.GetFileName(path);
                var ext = Path.GetExtension(path).ToLowerInvariant();
                if (ext != ".uasset" && ext != ".uexp" && ext != ".ubulk") continue;
                if (string.Equals(name, keepUasset, StringComparison.OrdinalIgnoreCase)) continue;
                if (string.Equals(name, keepUexp, StringComparison.OrdinalIgnoreCase)) continue;
                File.Delete(path);
                removed++;
                LogLine("NoFog: dropped filter collateral " + name + " (not shipped)");
            }
            return removed;
        }

        void LogLine(string msg)
        {
            if (Log != null) Log(msg);
        }
    }

    public sealed class NoFogPatchResult
    {
        public int AssetsReplaced;
        public int CollateralRemoved;
    }
}
