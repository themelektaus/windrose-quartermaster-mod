using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;

namespace Windrose.Quartermaster.Core
{
    // Rebuilds the game's RocksDB_v2_Backups checkpoint ZIP after a live DB write.
    // The game restores the live DB from this ZIP on every load, so any write to
    // the live DB must be reflected here or the change is reverted on next launch.
    //
    // Faithful port of the reference patcher's checkpoint_zip.py. The ZIP holds:
    //   Checkpoint/meta/1                       index: "<path> crc32 <crc32c>" lines
    //   Checkpoint/shared_checksum/<renamed>    .sst / .blob payloads
    //   Checkpoint/private/1/<name>             MANIFEST / CURRENT / OPTIONS / .log
    //   *AdditionalRecordFiles*                 copied verbatim from the old ZIP
    internal static class CheckpointZipBuilder
    {
        static readonly uint[] Crc32cTable = BuildCrc32cTable();

        static uint[] BuildCrc32cTable()
        {
            var table = new uint[256];
            for (uint i = 0; i < 256; i++)
            {
                uint crc = i;
                for (int k = 0; k < 8; k++)
                    crc = (crc & 1) != 0 ? (crc >> 1) ^ 0x82F63B78u : crc >> 1;
                table[i] = crc;
            }
            return table;
        }

        static uint Crc32c(byte[] data)
        {
            uint crc = 0xFFFFFFFFu;
            foreach (var b in data)
                crc = Crc32cTable[(crc ^ b) & 0xFF] ^ (crc >> 8);
            return crc ^ 0xFFFFFFFFu;
        }

        static readonly byte[] SessionMarker = Encoding.ASCII.GetBytes("session.identity");

        static string SessionIdentity(byte[] data)
        {
            int pos = IndexOf(data, SessionMarker, 0);
            if (pos < 0) return null;
            int i = pos + SessionMarker.Length;
            while (i < data.Length && data[i] == 0) i++;
            var sb = new StringBuilder();
            while (i < data.Length && data[i] >= 0x20 && data[i] < 0x7F)
            { sb.Append((char)data[i]); i++; }
            var s = sb.ToString().Trim();
            return s.Length == 0 ? null : s;
        }

        static readonly HashSet<string> SkipNames = new HashSet<string>(StringComparer.Ordinal)
        { "LOCK", "IDENTITY", "LOG", "rocksdict-config.json", "rocksdict-config.bak" };

        // save_root = .../RocksDB_v2/<version> ; dbDir = .../Players/<id>
        public static bool UpdateCheckpointZip(string saveRoot, string dbDir)
        {
            var saveRootDir = new DirectoryInfo(saveRoot);
            var dbDirInfo = new DirectoryInfo(dbDir);
            var profileRoot = saveRootDir.Parent?.Parent; // .../<steamid>
            if (profileRoot == null) return false;
            string version = saveRootDir.Name;
            string dbType = dbDirInfo.Parent?.Name; // "Players"
            string dbId = dbDirInfo.Name;

            string zipPath = Path.Combine(profileRoot.FullName, "RocksDB_v2_Backups",
                dbType, dbId, dbId + "_" + version + "_Latest.zip");
            if (!File.Exists(zipPath)) return false;

            string metaLine0 = "0", metaLine1 = "0";
            var additional = new List<(string name, byte[] content)>();
            using (var oldZip = ZipFile.OpenRead(zipPath))
            {
                var metaEntry = oldZip.GetEntry("Checkpoint/meta/1");
                if (metaEntry != null)
                {
                    var lines = ReadAllText(metaEntry).Replace("\r\n", "\n").Trim('\n').Split('\n');
                    if (lines.Length > 0) metaLine0 = lines[0];
                    if (lines.Length > 1) metaLine1 = lines[1];
                }
                foreach (var e in oldZip.Entries)
                    if (e.FullName.Contains("AdditionalRecordFiles"))
                        additional.Add((e.FullName, ReadAllBytes(e)));
            }

            var shared = new List<(string path, byte[] content)>();
            var priv = new List<(string path, byte[] content)>();

            foreach (var f in dbDirInfo.GetFiles().OrderBy(f => f.Name, StringComparer.Ordinal))
            {
                string name = f.Name;
                if (SkipNames.Contains(name) || name.StartsWith("LOG", StringComparison.Ordinal))
                    continue;
                byte[] content = File.ReadAllBytes(f.FullName);
                string stem = Path.GetFileNameWithoutExtension(name);
                string ext = f.Extension;

                if (ext == ".sst")
                {
                    string sid = SessionIdentity(content);
                    string renamed = sid != null
                        ? stem + "_s" + sid + "_" + content.Length + ".sst"
                        : stem + "_" + content.Length + ".sst";
                    shared.Add(("shared_checksum/" + renamed, content));
                }
                else if (ext == ".blob")
                {
                    uint crc = Crc32c(content);
                    string renamed = stem + "_" + crc + "_" + content.Length + ".blob";
                    shared.Add(("shared_checksum/" + renamed, content));
                }
                else if (name.StartsWith("MANIFEST-", StringComparison.Ordinal)
                    || name == "CURRENT"
                    || name.StartsWith("OPTIONS-", StringComparison.Ordinal)
                    || ext == ".log")
                {
                    priv.Add(("private/1/" + name, content));
                }
            }

            var all = new List<(string path, byte[] content)>();
            all.AddRange(shared);
            all.AddRange(priv);

            var sb = new StringBuilder();
            sb.Append(metaLine0).Append('\n').Append(metaLine1).Append('\n')
              .Append(all.Count).Append('\n');
            foreach (var (path, content) in all)
                sb.Append(path).Append(" crc32 ").Append(Crc32c(content)).Append('\n');
            byte[] metaContent = Encoding.UTF8.GetBytes(sb.ToString());

            string tmp = zipPath + ".tmp";
            if (File.Exists(tmp)) File.Delete(tmp);
            using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write))
            using (var zip = new ZipArchive(fs, ZipArchiveMode.Create))
            {
                WriteEntry(zip, "Checkpoint/meta/1", metaContent);
                foreach (var (path, content) in all)
                    WriteEntry(zip, "Checkpoint/" + path, content);
                foreach (var (zipName, content) in additional)
                    WriteEntry(zip, zipName, content);
            }
            // Atomic-ish replace.
            File.Copy(tmp, zipPath, overwrite: true);
            File.Delete(tmp);
            return true;
        }

        static void WriteEntry(ZipArchive zip, string name, byte[] content)
        {
            var entry = zip.CreateEntry(name, CompressionLevel.Optimal);
            using var s = entry.Open();
            s.Write(content, 0, content.Length);
        }

        static byte[] ReadAllBytes(ZipArchiveEntry e)
        {
            using var s = e.Open();
            using var ms = new MemoryStream();
            s.CopyTo(ms);
            return ms.ToArray();
        }

        static string ReadAllText(ZipArchiveEntry e)
            => Encoding.UTF8.GetString(ReadAllBytes(e));

        static int IndexOf(byte[] hay, byte[] needle, int start)
        {
            for (int i = start; i <= hay.Length - needle.Length; i++)
            {
                bool ok = true;
                for (int j = 0; j < needle.Length; j++)
                    if (hay[i + j] != needle[j]) { ok = false; break; }
                if (ok) return i;
            }
            return -1;
        }
    }
}
