// Quartermaster runtime memory wrapper - implementation.
// See qm_alloc.hpp for design notes.

#define _CRT_SECURE_NO_WARNINGS
#include <windows.h>
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

#include "qm_alloc.hpp"
#include "qm_log.hpp"

// hde64.h has its own extern "C" guard; include directly via relative path
// since hde64 lives under minhook/src/hde/ (not in minhook/include/).
#include "minhook/src/hde/hde64.h"

namespace QmAlloc
{
    // ----- State (module-private) -------------------------------------------

    static void**    s_gmallocPtr     = nullptr;   // &GMalloc (i.e. FMalloc** location)
    static uint32_t  s_reallocSlot    = 4;         // vtable slot index for Realloc
    static bool      s_resolved       = false;

    // Reserved buffer pool. Allocated once via InnerMalloc() (FMallocBinned2
    // direct, bypassing the FMallocProxy wrapper). FMallocBinned2's canary
    // check accepts these pointers on later Realloc/Free calls from game code.
    static constexpr int kReservedBuffersMax = 16;
    static void*    s_reservedBuffers[kReservedBuffersMax] = {};
    static int      s_reservedBufferCount = 0;
    static size_t   s_reservedBufferSize  = 0;

    // Inner-FMalloc bypass state. Populated by ResolveInnerMalloc() via
    // pattern-scan for the proxy's callTarget[0] wrapper.
    typedef void* (__fastcall *InnerMallocFn)(size_t Count, uint32_t Align);
    static InnerMallocFn s_innerMallocFn      = nullptr;
    static uintptr_t     s_innerMallocWrapper = 0;  // address of the 25-byte wrapper

    // Function signature of FMalloc::Realloc.
    //   void* Realloc(this, void* Original, SIZE_T NewSize, uint32 Alignment)
    // x64 __fastcall: rcx=this rdx=Original r8=NewSize r9=Alignment
    typedef void* (__fastcall *FMallocReallocFn)(void* This, void* Original, size_t Count, uint32_t Align);

    // ----- Memory-readable / executable probes ------------------------------

    static bool IsReadableLen(const void* ptr, size_t bytes)
    {
        if (!ptr || bytes == 0) return false;
        MEMORY_BASIC_INFORMATION mbi{};
        const uint8_t* p   = static_cast<const uint8_t*>(ptr);
        const uint8_t* end = p + bytes;
        while (p < end)
        {
            if (VirtualQuery(p, &mbi, sizeof(mbi)) == 0) return false;
            if (mbi.State != MEM_COMMIT) return false;
            const DWORD readableMask = PAGE_READONLY | PAGE_READWRITE
                                     | PAGE_WRITECOPY | PAGE_EXECUTE_READ
                                     | PAGE_EXECUTE_READWRITE | PAGE_EXECUTE_WRITECOPY;
            if ((mbi.Protect & readableMask) == 0) return false;
            if (mbi.Protect & PAGE_GUARD) return false;
            if (mbi.Protect & PAGE_NOACCESS) return false;
            const uint8_t* regionEnd = static_cast<const uint8_t*>(mbi.BaseAddress) + mbi.RegionSize;
            p = regionEnd;
        }
        return true;
    }

    static bool IsExecutable(const void* ptr)
    {
        if (!ptr) return false;
        MEMORY_BASIC_INFORMATION mbi{};
        if (VirtualQuery(ptr, &mbi, sizeof(mbi)) == 0) return false;
        if (mbi.State != MEM_COMMIT) return false;
        const DWORD execMask = PAGE_EXECUTE | PAGE_EXECUTE_READ
                             | PAGE_EXECUTE_READWRITE | PAGE_EXECUTE_WRITECOPY;
        return (mbi.Protect & execMask) != 0;
    }

    // ----- PE section enumeration: find executable .text-style sections -----

    struct TextSection
    {
        const uint8_t* start;
        size_t         size;
    };

