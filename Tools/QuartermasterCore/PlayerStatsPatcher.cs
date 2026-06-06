using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using UAssetAPI;
using UAssetAPI.ExportTypes;
using UAssetAPI.Unversioned;

namespace Windrose.Quartermaster.Core
{
    // "More Stamina (+ Health)": scales the player's Health and Stamina base
    // attributes in the shared CurveTable CT_CharactersAttributes.
    //
    // The reference mod ("More Stamina 5x") multiplied three Hero rows by 5 -
    // Hero_Stamina (start value), Hero_MaxStamina (the bar) and Hero_StaminaRegRate
    // (refill speed). We generalise that to a per-group MULTIPLIER applied on top of
    // the freshly-extracted vanilla values (no drift across game updates) and add an
    // independent Health group:
    //   Stamina group: Hero_Stamina, Hero_MaxStamina, Hero_StaminaRegRate
    //   Health  group: Hero_Health,  Hero_MaxHealth
    // Health has no scalable regen row (Hero_PassHPReg is 0 in vanilla, so x N stays
    // 0), hence two rows vs. stamina's three. Start and Max are scaled together so the
    // bar is full at spawn (matching the reference behaviour).
    //
    // CT_CharactersAttributes is a CurveTable; UAssetAPI 1.1.0 exposes its RowMap as
    // the export's raw Extras blob. Vanilla serialises every row as the same fixed
    // 29-byte single-key constant SimpleCurve the WeaponAbilityCooldownPatcher already
    // walks:
    //   FName (int32 NameIdx, int32 NameNumber)           8 bytes  [+0..+7]
    //   constant curve header  00 0B 01 01 00 00 00       7 bytes  [+8..+14]
    //   float Time (=1.0)                                 4 bytes  [+15..+18]
    //   float Value (the magnitude)                       4 bytes  [+19..+22]  <- scaled
    //   float DefaultValue                                4 bytes  [+23..+26]
    //   byte PreInfinityExtrap, byte PostInfinityExtrap   2 bytes  [+27..+28]
    // We walk the table sequentially, scale the Value of each targeted row in place and
    // let UAssetAPI re-serialise (verified byte-stable round-trip).
    public sealed class PlayerStatsPatcher
    {
        // Safety backstop only ("the GUI should have clamped this"); the per-slider UI
        // range (1x-10x) is the real limit.
        public const double MinMultiplier = 0.1;
        public const double MaxMultiplier = 10.0;

        public const string AssetStem = "CT_CharactersAttributes";
        public const string AssetVirtualPath =
            "R5/Content/Gameplay/Character/Player/Parameters/CT_CharactersAttributes.uasset";

        // Player stat rows scaled by each group. Hero_Health/Hero_Stamina are the
        // current/start values, Hero_Max* the bar size - both scaled so the bar spawns
        // full. Hero_StaminaRegRate is the refill speed (vanilla 40); there is no
        // health-regen analogue worth scaling (Hero_PassHPReg is 0 in vanilla).
        public static readonly string[] HealthRows =
            { "Hero_Health", "Hero_MaxHealth" };
        public static readonly string[] StaminaRows =
            { "Hero_Stamina", "Hero_MaxStamina", "Hero_StaminaRegRate" };

        const int RowFNameSize   = 8;
        const int RowTimeOffset  = 15;
        const int RowValueOffset = 19;
        const int RowSize        = RowValueOffset + 4 + 4 + 2; // value end + DefaultValue + 2 extrap = 29

        public Action<string> Log;

        // Scales the Value of every targeted Hero row in `inputAssetPath` and writes the
        // result to `outputAssetPath`. A group whose multiplier is ~1.0 is skipped, so
        // missing rows for an inactive group never trip the "row not found" guard.
        public PlayerStatsPatchResult Patch(
            string inputAssetPath, string outputAssetPath, string usmapPath,
            double healthMultiplier, double staminaMultiplier)
        {
            if (string.IsNullOrEmpty(inputAssetPath)) throw new ArgumentNullException("inputAssetPath");
            if (string.IsNullOrEmpty(outputAssetPath)) throw new ArgumentNullException("outputAssetPath");
            if (string.IsNullOrEmpty(usmapPath)) throw new ArgumentNullException("usmapPath");
            if (!File.Exists(inputAssetPath))
                throw new FileNotFoundException("Legacy uasset not found: " + inputAssetPath);
            if (!File.Exists(usmapPath))
                throw new FileNotFoundException("Usmap not found: " + usmapPath);
            ClampGuard(healthMultiplier, "health");
            ClampGuard(staminaMultiplier, "stamina");

            // Build the exact row -> multiplier map for the active groups only.
            var targets = new Dictionary<string, double>(StringComparer.Ordinal);
            bool healthActive = Math.Abs(healthMultiplier - 1.0) > 1e-9;
            bool staminaActive = Math.Abs(staminaMultiplier - 1.0) > 1e-9;
            if (healthActive)
                foreach (var r in HealthRows) targets[r] = healthMultiplier;
            if (staminaActive)
                foreach (var r in StaminaRows) targets[r] = staminaMultiplier;
            if (targets.Count == 0)
                throw new InvalidOperationException(
                    "PlayerStats: neither health nor stamina multiplier differs from vanilla - "
                    + "nothing to patch (the pipeline should have gated this).");

            LogLine("Loading usmap: " + usmapPath);
            var mappings = new Usmap(usmapPath);
            LogLine("Loading uasset: " + inputAssetPath);
            var asset = new UAsset(inputAssetPath, UAssetIo.Ue, mappings);

            var extras = FindCurveTableExtras(asset, inputAssetPath);
            var names = asset.GetNameMapIndexList();

            var healthSet = new HashSet<string>(HealthRows, StringComparer.Ordinal);
            var patched = new HashSet<string>(StringComparer.Ordinal);
            int healthRowsPatched = 0, staminaRowsPatched = 0;
            float staminaSampleVanilla = 0f, staminaSampleEffective = 0f;
            float healthSampleVanilla = 0f, healthSampleEffective = 0f;

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

                // Integrity guard: every vanilla row carries the same constant curve
                // header. If a game update reshapes the rows, this trips before we scale
                // the wrong bytes.
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

                if (rowName == null || !targets.TryGetValue(rowName, out double mult)) continue;

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
                        + " is not a plausible player attribute in " + inputAssetPath
                        + " - refusing to patch.");

