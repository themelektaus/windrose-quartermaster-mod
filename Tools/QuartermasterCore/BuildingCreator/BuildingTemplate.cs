namespace Windrose.Quartermaster.Core.BuildingCreator
{
    public sealed class BuildingTemplate
    {
        public string Id;

        public string DisplayName;

        public string Description;

        public string VanillaDaStem;
        public string VanillaDaPath;

        // FText key the vanilla DA carries for the item name. It sits inline in the DA's export body (not the NameMap), so FTextKeyRewriter binary-patches it on disk rather than DataAssetPatcher.
        public string VanillaNameKey;

        // Tooltip/description FText slot. Optional: null = skip description rewrite. Discovered separately at inspection time because vanilla naming is inconsistent (Name vs Description suffix mismatches).
        public string VanillaDescriptionKey;

        public string VanillaMeshStem;
        public string VanillaMeshPath;

        public string VanillaIconStem;
        public string VanillaIconPath;

        public string CategoryTag;

        // Recipes ship as PLAIN JSON in the legacy pak (not uasset/uexp), so RecipePatcher transforms JSON directly. This is the absolute on-disk path to the extracted source JSON.
        public string VanillaRecipeJsonPath;

        public string VanillaRecipeStem;

        // Full UE virtual path matching the recipe's second NameMap entry in the building's DA (WITHOUT the .Stem suffix).
        public string VanillaRecipePackagePath;

        // Accepts partial inspections: missing recipe refs are fine (no-op recipe step); missing Mesh/Icon refs are surfaced by the inspector as warnings.
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

                // Surfaced for diagnostics only; GameDeployer reads the tab filter from its own constant, not this string.
                CategoryTag = ins.Category ?? "BuildingDecoration",

                VanillaRecipeJsonPath    = ins.RecipeJsonPath,
                VanillaRecipeStem        = ins.RecipeStem,
                VanillaRecipePackagePath = ins.RecipePath,
            };
        }
    }
}