    static int EnumerateTextSections(uintptr_t imageBase, TextSection* out, int maxOut)
    {
        if (!imageBase || !out || maxOut <= 0) return 0;
        int written = 0;
        __try
        {
            auto dos = reinterpret_cast<const IMAGE_DOS_HEADER*>(imageBase);
            if (dos->e_magic != IMAGE_DOS_SIGNATURE) return 0;
            auto nt = reinterpret_cast<const IMAGE_NT_HEADERS64*>(imageBase + dos->e_lfanew);
            if (nt->Signature != IMAGE_NT_SIGNATURE) return 0;
            if (nt->FileHeader.NumberOfSections > 96) return 0;

            auto sec = IMAGE_FIRST_SECTION(nt);
            for (WORD i = 0; i < nt->FileHeader.NumberOfSections && written < maxOut; ++i)
            {
                if ((sec[i].Characteristics & IMAGE_SCN_MEM_EXECUTE) == 0) continue;
                if (sec[i].Misc.VirtualSize == 0) continue;
                out[written].start = reinterpret_cast<const uint8_t*>(imageBase + sec[i].VirtualAddress);
                out[written].size  = sec[i].Misc.VirtualSize;
                ++written;
            }
        }
        __except (EXCEPTION_EXECUTE_HANDLER) { return 0; }
        return written;
    }

    // ----- GMalloc candidate scan -------------------------------------------
    //
    // Pattern: `48 8B 0D ?? ?? ?? ?? 48 8B 01`
    //   mov rcx, [rip+rel32]  ; load global pointer (e.g. GMalloc)
    //   mov rax, [rcx]        ; dereference to get vtable
    //
    // GMalloc has BY FAR the highest reference count among vtable-loaded
    // globals in any UE shipping binary - every TArray growth, every FString
    // allocation, every UObject construction funnels through it. We count
    // references per target and validate the top candidate as a real FMalloc.

    struct Candidate
    {
        uintptr_t addr;       // address of the global pointer (i.e. &GMalloc)
        int       refCount;
    };

    static constexpr int kMaxCandidates = 256;

    static int FindOrAddCandidate(Candidate* cands, int& count, uintptr_t addr)
    {
        for (int i = 0; i < count; ++i)
            if (cands[i].addr == addr) return i;
        if (count >= kMaxCandidates) return -1;
        cands[count].addr = addr;
        cands[count].refCount = 0;
        return count++;
    }

    static int CompareByRefCountDesc(const void* a, const void* b)
    {
        const Candidate* ca = static_cast<const Candidate*>(a);
        const Candidate* cb = static_cast<const Candidate*>(b);
        if (cb->refCount > ca->refCount) return  1;
        if (cb->refCount < ca->refCount) return -1;
        return 0;
    }

    static bool ValidateFMallocCandidate(void** candidateAddr, void** outVtable)
    {
        if (!IsReadableLen(candidateAddr, sizeof(void*))) return false;
        void* fmalloc = nullptr;
        __try { fmalloc = *candidateAddr; }
        __except (EXCEPTION_EXECUTE_HANDLER) { return false; }
        if (!fmalloc) return false;
        if (!IsReadableLen(fmalloc, sizeof(void*))) return false;

        void** vtable = nullptr;
        __try { vtable = *reinterpret_cast<void***>(fmalloc); }
        __except (EXCEPTION_EXECUTE_HANDLER) { return false; }
        if (!vtable) return false;
        // FMalloc has ~20 virtuals - probe at least the first 6 (dtor, Exec,
        // Malloc, TryMalloc, Realloc, TryRealloc). All must be executable.
        if (!IsReadableLen(vtable, 6 * sizeof(void*))) return false;

        for (int k = 0; k < 6; ++k)
        {
            void* slot = nullptr;
            __try { slot = vtable[k]; }
            __except (EXCEPTION_EXECUTE_HANDLER) { return false; }
            if (!slot) return false;
            if (!IsExecutable(slot)) return false;
        }

        if (outVtable) *outVtable = vtable;
        return true;
    }

