using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

namespace Windrose.Quartermaster.Core
{
    // "Faster Ships": scales the motor-force response curve of each ship class so
    // the vessel accelerates harder and reaches a higher top speed. Every ship's
    // throttle->thrust mapping lives in a UCurveFloat (CRV_<Ship>Motor and its
    // AI/Service/faction siblings) under
    //   /Game/Gameplay/Water/Character/Params/Physics/<Ship>Curves/.
    // The curve is a FRichCurve: keys map time (-0.2 = full reverse .. 1 = full
    // throttle) to an output force value. The reference mod made ships faster by
    // raising those output values; we reproduce the mechanism generically by
    // multiplying every key's Value (and its tangents, to preserve curve shape) by
    // the user's per-curve multiplier.
    //
    // Why we derive the override from VANILLA (not from a third-party mod file):
    // the edit is a pure same-size float overwrite inside the freshly-extracted
    // vanilla .uexp - we read each key, multiply, and write the float back at the
    // same offset. Nothing is inserted or removed, the .uasset summary is left
    // untouched, and the result ships nothing but Windrose's own curve plus our
    // arithmetic. Verified end-to-end: vanilla -> scale x2 -> retoc to-zen ->
    // to-legacy round-trips to a curve whose key values are all exactly doubled.
    //
    // FRichCurveKey binary layout (UE5, custom WithSerializer): 3 enum bytes
    // (InterpMode, TangentMode, TangentWeightMode) followed by 6 floats
    // (Time, Value, ArriveTangent, ArriveTangentWeight, LeaveTangent,
    // LeaveTangentWeight) = 27 bytes. The .uexp begins with the unversioned
    // FloatCurve property header, then an int32 key count, then the key array.
    public sealed class ShipSpeedPatcher
    {
        public sealed class CurveInfo
        {
            public string Stem;          // e.g. "CRV_BrigMotor"
            public string VirtualPath;   // cooked virtual path of the .uasset
            public string ShipType;      // group key: "ShallowBoat" / "Brig" / ...
            public string Role;          // "Player" / "AI" / "Service" / "BlackBeard" / ...
            // Highest key Value in vanilla; verified on load as a version-drift gate.
            public float VanillaMaxValue;
        }

        public const double MinMultiplier = 0.1;
        public const double MaxMultiplier = 10.0;

        // FRichCurveKey serialized size and the byte offsets (relative to a key's
        // start) of the three floats we scale.
        const int KeySize = 27;
        const int OffTime = 3;
        const int OffValue = 7;
        const int OffArriveTangent = 11;
        const int OffLeaveTangent = 19;

        const string PhysicsRoot =
            "R5/Content/Gameplay/Water/Character/Params/Physics";

        static CurveInfo Curve(string ship, string dir, string stem, string role, float vanillaMax)
        {
            return new CurveInfo
            {
                Stem = stem,
                VirtualPath = PhysicsRoot + "/" + dir + "/" + stem + ".uasset",
                ShipType = ship,
                Role = role,
                VanillaMaxValue = vanillaMax,
            };
        }

