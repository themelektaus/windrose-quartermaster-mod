using System;
using System.Collections.Generic;
using System.IO;
using UAssetAPI;
using UAssetAPI.ExportTypes;
using UAssetAPI.PropertyTypes.Objects;
using UAssetAPI.PropertyTypes.Structs;
using UAssetAPI.UnrealTypes;
using UAssetAPI.Unversioned;

namespace Windrose.Quartermaster.Core.BuildingCreator
{
    // Reads a MaterialInstanceConstant uasset/uexp pair (legacy format)
    // and exposes its Scalar/Vector/Texture parameter blocks plus the
    // parent master-material reference.
    //
    // Works on:
    //   - Vanilla MIs after they've been extracted with retoc to-legacy
    //   - User-cooked MIs from the UE-Editor (already legacy format)
    //
    // Does NOT work on raw Zen-format assets (e.g. files directly under
    // Sources/Vanilla/...). For Vanilla inspection the caller must first
    // run retoc to-legacy --filter <stem>.
    //
    // Reader pattern extracted from .build-tmp/mi-probe/Program.cs.
    public sealed class MaterialInstanceInspector
    {
        public string UsmapPath;

        // Inspect a MI file. Returns null only if the file isn't a
        // MaterialInstanceConstant; throws on unreadable input.
        public MaterialInstanceData Inspect(string assetPath)
        {
            if (string.IsNullOrEmpty(assetPath))
                throw new ArgumentNullException("assetPath");
            if (!File.Exists(assetPath))
                throw new FileNotFoundException("MI asset not found", assetPath);
            if (string.IsNullOrEmpty(UsmapPath) || !File.Exists(UsmapPath))
                throw new InvalidOperationException("MaterialInstanceInspector.UsmapPath not set or not found: " + UsmapPath);

            var mapping = new Usmap(UsmapPath);
            var asset = new UAsset(assetPath, EngineVersion.VER_UE5_6, mapping);

            // Find the MaterialInstanceConstant export. We allow any name
            // (the stem == filename minus .uasset for top-level exports).
            NormalExport miExport = null;
            foreach (var ex in asset.Exports)
            {
                if (ex is NormalExport ne && ne.GetExportClassType()?.Value?.Value == "MaterialInstanceConstant")
                {
                    miExport = ne;
                    break;
                }
            }
            if (miExport == null) return null;

            var result = new MaterialInstanceData
            {
                AssetPath        = assetPath,
                AssetStem        = Path.GetFileNameWithoutExtension(assetPath),
                Scalars          = new List<MIScalarParam>(),
                Vectors          = new List<MIVectorParam>(),
                Textures         = new List<MITextureParam>(),
            };

            // Walk the export's properties looking for the three known
            // parameter blocks and the parent reference.
            foreach (var prop in miExport.Data)
            {
                var pname = prop.Name?.Value?.Value;

                if (pname == "Parent" && prop is ObjectPropertyData op)
                {
                    var imp = ResolveImport(asset, op.Value);
                    if (imp != null)
                    {
                        // Outer is normally the package import; its
                        // ObjectName is the /Game/... path string.
                        result.ParentMaterialStem = imp.ObjectName?.Value?.Value;
                        if (imp.OuterIndex != null && imp.OuterIndex.Index < 0)
                        {
                            var outerImp = ResolveImport(asset, imp.OuterIndex);
                            result.ParentMaterialPath = outerImp?.ObjectName?.Value?.Value;
                        }
                    }
                    continue;
                }

                if (!(prop is ArrayPropertyData arr) || arr.Value == null) continue;

                if (pname == "ScalarParameterValues")
                {
                    foreach (var item in arr.Value)
                    {
                        var entry = ReadScalarEntry(item);
                        if (entry != null) result.Scalars.Add(entry);
                    }
                }
                else if (pname == "VectorParameterValues")
                {
                    foreach (var item in arr.Value)
                    {
                        var entry = ReadVectorEntry(item);
                        if (entry != null) result.Vectors.Add(entry);
                    }
                }
                else if (pname == "TextureParameterValues")
                {
                    foreach (var item in arr.Value)
                    {
                        var entry = ReadTextureEntry(asset, item);
                        if (entry != null) result.Textures.Add(entry);
                    }
                }
            }

            return result;
        }

