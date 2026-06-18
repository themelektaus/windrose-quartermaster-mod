using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using RocksDbSharp;

namespace Windrose.Quartermaster.Core
{
    // Retro-fits equipment and inventory slots onto an EXISTING Windrose
    // character so it works with the slot-count pak (which alone only affects
    // newly-created characters). Ported from DeveloperBlue's Python patcher.
    //
    // The save is a RocksDB database; each character lives under the R5BLPlayer
    // column family as a BSON document. Each inventory module has two parallel
    // views the game cross-checks on load:
    //   ModuleParams.Slots  - blueprint, one entry per slot TYPE with CountSlots
    //   Slots               - live array, one entry per physical slot (SlotId,
    //                         SlotParams, ItemsStack)
    // Editing only the blueprint is reverted on next save (the game sees fewer
    // live slots than the blueprint claims), so we grow the live array too:
    // clone an EMPTY slot template, renumber element indices + SlotIds, and fix
    // every enclosing sub-document's int32 size prefix. The game also restores the
    // live DB from a checkpoint ZIP on every load, so we rebuild that afterwards
    // (CheckpointZipBuilder). Steam Cloud Sync must be off or it overwrites both.
    //
    // Modules patched:
    //   Jewelry (Ring / Necklace / Backpack equipment slots)
    //   Default (base player inventory grid, vanilla 16)
    public sealed class InventorySaveSlotsPatcher
    {
        public const int MinSlots = 1;
        public const int MaxSlots = 10;
        public const int MinDefaultSlots = 16;
        public const int MaxDefaultSlots = 256;
        const string PlayerCf = "R5BLPlayer";
        const string JewelryTag = "Inventory.Module.Jewelry";
        const string DefaultTag = "Inventory.Module.Default";
        const string RingPath = "/R5BusinessRules/Inventory/SlotsParams/DA_BL_Slot_Equipment_Ring.DA_BL_Slot_Equipment_Ring";
        const string NeckPath = "/R5BusinessRules/Inventory/SlotsParams/DA_BL_Slot_Equipment_Necklace.DA_BL_Slot_Equipment_Necklace";
        const string BackPath = "/R5BusinessRules/Inventory/SlotsParams/DA_BL_Slot_Equipment_Backpack.DA_BL_Slot_Equipment_Backpack";
        const string DefaultSlotPath = "/R5BusinessRules/Inventory/SlotsParams/DA_BL_Slot_Default.DA_BL_Slot_Default";

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

        // Canonical character DB folders across every Steam-ID profile. Shared by
        // the jewelry and ship save patchers so the backup-dir exclusion (the
        // "3 chars, 8 rows" fix) lives in exactly one place.
        //
        // Only the canonical Steam-ID profile dir (a pure numeric id) is scanned.
        // The game also keeps sibling backup dirs next to it (<steamid>_Backups,
        // <steamid>_Backups_Editor, <steamid>_progfix_backup_<timestamp>, ...)
        // each carrying its own RocksDB_v2 copy of the same characters -
        // enumerating those would surface every character several times over. A
        // real Steam ID is all digits, so the suffixed backups are excluded.
        public static List<string> DiscoverCharacterDbFolders()
        {
            var folders = new List<string>();
            var profilesRoot = SaveProfilesRoot();
            if (profilesRoot == null) return folders;

            foreach (var steamDir in Directory.GetDirectories(profilesRoot).OrderBy(d => d, StringComparer.Ordinal))
            {
                var steamName = Path.GetFileName(steamDir);
                if (steamName.Length == 0 || !steamName.All(char.IsDigit)) continue;
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
                folders.AddRange(byId.Values);
            }
            return folders;
        }

