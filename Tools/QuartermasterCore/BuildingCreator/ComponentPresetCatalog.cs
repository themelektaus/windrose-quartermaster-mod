using System;
using System.Collections.Generic;
using System.Linq;

namespace Windrose.Quartermaster.Core.BuildingCreator
{
    // Etappe J / Audio extension: Component-FX presets that can be attached
    // to any Building.
    //
    // History: This catalog was originally introduced as FlamePresetCatalog
    // for the Torch flame preset. The "Audio" extension generalized it to
    // ANY vanilla BP donor whose SCS-Components we want to inherit (Niagara
    // FX, point lights, ambient audio loops, ...). The class was renamed
    // accordingly; the JSON wire-format key remains flamePresetId on the
    // profile side for backward compat (handled in ProfileStore + the
    // ComponentPresetId-aliased setter on CustomBuilding).
    //
    // Concept:
    //   - User picks a Component Preset ("Torch", "Audio", ...) per
    //     building in the GUI. Default is None (no preset).
    //   - The build pipeline clones a Vanilla donor Blueprint (e.g.
    //     BP_BuildingBlock_FloorTorch_C for Torch, BP_BuildingBlock_
    //     PendulumClockT04_01_C for Audio) under our mod path, then patches
    //     the cloned Building DA so its ItemClass soft-class-ref points at
    //     our cloned BP. Result ingame: when the building is placed, the
    //     cloned BP spawns - which carries the donor's SCS-Components by
    //     inheritance (NiagaraComponent / Light / AudioComponent / ...).
    //
    // Per preset we record:
    //   - Id: stable identifier referenced from CustomBuilding.ComponentPresetId
    //   - DisplayName: shown in the GUI dropdown
    //   - Kind: discriminator (Flame vs Audio) - controls pipeline routing
    //     (Flame REQUIRES a socket on the user mesh; Audio does not).
    //   - NamePrefix: used to derive the cloned BP stem so two preset kinds
    //     for the same building Id don't collide (BP_QmFlaming_<id> vs
    //     BP_QmAudio_<id>).
    //   - VanillaBpStem / VanillaBpPath: the Vanilla BP we clone.
    //   - Source-DA refs: the vanilla DA we clone instead of the user's
    //     chosen template (because the donor DA already has ItemClass set,
    //     enabling the FName-rewrite path). The user's mesh / icon / recipe
    //     refs are then patched back into the cloned DA via NameMap.
    public enum ComponentPresetKind
    {
        Flame,
        Audio,
    }

    public static class ComponentPresetCatalog
    {
        public sealed class ComponentPreset
        {
            public string Id;
            public string DisplayName;
            public string Description;

            public ComponentPresetKind Kind;

            // Prefix injected into the cloned BP stem. Flame: "Flaming"
            // (preserves the existing BP_QmFlaming_<id> wire-format used by
            // existing profiles). Audio: "Audio" (new).
            public string NamePrefix;

            // Vanilla BP donor (where we extract the FX + Audio + Light SCS-
            // Components from). The patcher runs retoc to-legacy on this
            // stem then rewrites the NameMap to retarget the package self-
            // ref.
            public string VanillaBpStem;
            public string VanillaBpPath;

            // BP-Clones are PER-BUILDING (not shared per-preset) so each
            // building can have its hardcoded vanilla-mesh ref (e.g.
            // SM_TorchT01_01 baked into the BP's StaticMeshComponent SCS-
            // Node) rewritten to its own user-cooked mesh. See historical
            // commit log around the FlamePreset v3 transition for
            // background.
            public static string ClonedBpStemFor(ComponentPreset preset, string buildingId)
            {
                if (preset == null) throw new ArgumentNullException("preset");
                var prefix = string.IsNullOrEmpty(preset.NamePrefix) ? "Comp" : preset.NamePrefix;
                return "BP_Qm" + prefix + "_" + buildingId;
            }
            public static string ClonedClassPathFor(ComponentPreset preset, string buildingId)
                => WindrosePaths.ModItemsPackagePath + ClonedBpStemFor(preset, buildingId)
                   + "." + ClonedBpStemFor(preset, buildingId) + "_C";
            public static string ClonedPackagePathFor(ComponentPreset preset, string buildingId)
                => WindrosePaths.ModItemsPackagePath + ClonedBpStemFor(preset, buildingId);

            // ----- Source-template overrides (set when ComponentPresetId is
            // set on a building, the user's chosen template is OVERRIDDEN
            // with these vanilla refs).
            //
            // Why we override the whole template instead of just adding an
            // ItemClass property: R5BuildingItem DAs surface as RawExport
            // in UAssetAPI (CollisionApproximation has a custom C++
            // serializer the unversioned-property walker can't pass), so we
            // can't add a missing ItemClass property cleanly. Instead we
            // clone a Vanilla DA that ALREADY has ItemClass set, and via
            // the existing NameMap-rewrite path redirect the ItemClass
            // FName entries from the vanilla BP to our cloned BP.
            public string SourceVanillaDaStem;
            public string SourceVanillaDaPath;
            public string SourceVanillaNameKey;
            public string SourceVanillaDescriptionKey;
            public string SourceVanillaMeshStem;
            public string SourceVanillaMeshPath;

