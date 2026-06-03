using System;
using System.Globalization;
using System.IO;
using UAssetAPI;
using UAssetAPI.ExportTypes;
using UAssetAPI.Unversioned;

namespace Windrose.Quartermaster.Core
{
    // Patches CurveTable GE-value rows in the ItemsLogic tree, where the value lives
    // in a shared curve table rather than as an inline float on a GameplayEffect.
    // Two uses today:
    //   - Weapon special-ability cooldowns (Patch, by exact row name) in CT_Weapon_GE_Values.
    //   - Food/drink buff durations (PatchRowsBySuffix, all "_Duration" rows) in
    //     CT_Food_GE_Values. Both tables share the identical per-row layout below.
    //
    // Consumable cooldowns (CooldownsPatcher) carry their value inline as a
    // DurationMagnitude.ScalableFloatMagnitude.Value FloatProperty. Weapon ability
    // cooldowns instead live in the shared curve table CT_Weapon_GE_Values: the GE
    // (e.g. GE_Wpn_TwoHand_Souldrinker_Base_SoulHarvest_Cooldown) reads its
    // DurationMagnitude from a CurveTable row, so the GE has no value to patch - we
    // edit the curve row directly. Editing the table preserves every other row, so
    // shipping the patched table in the mod pak only changes the targeted ability.
    //
    // UAssetAPI 1.1.0 has no UCurveTable parser: the entire RowMap is exposed as the
    // export's raw `Extras` byte blob. Verified layout (UE5.6, R5 build) - every row in
    // this table is a single-key constant curve serialized as:
    //   FName  RowName     int32 nameMapIndex, int32 number(=0)        8 bytes
    //   <curve header>     constant bytes (00 0B 01 01 00 00 00)       7 bytes
    //   float  Time(=1.0)  at RowFName + 15
    //   float  Value       at RowFName + 19   <- the cooldown, in seconds
    // We anchor on the (unique) row FName, sanity-check the structure, then scale Value
    // in place and let UAssetAPI re-serialize (verified byte-stable round-trip).
    public sealed class WeaponAbilityCooldownPatcher
    {
        public const double MinMultiplier = 0.01;
        public const double MaxMultiplier = 3.00;

        public const string CurveTableStem = "CT_Weapon_GE_Values";
        public const string CurveTableVirtualPath =
            "R5/Content/Gameplay/ItemsLogic/Weapon/Shared/CT_Weapon_GE_Values.uasset";

        // Soul Eater = Souldrinker greatsword; [F] "Soul Harvest" cooldown (vanilla 180s).
        public const string SoulEaterRow = "Greatsword_Souldrinker_AbilityCooldown";

        // Food/drink buff durations: every "Food_<X>_Duration" row in this shared table is
        // the buff lifetime in seconds (vanilla 420/900/1800s). Scaling them all = the
        // "Extended Food Duration" feature. Non-Duration rows (MaxHealth, regen, attribute
        // buffs) live in the same table and must stay untouched - hence the suffix match.
        public const string FoodCurveTableStem = "CT_Food_GE_Values";
        public const string FoodCurveTableVirtualPath =
            "R5/Content/Gameplay/ItemsLogic/Consumables/CT_Food_GE_Values.uasset";
        public const string DurationRowSuffix = "_Duration";

        // Per-row offsets from the row's FName start (see class comment).
        const int RowFNameSize   = 8;
        const int RowTimeOffset  = 15;
        const int RowValueOffset = 19;

        public Action<string> Log;

