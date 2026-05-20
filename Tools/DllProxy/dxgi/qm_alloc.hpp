// Quartermaster runtime memory wrapper - calls UE's FMemory::Realloc.
// -------------------------------------------------------------------
// Why: when we inject a widget into a UE TArray whose Max == Num (no slack),
// we need to grow the underlying buffer. UE TArrays are heap-allocated by
// GMalloc (the engine's FMalloc instance), so a growth has to go through
// the same allocator - otherwise the destructor will call GMalloc->Free on
// our foreign buffer and crash.
//
// Strategy:
//   1. Pattern-scan .text for `mov rcx, [rip-rel]; mov rax, [rcx]` (the
//      GMalloc vtable-load pattern). Count references per target address.
//   2. Top candidate that validates as a real FMalloc (vtable[0..5] all
//      point to executable code) is GMalloc.
//   3. Realloc lives at vtable slot 4 in every UE5 FMalloc subclass
//      (FMallocBinned2 / FMallocBinned3 / FMallocAnsi / FMallocTBB share
//      the FMalloc abstract base layout).
//
// SEH-guarded - failure to resolve doesn't crash, callers fall back to the
// pre-realloc "skipped-no-slack" path.

#pragma once

#include <stdint.h>

namespace QmAlloc
{
    // Pattern-scan .text and validate GMalloc + Realloc vtable slot.
    // Returns true if both were located and the vtable looks plausible.
    // Idempotent - safe to call multiple times. Should be called AFTER
    // the engine has run far enough that GMalloc is non-null (i.e. any
    // time after GObjects is populated).
    bool Resolve(uintptr_t imageBase);

    // True if Resolve() succeeded.
    bool IsResolved();

    // Diagnostic: address of the GMalloc global slot (FMalloc**) and the
    // resolved Realloc slot offset within the vtable. Both zero before
    // Resolve() succeeds.
    void GetDebugInfo(void** outGMallocPtr, uint32_t* outReallocVtblSlot);

    // Call FMemory::Realloc through the resolved GMalloc vtable. Returns
    // nullptr if not resolved, GMalloc became null, or the call faulted.
    //
    //   - Original = nullptr is allowed in theory (acts like Malloc) but is
    //     observed to AV in subsequent calls after a "warm" first call on
    //     this game build - prefer QmAlloc::Malloc() for fresh allocations
    //   - NewSize = 0 is allowed (acts like Free, returns nullptr)
    //   - Alignment = 0 means "use allocator's natural alignment" (per UE's
    //     DEFAULT_ALIGNMENT convention). Most TArray<void*> allocations
    //     pass alignof(void*) = 8.
    void* Realloc(void* Original, size_t NewSize, uint32_t Alignment = 0);

    // Direct Malloc through the FMalloc vtable. At Resolve() we probe slots
    // 2..6 for a 2-arg (Count, Alignment) entry that returns a valid heap
    // pointer - that's our Malloc. Used by the TArray-grow path instead of
    // Realloc(nullptr, ...) because the nullptr-Realloc cold path AVs on
    // subsequent calls (root cause unclear - allocator TLS / hot-cache state).
    // Returns nullptr if not resolved, no usable Malloc slot was found,
    // GMalloc became null, or the call faulted.
    // NOTE: default Alignment is 0 (= natural). Non-zero alignment is routed
    // by the FMallocProxy wrapper into a wstring init that AVs - the proxy
    // dereferences it as `cmp word [alignment], ax`. With 0 the wrapper takes
    // a fast-path skip. See qm_alloc.cpp ReserveBuffers comment.
    void* Malloc(size_t Size, uint32_t Alignment = 0);

    // ----- Inner FMallocBinned2 bypass (Plan Phase 3) -----------------------
    // GMalloc on this build is a FMallocProxy wrapper that allocates a 0x58
    // byte tracker block on every call and registers it in a global tracker
    // list. Calling Realloc(nullptr,...) cold on the proxy AVs (the proxy
    // dereferences our alignment argument as wchar*). Even when we got past
    // that with Alignment=0, the proxy's tracker list ended up in a partial
    // state that froze the engine on first GC sweep.
    //
    // Offline disassembly of vtable[4] revealed the proxy's `callTarget[0]`
    // is a tiny 25-byte static wrapper that tail-jumps to the REAL inner
    // FMallocBinned2::Malloc. Wrapper byte signature:
    //
    //   48 85 C9              test  rcx, rcx
    //   B8 01 00 00 00        mov   eax, 1
    //   BA 10 00 00 00        mov   edx, 0x10   ; default Alignment=16
    //   48 0F 45 C1           cmovne rax, rcx
    //   48 8B C8              mov   rcx, rax    ; rcx = size (or 1 if 0)
    //   E9 ?? ?? ?? ??        jmp   <inner FMalloc::Malloc>
    //
    // Pattern-scan finds this wrapper, decodes rel32 -> inner Malloc address.
    // Inner Malloc signature is `void* Malloc(SIZE_T size, uint32 align)` -
    // NO `this` parameter (it's a static internal func of FMallocBinned2).
    //
    // ResolveInnerMalloc() does the pattern-scan + validation. Returns true
    // if exactly one viable wrapper was found (multiple matches = ambiguous,
    // we refuse to guess). Idempotent.
    bool  ResolveInnerMalloc(uintptr_t imageBase);

