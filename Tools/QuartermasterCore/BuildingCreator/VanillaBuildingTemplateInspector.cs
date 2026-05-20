using System;
using System.IO;
using System.Linq;
using CUE4Parse.UE4.Assets.Exports;
using CUE4Parse.UE4.Assets.Objects;
using CUE4Parse.UE4.Objects.Core.i18N;
using CUE4Parse.UE4.Objects.UObject;

namespace Windrose.Quartermaster.Core.BuildingCreator
{
    // Reads a Vanilla R5BuildingItem DataAsset (DA_BI_*.uasset) and pulls
    // out the metadata needed to construct a runtime BuildingTemplate
    // (Etappe I.2):
    //
    //   - Mesh asset ref     (StaticMesh path/stem - SM_*)
    //   - Icon asset ref     (Texture2D path/stem - T_*)
    //   - Recipe asset ref   (build cost recipe DA - DA_RD_*)
    //   - Name FText key     (BuildingItems.csv StringTable lookup)
    //   - Description FText  (same)
    //
    // Uses the catalog's mounted CUE4Parse provider (shared instance) -
    // we don't pay the mount cost per inspect. Each LoadPackage call
    // reads the DA's Zen-format bytes from the pak straight into memory.
    //
    // The vanilla DAs we've spot-checked store their refs as
    // FSoftObjectPath (UObjectProperty wrappers around an AssetPathName
    // FName + SubPathString). CUE4Parse exposes both as
    // FPackageIndex via GetOrDefault<FPackageIndex>(...) OR as
    // FSoftObjectPath via GetOrDefault<FSoftObjectPath>(...). We use
    // FSoftObjectPath for the asset refs (gives us the "/Game/..." style
    // string directly) and the Text history pattern for the FText keys
    // (StringTableEntry.Key is the inline-stored key the BuildingPatcher
    // needs for the FTextKeyRewriter).
    //
    // Property names probed (matches what BonfireRadiusPatcher's offset
    // table and our recon dump showed - the field names below are the
    // unversioned-property mapping names that the usmap tells UAssetAPI
    // for R5BuildingItem):
    //
    //   "PreviewMeshes"   FSoftObjectPath[] -> SM_<stem> (first element)
    //   "Icon"            FSoftObjectPath  -> T_<stem>
    //   "BuildingCost"    FSoftObjectPath  -> DA_RD_<stem>  (single ref,
    //                                                       NOT "Recipe")
    //   "Name"            FText            -> StringTableEntry.Key
    //   "Description"     FText            -> StringTableEntry.Key
    //
    // Property name discovery: dumped a sample of vanilla DAs via the
    // first-pass inspector with a diag log and found the names match the
    // SDK dump (Tools/Dumper7Setup/output/CppSDK/SDK/R5*structs.hpp) for
    // R5BuildingItem - which calls these "PreviewMeshes" and
    // "BuildingCost". Earlier guess of "Mesh"/"Recipe" was wrong.
    //
    // Spelling note: some vanilla DAs use "Descriptions" (plural). The
    // inspector probes both for robustness.
    public sealed class VanillaBuildingTemplateInspector
    {
        public VanillaBuildingTemplateCatalog Catalog;
        public Action<string> Log;

