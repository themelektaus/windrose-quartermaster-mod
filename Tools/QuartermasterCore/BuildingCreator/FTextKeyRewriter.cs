using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UAssetAPI;
using UAssetAPI.ExportTypes;
using UAssetAPI.UnrealTypes;
using UAssetAPI.Unversioned;

namespace Windrose.Quartermaster.Core.BuildingCreator
{
    // Rewrites FText records inside a Legacy .uasset+.uexp pair so the
    // displayed text becomes the user-supplied string. Handles both
    // FText.StringTableEntry (HistoryType=11) and FText.Base
    // (HistoryType=0) input shapes:
    //
    //   - StringTableEntry input: rewritten to FText.Base with Namespace=""
    //     and SourceString=<user text>. Drops the TableId reference so the
    //     text travels inline in the asset (no string-table lookup).
    //
    //   - Base input: only the SourceString FString is spliced; Flags,
    //     HistoryType, Namespace and Key stay verbatim. Sufficient because
    //     FText.Base already renders by SourceString when no localization
    //     override is registered for the (Namespace,Key) pair.
    //
    // Motivation:
    //   Custom building DAs (R5BuildingItem class) start life as a clone
    //   of a vanilla building DA. Vanilla DAs carry their display name
    //   + tooltip as either StringTableEntry (most decoration DAs point
    //   at the shared "BuildingItems" string-table) or Base (some POI /
    //   debug DAs ship inline text with auto-generated GUID keys).
    //
    //   A per-profile CSV is not an option: the Windrose CSV loader only
    //   registers the two vanilla CSVs (InventoryItems.csv +
    //   BuildingItems.csv) at boot by hardcoded name. Any
    //   BuildingItems_<short>.csv shipped in our pak would land on disk
    //   but never be mounted as a StringTable, so every lookup against
    //   it would resolve to <MISSING_STRING>.
    //
    //   Solution: inline the user text in the asset itself - either by
    //   converting StringTableEntry to Base (one-time rewrite) or by
    //   updating the existing Base's SourceString. Vanilla itself ships
    //   items that do this (e.g. DA_DID_Misc_EliaShell_T04's ItemName is
    //   a plain literal FText.Base), so it is a normal first-class FText
    //   shape, not a hack.
    //
    // Strategy:
    //   - Open the asset via UAssetAPI (gives us NameMap + RawExport.Data)
    //   - For each RawExport.Data byte array, scan for the vanilla Key as
    //     an on-disk FString (4-byte little-endian length prefix matching
    //     key+1, then UTF-8 bytes, then null term).
    //   - Disambiguate StringTableEntry vs Base by checking the bytes
    //     immediately preceding the Key:
    //         * StringTableEntry: HistoryType=11 at i-9, then 8 bytes
    //           TableId FName (NameIndex+Number) right before the Key.
    //         * Base (empty namespace, length=0): HistoryType=0 at i-5,
    //           then 4 bytes Namespace length=0.
    //         * Base (empty namespace, length=1): HistoryType=0 at i-6,
    //           then 4 bytes Namespace length=1, then 1 null byte.
    //         (Non-empty Base namespaces are not yet handled - we have
    //          not seen one on a vanilla building DA. Fallback: skip and
    //          warn so the user knows the in-game text stays as cloned.)
    //   - StringTableEntry match: splice [HistoryType .. end-of-Key] with
    //     a fresh FText.Base body (HistoryType=0 + Namespace="" + Key=
    //     vanillaKey + SourceString=<user text>).
    //   - Base match: only splice the SourceString FString that follows
    //     the Key. Flags / HistoryType / Namespace / Key stay verbatim.
    //   - The byte-array length changes per splice - UAssetAPI's
    //     asset.Write() recomputes Export.SerialSize and downstream
    //     offsets automatically, so we only need to mutate
    //     RawExport.Data.
    //
    // Limits:
    //   - Only ASCII vanilla keys (vanilla building DAs use only ASCII
    //     for FText keys, including the auto-generated GUID hex strings).
    //   - Only positive-length FString encoding on the vanilla key (UTF-8).
    //     UE supports negative-length for UTF-16 but vanilla building DAs
    //     do not use it for keys. The NEW SourceString we emit picks UTF-8
    //     vs UTF-16 automatically based on whether the user text is ASCII
    //     (umlauts / non-Latin glyphs trigger UTF-16).
    //   - FText.Base with a non-empty namespace currently surfaces as a
    //     "key not found" miss. Extend BaseHistoryTypeOffset() if a real
    //     template needs it.
    public sealed class FTextKeyRewriter
    {
        public Action<string> Log;