        public CooldownPatchResult Patch(
            string inputAssetPath, string outputAssetPath,
            string usmapPath, string rowName, double multiplier)
        {
            ValidateArgs(inputAssetPath, outputAssetPath, usmapPath, rowName, multiplier);

            LogLine("Loading usmap: " + usmapPath);
            var mappings = new Usmap(usmapPath);
            LogLine("Loading uasset: " + inputAssetPath);
            var asset = new UAsset(inputAssetPath, UAssetIo.Ue, mappings);

            var (exportIndex, extras) = FindCurveTableExtras(asset, inputAssetPath);

            int nameIndex = FindNameMapIndex(asset, rowName);
            if (nameIndex < 0)
                throw new InvalidOperationException(
                    "CurveTable " + inputAssetPath + " has no name-map entry '" + rowName
                    + "' - the row does not exist in this table (game update?).");

            int anchor = FindUniqueRowAnchor(extras, nameIndex, rowName, inputAssetPath);

            int valueOffset = anchor + RowValueOffset;
            if (valueOffset + 4 > extras.Length)
                throw new InvalidOperationException(
                    "Row '" + rowName + "' value field runs past the curve blob in "
                    + inputAssetPath + " - the curve format changed; refusing to patch.");

            // Structural guard: a single-key curve has a small finite Time at +15.
            // If the row layout ever shifts, this reads garbage and we refuse rather
            // than silently corrupting an unrelated float.
            float time = BitConverter.ToSingle(extras, anchor + RowTimeOffset);
            if (!IsFinite(time) || time < 0f || time > 100000f)
                throw new InvalidOperationException(
                    "Row '" + rowName + "' does not match the expected single-key curve layout "
                    + "(Time=" + time.ToString(CultureInfo.InvariantCulture) + ") in "
                    + inputAssetPath + " - the curve format changed; refusing to patch.");

            float vanillaValue = BitConverter.ToSingle(extras, valueOffset);
            if (!IsFinite(vanillaValue) || vanillaValue <= 0f || vanillaValue > 1000000f)
                throw new InvalidOperationException(
                    "Row '" + rowName + "' value "
                    + vanillaValue.ToString(CultureInfo.InvariantCulture)
                    + " is not a plausible cooldown in " + inputAssetPath
                    + " - refusing to patch.");

            float newValue = (float)(vanillaValue * multiplier);
            BitConverter.GetBytes(newValue).CopyTo(extras, valueOffset);
            LogLine("Updated CurveTable row '" + rowName + "' Value: "
                + vanillaValue.ToString("0.0000", CultureInfo.InvariantCulture)
                + " -> " + newValue.ToString("0.0000", CultureInfo.InvariantCulture)
                + " (multiplier=" + multiplier.ToString("0.##", CultureInfo.InvariantCulture) + ")");

            LogLine("Writing: " + outputAssetPath);
            asset.Write(outputAssetPath);

            return new CooldownPatchResult
            {
                AssetStem = Path.GetFileNameWithoutExtension(inputAssetPath),
                ExportIndex = exportIndex,
                Multiplier = multiplier,
                VanillaValue = vanillaValue,
                EffectiveValue = newValue,
                Shape = CooldownPatchShape.WeaponAbilityCurve,
            };
        }

