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
    // Reads everything the GUI needs from a user-cooked Content folder:
    //   - mesh (SM_<stem>.uasset) -> material slot list with slot-names
    //     + per-slot user-MI references
    //   - all MI_*.uasset files -> inspected via MaterialInstanceInspector
    //
    // The GUI feeds this into its dynamic slot UI: per mesh-slot we know
    // the User-cooked MI ref, can match it against the inspected user-MIs
    // to pre-fill values, and lay out the appropriate Vanilla-MI picker
    // and param-editing controls.
    //
    // All input files must be in legacy UE format (the UE-Editor produces
    // them that way during cook).
    public sealed class CookedFolderInspector
    {
        public string UsmapPath;
        public Action<string> Log;

        public CookedFolderInspection Inspect(string cookedFolderPath, string meshStem)
        {
            if (string.IsNullOrWhiteSpace(cookedFolderPath))
                throw new ArgumentException("cookedFolderPath is required");
            if (!Directory.Exists(cookedFolderPath))
                throw new ArgumentException("CookedFolder not found: " + cookedFolderPath);
            if (string.IsNullOrWhiteSpace(UsmapPath) || !File.Exists(UsmapPath))
                throw new InvalidOperationException("CookedFolderInspector.UsmapPath not set or not found");

            var inspection = new CookedFolderInspection
            {
                CookedFolderPath = cookedFolderPath,
                MeshStem         = meshStem,
                MeshSlots        = new List<MeshMaterialSlot>(),
                UserMaterialInstances = new Dictionary<string, MaterialInstanceData>(StringComparer.OrdinalIgnoreCase),
                Warnings         = new List<string>(),
            };

            // 1) Read the mesh (optional - if MeshStem missing or file
            //    absent we just skip and the GUI shows "specify mesh stem"
            //    state).
            if (!string.IsNullOrWhiteSpace(meshStem))
            {
                var meshFile = Path.Combine(cookedFolderPath, meshStem + ".uasset");
                if (!File.Exists(meshFile))
                {
                    inspection.Warnings.Add(
                        "Mesh '" + meshStem + ".uasset' not found in cooked folder - "
                        + "specify the correct mesh stem or re-cook the asset.");
                }
                else
                {
                    try
                    {
                        inspection.MeshSlots = ReadMeshSlots(meshFile);
                        LogLine("[cooked-inspect] mesh '" + meshStem
                            + "': " + inspection.MeshSlots.Count + " material slot(s)");
                    }
                    catch (Exception ex)
                    {
                        inspection.Warnings.Add(
                            "Failed to read mesh '" + meshStem + "': "
                            + ex.GetType().Name + ": " + ex.Message);
                    }
                }
            }

            // 2) Walk the cooked folder for MI_*.uasset files, inspect each.
            //    These are user-cooked MaterialInstanceConstants; we use
            //    them ONLY as pre-fill defaults for the GUI. The build
            //    pipeline never ships them (skip-list).
            var inspector = new MaterialInstanceInspector { UsmapPath = UsmapPath };
            foreach (var file in Directory.GetFiles(cookedFolderPath, "MI_*.uasset", SearchOption.TopDirectoryOnly))
            {
                var stem = Path.GetFileNameWithoutExtension(file);
                try
                {
                    var mi = inspector.Inspect(file);
                    if (mi != null)
                    {
                        inspection.UserMaterialInstances[stem] = mi;
                        LogLine("[cooked-inspect] user MI '" + stem
                            + "': parent=" + (mi.ParentMaterialStem ?? "<none>")
                            + " scalar=" + mi.Scalars.Count
                            + " vector=" + mi.Vectors.Count
                            + " texture=" + mi.Textures.Count);
                    }
                }
                catch (Exception ex)
                {
                    inspection.Warnings.Add(
                        "Failed to read MI '" + stem + "': "
                        + ex.GetType().Name + ": " + ex.Message);
                }
            }

            return inspection;
        }

        // -----------------------------------------------------------------
        // Mesh reader. UE StaticMesh has a `StaticMaterials` array on the
        // primary export, each entry being a StaticMaterial struct with
        // MaterialInterface (Object ref) + MaterialSlotName (FName).
        // -----------------------------------------------------------------
        List<MeshMaterialSlot> ReadMeshSlots(string meshFile)
        {
            var mapping = new Usmap(UsmapPath);
            var asset = new UAsset(meshFile, EngineVersion.VER_UE5_6, mapping);

            // Find the StaticMesh export (the primary export of a SM_*.uasset).
            NormalExport meshExport = null;
            foreach (var ex in asset.Exports)
            {
                if (ex is NormalExport ne && ne.GetExportClassType()?.Value?.Value == "StaticMesh")
                {
                    meshExport = ne;
                    break;
                }
            }
            if (meshExport == null)
                throw new InvalidOperationException("Asset has no StaticMesh export");

            var result = new List<MeshMaterialSlot>();
            foreach (var prop in meshExport.Data)
            {
                if (prop is ArrayPropertyData arr
                    && prop.Name?.Value?.Value == "StaticMaterials"
                    && arr.Value != null)
                {
                    for (int i = 0; i < arr.Value.Length; i++)
                    {
                        var item = arr.Value[i];
                        if (!(item is StructPropertyData entry) || entry.Value == null) continue;

                        string slotName = null;
                        string materialStem = null;
                        string materialPath = null;
                        foreach (var sub in entry.Value)
                        {
                            var subName = sub.Name?.Value?.Value;
                            if (subName == "MaterialSlotName" && sub is NamePropertyData np)
                            {
                                slotName = np.Value?.Value?.Value;
                            }
                            else if (subName == "MaterialInterface" && sub is ObjectPropertyData op)
                            {
                                if (op.Value != null && op.Value.Index < 0)
                                {
                                    int impIdx = -op.Value.Index - 1;
                                    if (impIdx >= 0 && impIdx < asset.Imports.Count)
                                    {
                                        var imp = asset.Imports[impIdx];
                                        materialStem = imp.ObjectName?.Value?.Value;
                                        if (imp.OuterIndex != null && imp.OuterIndex.Index < 0)
                                        {
                                            int outerIdx = -imp.OuterIndex.Index - 1;
                                            if (outerIdx >= 0 && outerIdx < asset.Imports.Count)
                                            {
                                                materialPath = asset.Imports[outerIdx].ObjectName?.Value?.Value;
                                            }
                                        }
                                    }
                                }
                            }
                        }
                        result.Add(new MeshMaterialSlot
                        {
                            Index            = i,
                            SlotName         = slotName ?? ("slot" + i),
                            UserMaterialStem = materialStem,
                            UserMaterialPath = materialPath,
                        });
                    }
                    break;
                }
            }
            return result;
        }

        void LogLine(string msg) { if (Log != null) Log(msg); }
    }

    public sealed class CookedFolderInspection
    {
        public string CookedFolderPath;
        public string MeshStem;
        public List<MeshMaterialSlot> MeshSlots;

        // Inspected user-cooked MIs keyed by stem (filename without ext).
        // The GUI matches a mesh slot's UserMaterialStem against this dict
        // to find the pre-fill source.
        public Dictionary<string, MaterialInstanceData> UserMaterialInstances;

        public List<string> Warnings;
    }

    public sealed class MeshMaterialSlot
    {
        public int    Index;
        public string SlotName;            // e.g. "WorldGridMaterial", "lambert1", "Frame"
        public string UserMaterialStem;    // user-cooked MI ref, e.g. "MI_QmPainting_Canvas"
        public string UserMaterialPath;    // package path, e.g. "/Game/Quartermaster/Items/MI_QmPainting_Canvas"
    }
}