        // Process one asset. For each vanilla FText.StringTableEntry whose
        // Key matches one in displayTextByVanillaKey, rewrite the binary
        // FText record into HistoryType=Base with Namespace="",
        // Key="<vanillaKey>" and SourceString=<user-display-text>.
        //
        // The Key in the rewritten FText.Base record is preserved verbatim
        // from the vanilla key - it's just used as a cache-identity hint
        // by UE's text cache, has no runtime semantics here because the
        // SourceString is what renders.
        //
        // Returns per-vanilla-key occurrence counts. Missing keys are NOT
        // a hard error - the building still renders, just with whatever
        // the cloned DA already carried for that field.
        public FTextKeyRewriteResult Patch(
            string assetPath,
            string usmapPath,
            IReadOnlyDictionary<string, string> displayTextByVanillaKey)
        {
            if (string.IsNullOrEmpty(assetPath))
                throw new ArgumentNullException("assetPath");
            if (displayTextByVanillaKey == null || displayTextByVanillaKey.Count == 0)
                throw new ArgumentException("displayTextByVanillaKey must not be empty");
            if (!File.Exists(assetPath))
                throw new FileNotFoundException("uasset not found: " + assetPath);
            if (!File.Exists(usmapPath))
                throw new FileNotFoundException("usmap not found: " + usmapPath);

            // Build the per-key plan once - vanillaKey -> (search pattern,
            // pre-encoded replacement body bytes).
            var rewrites = new List<RewritePlan>(displayTextByVanillaKey.Count);
            foreach (var kv in displayTextByVanillaKey)
            {
                if (string.IsNullOrEmpty(kv.Key)) continue;
                rewrites.Add(RewritePlan.Create(kv.Key, kv.Value ?? string.Empty));
            }
            if (rewrites.Count == 0)
                return new FTextKeyRewriteResult { PerKeyHits = new Dictionary<string, int>() };

            var mappings = new Usmap(usmapPath);
            var asset = new UAsset(assetPath, EngineVersion.VER_UE5_6, mappings);

            var perKeyHits = new Dictionary<string, int>(rewrites.Count, StringComparer.Ordinal);
            foreach (var r in rewrites) perKeyHits[r.VanillaKey] = 0;

            int rawExportsTouched = 0;
            for (int i = 0; i < asset.Exports.Count; i++)
            {
                if (!(asset.Exports[i] is RawExport raw)) continue;
                if (raw.Data == null || raw.Data.Length == 0) continue;

                bool touched = false;
                // After every successful splice the byte array changes
                // length, so we walk via a method that returns the new
                // buffer + per-key hit count.
                foreach (var r in rewrites)
                {
                    var (newData, hits) = RewriteOccurrences(raw.Data, r);
                    if (hits > 0)
                    {
                        raw.Data = newData;
                        perKeyHits[r.VanillaKey] = perKeyHits[r.VanillaKey] + hits;
                        touched = true;
                        LogLine("  FText[" + i + "] '" + r.VanillaKey
                                + "' -> FText.Base SourceString='" + Truncate(r.DisplayText, 60)
                                + "' (" + hits + " occurrence"
                                + (hits == 1 ? "" : "s") + ")");
                    }
                }
                if (touched) rawExportsTouched++;
            }

            if (rawExportsTouched > 0)
            {
                LogLine("  Writing FText-base-patched asset: " + assetPath);
                asset.Write(assetPath);
            }
            else
            {
                LogLine("  (no FText keys matched - asset bytes unchanged)");
            }

            var missed = new List<string>();
            foreach (var kv in perKeyHits)
            {
                if (kv.Value == 0) missed.Add(kv.Key);
            }

            return new FTextKeyRewriteResult
            {
                PerKeyHits = perKeyHits,
                Missed = missed,
                RawExportsTouched = rawExportsTouched,
            };
        }

