# Fix: build aborted "produces no changes" on ship-slots-only profiles — DONE

Status: DONE (committed). Resolved 2026-06-02.

## Problem
A profile that set **only** ship-slot multipliers (cargo x3, combat 5) patched
12 ship files successfully, but the build then aborted before packing:

```
[OK] Patching ship inventory slots (cargo / combat orders)
[OK]   cargo x3, combat orders 5 - 12 ship file(s) patched. NOTE: only affects NEW ships; existing ships need the save patcher.
[ERR] ERROR: Profile produces no changes - nothing to pack.
```

A profile that changed nothing but ship slots could not be built.

## Root cause
`shipSlotsResult` was written into `tmpDir` but never added to the
`totalWritten` sum in `BuildPipeline.cs`. For a ship-slots-only profile
`totalWritten == 0`, so the "no changes" gate tripped before packing.

## Fix
`shipSlotsResult.FilesWritten` now counts toward `totalWritten` (same as the
equipment slots), so the 12 ship JSONs land in the legacy pak and the build
packs normally — even when nothing else is set.

## Touched
- `Tools/QuartermasterCore/BuildPipeline.cs`
- `Tools/QuartermasterCore/ShipSlotsPatcher.cs`
