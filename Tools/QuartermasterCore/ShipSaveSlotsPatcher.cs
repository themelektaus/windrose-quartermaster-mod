using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;
using RocksDbSharp;

namespace Windrose.Quartermaster.Core
{
    // Retro-fits bigger cargo + more Combat Orders slots onto EXISTING ships (the
    // "Expanded Naval Tactics" pak only affects ships created AFTER it ships).
    // Writes directly to the RocksDB save, parallel to InventorySaveSlotsPatcher
    // (player jewelry). Ships live in the R5BLShip column family, one BSON
    // document per ship; a character can own several.
    //
    // Each ship doc carries its source inventory template in
    //   Inventory.InventoryParams = ".../DA_ShipInventory_<Type>.<...>"
    // which lets us look up the VANILLA cargo base for that exact ship type and
    // compute target = round(vanillaBase * multiplier). That makes the patch
    // idempotent (target derives from vanilla, never from the current value) and
    // identical to what the pak writes for new ships.
    //
    // The surgery mirrors the jewelry patcher: a module has a blueprint
    // (ModuleParams.Slots, CountSlots per slot type) AND a live Slots[] array
    // (one element per physical slot). The game reverts a blueprint-only edit, so
    // we grow the live array too, then rebuild the checkpoint ZIP the game
    // restores from on load. Steam Cloud Sync must be off.
    public sealed class ShipSaveSlotsPatcher
    {
        const string ShipCf = "R5BLShip";
        const string DefaultModuleTag = "Inventory.Module.Default";
        const string EquipmentModuleTag = "Inventory.Module.Equipment";
        const string ChestMarker = "DA_BL_Slot_Chest";
        const string CombatOrdersMarker = "DA_BL_Slot_ShipEquipment_CombatOrders";

        readonly string _vanillaShipDir;
        // Cache of vanilla cargo base per source-DA basename.
        readonly Dictionary<string, int> _vanillaBaseCache = new(StringComparer.OrdinalIgnoreCase);

        public Action<string> Log;
        void LogLine(string m) { if (Log != null) Log(m); }

        public ShipSaveSlotsPatcher(string vanillaShipDir)
        {
            _vanillaShipDir = vanillaShipDir;
        }

        // ------------------------------------------------------------------
        // Discovery: every ship across every character DB.
        // ------------------------------------------------------------------

        public List<SaveShip> DiscoverShips()
        {
            var result = new List<SaveShip>();
            foreach (var folder in InventorySaveSlotsPatcher.DiscoverCharacterDbFolders())
            {
                try { result.AddRange(ReadShips(folder)); }
                catch (Exception e) { LogLine("  skip " + folder + ": " + e.Message); }
            }
            return result;
        }

        public List<SaveShip> ReadShips(string dbFolder)
        {
            var ships = new List<SaveShip>();
            if (!IsCharacterDbDir(dbFolder)) return ships;

            var (dbOpts, cfs) = OpenOptions(dbFolder);
            using var db = RocksDb.OpenReadOnly(dbOpts, dbFolder, cfs, false);
            if (!cfs.Any(c => c.Name == ShipCf)) return ships;
            var cf = db.GetColumnFamily(ShipCf);
            var charId = Path.GetFileName(dbFolder);
            var playerName = ReadPlayerName(db, cfs);

            using var it = db.NewIterator(cf);
            for (it.SeekToFirst(); it.Valid(); it.Next())
            {
                var key = it.Key();
                var v = it.Value();
                if (v == null || v.Length < 8) continue;
                SaveShip s;
                try { s = ParseShip(v); }
                catch { continue; }
                if (s == null) continue;
                s.DbFolder = dbFolder;
                s.CharacterId = charId;
                s.OwnerName = playerName;
                s.ShipKey = Convert.ToHexString(key);
                ships.Add(s);
            }
            return ships;
        }

        SaveShip ParseShip(byte[] v)
        {
            // Must look like a ship inventory doc.
            if (BsonIndexOf(v, Encoding.ASCII.GetBytes(ChestMarker), 0) < 0) return null;

            var s = new SaveShip();
            s.ShipName = ReadTopString(v, "ShipName") ?? "";
            var invParams = ReadInventoryParams(v);
            s.SourceDa = invParams != null ? DaBasename(invParams) : "";
            s.Supported = ShipSlotsPatcher.IsTargetShipFile(s.SourceDa);
            s.VanillaCargoBase = s.Supported ? VanillaCargoBase(s.SourceDa) : 0;

            var cargo = LocateModule(v, DefaultModuleTag);
            if (cargo != null)
            {
                s.CargoSlots = cargo.LiveSlots.Count(e => e.IsTargetKind);
                s.BlueprintCargo = cargo.BlueprintCount;
            }
            var equip = LocateModule(v, EquipmentModuleTag);
            if (equip != null)
            {
                s.CombatSlots = equip.LiveSlots.Count(e => e.IsTargetKind);
                s.BlueprintCombat = equip.BlueprintCount;
            }
            return s;
        }