        // Scans `data` for every FText whose Key matches the planned
        // vanilla key, and splices each match to inline the user-supplied
        // display text. Returns the (possibly resized) buffer + hit count.
        //
        // Supports two FText shapes:
        //   - StringTableEntry (HistoryType=11): splice [HistoryType ..
        //     end-of-Key] with a fresh FText.Base body.
        //   - Base (HistoryType=0, empty namespace): splice only the
        //     SourceString FString that follows the Key. Flags, HistoryType,
        //     Namespace and Key stay verbatim.
        //
        // The match anchor is the Key's FString-on-disk encoding (length
        // prefix + UTF-8 bytes + null term). Disambiguation between the
        // two shapes happens by walking back from the Key length prefix
        // and checking the HistoryType byte at the expected offset (see
        // class doc comment for the layout sketches).
        static (byte[] newData, int hits) RewriteOccurrences(byte[] data, RewritePlan plan)
        {
            int hits = 0;
            int prefixLen = 4;                              // FString length prefix bytes
            int keyTotalLen = prefixLen + plan.KeyOnDiskLength;  // length prefix + body + null

            // Each splice resizes the buffer. To keep the scan simple, we
            // build the output incrementally into a list-of-segments and
            // glue it at the end (one allocation, no shift cost per match).
            var segments = new List<byte[]>();
            int cursor = 0;

            int i = 0;
            while (i <= data.Length - keyTotalLen)
            {
                // Match length prefix (signed little-endian int32, positive).
                if (data[i]     != plan.KeyLenLE[0]
                 || data[i + 1] != plan.KeyLenLE[1]
                 || data[i + 2] != plan.KeyLenLE[2]
                 || data[i + 3] != plan.KeyLenLE[3])
                {
                    i++;
                    continue;
                }

                // Match key body (UTF-8 + null terminator).
                bool match = true;
                for (int j = 0; j < plan.KeyBytes.Length; j++)
                {
                    if (data[i + prefixLen + j] != plan.KeyBytes[j]) { match = false; break; }
                }
                if (!match) { i++; continue; }
                if (data[i + prefixLen + plan.KeyBytes.Length] != 0) { i++; continue; }

                // Try StringTableEntry layout first. The 9 bytes preceding
                // the Key form [HistoryType=11(1) TableId(8)]. The Flags
                // int32 sits at [i-13..i-9] but is preserved verbatim, so
                // we only need to validate the HistoryType byte at i-9.
                if (i >= 9 && data[i - 9] == 11)
                {
                    int matchStart = i - 9;                  // HistoryType byte
                    int matchEnd   = i + keyTotalLen;        // exclusive: past Key's null term

                    int preLen = matchStart - cursor;
                    if (preLen > 0)
                    {
                        var pre = new byte[preLen];
                        Buffer.BlockCopy(data, cursor, pre, 0, preLen);
                        segments.Add(pre);
                    }
                    segments.Add(plan.ReplacementBody);

                    hits++;
                    cursor = matchEnd;
                    i = matchEnd;
                    continue;
                }

                // Try FText.Base layout (empty namespace). Two encodings of
                // an empty namespace are possible (length=0 with no body, or
                // length=1 with a single null byte) - probe both. If found,
                // splice only the SourceString FString that follows the Key.
                int baseHistOffset = TryDetectBaseEmptyNamespace(data, i);
                if (baseHistOffset >= 0)
                {
                    // SourceString FString starts immediately after the Key.
                    int sourceStringOffset = i + keyTotalLen;
                    int sourceStringTotal = FStringOnDiskBytes(data, sourceStringOffset);
                    if (sourceStringTotal < 0)
                    {
                        // SourceString is truncated or malformed - skip the
                        // splice rather than corrupt the asset.
                        i++;
                        continue;
                    }

                    int matchStart = sourceStringOffset;
                    int matchEnd   = sourceStringOffset + sourceStringTotal;

                    // Preserved-prefix segment: everything up to (but not
                    // including) the existing SourceString. Includes the
                    // Flags / HistoryType / Namespace / Key bytes verbatim.
                    int preLen = matchStart - cursor;
                    if (preLen > 0)
                    {
                        var pre = new byte[preLen];
                        Buffer.BlockCopy(data, cursor, pre, 0, preLen);
                        segments.Add(pre);
                    }
                    segments.Add(plan.SourceStringFString);

                    hits++;
                    cursor = matchEnd;
                    i = matchEnd;
                    continue;
                }

                // Not a recognised FText layout - could be the same byte
                // sequence appearing elsewhere (NameMap entry, raw string
                // literal, etc.). Don't splice.
                i++;
            }

            if (hits == 0) return (data, 0);

            // Tail segment: everything after the last match.
            if (cursor < data.Length)
            {
                var tail = new byte[data.Length - cursor];
                Buffer.BlockCopy(data, cursor, tail, 0, tail.Length);
                segments.Add(tail);
            }

            // Concat into a single buffer.
            int total = 0;
            for (int s = 0; s < segments.Count; s++) total += segments[s].Length;
            var output = new byte[total];
            int p = 0;
            for (int s = 0; s < segments.Count; s++)
            {
                Buffer.BlockCopy(segments[s], 0, output, p, segments[s].Length);
                p += segments[s].Length;
            }
            return (output, hits);
        }

