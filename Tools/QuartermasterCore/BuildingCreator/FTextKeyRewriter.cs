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
    public sealed class FTextKeyRewriter
    {
        public Action<string> Log;

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

            var rewrites = new List<RewritePlan>(displayTextByVanillaKey.Count);
            foreach (var kv in displayTextByVanillaKey)
            {
                if (string.IsNullOrEmpty(kv.Key)) continue;
                rewrites.Add(RewritePlan.Create(kv.Key, kv.Value ?? string.Empty));
            }
            if (rewrites.Count == 0)
                return new FTextKeyRewriteResult { PerKeyHits = new Dictionary<string, int>() };

            var mappings = new Usmap(usmapPath);
            var asset = new UAsset(assetPath, UAssetIo.Ue, mappings);

            var perKeyHits = new Dictionary<string, int>(rewrites.Count, StringComparer.Ordinal);
            foreach (var r in rewrites) perKeyHits[r.VanillaKey] = 0;

            int rawExportsTouched = 0;
            for (int i = 0; i < asset.Exports.Count; i++)
            {
                if (!(asset.Exports[i] is RawExport raw)) continue;
                if (raw.Data == null || raw.Data.Length == 0) continue;

                bool touched = false;
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

        static (byte[] newData, int hits) RewriteOccurrences(byte[] data, RewritePlan plan)
        {
            int hits = 0;
            int prefixLen = 4;
            int keyTotalLen = prefixLen + plan.KeyOnDiskLength;

            var segments = new List<byte[]>();
            int cursor = 0;

            int i = 0;
            while (i <= data.Length - keyTotalLen)
            {
                if (data[i]     != plan.KeyLenLE[0]
                 || data[i + 1] != plan.KeyLenLE[1]
                 || data[i + 2] != plan.KeyLenLE[2]
                 || data[i + 3] != plan.KeyLenLE[3])
                {
                    i++;
                    continue;
                }

                bool match = true;
                for (int j = 0; j < plan.KeyBytes.Length; j++)
                {
                    if (data[i + prefixLen + j] != plan.KeyBytes[j]) { match = false; break; }
                }
                if (!match) { i++; continue; }
                if (data[i + prefixLen + plan.KeyBytes.Length] != 0) { i++; continue; }

                // StringTableEntry layout: HistoryType=11 byte sits 9 bytes before the Key (1 byte type + 8 byte TableId FName).
                if (i >= 9 && data[i - 9] == 11)
                {
                    int matchStart = i - 9;
                    int matchEnd   = i + keyTotalLen;

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

                int baseHistOffset = TryDetectBaseEmptyNamespace(data, i);
                if (baseHistOffset >= 0)
                {
                    int sourceStringOffset = i + keyTotalLen;
                    int sourceStringTotal = FStringOnDiskBytes(data, sourceStringOffset);
                    if (sourceStringTotal < 0)
                    {
                        i++;
                        continue;
                    }

                    int matchStart = sourceStringOffset;
                    int matchEnd   = sourceStringOffset + sourceStringTotal;

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

                // Same byte sequence may appear outside an FText (NameMap entry, string literal); don't splice.
                i++;
            }

            if (hits == 0) return (data, 0);

            if (cursor < data.Length)
            {
                var tail = new byte[data.Length - cursor];
                Buffer.BlockCopy(data, cursor, tail, 0, tail.Length);
                segments.Add(tail);
            }

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

        // FText.Base empty namespace has two valid on-disk forms: length=0 (no body) or length=1 (one null byte). Returns offset of the HistoryType=0 byte, or -1.
        static int TryDetectBaseEmptyNamespace(byte[] data, int keyOffset)
        {
            if (keyOffset >= 5
                && data[keyOffset - 5] == 0
                && data[keyOffset - 4] == 0
                && data[keyOffset - 3] == 0
                && data[keyOffset - 2] == 0
                && data[keyOffset - 1] == 0)
            {
                return keyOffset - 5;
            }

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

        // FString length prefix: 0 = empty, >0 = ANSI (len body bytes), <0 = UTF-16 (|len|*2 body bytes). Returns total on-disk size, or -1 if truncated.
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

        readonly struct RewritePlan
        {
            public readonly string VanillaKey;
            public readonly string DisplayText;
            public readonly byte[] KeyBytes;
            public readonly int    KeyOnDiskLength;
            public readonly byte[] KeyLenLE;
            // Full FText.Base body, spliced when the input FText is StringTableEntry (shape conversion).
            public readonly byte[] ReplacementBody;
            // Just the SourceString FString, spliced when the input FText is already Base.
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
                int keyOnDisk = keyBytes.Length + 1;
                var keyLenLE  = Int32LE(keyOnDisk);

                using var srcMs = new MemoryStream();
                WriteFString(srcMs, displayText);
                var sourceStringFString = srcMs.ToArray();

                using var ms = new MemoryStream();
                ms.WriteByte(0);                            // HistoryType = Base
                WriteFString(ms, string.Empty);             // Namespace = ""
                WriteFString(ms, vanillaKey);
                ms.Write(sourceStringFString, 0, sourceStringFString.Length);
                var body = ms.ToArray();

                return new RewritePlan(vanillaKey, displayText,
                                       keyBytes, keyOnDisk, keyLenLE,
                                       body, sourceStringFString);
            }
        }

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
                var bytes = Encoding.UTF8.GetBytes(s);
                ms.Write(Int32LE(bytes.Length + 1), 0, 4);
                ms.Write(bytes, 0, bytes.Length);
                ms.WriteByte(0);
            }
            else
            {
                var utf16 = Encoding.Unicode.GetBytes(s);
                int charCount = s.Length + 1;
                ms.Write(Int32LE(-charCount), 0, 4);
                ms.Write(utf16, 0, utf16.Length);
                ms.WriteByte(0);
                ms.WriteByte(0);
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
        public Dictionary<string, int> PerKeyHits;
        public List<string> Missed = new List<string>();
        public int RawExportsTouched;
    }
}
