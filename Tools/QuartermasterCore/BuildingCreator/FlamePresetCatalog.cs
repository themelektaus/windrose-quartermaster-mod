using System;
using System.Collections.Generic;
using System.Linq;

namespace Windrose.Quartermaster.Core.BuildingCreator
{
    // Etappe J: Flame-FX presets that can be attached to any Building.
    //
    // Concept:
    //   - User picks a Flame Preset ("Torch", "Candle", ...) per building
    //     in the GUI. Default is None (no flame).
    //   - The build pipeline clones a Vanilla "fire building" Blueprint
    //     (e.g. BP_BuildingBlock_FloorTorch_C) under our mod path, then
    //     patches the cloned Building DA so its ItemClass soft-class-ref
    //     points at our cloned BP. Result ingame: when the building is
    //     placed, the cloned BP spawns - which carries NiagaraComponent
    //     (flame FX), ChildActor (BP_PointLight_TorchFire_C light), and
    //     AudioComponent (loop SFX) by inheritance.
    //
    // Per preset we record:
    //   - Id: stable identifier referenced from CustomBuilding.FlamePresetId
    //   - DisplayName: shown in the GUI dropdown
    //   - VanillaBpStem / VanillaBpPath: the Vanilla BP we clone. The
    //     BlueprintPatcher runs `retoc to-legacy --filter <stem>` to extract
    //     the .uasset, then rewrites its NameMap so the package path and
    //     class name point at our mod-owned clone.
    //   - ClonedBpStem: the stem we emit under /Game/Quartermaster/Items/
    //     (without the trailing "_C" suffix - BP package stem, not class).
    //     The class name appended at runtime is "<ClonedBpStem>_C".
    //
    // Why we ship one cloned BP per preset (shared across all buildings of
    // that preset), not per-building:
    //   - The Vanilla BP already carries the right NiagaraComponent + Light
    //     setup as SCS-Components. We don't need to mutate per-Building.
    //   - The Mesh assignment comes from the DA at runtime (the engine
    //     reads BuildingItem.PreviewMeshes[0] when constructing the
    //     building actor; the BP's own StaticMeshComponent is set from
    //     there - we don't need to clear it in the BP).
    //   - Less pak bloat: one ~6 KB cloned BP instead of one per building.
    //
    // Position of the flame: Phase 1 uses the Vanilla BP's hardcoded
    // NiagaraComponent.RelativeLocation (= top of the Vanilla torch mesh).
    // This works perfectly when the user's mesh has the flame point at the
    // same Z height as the Vanilla torch (~150 cm). Phase 2 will read the
    // "flame" socket transform from the user's StaticMesh and rewrite the
    // BP's NiagaraComponent + ChildActor positions per-building - that
    // step is gated until the BlueprintPatcher's SCS-rewrite is in place.
    public static class FlamePresetCatalog
    {
        public sealed class FlamePreset
        {
            public string Id;
            public string DisplayName;
            public string Description;

            // Vanilla BP donor (where we extract the FX + Light SCS-Components
            // from). The patcher runs retoc to-legacy on this stem then
            // rewrites the NameMap to retarget the package self-ref.
            public string VanillaBpStem;
            public string VanillaBpPath;

            // Etappe J v3: BP-Clones are PER-BUILDING (not shared per-preset)
            // so each building can have its hardcoded vanilla-mesh ref
            // (e.g. SM_TorchT01_01 baked into the BP's StaticMeshComponent
            // SCS-Node) rewritten to its own user-cooked mesh. The shared
            // per-preset approach v1/v2 used left every flame-enabled
            // building rendering the vanilla torch mesh on top of (or
            // replacing) the user mesh - empirically verified in-game.
            //
            // The clone-stem is now derived from BuildingId at runtime:
            //   "BP_QmFlaming_<BuildingId>"
            // See ClonedBpStemFor() / ClonedClassPathFor() / ClonedPackagePathFor().
            public static string ClonedBpStemFor(string buildingId)
                => "BP_QmFlaming_" + buildingId;
            public static string ClonedClassPathFor(string buildingId)
                => "/Game/Quartermaster/Items/" + ClonedBpStemFor(buildingId)
                   + "." + ClonedBpStemFor(buildingId) + "_C";
            public static string ClonedPackagePathFor(string buildingId)
                => "/Game/Quartermaster/Items/" + ClonedBpStemFor(buildingId);