        // Probes for FText.Base with an empty namespace immediately before
        // the Key length prefix at offset `keyOffset`. Returns the byte
        // offset of the HistoryType byte (= 0) on match, or -1 if neither
        // encoding fits. Empty namespace has two on-disk forms:
        //   (a) length=0, no body bytes      -> namespace block is 4 bytes
        //   (b) length=1, single null byte   -> namespace block is 5 bytes
        // The HistoryType byte sits one byte before the namespace block.
        static int TryDetectBaseEmptyNamespace(byte[] data, int keyOffset)
        {
            // Case (a): HistoryType at keyOffset-5, namespace length=0.
            if (keyOffset >= 5
                && data[keyOffset - 5] == 0
                && data[keyOffset - 4] == 0
                && data[keyOffset - 3] == 0
                && data[keyOffset - 2] == 0
                && data[keyOffset - 1] == 0)
            {
                return keyOffset - 5;
            }

            // Case (b): HistoryType at keyOffset-6, namespace length=1,
            // namespace body null byte at keyOffset-1.
            if (keyOffset >= 6
                && data[keyOffset - 6] == 0
                && data[keyOffset - 5] == 1
                && data[keyOffset - 4] == 0
                && data[keyOffset - 3] == 0
                && data[keyOffset - 2] == 0
                && data[keyOffset - 1] == 0)
            {
                return keyOffset - 6;
            }

            return -1;
        }

        // Reads an FString's total on-disk byte count (length prefix +
        // body) starting at `offset`. Returns -1 if the buffer is too
        // short to contain a complete FString.
        //
        // FString length-prefix encoding (FArchive::operator<<):
        //   length == 0      : empty string, 4 prefix bytes only
        //   length  > 0      : ANSI, 4 prefix + length body bytes (incl. null)
        //   length  < 0      : UTF-16, 4 prefix + |length| * 2 body bytes
        static int FStringOnDiskBytes(byte[] data, int offset)
        {
            if (offset + 4 > data.Length) return -1;
            int len = data[offset]
                    | (data[offset + 1] << 8)
                    | (data[offset + 2] << 16)
                    | (data[offset + 3] << 24);
            if (len == 0) return 4;
            int body = len > 0 ? len : -len * 2;
            if (offset + 4 + body > data.Length) return -1;
            return 4 + body;
        }

        static string Truncate(string s, int max)
        {
            if (string.IsNullOrEmpty(s)) return string.Empty;
            return s.Length <= max ? s : s.Substring(0, max - 1) + "...";
        }

        void LogLine(string msg) { if (Log != null) Log(msg); }

