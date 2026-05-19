using System;
using System.Collections.Generic;

namespace Windrose.Quartermaster.Core.BuildingCreator
{
    // Declarative description of a "buildable thing" we know how to ship as
    // a mod-pak'd custom item. The template names the Vanilla donor assets
    // (DA, Mesh, Icon, MI for each material slot) plus the Vanilla texture
    // refs each slot's MI carries, plus the in-game category tag we need
    // for tab-purity filtering at inject-time.
    //
    // The patcher pipeline (BuildingPatcher) treats Vanilla as the
    // structural ground-truth: every actual asset that ends up in the mod
    // pak is either user-cooked (the mesh + icon + user textures) or a
    // post-cook clone of a Vanilla asset (the DA + per-slot MI). We never
    // ship a user-cooked Material - that's the empirical Crash-Pattern we
    // bisected to in the painting spike.
    //
    // Per-template constraints we encoded after the bisect:
    //   * The Vanilla MI named in MaterialSlotTemplate.VanillaMaterialStem
    //     must compile against ShaderLibrary chunks already present in the
    //     Vanilla pakchunk0_s* set - otherwise our clone references shader
    //     hashes that the shipping ShaderLibrary doesn't have and the
    //     material renders black or crashes the loader.
    //   * The Vanilla textures named in VanillaAlbedoStem / VanillaNormalStem
    //     / VanillaMtrmStem must be Virtual-Textures (VT) - the parent
    //     material M_Object expects VT samplers and a non-VT texture sample
    //     yields black at runtime ("Material expects texture to be Virtual"
    //     warning).
    public sealed class BuildingTemplate
    {
        // Stable identifier referenced from BuildingDto.TemplateId. Currently
        // we only ship "Painting" - additional templates should pick unique
        // values (e.g. "Furniture", "Light", ...).
        public string Id;

        // Display label shown in the GUI's Template picker.
        public string DisplayName;

        // Short description shown next to the picker (one line).
        public string Description;

        // --- Vanilla DataAsset (BuildingItem definition) -----------------
        //
        // Vanilla DA stem (no slashes, no extension), e.g.:
        //   "DA_BI_Paintings_HighLands_02"
        public string VanillaDaStem;

        // Full Vanilla DA package path (with leading slash, no extension):
        //   "/Game/Gameplay/Building/BuildingDecoration/DA_BI_Paintings_HighLands_02"
        public string VanillaDaPath;

        // Localization key the Vanilla DA's CSV-Synthese-Pattern uses for
        // the user-facing item name, e.g. "Decorations_Paintings_HighLands_02_Name".
        // We rename this in the cloned DA's NameMap to point at a per-Building
        // synthesized CSV key (e.g. "Decoration_<BuildingId>_Name").
        public string VanillaNameKey;

        // --- Vanilla Mesh (referenced by the DA) -------------------------
        //
        // Vanilla StaticMesh stem and full path. The patcher rewrites the
        // DA's NameMap so the mesh-ref points at the user-cooked mesh
        // instead. We don't actually pull the Vanilla mesh out via retoc -
        // we just need its stem+path to know which NameMap strings to swap.
        public string VanillaMeshStem;
        public string VanillaMeshPath;

        // --- Vanilla Icon ------------------------------------------------
        //
        // Same idea as the mesh - we only need the names to drive the DA's
        // NameMap rename. The actual icon comes from the user's cook.
        public string VanillaIconStem;
        public string VanillaIconPath;

        // --- Game category for the inject-side tab-purity filter ---------
        //
        // The DLL's qm_config.cpp uses this string to recognise which build
        // tab the injected widget belongs to. For "Painting" this matches
        // "BuildingDecoration" (same tab as the Vanilla paintings sit in).
        public string CategoryTag;

        // --- Per-material-slot definitions -------------------------------
        //
        // Order matters: SlotIndex 0 maps to the first material slot on the
        // Vanilla mesh, SlotIndex 1 to the second, etc. For Painting we
        // have two slots: Frame (wooden frame) and Canvas (image area).
        public List<MaterialSlotTemplate> Slots;

