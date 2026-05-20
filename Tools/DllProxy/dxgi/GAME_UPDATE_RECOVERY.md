# Game-Update Recovery Playbook

This doc explains how to get the Quartermaster `dxgi.dll` working again after
a Windrose / R5 game update changes the shipped binary. Written for the AI
agent (Claude) or anybody else who needs to redo the diagnosis.

The mod's runtime path is a precarious chain of pattern scans. If the binary
moves around or recompiles with different optimizer settings, one or more
scans may fail. By design the DLL then degrades silently: items stop showing
in the build menu, no crash. So a missing-items report on a game update is
almost always one of the scans coming up empty.

## What the DLL needs at runtime

In order, all of these must succeed:

1. **`GMalloc` pattern scan** (qm_alloc.cpp `Resolve`) - finds the global
   `FMalloc*` slot via `mov rcx, [rip-rel]; mov rax, [rcx]` pattern, picked
   by ref-count.
2. **`InnerMalloc` shim scan** (qm_alloc.cpp `ResolveInnerMalloc`) - finds
   the 25-byte tail-jmp shim that bypasses FMallocProxy.
3. **`ReserveBuffers`** (qm_alloc.cpp) - allocates 8 x 128-byte buffers via
   the inner Malloc.
4. **`GetBuildingGroupsByCategoryTag` UFunction hook** (qm_hook.cpp) - the
   actual injection point.
5. **`kBuildingItemsOffset`** in `qm_inject.cpp` - the field offset within a
   `BuildingGroup` widget where the `Items` TArray header lives (0x350 at
   the time of writing).

## Diagnosing a broken build

### Step 1: Capture a fresh `Quartermaster_Inject.log`

Run the game once. The log lives next to the DLL. Look for these lines (all
must appear and succeed for the mod to be functional):

| Line prefix                                                         | Meaning                                                |
| ------------------------------------------------------------------- | ------------------------------------------------------ |
| `[Alloc] GMalloc resolved: ... refs=N`                              | Pattern scan 1/3 OK                                    |
| `[Alloc] InnerMalloc resolved: wrapper=0x... -> inner=0x...`        | Pattern scan 2/3 OK                                    |
| `[Alloc] reserved 8/8 buffers ... via InnerMalloc`                  | Buffer pool ready                                      |
| `[Hook] *** INSTALLED *** GetBuildingGroupsByCategoryTag ...`       | UFunction hook OK                                      |
| `[Spawn] *** READY ***`                                             | Inject path armed                                      |
| `[ItemSwap] *** SUCCESS *** apply#N ... vanilla=3 + custom=2 = 5`   | Items injected on Tab-switch (only visible after open) |

### Step 2: Identify the failing scan

Find the FIRST `[Alloc]` warning. The recovery action depends on which:

#### A. `[Alloc] no GMalloc candidates found (scan empty)`

The `mov rcx, [rip-rel]; mov rax, [rcx]` pattern has moved or been
inlined-out. This is very unusual - the pattern is so generic that even
heavy LTO leaves a few hundred copies.

Recovery:
- Add the alternate 7-byte LEA form (`48 8D 0D rel32`) and a 2-byte indirect
  call form to the scanner in `qm_alloc.cpp::Resolve`. Search ripgrep for
  similar UE5 GMalloc finders in other mod projects.

#### B. `[Alloc] GMalloc resolution FAILED - none of the top N candidates validated`

The pattern matched but no candidate's `[ptr] -> vtable -> slot[0..5]` chain
passed the executable-pointer check. Most likely: the FMalloc layout changed
(e.g. vtable now has the dtor in slot 1 instead of slot 0).

Recovery:
- Relax `ValidateFMallocCandidate` to require only 4 executable slots
  (currently 6). If still no luck, drop the executable-check entirely on
  slot 0 (some builds put a non-executable thunk there).

#### C. `[Alloc] ResolveInnerMalloc: no FMallocProxy wrapper pattern found`

This is the highest-risk failure - the 20-byte signature for the FMallocProxy
shim is build-specific. If the optimizer inlined the shim, or removed the
`test rcx, rcx; cmovne` branch, or reordered the regs (`mov eax, 1` becomes
`mov al, 1` etc.), the scan misses.

Recovery (this is the playbook that took 5 hours the first time, so follow
it carefully):

1. Re-enable the offline hex dump diagnostic. It was removed from the build
   to keep the log clean. Look at git history before commit `1df6d61` to
   restore `DumpVtableSlotsHexForOfflineAnalysis` in qm_alloc.cpp / .hpp
   and a call in qm_hook.cpp. Build, run game once.
2. Find `[Alloc][HexDump] === BEGIN offline analysis dump` in the log. The
   block below it dumps every vtable slot + every reachable call/jmp target,
   512 bytes each.
3. Find the `callTarget[N]` whose first byte is `0x48` followed by a small
   prologue (`test rcx, rcx` or `cmp rcx, 0` or similar) and ends in
   `E9 ?? ?? ?? ??` within ~25-35 bytes. That's the shim.
4. Decode the rel32 at offset `len-4`: `target = (shim+len) + sign_ext(rel32)`.
   That target is the new inner Malloc address. Sanity-check: it should be
   in the same module as GMalloc and look like an FMallocBinned function
   (prologue `48 89 5C 24 08` or similar).
5. Update the `kSig` byte array in `ResolveInnerMalloc` to match the actual
   prologue you see in the hex dump. ALSO update the `sizeof(kSig)` references
   (`p[20] != 0xE9`, `p + 21`, `p + 25` in the code) to match the new length.
6. Build, test. If `[Alloc] InnerMalloc resolved` appears, success.