        // Per-vanilla-key plan: vanilla key byte pattern (for matching) +
        // pre-encoded splice payloads. Two payloads are pre-built because
        // the two FText shapes we handle need different splice content:
        //
        //   ReplacementBody (for StringTableEntry input):
        //     [HistoryType: int8 = 0]                       1 byte
        //     [Namespace FString = ""]                      4 bytes (length 0)
        //     [Key FString = vanillaKey]                    4 + N + 1 bytes
        //     [SourceString FString = displayText]          4 + M + {1,2} bytes
        //
        //   SourceStringFString (for Base input):
        //     [SourceString FString = displayText]          4 + M + {1,2} bytes
        //   (Just the FString on its own - the rest of the FText body is
        //    preserved verbatim around this splice.)
        readonly struct RewritePlan
        {
            public readonly string VanillaKey;
            public readonly string DisplayText;
            // UTF-8 bytes of the vanilla key WITHOUT null terminator.
            public readonly byte[] KeyBytes;
            // OnDiskLength = KeyBytes.Length + 1 (FString includes null
            // in its positive-length prefix).
            public readonly int    KeyOnDiskLength;
            // Little-endian 4-byte encoding of KeyOnDiskLength.
            public readonly byte[] KeyLenLE;
            // Full FText.Base body bytes ready to splice in (used when the
            // input FText is StringTableEntry and we're converting shape).
            public readonly byte[] ReplacementBody;
            // Just the SourceString FString bytes (used when the input
            // FText is already Base and we're only updating its source).
            public readonly byte[] SourceStringFString;

            RewritePlan(string vanillaKey, string displayText,
                        byte[] keyBytes, int keyLen, byte[] keyLenLE,
                        byte[] replacementBody, byte[] sourceStringFString)
            {
                VanillaKey          = vanillaKey;
                DisplayText         = displayText;
                KeyBytes            = keyBytes;
                KeyOnDiskLength     = keyLen;
                KeyLenLE            = keyLenLE;
                ReplacementBody     = replacementBody;
                SourceStringFString = sourceStringFString;
            }

            public static RewritePlan Create(string vanillaKey, string displayText)
            {
                if (string.IsNullOrEmpty(vanillaKey))
                    throw new ArgumentException("vanillaKey must not be empty");
                if (displayText == null) displayText = string.Empty;

                var keyBytes = Encoding.UTF8.GetBytes(vanillaKey);
                int keyOnDisk = keyBytes.Length + 1;        // includes null term
                var keyLenLE  = Int32LE(keyOnDisk);

                // Pre-encode the SourceString FString once - shared by both
                // splice payloads.
                using var srcMs = new MemoryStream();
                WriteFString(srcMs, displayText);
                var sourceStringFString = srcMs.ToArray();

                // Pre-encode the full FText.Base body for the StringTable-
                // Entry -> Base conversion path.
                using var ms = new MemoryStream();
                ms.WriteByte(0);                            // HistoryType = Base
                WriteFString(ms, string.Empty);             // Namespace = ""
                WriteFString(ms, vanillaKey);               // Key = preserved
                ms.Write(sourceStringFString, 0, sourceStringFString.Length);
                var body = ms.ToArray();

                return new RewritePlan(vanillaKey, displayText,
                                       keyBytes, keyOnDisk, keyLenLE,
                                       body, sourceStringFString);
            }
        }

        // FString on-disk encoding per UE's FArchive::operator<<:
        //   - Empty string  : write int32 length = 0, no bytes.
        //   - ANSI-safe     : write int32 length = chars+1 (positive,
        //                     includes null term), then ASCII bytes, then
        //                     1 null byte.
        //   - Contains non-ANSI: write int32 length = -(chars+1) (negative,
        //                     UCS-2 sized), then UTF-16-LE bytes, then 2
        //                     null bytes.
        static void WriteFString(MemoryStream ms, string s)
        {
            if (string.IsNullOrEmpty(s))
            {
                ms.Write(Int32LE(0), 0, 4);
                return;
            }

            bool isAnsi = true;
            for (int c = 0; c < s.Length; c++)
            {
                if (s[c] > 127) { isAnsi = false; break; }
            }

            if (isAnsi)
            {
                var bytes = Encoding.UTF8.GetBytes(s);      // ANSI subset of UTF-8
                ms.Write(Int32LE(bytes.Length + 1), 0, 4);
                ms.Write(bytes, 0, bytes.Length);
                ms.WriteByte(0);
            }
            else
            {
                // Negative length = UCS-2/UTF-16 character count including
                // the null terminator. UE serializes UTF-16-LE on disk.
                var utf16 = Encoding.Unicode.GetBytes(s);   // little-endian
                int charCount = s.Length + 1;               // +1 for null term
                ms.Write(Int32LE(-charCount), 0, 4);
                ms.Write(utf16, 0, utf16.Length);
                ms.WriteByte(0);                            // null term low byte
                ms.WriteByte(0);                            // null term high byte
            }
        }

        static byte[] Int32LE(int v)
        {
            var b = new byte[4];
            b[0] = (byte)(v & 0xFF);
            b[1] = (byte)((v >> 8) & 0xFF);
            b[2] = (byte)((v >> 16) & 0xFF);
            b[3] = (byte)((v >> 24) & 0xFF);
            return b;
        }
    }

    public sealed class FTextKeyRewriteResult
    {
        // Per-vanilla-key occurrence counts (>= 0). One match = one binary
        // FText record rewritten from StringTableEntry to Base.
        public Dictionary<string, int> PerKeyHits;
        // Subset of keys with 0 hits - vanilla bytes weren't found. Empty
        // list = all keys hit at least once. Caller can surface as a
        // warning (template / extracted DA mismatch).
        public List<string> Missed = new List<string>();
        // Number of RawExports that had at least one rewrite. Used to
        // decide whether to re-write the file (0 = skip the I/O).
        public int RawExportsTouched;
    }
}
