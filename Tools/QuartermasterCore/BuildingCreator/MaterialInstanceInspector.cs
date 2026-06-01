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
    // Reads a legacy-format MaterialInstanceConstant uasset/uexp; fails on raw Zen-format assets (caller must retoc to-legacy first).
    public sealed class MaterialInstanceInspector
    {
        // Serializes Usmap+UAsset reads: UAssetAPI is not thread-safe and `new Usmap` opens the file exclusively. Reentrant so a caller can hold it across a whole folder scan.
        public static readonly object UsmapGate = new object();

        public string UsmapPath;

        // Returns null only if the asset isn't a MaterialInstanceConstant; throws on unreadable input.
        public MaterialInstanceData Inspect(string assetPath)
        {
            if (string.IsNullOrEmpty(assetPath))
                throw new ArgumentNullException("assetPath");
            if (!File.Exists(assetPath))
                throw new FileNotFoundException("MI asset not found", assetPath);
            if (string.IsNullOrEmpty(UsmapPath) || !File.Exists(UsmapPath))
                throw new InvalidOperationException("MaterialInstanceInspector.UsmapPath not set or not found: " + UsmapPath);

            lock (UsmapGate)
            {
                return InspectLocked(assetPath);
            }
        }

        MaterialInstanceData InspectLocked(string assetPath)
        {

            var mapping = new Usmap(UsmapPath);
            var asset = new UAsset(assetPath, EngineVersion.VER_UE5_6, mapping);

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

            foreach (var prop in miExport.Data)
            {
                var pname = prop.Name?.Value?.Value;

                if (pname == "Parent" && prop is ObjectPropertyData op)
                {
                    var imp = ResolveImport(asset, op.Value);
                    if (imp != null)
                    {
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

        static Import ResolveImport(UAsset asset, FPackageIndex idx)
        {
            if (idx == null || idx.Index >= 0) return null;
            int i = -idx.Index - 1;
            if (i < 0 || i >= asset.Imports.Count) return null;
            return asset.Imports[i];
        }
    }

    public sealed class MaterialInstanceData
    {
        public string AssetPath;
        public string AssetStem;

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
        public string TextureStem;
        public string TexturePath;
    }
}