**Hard rule**: NEVER add a fallback that hands DLL-static buffers to UE
TArrays. The FMallocBinned2 canary check (0xE3 byte before each block) is
unforgiving - it triggers `appError()` on the first Free/Realloc that hits
our pointer, and that's a hard game-crash. The mod is designed so missing
items is the worst-case failure mode. Keep it that way.

#### D. `[Alloc] InnerMalloc resolved` but `ReserveBuffers returned 0` or `< 8`

InnerMalloc was located but calling it returned `nullptr`. The inner
function's calling convention or arg semantics changed.

Recovery:
- Try `Alignment=0` instead of `16`. The shim hardcodes `mov edx, 0x10`
  before the tail-jmp - but if the inner function changed to require natural
  alignment, 16 might now mean something else.
- Try `Size=1` as the first-ever call (some bins refuse 128-byte requests
  if they haven't been warmed by smaller allocations).
- If `[Alloc] *** EXCEPTION inside InnerMalloc` appears in the log, the
  signature scan picked the wrong function. Re-check the hex dump.

#### E. Allocator stuff is fine but `[Hook] *** INSTALLED *** GetBuildingGroupsByCategoryTag` is missing

The UFunction name search failed - either the function was renamed or its
parent class moved. Check `qm_ue.cpp` / `qm_hook.cpp` for the FName lookup.
Look in the log for `[UE] init reached threshold on attempt#... GObjects.Num=N`
- if that line never prints, GObjects discovery itself is broken (different
problem - check `qm_ue.cpp::GetGObjects` pattern).

#### F. Hooks OK but `[ItemSwap] vanilla group0=0x... has Num=N, can't fit + 2 custom into buffer cap=16` or items are visually mangled

`kBuildingItemsOffset` (currently `0x350`) is wrong. The `Items` TArray
header inside a `BuildingGroup` widget moved.

Recovery:
- Open `Brush_Pier_01` (or any other Building Group asset) in UnrealPak +
  inspect the property layout. Find the offset to the `Items` field.
- Alternative: write a diag pass that scans the first ~1KB of a known
  group widget for `{ Data=ptr, Num=3, Max=4 }`-shaped triples and reports
  the offset.

## Verifying a fix

After patching the relevant pattern:

1. Build: `cd Tools/DllProxy/dxgi && build.bat` (dev) or `build.bat release`.
2. Deploy by copying `dxgi.dll` to `R5/Binaries/Win64/`. **Don't run
   `deploy.bat`** - it overwrites `qm_items.json`, which has manually-tuned
   data. (Or update the manifest first; just be aware.)
3. Start the game, open the build menu, switch to the "Vorgefertigte
   Strukturen" (BuildingBrushes) tab. The 2 custom items should appear
   alongside the vanilla Pier / House_02 / House_03.
4. Stress-test: 10-20 tab switches, then build several custom items in the
   world. If no crash after a few minutes of mixed play, ship it.

## What we know about this build (snapshot for diff after update)

Recorded `2026-05-20`, dxgi.dll size `256512` bytes (post-cleanup commit):

| Symbol                     | Address              | Notes                              |
| -------------------------- | -------------------- | ---------------------------------- |
| Game module                | `R5-Win64-Shipping`  | UE 5.6 build                       |
| `GMalloc` slot             | varies (ASLR)        | resolves via ref-count pattern     |
| FMallocProxy shim wrapper  | varies (ASLR)        | log shows `wrapper=0x00007FF6...`  |
| Inner `FMalloc::Malloc`    | varies (ASLR)        | log shows `inner=0x00007FF66A871...` |
| Realloc vtable slot        | `4`                  | hardcoded in qm_alloc.cpp          |
| `BuildingGroup.Items`      | `0x350`              | `kBuildingItemsOffset` in qm_inject.cpp |
| Custom item buffer size    | `8 x 128 bytes`      | `kCustomGroupBufferSlots` + `kCustomGroupItemMax * sizeof(void*)` |
| `RF_Standalone` flag       | `0x00000002`         | UObject->Flags bit                 |

## Things that absolutely don't work (don't try them again)

These were tried during the original implementation and all failed - leaving
the notes here so future-you doesn't waste time:

- **DLL-static buffers** as TArray.Data. Crashes via FMallocBinned2's 0xE3
  canary check on the FIRST engine-side Realloc/Free that hits our pointer.
  ~1-2 minutes after first inject.
- **Probe-thread `Realloc(nullptr, ...)` cold call**. FMallocProxy
  dereferences our `Alignment` arg as a wide-char pointer (`cmp word [rdx], ax`).
  AVs at faultAddr = alignment value. Alignment=0 lets the call go through
  ONCE then partial-corrupts the proxy's tracker list, freezing the engine
  on the first GC sweep.
- **Hot `Realloc(vanillaData, 128, X)` from inside the inject hook**. Same
  proxy-tracker-corruption issue. Same FMallocProxy wstring-deref AV with
  non-zero alignment.
- **MinHooking the vtable slots that perform the canary check** to filter
  external pointers. The canary check lives 2+ levels deep in helper
  functions (FMallocBinned2::FreeExternal etc), not inline in the vtable
  slots - and even if you find them, the wrong slot will corrupt the heap
  on probe and the game black-screens on startup.
- **Probing `FMalloc::Malloc` slot** by calling vtable[2..6] with test args.
  If the wrong slot turns out to be `Free`, you've just freed address 0x40
  from FMallocBinned's internal lists - silent heap corruption, manifests
  as a deadlock during AssetRegistry init minutes later.

The only path that worked is the one currently shipping: pattern-scan for
the FMallocProxy shim and bypass the proxy entirely with the inner Malloc.
