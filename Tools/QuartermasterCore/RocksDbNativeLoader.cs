using System;
using System.Runtime.InteropServices;
using RocksDbSharp;

namespace Windrose.Quartermaster.Core
{
    // Single-file-publish fix for the RocksDB native library.
    //
    // The save patchers (jewelry + ship) open the game's RocksDB stores via
    // RocksDbSharp, whose bundled native loader (RocksDbSharp.NativeImport, "Auto"
    // mode) probes for "rocksdb" only next to the executable and in
    // runtimes/<rid>/native/. In a dev build the native sits physically at
    //   bin/.../runtimes/win-x64/native/rocksdb.dll
    // so that probing succeeds. In a PublishSingleFile build with
    // IncludeNativeLibrariesForSelfExtract=true the native is packed inside the
    // .exe and self-extracted to a temp directory that NativeImport never
    // searches -> RocksDb.Open throws "Unable to load ... rocksdb", which the
    // per-character discovery loop swallows -> /api/savegame/characters and
    // /api/savegame/ships return an empty list while still reporting
    // supported = true (discovery is filesystem-only and finds the folders).
    //
    // Fix: before the first RocksDB call, pre-load the native through the STANDARD
    // .NET resolver via the RocksDbSharp assembly. That resolver (unlike
    // NativeImport) understands single-file self-extraction and resolves the
    // native from the bundle's extraction directory. Once the OS has a module
    // named "rocksdb" loaded in the process, RocksDbSharp's later
    // LoadLibrary("rocksdb") reuses that already-loaded module and succeeds.
    //
    // Dev builds (and any layout where NativeImport would have found the native
    // on disk anyway) are unaffected: the load either succeeds redundantly or the
    // catch swallows it and RocksDbSharp falls back to its own probing.
    public static class RocksDbNativeLoader
    {
        static readonly object s_gate = new object();
        static bool s_attempted;

        // Idempotent, thread-safe, never throws. Call once at app startup before
        // any RocksDB usage (a second call is a cheap no-op).
        public static void EnsurePreloaded()
        {
            if (s_attempted) return;
            lock (s_gate)
            {
                if (s_attempted) return;
                s_attempted = true;
                try
                {
                    // typeof(RocksDb).Assembly == RocksDbSharp.dll; its dependency
                    // resolver maps the bare name "rocksdb" to the real native
                    // (rocksdb.dll / librocksdb.so) including the self-extract path.
                    NativeLibrary.Load("rocksdb", typeof(RocksDb).Assembly, null);
                }
                catch
                {
                    // Not fatal: on disk-layout runs RocksDbSharp's own loader will
                    // still find the native; if it genuinely cannot, the first real
                    // RocksDb.Open surfaces a precise exception to the caller.
                }
            }
        }
    }
}
