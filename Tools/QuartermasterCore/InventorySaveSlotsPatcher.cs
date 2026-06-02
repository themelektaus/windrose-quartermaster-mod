using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using RocksDbSharp;

namespace Windrose.Quartermaster.Core
{
    // Retro-fits Ring / Necklace equipment slots onto an EXISTING Windrose
    // character so it works with the slot-count pak (which alone only affects
    // newly-created characters). Ported from DeveloperBlue's Python patcher.
    //
    // The save is a RocksDB database; each character lives under the R5BLPlayer
    // column family as a BSON document. The Jewelry module has two parallel views
    // the game cross-checks on load:
    //   ModuleParams.Slots  - blueprint, one entry per slot TYPE with CountSlots
    //   Slots               - live array, one entry per physical slot (SlotId,
    //                         SlotParams, ItemsStack)
    // Editing only the blueprint is reverted on next save (the game sees fewer
    // live slots than the blueprint claims), so we grow the live array too:
    // clone an EMPTY slot template, renumber element indices + SlotIds, and fix
    // every enclosing sub-document's int32 size prefix. The game also restores the
    // live DB from a checkpoint ZIP on every load, so we rebuild that afterwards
    // (CheckpointZipBuilder). Steam Cloud Sync must be off or it overwrites both.
    public sealed class InventorySaveSlotsPatcher
    {
        public const int MinSlots = 1;
        public const int MaxSlots = 10;
        const string PlayerCf = "R5BLPlayer";
        const string JewelryTag = "Inventory.Module.Jewelry";
        const string RingPath = "/R5BusinessRules/Inventory/SlotsParams/DA_BL_Slot_Equipment_Ring.DA_BL_Slot_Equipment_Ring";
        const string NeckPath = "/R5BusinessRules/Inventory/SlotsParams/DA_BL_Slot_Equipment_Necklace.DA_BL_Slot_Equipment_Necklace";
        const string BackPath = "/R5BusinessRules/Inventory/SlotsParams/DA_BL_Slot_Equipment_Backpack.DA_BL_Slot_Equipment_Backpack";

        public Action<string> Log;
        void LogLine(string m) { if (Log != null) Log(m); }

        // ------------------------------------------------------------------
        // Discovery: %LOCALAPPDATA%\R5\Saved\SaveProfiles\<steamid>\RocksDB_v2\<version>\Players\<id>
        // ------------------------------------------------------------------

        public static string SaveProfilesRoot()
        {
            var local = Environment.GetEnvironmentVariable("LOCALAPPDATA");
            if (string.IsNullOrEmpty(local)) return null;
            var root = Path.Combine(local, "R5", "Saved", "SaveProfiles");
            return Directory.Exists(root) ? root : null;
        }

        static bool IsCharacterDbDir(string path)
            => Directory.Exists(path) && File.Exists(Path.Combine(path, "CURRENT"));

        public List<SaveCharacter> DiscoverCharacters()
        {
            var result = new List<SaveCharacter>();
            var profilesRoot = SaveProfilesRoot();
            if (profilesRoot == null) return result;

            foreach (var steamDir in Directory.GetDirectories(profilesRoot).OrderBy(d => d, StringComparer.Ordinal))
            {
                if (Path.GetFileName(steamDir).StartsWith(".", StringComparison.Ordinal)) continue;
                var rocks = Path.Combine(steamDir, "RocksDB_v2");
                if (!Directory.Exists(rocks)) continue;

                // Newest version wins when the same character id appears twice.
                var byId = new Dictionary<string, string>(StringComparer.Ordinal);
                foreach (var versionDir in Directory.GetDirectories(rocks).OrderBy(d => d, StringComparer.Ordinal))
                {
                    var players = Path.Combine(versionDir, "Players");
                    if (!Directory.Exists(players)) continue;
                    foreach (var charDir in Directory.GetDirectories(players))
                        if (IsCharacterDbDir(charDir))
                            byId[Path.GetFileName(charDir)] = charDir;
                }

                foreach (var folder in byId.Values)
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
            }
            return result;
        }