    bool Resolve(uintptr_t imageBase)
    {
        if (s_resolved) return true;
        if (!imageBase) return false;

        TextSection sections[16] = {};
        const int secCount = EnumerateTextSections(imageBase, sections, 16);
        if (secCount == 0)
        {
            QM_LOG_ERROR("[Alloc] Resolve: no executable sections found");
            return false;
        }

        Candidate cands[kMaxCandidates] = {};
        int candCount = 0;

        const DWORD t0 = GetTickCount();
        uint64_t patternHits = 0;

        // Scan each .text-style section for the pattern.
        for (int s = 0; s < secCount; ++s)
        {
            const uint8_t* p   = sections[s].start;
            const uint8_t* end = p + sections[s].size;
            if (end < p) continue;

            __try
            {
                while (p + 10 <= end)
                {
                    // 48 8B 0D ?? ?? ?? ?? 48 8B 01
                    if (p[0] == 0x48 && p[1] == 0x8B && p[2] == 0x0D &&
                        p[7] == 0x48 && p[8] == 0x8B && p[9] == 0x01)
                    {
                        int32_t rel = *reinterpret_cast<const int32_t*>(p + 3);
                        uintptr_t target = reinterpret_cast<uintptr_t>(p + 7) + rel;
                        int idx = FindOrAddCandidate(cands, candCount, target);
                        if (idx >= 0)
                        {
                            cands[idx].refCount++;
                            ++patternHits;
                        }
                        p += 10;
                    }
                    else
                    {
                        ++p;
                    }
                }
            }
            __except (EXCEPTION_EXECUTE_HANDLER) { /* fault: next section */ }
        }

        const DWORD t1 = GetTickCount();

        if (candCount == 0)
        {
            QM_LOG_ERROR("[Alloc] no GMalloc candidates found (scan empty)");
            return false;
        }

        // Sort by ref-count descending.
        qsort(cands, candCount, sizeof(Candidate), &CompareByRefCountDesc);

        // Validate top candidates - first one whose *ptr looks like a real
        // FMalloc instance (vtable with executable slots) wins.
        const int kProbeMax = (candCount < 16) ? candCount : 16;
        for (int i = 0; i < kProbeMax; ++i)
        {
            void** ptr = reinterpret_cast<void**>(cands[i].addr);
            void* vtable = nullptr;
            if (!ValidateFMallocCandidate(ptr, &vtable)) continue;

            s_gmallocPtr   = ptr;
            s_reallocSlot  = 4;  // FMalloc::Realloc is virtual slot 4 in every UE5 FMalloc subclass
            s_resolved     = true;

            QM_LOG_INFO("[Alloc] GMalloc resolved: addr=0x%p refs=%d (top candidate %d/%d) FMalloc=0x%p vtable=0x%p Realloc=vtable[%u]=0x%p (scan: %llu pattern hits, %d candidates, %lums)",
                ptr, cands[i].refCount, i + 1, candCount,
                *ptr, vtable, s_reallocSlot,
                reinterpret_cast<void**>(vtable)[s_reallocSlot],
                static_cast<unsigned long long>(patternHits), candCount, (t1 - t0));

            // Inner FMalloc bypass: pattern-scan for the proxy's callTarget[0]
            // shim that tail-jumps to the real FMallocBinned2::Malloc, then
            // pre-reserve our ItemSwap buffer pool through it. If either step
            // fails we LEAVE reserved buffer count at 0 - the inject path then
            // skips ItemSwap entirely (items don't show), no fallback.
            if (ResolveInnerMalloc())
            {
                const int wantCount      = 8;
                const size_t wantBytes   = 128;  // 16 ptrs (kCustomGroupItemMax)
                const uint32_t wantAlign = 16;   // match proxy shim's `mov edx, 0x10`
                int got = ReserveBuffers(wantCount, wantBytes, wantAlign);
                if (got > 0)
                    QM_LOG_INFO("[Alloc] reserved %d/%d buffers (%zu bytes each, align=%u) via InnerMalloc - FMallocBinned2 canary will accept these on Realloc/Free",
                        got, wantCount, wantBytes, wantAlign);
                else
                    QM_LOG_WARN("[Alloc] InnerMalloc resolved but ReserveBuffers returned 0 - ItemSwap will skip, items will not appear in build menu");
            }
            else
            {
                QM_LOG_WARN("[Alloc] InnerMalloc shim NOT found - ItemSwap will skip, items will not appear in build menu (see GAME_UPDATE_RECOVERY.md)");
            }

            return true;
        }

        QM_LOG_ERROR("[Alloc] GMalloc resolution FAILED - none of the top %d candidates validated as FMalloc (top ref-counts: %d, %d, %d) (scan: %llu pattern hits, %d candidates, %lums)",
            kProbeMax,
            candCount > 0 ? cands[0].refCount : 0,
            candCount > 1 ? cands[1].refCount : 0,
            candCount > 2 ? cands[2].refCount : 0,
            static_cast<unsigned long long>(patternHits), candCount, (t1 - t0));
        return false;
    }