        // Convenience factory for the only template we ship today.
        // Mirrors the validated spike pipeline 1:1 (every Vanilla path here
        // was extracted from the actual painting spike and proved to work
        // in-game after the VT/Default-Texture fix).
        public static BuildingTemplate Painting()
        {
            return new BuildingTemplate
            {
                Id          = "Painting",
                DisplayName = "Painting",
                Description = "Custom wall painting cloned from Vanilla HighLands painting (frame + canvas with your image).",

                VanillaDaStem  = "DA_BI_Paintings_HighLands_02",
                VanillaDaPath  = "/Game/Gameplay/Building/BuildingDecoration/DA_BI_Paintings_HighLands_02",
                VanillaNameKey = "Decorations_Paintings_HighLands_02_Name",

                VanillaMeshStem = "SM_Paintings_HighLands_02",
                VanillaMeshPath = "/Game/Environment/Gameplay/Building/BuildingDecoration/SM_Paintings_HighLands_02",

                VanillaIconStem = "T_Paintings_HighLands_02",
                VanillaIconPath = "/Game/UI/HUD/Building/Icons/BuildingBits/T_Paintings_HighLands_02",

                CategoryTag = "BuildingDecoration",

                Slots = new List<MaterialSlotTemplate>
                {
                    // Slot 0: Frame (Holzrahmen). Albedo on shared default
                    // white VT so the Vanilla EdgeColor tint (sandbraun)
                    // shines through unmodified.
                    new MaterialSlotTemplate
                    {
                        SlotName              = "Frame",
                        VanillaMaterialStem   = "MI_Paintings_01",
                        VanillaMaterialPath   = "/Game/Environment/Gameplay/Building/BuildingDecoration/Materials/MI_Paintings_01",

                        VanillaAlbedoStem = "T_Paintings_01_A",
                        VanillaAlbedoPath = "/Game/Environment/Gameplay/Building/BuildingDecoration/Textures/T_Paintings_01_A",
                        VanillaNormalStem = "T_Paintings_01_N",
                        VanillaNormalPath = "/Game/Environment/Gameplay/Building/BuildingDecoration/Textures/T_Paintings_01_N",
                        VanillaMtrmStem   = "T_Paintings_01_MTRM",
                        VanillaMtrmPath   = "/Game/Environment/Gameplay/Building/BuildingDecoration/Textures/T_Paintings_01_MTRM",

                        // No vector overrides - Vanilla EdgeColor/AOColor
                        // defaults (sandy/brown) give the warm-frame look.
                        VectorOverrides = new List<VectorParamOverride>(),
                    },

                    // Slot 1: Canvas (Bildflaeche). Albedo gets the user's
                    // texture, Normal+MTRM on shared defaults. EdgeColor +
                    // AOColor forced white so the image renders untinted.
                    new MaterialSlotTemplate
                    {
                        SlotName              = "Canvas",
                        VanillaMaterialStem   = "MI_Paintings_01",
                        VanillaMaterialPath   = "/Game/Environment/Gameplay/Building/BuildingDecoration/Materials/MI_Paintings_01",

                        VanillaAlbedoStem = "T_Paintings_01_A",
                        VanillaAlbedoPath = "/Game/Environment/Gameplay/Building/BuildingDecoration/Textures/T_Paintings_01_A",
                        VanillaNormalStem = "T_Paintings_01_N",
                        VanillaNormalPath = "/Game/Environment/Gameplay/Building/BuildingDecoration/Textures/T_Paintings_01_N",
                        VanillaMtrmStem   = "T_Paintings_01_MTRM",
                        VanillaMtrmPath   = "/Game/Environment/Gameplay/Building/BuildingDecoration/Textures/T_Paintings_01_MTRM",

                        // Canvas wants its image to show through without
                        // any Vanilla tint - force both vector params white.
                        VectorOverrides = new List<VectorParamOverride>
                        {
                            new VectorParamOverride { Name = "Edge Color", R = 1f, G = 1f, B = 1f, A = 1f },
                            new VectorParamOverride { Name = "AO Color",   R = 1f, G = 1f, B = 1f, A = 1f },
                        },

                        // Canvas takes a user-supplied image. The patcher
                        // uses this flag to know whether to fail/warn if
                        // BuildingInputs doesn't carry a custom albedo.
                        UserAlbedoRequired = true,
                    },
                },
            };
        }
    }

    // Description of a single material slot on the Vanilla mesh. The
    // patcher generates one MI clone per slot, then rewrites the user-cooked
    // mesh's NameMap so its slot-N material-ref points at the clone.
    public sealed class MaterialSlotTemplate
    {
        // Human-readable slot label (used in clone stem and in logs).
        // Examples: "Canvas", "Frame".
        public string SlotName;

        // Vanilla MaterialInstance to clone for this slot. The .uexp of
        // this MI carries the compiled ShaderMap that the shipping
        // ShaderLibrary knows about - that's why we clone instead of
        // shipping a user-cooked material.
        public string VanillaMaterialStem;
        public string VanillaMaterialPath;

        // Vanilla texture refs the cloned MI starts out pointing at. The
        // patcher rewrites these to either the user-supplied custom texture
        // (BuildingSlotInputs.CustomAlbedoStem etc.) or the shared default
        // VT textures shipped by the GUI (T_QmPainting_White / NormalFlat /
        // MTRMDefault). Per Punkt 9 from the planning session.
        public string VanillaAlbedoStem;
        public string VanillaAlbedoPath;
        public string VanillaNormalStem;
        public string VanillaNormalPath;
        public string VanillaMtrmStem;
        public string VanillaMtrmPath;

        // Vector-parameter overrides applied to the MI clone via UAssetAPI
        // after the NameMap rewrite. The Vanilla MI ships with sandy/brown
        // defaults for EdgeColor + AOColor; templates that want a neutral
        // (untinted) look list those params here with (1,1,1,1).
        public List<VectorParamOverride> VectorOverrides;

        // If true, the patcher fails if BuildingInputs doesn't supply a
        // CustomAlbedoStem for this slot (the slot is "image-bearing" and
        // makes no sense without user content).
        public bool UserAlbedoRequired;
    }

    // A single vector-parameter override applied to the cloned MI's
    // VectorParameterValues array.
    public sealed class VectorParamOverride
    {
        public string Name;
        public float R, G, B, A;
    }
}