        // ------------------------------------------------------------------
        // Read-only peek (non-mutating): name + current ring/necklace counts.
        // ------------------------------------------------------------------

        public SaveCharacter ReadCharacter(string dbFolder)
        {
            if (!IsCharacterDbDir(dbFolder))
                throw new DirectoryNotFoundException(
                    "Not a Windrose character save folder (no CURRENT): " + dbFolder);

            var (dbOpts, cfs) = OpenOptions(dbFolder);
            using var db = RocksDb.OpenReadOnly(dbOpts, dbFolder, cfs, false);
            var cf = db.GetColumnFamily(PlayerCf);
            var (key, value) = FindJewelryCharacter(db, cf);
            if (value == null) return null;

            var info = Locate(value);
            return new SaveCharacter
            {
                DbFolder = dbFolder,
                CharacterId = Path.GetFileName(dbFolder),
                PlayerName = GetPlayerName(value) ?? Path.GetFileName(dbFolder),
                RingSlots = info.LiveSlots.Count(s => s.Kind == "ring"),
                NecklaceSlots = info.LiveSlots.Count(s => s.Kind == "neck"),
                BlueprintRing = I32(value, info.BpRingPos),
                BlueprintNeck = I32(value, info.BpNeckPos),
            };
        }

        // ------------------------------------------------------------------
        // Patch (mutating): grow/shrink live array + blueprint, backup, zip.
        // ------------------------------------------------------------------

