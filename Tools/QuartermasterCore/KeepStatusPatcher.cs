using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UAssetAPI;
using UAssetAPI.UnrealTypes;
using UAssetAPI.Unversioned;
using UAssetAPI.ExportTypes;
using UAssetAPI.PropertyTypes.Objects;
using UAssetAPI.PropertyTypes.Structs;

namespace Windrose.Quartermaster.Core
{
    // "Keep Status": stops the player's food/elixir/comfort buffs from being wiped
    // on death (the "Keep Status" reference mod).
    //
    // Mechanism (derived from VANILLA, generalises the reference mod's effect):
    // the player's death config DataAsset DA_Hero_ActorDeathParams carries, inside
    // its R5BaseActorDeathData struct, a GameplayTagContainer property
    // RemoveEffectWithTagPrefix - the prefix list of gameplay-effect tags the game
    // strips when the hero dies. Vanilla lists the BROAD prefix GAS.Effect.Status,
    // which matches (and therefore removes) EVERY status effect, food/elixir/comfort
    // included. We replace that one broad prefix with a curated set of specific
    // GAS.Effect.Status.* sub-prefixes (DoT, Movement, Stagger, ...) that
    // deliberately omits the food/elixir/comfort sub-tags, so those buffs survive
    // death while the transient debuffs are still cleared. Every other prefix in the
    // container (e.g. GAS.Ability.Melee.BlockMovement) is left untouched.
    //
    // Why UAssetAPI (not a byte overwrite): swapping one tag for ten grows the
    // package name map and the custom-serialised tag container, re-flowing every
    // downstream offset. UAssetAPI rewrites the FName[] on the
    // GameplayTagContainerPropertyData and reserialises the package, exactly the way
    // the reference mod was produced.
    public sealed class KeepStatusPatcher
    {
        // retoc --filter stem for the player death-params DataAsset.
        public const string AssetFilterStem = "DA_Hero_ActorDeathParams";

        // Legacy path the filter produces under the to-legacy staging root.
        public const string AssetVirtualPath =
            "R5/Content/Gameplay/Character/Player/Parameters/Death/DA_Hero_ActorDeathParams.uasset";

        // The GameplayTagContainer property that lists which effects die with the hero.
        const string TagProperty = "RemoveEffectWithTagPrefix";

        // The broad prefix we replace. Removing it (and only it) keeps food/elixir/
        // comfort; a game version that drops this prefix means the mechanic changed,
        // so we fail loudly rather than ship a wrong asset.
        const string BroadStatusTag = "GAS.Effect.Status";

        // Curated GAS.Effect.Status.* sub-prefixes that REPLACE the broad removal -
        // these are the transient/debuff status families that should still be cleared
        // on death. Food/elixir/comfort sub-tags are intentionally absent so they
        // persist. Matches the reference mod's effect.
        static readonly string[] CuratedStatusTags =
        {
            "GAS.Effect.Status.DoT",
            "GAS.Effect.Status.Enviroment",
            "GAS.Effect.Status.Movement",
            "GAS.Effect.Status.Stagger",
            "GAS.Effect.Status.Difficulty",
            "GAS.Effect.Status.Ability",
            "GAS.Effect.Status.Action",
            "GAS.Effect.Status.Block",
            "GAS.Effect.Status.Conquistador",
            "GAS.Effect.Status.Wet",
        };

        public Action<string> Log;

        // Patches the just-extracted vanilla death-params asset in the IoStore staging
        // tree: rewrites RemoveEffectWithTagPrefix to keep buffs. `stagingDir` is the
        // composite legacy root retoc to-legacy populated; `usmapPath` is the
        // unversioned property map UAssetAPI needs to load the cooked DataAsset.
        public KeepStatusPatchResult Patch(string stagingDir, string usmapPath)
        {
            if (string.IsNullOrEmpty(stagingDir)) throw new ArgumentNullException("stagingDir");
            if (string.IsNullOrEmpty(usmapPath)) throw new ArgumentNullException("usmapPath");
            if (!File.Exists(usmapPath))
                throw new FileNotFoundException("Usmap not found: " + usmapPath);

            var relUasset = AssetVirtualPath.Replace('/', Path.DirectorySeparatorChar);
            var assetPath = Path.Combine(stagingDir, relUasset);
            if (!File.Exists(assetPath))
                throw new InvalidOperationException(
                    "Keep Status: expected the vanilla death-params asset at " + assetPath
                    + " after retoc to-legacy, but it is missing - the game container may "
                    + "have moved the asset (filter '" + AssetFilterStem + "').");
            if (!File.Exists(Path.ChangeExtension(assetPath, ".uexp")))
                throw new FileNotFoundException(
                    "Keep Status: legacy uexp sibling not found for " + assetPath
                    + " - expected a uasset/uexp pair from `retoc to-legacy`.");

            var mappings = new Usmap(usmapPath);
            int replaced = RepointTagContainer(assetPath, mappings) ? 1 : 0;
            return new KeepStatusPatchResult { AssetsReplaced = replaced };
        }