            // ----- Source-template overrides (set when FlamePresetId is set
            // on a building, the user's chosen template is OVERRIDDEN with
            // these vanilla refs).
            //
            // Why we override the whole template instead of just adding an
            // ItemClass property: R5BuildingItem DAs surface as RawExport in
            // UAssetAPI (CollisionApproximation has a custom C++ serializer
            // the unversioned-property walker can't pass), so we can't
            // add a missing ItemClass property cleanly. Instead we clone a
            // Vanilla DA that ALREADY has ItemClass set (e.g. DA_BI_FloorTorch),
            // and via the existing NameMap-rewrite path redirect the
            // ItemClass FName entries from the vanilla BP to our cloned BP.
            //
            // Trade-off: when a flame preset is selected, the building
            // INHERITS the source flame DA's gameplay properties (snap
            // rules, GameplayTag = Comfort.Lighting, recipe class, etc.)
            // - NOT the user-picked template's. That's deliberate and
            // matches the user's intent ("a torch with my mesh").
            public string SourceVanillaDaStem;
            public string SourceVanillaDaPath;
            public string SourceVanillaNameKey;
            public string SourceVanillaDescriptionKey;
            public string SourceVanillaMeshStem;
            public string SourceVanillaMeshPath;
            public string SourceVanillaIconStem;
            public string SourceVanillaIconPath;
            public string SourceVanillaRecipeJsonPath;
            public string SourceVanillaRecipeStem;
            public string SourceVanillaRecipePackagePath;

            // The Vanilla BP class FName as it appears in the source DA's
            // NameMap (the FSoftClassPath that backs ItemClass references it
            // via FName indices). Both the path and the class-with-"_C"
            // variant need rewriting so the cloned DA's ItemClass points at
            // our cloned BP instead of the vanilla one.
            //
            // Example for the Torch preset:
            //   SourceVanillaItemClassStem = "BP_BuildingBlock_FloorTorch"
            //   SourceVanillaItemClassPath = "/Game/Gameplay/Building/Actors/Furniture/BP_BuildingBlock_FloorTorch"
            public string SourceVanillaItemClassStem;
            public string SourceVanillaItemClassPath;

            // Produces an effective BuildingTemplate that overrides the
            // user's chosen template with this preset's flame-source refs.
            // The patcher then clones the flame source DA + rewrites
            // mesh/icon/recipe/itemclass FNames in one pass.
            public BuildingTemplate ApplyTo(BuildingTemplate baseTemplate)
            {
                if (baseTemplate == null) throw new ArgumentNullException("baseTemplate");
                return new BuildingTemplate
                {
                    // Preserve the user-facing template id + display name
                    // for diagnostics in build logs (so a build log line
                    // like "Patching building 'X' (template=Bedroll+flame:torch)"
                    // tells the user which preset took over).
                    Id          = baseTemplate.Id + "+flame:" + Id,
                    DisplayName = baseTemplate.DisplayName,
                    Description = baseTemplate.Description + " (flame: " + DisplayName + ")",

                    // CategoryTag stays whatever the user chose - the
                    // build-tab filter is independent of which DA we clone.
                    CategoryTag = baseTemplate.CategoryTag,

                    // ALL vanilla refs come from the flame source.
                    VanillaDaStem            = SourceVanillaDaStem,
                    VanillaDaPath            = SourceVanillaDaPath,
                    VanillaNameKey           = SourceVanillaNameKey,
                    VanillaDescriptionKey    = SourceVanillaDescriptionKey,
                    VanillaMeshStem          = SourceVanillaMeshStem,
                    VanillaMeshPath          = SourceVanillaMeshPath,
                    VanillaIconStem          = SourceVanillaIconStem,
                    VanillaIconPath          = SourceVanillaIconPath,
                    VanillaRecipeJsonPath    = SourceVanillaRecipeJsonPath,
                    VanillaRecipeStem        = SourceVanillaRecipeStem,
                    VanillaRecipePackagePath = SourceVanillaRecipePackagePath,
                };
            }
        }

