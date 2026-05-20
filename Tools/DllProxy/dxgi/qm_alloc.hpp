// Quartermaster runtime memory wrapper - calls UE's FMemory::Realloc.
// -------------------------------------------------------------------
// Why: when we inject our custom items into the BuildingBrushes vanilla
// group, we swap the group's Items.Data TArray to point at one of our own
// buffers (vanilla pointers copied + custom widgets appended). Those
// buffers MUST be allocated through FMallocBinned2 so the engine's
// canary-byte (0xE3) check on later Free/Realloc accepts them.
//
// FMallocBinned2 on this build is hidden behind an FMallocProxy wrapper
// (memory profiler). Going through the proxy from a probe-thread cold call
// crashes (its tracker-list partial-init corrupts engine state). The
// workaround is to pattern-scan for the proxy's `callTarget[0]` shim that
// tail-jumps directly to the real FMallocBinned2::Malloc - that gives us
// a clean bypass.
//
// HARD-FAIL POLICY: if either pattern-scan (GMalloc OR the inner-Malloc
// shim) fails, the mod does NOT fall back to DLL-static buffers - that
// path crashes the game seconds after the first build via the canary check.
// Instead, reserved buffer count stays 0; the inject path then skips
// ItemSwap and our items simply don't appear in the build menu. Clean
// degradation, no crash.
//
// Recovery playbook for game updates: see GAME_UPDATE_RECOVERY.md.

#pragma once

#include <stdint.h>

namespace QmAlloc
{
    // -- GMalloc resolution --------------------------------------------------
    // Pattern-scan .text for `mov rcx, [rip-rel]; mov rax, [rcx]` (vtable
    // load), count references per target, validate top candidate as a real
    // FMalloc. Idempotent. Should be called AFTER the engine has populated
    // GObjects (= GMalloc is non-null and points at the proxy).
    //
    // On success, also runs ResolveInnerMalloc + ReserveBuffers as a single
    // bring-up step. If the inner-Malloc shim isn't found, no buffers are
    // reserved - that's a deliberate hard-fail so the inject path stays off
    // and the game keeps running without our items.
    bool Resolve(uintptr_t imageBase);

    // True if Resolve() succeeded.
    bool IsResolved();

    // Diagnostic: address of the GMalloc global slot (FMalloc**) and the
    // resolved Realloc slot offset within the vtable. Both zero before
    // Resolve() succeeds.
    void GetDebugInfo(void** outGMallocPtr, uint32_t* outReallocVtblSlot);

    // Call FMemory::Realloc through the resolved GMalloc vtable. Returns
    // nullptr if not resolved, GMalloc became null, or the call faulted.
    // Used by the legacy "TArray grow" path in qm_inject.cpp - NOT used by
    // the primary ItemSwap path (which uses InnerMalloc + reserved buffers).
    //
    //   - Original = nullptr is allowed in theory (acts like Malloc) but on
    //     this build's FMallocProxy it AVs cold - prefer reserved buffers
    //     for fresh allocations.
    //   - NewSize = 0 is allowed (acts like Free, returns nullptr).
    //   - Alignment = 0 means "use allocator's natural alignment". MUST be 0
    //     on this build: the proxy routes non-zero alignment into a wstring
    //     init function and dereferences it as wchar*, causing an AV.
    void* Realloc(void* Original, size_t NewSize, uint32_t Alignment = 0);

    // -- Inner FMallocBinned2 bypass -----------------------------------------
    // The proxy's callTarget[0] is a 25-byte shim that tail-jumps to the real
    // FMallocBinned2::Malloc. Signature bytes:
    //
    //   48 85 C9              test  rcx, rcx
    //   B8 01 00 00 00        mov   eax, 1
    //   BA 10 00 00 00        mov   edx, 0x10
    //   48 0F 45 C1           cmovne rax, rcx
    //   48 8B C8              mov   rcx, rax
    //   E9 ?? ?? ?? ??        jmp   <inner FMalloc::Malloc>
    //
    // Inner Malloc signature: `void* Malloc(SIZE_T size, uint32 align)` -
    // no `this` parameter (it's a static internal func of FMallocBinned2).
    //
    // If a future build changes the shim layout this scan finds 0 matches
    // and we deliberately do NOT fall back. See header comment above.
    bool  ResolveInnerMalloc();
    bool  IsInnerMallocResolved();
    void* GetInnerMallocFn();

    // Direct call into inner FMallocBinned2::Malloc, bypassing the proxy.
    // Default Alignment=16 matches the shim's hardcoded `mov edx, 0x10`.
    void* InnerMalloc(size_t Size, uint32_t Alignment = 16);

    // -- Reserved buffer pool ------------------------------------------------
    // ReserveBuffers tries to allocate `count` blocks of `bytesPerBuffer` each
    // via InnerMalloc. Returns the number successfully allocated. Pointers
    // stay valid for the DLL lifetime - we never Free them.
    //
    // Called exactly once from Resolve() after ResolveInnerMalloc succeeds.
    // If InnerMalloc isn't resolved, this returns 0 without attempting any
    // allocation (would risk proxy-tracker corruption otherwise).
    int   ReserveBuffers(int count, size_t bytesPerBuffer, uint32_t Alignment = 16);
    int   GetReservedBufferCount();
    void* GetReservedBuffer(int idx);
    size_t GetReservedBufferSize();

} // namespace QmAlloc