        // ------------------------------------------------------------------
        // Patch one ship to (cargoMultiplier, combatOrders).
        // ------------------------------------------------------------------

        public ShipPatchResult PatchShip(string dbFolder, string shipKeyHex,
            double cargoMultiplier, int combatOrders, bool force = false)
        {
            if (!IsCharacterDbDir(dbFolder))
                throw new DirectoryNotFoundException(
                    "Not a Windrose character save folder (no CURRENT): " + dbFolder);
            if (string.IsNullOrEmpty(shipKeyHex))
                throw new ArgumentNullException(nameof(shipKeyHex));

            var profilesRoot = InventorySaveSlotsPatcher.SaveProfilesRoot();
            if (profilesRoot == null || !Path.GetFullPath(dbFolder)
                    .StartsWith(Path.GetFullPath(profilesRoot), StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException(
                    "Refusing to patch a folder outside the Windrose save profiles root.");

            var key = Convert.FromHexString(shipKeyHex);
            var result = new ShipPatchResult();

            var (dbOpts, cfs) = OpenOptions(dbFolder);
            using (var db = RocksDb.Open(dbOpts, dbFolder, cfs))
            {
                var cf = db.GetColumnFamily(ShipCf);
                var value = db.Get(key, cf);
                if (value == null)
                    throw new InvalidOperationException("Ship not found in this save.");

                var ship = ParseShip(value);
                if (ship == null)
                    throw new InvalidOperationException("Selected entry is not a ship inventory doc.");
                result.ShipName = ship.ShipName;
                result.SourceDa = ship.SourceDa;

                if (!ShipSlotsPatcher.IsTargetShipFile(ship.SourceDa))
                {
                    result.Unsupported = true;
                    return result;
                }

                int vanillaBase = VanillaCargoBase(ship.SourceDa);
                int targetCargo = ShipSlotsPatcher.CargoTarget(vanillaBase, cargoMultiplier);
                int targetCombat = combatOrders;

                var cargoInfo = LocateModule(value, DefaultModuleTag);
                var equipInfo = LocateModule(value, EquipmentModuleTag);

                result.OldCargo = cargoInfo?.LiveSlots.Count(e => e.IsTargetKind) ?? 0;
                result.OldCombat = equipInfo?.LiveSlots.Count(e => e.IsTargetKind) ?? 0;
                result.NewCargo = cargoInfo != null ? targetCargo : result.OldCargo;
                result.NewCombat = equipInfo != null ? targetCombat : result.OldCombat;

                // "Needs patch" = the save's current state (live count OR blueprint)
                // differs from the target. The target derives from the vanilla cargo
                // base * multiplier and the absolute combat count - NEVER from the
                // current value - so resetting the sliders back to vanilla (x1 / 1)
                // downgrades a previously-patched ship to vanilla instead of being
                // wrongly treated as "already up to date".
                bool cargoNeeds = cargoInfo != null
                    && (result.OldCargo != targetCargo || cargoInfo.BlueprintCount != targetCargo);
                bool combatNeeds = equipInfo != null
                    && (result.OldCombat != targetCombat || equipInfo.BlueprintCount != targetCombat);

                if (!cargoNeeds && !combatNeeds)
                {
                    result.AlreadyMatches = true;
                    return result;
                }

                // Blocking-item check for any shrink (cargo OR combat) before we
                // touch anything - this also guards downgrades back toward vanilla.
                var blocking = new List<string>();
                if (cargoNeeds)
                    blocking.AddRange(BlockingOnShrink(value, cargoInfo, targetCargo, "Cargo"));
                if (combatNeeds)
                    blocking.AddRange(BlockingOnShrink(value, equipInfo, targetCombat, "Combat order"));
                if (blocking.Count > 0 && !force)
                {
                    result.BlockingItems = blocking;
                    return result;
                }

                // Pre-patch backup of this ship's value (never overwrite).
                var bak = Path.Combine(dbFolder,
                    "ship_" + shipKeyHex + ".value.pre-patch.bak");
                if (!File.Exists(bak))
                {
                    File.WriteAllBytes(bak, value);
                    result.BackupPath = bak;
                    LogLine("  pre-patch backup: " + bak);
                }

                result.OldBytes = value.Length;
                // Patch one module per pass, re-locating on the updated buffer
                // (a splice shifts every later offset).
                if (cargoNeeds)
                    value = PatchModule(value, DefaultModuleTag, ChestMarker, targetCargo, force);
                if (combatNeeds)
                    value = PatchModule(value, EquipmentModuleTag, CombatOrdersMarker, targetCombat, force);
                result.NewBytes = value.Length;
                LogLine("  patched ship " + (string.IsNullOrEmpty(ship.ShipName) ? ship.SourceDa : ship.ShipName)
                    + ": cargo " + result.OldCargo + "->" + result.NewCargo
                    + ", combat " + result.OldCombat + "->" + result.NewCombat
                    + " (" + result.OldBytes + "->" + result.NewBytes + " bytes)");

                db.Put(key, value, cf);
                try { db.CompactRange((byte[])null, (byte[])null, cf); } catch { }
            }

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

        // ================= vanilla base lookup =================

        int VanillaCargoBase(string sourceDaBasename)
        {
            if (string.IsNullOrEmpty(sourceDaBasename)) return 0;
            if (_vanillaBaseCache.TryGetValue(sourceDaBasename, out var cached)) return cached;
            int baseVal = 0;
            try
            {
                var path = Path.Combine(_vanillaShipDir ?? "", sourceDaBasename + ".json");
                if (File.Exists(path))
                {
                    var root = JsonNode.Parse(File.ReadAllText(path, Encoding.UTF8)) as JsonObject;
                    var modules = root?["InventoryModules"]?.AsArray();
                    if (modules != null)
                        foreach (var m in modules)
                        {
                            if (m?["ModuleTag"]?["TagName"]?.GetValue<string>() != DefaultModuleTag) continue;
                            foreach (var slot in m["Slots"]?.AsArray() ?? new JsonArray())
                            {
                                var sp = slot?["SlotParams"]?.GetValue<string>() ?? "";
                                if (sp.IndexOf(ChestMarker, StringComparison.OrdinalIgnoreCase) >= 0)
                                { baseVal = slot["CountSlots"]?.GetValue<int>() ?? 0; break; }
                            }
                        }
                }
            }
            catch { baseVal = 0; }
            _vanillaBaseCache[sourceDaBasename] = baseVal;
            return baseVal;
        }

        static string DaBasename(string assetPath)
        {
            if (string.IsNullOrEmpty(assetPath)) return "";
            var afterSlash = assetPath.Split('/').Last();
            return afterSlash.Split('.').First();
        }

        // ================= BSON surgery =================
        // Mirrors InventorySaveSlotsPatcher's verified primitives, generalised to
        // grow ONE slot-kind (matched by SlotParams marker) inside a module's live
        // array to a target count, preserving every other slot in place.
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

        static int BsonIndexOf(byte[] hay, byte[] needle, int start)
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

        string ReadTopString(byte[] v, string field)
        {
            var f = Field(v, 0, field);
            return f is { t: BT_STRING } ? ReadStr(v, f.Value.vpos) : null;
        }

        string ReadInventoryParams(byte[] v)
        {
            var inv = Field(v, 0, "Inventory");
            if (inv is not { t: BT_SUBDOC }) return null;
            var ip = Field(v, inv.Value.vpos, "InventoryParams");
            return ip is { t: BT_STRING } ? ReadStr(v, ip.Value.vpos) : null;
        }

        static string ReadPlayerName(RocksDb db, ColumnFamilies cfs)
        {
            if (!cfs.Any(c => c.Name == "R5BLPlayer")) return null;
            var cf = db.GetColumnFamily("R5BLPlayer");
            var marker = Encoding.ASCII.GetBytes("PlayerName\0");
            using var it = db.NewIterator(cf);
            for (it.SeekToFirst(); it.Valid(); it.Next())
            {
                var v = it.Value();
                if (v == null) continue;
                int p = BsonIndexOf(v, marker, 0);
                if (p < 0) continue;
                int start = p + marker.Length;
                if (v.Length < start + 4) continue;
                int n = (int)U32(v, start);
                if (n <= 0 || v.Length < start + 4 + n) continue;
                return Encoding.UTF8.GetString(v, start + 4, n).TrimEnd('\0');
            }
            return null;
        }

        sealed class LiveSlot
        {
            public bool IsTargetKind;
            public bool HasItem;
            public int ElemStart, ElemEnd;
        }

        sealed class ModuleInfo
        {
            public List<int> AncestorChain;       // root..moduleDoc inclusive
            public int ModuleDocStart;
            public int BlueprintCountPos;         // int32 pos of the target slot's CountSlots, -1 if none
            public int BlueprintCount = -1;
            public int LiveArrayStart, LiveArrayEnd;
            public List<LiveSlot> LiveSlots = new();
            public string Marker;                 // the slot-kind marker this module was located for
        }

        // Locate a module by its ModuleTag, classifying its live slots by whether
        // the SlotParams contains the relevant marker for that module.
        ModuleInfo LocateModule(byte[] buf, string moduleTag)
        {
            string marker = moduleTag == DefaultModuleTag ? ChestMarker
                : moduleTag == EquipmentModuleTag ? CombatOrdersMarker : null;

            ModuleInfo found = null;
            void Descend(int docStart, List<int> chain)
            {
                if (found != null) return;
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
                                if (tn is { t: BT_STRING } && ReadStr(buf, tn.Value.vpos) == moduleTag)
                                {
                                    found = BuildModuleInfo(buf, vpos, mp.Value.vpos, marker,
                                        new List<int>(chain) { docStart, vpos });
                                    return;
                                }
                            }
                        }
                        Descend(vpos, new List<int>(chain) { docStart });
                        if (found != null) return;
                    }
                    else // array
                    {
                        Descend(vpos, new List<int>(chain) { docStart });
                        if (found != null) return;
                    }
                }
            }
            Descend(0, new List<int>());
            return found;
        }

