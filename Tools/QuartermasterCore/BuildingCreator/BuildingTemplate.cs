namespace Windrose.Quartermaster.Core.BuildingCreator
{
    // Declarative description of a "buildable thing" (Etappe G mesh-driven).
    //
    // What a template defines (gameplay-only):
    //   - Vanilla DA donor (snap sockets, collision, category, placement)
    //   - Vanilla Mesh + Icon donor (so the patcher knows which NameMap
    //     strings in the DA to rewrite onto the user-cooked Mesh + Icon)
    //   - In-game CategoryTag (for the DLL's tab-purity filter)
    //
    // What a template does NOT define anymore (this is the G-wandel):
    //   - Material slot list - that comes from the user-cooked Mesh
    //   - Vanilla MI to clone - the user picks per slot from the
    //     VanillaMaterialCatalog dropdown
    //   - Texture / Scalar / Vector defaults - read from the user's
    //     MIs in the cooked folder, then user-editable in the GUI
    //
    // Adding a new template = pick a Vanilla DA you want to inherit the
    // gameplay properties from (snap rules, hit-box, build menu tab),
    // record its stem+path here, and the patcher pipeline does the rest.
    public sealed class BuildingTemplate
    {
        // Stable identifier referenced from BuildingDto.TemplateId.
        // Keep unique across all factory methods.
        public string Id;

        // Display label shown in the GUI's Template picker.
        public string DisplayName;

        // Short description shown next to the picker (one line).
        public string Description;

        // --- Vanilla DataAsset (BuildingItem definition) -----------------
        //
        // The patcher clones this DA, rewrites its NameMap so the
        // mesh/icon/self refs point at our mod paths, and ships the
        // result as DA_BI_<Building.Id> under /Game/Quartermaster/Items/.
        public string VanillaDaStem;
        public string VanillaDaPath;

        // Localization key the Vanilla DA carries for the user-facing
        // item name. The DA stores this as an FText StringTableEntry
        // pointing at the BuildingItems CSV string-table; the literal
        // key string sits inline in the DA's export body (RawExport.Data),
        // NOT in the NameMap, so the DataAssetPatcher's NameMap rewrite
        // can't reach it. Instead, BuildingPatcher does a binary in-place
        // rewrite of these bytes after the NameMap-level patch, swapping
        // the vanilla key for a per-Building synthesized key while keeping
        // the byte length identical (padded with underscores). The
        // BuildingItemsCsvPatcher then appends a matching row to the
        // extended BuildingItems.csv so the engine resolves the new key
        // at runtime to the user-supplied display name.
        public string VanillaNameKey;

        // Sister field to VanillaNameKey for the tooltip / description
        // FText slot. Optional: null = skip description rewrite (the
        // building keeps whatever description the cloned vanilla DA
        // came with; if the vanilla DA doesn't carry one this is fine).
        // For Painting it is "Decoration_Paintings_T02_Description"
        // (note the inconsistent vanilla naming: Name uses
        // "Decorations_Paintings_HighLands_02_*" but Description uses
        // "Decoration_Paintings_T02_*"). Cross-checked by dumping the
        // DA's uexp bytes for the literal strings.
        public string VanillaDescriptionKey;

        // --- Vanilla Mesh donor (referenced by the DA) -------------------
        //
        // The user-cooked Mesh replaces this in the clone's NameMap.
        // We don't ship the Vanilla mesh; we just need its strings to
        // know what to swap.
        public string VanillaMeshStem;
        public string VanillaMeshPath;

        // --- Vanilla Icon donor ------------------------------------------
        //
        // Same idea as the mesh: only the strings, the actual icon
        // texture comes from the user's cook.
        public string VanillaIconStem;
        public string VanillaIconPath;

        // --- Game category for the inject-side tab-purity filter ---------
        //
        // The DLL's qm_config.cpp uses this to recognise which build tab
        // the injected widget belongs to. Values seen so far:
        //   "BuildingDecoration" - paintings, buckets, lamps, etc.
        // Future templates may need extending the DLL's filter to
        // accept per-item tags (PENDING risk in G.2 plan).
        public string CategoryTag;

        // --- Vanilla recipe (build-cost source, Etappe H2) ---------------
        //
        // Each building references an external recipe DA which carries the
        // RecipeCost array (resource items + counts), RecipeTag, and
        // CraftRequirement. Discovered via the Vanilla DA's NameMap: e.g.
        // DA_BI_Paintings_HighLands_02 has both the full package path
        // "/R5BusinessRules/Recipes/Building/Items/Decorations/
        //  DA_RD_BuildObject_Deco_Paintings_T02" and the bare stem
        // "DA_RD_BuildObject_Deco_Paintings_T02" in its NameMap. Both
        // entries get rewritten via DataAssetPatcher to point at our
        // cloned recipe under
        // /R5BusinessRules/Recipes/Building/Items/Decorations/
        //   DA_RD_Qm<BuildingId>.
        //
        // Unlike all other vanilla assets here, recipes ship as PLAIN JSON
        // in the legacy pakchunk0-Windows.pak (not as uasset/uexp) - so the
        // RecipePatcher just parses + transforms + writes JSON, no UAssetAPI
        // involvement. VanillaRecipeJsonPath is the absolute on-disk path
        // to the extracted source JSON under Sources/Vanilla.
        public string VanillaRecipeJsonPath;

        // Bare file stem ("DA_RD_BuildObject_Deco_Paintings_T02"). Goes
        // into the DataAssetPatcher's NameMap-replacement table.
        public string VanillaRecipeStem;

        // Full UE virtual path matching the second NameMap entry for the
        // recipe in the building's DA ("/R5BusinessRules/Recipes/.../
        // DA_RD_BuildObject_Deco_Paintings_T02", WITHOUT the .Stem suffix
        // - that style of path is unique to RecipeCost item refs).
        public string VanillaRecipePackagePath;

        // -----------------------------------------------------------------
        // Convenience factories.
        // -----------------------------------------------------------------
        public static BuildingTemplate Painting()
        {
            return new BuildingTemplate
            {
                Id          = "Painting",
                DisplayName = "Painting",
                Description = "Wall painting cloned from Vanilla HighLands painting (image on wall).",

                VanillaDaStem          = "DA_BI_Paintings_HighLands_02",
                VanillaDaPath          = "/Game/Gameplay/Building/BuildingDecoration/DA_BI_Paintings_HighLands_02",
                VanillaNameKey         = "Decorations_Paintings_HighLands_02_Name",
                VanillaDescriptionKey  = "Decoration_Paintings_T02_Description",

                VanillaMeshStem = "SM_Paintings_HighLands_02",
                VanillaMeshPath = "/Game/Environment/Gameplay/Building/BuildingDecoration/SM_Paintings_HighLands_02",

                VanillaIconStem = "T_Paintings_HighLands_02",
                VanillaIconPath = "/Game/UI/HUD/Building/Icons/BuildingBits/T_Paintings_HighLands_02",

                CategoryTag = "BuildingDecoration",

                // Recipe wiring (Etappe H2). Note the Painting DA's
                // NameMap references the recipe by both full path and
                // bare stem - DataAssetPatcher will rewrite both.
                VanillaRecipeJsonPath    = "R5/Plugins/R5BusinessRules/Content/Recipes/Building/Items/Decorations/DA_RD_BuildObject_Deco_Paintings_T02.json",
                VanillaRecipeStem        = "DA_RD_BuildObject_Deco_Paintings_T02",
                VanillaRecipePackagePath = "/R5BusinessRules/Recipes/Building/Items/Decorations/DA_RD_BuildObject_Deco_Paintings_T02",
            };
        }

        public static BuildingTemplate Bucket()
        {
            return new BuildingTemplate
            {
                Id          = "Bucket",
                DisplayName = "Bucket",
                Description = "Free-standing bucket cloned from Vanilla wooden bucket (floor placement).",

                VanillaDaStem          = "DA_BI_Bucket_01",
                VanillaDaPath          = "/Game/Gameplay/Building/BuildingDecoration/DA_BI_Bucket_01",
                // Discovered by binary-dumping DA_BI_Bucket_01.uexp; both
                // keys sit inline in the export body (BuildingPatcher's
                // post-NameMap binary rewrite handles them).
                VanillaNameKey         = "Decorations_Bucket_01_Name",
                VanillaDescriptionKey  = "Decorations_DecorDishes_01_Descriptions",

                VanillaMeshStem = "SM_BucketWooden_01",
                VanillaMeshPath = "/Game/Environment/Props/Camp/SM_BucketWooden_01",

                VanillaIconStem = "T_BI_Bucket_01",
                VanillaIconPath = "/Game/UI/HUD/Building/Icons/BuildingBits/T_BI_Bucket_01",

                CategoryTag = "BuildingDecoration",

                // Recipe wiring (Etappe H2).
                VanillaRecipeJsonPath    = "R5/Plugins/R5BusinessRules/Content/Recipes/Building/Items/Decorations/DA_RD_BuildObject_Deco_Dishes_T01_Wood.json",
                VanillaRecipeStem        = "DA_RD_BuildObject_Deco_Dishes_T01_Wood",
                VanillaRecipePackagePath = "/R5BusinessRules/Recipes/Building/Items/Decorations/DA_RD_BuildObject_Deco_Dishes_T01_Wood",
            };
        }

        // Etappe I.2: build a BuildingTemplate dynamically from a
        // VanillaBuildingTemplateInspection. Used by the BuildPipeline
        // when the profile's TemplateId references a Vanilla DA path
        // (rather than one of the legacy "Painting"/"Bucket" sentinels).
        //
        // The factory deliberately accepts partial inspections - missing
        // recipe refs are fine (some Vanilla DAs ship without a recipe;
        // the patcher's recipe step is a no-op when VanillaRecipeStem is
        // null). Missing Mesh/Icon refs would be fatal at build time, so
        // the inspector surfaces those as warnings the GUI can show.
        public static BuildingTemplate FromInspection(VanillaBuildingTemplateInspection ins)
        {
            if (ins == null) throw new System.ArgumentNullException("ins");

            return new BuildingTemplate
            {
                Id          = ins.Id,
                DisplayName = ins.DisplayName,
                Description = "Cloned from Vanilla " + ins.DisplayName + " (" + (ins.Category ?? "?") + ").",

                VanillaDaStem          = ins.DisplayName,
                VanillaDaPath          = ins.PackagePath,
                VanillaNameKey         = ins.NameKey,
                VanillaDescriptionKey  = ins.DescriptionKey,

                VanillaMeshStem = ins.MeshStem,
                VanillaMeshPath = ins.MeshPath,

                VanillaIconStem = ins.IconStem,
                VanillaIconPath = ins.IconPath,

                // Tab routing is pinned to "Vorgefertigte Strukturen"
                // (Etappe H1) regardless of the DA's source folder. The
                // CategoryTag field is still surfaced here for diagnostics
                // - GameDeployer reads `tabPurityFilter` from its own
                // constant, not from this string.
                CategoryTag = ins.Category ?? "BuildingDecoration",

                VanillaRecipeJsonPath    = ins.RecipeJsonPath,
                VanillaRecipeStem        = ins.RecipeStem,
                VanillaRecipePackagePath = ins.RecipePath,
            };
        }
    }
}