                float newValue = (float)(vanillaValue * mult);
                BitConverter.GetBytes(newValue).CopyTo(extras, valueOffset);
                patched.Add(rowName);

                if (healthSet.Contains(rowName))
                {
                    if (healthRowsPatched == 0) { healthSampleVanilla = vanillaValue; healthSampleEffective = newValue; }
                    healthRowsPatched++;
                }
                else
                {
                    if (staminaRowsPatched == 0) { staminaSampleVanilla = vanillaValue; staminaSampleEffective = newValue; }
                    staminaRowsPatched++;
                }
            }

            // Fixed-stride walk must consume the whole blob exactly; otherwise the
            // row-size assumption is wrong and we must not ship a half-rewritten table.
            if (pos != extras.Length)
                throw new InvalidOperationException(
                    "CurveTable walk in " + inputAssetPath + " ended at " + pos + " of "
                    + extras.Length + " bytes - the table layout changed; refusing to patch.");

            // Every targeted row of an active group must exist (game update guard).
            foreach (var t in targets.Keys)
                if (!patched.Contains(t))
                    throw new InvalidOperationException(
                        "PlayerStats: expected row '" + t + "' not found in " + inputAssetPath
                        + " - the player attribute table changed (game update?); refusing to patch.");

            if (healthActive)
                LogLine("PlayerStats: scaled " + healthRowsPatched + " health row(s) by "
                        + healthMultiplier.ToString("0.##", CultureInfo.InvariantCulture) + "x; sample "
                        + healthSampleVanilla.ToString("0.##", CultureInfo.InvariantCulture) + " -> "
                        + healthSampleEffective.ToString("0.##", CultureInfo.InvariantCulture));
            if (staminaActive)
                LogLine("PlayerStats: scaled " + staminaRowsPatched + " stamina row(s) by "
                        + staminaMultiplier.ToString("0.##", CultureInfo.InvariantCulture) + "x; sample "
                        + staminaSampleVanilla.ToString("0.##", CultureInfo.InvariantCulture) + " -> "
                        + staminaSampleEffective.ToString("0.##", CultureInfo.InvariantCulture));

            LogLine("Writing: " + outputAssetPath);
            asset.Write(outputAssetPath);

            return new PlayerStatsPatchResult
            {
                HealthMultiplier = healthActive ? healthMultiplier : 1.0,
                StaminaMultiplier = staminaActive ? staminaMultiplier : 1.0,
                HealthRowsPatched = healthRowsPatched,
                StaminaRowsPatched = staminaRowsPatched,
            };
        }

        // Curve tables expose their RowMap as a single CurveTable export whose data
        // sits entirely in the raw Extras blob (UAssetAPI has no UCurveTable parser).
        static byte[] FindCurveTableExtras(UAsset asset, string path)
        {
            for (int i = 0; i < asset.Exports.Count; i++)
            {
                var e = asset.Exports[i];
                string cls = null;
                try { cls = e.GetExportClassType()?.Value?.Value; } catch { }
                if (string.Equals(cls, "CurveTable", StringComparison.Ordinal)
                    && e.Extras != null && e.Extras.Length > 0)
                    return e.Extras;
            }
            for (int i = 0; i < asset.Exports.Count; i++)
            {
                var e = asset.Exports[i];
                if (e.Extras != null && e.Extras.Length > 0) return e.Extras;
            }
            throw new InvalidOperationException(
                "No CurveTable export with a curve blob found in " + path + ".");
        }

        static void ClampGuard(double multiplier, string which)
        {
            if (!(multiplier >= MinMultiplier && multiplier <= MaxMultiplier))
                throw new ArgumentOutOfRangeException(
                    which + "Multiplier",
                    "PlayerStats " + which + " multiplier " + multiplier
                    + " is outside the allowed range [" + MinMultiplier + ", " + MaxMultiplier + "].");
        }

        static bool IsFinite(float f) => !float.IsNaN(f) && !float.IsInfinity(f);

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

        void LogLine(string msg)
        {
            if (Log != null) Log(msg);
        }
    }

    public sealed class PlayerStatsPatchResult
    {
        public double HealthMultiplier;
        public double StaminaMultiplier;
        public int HealthRowsPatched;
        public int StaminaRowsPatched;
    }
}