        static ModuleInfo BuildModuleInfo(byte[] buf, int moduleDocStart, int moduleParamsStart,
            string marker, List<int> ancestorChain)
        {
            var info = new ModuleInfo
            {
                ModuleDocStart = moduleDocStart,
                AncestorChain = ancestorChain,
                Marker = marker,
            };

            // Blueprint: ModuleParams.Slots -> entry whose SlotParams contains marker.
            var bp = Field(buf, moduleParamsStart, "Slots");
            if (bp is { t: BT_ARRAY } && marker != null)
                foreach (var (t, _, vpos, _) in Elements(buf, bp.Value.vpos))
                {
                    if (t != BT_SUBDOC) continue;
                    var sp = Field(buf, vpos, "SlotParams");
                    var cs = Field(buf, vpos, "CountSlots");
                    if (sp is not { t: BT_STRING } || cs is not { t: BT_INT32 }) continue;
                    if (ReadStr(buf, sp.Value.vpos).IndexOf(marker, StringComparison.OrdinalIgnoreCase) >= 0)
                    { info.BlueprintCountPos = cs.Value.vpos; info.BlueprintCount = I32(buf, cs.Value.vpos); break; }
                }

            // Live: module.Slots (the array directly on the module subdoc).
            var live = Field(buf, moduleDocStart, "Slots")
                ?? throw new InvalidDataException("Module live Slots array missing.");
            if (live.t != BT_ARRAY) throw new InvalidDataException("Module live Slots not an array.");
            info.LiveArrayStart = live.vpos;
            info.LiveArrayEnd = live.vend;
            foreach (var (t, name, vpos, vend) in Elements(buf, live.vpos))
            {
                if (t != BT_SUBDOC) continue;
                int elemStart = vpos - name.Length - 2;
                var sp = Field(buf, vpos, "SlotParams");
                bool isKind = marker != null && sp is { t: BT_STRING }
                    && ReadStr(buf, sp.Value.vpos).IndexOf(marker, StringComparison.OrdinalIgnoreCase) >= 0;
                info.LiveSlots.Add(new LiveSlot
                {
                    IsTargetKind = isKind,
                    HasItem = SlotHasItem(buf, vpos),
                    ElemStart = elemStart,
                    ElemEnd = vend,
                });
            }
            return info;
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

        // Items that would be destroyed by shrinking the target kind to newCount
        // (the trailing target-kind slots beyond newCount that still hold items).
        static List<string> BlockingOnShrink(byte[] buf, ModuleInfo info, int newCount, string label)
        {
            var blocking = new List<string>();
            var kind = info.LiveSlots.Where(s => s.IsTargetKind).ToList();
            if (newCount < kind.Count)
                for (int i = newCount; i < kind.Count; i++)
                    if (kind[i].HasItem) blocking.Add(label + " slot " + (i + 1) + " holds an item");
            return blocking;
        }

        // ----- live-array rebuild -----

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

        // An empty clone of a slot element (zeroes its ItemsStack).
        static byte[] EmptiedSlot(byte[] buf, LiveSlot s)
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

        // Re-name a slot element (BSON array index) and overwrite its SlotId int.
        static byte[] Retag(byte[] tmpl, string newName, int newSlotId)
        {
            if (tmpl[0] != BT_SUBDOC) throw new InvalidDataException("Slot template not a sub-doc.");
            int nameEnd = Array.IndexOf(tmpl, (byte)0, 1);
            int subStart = nameEnd + 1;
            int subSize = (int)U32(tmpl, subStart);
            var sub = tmpl[subStart..(subStart+subSize)];
            var marker = Encoding.ASCII.GetBytes("\x10SlotId\0");
            int sp = BsonIndexOf(sub, marker, 0);
            if (sp >= 0) LE(newSlotId).CopyTo(sub, sp + marker.Length);
            var outp = new List<byte> { BT_SUBDOC };
            outp.AddRange(Encoding.ASCII.GetBytes(newName));
            outp.Add(0);
            outp.AddRange(sub);
            return outp.ToArray();
        }

        static byte[] EmptyTemplate(byte[] buf, List<LiveSlot> kind)
        {
            foreach (var s in kind) if (!s.HasItem) return buf[s.ElemStart..s.ElemEnd];
            return EmptiedSlot(buf, kind[0]);
        }

        // Rebuild the module's live array so the target kind has exactly newCount
        // entries: keep existing entries in their original order (dropping trailing
        // target-kind slots on a shrink), then append empty clones to reach
        // newCount; renumber every element 0..n-1 with a matching SlotId.
        static byte[] BuildLiveArray(byte[] buf, ModuleInfo info, int newCount)
        {
            var kind = info.LiveSlots.Where(s => s.IsTargetKind).ToList();
            if (kind.Count == 0)
                throw new InvalidDataException("Module has no live slot of the target kind to template.");
            int have = kind.Count;

            // Which trailing target-kind slots to drop (shrink). Identity by ElemStart.
            var dropped = new HashSet<int>();
            if (newCount < have)
                for (int i = newCount; i < have; i++) dropped.Add(kind[i].ElemStart);

            var sources = new List<byte[]>();
            foreach (var s in info.LiveSlots)
            {
                if (s.IsTargetKind && dropped.Contains(s.ElemStart)) continue;
                sources.Add(buf[s.ElemStart..s.ElemEnd]);
            }
            if (newCount > have)
            {
                var tmpl = EmptyTemplate(buf, kind);
                for (int i = 0; i < newCount - have; i++) sources.Add(tmpl);
            }

            var body = new List<byte>();
            for (int i = 0; i < sources.Count; i++) body.AddRange(Retag(sources[i], i.ToString(), i));
            body.Add(0);
            int arrSize = 4 + body.Count;
            var res = new List<byte>(LE(arrSize)); res.AddRange(body); return res.ToArray();
        }

        // Set the blueprint count (in-place int32, no size change) then splice the
        // rebuilt live array in, fixing every ancestor doc's size prefix.
        byte[] PatchModule(byte[] value, string moduleTag, string marker, int newCount, bool force)
        {
            var info = LocateModule(value, moduleTag)
                ?? throw new InvalidDataException("Module " + moduleTag + " not found in ship doc.");

            var outp = (byte[])value.Clone();
            if (info.BlueprintCountPos >= 0)
                LE(newCount).CopyTo(outp, info.BlueprintCountPos);

            var newArray = BuildLiveArray(outp, info, newCount);
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

        // ================= RocksDB plumbing =================

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
    }

    public sealed class SaveShip
    {
        public string DbFolder;
        public string CharacterId;
        public string OwnerName;
        public string ShipKey;        // hex of the RocksDB key
        public string ShipName;
        public string SourceDa;       // e.g. "DA_ShipInventory_Ketch_Stock"
        public bool Supported;        // Brig/Frigate/Ketch family (patchable)
        public int CargoSlots;
        public int BlueprintCargo;
        public int CombatSlots;
        public int BlueprintCombat;
        public int VanillaCargoBase;  // for target = round(base * multiplier)
    }

    public sealed class ShipPatchResult
    {
        public bool Patched;
        public bool AlreadyMatches;
        public bool Unsupported;
        public string ShipName;
        public string SourceDa;
        public int OldCargo, NewCargo, OldCombat, NewCombat;
        public int OldBytes, NewBytes;
        public string BackupPath;
        public bool CheckpointZipRebuilt;
        public List<string> BlockingItems;
    }
}