    bool IsResolved() { return s_resolved; }

    void GetDebugInfo(void** outGMallocPtr, uint32_t* outReallocVtblSlot)
    {
        if (outGMallocPtr)      *outGMallocPtr      = s_gmallocPtr;
        if (outReallocVtblSlot) *outReallocVtblSlot = s_reallocSlot;
    }

    // ----- Exception-info capture for the Realloc / InnerMalloc filters -----
    // One-shot, only for the most recent Realloc/Malloc exception.
    static thread_local uint32_t  tl_excCode       = 0;
    static thread_local void*     tl_excFaultAddr  = nullptr;
    static thread_local void*     tl_excRip        = nullptr;

    static int CaptureExceptionInfo(EXCEPTION_POINTERS* ep)
    {
        if (ep && ep->ExceptionRecord)
        {
            tl_excCode = ep->ExceptionRecord->ExceptionCode;
            if (ep->ExceptionRecord->ExceptionCode == EXCEPTION_ACCESS_VIOLATION &&
                ep->ExceptionRecord->NumberParameters >= 2)
            {
                tl_excFaultAddr = reinterpret_cast<void*>(ep->ExceptionRecord->ExceptionInformation[1]);
            }
            else { tl_excFaultAddr = nullptr; }
            tl_excRip = reinterpret_cast<void*>(ep->ExceptionRecord->ExceptionAddress);
        }
        return EXCEPTION_EXECUTE_HANDLER;
    }

    void* Realloc(void* Original, size_t NewSize, uint32_t Alignment)
    {
        if (!s_resolved || !s_gmallocPtr) return nullptr;

        void* fmalloc = nullptr;
        __try { fmalloc = *s_gmallocPtr; }
        __except (EXCEPTION_EXECUTE_HANDLER) { return nullptr; }
        if (!fmalloc) return nullptr;

        void** vtable = nullptr;
        __try { vtable = *reinterpret_cast<void***>(fmalloc); }
        __except (EXCEPTION_EXECUTE_HANDLER) { return nullptr; }
        if (!vtable) return nullptr;

        FMallocReallocFn fn = reinterpret_cast<FMallocReallocFn>(vtable[s_reallocSlot]);
        if (!fn) return nullptr;

        void* result = nullptr;
        __try
        {
            result = fn(fmalloc, Original, NewSize, Alignment);
        }
        __except (CaptureExceptionInfo(GetExceptionInformation()))
        {
            QM_LOG_ERROR("[Alloc] *** EXCEPTION inside GMalloc::Realloc(Original=0x%p Size=%zu Align=%u) code=0x%08X rip=0x%p faultAddr=0x%p fmalloc=0x%p vtable=0x%p fn=0x%p",
                Original, NewSize, Alignment,
                tl_excCode, tl_excRip, tl_excFaultAddr,
                fmalloc, vtable, fn);
            return nullptr;
        }
        return result;
    }

    // ----- Reserved buffer pool ---------------------------------------------