        // The 24 motor-force curves the game ships, grouped by ship class. The four
        // *_Sails_Motor* curves under ModuleEfficiency are deliberately excluded:
        // those are 0..1 efficiency curves, not speed.
        public static readonly List<CurveInfo> Curves = new List<CurveInfo>
        {
            // ShallowBoat (starter rowboat: player + service only, no AI/faction)
            Curve("ShallowBoat", "BoatCurves", "CRV_ShallowBoatMotor",          "Player",     7f),
            Curve("ShallowBoat", "BoatCurves", "CRV_ShallowBoatServiceMotor",   "Service",    0.15f),

            // Brig
            Curve("Brig", "BrigCurves", "CRV_BrigMotor",            "Player",     1950f),
            Curve("Brig", "BrigCurves", "CRV_BrigMotor_BlackBeard", "BlackBeard", 2300f),
            Curve("Brig", "BrigCurves", "CRV_BrigMotor_Brethren",   "Brethren",   1600f),
            Curve("Brig", "BrigCurves", "CRV_BrigServiceMotor",     "Service",    450f),
            Curve("Brig", "BrigCurves", "CRV_AI_BrigMotor",         "AI",         3300f),
            Curve("Brig", "BrigCurves", "CRV_AI_BrigServiceMotor",  "AI Service", 500f),

            // Cutter
            Curve("Cutter", "CutterCurves", "CRV_CutterMotor",           "Player",     3000f),
            Curve("Cutter", "CutterCurves", "CRV_CutterServiceMotor",    "Service",    200f),
            Curve("Cutter", "CutterCurves", "CRV_AI_CutterMotor",        "AI",         2000f),
            Curve("Cutter", "CutterCurves", "CRV_AI_CutterServiceMotor", "AI Service", 200f),

            // Frigate
            Curve("Frigate", "FrigateCurves", "CRV_FrigateMotor",            "Player",     2150f),
            Curve("Frigate", "FrigateCurves", "CRV_FrigateMotor_BlackBeard", "BlackBeard", 2600f),
            Curve("Frigate", "FrigateCurves", "CRV_FrigateMotor_Brethren",   "Brethren",   1750f),
            Curve("Frigate", "FrigateCurves", "CRV_FrigateServiceMotor",     "Service",    1300f),
            Curve("Frigate", "FrigateCurves", "CRV_AI_FrigateMotor",         "AI",         4300f),
            Curve("Frigate", "FrigateCurves", "CRV_AI_FrigateServiceMotor",  "AI Service", 1500f),

            // Ketch
            Curve("Ketch", "KetchCurves", "CRV_KetchMotor",            "Player",     1150f),
            Curve("Ketch", "KetchCurves", "CRV_KetchMotor_BlackBeard", "BlackBeard", 1400f),
            Curve("Ketch", "KetchCurves", "CRV_KetchMotor_Brethren",   "Brethren",   900f),
            Curve("Ketch", "KetchCurves", "CRV_KetchServiceMotor",     "Service",    240f),
            Curve("Ketch", "KetchCurves", "CRV_AI_KetchMotor",         "AI",         2100f),
            Curve("Ketch", "KetchCurves", "CRV_AI_KetchServiceMotor",  "AI Service", 270f),
        };

        public static CurveInfo Find(string stem)
        {
            if (string.IsNullOrEmpty(stem)) return null;
            return Curves.FirstOrDefault(c =>
                string.Equals(c.Stem, stem, StringComparison.OrdinalIgnoreCase));
        }

        public Action<string> Log;

        // Scales the curve's .uexp key values in place. `stagingDir` is the
        // composite legacy root retoc to-legacy populated; `info` identifies the
        // curve (and supplies the version-drift baseline). Returns the patch result.
        public ShipSpeedPatchResult PatchCurve(string stagingDir, double multiplier, CurveInfo info)
        {
            if (string.IsNullOrEmpty(stagingDir)) throw new ArgumentNullException("stagingDir");
            if (info == null) throw new ArgumentNullException("info");
            if (multiplier < MinMultiplier || multiplier > MaxMultiplier)
                throw new ArgumentOutOfRangeException("multiplier",
                    "Multiplier " + multiplier + " is outside ["
                    + MinMultiplier + ", " + MaxMultiplier
                    + "] - the GUI should have clamped this.");

            var relUexp = Path.ChangeExtension(
                info.VirtualPath.Replace('/', Path.DirectorySeparatorChar), ".uexp");
            var uexpPath = Path.Combine(stagingDir, relUexp);

            // Sanity gate: the vanilla curve must have extracted here first.
            if (!File.Exists(uexpPath))
                throw new InvalidOperationException(
                    "Faster Ships: expected the vanilla curve payload at " + uexpPath
                    + " after retoc to-legacy, but it is missing - the game container "
                    + "may have moved the asset (filter '" + info.Stem + "').");

            var bytes = File.ReadAllBytes(uexpPath);

            int keyArrayOffset, keyCount;
            float vanillaMax;
            if (!TryLocateKeyArray(bytes, info.VanillaMaxValue, out keyArrayOffset, out keyCount, out vanillaMax))
            {
                throw new InvalidOperationException(
                    "Faster Ships: could not locate the FRichCurve key array in "
                    + info.Stem + " whose peak value matches the expected vanilla "
                    + "baseline " + info.VanillaMaxValue.ToString(CultureInfo.InvariantCulture)
                    + ". The curve layout or its values may have changed in a game "
                    + "update - re-probe and update ShipSpeedPatcher.Curves[" + info.Stem
                    + "].VanillaMaxValue.");
            }

            float effectiveMax = 0f;
            int p = keyArrayOffset + 4;
            for (int k = 0; k < keyCount; k++)
            {
                ScaleFloatAt(bytes, p + OffValue, multiplier);
                ScaleFloatAt(bytes, p + OffArriveTangent, multiplier);
                ScaleFloatAt(bytes, p + OffLeaveTangent, multiplier);
                float v = BitConverter.ToSingle(bytes, p + OffValue);
                if (Math.Abs(v) > Math.Abs(effectiveMax)) effectiveMax = v;
                p += KeySize;
            }

            File.WriteAllBytes(uexpPath, bytes);

            LogLine(info.Stem + " (" + info.ShipType + "/" + info.Role + "): motor curve x"
                    + multiplier.ToString("0.##", CultureInfo.InvariantCulture)
                    + " (peak " + vanillaMax.ToString("0.##", CultureInfo.InvariantCulture)
                    + " -> " + effectiveMax.ToString("0.##", CultureInfo.InvariantCulture)
                    + ", " + keyCount + " keys)");

            return new ShipSpeedPatchResult
            {
                Stem = info.Stem,
                ShipType = info.ShipType,
                Role = info.Role,
                Multiplier = multiplier,
                VanillaMaxValue = vanillaMax,
                EffectiveMaxValue = effectiveMax,
                KeysScaled = keyCount,
            };
        }