        // Inspect a single DA by its catalog id (== /Game/... path). The
        // catalog must contain the entry; the inspector won't go looking
        // for unindexed paths.
        public VanillaBuildingTemplateInspection Inspect(string id)
        {
            if (Catalog == null) throw new InvalidOperationException("VanillaBuildingTemplateInspector.Catalog not set");
            if (string.IsNullOrWhiteSpace(id)) throw new ArgumentNullException("id");

            var entry = Catalog.GetById(id);
            if (entry == null)
                throw new InvalidOperationException("Unknown template id (not in catalog): " + id);

            var result = new VanillaBuildingTemplateInspection
            {
                Id              = entry.Id,
                DisplayName     = entry.DisplayName,
                Category        = entry.Category,
                PackagePath     = entry.PackagePath,
                PakRelativePath = entry.PakRelativePath,
                Warnings        = new System.Collections.Generic.List<string>(),
            };

            var provider = Catalog.Provider;
            if (provider == null)
                throw new InvalidOperationException("Catalog provider not initialized");

            LogLine("[building-inspect] loading package: " + entry.PackagePath);

            var pkg = provider.LoadPackage(entry.PackagePath);
            if (pkg == null)
            {
                result.Error = "Failed to load package: " + entry.PackagePath;
                return result;
            }

            // Find the main R5BuildingItem-class export. Vanilla DAs ship
            // a single relevant export (the data asset itself); look for
            // it by class name first and fall back to "the first
            // non-import export" if the class name isn't what we expect.
            UObject mainExport = null;
            string mainClass = null;
            foreach (var ex in pkg.GetExports())
            {
                if (ex == null) continue;
                // ExportType is already a string (UObject.cs:117 resolves
                // Class?.Name.Text ?? GetType().Name).
                var cls = ex.ExportType ?? "";
                if (string.Equals(cls, "R5BuildingItem", StringComparison.OrdinalIgnoreCase))
                {
                    mainExport = ex;
                    mainClass = cls;
                    break;
                }
                // Keep a fallback candidate in case the class lookup fails
                if (mainExport == null && !string.IsNullOrEmpty(cls))
                {
                    mainExport = ex;
                    mainClass = cls;
                }
            }
            if (mainExport == null)
            {
                result.Error = "No export found in " + entry.PackagePath;
                return result;
            }
            result.AssetClass = mainClass;

            if (!string.Equals(mainClass, "R5BuildingItem", StringComparison.OrdinalIgnoreCase))
            {
                // Soft warning - we keep going so the GUI can show the
                // refs anyway, but BuildPipeline will refuse to clone a
                // non-R5BuildingItem template.
                result.Warnings.Add("Asset class is '" + mainClass + "', not R5BuildingItem - this DA may not be cloneable as a building template.");
            }

            // Mesh / Icon / Recipe asset refs. Vanilla R5BuildingItem uses:
            //   PreviewMeshes  - ArrayProperty<SoftObjectProperty>; we take
            //                    the first element as the canonical mesh
            //   Icon           - SoftObjectProperty
            //   BuildingCost   - SoftObjectProperty -> Recipe DA
            // The earlier guess of "Mesh"/"Recipe" property names was
            // wrong and falls back through ReadSoftObject as a no-op.
            ReadSoftObjectArray(mainExport, "PreviewMeshes", out result.MeshStem, out result.MeshPath);
            ReadSoftObject(mainExport, "Icon",         out result.IconStem,   out result.IconPath);
            ReadSoftObject(mainExport, "BuildingCost", out result.RecipeStem, out result.RecipePath);

            // FText keys. Vanilla DAs ship Name+Description as
            // StringTableEntry-style FText (key into BuildingItems.csv).
            // Some old DAs use "Descriptions" (plural) - probe both.
            result.NameKey = ReadFTextKey(mainExport, "Name");
            result.DescriptionKey = ReadFTextKey(mainExport, "Description");
            if (string.IsNullOrEmpty(result.DescriptionKey))
                result.DescriptionKey = ReadFTextKey(mainExport, "Descriptions");

            // Recipe stem is also encoded as the JSON file stem under
            // R5/Plugins/R5BusinessRules/Content/Recipes/... - derive the
            // JSON path from RecipePath (the package path). The RecipePatcher
            // reads JSON in plain mode (Etappe H2) so we don't need uasset.
            if (!string.IsNullOrEmpty(result.RecipePath))
                result.RecipeJsonPath = DeriveRecipeJsonPath(result.RecipePath);

            return result;
        }

        // Read the first FSoftObjectPath from an ArrayProperty. Used for
        // PreviewMeshes (the vanilla "main mesh" lives at index 0). Bails
        // cleanly if the array is empty or the property doesn't exist.
        static void ReadSoftObjectArray(UObject ex, string propertyName, out string stem, out string path)
        {
            stem = null;
            path = null;

            var arr = ex.GetOrDefault<FSoftObjectPath[]>(propertyName);
            if (arr == null || arr.Length == 0) return;
            var first = arr[0];
            var firstText = first.AssetPathName.Text;
            if (string.IsNullOrEmpty(firstText) || string.Equals(firstText, "None", StringComparison.OrdinalIgnoreCase))
                return;
            path = SoftToVirtualPath(firstText);
            stem = ExtractStem(path);
        }