    // True if ResolveInnerMalloc() succeeded.
    bool  IsInnerMallocResolved();

    // Diagnostic: address of the resolved inner FMalloc::Malloc.
    void* GetInnerMallocFn();

    // Call inner FMallocBinned2::Malloc directly, bypassing the FMallocProxy.
    // Returns nullptr if not resolved, Size==0, or the call faulted.
    // Default Alignment=16 matches what the proxy wrapper passes (BA 10..).
    // Use 0 for natural alignment but expect ~8 (engine treats 0 as "default").
    void* InnerMalloc(size_t Size, uint32_t Alignment = 16);

    // ----- Reserved buffer pool ---------------------------------------------
    // FMallocBinned2 prefixes every heap block with a canary byte (0xE3). If
    // we hand it back a pointer it never allocated (e.g. a DLL .data static
    // buffer), any later Realloc()/Free() call from game code triggers
    // appError() -> "Attempt to realloc an unrecognized block ... canary 0x00".
    //
    // To avoid that, we use InnerMalloc (above) which routes directly through
    // FMallocBinned2 and bypasses the FMallocProxy tracker entirely - so
    // partial-failure can't corrupt proxy state.
    //
    // ReserveBuffers tries to allocate `count` blocks of `bytesPerBuffer` each
    // via InnerMalloc(). Returns the number successfully allocated. Caller
    // (qm_inject.cpp) checks GetReservedBufferCount() and falls back to
    // DLL-static buffers if reservation came up short.
    //
    // Safe to call exactly once from the probe thread right after Resolve()
    // and ResolveInnerMalloc() both succeed. Pointers stay valid for the
    // DLL's lifetime - we never Free.
    int   ReserveBuffers(int count, size_t bytesPerBuffer, uint32_t Alignment = 16);
    int   GetReservedBufferCount();
    void* GetReservedBuffer(int idx);
    size_t GetReservedBufferSize();

    // ----- External-buffer hook (Plan B) ------------------------------------
    // To support handing DLL-static buffers to UE TArrays without tripping
    // FMallocBinned2's canary check on Free/Realloc, we MinHook the FMalloc
    // vtable slots that perform the canary check. The detour intercepts
    // calls where the pointer argument matches a registered external buffer:
    //   - Free(this, ptr=external)         -> return without calling original
    //   - Realloc(this, ptr=external, ...) -> allocate fresh + memcpy + return
    //
    // Slot detection works by disassembling the first ~512 bytes of each
    // vtable slot and looking for the 0xE3 canary byte as an immediate
    // operand. Free / Realloc / TryRealloc all contain this check.
    //
    // Registered buffer pointers are stored in a small fixed-size table
    // and looked up via linear scan in the detour. Performance impact is
    // ~16 pointer comparisons per Free/Realloc call (single-digit ns).

    // Register a DLL-static (or other foreign) buffer with the interception
    // table. The detour will treat any Free/Realloc on `ptr` as a no-op /
    // fresh-alloc respectively, using `size` for memcpy sizing.
    // Must be called BEFORE InstallExternalBufferHooks().
    bool RegisterExternalBuffer(void* ptr, size_t size);

    // True if `ptr` matches any registered external buffer.
    bool IsExternalBuffer(void* ptr);

    // Disassemble vtable[0..maxSlots-1] and identify slots that contain the
    // 0xE3 canary immediate in their first ~512 bytes. Logs findings.
    // Returns number of canary-check candidate slots found. Idempotent.
    int DetectCanaryCheckSlots(int maxSlots = 16);

    // Install MinHook on detected canary-check slots. Returns count of
    // hooks successfully installed. Should be called from the probe thread
    // BEFORE any game-thread Free/Realloc on registered buffers can occur.
    // Requires MH_Initialize() to have been called already.
    int InstallExternalBufferHooks();

    // ----- Offline analysis hex dump (Plan A diagnostic) --------------------
    // Inline disassembly heuristics failed to locate the FMallocBinned2 canary
    // check (0 hits across 16 vtable slots, e3bytes=0 everywhere). To resolve
    // this, dump raw code bytes of every vtable slot plus every unique
    // `call rel32` / `jmp rel32` target encountered, so they can be loaded into
    // Ghidra/ndisasm offline.
    //
    // Output is a series of [Alloc][HexDump] log lines:
    //   [Alloc][HexDump] vtable[ 4] BEGIN addr=0x... size=512
    //   [Alloc][HexDump] vtable[ 4] +0x000: 48 89 5C 24 08 ...
    //   [Alloc][HexDump] vtable[ 4] +0x020: ...
    //   ...
    //   [Alloc][HexDump] vtable[ 4] END
    //   [Alloc][HexDump] callTarget[ 0] BEGIN addr=0x... size=512
    //   ...
    //
    // Idempotent. Safe to call after Resolve() succeeds. Produces ~50-100KB
    // of log output, takes 1-2 seconds.
    void DumpVtableSlotsHexForOfflineAnalysis(int maxSlots = 16, size_t bytesPerSlot = 512);

} // namespace QmAlloc
