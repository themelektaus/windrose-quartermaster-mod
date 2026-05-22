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
    // Etappe J v4: Reads StaticMeshSocket entries (name + relative transform)
    // from a cooked UE5 StaticMesh .uasset via UAssetAPI.
    //
    // Each socket in a cooked StaticMesh surfaces as its own NormalExport with
    // class="StaticMeshSocket". The export carries the SocketName + Relative-
    // Location / RelativeRotation / RelativeScale (each as a struct).
    //
    // The reader is tolerant: missing transforms default to the identity, so
    // an empty Blender Plain Axes object at (0,0,80) survives Blender->FBX->UE
    // ->Cook with Z=80cm and (Rot=0, Scale=1) automatically.
    //
    // Used by the build pipeline when a building has a FlamePresetId set:
    // the FIRST socket found (name-agnostic) is consulted to position the
    // cloned BP's NiagaraComponent / Light / Audio components. If the mesh
    // has zero sockets, the build pipeline skips the flame for that
    // building entirely (no BP clone, no DA ItemClass swap).
    public sealed class StaticMeshSocketReader
    {
        public string UsmapPath;
        public Action<string> Log;

        public sealed class Socket
        {
            // SocketName as it appears in the cooked mesh. The build pipeline
            // takes the first socket regardless of name (name-agnostic), but
            // logs the name so users can see which socket their flame ended
            // up at.
            public string Name;
            // RelativeLocation in UE-cm. Defaults to (0,0,0) if missing.
            public double LocX;
            public double LocY;
            public double LocZ;
            // RelativeRotation in degrees. Defaults to identity if missing.
            public double Pitch;
            public double Yaw;
            public double Roll;
            // RelativeScale. Defaults to (1,1,1) if missing.
            public double ScaleX = 1.0;
            public double ScaleY = 1.0;
            public double ScaleZ = 1.0;
        }

        // Reads all sockets from the given cooked mesh file. Returns empty
        // list if the file is missing or has no sockets - callers treat that
        // as "use the BP's vanilla component positions".
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
                // Default name = the export's ObjectName, in case SocketName
                // is absent (rare - cooked sockets nearly always have an
                // explicit SocketName property).
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
                            // Some cooked meshes use "RelativeScale" (without
                            // the "3D" suffix) for the socket scale - that's
                            // the StaticMeshSocket convention.
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

        // Convenience: returns the FIRST socket found in the mesh (any
        // name accepted - name-agnostic), or null if the mesh has no
        // sockets at all. The pipeline calls this when a flame preset is
        // active. The original revision matched only sockets named "flame"
        // (case-insensitive); the current contract accepts any socket
        // because users have legitimate reasons to use other names
        // (e.g. "Flame_01", "torch_tip") and the multi-flame-from-N-sockets
        // experiment was rolled back so only the first socket is consumed.
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