    int ReserveBuffers(int count, size_t bytesPerBuffer, uint32_t Alignment)
    {
        if (count <= 0 || bytesPerBuffer == 0) return 0;
        if (count > kReservedBuffersMax) count = kReservedBuffersMax;

        // Already reserved? Idempotent.
        if (s_reservedBufferCount > 0) return s_reservedBufferCount;

        // HARD-FAIL: InnerMalloc must be resolved. Going through the proxy
        // (Realloc(nullptr,...)) at probe time either AVs on the proxy's
        // wstring-init helper or corrupts the proxy's tracker list on partial
        // success - neither acceptable. If we land here without InnerMalloc,
        // return 0 and let the caller deal with "no reserved buffers".
        if (!s_innerMallocFn)
        {
            QM_LOG_WARN("[Alloc] ReserveBuffers: InnerMalloc not resolved - refusing to allocate (proxy path is unsafe at probe time)");
            return 0;
        }

        s_reservedBufferSize = bytesPerBuffer;

        int ok = 0;
        for (int i = 0; i < count; ++i)
        {
            void* p = InnerMalloc(bytesPerBuffer, Alignment);
            if (!p)
            {
                QM_LOG_WARN("[Alloc] ReserveBuffers: InnerMalloc(%zu, %u) returned null at slot %d/%d - stopping",
                    bytesPerBuffer, Alignment, i, count);
                break;
            }
            __try { memset(p, 0, bytesPerBuffer); }
            __except (EXCEPTION_EXECUTE_HANDLER) {
                QM_LOG_WARN("[Alloc] ReserveBuffers: memset on slot %d (addr=0x%p) faulted - skipping", i, p);
                break;
            }
            s_reservedBuffers[i] = p;
            ++ok;
        }
        s_reservedBufferCount = ok;

        if (ok > 0)
        {
            QM_LOG_INFO("[Alloc] reserved buffer[0]=0x%p [%d-1]=0x%p (size=%zu) via InnerMalloc",
                s_reservedBuffers[0], ok, s_reservedBuffers[ok - 1], bytesPerBuffer);
        }
        return ok;
    }

    int GetReservedBufferCount() { return s_reservedBufferCount; }

    void* GetReservedBuffer(int idx)
    {
        if (idx < 0 || idx >= s_reservedBufferCount) return nullptr;
        return s_reservedBuffers[idx];
    }

    size_t GetReservedBufferSize() { return s_reservedBufferSize; }

    // ========================================================================
    // Inner FMallocBinned2 bypass
    //
    // The FMallocProxy is wrapped in front of FMallocBinned2 on this build.
    // Its `callTarget[0]` is a 25-byte static shim that tail-jumps directly
    // to the real FMallocBinned2::Malloc. We pattern-scan for the shim, then
    // call inner Malloc with no `this` parameter (it's static).
    //
    // The scan walks vtable[0..15] of the proxy, collects every `call rel32`
    // and `jmp rel32` target, follows them one level deep, then byte-compares
    // each unique target against the 20-byte signature + E9 + rel32.
    // ========================================================================

    // Scan a function's first `maxBytes` for call/jmp rel32 instructions and
    // record their absolute targets. Stops at ret or unconditional jmp/short-jmp.
    static int CollectCallJmpTargets(const void* fn, size_t maxBytes,
                                     void** out, int maxTargets)
    {
        if (!fn || !out || maxTargets <= 0) return 0;
        if (!IsReadableLen(fn, 16)) return 0;

        const uint8_t* code = static_cast<const uint8_t*>(fn);
        int count = 0;
        size_t offset = 0;

        __try
        {
            while (offset < maxBytes && count < maxTargets)
            {
                if (!IsReadableLen(code + offset, 16)) break;
                hde64s hs = {};
                unsigned int len = hde64_disasm(code + offset, &hs);
                if (len == 0 || (hs.flags & F_ERROR) || len > 15) break;

                const bool isCallRel32 = (hs.opcode == 0xE8 && (hs.flags & F_RELATIVE));
                const bool isJmpRel32  = (hs.opcode == 0xE9 && (hs.flags & F_RELATIVE));
                if (isCallRel32 || isJmpRel32)
                {
                    const int32_t rel = (int32_t)hs.imm.imm32;
                    void* target = reinterpret_cast<void*>(
                        reinterpret_cast<uintptr_t>(code + offset + len) + rel);
                    out[count++] = target;
                }

                if (hs.opcode == 0xC3 || hs.opcode == 0xC2) { offset += len; break; }
                if (hs.opcode == 0xE9 || hs.opcode == 0xEB) { offset += len; break; }

                offset += len;
            }
        }
        __except (EXCEPTION_EXECUTE_HANDLER) { /* truncate */ }

        return count;
    }