        // Deletes catalog curves the substring filters dragged into staging that
        // are NOT among the curves we actually patched (e.g. filter "CRV_BrigMotor"
        // also extracts "CRV_BrigMotor_BlackBeard"). Only ever touches known
        // catalog files, so it can never clobber another feature's assets.
        public int RemoveCollateral(string stagingDir, ISet<string> keepStems)
        {
            if (string.IsNullOrEmpty(stagingDir)) return 0;
            int removed = 0;
            foreach (var info in Curves)
            {
                if (keepStems != null && keepStems.Contains(info.Stem)) continue;
                var relUasset = info.VirtualPath.Replace('/', Path.DirectorySeparatorChar);
                var relUexp = Path.ChangeExtension(relUasset, ".uexp");
                foreach (var rel in new[] { relUasset, relUexp })
                {
                    var full = Path.Combine(stagingDir, rel);
                    if (File.Exists(full))
                    {
                        File.Delete(full);
                        removed++;
                    }
                }
            }
            if (removed > 0)
                LogLine("Faster Ships: dropped " + removed + " filter-collateral curve file(s) (not shipped)");
            return removed;
        }

        // Scans the first few byte offsets for an int32 key count followed by a
        // valid FRichCurveKey array, picking the candidate whose peak Value matches
        // the expected vanilla baseline (which pins the right offset even if the
        // unversioned header shifts). Returns false if none matches.
        static bool TryLocateKeyArray(byte[] d, float expectedMax,
            out int arrayOffset, out int keyCount, out float foundMax)
        {
            arrayOffset = -1; keyCount = 0; foundMax = 0f;
            float tol = Math.Max(0.05f, Math.Abs(expectedMax) * 0.01f);
            for (int off = 0; off <= 8; off++)
            {
                if (off + 4 > d.Length) break;
                int n = BitConverter.ToInt32(d, off);
                if (n < 1 || n > 16) continue;
                long need = (long)off + 4 + (long)n * KeySize;
                if (need > d.Length) continue;

                bool ok = true;
                float max = 0f;
                int p = off + 4;
                for (int k = 0; k < n; k++)
                {
                    // enum bytes (InterpMode/TangentMode/TangentWeightMode) are small
                    if (d[p] > 4 || d[p + 1] > 4 || d[p + 2] > 4) { ok = false; break; }
                    float time = BitConverter.ToSingle(d, p + OffTime);
                    float val = BitConverter.ToSingle(d, p + OffValue);
                    if (float.IsNaN(time) || float.IsInfinity(time)
                        || float.IsNaN(val) || float.IsInfinity(val)
                        || Math.Abs(time) > 1e7f || Math.Abs(val) > 1e7f) { ok = false; break; }
                    if (Math.Abs(val) > Math.Abs(max)) max = val;
                    p += KeySize;
                }
                if (!ok) continue;
                if (Math.Abs(Math.Abs(max) - Math.Abs(expectedMax)) <= tol)
                {
                    arrayOffset = off; keyCount = n; foundMax = max;
                    return true;
                }
            }
            return false;
        }

        static void ScaleFloatAt(byte[] d, int offset, double multiplier)
        {
            float v = BitConverter.ToSingle(d, offset);
            float scaled = (float)(v * multiplier);
            var b = BitConverter.GetBytes(scaled);
            d[offset] = b[0]; d[offset + 1] = b[1]; d[offset + 2] = b[2]; d[offset + 3] = b[3];
        }

        void LogLine(string msg)
        {
            if (Log != null) Log(msg);
        }
    }

    public sealed class ShipSpeedPatchResult
    {
        public string Stem;
        public string ShipType;
        public string Role;
        public double Multiplier;
        public float VanillaMaxValue;
        public float EffectiveMaxValue;
        public int KeysScaled;
    }
}