        // Preset table. Order = GUI dropdown order.
        //
        // Adding a new preset:
        //   1. Pick a Vanilla "fire building" BP that has the FX + Light
        //      setup you want (Niagara asset + Light-BP referenced as
        //      SCS-Components).
        //   2. Run `retoc to-legacy --filter <BpStem>` once in a scratch
        //      dir to verify the BP extracts cleanly.
        //   3. Add an entry below with a unique Id. The ClonedBpStem
        //      convention is "BP_QmFlaming_<TitleCase>".
        //   4. Test ingame.
        //
        // Discovery findings (see conversation log 2026-05-21):
        //   - BP_BuildingBlock_FloorTorch references FX_Flame_FloorTorch +
        //     BP_PointLight_TorchFire (flickering via MI_LF_Dimming_FireSmall
        //     light-function) + MS_BuildingFireTorch audio loop.
        public static readonly IReadOnlyList<FlamePreset> Presets = new[]
        {
            new FlamePreset
            {
                Id            = "torch",
                DisplayName   = "Torch",
                Description   = "Flickering torch flame with warm point light and ambient loop SFX. Cloned from vanilla FloorTorch.",
                VanillaBpStem = "BP_BuildingBlock_FloorTorch",
                VanillaBpPath = "/Game/Gameplay/Building/Actors/Furniture/BP_BuildingBlock_FloorTorch",

                // Source DA values - verified by retoc to-legacy + NameMap
                // dump of vanilla DA_BI_FloorTorch (see flame-da-probe).
                SourceVanillaDaStem      = "DA_BI_FloorTorch",
                SourceVanillaDaPath      = "/Game/Gameplay/Building/BuildingDecoration/DA_BI_FloorTorch",
                // FText keys: left null for now (the binary rewriter is
                // skipped, the building shows up under the vanilla
                // "Floor Torch" name + description ingame). Probing the
                // exact key strings in the DA's RawExport body is a
                // future polish step.
                SourceVanillaNameKey         = null,
                SourceVanillaDescriptionKey  = null,

                SourceVanillaMeshStem = "SM_TorchT01_01",
                SourceVanillaMeshPath = "/Game/Environment/Gameplay/Building/Furniture/FurnitureSet_T01/SM_TorchT01_01",

                SourceVanillaIconStem = "T_TorchT01_01",
                SourceVanillaIconPath = "/Game/UI/HUD/Building/Icons/BuildingBits/T_TorchT01_01",

                SourceVanillaRecipeJsonPath    = "R5/Plugins/R5BusinessRules/Content/Recipes/Building/Items/Decorations/DA_RD_BuildObject_Deco_Lights_T01_Torch.json",
                SourceVanillaRecipeStem        = "DA_RD_BuildObject_Deco_Lights_T01_Torch",
                SourceVanillaRecipePackagePath = "/R5BusinessRules/Recipes/Building/Items/Decorations/DA_RD_BuildObject_Deco_Lights_T01_Torch",

                SourceVanillaItemClassStem = "BP_BuildingBlock_FloorTorch",
                SourceVanillaItemClassPath = "/Game/Gameplay/Building/Actors/Furniture/BP_BuildingBlock_FloorTorch",
            },
        };

        // Returns the preset for a given Id, or null if Id is null/empty/
        // unknown. Empty string + null both map to "no flame preset" and
        // return null without warning - that's the default state for any
        // building that hasn't explicitly opted in.
        public static FlamePreset Resolve(string presetId)
        {
            if (string.IsNullOrWhiteSpace(presetId)) return null;
            var trimmed = presetId.Trim();
            return Presets.FirstOrDefault(p =>
                string.Equals(p.Id, trimmed, StringComparison.OrdinalIgnoreCase));
        }

        // Lightweight DTO for the GUI dropdown (no Vanilla-path fields to
        // avoid leaking internal asset paths into the frontend).
        public sealed class FlamePresetDto
        {
            public string id;
            public string displayName;
            public string description;
        }

        public static IReadOnlyList<FlamePresetDto> GetDtos()
        {
            return Presets.Select(p => new FlamePresetDto
            {
                id          = p.Id,
                displayName = p.DisplayName,
                description = p.Description,
            }).ToList();
        }
    }
}