    bool ResolveInnerMalloc()
    {
        if (s_innerMallocFn) return true;
        if (!s_resolved || !s_gmallocPtr) return false;

        // ---- Get the FMallocProxy vtable from already-resolved GMalloc ----
        void* fmalloc = nullptr;
        __try { fmalloc = *s_gmallocPtr; }
        __except (EXCEPTION_EXECUTE_HANDLER) { return false; }
        if (!fmalloc) return false;

        void** vtable = nullptr;
        __try { vtable = *reinterpret_cast<void***>(fmalloc); }
        __except (EXCEPTION_EXECUTE_HANDLER) { return false; }
        if (!vtable) return false;
        if (!IsReadableLen(vtable, 16 * sizeof(void*))) return false;

        // 20-byte signature, then E9 + rel32 = 25 bytes total wrapper head.
        //
        //   48 85 C9              test  rcx, rcx              (3)
        //   B8 01 00 00 00        mov   eax, 1                (5)
        //   BA 10 00 00 00        mov   edx, 0x10             (5)
        //   48 0F 45 C1           cmovne rax, rcx             (4)
        //   48 8B C8              mov   rcx, rax              (3)  -> 20 bytes
        //   E9 ?? ?? ?? ??        jmp   <inner FMalloc::Malloc> (5) -> 25
        static const uint8_t kSig[20] = {
            0x48, 0x85, 0xC9,                              // test rcx, rcx
            0xB8, 0x01, 0x00, 0x00, 0x00,                  // mov eax, 1
            0xBA, 0x10, 0x00, 0x00, 0x00,                  // mov edx, 0x10
            0x48, 0x0F, 0x45, 0xC1,                        // cmovne rax, rcx
            0x48, 0x8B, 0xC8,                              // mov rcx, rax
        };

        const DWORD t0 = GetTickCount();

        // ---- Phase 1: collect targets across vtable[0..15] + 1 level deep ----
        constexpr int kMaxTargets = 256;
        void* targets[kMaxTargets] = {};
        int   targetCount = 0;

        // Seed with the vtable slots themselves (rare-but-cheap case: wrapper
        // installed directly as a slot).
        for (int slot = 0; slot < 16 && targetCount < kMaxTargets; ++slot)
        {
            void* fn = nullptr;
            __try { fn = vtable[slot]; }
            __except (EXCEPTION_EXECUTE_HANDLER) { continue; }
            if (!fn || !IsExecutable(fn)) continue;
            targets[targetCount++] = fn;
        }

        // Walk each vtable slot for call/jmp rel32 targets.
        for (int slot = 0; slot < 16; ++slot)
        {
            void* fn = nullptr;
            __try { fn = vtable[slot]; }
            __except (EXCEPTION_EXECUTE_HANDLER) { continue; }
            if (!fn || !IsExecutable(fn)) continue;

            void* slotTargets[32] = {};
            const int found = CollectCallJmpTargets(fn, 1024, slotTargets, 32);
            for (int i = 0; i < found; ++i)
            {
                void* t = slotTargets[i];
                if (!t || !IsExecutable(t)) continue;
                bool dup = false;
                for (int j = 0; j < targetCount; ++j)
                    if (targets[j] == t) { dup = true; break; }
                if (!dup && targetCount < kMaxTargets)
                    targets[targetCount++] = t;
            }
        }

        // One level deeper. callTarget[0] (= our shim) sits at vtable[N] ->
        // [helper] -> [shim], so we need 2 tiers of reachability.
        const int tier1Count = targetCount;
        for (int i = 0; i < tier1Count && targetCount < kMaxTargets; ++i)
        {
            void* slotTargets[16] = {};
            const int found = CollectCallJmpTargets(targets[i], 512, slotTargets, 16);
            for (int k = 0; k < found && targetCount < kMaxTargets; ++k)
            {
                void* t = slotTargets[k];
                if (!t || !IsExecutable(t)) continue;
                bool dup = false;
                for (int j = 0; j < targetCount; ++j)
                    if (targets[j] == t) { dup = true; break; }
                if (!dup) targets[targetCount++] = t;
            }
        }

        // ---- Phase 2: pattern-check each collected target ----
        int matches = 0;
        uintptr_t firstWrapper = 0, firstInner   = 0;
        uintptr_t secondWrapper = 0, secondInner = 0;

        for (int i = 0; i < targetCount && matches < 8; ++i)
        {
            const uint8_t* p = static_cast<const uint8_t*>(targets[i]);
            if (!IsReadableLen(p, 25)) continue;

            __try
            {
                if (memcmp(p, kSig, sizeof(kSig)) != 0)        continue;
                if (p[20] != 0xE9)                              continue;
                int32_t rel = *reinterpret_cast<const int32_t*>(p + 21);
                uintptr_t target = reinterpret_cast<uintptr_t>(p + 25) + rel;
                if (!IsExecutable(reinterpret_cast<void*>(target))) continue;
                if (!IsReadableLen(reinterpret_cast<void*>(target), 16)) continue;

                if (matches == 0)
                {
                    firstWrapper = reinterpret_cast<uintptr_t>(p);
                    firstInner   = target;
                }
                else if (matches == 1)
                {
                    secondWrapper = reinterpret_cast<uintptr_t>(p);
                    secondInner   = target;
                }
                ++matches;
            }
            __except (EXCEPTION_EXECUTE_HANDLER) { /* skip */ }
        }

        const DWORD t1 = GetTickCount();

        QM_LOG_INFO("[Alloc] ResolveInnerMalloc: scanned %d targets (vtable[0..15] + 1 level of call/jmp), %d sig matches (%lums)",
            targetCount, matches, (t1 - t0));

        if (matches == 0)
        {
            QM_LOG_WARN("[Alloc] ResolveInnerMalloc: no FMallocProxy wrapper pattern found among %d reachable targets - see GAME_UPDATE_RECOVERY.md",
                targetCount);
            return false;
        }
        if (matches >= 2 && firstInner != secondInner)
        {
            QM_LOG_WARN("[Alloc] ResolveInnerMalloc: %d wrappers match, but inner targets differ (first wrapper=0x%p->inner=0x%p, second wrapper=0x%p->inner=0x%p) - refusing to pick blindly",
                matches,
                reinterpret_cast<void*>(firstWrapper),  reinterpret_cast<void*>(firstInner),
                reinterpret_cast<void*>(secondWrapper), reinterpret_cast<void*>(secondInner));
            return false;
        }

        s_innerMallocWrapper = firstWrapper;
        s_innerMallocFn      = reinterpret_cast<InnerMallocFn>(firstInner);

        QM_LOG_INFO("[Alloc] InnerMalloc resolved: wrapper=0x%p -> inner FMalloc::Malloc=0x%p (sig matches=%d, %lums)",
            reinterpret_cast<void*>(s_innerMallocWrapper),
            reinterpret_cast<void*>(s_innerMallocFn),
            matches, (t1 - t0));
        return true;
    }

    bool  IsInnerMallocResolved() { return s_innerMallocFn != nullptr; }
    void* GetInnerMallocFn()      { return reinterpret_cast<void*>(s_innerMallocFn); }

    void* InnerMalloc(size_t Size, uint32_t Alignment)
    {
        if (!s_innerMallocFn) return nullptr;
        if (Size == 0) return nullptr;

        void* result = nullptr;
        __try
        {
            // Inner FMallocBinned2::Malloc(SIZE_T size, uint32 align).
            // No `this` parameter - it's a static internal function.
            result = s_innerMallocFn(Size, Alignment);
        }
        __except (CaptureExceptionInfo(GetExceptionInformation()))
        {
            QM_LOG_ERROR("[Alloc] *** EXCEPTION inside InnerMalloc(Size=%zu Align=%u) code=0x%08X rip=0x%p faultAddr=0x%p fn=0x%p",
                Size, Alignment, tl_excCode, tl_excRip, tl_excFaultAddr, s_innerMallocFn);
            return nullptr;
        }
        return result;
    }

} // namespace QmAlloc