        public SaveSlotsPatchResult PatchCharacter(
            string dbFolder, int newRing, int newNeck, bool forceDeleteEquipped = false)
        {
            if (!IsCharacterDbDir(dbFolder))
                throw new DirectoryNotFoundException(
                    "Not a Windrose character save folder (no CURRENT): " + dbFolder);
            ValidateSlots(newRing, newNeck);

            // Validate the folder lives under the real save profiles root - never
            // let a caller point this write at an arbitrary directory.
            var profilesRoot = SaveProfilesRoot();
            if (profilesRoot == null || !Path.GetFullPath(dbFolder)
                    .StartsWith(Path.GetFullPath(profilesRoot), StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException(
                    "Refusing to patch a folder outside the Windrose save profiles root.");

            var result = new SaveSlotsPatchResult { NewRing = newRing, NewNeck = newNeck };

            var (dbOpts, cfs) = OpenOptions(dbFolder);
            byte[] key, value;
            using (var db = RocksDb.Open(dbOpts, dbFolder, cfs))
            {
                var cf = db.GetColumnFamily(PlayerCf);
                (key, value) = FindJewelryCharacter(db, cf);
                if (value == null)
                    throw new InvalidOperationException("No Jewelry character found in this save.");

                var info = Locate(value);
                result.PlayerName = GetPlayerName(value) ?? Path.GetFileName(dbFolder);
                result.OldRing = info.LiveSlots.Count(s => s.Kind == "ring");
                result.OldNeck = info.LiveSlots.Count(s => s.Kind == "neck");
                result.OldBytes = value.Length;

                var blocking = FindBlockingItems(value, info, newRing, newNeck);
                if (blocking.Count > 0 && !forceDeleteEquipped)
                {
                    result.BlockingItems = blocking;
                    return result; // caller must confirm destructive shrink
                }

                if (result.OldRing == newRing && result.OldNeck == newNeck
                    && info.BpRingValue == newRing && info.BpNeckValue == newNeck)
                {
                    result.AlreadyMatches = true;
                    result.NewBytes = value.Length;
                    return result;
                }

                // Pre-patch backup (never overwrite an existing one).
                var bak = Path.Combine(dbFolder, Path.GetFileName(dbFolder) + ".value.pre-patch.bak");
                if (!File.Exists(bak))
                {
                    File.WriteAllBytes(bak, value);
                    result.BackupPath = bak;
                    LogLine("  pre-patch backup: " + bak);
                }

                var newValue = PatchPlayerValue(value, info, newRing, newNeck);
                result.NewBytes = newValue.Length;
                LogLine("  patched value " + value.Length + " -> " + newValue.Length
                        + " bytes (delta " + (newValue.Length - value.Length).ToString("+0;-0;0") + ")");

                db.Put(key, newValue, cf);
                try { db.CompactRange((byte[])null, (byte[])null, cf); } catch { }
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

        static (byte[] key, byte[] value) FindJewelryCharacter(RocksDb db, ColumnFamilyHandle cf)
        {
            var tag = Encoding.ASCII.GetBytes(JewelryTag);
            using var it = db.NewIterator(cf);
            for (it.SeekToFirst(); it.Valid(); it.Next())
            {
                var v = it.Value();
                if (v != null && IndexOf(v, tag, 0) >= 0)
                    return (it.Key(), v);
            }
            return (null, null);
        }

        static void ValidateSlots(int ring, int neck)
        {
            if (ring < MinSlots || ring > MaxSlots)
                throw new ArgumentOutOfRangeException(nameof(ring), ring,
                    "Ring slots must be between " + MinSlots + " and " + MaxSlots);
            if (neck < MinSlots || neck > MaxSlots)
                throw new ArgumentOutOfRangeException(nameof(neck), neck,
                    "Necklace slots must be between " + MinSlots + " and " + MaxSlots);
        }

        // ================= BSON surgery (verified in the spike) =================
        const byte BT_DOUBLE=0x01, BT_STRING=0x02, BT_SUBDOC=0x03, BT_ARRAY=0x04,
                   BT_BINARY=0x05, BT_BOOL=0x08, BT_NULL=0x0A, BT_INT32=0x10, BT_INT64=0x12;

        static uint U32(byte[] b, int p) => (uint)(b[p] | b[p+1]<<8 | b[p+2]<<16 | b[p+3]<<24);
        static int I32(byte[] b, int p) => b[p] | b[p+1]<<8 | b[p+2]<<16 | b[p+3]<<24;
        static byte[] LE(int v) => BitConverter.GetBytes(v);

        static int ValueEnd(byte[] b, int p, byte t) => t switch
        {
            BT_DOUBLE => p + 8,
            BT_STRING => p + 4 + (int)U32(b, p),
            BT_SUBDOC or BT_ARRAY => p + (int)U32(b, p),
            BT_BINARY => p + 4 + 1 + (int)U32(b, p),
            BT_BOOL => p + 1,
            BT_NULL => p,
            BT_INT32 => p + 4,
            BT_INT64 => p + 8,
            _ => throw new InvalidDataException($"unsupported bson type 0x{t:x2} at {p}")
        };

        static IEnumerable<(byte t, byte[] name, int vpos, int vend)> Elements(byte[] b, int docStart)
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
                yield return (t, b[nameStart..nameEnd], vpos, vend);
                pos = vend;
            }
        }

        static (byte t, int vpos, int vend)? Field(byte[] b, int docStart, string name)
        {
            var nb = Encoding.UTF8.GetBytes(name);
            foreach (var (t, n, vp, ve) in Elements(b, docStart))
                if (n.AsSpan().SequenceEqual(nb)) return (t, vp, ve);
            return null;
        }

        static string ReadStr(byte[] b, int vpos)
        { int n = (int)U32(b, vpos); return n <= 0 ? "" : Encoding.UTF8.GetString(b, vpos+4, n-1); }

        static string ClassifyPath(string p) => p == RingPath ? "ring" : p == NeckPath ? "neck" : p == BackPath ? "back" : null;

        sealed class Slot { public string Kind; public bool HasItem; public int ElemStart, ElemEnd; public byte[] IndexName; }
        sealed class JewelryInfo
        {
            public List<int> AncestorChain = new();
            public int JewelryDocStart, ModuleParamsStart, BpRingPos, BpNeckPos, LiveArrayStart, LiveArrayEnd;
            public List<Slot> LiveSlots = new();
            public int BpRingValue, BpNeckValue;
        }

        static bool SlotHasItem(byte[] b, int slotDoc)
        {
            var stack = Field(b, slotDoc, "ItemsStack");
            if (stack is not { t: BT_SUBDOC }) return false;
            var cnt = Field(b, stack.Value.vpos, "Count");
            if (cnt is { t: BT_INT32 } && I32(b, cnt.Value.vpos) != 0) return true;
            var item = Field(b, stack.Value.vpos, "Item");
            if (item is { t: BT_SUBDOC })
            {
                var iid = Field(b, item.Value.vpos, "ItemId");
                if (iid is { t: BT_STRING } && ReadStr(b, iid.Value.vpos).Length > 0) return true;
            }
            return false;
        }

        static JewelryInfo Locate(byte[] buf)
        {
            var info = new JewelryInfo();
            bool found = false;
            void Descend(int docStart, List<int> chain)
            {
                if (found) return;
                foreach (var (t, name, vpos, vend) in Elements(buf, docStart))
                {
                    if (t != BT_SUBDOC && t != BT_ARRAY) continue;
                    if (t == BT_SUBDOC)
                    {
                        var mp = Field(buf, vpos, "ModuleParams");
                        if (mp is { t: BT_SUBDOC })
                        {
                            var mt = Field(buf, mp.Value.vpos, "ModuleTag");
                            if (mt is { t: BT_SUBDOC })
                            {
                                var tn = Field(buf, mt.Value.vpos, "TagName");
                                if (tn is { t: BT_STRING } && ReadStr(buf, tn.Value.vpos) == JewelryTag)
                                {
                                    info.JewelryDocStart = vpos;
                                    info.ModuleParamsStart = mp.Value.vpos;
                                    info.AncestorChain = new List<int>(chain) { docStart, vpos };
                                    found = true; return;
                                }
                            }
                        }
                    }
                    Descend(vpos, new List<int>(chain) { docStart });
                    if (found) return;
                }
            }
            Descend(0, new List<int>());
            if (!found) throw new InvalidDataException("Jewelry module not found in character data.");

            var bp = Field(buf, info.ModuleParamsStart, "Slots")
                ?? throw new InvalidDataException("Blueprint Slots array missing.");
            if (bp.t != BT_ARRAY) throw new InvalidDataException("Blueprint Slots not an array.");
            info.BpRingPos = info.BpNeckPos = -1;
            foreach (var (t, _, vpos, _) in Elements(buf, bp.vpos))
            {
                if (t != BT_SUBDOC) continue;
                var sp = Field(buf, vpos, "SlotParams");
                var cs = Field(buf, vpos, "CountSlots");
                if (sp is not { t: BT_STRING } || cs is not { t: BT_INT32 }) continue;
                var p = ReadStr(buf, sp.Value.vpos);
                if (p == RingPath) info.BpRingPos = cs.Value.vpos;
                else if (p == NeckPath) info.BpNeckPos = cs.Value.vpos;
            }
            if (info.BpRingPos < 0 || info.BpNeckPos < 0)
                throw new InvalidDataException("Blueprint Ring/Necklace entries not found.");
            info.BpRingValue = I32(buf, info.BpRingPos);
            info.BpNeckValue = I32(buf, info.BpNeckPos);

            var live = Field(buf, info.JewelryDocStart, "Slots")
                ?? throw new InvalidDataException("Live Slots array missing.");
            if (live.t != BT_ARRAY) throw new InvalidDataException("Live Slots not an array.");
            info.LiveArrayStart = live.vpos; info.LiveArrayEnd = live.vend;
            foreach (var (t, name, vpos, vend) in Elements(buf, live.vpos))
            {
                if (t != BT_SUBDOC) continue;
                int elemStart = vpos - name.Length - 2;
                var sp = Field(buf, vpos, "SlotParams");
                string kind = sp is { t: BT_STRING } ? ClassifyPath(ReadStr(buf, sp.Value.vpos)) : null;
                info.LiveSlots.Add(new Slot { Kind = kind, HasItem = SlotHasItem(buf, vpos), ElemStart = elemStart, ElemEnd = vend, IndexName = name });
            }
            return info;
        }

        static List<string> FindBlockingItems(byte[] buf, JewelryInfo info, int newRing, int newNeck)
        {
            var blocking = new List<string>();
            var rings = info.LiveSlots.Where(s => s.Kind == "ring").ToList();
            var necks = info.LiveSlots.Where(s => s.Kind == "neck").ToList();
            if (newRing < rings.Count)
                foreach (var s in rings.Skip(newRing))
                    if (s.HasItem) blocking.Add("Ring slot " + Encoding.ASCII.GetString(s.IndexName) + " holds an equipped item");
            if (newNeck < necks.Count)
                foreach (var s in necks.Skip(newNeck))
                    if (s.HasItem) blocking.Add("Necklace slot " + Encoding.ASCII.GetString(s.IndexName) + " holds an equipped item");
            return blocking;
        }

        static byte[] Retag(byte[] tmpl, string newName, int newSlotId)
        {
            if (tmpl[0] != BT_SUBDOC) throw new InvalidDataException("Slot template not a sub-doc.");
            int nameEnd = Array.IndexOf(tmpl, (byte)0, 1);
            int subStart = nameEnd + 1;
            int subSize = (int)U32(tmpl, subStart);
            var sub = tmpl[subStart..(subStart+subSize)];
            var marker = Encoding.ASCII.GetBytes("\x10SlotId\0");
            int sp = IndexOf(sub, marker, 0);
            if (sp < 0) throw new InvalidDataException("SlotId not found in slot template.");
            LE(newSlotId).CopyTo(sub, sp + marker.Length);
            var outp = new List<byte> { BT_SUBDOC };
            outp.AddRange(Encoding.ASCII.GetBytes(newName));
            outp.Add(0);
            outp.AddRange(sub);
            return outp.ToArray();
        }

        static byte[] ZeroedValue(byte[] b, byte t, int vpos) => t switch
        {
            BT_DOUBLE => new byte[8],
            BT_STRING => LE(1).Concat(new byte[]{0}).ToArray(),
            BT_SUBDOC or BT_ARRAY => ZeroedDoc(b, vpos),
            BT_BINARY => LE(0).Concat(new byte[]{0}).ToArray(),
            BT_BOOL => new byte[1],
            BT_NULL => Array.Empty<byte>(),
            BT_INT32 => new byte[4],
            BT_INT64 => new byte[8],
            _ => throw new InvalidDataException($"zero unsupported 0x{t:x2}")
        };

        static byte[] ZeroedDoc(byte[] b, int docStart)
        {
            var body = new List<byte>();
            foreach (var (t, name, vpos, _) in Elements(b, docStart))
            {
                body.Add(t); body.AddRange(name); body.Add(0);
                body.AddRange(ZeroedValue(b, t, vpos));
            }
            body.Add(0);
            int total = 4 + body.Count;
            var res = new List<byte>(LE(total)); res.AddRange(body); return res.ToArray();
        }

        static byte[] EmptiedSlot(byte[] buf, Slot s)
        {
            var elem = buf[s.ElemStart..s.ElemEnd];
            int nameEnd = Array.IndexOf(elem, (byte)0, 1);
            int subStart = nameEnd + 1;
            var body = new List<byte>();
            foreach (var (t, name, vpos, vend) in Elements(elem, subStart))
            {
                body.Add(t); body.AddRange(name); body.Add(0);
                if (Encoding.ASCII.GetString(name) == "ItemsStack" && t == BT_SUBDOC)
                    body.AddRange(ZeroedDoc(elem, vpos));
                else body.AddRange(elem[vpos..vend]);
            }
            body.Add(0);
            int newSub = 4 + body.Count;
            var newSubdoc = new List<byte>(LE(newSub)); newSubdoc.AddRange(body);
            var res = new List<byte>(elem[..subStart]); res.AddRange(newSubdoc); return res.ToArray();
        }

        static byte[] EmptyTemplate(byte[] buf, List<Slot> kind)
        {
            foreach (var s in kind) if (!s.HasItem) return buf[s.ElemStart..s.ElemEnd];
            return EmptiedSlot(buf, kind[0]);
        }

        static byte[] BuildLiveArray(byte[] buf, JewelryInfo info, int newRing, int newNeck)
        {
            var slots = info.LiveSlots;
            var rings = slots.Where(s => s.Kind == "ring").ToList();
            var necks = slots.Where(s => s.Kind == "neck").ToList();
            var backs = slots.Where(s => s.Kind == "back").ToList();
            var others = slots.Where(s => s.Kind is not ("ring" or "neck" or "back")).ToList();
            if (rings.Count == 0 || necks.Count == 0)
                throw new InvalidDataException("Character has no Ring/Necklace live slot to use as a template.");

            var ringT = EmptyTemplate(buf, rings);
            var neckT = EmptyTemplate(buf, necks);
            var sources = new List<byte[]>();
            sources.AddRange(rings.Take(newRing).Select(s => buf[s.ElemStart..s.ElemEnd]));
            for (int i = 0; i < Math.Max(0, newRing - rings.Count); i++) sources.Add(ringT);
            sources.AddRange(necks.Take(newNeck).Select(s => buf[s.ElemStart..s.ElemEnd]));
            for (int i = 0; i < Math.Max(0, newNeck - necks.Count); i++) sources.Add(neckT);
            sources.AddRange(backs.Select(s => buf[s.ElemStart..s.ElemEnd]));
            sources.AddRange(others.Select(s => buf[s.ElemStart..s.ElemEnd]));

            var body = new List<byte>();
            for (int i = 0; i < sources.Count; i++) body.AddRange(Retag(sources[i], i.ToString(), i));
            body.Add(0);
            int arrSize = 4 + body.Count;
            var res = new List<byte>(LE(arrSize)); res.AddRange(body); return res.ToArray();
        }

        static byte[] PatchPlayerValue(byte[] value, JewelryInfo info, int newRing, int newNeck)
        {
            var newArray = BuildLiveArray(value, info, newRing, newNeck);
            var outp = (byte[])value.Clone();
            LE(newRing).CopyTo(outp, info.BpRingPos);
            LE(newNeck).CopyTo(outp, info.BpNeckPos);
            int delta = newArray.Length - (info.LiveArrayEnd - info.LiveArrayStart);
            var spliced = new List<byte>(outp[..info.LiveArrayStart]);
            spliced.AddRange(newArray);
            spliced.AddRange(outp[info.LiveArrayEnd..]);
            outp = spliced.ToArray();
            if (delta != 0)
                foreach (var ds in info.AncestorChain)
                { int sz = (int)U32(outp, ds); LE(sz + delta).CopyTo(outp, ds); }
            if ((int)U32(outp, 0) != outp.Length)
                throw new InvalidDataException(
                    $"Internal error: root size {U32(outp,0)} != buffer length {outp.Length} after splice.");
            return outp;
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

    public sealed class SaveCharacter
    {
        public string DbFolder;
        public string CharacterId;
        public string PlayerName;
        public int RingSlots;
        public int NecklaceSlots;
        public int BlueprintRing;
        public int BlueprintNeck;
    }

    public sealed class SaveSlotsPatchResult
    {
        public bool Patched;
        public bool AlreadyMatches;
        public string PlayerName;
        public int OldRing, OldNeck, NewRing, NewNeck;
        public int OldBytes, NewBytes;
        public string BackupPath;
        public bool CheckpointZipRebuilt;
        public List<string> BlockingItems;
    }
}
