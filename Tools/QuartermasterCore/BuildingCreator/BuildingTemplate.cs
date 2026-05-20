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
        // item name. We rename this to a per-Building synthesized key
        // ("Decoration_<BuildingId>_Name") so each clone gets its own
        // CSV-Synthese-Pattern entry.
        //
        // Note: some Vanilla DAs (e.g. DA_BI_Bucket_01) store the name
        // key indirectly via an export body that's a RawExport - the
        // NameMap-rename then misses, the patcher warns but proceeds,
        // and the in-game display falls back to the default text. Polish
        // task for a future iteration.
        public string VanillaNameKey;

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

                VanillaDaStem   = "DA_BI_Paintings_HighLands_02",
                VanillaDaPath   = "/Game/Gameplay/Building/BuildingDecoration/DA_BI_Paintings_HighLands_02",
                VanillaNameKey  = "Decorations_Paintings_HighLands_02_Name",

                VanillaMeshStem = "SM_Paintings_HighLands_02",
                VanillaMeshPath = "/Game/Environment/Gameplay/Building/BuildingDecoration/SM_Paintings_HighLands_02",

                VanillaIconStem = "T_Paintings_HighLands_02",
                VanillaIconPath = "/Game/UI/HUD/Building/Icons/BuildingBits/T_Paintings_HighLands_02",

                CategoryTag = "BuildingDecoration",
            };
        }

        public static BuildingTemplate Bucket()
        {
            return new BuildingTemplate
            {
                Id          = "Bucket",
                DisplayName = "Bucket",
                Description = "Free-standing bucket cloned from Vanilla wooden bucket (floor placement).",

                VanillaDaStem   = "DA_BI_Bucket_01",
                VanillaDaPath   = "/Game/Gameplay/Building/BuildingDecoration/DA_BI_Bucket_01",
                // DA_BI_Bucket_01 stores its name string in a RawExport
                // body the legacy patcher can't rewrite via NameMap. The
                // value below is the convention we'd expect if it WAS
                // in the NameMap; the patcher logs a warning when it
                // can't find the string and the in-game name falls back
                // to the default. Acceptable for the G-test.
                VanillaNameKey  = "BuildingItems_Bucket_01_Name",

                VanillaMeshStem = "SM_BucketWooden_01",
                VanillaMeshPath = "/Game/Environment/Props/Camp/SM_BucketWooden_01",

                VanillaIconStem = "T_BI_Bucket_01",
                VanillaIconPath = "/Game/UI/HUD/Building/Icons/BuildingBits/T_BI_Bucket_01",

                CategoryTag = "BuildingDecoration",
            };
        }
    }
}
