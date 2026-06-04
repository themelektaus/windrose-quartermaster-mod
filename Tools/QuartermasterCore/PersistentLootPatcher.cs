using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UAssetAPI;
using UAssetAPI.UnrealTypes;
using UAssetAPI.Unversioned;
using UAssetAPI.ExportTypes;
using UAssetAPI.PropertyTypes.Objects;

namespace Windrose.Quartermaster.Core
{
    // "Persistent Loot": stops the player's death-loot drop from despawning, so
    // dropped items stay in the world indefinitely - both on land and at sea.
    //
    // Mechanism (derived from VANILLA, generalises the reference mod's effect):
    // the spawn abilities GA_SpawnPosthumousContainer (land) and
    // GA_SpawnPosthumousContainer_Ship (ship) each carry, on their CDO, a
    // SoftObject property PosthumousContainerClass that decides WHICH actor spawns
    // at the death spot. Vanilla spawns BP_PosthumousContainer / its _Ship variant
    // - both subclasses of the native R5PosthumousContainer, whose despawn timer
    // lives in C++/business-logic and exposes no patchable asset property. We
    // repoint each ability's soft class reference to BP_Storage_DecorBag_02, a
    // placeable storage box (R5LootableInventoryBox) with no despawn logic, so the
    // loot persists. The bag is a STATIC placeable (ActorRegistrator-registered, no
    // physics/buoyancy sim), so it holds its spawn transform - safe at the sea
    // death point too (it stays at the surface position rather than sinking).
    //
    // Why UAssetAPI (not a byte overwrite like the curve/material patchers): the
    // two soft references differ in length, so swapping them grows the name table
    // and re-flows every downstream summary offset. UAssetAPI rewrites the soft
    // path's two FName entries and reserialises the package for us, exactly the way
    // the reference mod was produced. Verified end-to-end: vanilla -> repoint ->
    // retoc to-zen -> to-legacy round-trips to abilities that spawn
    // BP_Storage_DecorBag_02 (target confirmed to exist in vanilla).
    public sealed class PersistentLootPatcher
    {
        // retoc --filter stem for the death-container spawn abilities. NOTE: retoc
        // filters are substring matches; this stem matches BOTH the land ability
        // and the _Ship variant, which is exactly what we want to patch.
        public const string AssetFilterStem = "GA_SpawnPosthumousContainer";

        // The CDO soft-class property that selects the spawned death container.
        const string SpawnClassProperty = "PosthumousContainerClass";

        // Persistent replacement (a placeable storage bag with no despawn timer).
        const string PersistentPackage = "/Game/Gameplay/Inventory/BP_Storage_DecorBag_02";
        const string PersistentAsset   = "BP_Storage_DecorBag_02_C";

        // The abilities we repoint, each with its expected vanilla target (a
        // game-version rename of either must fail the build loudly rather than
        // silently ship a wrong reference). The land ability is required; the ship
        // ability is optional only as a forward-compat guard (a future build that
        // drops it should not hard-fail).
        sealed class AbilityTarget
        {
            public string VirtualPath;
            public string VanillaPackage;
            public string VanillaAsset;
            public bool Required;
        }

        static readonly AbilityTarget[] Targets =
        {
            new AbilityTarget
            {
                VirtualPath = "R5/Content/Gameplay/Character/Player/GameplayAbilities/Death/GA_SpawnPosthumousContainer.uasset",
                VanillaPackage = "/Game/Gameplay/Character/Player/GameplayAbilities/Death/BP_PosthumousContainer",
                VanillaAsset = "BP_PosthumousContainer_C",
                Required = true,
            },
            new AbilityTarget
            {
                VirtualPath = "R5/Content/Gameplay/Character/Player/GameplayAbilities/Death/GA_SpawnPosthumousContainer_Ship.uasset",
                VanillaPackage = "/Game/Gameplay/Water/ShipDropContainer/BP_PosthumousContainer_Ship",
                VanillaAsset = "BP_PosthumousContainer_Ship_C",
                Required = false,
            },
        };

        public Action<string> Log;

        // Patches the just-extracted vanilla death-spawn abilities in the IoStore
        // staging tree: repoints each PosthumousContainerClass to
        // BP_Storage_DecorBag_02_C, then deletes any sibling assets the substring
        // filter dragged in that are not one of our targets. `stagingDir` is the
        // composite legacy root that retoc to-legacy populated; `usmapPath` is the
        // unversioned property map UAssetAPI needs to load the cooked abilities.
        public PersistentLootPatchResult Patch(string stagingDir, string usmapPath)
        {
            if (string.IsNullOrEmpty(stagingDir)) throw new ArgumentNullException("stagingDir");
            if (string.IsNullOrEmpty(usmapPath)) throw new ArgumentNullException("usmapPath");
            if (!File.Exists(usmapPath))
                throw new FileNotFoundException("Usmap not found: " + usmapPath);

            var mappings = new Usmap(usmapPath);
            int replaced = 0;
            var keepNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var target in Targets)
            {
                var relUasset = target.VirtualPath.Replace('/', Path.DirectorySeparatorChar);
                var assetPath = Path.Combine(stagingDir, relUasset);

                if (!File.Exists(assetPath))
                {
                    if (target.Required)
                        throw new InvalidOperationException(
                            "Persistent Loot: expected the vanilla ability at " + assetPath
                            + " after retoc to-legacy, but it is missing - the game container "
                            + "may have moved the asset (filter '" + AssetFilterStem
                            + "'); the death-container class cannot be repointed.");
                    LogLine("Persistent Loot: optional ability " + Path.GetFileName(assetPath)
                            + " not present - skipping.");
                    continue;
                }

                keepNames.Add(Path.GetFileName(assetPath));
                keepNames.Add(Path.GetFileName(Path.ChangeExtension(assetPath, ".uexp")));

                if (!File.Exists(Path.ChangeExtension(assetPath, ".uexp")))
                    throw new FileNotFoundException(
                        "Persistent Loot: legacy uexp sibling not found for " + assetPath
                        + " - expected a uasset/uexp pair from `retoc to-legacy`.");

                if (RepointAbility(assetPath, mappings, target)) replaced++;
            }

