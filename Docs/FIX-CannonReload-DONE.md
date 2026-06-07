# Fix: ship cannon reload multiplier had no in-game effect — DONE

Status: DONE (committed: `79782b2` reload fix, `07fd28c` range slider).
Originally filed as unresolved recon; the in-game test confirmed the triage's
"Option A" — it was a real runtime bug, not a reporter-side mistake.

## Problem (user report)
> The Reload Multiplier for Cannons doesn't seem to work. I tried different
> settings and also different cannons … Reload Time is still the same as in
> vanilla.

## Root cause
The patcher targeted the `DA_BatteryManagerParams_*` uassets
(`AimingData.ReloadTime`). That pak override has **no in-game effect** — the
runtime reads cannon reload from the loose `R5CannonParams` .json files
(`CannonAimingData.ReloadTime`), not from those uassets.

Everything else was already proven correct (data model, GUI→profile→pipeline
wiring, patch math, and the IoStore pack roundtrip preserved the value), which
is exactly why the tool-side triage could not reproduce a fault — the wrong
asset was being patched.

## Fix
`CannonReloadPatcher` (renamed from `ShipCannonPatcher`) now patches the loose
`R5CannonParams` .json in a single pass per file:
- `CannonAimingData.ReloadTime` — reload slider
- `ShotRangeInterval.Max` — firing-range slider (added in `07fd28c`)

Both dimensions share the same .json, so they are patched together to avoid one
overwriting the other in the staging dir. **Player-only invariant enforced:**
only `DA_Cannon_*.json` are patched; `DA_AI_Cannon_*.json` (enemy / NPC cannons)
stay vanilla, so the sliders never buff enemy ships.

## Side bug, also fixed
Slider `min="0.01"` in the HTML vs patcher clamp `MinMultiplier = 0.1` made a
0.01–0.09 setting crash the whole build with an exception. The clamps
(`CooldownsPatcher`, `RangedReloadPatcher`) are now `0.01`, matching the slider.

## Touched
- `Tools/QuartermasterCore/CannonReloadPatcher.cs` (replaces `ShipCannonPatcher.cs`)
- `Tools/QuartermasterCore/RangedReloadPatcher.cs` / `CooldownsPatcher.cs` (clamp 0.01)
- `GUI/Web/wwwroot/tabs/cooldowns.js`
- `Tools/QuartermasterCore/Profile.cs`

## Obsolete
The `zzz_CannonReloadTest_P` throwaway test pak (all 8 hulls at 0.2x) is no
longer needed now that the fix is verified.
