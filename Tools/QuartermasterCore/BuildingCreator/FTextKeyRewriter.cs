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
    // Rewrites FText.StringTableEntry key strings inside a Legacy
    // .uasset+.uexp pair in-place at the byte level.
    //
    // Motivation:
    //   Windrose's BuildingItem DAs (R5BuildingItem class) carry their
    //   display name + tooltip as FText properties with HistoryType
    //   StringTableEntry. The TableId is an FName referring to the
    //   "BuildingItems" string-table; the literal Key string sits
    //   serialized inline in the export body as a length-prefixed FString
    //   (4-byte little-endian byte count + UTF-8 bytes + null terminator).
    //
    //   These Key strings are NOT in the asset's NameMap, so the standard
    //   DataAssetPatcher (which only renames NameMap entries) cannot
    //   reach them. The R5BuildingItem class also isn't in the engine's
    //   usmap, which is why UAssetAPI loads its export as RawExport (raw
    //   byte blob) rather than a property tree we could walk.
    //
    //   To give each cloned building its own unique localization key
    //   (so the BuildingItems.csv synthesis can map it to user-supplied
    //   display text instead of inheriting the vanilla translation), we
    //   need to find those key bytes and rewrite them.
    //
    // Strategy:
    //   - Open the asset via UAssetAPI (gives us NameMap + RawExport.Data)
    //   - For each RawExport.Data byte array, scan for FString-encoded
    //     instances of the vanilla key, identified by:
    //       * 4-byte little-endian length prefix matching byteCount+1
    //         (the null terminator counts towards the on-disk length)
    //       * UTF-8 bytes equal to the vanilla key
    //       * trailing null terminator
    //   - Replace the bytes in place with a new key of the SAME byte
    //     length (padded with underscores). Same length is critical:
    //     it lets us avoid recomputing SerialSize, ScriptSerializationEnd
    //     offsets, downstream-export offsets, or anything else the asset
    //     header tracks. Pure byte-for-byte swap.
    //
    // Limits:
    //   - The new core key + suffix must fit inside the vanilla key's
    //     byte budget. For the painting DA the budget is 39 bytes for
    //     Name and 36 bytes for Description; "QmBldg_<8charId>_Name" is
    //     20 bytes (plenty of room, padding fills the rest). User-chosen
    //     longer Building Ids could in theory overflow - the rewriter
    //     throws a clear error if the new key won't fit.
    //   - Only ASCII keys are supported (vanilla uses ASCII; building
    //     keys never contain non-ASCII characters).
    //   - Only positive-length FString encoding (UTF-8). UE supports
    //     negative-length FString for UTF-16 but vanilla building DAs
    //     don't use it for these keys.
    public sealed class FTextKeyRewriter
    {
        public Action<string> Log;

        // Process one asset. For each (vanillaKey -> newKey) entry in
        // replacements, scans every RawExport.Data array and rewrites
        // all matching FString-encoded occurrences in place.
        //
        // Returns per-key hit counts so callers can warn on dead-letter
        // replacements (vanilla key not found at all = template / cooked-
        // DA mismatch, worth surfacing). Missing keys are NOT a hard error
        // here - the building still renders, just with the vanilla locale.
        public FTextKeyRewriteResult Patch(
            string assetPath,
            string usmapPath,
            IReadOnlyDictionary<string, string> replacements)
        {
            if (string.IsNullOrEmpty(assetPath))
                throw new ArgumentNullException("assetPath");
            if (replacements == null || replacements.Count == 0)
                throw new ArgumentException("replacements must not be empty");
            if (!File.Exists(assetPath))
                throw new FileNotFoundException("uasset not found: " + assetPath);
            if (!File.Exists(usmapPath))
                throw new FileNotFoundException("usmap not found: " + usmapPath);

            // Build the binary search patterns up-front. Each vanilla key
            // becomes the FString-on-disk byte sequence the scanner will
            // look for; each new key becomes the byte sequence we'll
            // splice in (padded to identical length).
            var rewrites = new List<KeyRewrite>(replacements.Count);
            foreach (var kv in replacements)
            {
                if (string.IsNullOrEmpty(kv.Key)) continue;
                if (kv.Value == null)
                    throw new ArgumentException("replacement value for '" + kv.Key + "' is null");
                rewrites.Add(KeyRewrite.Create(kv.Key, kv.Value));
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
                foreach (var r in rewrites)
                {
                    int hits = RewriteAllOccurrences(raw.Data, r);
                    if (hits > 0)
                    {
                        perKeyHits[r.VanillaKey] = perKeyHits[r.VanillaKey] + hits;
                        touched = true;
                        LogLine("  FText[" + i + "] '" + r.VanillaKey + "' -> '" + r.NewKey + "' (" + hits + " occurrence" + (hits == 1 ? "" : "s") + ")");
                    }
                }
                if (touched) rawExportsTouched++;
            }

            if (rawExportsTouched > 0)
            {
                LogLine("  Writing FText-key-patched asset: " + assetPath);
                asset.Write(assetPath);
            }
            else
            {
                LogLine("  (no FText keys touched - asset bytes unchanged)");
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

        // Walk one byte array and replace every FString-encoded
        // instance of vanillaBytes with newBytes. Returns hit count.
        // Same-length splice keeps the byte array size identical; the
        // 4-byte length prefix gets re-written too (a no-op since old
        // and new have the same length, but explicit so future variable-
        // length code paths can hang off the same scaffolding).
        static int RewriteAllOccurrences(byte[] data, KeyRewrite r)
        {
            int hits = 0;
            int prefixLen = 4;                       // FString length prefix bytes
            int totalLen  = prefixLen + r.OnDiskLength;

            for (int i = 0; i <= data.Length - totalLen; i++)
            {
                // Match length prefix (signed little-endian int32).
                if (data[i]     != r.LenLE[0]) continue;
                if (data[i + 1] != r.LenLE[1]) continue;
                if (data[i + 2] != r.LenLE[2]) continue;
                if (data[i + 3] != r.LenLE[3]) continue;

                // Match string body (UTF-8 + null terminator).
                bool match = true;
                for (int j = 0; j < r.VanillaBytes.Length; j++)
                {
                    if (data[i + prefixLen + j] != r.VanillaBytes[j]) { match = false; break; }
                }
                if (!match) continue;
                if (data[i + prefixLen + r.VanillaBytes.Length] != 0) continue;

                // Splice in new key. Same length means no shift.
                Buffer.BlockCopy(r.NewBytes, 0, data, i + prefixLen, r.NewBytes.Length);
                // Null terminator stays in place at i + prefixLen + length - 1
                // (we never wrote past it - new key is same length).
                hits++;

                // Skip past the patched bytes to avoid re-matching inside
                // (defensive; doesn't happen because new key differs from
                // vanilla, but cheap to be explicit).
                i += totalLen - 1;
            }
            return hits;
        }

        void LogLine(string msg) { if (Log != null) Log(msg); }

        // Pre-computed byte-level patterns for one (vanilla -> new) pair.
        // Caching the byte arrays + length prefix lets the inner scanner
        // do a flat memcmp-style match without per-byte UTF-8 encoding.
        readonly struct KeyRewrite
        {
            public readonly string VanillaKey;
            public readonly string NewKey;
            // UTF-8 bytes of the vanilla key WITHOUT null terminator
            // (we match the null separately so we can fail-fast on the
            // length prefix first).
            public readonly byte[] VanillaBytes;
            // UTF-8 bytes of the new key, padded with '_' to match
            // VanillaBytes.Length exactly. WITHOUT null terminator
            // (we don't overwrite the existing null terminator byte).
            public readonly byte[] NewBytes;
            // OnDiskLength = VanillaBytes.Length + 1 (the null
            // terminator counts in the FString length prefix per UE
            // serialization rules for positive-length encoding).
            public readonly int OnDiskLength;
            // Little-endian 4-byte encoding of OnDiskLength.
            public readonly byte[] LenLE;

            KeyRewrite(string vanillaKey, string newKey,
                       byte[] vBytes, byte[] nBytes, int onDiskLen, byte[] lenLE)
            {
                VanillaKey = vanillaKey;
                NewKey = newKey;
                VanillaBytes = vBytes;
                NewBytes = nBytes;
                OnDiskLength = onDiskLen;
                LenLE = lenLE;
            }

            public static KeyRewrite Create(string vanillaKey, string newKey)
            {
                if (string.IsNullOrEmpty(vanillaKey))
                    throw new ArgumentException("vanillaKey must not be empty");
                if (newKey == null)
                    throw new ArgumentException("newKey must not be null");

                var vBytes = Encoding.UTF8.GetBytes(vanillaKey);
                var nBytesRaw = Encoding.UTF8.GetBytes(newKey);
                if (nBytesRaw.Length > vBytes.Length)
                {
                    throw new InvalidOperationException(
                        "FText key rewrite: new key '" + newKey + "' (" + nBytesRaw.Length
                        + " bytes) is longer than vanilla key '" + vanillaKey + "' ("
                        + vBytes.Length + " bytes). Same-length-in-place rewrite is not "
                        + "possible. Shorten the new key (e.g. fewer characters in the "
                        + "BuildingId) or extend FTextKeyRewriter to support length-changing "
                        + "splices (would require SerialSize / export-offset fixups).");
                }
                // Pad nBytes with '_' (0x5F) to match vBytes length.
                var nBytes = new byte[vBytes.Length];
                Buffer.BlockCopy(nBytesRaw, 0, nBytes, 0, nBytesRaw.Length);
                for (int j = nBytesRaw.Length; j < nBytes.Length; j++) nBytes[j] = 0x5F; // '_'

                int onDiskLen = vBytes.Length + 1; // includes null terminator
                var lenLE = new byte[4];
                lenLE[0] = (byte)(onDiskLen & 0xFF);
                lenLE[1] = (byte)((onDiskLen >> 8) & 0xFF);
                lenLE[2] = (byte)((onDiskLen >> 16) & 0xFF);
                lenLE[3] = (byte)((onDiskLen >> 24) & 0xFF);

                return new KeyRewrite(vanillaKey, newKey, vBytes, nBytes, onDiskLen, lenLE);
            }
        }
    }

    public sealed class FTextKeyRewriteResult
    {
        // Per-vanilla-key occurrence counts (>= 0).
        public Dictionary<string, int> PerKeyHits;
        // Subset of replacement keys with 0 hits - vanilla bytes weren't
        // found. Empty list = all keys hit at least once.
        public List<string> Missed = new List<string>();
        // Number of RawExports that had at least one rewrite. Used to
        // decide whether to re-write the file (0 = skip the I/O).
        public int RawExportsTouched;
    }

    // Shared utility for constructing the per-building FText keys that
    // both BuildingPatcher (for the binary rewrite) and
    // BuildingItemsCsvPatcher (for the CSV row synthesis) use. Single
    // source of truth so the two sides cannot drift.
    //
    // Shape: "<BuildingId>" core (the GUI-generated id already carries
    // the QmBldg_ prefix), then padding '_' to fill the vanilla key's
    // byte count, then the suffix "_Name" / "_Description". Same-byte-
    // length is required so the binary in-place rewrite in
    // FTextKeyRewriter works without offset recomputation.
    //
    // The padding lives BETWEEN the core and the suffix so the key
    // still has a recognisable shape ("<id>_______Name") for anyone
    // debugging a raw CSV row or asset hex dump.
    public static class BuildingFTextKey
    {
        // Produces a per-building key of EXACTLY vanillaKey.Length bytes.
        // Throws if buildingId + suffix is longer than the vanilla key
        // allows (no truncation - prefer surfacing the configuration
        // problem to silently producing a wrong key the engine then
        // can't resolve).
        public static string Build(string vanillaKey, string buildingId, string suffix)
        {
            if (string.IsNullOrEmpty(vanillaKey))
                throw new ArgumentException("vanillaKey is required");
            if (string.IsNullOrEmpty(buildingId))
                throw new ArgumentException("buildingId is required");
            if (string.IsNullOrEmpty(suffix))
                throw new ArgumentException("suffix is required");
            int padLen = vanillaKey.Length - buildingId.Length - suffix.Length;
            if (padLen < 0)
            {
                throw new InvalidOperationException(
                    "Building '" + buildingId + "' cannot fit a same-length FText key: "
                    + "vanilla key '" + vanillaKey + "' (" + vanillaKey.Length
                    + " chars) is shorter than required buildingId+suffix '"
                    + buildingId + suffix + "' ("
                    + (buildingId.Length + suffix.Length) + " chars). "
                    + "Use a shorter BuildingId.");
            }
            return buildingId + new string('_', padLen) + suffix;
        }
    }
}