            // Some vanilla donor BPs reference MORE than one StaticMesh
            // (e.g. PendulumClockT04_01 ships a main body
            // SM_PendulumClockT04_01 PLUS a secondary piece
            // SM_PendulumClockT04_01_p01 - the latter is a transparent
            // glass-front cover rendered through a BP-added R5FoliageMesh-
            // Component on top of the native StaticMesh component). The
            // primary mesh (SourceVanillaMeshStem/Path above) is rewritten
            // to the user mesh; every entry in this list ALSO gets rewritten
            // to the same user mesh so all secondary components render the
            // user mesh on top of each other (identity transform on both -
            // verified via the AudioRecon dump). Net visual effect: one
            // mesh, no foreign vanilla geometry leaking through.
            //
            // If the secondary components had non-identity transforms,
            // we'd see N copies at different positions instead - in that
            // case the right fix would be an SCS-Node visibility patch.
            // For the current Audio donor (PendulumClock) all secondaries
            // are at (0,0,0), so this simple NameMap redirect is enough.
            public sealed class VanillaMeshRef
            {
                public string Stem;
                public string Path;
            }
            public IReadOnlyList<VanillaMeshRef> AdditionalSourceVanillaMeshes;

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
            public string SourceVanillaItemClassStem;
            public string SourceVanillaItemClassPath;

