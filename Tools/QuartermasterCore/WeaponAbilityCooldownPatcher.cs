using System;
using System.Globalization;
using System.IO;
using UAssetAPI;
using UAssetAPI.ExportTypes;
using UAssetAPI.Unversioned;

namespace Windrose.Quartermaster.Core
{
    // Patches a weapon special-ability cooldown that is stored as a CurveTable row,
    // not as an inline float on a GameplayEffect.
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
            int anchor = -1, count = 0;
            for (int off = 0; off + RowFNameSize <= extras.Length; off++)
            {
                if (BitConverter.ToInt32(extras, off) == nameIndex
                    && BitConverter.ToInt32(extras, off + 4) == 0)
                {
                    count++;
                    if (anchor < 0) anchor = off;
                }
            }
            if (count == 0)
                throw new InvalidOperationException(
                    "Row '" + rowName + "' FName not found in the curve blob of " + path + ".");
            if (count > 1)
                throw new InvalidOperationException(
                    "Row '" + rowName + "' FName appears " + count + " times in the curve blob of "
                    + path + " - ambiguous; refusing to patch.");
            return anchor;
        }

        static bool IsFinite(float f)
        {
            return !float.IsNaN(f) && !float.IsInfinity(f);
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