        // Loads the death-params DataAsset, swaps the broad GAS.Effect.Status removal
        // prefix for the curated sub-list, and writes it back. Returns true if it
        // changed anything (false if already patched).
        bool RepointTagContainer(string assetPath, Usmap mappings)
        {
            LogLine("Keep Status: loading " + Path.GetFileNameWithoutExtension(assetPath));
            var asset = new UAsset(assetPath, UAssetIo.Ue, mappings);

            var container = asset.Exports.OfType<NormalExport>()
                .SelectMany(e => FindTagContainers(e.Data))
                .FirstOrDefault(c => c.Name != null && c.Name.ToString() == TagProperty);
            if (container == null)
                throw new InvalidOperationException(
                    "Keep Status: no GameplayTagContainer '" + TagProperty + "' found in "
                    + Path.GetFileName(assetPath)
                    + " - the death-params layout changed; cannot adjust the removed-effect list.");

            var current = (container.Value ?? Array.Empty<FName>())
                .Select(n => n != null ? n.ToString() : string.Empty).ToList();

            bool hasBroad = current.Contains(BroadStatusTag);
            bool hasCurated = CuratedStatusTags.All(current.Contains);

            if (!hasBroad && hasCurated)
            {
                LogLine("Keep Status: " + Path.GetFileName(assetPath)
                        + " already keeps status effects - nothing to repoint.");
                return false;
            }
            if (!hasBroad)
                throw new InvalidOperationException(
                    "Keep Status: " + TagProperty + " in " + Path.GetFileName(assetPath)
                    + " does not contain the expected broad prefix '" + BroadStatusTag
                    + "' (found: " + string.Join(", ", current)
                    + "). The game version changed the death removal tags; the patch was "
                    + "not applied to avoid shipping a wrong asset.");

            // Keep every existing prefix except the broad GAS.Effect.Status, then add
            // the curated sub-prefixes (skipping any already present). Order: surviving
            // vanilla tags first (e.g. GAS.Ability.Melee.BlockMovement), then curated.
            var newTags = new List<string>();
            foreach (var t in current)
                if (t != BroadStatusTag && !newTags.Contains(t)) newTags.Add(t);
            foreach (var t in CuratedStatusTags)
                if (!newTags.Contains(t)) newTags.Add(t);

            container.Value = newTags.Select(t => FName.FromString(asset, t)).ToArray();
            asset.Write(assetPath);
            LogLine("Keep Status: rewrote " + TagProperty + " in " + Path.GetFileName(assetPath)
                    + " (" + BroadStatusTag + " -> " + CuratedStatusTags.Length
                    + " curated sub-prefixes; food/elixir/comfort kept, derived from vanilla)");
            return true;
        }

        // Walks a property subtree (structs + arrays) yielding every gameplay-tag
        // container so the target is found regardless of how UAssetAPI nests the
        // custom-serialised GameplayTagContainer under the R5BaseActorDeathData struct.
        static IEnumerable<GameplayTagContainerPropertyData> FindTagContainers(
            IEnumerable<PropertyData> props)
        {
            if (props == null) yield break;
            foreach (var p in props)
            {
                if (p is GameplayTagContainerPropertyData gtc)
                    yield return gtc;
                else if (p is StructPropertyData sp)
                    foreach (var c in FindTagContainers(sp.Value)) yield return c;
                else if (p is ArrayPropertyData ap)
                    foreach (var c in FindTagContainers(ap.Value)) yield return c;
            }
        }

        void LogLine(string msg)
        {
            if (Log != null) Log(msg);
        }
    }

    public sealed class KeepStatusPatchResult
    {
        public int AssetsReplaced;
    }
}
