using System;
using System.Collections.Generic;
using System.Linq;

namespace Windrose.Quartermaster.Core.BuildingCreator
{
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

            // Flame NamePrefix must stay "Flaming" to preserve the BP_QmFlaming_<id> wire-format in existing profiles.
            public string NamePrefix;

            public string VanillaBpStem;
            public string VanillaBpPath;

            // Cloned BP stem is per-building (not per-preset) so each building's baked-in vanilla mesh ref can be rewritten to its own user mesh.
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

            // When ComponentPresetId is set, these vanilla refs override the user's chosen template: the build clones a donor DA that already has ItemClass set rather than synthesizing one.
            public string SourceVanillaDaStem;
            public string SourceVanillaDaPath;
            public string SourceVanillaNameKey;
            public string SourceVanillaDescriptionKey;
            public string SourceVanillaMeshStem;
            public string SourceVanillaMeshPath;

            // Secondary meshes in the donor BP; each is redirected to the same user mesh as the primary. Only valid because the donor's secondary components sit at identity transform (else they'd render as offset duplicates).
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

            public string SourceVanillaItemClassStem;
            public string SourceVanillaItemClassPath;

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

        // Order = GUI dropdown order.
        public static readonly IReadOnlyList<ComponentPreset> Presets = new[]
        {
            new ComponentPreset
            {
                Id            = "torch",
                DisplayName   = "Torch",
                Description   = "Flickering torch flame with warm point light and ambient loop SFX. Cloned from vanilla FloorTorch.",
                Kind          = ComponentPresetKind.Flame,
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

            new ComponentPreset
            {
                Id            = "audio",
                DisplayName   = "Audio",
                Description   = "Looping ambient audio on the building. Upload your own WAV/MP3 and tune range + volume; falls back to a vanilla clock tick-tack if no file is uploaded.",
                Kind          = ComponentPresetKind.Audio,
                NamePrefix    = "Audio",
                VanillaBpStem = "BP_BuildingBlock_PendulumClockT04_01",
                VanillaBpPath = "/Game/Gameplay/Building/Actors/BP_BuildingBlock_PendulumClockT04_01",

                SourceVanillaDaStem          = "DA_BI_PendulumClockT04_01",
                SourceVanillaDaPath          = "/Game/Gameplay/Building/BuildingDecoration/DA_BI_PendulumClockT04_01",
                SourceVanillaNameKey         = "Decorations_PendulumClockT04_01_Name",
                SourceVanillaDescriptionKey  = "Decoration_Misc_T04_Description",

                SourceVanillaMeshStem = "SM_PendulumClockT04_01",
                SourceVanillaMeshPath = "/Game/Environment/Gameplay/Building/Furniture/FurnitureSet_T04/SM_PendulumClockT04_01",

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

        // null/empty/unknown all return null = "no preset selected" (the default), without warning.
        public static ComponentPreset Resolve(string presetId)
        {
            if (string.IsNullOrWhiteSpace(presetId)) return null;
            var trimmed = presetId.Trim();
            return Presets.FirstOrDefault(p =>
                string.Equals(p.Id, trimmed, StringComparison.OrdinalIgnoreCase));
        }

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