        public List<SaveCharacter> DiscoverCharacters()
        {
            var result = new List<SaveCharacter>();
            foreach (var folder in DiscoverCharacterDbFolders())
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

        // ------------------------------------------------------------------
        // Diagnostic: an annotated dump of the SaveProfiles tree that mirrors
        // the exact discovery filters above, so a bug report can show WHY a
        // character was (or was not) listed. Only folder names + flags are
        // emitted - never save contents - so it is safe to ship in a report.
        // Never throws: any failure is folded into the returned text.
        // ------------------------------------------------------------------
        public static string DiagnoseSaveProfiles()
        {
            var sb = new StringBuilder();
            try
            {
                var local = Environment.GetEnvironmentVariable("LOCALAPPDATA");
                sb.AppendLine("=== Windrose SaveProfiles diagnostic ===");
                sb.AppendLine("LOCALAPPDATA = " + (string.IsNullOrEmpty(local) ? "(NOT SET!)" : local));

                var root = string.IsNullOrEmpty(local)
                    ? null : Path.Combine(local, "R5", "Saved", "SaveProfiles");
                bool rootExists = root != null && Directory.Exists(root);
                sb.AppendLine("SaveProfiles root = " + (root ?? "(cannot resolve)")
                    + "  [" + (rootExists ? "EXISTS" : "MISSING") + "]");
                sb.AppendLine();
                sb.AppendLine("Scan rule: only profile folders whose name is ALL DIGITS (a real");
                sb.AppendLine("Steam ID) are read. Non-numeric siblings (backups, or non-Steam");
                sb.AppendLine("launchers such as Epic / Game Pass / Microsoft Store) are skipped.");
                sb.AppendLine("A character is only listed if its DB opens and contains an");
                sb.AppendLine("\"" + JewelryTag + "\" entry.");
                sb.AppendLine();

                if (!rootExists)
                {
                    sb.AppendLine(">> Root does not exist -> the Characters tab shows");
                    sb.AppendLine("   \"No Windrose save profiles found on this machine.\"");
                    sb.AppendLine("   Likely a non-Steam install (Game Pass / Microsoft Store keep");
                    sb.AppendLine("   saves under %LOCALAPPDATA%\\Packages\\...), or the game has");
                    sb.AppendLine("   never been launched on this machine yet.");
                    return sb.ToString();
                }

                int listed = 0, dropped = 0;
                var droppedFolders = new List<string>();

                foreach (var steamDir in Directory.GetDirectories(root)
                    .OrderBy(d => d, StringComparer.Ordinal))
                {
                    var name = Path.GetFileName(steamDir);
                    bool numeric = name.Length > 0 && name.All(char.IsDigit);
                    if (!numeric)
                    {
                        sb.AppendLine("[profile] " + name + "   non-numeric -> SKIPPED "
                            + "(not a Steam ID; backups / non-Steam launchers are never scanned)");
                        continue;
                    }
                    sb.AppendLine("[profile] " + name + "   numeric -> SCANNED");

                    var rocks = Path.Combine(steamDir, "RocksDB_v2");
                    if (!Directory.Exists(rocks))
                    {
                        sb.AppendLine("  RocksDB_v2  [MISSING]  -> nothing to read for this profile");
                        continue;
                    }
                    sb.AppendLine("  RocksDB_v2  [present]");

                    // Replicate newest-version-wins so superseded copies are flagged.
                    var winner = new Dictionary<string, string>(StringComparer.Ordinal);
                    var versionDirs = Directory.GetDirectories(rocks)
                        .OrderBy(d => d, StringComparer.Ordinal).ToList();
                    foreach (var versionDir in versionDirs)
                    {
                        var players0 = Path.Combine(versionDir, "Players");
                        if (!Directory.Exists(players0)) continue;
                        foreach (var charDir in Directory.GetDirectories(players0))
                            if (IsCharacterDbDir(charDir))
                                winner[Path.GetFileName(charDir)] = charDir;
                    }

                    foreach (var versionDir in versionDirs)
                    {
                        sb.AppendLine("    " + Path.GetFileName(versionDir));
                        var players = Path.Combine(versionDir, "Players");
                        if (!Directory.Exists(players))
                        {
                            sb.AppendLine("      Players  [MISSING]");
                            continue;
                        }
                        sb.AppendLine("      Players  [present]");
                        var charDirs = Directory.GetDirectories(players)
                            .OrderBy(d => d, StringComparer.Ordinal).ToList();
                        if (charDirs.Count == 0)
                            sb.AppendLine("        (no character folders)");
                        foreach (var charDir in charDirs)
                        {
                            var cName = Path.GetFileName(charDir);
                            if (!File.Exists(Path.Combine(charDir, "CURRENT")))
                            {
                                sb.AppendLine("        " + cName
                                    + "   CURRENT=no  -> SKIPPED (not a character DB)");
                                continue;
                            }
                            bool isWinner = winner.TryGetValue(cName, out var w)
                                && string.Equals(w, charDir, StringComparison.OrdinalIgnoreCase);
                            if (!isWinner)
                            {
                                sb.AppendLine("        " + cName
                                    + "   CURRENT=yes  (older version, superseded by newest - not listed)");
                                continue;
                            }
                            var probe = ProbeCharacterDb(charDir);
                            if (probe.Error != null)
                            {
                                sb.AppendLine("        " + cName + "   CURRENT=yes  jewelry=?  -> DROPPED"
                                    + " (DB read error: " + probe.Error
                                    + ")  [is the game still running? it locks the save]");
                                dropped++; droppedFolders.Add(charDir + " (read error)");
                            }
                            else if (!probe.HasJewelry)
                            {
                                sb.AppendLine("        " + cName + "   CURRENT=yes  jewelry=NO  -> DROPPED"
                                    + " (no Jewelry module in save)");
                                dropped++; droppedFolders.Add(charDir + " (no Jewelry module)");
                            }
                            else
                            {
                                sb.AppendLine("        " + cName + "   CURRENT=yes  jewelry=YES  name=\""
                                    + (probe.PlayerName ?? "?") + "\"  -> LISTED");
                                listed++;
                            }
                        }
                    }
                }

                sb.AppendLine();
                sb.AppendLine("Summary: the Characters tab would list " + listed + " character(s).");
                if (dropped > 0)
                {
                    sb.AppendLine("Dropped " + dropped
                        + " character DB folder(s) present on disk but filtered out:");
                    foreach (var d in droppedFolders) sb.AppendLine("  - " + d);
                }
                if (listed == 0)
                    sb.AppendLine("=> UI shows \"No characters found.\""
                        + " (root exists but nothing survived the scan).");
            }
            catch (Exception e)
            {
                sb.AppendLine();
                sb.AppendLine("!! diagnostic aborted: " + e);
            }
            return sb.ToString();
        }

        // Read-only probe of a single character DB: does it open, does it carry a
        // Jewelry character, and what is the player name. Never throws.
        static DbProbeResult ProbeCharacterDb(string dbFolder)
        {
            try
            {
                var (dbOpts, cfs) = OpenOptions(dbFolder);
                using var db = RocksDb.OpenReadOnly(dbOpts, dbFolder, cfs, false);
                var cf = db.GetColumnFamily(PlayerCf);
                var (_, value) = FindJewelryCharacter(db, cf);
                if (value == null) return new DbProbeResult(false, null, null);
                return new DbProbeResult(true, GetPlayerName(value), null);
            }
            catch (Exception e)
            {
                return new DbProbeResult(false, null, e.Message);
            }
        }

        readonly struct DbProbeResult
        {
            public readonly bool HasJewelry;
            public readonly string PlayerName;
            public readonly string Error;
            public DbProbeResult(bool hasJewelry, string playerName, string error)
            {
                HasJewelry = hasJewelry; PlayerName = playerName; Error = error;
            }
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
            var ch = new SaveCharacter
            {
                DbFolder = dbFolder,
                CharacterId = Path.GetFileName(dbFolder),
                PlayerName = GetPlayerName(value) ?? Path.GetFileName(dbFolder),
                RingSlots = info.LiveSlots.Count(s => s.Kind == "ring"),
                NecklaceSlots = info.LiveSlots.Count(s => s.Kind == "neck"),
                BackpackSlots = info.LiveSlots.Count(s => s.Kind == "back"),
                BlueprintRing = I32(value, info.BpRingPos),
                BlueprintNeck = I32(value, info.BpNeckPos),
                BlueprintBack = info.BpBackPos >= 0 ? I32(value, info.BpBackPos) : 1,
            };
            var defInfo = LocateDefault(value);
            if (defInfo != null)
            {
                ch.DefaultSlots = defInfo.LiveSlots.Count;
                ch.BlueprintDefault = I32(value, defInfo.BpDefaultPos);
            }
            return ch;
        }

        // ------------------------------------------------------------------
        // Patch (mutating): grow/shrink live array + blueprint, backup, zip.
        // ------------------------------------------------------------------

        public SaveSlotsPatchResult PatchCharacter(
            string dbFolder, int newRing, int newNeck,
            int? newBack = null, int? newDefault = null,
            bool forceDeleteEquipped = false)
        {
            if (!IsCharacterDbDir(dbFolder))
                throw new DirectoryNotFoundException(
                    "Not a Windrose character save folder (no CURRENT): " + dbFolder);
            int effBack = newBack ?? 1;
            int effDefault = newDefault ?? 0; // 0 = no-op for Default module
            ValidateSlots(newRing, newNeck, effBack);

            // Validate the folder lives under the real save profiles root - never
            // let a caller point this write at an arbitrary directory.
            var profilesRoot = SaveProfilesRoot();
            if (profilesRoot == null || !Path.GetFullPath(dbFolder)
                    .StartsWith(Path.GetFullPath(profilesRoot), StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException(
                    "Refusing to patch a folder outside the Windrose save profiles root.");

            var result = new SaveSlotsPatchResult
            {
                NewRing = newRing, NewNeck = newNeck,
                NewBack = effBack, NewDefault = effDefault
            };

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
                result.OldBack = info.LiveSlots.Count(s => s.Kind == "back");
                result.OldBytes = value.Length;

                // Default module: read current state (may be null if not found).
                DefaultInfo defInfo = null;
                if (effDefault > 0) defInfo = LocateDefault(value);
                result.OldDefault = defInfo != null ? defInfo.LiveSlots.Count : 0;

                var blocking = FindBlockingItems(value, info, newRing, newNeck, effBack);
                if (effDefault > 0 && defInfo != null)
                    blocking.AddRange(FindDefaultBlockingItems(value, defInfo, effDefault));
                if (blocking.Count > 0 && !forceDeleteEquipped)
                {
                    result.BlockingItems = blocking;
                    return result;
                }

                // Check if already matching.
                bool jewelryMatch = result.OldRing == newRing && result.OldNeck == newNeck
                    && result.OldBack == effBack
                    && info.BpRingValue == newRing && info.BpNeckValue == newNeck
                    && info.BpBackValue == effBack;
                bool defaultMatch = effDefault <= 0
                    || (defInfo != null && defInfo.BpDefaultValue == effDefault);
                if (jewelryMatch && defaultMatch)
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

                // Patch Jewelry module (ring/neck/back).
                if (!jewelryMatch)
                {
                    value = PatchPlayerValue(value, info, newRing, newNeck, effBack);
                    LogLine("  jewelry module patched");
                }

                // Patch Default module (player inventory).
                if (!defaultMatch && defInfo != null)
                {
                    // Re-locate after Jewelry splice may have shifted offsets.
                    defInfo = LocateDefault(value);
                    if (defInfo != null)
                    {
                        int bpBonus = Math.Max(0, defInfo.LiveSlots.Count - defInfo.BpDefaultValue);
                        value = PatchDefaultModule(value, defInfo, effDefault);
                        LogLine("  default module patched (" + effDefault + " base slots"
                            + (bpBonus > 0 ? ", +" + bpBonus + " from backpack preserved" : "") + ")");
                    }
                }

                result.NewBytes = value.Length;
                LogLine("  patched value " + result.OldBytes + " -> " + value.Length
                        + " bytes (delta " + (value.Length - result.OldBytes).ToString("+0;-0;0") + ")");

                db.Put(key, value, cf);
                try { db.CompactRange((byte[])null, (byte[])null, cf); } catch { }
            }

            // Rebuild the checkpoint ZIP the game restores from on load.
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

        static void ValidateSlots(int ring, int neck, int back)
        {
            if (ring < MinSlots || ring > MaxSlots)
                throw new ArgumentOutOfRangeException(nameof(ring), ring,
                    "Ring slots must be between " + MinSlots + " and " + MaxSlots);
            if (neck < MinSlots || neck > MaxSlots)
                throw new ArgumentOutOfRangeException(nameof(neck), neck,
                    "Necklace slots must be between " + MinSlots + " and " + MaxSlots);
            if (back < MinSlots || back > MaxSlots)
                throw new ArgumentOutOfRangeException(nameof(back), back,
                    "Backpack slots must be between " + MinSlots + " and " + MaxSlots);
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
            public int JewelryDocStart, ModuleParamsStart, BpRingPos, BpNeckPos, BpBackPos, LiveArrayStart, LiveArrayEnd;
            public List<Slot> LiveSlots = new();
            public int BpRingValue, BpNeckValue, BpBackValue;
        }

        sealed class DefaultInfo
        {
            public List<int> AncestorChain = new();
            public int DefaultDocStart, ModuleParamsStart, BpDefaultPos, LiveArrayStart, LiveArrayEnd;
            public List<Slot> LiveSlots = new();
            public int BpDefaultValue;
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
            info.BpRingPos = info.BpNeckPos = info.BpBackPos = -1;
            foreach (var (t, _, vpos, _) in Elements(buf, bp.vpos))
            {
                if (t != BT_SUBDOC) continue;
                var sp = Field(buf, vpos, "SlotParams");
                var cs = Field(buf, vpos, "CountSlots");
                if (sp is not { t: BT_STRING } || cs is not { t: BT_INT32 }) continue;
                var p = ReadStr(buf, sp.Value.vpos);
                if (p == RingPath) info.BpRingPos = cs.Value.vpos;
                else if (p == NeckPath) info.BpNeckPos = cs.Value.vpos;
                else if (p == BackPath) info.BpBackPos = cs.Value.vpos;
            }
            if (info.BpRingPos < 0 || info.BpNeckPos < 0)
                throw new InvalidDataException("Blueprint Ring/Necklace entries not found.");
            info.BpRingValue = I32(buf, info.BpRingPos);
            info.BpNeckValue = I32(buf, info.BpNeckPos);
            info.BpBackValue = info.BpBackPos >= 0 ? I32(buf, info.BpBackPos) : 1;

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

        static List<string> FindBlockingItems(byte[] buf, JewelryInfo info, int newRing, int newNeck, int newBack)
        {
            var blocking = new List<string>();
            var rings = info.LiveSlots.Where(s => s.Kind == "ring").ToList();
            var necks = info.LiveSlots.Where(s => s.Kind == "neck").ToList();
            var backs = info.LiveSlots.Where(s => s.Kind == "back").ToList();
            if (newRing < rings.Count)
                foreach (var s in rings.Skip(newRing))
                    if (s.HasItem) blocking.Add("Ring slot " + Encoding.ASCII.GetString(s.IndexName) + " holds an equipped item");
            if (newNeck < necks.Count)
                foreach (var s in necks.Skip(newNeck))
                    if (s.HasItem) blocking.Add("Necklace slot " + Encoding.ASCII.GetString(s.IndexName) + " holds an equipped item");
            if (newBack < backs.Count)
                foreach (var s in backs.Skip(newBack))
                    if (s.HasItem) blocking.Add("Backpack slot " + Encoding.ASCII.GetString(s.IndexName) + " holds an equipped item");
            return blocking;
        }

        static List<string> FindDefaultBlockingItems(byte[] buf, DefaultInfo info, int newDefault)
        {
            var blocking = new List<string>();
            int backpackBonus = Math.Max(0, info.LiveSlots.Count - info.BpDefaultValue);
            int effectiveLive = newDefault + backpackBonus;
            if (effectiveLive < info.LiveSlots.Count)
                foreach (var s in info.LiveSlots.Skip(effectiveLive))
                    if (s.HasItem) blocking.Add("Inventory slot " + Encoding.ASCII.GetString(s.IndexName) + " holds an item");
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

        static byte[] BuildLiveArray(byte[] buf, JewelryInfo info, int newRing, int newNeck, int newBack)
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
            var backT = backs.Count > 0 ? EmptyTemplate(buf, backs) : null;
            var sources = new List<byte[]>();
            sources.AddRange(rings.Take(newRing).Select(s => buf[s.ElemStart..s.ElemEnd]));
            for (int i = 0; i < Math.Max(0, newRing - rings.Count); i++) sources.Add(ringT);
            sources.AddRange(necks.Take(newNeck).Select(s => buf[s.ElemStart..s.ElemEnd]));
            for (int i = 0; i < Math.Max(0, newNeck - necks.Count); i++) sources.Add(neckT);
            sources.AddRange(backs.Take(newBack).Select(s => buf[s.ElemStart..s.ElemEnd]));
            if (backT != null)
                for (int i = 0; i < Math.Max(0, newBack - backs.Count); i++) sources.Add(backT);
            sources.AddRange(others.Select(s => buf[s.ElemStart..s.ElemEnd]));

            var body = new List<byte>();
            for (int i = 0; i < sources.Count; i++) body.AddRange(Retag(sources[i], i.ToString(), i));
            body.Add(0);
            int arrSize = 4 + body.Count;
            var res = new List<byte>(LE(arrSize)); res.AddRange(body); return res.ToArray();
        }

        static byte[] BuildDefaultLiveArray(byte[] buf, DefaultInfo info, int newCount)
        {
            if (info.LiveSlots.Count == 0)
                throw new InvalidDataException("Character has no Default inventory slots to use as a template.");
            var tmpl = EmptyTemplate(buf, info.LiveSlots);
            var sources = new List<byte[]>();
            sources.AddRange(info.LiveSlots.Take(newCount).Select(s => buf[s.ElemStart..s.ElemEnd]));
            for (int i = 0; i < Math.Max(0, newCount - info.LiveSlots.Count); i++) sources.Add(tmpl);

            var body = new List<byte>();
            for (int i = 0; i < sources.Count; i++) body.AddRange(Retag(sources[i], i.ToString(), i));
            body.Add(0);
            int arrSize = 4 + body.Count;
            var res = new List<byte>(LE(arrSize)); res.AddRange(body); return res.ToArray();
        }

        static byte[] PatchPlayerValue(byte[] value, JewelryInfo info, int newRing, int newNeck, int newBack)
        {
            var newArray = BuildLiveArray(value, info, newRing, newNeck, newBack);
            var outp = (byte[])value.Clone();
            LE(newRing).CopyTo(outp, info.BpRingPos);
            LE(newNeck).CopyTo(outp, info.BpNeckPos);
            if (info.BpBackPos >= 0) LE(newBack).CopyTo(outp, info.BpBackPos);
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

        // Locate the Default inventory module (base player inventory grid).
        // Returns null if the module is not present in this save.
        static DefaultInfo LocateDefault(byte[] buf)
        {
            var info = new DefaultInfo();
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
                                if (tn is { t: BT_STRING } && ReadStr(buf, tn.Value.vpos) == DefaultTag)
                                {
                                    info.DefaultDocStart = vpos;
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
            if (!found) return null;

            var bp = Field(buf, info.ModuleParamsStart, "Slots");
            if (bp is not { t: BT_ARRAY }) return null;
            info.BpDefaultPos = -1;
            foreach (var (t, _, vpos, _) in Elements(buf, bp.Value.vpos))
            {
                if (t != BT_SUBDOC) continue;
                var cs = Field(buf, vpos, "CountSlots");
                if (cs is { t: BT_INT32 }) { info.BpDefaultPos = cs.Value.vpos; break; }
            }
            if (info.BpDefaultPos < 0) return null;
            info.BpDefaultValue = I32(buf, info.BpDefaultPos);

            var live = Field(buf, info.DefaultDocStart, "Slots");
            if (live is not { t: BT_ARRAY }) return null;
            info.LiveArrayStart = live.Value.vpos; info.LiveArrayEnd = live.Value.vend;
            foreach (var (t, name, vpos, vend) in Elements(buf, live.Value.vpos))
            {
                if (t != BT_SUBDOC) continue;
                int elemStart = vpos - name.Length - 2;
                info.LiveSlots.Add(new Slot { Kind = "default", HasItem = SlotHasItem(buf, vpos), ElemStart = elemStart, ElemEnd = vend, IndexName = name });
            }
            return info;
        }

        static byte[] PatchDefaultModule(byte[] value, DefaultInfo info, int newDefault)
        {
            // Backpack modifiers add extra slots to the live array at runtime.
            // Preserve those extra slots so equipped backpacks keep working.
            int backpackBonus = Math.Max(0, info.LiveSlots.Count - info.BpDefaultValue);
            int newLiveCount = newDefault + backpackBonus;
            var newArray = BuildDefaultLiveArray(value, info, newLiveCount);
            var outp = (byte[])value.Clone();
            LE(newDefault).CopyTo(outp, info.BpDefaultPos);
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
                    $"Internal error: root size {U32(outp,0)} != buffer length {outp.Length} after default-module splice.");
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
        public int BackpackSlots;
        public int DefaultSlots;
        public int BlueprintRing;
        public int BlueprintNeck;
        public int BlueprintBack;
        public int BlueprintDefault;
    }

    public sealed class SaveSlotsPatchResult
    {
        public bool Patched;
        public bool AlreadyMatches;
        public string PlayerName;
        public int OldRing, OldNeck, OldBack, OldDefault;
        public int NewRing, NewNeck, NewBack, NewDefault;
        public int OldBytes, NewBytes;
        public string BackupPath;
        public bool CheckpointZipRebuilt;
        public List<string> BlockingItems;
    }
}