            // Produces an effective BuildingTemplate that overrides the
            // user's chosen template with this preset's vanilla source refs.
            // The patcher then clones the source DA + rewrites
            // mesh/icon/recipe/itemclass FNames in one pass.
            public BuildingTemplate ApplyTo(BuildingTemplate baseTemplate)
            {
                if (baseTemplate == null) throw new ArgumentNullException("baseTemplate");
                return new BuildingTemplate
                {
                    Id          = baseTemplate.Id + "+" + Kind.ToString().ToLowerInvariant() + ":" + Id,
                    DisplayName = baseTemplate.DisplayName,
                    Description = baseTemplate.Description + " (" + Kind.ToString().ToLowerInvariant() + ": " + DisplayName + ")",

                    CategoryTag = baseTemplate.CategoryTag,

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
        //   1. Pick a Vanilla donor BP that has the SCS-Components you want
        //      (Niagara/Light for Flame; AudioComponent for Audio).
        //   2. Run `retoc to-legacy --filter <BpStem>` once in a scratch dir
        //      to verify the BP extracts cleanly + dump its NameMap.
        //   3. Find the donor DA (DA_BI_*) - run retoc to-legacy + names-dump
        //      to harvest the source mesh/icon/recipe/FText keys.
        //   4. Add an entry below with a unique Id + Kind + NamePrefix.
        //   5. Test ingame.
        public static readonly IReadOnlyList<ComponentPreset> Presets = new[]
        {
            new ComponentPreset
            {
                Id            = "torch",
                DisplayName   = "Torch",
                Description   = "Flickering torch flame with warm point light and ambient loop SFX. Cloned from vanilla FloorTorch.",
                Kind          = ComponentPresetKind.Flame,
                // "Flaming" preserves the existing BP_QmFlaming_<id>
                // wire-format used by profiles that opted into the torch
                // preset before the catalog rename.
                NamePrefix    = "Flaming",
                VanillaBpStem = "BP_BuildingBlock_FloorTorch",
                VanillaBpPath = "/Game/Gameplay/Building/Actors/Furniture/BP_BuildingBlock_FloorTorch",

                SourceVanillaDaStem      = "DA_BI_FloorTorch",
                SourceVanillaDaPath      = "/Game/Gameplay/Building/BuildingDecoration/DA_BI_FloorTorch",
                SourceVanillaNameKey         = "Decorations_FloorTorch_Name",
                SourceVanillaDescriptionKey  = "Decorations_NoComfortFloorTorch_Description",

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

            // Audio preset. Donor: BP_BuildingBlock_PendulumClockT04_01 (a
            // vanilla decoration BP whose only SCS-Component beyond the
            // mesh + scene root is an AudioComponent looping the
            // MS_Building_Clock_LP MetaSoundSource (a ticking pendulum-
            // clock loop). For Phase A we keep the vanilla Tick-Tack sound
            // - users get a "your custom building + ambient clock-tick" loop
            // without uploading anything. Phase B will add per-building
            // user-WAV upload + range slider (clones MetaSoundSource ->
            // SoundCue with attached attenuation).
            //
            // Why this donor:
            //   - AudioComponent has only 2 properties (Sound +
            //     RelativeLocation), no Niagara complexity that crashed
            //     J-v5's multi-flame attempt.
            //   - No NiagaraComponent, no PointLight - the preset is purely
            //     audio (zero visual pollution on the user's mesh).
            //   - Same R5BuildingBlock parent class as Torch + Books_01,
            //     drop-in compatible with the existing ItemClass-rewrite
            //     pipeline.
            new ComponentPreset
            {
                Id            = "audio",
                DisplayName   = "Audio",
                Description   = "Looping ambient audio on the building (Phase A: vanilla clock tick-tack). Cloned from vanilla PendulumClock.",
                Kind          = ComponentPresetKind.Audio,
                NamePrefix    = "Audio",
                VanillaBpStem = "BP_BuildingBlock_PendulumClockT04_01",
                VanillaBpPath = "/Game/Gameplay/Building/Actors/BP_BuildingBlock_PendulumClockT04_01",

                // Verified via retoc to-legacy + names-dump:
                //   FText keys are inline FText strings in the DA body:
                //     Decorations_PendulumClockT04_01_Name (36 chars)
                //     Decoration_Misc_T04_Description      (31 chars)
                //   Both long enough for the QmBldg_<8hex>_<suffix> rewrite.
                SourceVanillaDaStem          = "DA_BI_PendulumClockT04_01",
                SourceVanillaDaPath          = "/Game/Gameplay/Building/BuildingDecoration/DA_BI_PendulumClockT04_01",
                SourceVanillaNameKey         = "Decorations_PendulumClockT04_01_Name",
                SourceVanillaDescriptionKey  = "Decoration_Misc_T04_Description",

                SourceVanillaMeshStem = "SM_PendulumClockT04_01",
                SourceVanillaMeshPath = "/Game/Environment/Gameplay/Building/Furniture/FurnitureSet_T04/SM_PendulumClockT04_01",

                // Secondary mesh ref: the BP has a second R5FoliageMesh-
                // Component (Export[6] in the AudioRecon dump, SCS-added
                // as 'SM_WindowframeT03_ABE_01_p01' - leftover variable
                // name from when the BP was forked from a windowframe)
                // pointing at SM_PendulumClockT04_01_p01, which renders
                // as a half-transparent rectangle (the clock's glass-
                // front cover) on top of the user mesh. Redirecting it
                // to the user mesh as well makes both components render
                // the same mesh at the same transform - visually one mesh.
                AdditionalSourceVanillaMeshes = new[]
                {
                    new ComponentPreset.VanillaMeshRef
                    {
                        Stem = "SM_PendulumClockT04_01_p01",
                        Path = "/Game/Environment/Gameplay/Building/Furniture/FurnitureSet_T04/SM_PendulumClockT04_01_p01",
                    },
                },

                SourceVanillaIconStem = "T_PendulumClockT04_01",
                SourceVanillaIconPath = "/Game/UI/HUD/Building/Icons/BuildingBits/T_PendulumClockT04_01",

                SourceVanillaRecipeJsonPath    = "R5/Plugins/R5BusinessRules/Content/Recipes/Building/Items/Decorations/DA_RD_BuildObject_Deco_Misc_T04_ClockAndGlobe.json",
                SourceVanillaRecipeStem        = "DA_RD_BuildObject_Deco_Misc_T04_ClockAndGlobe",
                SourceVanillaRecipePackagePath = "/R5BusinessRules/Recipes/Building/Items/Decorations/DA_RD_BuildObject_Deco_Misc_T04_ClockAndGlobe",

                SourceVanillaItemClassStem = "BP_BuildingBlock_PendulumClockT04_01",
                SourceVanillaItemClassPath = "/Game/Gameplay/Building/Actors/BP_BuildingBlock_PendulumClockT04_01",
            },
        };

        // Returns the preset for a given Id, or null if Id is null/empty/
        // unknown. Empty string + null both map to "no preset selected" and
        // return null without warning - that's the default state for any
        // building that hasn't explicitly opted in.
        public static ComponentPreset Resolve(string presetId)
        {
            if (string.IsNullOrWhiteSpace(presetId)) return null;
            var trimmed = presetId.Trim();
            return Presets.FirstOrDefault(p =>
                string.Equals(p.Id, trimmed, StringComparison.OrdinalIgnoreCase));
        }

        // Lightweight DTO for the GUI dropdown (no Vanilla-path fields to
        // avoid leaking internal asset paths into the frontend).
        public sealed class ComponentPresetDto
        {
            public string id;
            public string displayName;
            public string description;
            public string kind;  // "flame" / "audio"
        }

        public static IReadOnlyList<ComponentPresetDto> GetDtos()
        {
            return Presets.Select(p => new ComponentPresetDto
            {
                id          = p.Id,
                displayName = p.DisplayName,
                description = p.Description,
                kind        = p.Kind.ToString().ToLowerInvariant(),
            }).ToList();
        }
    }
}