            // Drop collateral the substring filter extracted that is not one of our
            // abilities. With both land + ship abilities kept this is normally a
            // no-op, but it stays as a safety net (and uses the union of every
            // target's folder so nothing else is touched).
            int collateral = RemoveCollateral(stagingDir, keepNames);

            return new PersistentLootPatchResult { AssetsReplaced = replaced, CollateralRemoved = collateral };
        }

        // Loads one cooked ability, repoints PosthumousContainerClass from the
        // expected vanilla target to the persistent bag, and writes it back in
        // place. Returns true if it repointed (false if already repointed).
        bool RepointAbility(string assetPath, Usmap mappings, AbilityTarget target)
        {
            LogLine("Persistent Loot: loading " + Path.GetFileNameWithoutExtension(assetPath));
            var asset = new UAsset(assetPath, UAssetIo.Ue, mappings);

            // The ability default object is the "Default__<ClassName>_C" NormalExport.
            var cdo = asset.Exports.OfType<NormalExport>().FirstOrDefault(
                e => e.ObjectName != null
                  && e.ObjectName.ToString().StartsWith("Default__", StringComparison.Ordinal));
            if (cdo == null)
                throw new InvalidOperationException(
                    "Persistent Loot: no CDO (Default__*) export in " + assetPath
                    + " - the ability layout changed; cannot locate PosthumousContainerClass.");

            var spawnName = FName.FromString(asset, SpawnClassProperty);
            var soft = cdo.Data.FirstOrDefault(p => p.Name == spawnName) as SoftObjectPropertyData;
            if (soft == null)
                throw new InvalidOperationException(
                    "Persistent Loot: CDO has no SoftObject property '" + SpawnClassProperty
                    + "' in " + Path.GetFileName(assetPath)
                    + " - the ability layout changed; cannot repoint the death container.");

            var path = soft.Value;
            var top = path.AssetPath;
            var curPkg = top.PackageName?.Value?.Value ?? string.Empty;
            var curAsset = top.AssetName?.Value?.Value ?? string.Empty;

            if (curAsset == PersistentAsset && curPkg == PersistentPackage)
            {
                LogLine("Persistent Loot: " + Path.GetFileName(assetPath) + " already points at "
                        + PersistentAsset + " - nothing to repoint.");
                return false;
            }
            if (curAsset != target.VanillaAsset || curPkg != target.VanillaPackage)
                throw new InvalidOperationException(
                    "Persistent Loot: " + SpawnClassProperty + " in " + Path.GetFileName(assetPath)
                    + " points at an unexpected class '" + curPkg + "." + curAsset
                    + "' (expected vanilla '" + target.VanillaPackage + "." + target.VanillaAsset
                    + "'). The game version changed the death-container ability; the patch was "
                    + "not applied to avoid shipping a wrong reference.");

            top.PackageName = FName.FromString(asset, PersistentPackage);
            top.AssetName = FName.FromString(asset, PersistentAsset);
            path.AssetPath = top;
            soft.Value = path;
            asset.Write(assetPath);
            LogLine("Persistent Loot: repointed " + SpawnClassProperty + " in "
                    + Path.GetFileName(assetPath) + " " + target.VanillaAsset + " -> "
                    + PersistentAsset + " (derived from vanilla)");
            return true;
        }

        // Removes every *.uasset/*.uexp/*.ubulk in the patched abilities' folder(s)
        // that is not one of the assets we kept. Only the persistent-loot source
        // stages into these folders, so this cannot clobber another feature's assets.
        int RemoveCollateral(string stagingDir, HashSet<string> keepNames)
        {
            var dirs = Targets
                .Select(t => Path.GetDirectoryName(
                    Path.Combine(stagingDir, t.VirtualPath.Replace('/', Path.DirectorySeparatorChar))))
                .Where(d => !string.IsNullOrEmpty(d) && Directory.Exists(d))
                .Distinct(StringComparer.OrdinalIgnoreCase);

            int removed = 0;
            foreach (var dir in dirs)
            {
                foreach (var p in Directory.GetFiles(dir))
                {
                    var name = Path.GetFileName(p);
                    var ext = Path.GetExtension(p).ToLowerInvariant();
                    if (ext != ".uasset" && ext != ".uexp" && ext != ".ubulk") continue;
                    if (keepNames.Contains(name)) continue;
                    File.Delete(p);
                    removed++;
                    LogLine("Persistent Loot: dropped filter collateral " + name + " (not shipped)");
                }
            }
            return removed;
        }

        void LogLine(string msg)
        {
            if (Log != null) Log(msg);
        }
    }

    public sealed class PersistentLootPatchResult
    {
        public int AssetsReplaced;
        public int CollateralRemoved;
    }
}
