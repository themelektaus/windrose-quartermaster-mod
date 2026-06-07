using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using RocksDbSharp;

namespace Windrose.Quartermaster.Core
{
    // "Level Rewards" EXISTING-character save patcher.
    //
    // The DA_HeroLevels pak only changes how many talent / stat points FUTURE
    // level-ups grant; like the equipment-slot paks it does NOT retro-fit
    // existing characters. The points a character already earned are baked into
    // its RocksDB save as two persisted int32 pools, and the game does NOT read
    // our pak when loading them - they are plain save state. That is exactly why
    // patching those int32 pools directly works and survives reloads.
    //
    // To retro-fit the mod onto an existing character we set those free pools
    // directly to the mod-cumulative total for the character's level. The caller
    // (front-end, reusing the Level Rewards tab math) computes the absolute target
    // free counts and sends them. The patch is BIDIRECTIONAL: raising the
    // multiplier tops the pool up, lowering it (e.g. back to vanilla 1x) reduces it
    // again. It only ever changes UNSPENT points (invested nodes are never
    // touched) and clamps to a sane range, so the worst it can do is fail to claw
    // back points the player already spent.
    //
    // Save layout (verified by the save-inspector spike against all 3 test chars):
    //   PlayerMetadata.PlayerProgression
    //     RewardLevel            int32   (character level - 1)
    //     StatTree   { ProgressionPoints int32 (free), Nodes[] { NodeLevel int32 } }
    //     TalentTree { ProgressionPoints int32 (free), Nodes[] { NodeLevel int32 } }
    // ProgressionPoints is a fixed-width int32, so the patch overwrites 4 bytes in
    // place - no array surgery and no document-size recalculation (unlike the
    // jewelry slot patcher). spent = sum of NodeLevel across Nodes; earned =
    // free + spent. Already-invested nodes are never touched.
    //
    // NOTE: an earlier theory that an in-game stat / talent RESET would recompute
    // the pools from vanilla and discard the bonus did NOT hold up - a follow-up
    // test showed a post-patch reset kept the granted points, so no caveat is
    // surfaced in the UI anymore.
    //
    // Reuses InventorySaveSlotsPatcher's discovery (same canonical-folder rules)
    // and CheckpointZipBuilder (the game restores the live DB from a checkpoint
    // ZIP on load, so the write must be mirrored there or it reverts on next
    // launch). Steam Cloud Sync must be off; the game must be fully closed (it
    // locks the DB). The BSON byte surgery is split into the pure, RocksDB-free
    // statics ReadValue / BuildPatchedValue so it can be verified directly against
    // real save bytes.
    public sealed class ProgressionSaveSlotsPatcher
    {
        public const int MinPoints = 0;
        public const int MaxPoints = 9999;
        const string PlayerCf = "R5BLPlayer";
        const string ProgressionTag = "PlayerProgression";

        public Action<string> Log;
        void LogLine(string m) { if (Log != null) Log(m); }

        // ------------------------------------------------------------------
        // Discovery (read-only)
        // ------------------------------------------------------------------

        public List<SaveProgressionCharacter> DiscoverCharacters()
        {
            var result = new List<SaveProgressionCharacter>();
            foreach (var folder in InventorySaveSlotsPatcher.DiscoverCharacterDbFolders())
            {
                try
                {
                    var info = ReadCharacter(folder);
                    if (info != null) result.Add(info);
                }
                catch (Exception e)
                {
                    LogLine("  skip " + folder + ": " + e.Message);
                }
            }
            return result;
        }

        public SaveProgressionCharacter ReadCharacter(string dbFolder)
        {
            if (!IsCharacterDbDir(dbFolder))
                throw new DirectoryNotFoundException(
                    "Not a Windrose character save folder (no CURRENT): " + dbFolder);

            var (dbOpts, cfs) = OpenOptions(dbFolder);
            using var db = RocksDb.OpenReadOnly(dbOpts, dbFolder, cfs, false);
            var cf = db.GetColumnFamily(PlayerCf);
            var (_, value) = FindPlayer(db, cf);
            if (value == null) return null;

            var v = ReadValue(value);
            return new SaveProgressionCharacter
            {
                DbFolder = dbFolder,
                CharacterId = Path.GetFileName(dbFolder),
                PlayerName = GetPlayerName(value) ?? Path.GetFileName(dbFolder),
                RewardLevel = v.RewardLevel,
                CharacterLevel = v.CharacterLevel,
                FreeTalent = v.TalentFree,
                FreeStat = v.StatFree,
                SpentTalent = v.TalentSpent,
                SpentStat = v.StatSpent,
                EarnedTalent = v.TalentFree + v.TalentSpent,
                EarnedStat = v.StatFree + v.StatSpent,
            };
        }

