using System;
using System.IO;
using System.Linq;
using CUE4Parse.UE4.Assets.Exports;
using CUE4Parse.UE4.Assets.Objects;
using CUE4Parse.UE4.Objects.Core.i18N;
using CUE4Parse.UE4.Objects.UObject;

namespace Windrose.Quartermaster.Core.BuildingCreator
{
    // Reads a vanilla R5BuildingItem DataAsset and extracts the mesh/icon/recipe refs + Name/Description FText keys for a BuildingTemplate.
    // R5BuildingItem property names: mesh="PreviewMeshes" (array, index 0), recipe="BuildingCost" (NOT "Recipe"); some DAs spell description "Descriptions".
    public sealed class VanillaBuildingTemplateInspector
    {
        public VanillaBuildingTemplateCatalog Catalog;
        public Action<string> Log;

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

            // Prefer the R5BuildingItem export; else fall back to the first non-empty-class export.
            UObject mainExport = null;
            string mainClass = null;
            foreach (var ex in pkg.GetExports())
            {
                if (ex == null) continue;
                var cls = ex.ExportType ?? "";
                if (string.Equals(cls, "R5BuildingItem", StringComparison.OrdinalIgnoreCase))
                {
                    mainExport = ex;
                    mainClass = cls;
                    break;
                }
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
                result.Warnings.Add("Asset class is '" + mainClass + "', not R5BuildingItem - this DA may not be cloneable as a building template.");
            }

            ReadSoftObjectArray(mainExport, "PreviewMeshes", out result.MeshStem, out result.MeshPath);
            ReadSoftObject(mainExport, "Icon",         out result.IconStem,   out result.IconPath);
            ReadSoftObject(mainExport, "BuildingCost", out result.RecipeStem, out result.RecipePath);

            result.NameKey = ReadFTextKey(mainExport, "Name");
            result.DescriptionKey = ReadFTextKey(mainExport, "Description");
            if (string.IsNullOrEmpty(result.DescriptionKey))
                result.DescriptionKey = ReadFTextKey(mainExport, "Descriptions");

            if (!string.IsNullOrEmpty(result.RecipePath))
                result.RecipeJsonPath = DeriveRecipeJsonPath(result.RecipePath);

            return result;
        }

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

        // FSoftObjectPath is a non-nullable struct, so "no value" is signalled by an empty AssetPathName.Text. Falls back to FPackageIndex for hard-ref cooks.
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

        static string SoftToVirtualPath(string assetPathName)
        {
            if (string.IsNullOrEmpty(assetPathName)) return assetPathName;
            int dot = assetPathName.LastIndexOf('.');
            int slash = assetPathName.LastIndexOf('/');
            if (dot > slash && dot >= 0) return assetPathName.Substring(0, dot);
            return assetPathName;
        }

        static string ExtractStem(string virtualPath)
        {
            if (string.IsNullOrEmpty(virtualPath)) return virtualPath;
            int slash = virtualPath.LastIndexOf('/');
            return slash >= 0 ? virtualPath.Substring(slash + 1) : virtualPath;
        }

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

    public sealed class VanillaBuildingTemplateInspection
    {
        public string Id;               // = PackagePath
        public string DisplayName;
        public string Category;
        public string PackagePath;
        public string PakRelativePath;

        public string AssetClass;

        public string MeshStem;
        public string MeshPath;

        public string IconStem;
        public string IconPath;

        public string RecipeStem;
        public string RecipePath;
        public string RecipeJsonPath;

        public string NameKey;
        public string DescriptionKey;

        public string Error;
        public System.Collections.Generic.List<string> Warnings;
    }
}
