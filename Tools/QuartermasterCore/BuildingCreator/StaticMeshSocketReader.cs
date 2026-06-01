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
    // Reads StaticMeshSocket exports (name + relative transform) from a cooked UE5 StaticMesh. Missing transforms default to identity.
    public sealed class StaticMeshSocketReader
    {
        public string UsmapPath;
        public Action<string> Log;

        public sealed class Socket
        {
            public string Name;
            public double LocX;
            public double LocY;
            public double LocZ;
            public double Pitch;
            public double Yaw;
            public double Roll;
            public double ScaleX = 1.0;
            public double ScaleY = 1.0;
            public double ScaleZ = 1.0;
        }

        // Returns empty list if the file is missing or has no sockets - callers treat that as "use the BP's vanilla component positions".
        public List<Socket> ReadAll(string meshAssetPath)
        {
            var sockets = new List<Socket>();
            if (string.IsNullOrEmpty(meshAssetPath) || !File.Exists(meshAssetPath))
            {
                LogLine("[socket-reader] mesh file not found: " + meshAssetPath);
                return sockets;
            }
            if (string.IsNullOrEmpty(UsmapPath) || !File.Exists(UsmapPath))
                throw new InvalidOperationException("StaticMeshSocketReader: UsmapPath not set or missing: " + UsmapPath);

            var mappings = new Usmap(UsmapPath);
            var asset = new UAsset(meshAssetPath, EngineVersion.VER_UE5_6, mappings);

            for (int i = 0; i < asset.Exports.Count; i++)
            {
                var ex = asset.Exports[i] as NormalExport;
                if (ex == null) continue;
                var classType = ex.GetExportClassType()?.Value?.Value;
                if (classType != "StaticMeshSocket") continue;

                var s = new Socket();
                // Fall back to ObjectName in case the SocketName property is absent.
                s.Name = ex.ObjectName?.Value?.Value ?? "";

                foreach (var prop in ex.Data)
                {
                    var pname = prop.Name?.Value?.Value;
                    switch (pname)
                    {
                        case "SocketName":
                            if (prop is NamePropertyData np)
                                s.Name = np.Value?.Value?.Value ?? s.Name;
                            break;
                        case "RelativeLocation":
                            if (prop is StructPropertyData stp1
                                && stp1.Value != null && stp1.Value.Count > 0
                                && stp1.Value[0] is VectorPropertyData vp)
                            {
                                s.LocX = vp.Value.X;
                                s.LocY = vp.Value.Y;
                                s.LocZ = vp.Value.Z;
                            }
                            break;
                        case "RelativeRotation":
                            if (prop is StructPropertyData stp2
                                && stp2.Value != null && stp2.Value.Count > 0
                                && stp2.Value[0] is RotatorPropertyData rp)
                            {
                                s.Pitch = rp.Value.Pitch;
                                s.Yaw   = rp.Value.Yaw;
                                s.Roll  = rp.Value.Roll;
                            }
                            break;
                        case "RelativeScale":
                            // StaticMeshSocket uses "RelativeScale" without the "3D" suffix.
                            if (prop is StructPropertyData stp3
                                && stp3.Value != null && stp3.Value.Count > 0
                                && stp3.Value[0] is VectorPropertyData vpS)
                            {
                                s.ScaleX = vpS.Value.X;
                                s.ScaleY = vpS.Value.Y;
                                s.ScaleZ = vpS.Value.Z;
                            }
                            break;
                    }
                }

                sockets.Add(s);
            }

            LogLine("[socket-reader] " + Path.GetFileName(meshAssetPath)
                + ": " + sockets.Count + " socket(s)");
            return sockets;
        }

        // Returns the first socket (name-agnostic), or null if the mesh has none.
        public Socket FindFirst(string meshAssetPath)
        {
            var all = ReadAll(meshAssetPath);
            return all.Count > 0 ? all[0] : null;
        }

        void LogLine(string s)
        {
            if (Log != null) Log(s);
        }
    }
}