        // ------------------------------------------------------------------
        // Patch (mutating): raise the free pools, backup, checkpoint ZIP.
        // ------------------------------------------------------------------

        public ProgressionPatchResult PatchCharacter(string dbFolder, int targetTalentFree, int targetStatFree)
        {
            if (!IsCharacterDbDir(dbFolder))
                throw new DirectoryNotFoundException(
                    "Not a Windrose character save folder (no CURRENT): " + dbFolder);

            // Never let a caller point this write at an arbitrary directory.
            var profilesRoot = InventorySaveSlotsPatcher.SaveProfilesRoot();
            if (profilesRoot == null || !Path.GetFullPath(dbFolder)
                    .StartsWith(Path.GetFullPath(profilesRoot), StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException(
                    "Refusing to patch a folder outside the Windrose save profiles root.");

            var result = new ProgressionPatchResult();

            var (dbOpts, cfs) = OpenOptions(dbFolder);
            byte[] key, value;
            using (var db = RocksDb.Open(dbOpts, dbFolder, cfs))
            {
                var cf = db.GetColumnFamily(PlayerCf);
                (key, value) = FindPlayer(db, cf);
                if (value == null)
                    throw new InvalidOperationException("No player progression found in this save.");

                var v = ReadValue(value);
                result.PlayerName = GetPlayerName(value) ?? Path.GetFileName(dbFolder);
                result.OldTalent = v.TalentFree;
                result.OldStat = v.StatFree;

                // Set the free pools to the requested target (bidirectional: a
                // lower multiplier reduces them back toward vanilla). Only UNSPENT
                // points change - invested nodes are never touched - and the value
                // is clamped to a sane range, so it cannot corrupt a character.
                int newTalent = Clamp(targetTalentFree);
                int newStat = Clamp(targetStatFree);
                result.NewTalent = newTalent;
                result.NewStat = newStat;

                if (newTalent == v.TalentFree && newStat == v.StatFree)
                {
                    result.AlreadyMatches = true;
                    return result;
                }

                // Pre-patch backup (never overwrite an existing one). Distinct name
                // from the jewelry / ship patchers so all backups coexist.
                var bak = Path.Combine(dbFolder,
                    Path.GetFileName(dbFolder) + ".value.progression.pre-patch.bak");
                if (!File.Exists(bak))
                {
                    File.WriteAllBytes(bak, value);
                    result.BackupPath = bak;
                    LogLine("  pre-patch backup: " + bak);
                }

                var newValue = BuildPatchedValue(value, newTalent, newStat);
                db.Put(key, newValue, cf);
                try { db.CompactRange((byte[])null, (byte[])null, cf); } catch { }

                LogLine("  patched free points: talent " + v.TalentFree + " -> " + newTalent
                        + ", stat " + v.StatFree + " -> " + newStat
                        + " (" + result.PlayerName + ")");
            }

            // Rebuild the checkpoint ZIP the game restores from on load.
            // dbFolder = .../RocksDB_v2/<version>/Players/<id>; saveRoot = .../<version>.
            var saveRoot = Directory.GetParent(dbFolder)?.Parent?.FullName;
            if (saveRoot != null)
            {
                try
                {
                    result.CheckpointZipRebuilt = CheckpointZipBuilder.UpdateCheckpointZip(saveRoot, dbFolder);
                    LogLine(result.CheckpointZipRebuilt
                        ? "  checkpoint ZIP rebuilt"
                        : "  checkpoint ZIP not found (skipped)");
                }
                catch (Exception e)
                {
                    LogLine("  WARNING: checkpoint ZIP rebuild failed: " + e.Message
                            + " - the live save is patched but the next launch may revert it.");
                }
            }

            result.Patched = true;
            return result;
        }

        static int Clamp(int v) => v < MinPoints ? MinPoints : (v > MaxPoints ? MaxPoints : v);

        // ------------------------------------------------------------------
        // Pure BSON surgery (no RocksDB) - directly testable against real bytes.
        // ------------------------------------------------------------------

        // Read the progression view (level + free / spent pools) from a raw
        // R5BLPlayer BSON value.
        public static ProgressionView ReadValue(byte[] value)
        {
            if (value == null) throw new ArgumentNullException(nameof(value));
            var loc = Locate(value);
            return new ProgressionView
            {
                RewardLevel = loc.RewardLevel,
                CharacterLevel = loc.RewardLevel + 1,
                TalentFree = loc.TalentFree,
                StatFree = loc.StatFree,
                TalentSpent = loc.TalentSpent,
                StatSpent = loc.StatSpent,
            };
        }

        // Clone `value`, overwrite the two free-pool int32s in place, and return
        // the patched bytes. The document length is unchanged (fixed-width int32),
        // which is asserted before returning.
        public static byte[] BuildPatchedValue(byte[] value, int newTalentFree, int newStatFree)
        {
            if (value == null) throw new ArgumentNullException(nameof(value));
            var loc = Locate(value);
            var outp = (byte[])value.Clone();
            WriteI32(outp, loc.TalentFreePos, newTalentFree);
            WriteI32(outp, loc.StatFreePos, newStatFree);
            if ((int)U32(outp, 0) != outp.Length)
                throw new InvalidDataException(
                    "Internal error: root size " + U32(outp, 0) + " != buffer length "
                    + outp.Length + " after in-place int32 write.");
            return outp;
        }

        sealed class ProgLoc
        {
            public int RewardLevel;
            public int TalentFree, TalentFreePos, TalentSpent;
            public int StatFree, StatFreePos, StatSpent;
        }

        static ProgLoc Locate(byte[] b)
        {
            int prog = FindSubdoc(b, 0, "PlayerProgression");
            if (prog < 0) throw new InvalidDataException(
                "PlayerProgression subdoc not found (save layout may have changed).");

            var loc = new ProgLoc();
            var rl = Field(b, prog, "RewardLevel");
            loc.RewardLevel = rl is { t: BT_INT32 } ? I32(b, rl.Value.vpos) : 0;

            LocateTree(b, prog, "TalentTree",
                out loc.TalentFree, out loc.TalentFreePos, out loc.TalentSpent);
            LocateTree(b, prog, "StatTree",
                out loc.StatFree, out loc.StatFreePos, out loc.StatSpent);
            return loc;
        }

        static void LocateTree(byte[] b, int progDoc, string treeName,
            out int free, out int freePos, out int spent)
        {
            free = 0; freePos = -1; spent = 0;
            var tree = Field(b, progDoc, treeName);
            if (tree is not { t: BT_SUBDOC })
                throw new InvalidDataException(treeName + " subdoc not found.");
            int treeDoc = tree.Value.vpos;

            var pp = Field(b, treeDoc, "ProgressionPoints");
            if (pp is { t: BT_INT32 }) { free = I32(b, pp.Value.vpos); freePos = pp.Value.vpos; }
            else throw new InvalidDataException(treeName + ".ProgressionPoints int32 not found.");

            var nodes = Field(b, treeDoc, "Nodes");
            if (nodes is { t: BT_ARRAY })
                foreach (var (t, _, vpos, _) in Elements(b, nodes.Value.vpos))
                {
                    if (t != BT_SUBDOC) continue;
                    var nl = Field(b, vpos, "NodeLevel");
                    if (nl is { t: BT_INT32 }) { int lv = I32(b, nl.Value.vpos); if (lv > 0) spent += lv; }
                }
        }

        // Recursive: value position of the FIRST subdoc named `name`.
        static int FindSubdoc(byte[] b, int docStart, string name)
        {
            foreach (var (t, n, vpos, _) in Elements(b, docStart))
            {
                if (t == BT_SUBDOC)
                {
                    if (n == name) return vpos;
                    int r = FindSubdoc(b, vpos, name);
                    if (r >= 0) return r;
                }
                else if (t == BT_ARRAY)
                {
                    foreach (var (et, _, evp, _) in Elements(b, vpos))
                        if (et == BT_SUBDOC)
                        {
                            int r = FindSubdoc(b, evp, name);
                            if (r >= 0) return r;
                        }
                }
            }
            return -1;
        }

        // ------------------------------------------------------------------
        // RocksDB plumbing
        // ------------------------------------------------------------------

        static bool IsCharacterDbDir(string path)
            => Directory.Exists(path) && File.Exists(Path.Combine(path, "CURRENT"));

        static (DbOptions, ColumnFamilies) OpenOptions(string dbFolder)
        {
            var dbOpts = new DbOptions()
                .SetCreateIfMissing(false)
                .SetCreateMissingColumnFamilies(false);
            var cfs = new ColumnFamilies();
            foreach (var n in RocksDb.ListColumnFamilies(dbOpts, dbFolder))
                cfs.Add(n, new ColumnFamilyOptions().SetCompression(Compression.No));
            return (dbOpts, cfs);
        }

        static (byte[] key, byte[] value) FindPlayer(RocksDb db, ColumnFamilyHandle cf)
        {
            var tag = Encoding.ASCII.GetBytes(ProgressionTag);
            using var it = db.NewIterator(cf);
            for (it.SeekToFirst(); it.Valid(); it.Next())
            {
                var v = it.Value();
                if (v != null && IndexOf(v, tag, 0) >= 0) return (it.Key(), v);
            }
            return (null, null);
        }

        // ------------------------------------------------------------------
        // BSON primitives (read + single in-place int32 write)
        // ------------------------------------------------------------------
        const byte BT_DOUBLE=0x01, BT_STRING=0x02, BT_SUBDOC=0x03, BT_ARRAY=0x04,
                   BT_BINARY=0x05, BT_OID=0x07, BT_BOOL=0x08, BT_DATETIME=0x09,
                   BT_NULL=0x0A, BT_INT32=0x10, BT_TIMESTAMP=0x11, BT_INT64=0x12;

        static uint U32(byte[] b, int p) => (uint)(b[p] | b[p+1]<<8 | b[p+2]<<16 | b[p+3]<<24);
        static int I32(byte[] b, int p) => b[p] | b[p+1]<<8 | b[p+2]<<16 | b[p+3]<<24;
        static void WriteI32(byte[] b, int p, int v)
        { b[p]=(byte)v; b[p+1]=(byte)(v>>8); b[p+2]=(byte)(v>>16); b[p+3]=(byte)(v>>24); }

        static int ValueEnd(byte[] b, int p, byte t) => t switch
        {
            BT_DOUBLE => p + 8,
            BT_STRING => p + 4 + (int)U32(b, p),
            BT_SUBDOC or BT_ARRAY => p + (int)U32(b, p),
            BT_BINARY => p + 4 + 1 + (int)U32(b, p),
            BT_OID => p + 12,
            BT_BOOL => p + 1,
            BT_DATETIME => p + 8,
            BT_NULL => p,
            BT_INT32 => p + 4,
            BT_TIMESTAMP => p + 8,
            BT_INT64 => p + 8,
            _ => throw new InvalidDataException($"unsupported bson type 0x{t:x2} at {p}")
        };

        static IEnumerable<(byte t, string name, int vpos, int vend)> Elements(byte[] b, int docStart)
        {
            int docEnd = docStart + (int)U32(b, docStart);
            int pos = docStart + 4;
            while (pos < docEnd)
            {
                byte t = b[pos];
                if (t == 0) yield break;
                int nameStart = pos + 1;
                int nameEnd = Array.IndexOf(b, (byte)0, nameStart);
                int vpos = nameEnd + 1;
                int vend = ValueEnd(b, vpos, t);
                yield return (t, Encoding.UTF8.GetString(b, nameStart, nameEnd - nameStart), vpos, vend);
                pos = vend;
            }
        }

        static (byte t, int vpos, int vend)? Field(byte[] b, int docStart, string name)
        {
            foreach (var (t, n, vp, ve) in Elements(b, docStart))
                if (n == name) return (t, vp, ve);
            return null;
        }

        static string GetPlayerName(byte[] v)
        {
            var key = Encoding.ASCII.GetBytes("PlayerName\0");
            int p = IndexOf(v, key, 0); if (p < 0) return null;
            int start = p + key.Length; if (v.Length < start + 4) return null;
            int n = (int)U32(v, start); if (n <= 0 || v.Length < start + 4 + n) return null;
            return Encoding.UTF8.GetString(v, start + 4, n).TrimEnd('\0');
        }

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

    // Read-only progression view of a character (level + free / spent pools).
    public sealed class ProgressionView
    {
        public int RewardLevel;
        public int CharacterLevel;
        public int TalentFree;
        public int StatFree;
        public int TalentSpent;
        public int StatSpent;
    }

    public sealed class SaveProgressionCharacter
    {
        public string DbFolder;
        public string CharacterId;
        public string PlayerName;
        public int RewardLevel;
        public int CharacterLevel;
        public int FreeTalent;
        public int FreeStat;
        public int SpentTalent;
        public int SpentStat;
        public int EarnedTalent;
        public int EarnedStat;
    }

    public sealed class ProgressionPatchResult
    {
        public bool Patched;
        public bool AlreadyMatches;
        public string PlayerName;
        public int OldTalent, NewTalent;
        public int OldStat, NewStat;
        public string BackupPath;
        public bool CheckpointZipRebuilt;
    }
}