        // -----------------------------------------------------------------
        // Per-entry readers. Each MI param entry is a Struct with at least
        // ParameterInfo (which has Name) and ParameterValue. ExpressionGUID
        // is also present but we don't surface it (caller never edits it,
        // and we copy it through unchanged via the clone-then-patch flow).
        // -----------------------------------------------------------------
        static MIScalarParam ReadScalarEntry(PropertyData item)
        {
            if (!(item is StructPropertyData entry) || entry.Value == null) return null;
            string name = null;
            float? value = null;
            foreach (var sub in entry.Value)
            {
                if (sub.Name?.Value?.Value == "ParameterInfo" && sub is StructPropertyData pi)
                {
                    name = ReadParameterName(pi);
                }
                else if (sub.Name?.Value?.Value == "ParameterValue" && sub is FloatPropertyData fp)
                {
                    value = fp.Value;
                }
            }
            if (string.IsNullOrEmpty(name) || !value.HasValue) return null;
            return new MIScalarParam { Name = name, Value = value.Value };
        }

        static MIVectorParam ReadVectorEntry(PropertyData item)
        {
            if (!(item is StructPropertyData entry) || entry.Value == null) return null;
            string name = null;
            float r = 0, g = 0, b = 0, a = 1;
            bool gotValue = false;
            foreach (var sub in entry.Value)
            {
                if (sub.Name?.Value?.Value == "ParameterInfo" && sub is StructPropertyData pi)
                {
                    name = ReadParameterName(pi);
                }
                else if (sub.Name?.Value?.Value == "ParameterValue" && sub is StructPropertyData pvs)
                {
                    foreach (var inner in pvs.Value ?? new List<PropertyData>())
                    {
                        if (inner is LinearColorPropertyData lc)
                        {
                            r = lc.Value.R; g = lc.Value.G; b = lc.Value.B; a = lc.Value.A;
                            gotValue = true;
                        }
                    }
                }
            }
            if (string.IsNullOrEmpty(name) || !gotValue) return null;
            return new MIVectorParam { Name = name, R = r, G = g, B = b, A = a };
        }

        static MITextureParam ReadTextureEntry(UAsset asset, PropertyData item)
        {
            if (!(item is StructPropertyData entry) || entry.Value == null) return null;
            string name = null;
            string textureStem = null;
            string texturePath = null;
            foreach (var sub in entry.Value)
            {
                if (sub.Name?.Value?.Value == "ParameterInfo" && sub is StructPropertyData pi)
                {
                    name = ReadParameterName(pi);
                }
                else if (sub.Name?.Value?.Value == "ParameterValue" && sub is ObjectPropertyData op)
                {
                    var imp = ResolveImport(asset, op.Value);
                    if (imp != null)
                    {
                        textureStem = imp.ObjectName?.Value?.Value;
                        if (imp.OuterIndex != null && imp.OuterIndex.Index < 0)
                        {
                            var outerImp = ResolveImport(asset, imp.OuterIndex);
                            texturePath = outerImp?.ObjectName?.Value?.Value;
                        }
                    }
                }
            }
            if (string.IsNullOrEmpty(name)) return null;
            return new MITextureParam
            {
                Name        = name,
                TextureStem = textureStem,
                TexturePath = texturePath,
            };
        }

        static string ReadParameterName(StructPropertyData paramInfo)
        {
            if (paramInfo?.Value == null) return null;
            foreach (var sub in paramInfo.Value)
            {
                if (sub is NamePropertyData np && sub.Name?.Value?.Value == "Name")
                    return np.Value?.Value?.Value;
            }
            return null;
        }

        // Resolve an FPackageIndex to an Import entry. Returns null for
        // export indices or out-of-bounds.
        static Import ResolveImport(UAsset asset, FPackageIndex idx)
        {
            if (idx == null || idx.Index >= 0) return null;
            int i = -idx.Index - 1;
            if (i < 0 || i >= asset.Imports.Count) return null;
            return asset.Imports[i];
        }
    }

    // -------------------------------------------------------------------
    // Result types. These mirror what the GUI needs to render the dynamic
    // Slot-UI: a list of named parameters per type, each with the current
    // value (so we can pre-fill the input).
    // -------------------------------------------------------------------

    public sealed class MaterialInstanceData
    {
        public string AssetPath;             // absolute path the inspector read from
        public string AssetStem;             // filename without .uasset

        // Parent reference (the master material this MI inherits from).
        // For MI_Paintings_01 this would be ParentMaterialStem="M_Object",
        // ParentMaterialPath="/Game/Environment/Shaders/Objects/M_Object".
        public string ParentMaterialStem;
        public string ParentMaterialPath;

        public List<MIScalarParam>  Scalars;
        public List<MIVectorParam>  Vectors;
        public List<MITextureParam> Textures;
    }

    public sealed class MIScalarParam
    {
        public string Name;
        public float  Value;
    }

    public sealed class MIVectorParam
    {
        public string Name;
        public float  R, G, B, A;
    }

    public sealed class MITextureParam
    {
        public string Name;
        public string TextureStem;   // e.g. "T_Paintings_01_A"
        public string TexturePath;   // e.g. "/Game/Environment/.../T_Paintings_01_A"
    }
}