        // Scales the magnitude (first key Value) of every curve row whose name ends with
        // `suffix` (e.g. "_Duration" in CT_Food_GE_Values). Non-matching rows (other GE
        // values in the same table) are left byte-identical. The single-row Patch() above
        // byte-scans for one unique row FName; that is ambiguous across the many rows here,
        // so this walks the cooked SimpleCurve table sequentially instead (same field layout
        // the recon dumper verified) and patches each matching row's first key in place.
        public CooldownPatchResult PatchRowsBySuffix(
            string inputAssetPath, string outputAssetPath,
            string usmapPath, string suffix, double multiplier)
        {
            ValidateArgs(inputAssetPath, outputAssetPath, usmapPath, suffix, multiplier);

            LogLine("Loading usmap: " + usmapPath);
            var mappings = new Usmap(usmapPath);
            LogLine("Loading uasset: " + inputAssetPath);
            var asset = new UAsset(inputAssetPath, UAssetIo.Ue, mappings);

            var (exportIndex, extras) = FindCurveTableExtras(asset, inputAssetPath);
            var names = asset.GetNameMapIndexList();

            int rowsPatched = 0;
            string sampleRow = null;
            float sampleVanilla = 0f, sampleEffective = 0f;

            // Cooked single-key constant SimpleCurve rows are a fixed 29-byte record (verified:
            // blob length == 5-byte table header + NumRows * 29):
            //   FName (int32 NameIdx, int32 NameNumber)           8 bytes  [+0..+7]
            //   constant curve header  00 0B 01 01 00 00 00       7 bytes  [+8..+14]
            //   float Time (=1.0)                                 4 bytes  [+15..+18]
            //   float Value (the magnitude, in seconds)           4 bytes  [+19..+22]  <- scaled
            //   float DefaultValue                                4 bytes  [+23..+26]
            //   byte PreInfinityExtrap, byte PostInfinityExtrap   2 bytes  [+27..+28]
            const int RowSize = RowValueOffset + 4 + 4 + 2; // value end + DefaultValue + 2 extrap = 29
            int pos = 0;
            int numRows = ReadI32(extras, inputAssetPath, ref pos);
            if (numRows < 0 || numRows > 1000000)
                throw new InvalidOperationException(
                    "CurveTable in " + inputAssetPath + " reports an implausible row count ("
                    + numRows + ") - the table format changed; refusing to patch.");
            NeedBytes(extras, pos, 1, inputAssetPath);
            pos += 1; // ECurveTableMode (1 = SimpleCurves)

            for (int r = 0; r < numRows; r++)
            {
                int rowStart = pos;
                NeedBytes(extras, rowStart, RowSize, inputAssetPath);

                // Integrity guard: every row carries the same constant curve header. If a game
                // update reshapes the rows, this trips before we scale the wrong bytes.
                if (!(extras[rowStart + 8] == 0x00 && extras[rowStart + 9] == 0x0B
                      && extras[rowStart + 10] == 0x01 && extras[rowStart + 11] == 0x01
                      && extras[rowStart + 12] == 0x00 && extras[rowStart + 13] == 0x00
                      && extras[rowStart + 14] == 0x00))
                    throw new InvalidOperationException(
                        "Row #" + r + " in " + inputAssetPath + " does not carry the expected "
                        + "single-key curve header - the table format changed; refusing to patch.");

                int nameIdx = BitConverter.ToInt32(extras, rowStart);
                int nameNum = BitConverter.ToInt32(extras, rowStart + 4);
                string baseName = (nameIdx >= 0 && nameIdx < names.Count) ? names[nameIdx]?.Value : null;
                string rowName = baseName == null ? null
                    : (nameNum > 0 ? baseName + "_" + (nameNum - 1) : baseName);

                pos += RowSize;

                if (rowName == null || !rowName.EndsWith(suffix, StringComparison.Ordinal)) continue;

                int valueOffset = rowStart + RowValueOffset;
                float time = BitConverter.ToSingle(extras, rowStart + RowTimeOffset);
                if (!IsFinite(time) || time < 0f || time > 100000f)
                    throw new InvalidOperationException(
                        "Row '" + rowName + "' Time=" + time.ToString(CultureInfo.InvariantCulture)
                        + " is not the expected single-key curve layout in " + inputAssetPath
                        + " - the curve format changed; refusing to patch.");

                float vanillaValue = BitConverter.ToSingle(extras, valueOffset);
                if (!IsFinite(vanillaValue) || vanillaValue <= 0f || vanillaValue > 1000000f)
                    throw new InvalidOperationException(
                        "Row '" + rowName + "' value "
                        + vanillaValue.ToString(CultureInfo.InvariantCulture)
                        + " is not a plausible duration in " + inputAssetPath
                        + " - refusing to patch.");

                float newValue = (float)(vanillaValue * multiplier);
                BitConverter.GetBytes(newValue).CopyTo(extras, valueOffset);
                rowsPatched++;
                if (sampleRow == null)
                {
                    sampleRow = rowName;
                    sampleVanilla = vanillaValue;
                    sampleEffective = newValue;
                }
            }

            // The fixed-stride walk must consume the whole blob exactly; otherwise the row-size
            // assumption is wrong and we must not ship a half-rewritten table.
            if (pos != extras.Length)
                throw new InvalidOperationException(
                    "CurveTable walk in " + inputAssetPath + " ended at " + pos + " of "
                    + extras.Length + " bytes - the table layout changed; refusing to patch.");

            if (rowsPatched == 0)
                throw new InvalidOperationException(
                    "No curve rows ending in '" + suffix + "' found in " + inputAssetPath
                    + " - the table layout changed (game update?); refusing to patch.");

            LogLine("Updated " + rowsPatched + " '" + suffix + "' curve row"
                + (rowsPatched == 1 ? "" : "s") + " (multiplier="
                + multiplier.ToString("0.##", CultureInfo.InvariantCulture) + "); sample "
                + sampleRow + " "
                + sampleVanilla.ToString("0.0000", CultureInfo.InvariantCulture) + " -> "
                + sampleEffective.ToString("0.0000", CultureInfo.InvariantCulture));

            LogLine("Writing: " + outputAssetPath);
            asset.Write(outputAssetPath);

            return new CooldownPatchResult
            {
                AssetStem = Path.GetFileNameWithoutExtension(inputAssetPath),
                ExportIndex = exportIndex,
                Multiplier = multiplier,
                VanillaValue = sampleVanilla,
                EffectiveValue = sampleEffective,
                RowsPatched = rowsPatched,
                Shape = CooldownPatchShape.FoodDurationCurve,
            };
        }

