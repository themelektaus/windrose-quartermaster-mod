# Fix: Ship-Slots save patcher refused downgrades — DONE

Status: DONE (committed). Resolved 2026-06-02.

## Problem
After patching a save's ship slots up (cargo 28 → 84, combat 1 → 5) and then
moving the profile sliders back to vanilla (cargo 1x = 28, combat 1), the
Characters tab showed **"Up to date"** and refused to patch back down.

Symptom (card "Speedrunner - Ketch_Stock"):
- Display: `Cargo 84 (vanilla 28) -> 28 | Combat orders 5 -> 1 [Ketch_Stock]`
- Button: **"Up to date"**, text *"Already cargo 84 / combat 5 - nothing to do."*

The target was clearly 28/1 while the save held 84/5, yet the
"alreadyMatches" check treated the state as current and blocked the downgrade.

## Root cause
A `cargoActive` / `combatActive` gate ("slider != vanilla") in
`ShipSaveSlotsPatcher.PatchShip` AND `characters.js` (`shipNeedsPatch`). When a
slider sat at vanilla the gate went `false` → "Up to date", even though the
target (28/1) differed from the current save value (84/5). The target is already
computed idempotently (`vanillaBase * mult` for cargo, absolute for combat), so
the gate was dead weight that only blocked downgrades. Equipment / ring slots
never had this gate, which is why they downgraded correctly.

## Fix
Gate removed. "needs patch" is now simply: current save value (live OR
blueprint) != target. The blocking-item check still guards the shrink
(downgrade) path so items are never deleted without confirmation.

## Touched
- `Tools/QuartermasterCore/ShipSaveSlotsPatcher.cs`
- `GUI/Web/wwwroot/tabs/characters.js`