        // Read an FSoftObjectPath property. Falls through to FPackageIndex
        // if the property serialized as an ObjectProperty (some Vanilla
        // DAs do this for "Icon" depending on cook flags).
        //
        // FSoftObjectPath is a readonly struct (not nullable), so the
        // "no value present" signal is an empty AssetPathName.Text.
        static void ReadSoftObject(UObject ex, string propertyName, out string stem, out string path)
        {
            stem = null;
            path = null;

            var soft = ex.GetOrDefault<FSoftObjectPath>(propertyName);
            var softText = soft.AssetPathName.Text;
            if (!string.IsNullOrEmpty(softText) && !string.Equals(softText, "None", StringComparison.OrdinalIgnoreCase))
            {
                path = SoftToVirtualPath(softText);
                stem = ExtractStem(path);
                return;
            }

            // Older vanilla cooks sometimes embed hard refs - try the
            // FPackageIndex path. Resolve to the underlying name.
            var idx = ex.GetOrDefault<FPackageIndex>(propertyName);
            if (idx != null && !idx.IsNull && idx.Name != "None" && !string.IsNullOrEmpty(idx.Name))
            {
                stem = idx.Name;
                var resolved = idx.ResolvedObject;
                if (resolved != null)
                {
                    var p = resolved.GetPathName();
                    if (!string.IsNullOrEmpty(p)) path = SoftToVirtualPath(p);
                }
            }
        }

        // Extract the StringTableEntry.Key from an FText property. Returns
        // null if the property isn't set or doesn't use a string-table
        // history (i.e. is a literal Base-history FText).
        static string ReadFTextKey(UObject ex, string propertyName)
        {
            var ft = ex.GetOrDefault<FText>(propertyName);
            if (ft == null || ft.TextHistory == null) return null;
            if (ft.TextHistory is FTextHistory.StringTableEntry ste)
                return ste.Key;
            if (ft.TextHistory is FTextHistory.Base baseHist && !string.IsNullOrEmpty(baseHist.Key))
                return baseHist.Key;
            return null;
        }

        // "/Game/Path/Stem.Stem"  ->  "/Game/Path/Stem"
        // "/Game/Path/Stem"        ->  "/Game/Path/Stem"  (no-op)
        static string SoftToVirtualPath(string assetPathName)
        {
            if (string.IsNullOrEmpty(assetPathName)) return assetPathName;
            int dot = assetPathName.LastIndexOf('.');
            int slash = assetPathName.LastIndexOf('/');
            if (dot > slash && dot >= 0) return assetPathName.Substring(0, dot);
            return assetPathName;
        }

        // "/Game/Path/Stem" -> "Stem"
        static string ExtractStem(string virtualPath)
        {
            if (string.IsNullOrEmpty(virtualPath)) return virtualPath;
            int slash = virtualPath.LastIndexOf('/');
            return slash >= 0 ? virtualPath.Substring(slash + 1) : virtualPath;
        }

        // The Recipe DA's UE virtual path is e.g.
        //   /R5BusinessRules/Recipes/Building/Items/Decorations/DA_RD_BuildObject_Deco_Dishes_T01_Wood
        // The on-disk JSON for that lives at
        //   R5/Plugins/R5BusinessRules/Content/Recipes/Building/Items/Decorations/DA_RD_BuildObject_Deco_Dishes_T01_Wood.json
        //
        // Map "/R5BusinessRules/" virtual prefix to the on-disk
        // "R5/Plugins/R5BusinessRules/Content/" path. Other virtual
        // prefixes ("/Game/...") aren't expected for recipes - bail with
        // null so the caller can handle "this template has no recipe".
        static string DeriveRecipeJsonPath(string recipeVirtualPath)
        {
            if (string.IsNullOrWhiteSpace(recipeVirtualPath)) return null;
            const string prefix = "/R5BusinessRules/";
            if (!recipeVirtualPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                return null;
            var rel = recipeVirtualPath.Substring(prefix.Length);
            return "R5/Plugins/R5BusinessRules/Content/" + rel + ".json";
        }

        void LogLine(string msg) { if (Log != null) Log(msg); }
    }

    // Full inspection result for one DA. Mirrors the field set on the
    // legacy BuildingTemplate POCO (Painting/Bucket factories) - the
    // BuildPipeline converts an inspection into a BuildingTemplate at
    // build time.
    public sealed class VanillaBuildingTemplateInspection
    {
        public string Id;               // = PackagePath
        public string DisplayName;      // file stem
        public string Category;         // parent folder
        public string PackagePath;
        public string PakRelativePath;

        public string AssetClass;       // "R5BuildingItem" expected

        public string MeshStem;         // "SM_Bucket_01" etc.
        public string MeshPath;

        public string IconStem;
        public string IconPath;

        public string RecipeStem;
        public string RecipePath;       // /R5BusinessRules/...
        public string RecipeJsonPath;   // R5/Plugins/...json (relative to Vanilla extract root)

        public string NameKey;          // FText key for Name
        public string DescriptionKey;   // FText key for Description (or Descriptions)

        public string Error;
        public System.Collections.Generic.List<string> Warnings;
    }
}