        // Curve tables expose their RowMap as a single CurveTable export whose data
        // sits entirely in the raw Extras blob (UAssetAPI has no UCurveTable parser).
        static (int index, byte[] extras) FindCurveTableExtras(UAsset asset, string path)
        {
            for (int i = 0; i < asset.Exports.Count; i++)
            {
                var e = asset.Exports[i];
                string cls = null;
                try { cls = e.GetExportClassType()?.Value?.Value; } catch { }
                if (string.Equals(cls, "CurveTable", StringComparison.Ordinal)
                    && e.Extras != null && e.Extras.Length > 0)
                    return (i, e.Extras);
            }
            // Fallback: the first export carrying a raw curve blob.
            for (int i = 0; i < asset.Exports.Count; i++)
            {
                var e = asset.Exports[i];
                if (e.Extras != null && e.Extras.Length > 0) return (i, e.Extras);
            }
            throw new InvalidOperationException(
                "No CurveTable export with a curve blob found in " + path + ".");
        }

        static int FindNameMapIndex(UAsset asset, string name)
        {
            var nmap = asset.GetNameMapIndexList();
            for (int i = 0; i < nmap.Count; i++)
                if (string.Equals(nmap[i].Value, name, StringComparison.Ordinal)) return i;
            return -1;
        }

        // A row FName is serialized as (int32 nameMapIndex, int32 number==0). The row
        // name is unique in the table, so exactly one anchor must match.
        static int FindUniqueRowAnchor(byte[] extras, int nameIndex, string rowName, string path)
        {
            if (TryFindRowAnchor(extras, nameIndex, out int anchor, out int count))
                return anchor;
            if (count == 0)
                throw new InvalidOperationException(
                    "Row '" + rowName + "' FName not found in the curve blob of " + path + ".");
            throw new InvalidOperationException(
                "Row '" + rowName + "' FName appears " + count + " times in the curve blob of "
                + path + " - ambiguous; refusing to patch.");
        }

        // Returns true (with the first anchor) only when exactly one match exists; out count
        // carries the raw match count so callers can distinguish missing (0) from ambiguous (>1).
        static bool TryFindRowAnchor(byte[] extras, int nameIndex, out int anchor, out int count)
        {
            anchor = -1; count = 0;
            for (int off = 0; off + RowFNameSize <= extras.Length; off++)
            {
                if (BitConverter.ToInt32(extras, off) == nameIndex
                    && BitConverter.ToInt32(extras, off + 4) == 0)
                {
                    count++;
                    if (anchor < 0) anchor = off;
                }
            }
            return count == 1;
        }

        static bool IsFinite(float f)
        {
            return !float.IsNaN(f) && !float.IsInfinity(f);
        }

        static void NeedBytes(byte[] b, int pos, int n, string path)
        {
            if (pos < 0 || pos + n > b.Length)
                throw new InvalidOperationException(
                    "Curve blob in " + path + " is truncated (need " + n + " bytes at "
                    + pos + " of " + b.Length + ") - the table format changed; refusing to patch.");
        }

        static int ReadI32(byte[] b, string path, ref int pos)
        {
            NeedBytes(b, pos, 4, path);
            int v = BitConverter.ToInt32(b, pos);
            pos += 4;
            return v;
        }

        static void ValidateArgs(string input, string output, string usmap, string rowName, double multiplier)
        {
            if (string.IsNullOrEmpty(input))
                throw new ArgumentNullException("inputAssetPath");
            if (string.IsNullOrEmpty(output))
                throw new ArgumentNullException("outputAssetPath");
            if (string.IsNullOrEmpty(usmap))
                throw new ArgumentNullException("usmapPath");
            if (string.IsNullOrEmpty(rowName))
                throw new ArgumentNullException("rowName");
            if (!File.Exists(input))
                throw new FileNotFoundException("Legacy uasset not found: " + input);
            if (!File.Exists(usmap))
                throw new FileNotFoundException("Usmap mappings not found: " + usmap);
            if (multiplier < MinMultiplier || multiplier > MaxMultiplier)
                throw new ArgumentOutOfRangeException("multiplier",
                    "Multiplier " + multiplier + " is outside ["
                    + MinMultiplier + ", " + MaxMultiplier
                    + "] - the GUI should have clamped this.");
        }

        void LogLine(string msg)
        {
            if (Log != null) Log(msg);
        }
    }
}
