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
#include "MinHook.h"

// hde64.h has its own extern "C" guard; include directly via relative path
// since hde64 lives under minhook/src/hde/ (not in minhook/include/).
#include "minhook/src/hde/hde64.h"

namespace QmAlloc
{
    // ----- State (module-private) -------------------------------------------

    static void**    s_gmallocPtr     = nullptr;   // &GMalloc (i.e. FMalloc** location)
    static uint32_t  s_reallocSlot    = 4;         // vtable slot index for Realloc
    static uint32_t  s_mallocSlot     = 0;         // vtable slot index for Malloc (0 = not detected)
    static bool      s_resolved       = false;

    // Reserved buffer pool. Allocated once via InnerMalloc() (FMallocBinned2
    // direct, bypassing the FMallocProxy wrapper). FMallocBinned2's canary
    // check accepts these pointers on later Realloc/Free calls from game code.
    // See ReserveBuffers().
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

    // Function signature of FMalloc::Malloc.
    //   void* Malloc(this, SIZE_T Count, uint32 Alignment)
    // x64 __fastcall: rcx=this rdx=Count r8=Alignment
    typedef void* (__fastcall *FMallocMallocFn)(void* This, size_t Count, uint32_t Align);

    // ----- Memory-readable / executable probes ------------------------------
    // (Duplicated from qm_scan.cpp - kept module-local so qm_alloc has no
    // back-dependency on qm_scan. Both are tiny.)

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

    // ----- PE section enumeration: find executable .text-style sections ----

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
    // This is the universal "load vtable from a global FMalloc-like pointer"
    // pattern. Many globals have it (GLog, GEngine etc.) but GMalloc has BY
    // FAR the highest reference count in any UE shipping binary - every
    // TArray growth, every FString allocation, every UObject construction
    // funnels through it. So we count references per target and validate
    // the top candidate as a real FMalloc.

    struct Candidate
    {
        uintptr_t addr;       // address of the global pointer (i.e. &GMalloc)
        int       refCount;
    };

    // Cap candidates to keep the scan bounded. A typical UE binary has <100
    // distinct vtable-loaded globals.
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
                        // RIP-relative is from the address of the NEXT instruction.
                        // `mov rcx, [rip+rel32]` is 7 bytes (48 8B 0D rel32), so
                        // the next-instruction RIP is at p+7.
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
            __except (EXCEPTION_EXECUTE_HANDLER)
            {
                // Page fault while scanning - move on to next section.
            }
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

            // ---- Malloc slot detection: DISABLED ----------------------------
            // A previous version probed vtable[2..6] by calling each slot with
            // (this, 64, 16) and SEH-guarding the call. That works when the
            // wrong slot AVs cleanly (e.g. virtual-dtor reads garbage as
            // ImageRel offset), but it CORRUPTS THE ALLOCATOR HEAP when the
            // wrong slot happens to be FMalloc::Free - because we then call
            // Free(this, ptr=64, align=16) which tries to free address 0x40.
            // FMallocBinned's Free unbins 0x40 from its internal free-list
            // structures, silently corrupting heap metadata. The DLL init
            // logs success, but the game later deadlocks during AssetRegistry
            // population (huge legitimate allocations land in corrupted bins).
            //
            // Observed symptom: black screen on startup, log shows
            //   [Hook] *** INSTALLED *** ... (success)
            //   [Spawn] *** READY *** ... (success)
            // ... then game hangs indefinitely, only killable via Task Manager.
            //
            // Decision: don't probe Malloc. Leave s_mallocSlot = 0; the caller
            // (qm_inject.cpp:InjectIntoGroup) falls back to Realloc(nullptr,
            // size, 0) which has been observed to AV intermittently on the
            // 2nd+ tab-open. That's a known bug (one custom building may be
            // missing after the first visit) but at least the game STARTS.
            //
            // Real fix path (for later): disassemble FMallocBinned3::Malloc
            // and compare addresses against vtable to identify the slot
            // statically. Or detour the existing TArray::Add path. Both are
            // more work than we want to spend right now - intermittent miss
            // on a 2nd building injection is acceptable.
            s_mallocSlot = 0;
            QM_LOG_INFO("[Alloc] Malloc slot probe DISABLED (would corrupt heap if wrong slot is Free). TArray-grow uses Realloc(nullptr,...) fallback - known to AV intermittently on repeated tab-open, accepting that for now.");

            // ---- Inner FMalloc bypass (Plan Phase 3) ----
            // Pattern-scan for the FMallocProxy::callTarget[0] wrapper which
            // tail-jumps to the real FMallocBinned2::Malloc. If found, route
            // ReserveBuffers through it - this skips the proxy's tracker
            // list entirely, so the partial-failure state corruption that
            // froze the engine previously cannot happen.
            if (ResolveInnerMalloc(imageBase))
            {
                const int wantCount = 8;
                const size_t wantBytes = 128;   // 16 ptrs (qm_inject's kCustomGroupItemMax)
                const uint32_t wantAlign = 16;  // match the proxy wrapper default
                int got = ReserveBuffers(wantCount, wantBytes, wantAlign);
                if (got > 0)
                {
                    QM_LOG_INFO("[Alloc] reserved %d/%d buffers (%zu bytes each, align=%u) via InnerMalloc - FMallocBinned2 canary will accept these on Realloc/Free",
                        got, wantCount, wantBytes, wantAlign);
                }
                else
                {
                    QM_LOG_WARN("[Alloc] could NOT reserve any InnerMalloc-backed buffers - qm_inject will fall back to DLL-static buffers (risk: FMallocBinned2 canary AV on Realloc/Free)");
                }
            }
            else
            {
                QM_LOG_WARN("[Alloc] InnerMalloc resolve FAILED - skipping ReserveBuffers. qm_inject will fall back to DLL-static buffers (risk: FMallocBinned2 canary AV on Realloc/Free).");
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

    void* Malloc(size_t Size, uint32_t Alignment)
    {
        if (!s_resolved || !s_gmallocPtr) return nullptr;
        if (s_mallocSlot == 0) return nullptr;

        void* fmalloc = nullptr;
        __try { fmalloc = *s_gmallocPtr; }
        __except (EXCEPTION_EXECUTE_HANDLER) { return nullptr; }
        if (!fmalloc) return nullptr;

        void** vtable = nullptr;
        __try { vtable = *reinterpret_cast<void***>(fmalloc); }
        __except (EXCEPTION_EXECUTE_HANDLER) { return nullptr; }
        if (!vtable) return nullptr;

        FMallocMallocFn fn = reinterpret_cast<FMallocMallocFn>(vtable[s_mallocSlot]);
        if (!fn) return nullptr;

        void* result = nullptr;
        __try
        {
            result = fn(fmalloc, Size, Alignment);
        }
        __except (EXCEPTION_EXECUTE_HANDLER)
        {
            QM_LOG_ERROR("[Alloc] *** EXCEPTION inside GMalloc::Malloc(Size=%zu Align=%u) fmalloc=0x%p vtable=0x%p fn=0x%p slot=%u",
                Size, Alignment, fmalloc, vtable, fn, s_mallocSlot);
            return nullptr;
        }
        return result;
    }

    // Capture diagnostics from the exception filter (set before EXECUTE_HANDLER
    // returns so the handler block can log them). One-shot, only for the
    // most recent Realloc/Malloc exception.
    static thread_local uint32_t  tl_excCode       = 0;
    static thread_local void*     tl_excFaultAddr  = nullptr;
    static thread_local void*     tl_excRip        = nullptr;

    static int CaptureExceptionInfo(EXCEPTION_POINTERS* ep)
    {
        if (ep && ep->ExceptionRecord)
        {
            tl_excCode = ep->ExceptionRecord->ExceptionCode;
            // For access violations, ExceptionInformation[1] = faulting address.
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

    // ----- Reserved buffer pool ----------------------------------------------

    int ReserveBuffers(int count, size_t bytesPerBuffer, uint32_t Alignment)
    {
        if (count <= 0 || bytesPerBuffer == 0) return 0;
        if (count > kReservedBuffersMax) count = kReservedBuffersMax;

        // Already reserved? Idempotent.
        if (s_reservedBufferCount > 0) return s_reservedBufferCount;

        // Require InnerMalloc to be available. Going through the FMallocProxy
        // (Realloc(nullptr,...)) at probe time has been observed to either AV
        // on the proxy's wstring-init helper or corrupt the proxy's tracker
        // list on partial success - neither acceptable.
        if (!s_innerMallocFn)
        {
            QM_LOG_WARN("[Alloc] ReserveBuffers: InnerMalloc not resolved - refusing to allocate via FMallocProxy (would risk tracker-list corruption)");
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
            // Initialize to zero so consumers see clean pointers.
            __try { memset(p, 0, bytesPerBuffer); }
            __except (EXCEPTION_EXECUTE_HANDLER) {
                QM_LOG_WARN("[Alloc] ReserveBuffers: memset on slot %d (addr=0x%p) faulted - skipping", i, p);
                break;
            }
            s_reservedBuffers[i] = p;
            ++ok;
        }
        s_reservedBufferCount = ok;

        // Diagnostic: log the addresses so we can confirm they're in GMalloc's
        // heap (typically 0x000001...0x00000F range on x64, NOT 0x7FFD... which
        // would indicate DLL .data).
        if (ok > 0)
        {
            QM_LOG_INFO("[Alloc] reserved buffer[0]=0x%p [%d-1]=0x%p (size=%zu) via InnerMalloc",
                s_reservedBuffers[0], ok, s_reservedBuffers[ok - 1], bytesPerBuffer);
        }
        return ok;
    }

    int GetReservedBufferCount()
    {
        return s_reservedBufferCount;
    }

    void* GetReservedBuffer(int idx)
    {
        if (idx < 0 || idx >= s_reservedBufferCount) return nullptr;
        return s_reservedBuffers[idx];
    }

    size_t GetReservedBufferSize()
    {
        return s_reservedBufferSize;
    }

    // ========================================================================
    // External-buffer hook (Plan B): intercept FMalloc::Free / Realloc /
    // TryRealloc when called on DLL-static buffers we registered, so the
    // canary check in FMallocBinned2 never fires on our pointers.
    // ========================================================================

    // Registration table - small fixed size, looked up via linear scan.
    static constexpr int kMaxExternalBuffers = 32;
    struct ExternalBufferEntry { void* ptr; size_t size; };
    static ExternalBufferEntry s_externalBuffers[kMaxExternalBuffers] = {};
    static volatile LONG       s_externalBufferCount = 0;
    static SRWLOCK             s_externalBuffersLock = SRWLOCK_INIT;

    bool RegisterExternalBuffer(void* ptr, size_t size)
    {
        if (!ptr || size == 0) return false;
        AcquireSRWLockExclusive(&s_externalBuffersLock);
        // Dedup: ignore if already registered.
        for (int i = 0; i < s_externalBufferCount; ++i)
        {
            if (s_externalBuffers[i].ptr == ptr)
            {
                ReleaseSRWLockExclusive(&s_externalBuffersLock);
                return true;
            }
        }
        if (s_externalBufferCount >= kMaxExternalBuffers)
        {
            ReleaseSRWLockExclusive(&s_externalBuffersLock);
            return false;
        }
        s_externalBuffers[s_externalBufferCount].ptr  = ptr;
        s_externalBuffers[s_externalBufferCount].size = size;
        InterlockedIncrement(&s_externalBufferCount);
        ReleaseSRWLockExclusive(&s_externalBuffersLock);
        return true;
    }

    // Lookup must be lock-free in the detour fast path (Free/Realloc are hot).
    // We accept torn reads: s_externalBufferCount is monotonically increasing,
    // and individual entries are written atomically on x64 for aligned pointers.
    // Worst case: a just-registered buffer is missed by one call. Acceptable
    // since registration happens at init, hooks are installed AFTER.
    static int FindExternalBufferIdx(void* ptr)
    {
        if (!ptr) return -1;
        const int n = s_externalBufferCount;
        for (int i = 0; i < n; ++i)
        {
            if (s_externalBuffers[i].ptr == ptr) return i;
        }
        return -1;
    }

    bool IsExternalBuffer(void* ptr) { return FindExternalBufferIdx(ptr) >= 0; }

    // ----- Slot detection via hde64 disassembly -----------------------------
    //
    // FMallocBinned2::Free and ::Realloc both contain the canary check:
    //   cmp byte ptr [<ptr> - <off>], 0E3h
    // ...but the compiler can emit it in several encodings:
    //   80 /7 imm8 = 0xE3  (cmp r/m8,  0xE3)              - most common
    //   3C E3              (cmp AL,    0xE3)              - if AL was loaded first
    //   83 /7 imm8 = 0xE3  (cmp r/m32, 0xE3 sign-ext)     - movzx eax,[..] then cmp
    //   81 /7 imm32 = 0xE3 (cmp r/m32, 0xE3 zero-ext)     - rare
    //
    // Worse: in shipping UE5 builds the check often lives in a CALLED helper
    // (FMallocBinned2::FreeExternal / ::ReallocExternal / FFreeBlock::IsCanaryOk),
    // not inline in the vtable function. So we must follow `call rel32` one
    // level deep to find it.
    //
    // Heuristic:
    //   1. Scan vtable[slot] for kRootScanBytes bytes.
    //   2. Whenever a canary-pattern matches, count it.
    //   3. Whenever a `call rel32` is seen, record the target (up to N).
    //   4. After the root scan, scan each recorded callee for kCalleeScanBytes
    //      bytes, also counting canary patterns.
    //   5. A slot is a "canary-check slot" if inline OR any callee saw the
    //      pattern.

    static constexpr int    kVtableScanSlots    = 16;
    static constexpr size_t kRootScanBytes      = 2048;
    static constexpr size_t kCalleeScanBytes    = 1024;
    static constexpr int    kMaxCallsPerSlot    = 32;

    struct SlotProfile
    {
        void*    fn;
        bool     hasCanaryCheck;
        int      canaryHitsInline;     // hits in the vtable function itself
        int      canaryHitsViaCalls;   // hits in one-level-deep callees
        int      e3BytesSeenInline;    // ANY 0xE3 byte in instr stream (diag)
        int      callsRecorded;
        int      callsScanned;
        int      callsWithCanary;
        size_t   inlineBytesScanned;
        bool     sawRet;
        uint8_t  firstBytes[32];
    };

    static SlotProfile s_slotProfiles[kVtableScanSlots] = {};
    static int         s_slotProfileCount = 0;

    // True if instruction matches one of the canary-cmp encodings.
    static inline bool IsCanaryCmp(const hde64s& hs)
    {
        // cmp r/m8, imm8 (opcode 80 with /7 modrm extension)
        if (hs.opcode == 0x80 && hs.modrm_reg == 7 &&
            (hs.flags & F_IMM8) && hs.imm.imm8 == 0xE3)
            return true;
        // cmp AL, imm8 (opcode 3C, no modrm)
        if (hs.opcode == 0x3C &&
            (hs.flags & F_IMM8) && hs.imm.imm8 == 0xE3)
            return true;
        // cmp r/m32, imm8 sign-extended (opcode 83 with /7 modrm)
        if (hs.opcode == 0x83 && hs.modrm_reg == 7 &&
            (hs.flags & F_IMM8) && hs.imm.imm8 == 0xE3)
            return true;
        // cmp r/m32, imm32 (opcode 81 with /7 modrm) where imm32 happens to be 0xE3
        if (hs.opcode == 0x81 && hs.modrm_reg == 7 &&
            (hs.flags & F_IMM32) && hs.imm.imm32 == 0xE3)
            return true;
        return false;
    }

    // Scan a function body up to maxBytes (or first ret), counting canary hits
    // and (if collectCalls != nullptr) recording call rel32 targets.
    // SEH-guarded. Returns bytes scanned.
    static size_t ScanFunctionBody(const uint8_t* code, size_t maxBytes,
                                   int* outCanaryHits, int* outE3Bytes,
                                   void** collectCalls, int* collectCount, int collectMax,
                                   bool* outSawRet)
    {
        if (outCanaryHits) *outCanaryHits = 0;
        if (outE3Bytes)    *outE3Bytes    = 0;
        if (collectCount)  *collectCount  = 0;
        if (outSawRet)     *outSawRet     = false;
        if (!code || maxBytes == 0) return 0;

        size_t offset = 0;
        __try
        {
            while (offset < maxBytes)
            {
                hde64s hs = {};
                unsigned int len = hde64_disasm(code + offset, &hs);
                if (len == 0 || (hs.flags & F_ERROR)) break;
                if (len > 15) break;

                // Count any 0xE3 byte in the instruction stream (diag).
                if (outE3Bytes)
                {
                    for (unsigned int b = 0; b < len; ++b)
                        if (code[offset + b] == 0xE3) (*outE3Bytes)++;
                }

                if (IsCanaryCmp(hs))
                {
                    if (outCanaryHits) (*outCanaryHits)++;
                }

                // Record `call rel32` target (opcode E8 + rel32).
                if (hs.opcode == 0xE8 && (hs.flags & F_RELATIVE) &&
                    collectCalls && collectCount && *collectCount < collectMax)
                {
                    int32_t rel = (int32_t)hs.imm.imm32;
                    uintptr_t target = reinterpret_cast<uintptr_t>(code + offset + len) + rel;
                    collectCalls[*collectCount] = reinterpret_cast<void*>(target);
                    (*collectCount)++;
                }

                if (hs.opcode == 0xC3 || hs.opcode == 0xC2)
                {
                    if (outSawRet) *outSawRet = true;
                    offset += len;
                    break;
                }

                offset += len;
            }
        }
        __except (EXCEPTION_EXECUTE_HANDLER)
        {
            // Truncate at whatever we got.
        }
        return offset;
    }

    // Profile one vtable slot: root scan + follow each `call rel32` one level.
    static void ProfileFunction(void* fn, SlotProfile& out)
    {
        out.fn                  = fn;
        out.hasCanaryCheck      = false;
        out.canaryHitsInline    = 0;
        out.canaryHitsViaCalls  = 0;
        out.e3BytesSeenInline   = 0;
        out.callsRecorded       = 0;
        out.callsScanned        = 0;
        out.callsWithCanary     = 0;
        out.inlineBytesScanned  = 0;
        out.sawRet              = false;
        memset(out.firstBytes, 0, sizeof(out.firstBytes));
        if (!fn) return;
        if (!IsReadableLen(fn, 32)) return;

        const uint8_t* code = reinterpret_cast<const uint8_t*>(fn);
        __try { for (int i = 0; i < 32; ++i) out.firstBytes[i] = code[i]; }
        __except (EXCEPTION_EXECUTE_HANDLER) { return; }

        void* calls[kMaxCallsPerSlot] = {};
        int   callCount = 0;
        int   inlineHits = 0, inlineE3 = 0;
        bool  sawRet = false;
        out.inlineBytesScanned = ScanFunctionBody(
            code, kRootScanBytes,
            &inlineHits, &inlineE3,
            calls, &callCount, kMaxCallsPerSlot,
            &sawRet);
        out.canaryHitsInline  = inlineHits;
        out.e3BytesSeenInline = inlineE3;
        out.callsRecorded     = callCount;
        out.sawRet            = sawRet;

        // For each unique call target, scan callee body.
        for (int i = 0; i < callCount; ++i)
        {
            void* target = calls[i];
            if (!target) continue;
            // De-dup against earlier entries.
            bool dup = false;
            for (int j = 0; j < i; ++j) if (calls[j] == target) { dup = true; break; }
            if (dup) continue;
            if (!IsExecutable(target)) continue;
            if (!IsReadableLen(target, 16)) continue;

            int calleeHits = 0;
            ScanFunctionBody(
                reinterpret_cast<const uint8_t*>(target), kCalleeScanBytes,
                &calleeHits, nullptr,
                nullptr, nullptr, 0, nullptr);
            out.callsScanned++;
            if (calleeHits > 0)
            {
                out.canaryHitsViaCalls += calleeHits;
                out.callsWithCanary++;
            }
        }

        out.hasCanaryCheck = (out.canaryHitsInline + out.canaryHitsViaCalls) > 0;
    }

    int DetectCanaryCheckSlots(int maxSlots)
    {
        if (!s_resolved || !s_gmallocPtr) return 0;
        if (maxSlots <= 0 || maxSlots > kVtableScanSlots) maxSlots = kVtableScanSlots;

        void* fmalloc = nullptr;
        __try { fmalloc = *s_gmallocPtr; }
        __except (EXCEPTION_EXECUTE_HANDLER) { return 0; }
        if (!fmalloc) return 0;

        void** vtable = nullptr;
        __try { vtable = *reinterpret_cast<void***>(fmalloc); }
        __except (EXCEPTION_EXECUTE_HANDLER) { return 0; }
        if (!vtable) return 0;
        if (!IsReadableLen(vtable, maxSlots * sizeof(void*))) return 0;

        memset(s_slotProfiles, 0, sizeof(s_slotProfiles));
        s_slotProfileCount = 0;

        int canaryHits = 0;
        for (int slot = 0; slot < maxSlots; ++slot)
        {
            void* fn = nullptr;
            __try { fn = vtable[slot]; }
            __except (EXCEPTION_EXECUTE_HANDLER) { continue; }
            if (!fn) continue;
            if (!IsExecutable(fn)) continue;

            ProfileFunction(fn, s_slotProfiles[slot]);
            s_slotProfileCount = slot + 1;
            if (s_slotProfiles[slot].hasCanaryCheck) canaryHits++;

            const SlotProfile& p = s_slotProfiles[slot];
            const uint8_t* b = p.firstBytes;
            QM_LOG_INFO("[Alloc] vtable[%2d]=0x%p prologue=%02X %02X %02X %02X %02X %02X %02X %02X %02X %02X %02X %02X %02X %02X %02X %02X scanned=%zu calls=%d/%d canary=%s (inline=%d viaCalls=%d e3bytes=%d%s)",
                slot, fn,
                b[0],b[1],b[2],b[3],b[4],b[5],b[6],b[7],
                b[8],b[9],b[10],b[11],b[12],b[13],b[14],b[15],
                p.inlineBytesScanned,
                p.callsScanned, p.callsRecorded,
                p.hasCanaryCheck ? "YES" : "no",
                p.canaryHitsInline, p.canaryHitsViaCalls, p.e3BytesSeenInline,
                p.sawRet ? " ret" : "");
        }

        QM_LOG_INFO("[Alloc] DetectCanaryCheckSlots: scanned %d slots, %d contain 0xE3 canary check pattern (root+1 level deep; FMallocBinned2 Free/Realloc/TryRealloc family)",
            s_slotProfileCount, canaryHits);
        return canaryHits;
    }

    // ----- MinHook installation + detour ------------------------------------
    //
    // We use ONE detour signature - the "Realloc shape": void*(this, ptr, size, align).
    // This covers Free naturally because:
    //   - Free's real signature is (this, ptr) - r8 and r9 are garbage but we
    //     never read them when handling external-buffer ptrs.
    //   - Free's return value is void - returning rax=0 is harmless (caller
    //     discards it).
    //   - For non-external ptrs we call the original trampoline with the
    //     original arg values (r8/r9 still hold whatever the caller passed),
    //     so passthrough is correct.
    //
    // Per-slot trampolines because MinHook gives us one trampoline per hook.

    typedef void* (__fastcall *AllocFn4)(void* This, void* Original, size_t NewSize, uint32_t Alignment);

    static AllocFn4 s_origPerSlot[kVtableScanSlots] = {};
    static void*    s_targetPerSlot[kVtableScanSlots] = {};
    static int      s_hooksInstalled = 0;

    // Diagnostic counters
    static volatile LONG s_externalInterceptsTotal     = 0;
    static volatile LONG s_externalInterceptsFreeLike  = 0;  // NewSize == 0
    static volatile LONG s_externalInterceptsRealloc   = 0;  // NewSize > 0
    static volatile LONG s_externalInterceptsReallocFresh = 0;  // fresh alloc success
    static volatile LONG s_externalInterceptsReallocFail  = 0;  // fresh alloc failed

    // Core detour body - shared between per-slot wrappers.
    static void* HandleExternalBuffer(int slotIdx, void* This, void* Original, size_t NewSize, uint32_t Alignment)
    {
        int idx = FindExternalBufferIdx(Original);
        size_t oldSize = (idx >= 0) ? s_externalBuffers[idx].size : 0;

        InterlockedIncrement(&s_externalInterceptsTotal);

        if (NewSize == 0)
        {
            // Free-shape: just don't call the original. Buffer stays alive
            // in DLL .data - which is exactly what we want (the ring buffer
            // is reused / RestorePrevSwapAtSlot nulled the TArray header by
            // the time we reach here, so no later reads either).
            long n = InterlockedIncrement(&s_externalInterceptsFreeLike);
            if (n <= 10 || (n % 100) == 0)
                QM_LOG_INFO("[Alloc][Hook] intercept Free(ptr=0x%p size=%zu) on slot=%d - returning without calling original (intercepted#%ld)",
                    Original, oldSize, slotIdx, n);
            return nullptr;
        }

        // Realloc-shape with non-zero size: allocate fresh, memcpy, return.
        // We do NOT free the original (it's our static buffer - leave it).
        InterlockedIncrement(&s_externalInterceptsRealloc);

        // Try malloc via GMalloc's Realloc(nullptr,...) - this is the same
        // call path we use elsewhere. If it AVs we fall back to nullptr
        // which makes the TArray empty (acceptable degradation).
        void* fresh = nullptr;
        __try
        {
            // Use FMalloc's Realloc(nullptr, ...) via the resolved vtable
            // (this thread/context is presumably warm - we're inside the
            // game's Free/Realloc call). Don't call our QmAlloc::Realloc
            // wrapper because that would re-enter through this same hook
            // detour and recurse.
            void** vtable = *reinterpret_cast<void***>(This);
            AllocFn4 originalFn = s_origPerSlot[slotIdx];
            if (originalFn)
            {
                // The original (trampoline) call with Original=nullptr is
                // equivalent to Malloc. If slot is actually Realloc and
                // accepts nullptr, this allocates fresh memory.
                fresh = originalFn(This, nullptr, NewSize, Alignment);
            }
        }
        __except (EXCEPTION_EXECUTE_HANDLER) { fresh = nullptr; }

        if (!fresh)
        {
            long n = InterlockedIncrement(&s_externalInterceptsReallocFail);
            if (n <= 10)
                QM_LOG_WARN("[Alloc][Hook] intercept Realloc(ptr=0x%p oldSize=%zu newSize=%zu align=%u) on slot=%d - fresh alloc failed, returning null (fail#%ld)",
                    Original, oldSize, NewSize, Alignment, slotIdx, n);
            return nullptr;
        }

        // Copy as much as we know is valid.
        size_t copySize = (oldSize > 0 && oldSize < NewSize) ? oldSize : NewSize;
        if (oldSize == 0) copySize = NewSize;  // unknown, copy full (Original is presumably readable)
        __try
        {
            memcpy(fresh, Original, copySize);
        }
        __except (EXCEPTION_EXECUTE_HANDLER)
        {
            // Bad memcpy - leave fresh zero-initialized (Malloc may have
            // returned zero'd memory anyway for first-use bins).
            memset(fresh, 0, NewSize);
        }

        long n = InterlockedIncrement(&s_externalInterceptsReallocFresh);
        if (n <= 10 || (n % 50) == 0)
            QM_LOG_INFO("[Alloc][Hook] intercept Realloc(ptr=0x%p oldSize=%zu newSize=%zu align=%u) on slot=%d - returned fresh=0x%p (copied %zu bytes; fresh#%ld)",
                Original, oldSize, NewSize, Alignment, slotIdx, fresh, copySize, n);

        return fresh;
    }

    // Macro-generated per-slot detours. MinHook needs one detour function
    // pointer per target, so we predeclare 16 wrappers.
#define QM_DETOUR_SLOT(N) \
    static void* __fastcall Detour_Slot##N(void* This, void* Original, size_t NewSize, uint32_t Alignment) \
    { \
        if (FindExternalBufferIdx(Original) >= 0) \
            return HandleExternalBuffer(N, This, Original, NewSize, Alignment); \
        AllocFn4 fn = s_origPerSlot[N]; \
        if (!fn) return nullptr; \
        return fn(This, Original, NewSize, Alignment); \
    }

    QM_DETOUR_SLOT(0)  QM_DETOUR_SLOT(1)  QM_DETOUR_SLOT(2)  QM_DETOUR_SLOT(3)
    QM_DETOUR_SLOT(4)  QM_DETOUR_SLOT(5)  QM_DETOUR_SLOT(6)  QM_DETOUR_SLOT(7)
    QM_DETOUR_SLOT(8)  QM_DETOUR_SLOT(9)  QM_DETOUR_SLOT(10) QM_DETOUR_SLOT(11)
    QM_DETOUR_SLOT(12) QM_DETOUR_SLOT(13) QM_DETOUR_SLOT(14) QM_DETOUR_SLOT(15)

#undef QM_DETOUR_SLOT

    static void* GetDetourForSlot(int slot)
    {
        switch (slot)
        {
            case 0:  return reinterpret_cast<void*>(&Detour_Slot0);
            case 1:  return reinterpret_cast<void*>(&Detour_Slot1);
            case 2:  return reinterpret_cast<void*>(&Detour_Slot2);
            case 3:  return reinterpret_cast<void*>(&Detour_Slot3);
            case 4:  return reinterpret_cast<void*>(&Detour_Slot4);
            case 5:  return reinterpret_cast<void*>(&Detour_Slot5);
            case 6:  return reinterpret_cast<void*>(&Detour_Slot6);
            case 7:  return reinterpret_cast<void*>(&Detour_Slot7);
            case 8:  return reinterpret_cast<void*>(&Detour_Slot8);
            case 9:  return reinterpret_cast<void*>(&Detour_Slot9);
            case 10: return reinterpret_cast<void*>(&Detour_Slot10);
            case 11: return reinterpret_cast<void*>(&Detour_Slot11);
            case 12: return reinterpret_cast<void*>(&Detour_Slot12);
            case 13: return reinterpret_cast<void*>(&Detour_Slot13);
            case 14: return reinterpret_cast<void*>(&Detour_Slot14);
            case 15: return reinterpret_cast<void*>(&Detour_Slot15);
            default: return nullptr;
        }
    }

    int InstallExternalBufferHooks()
    {
        if (!s_resolved) return 0;
        if (s_hooksInstalled > 0) return s_hooksInstalled;  // idempotent
        if (s_slotProfileCount == 0)
        {
            QM_LOG_WARN("[Alloc][Hook] InstallExternalBufferHooks called before DetectCanaryCheckSlots - aborting");
            return 0;
        }

        // Safety: count canary candidates. If 0 or >4 we don't trust the
        // detection (FMalloc has Free + Realloc + TryRealloc as canary
        // checkers - 3 expected, allow 2..4).
        int candidateCount = 0;
        for (int i = 0; i < s_slotProfileCount; ++i)
            if (s_slotProfiles[i].hasCanaryCheck) candidateCount++;

        if (candidateCount < 1 || candidateCount > 6)
        {
            QM_LOG_WARN("[Alloc][Hook] suspicious canary slot count = %d (expected 2..4 for Free/Realloc/TryRealloc) - declining to install hooks for safety",
                candidateCount);
            return 0;
        }

        int installed = 0;
        for (int slot = 0; slot < s_slotProfileCount; ++slot)
        {
            if (!s_slotProfiles[slot].hasCanaryCheck) continue;
            void* target = s_slotProfiles[slot].fn;
            void* detour = GetDetourForSlot(slot);
            if (!target || !detour) continue;

            LPVOID original = nullptr;
            MH_STATUS rc = MH_CreateHook(target, detour, &original);
            if (rc != MH_OK)
            {
                QM_LOG_WARN("[Alloc][Hook] MH_CreateHook slot=%d target=0x%p detour=0x%p FAILED rc=%d (%s) - skipping",
                    slot, target, detour, (int)rc, MH_StatusToString(rc));
                continue;
            }
            s_origPerSlot[slot]   = reinterpret_cast<AllocFn4>(original);
            s_targetPerSlot[slot] = target;

            rc = MH_EnableHook(target);
            if (rc != MH_OK)
            {
                QM_LOG_WARN("[Alloc][Hook] MH_EnableHook slot=%d target=0x%p FAILED rc=%d (%s)",
                    slot, target, (int)rc, MH_StatusToString(rc));
                MH_RemoveHook(target);
                s_origPerSlot[slot]   = nullptr;
                s_targetPerSlot[slot] = nullptr;
                continue;
            }

            installed++;
            QM_LOG_INFO("[Alloc][Hook] *** INSTALLED *** slot=%d target=0x%p detour=0x%p trampoline=0x%p (canary-check function)",
                slot, target, detour, original);
        }

        s_hooksInstalled = installed;
        QM_LOG_INFO("[Alloc][Hook] InstallExternalBufferHooks: %d/%d canary-check slots hooked; %d external buffers registered",
            installed, candidateCount, s_externalBufferCount);
        return installed;
    }

    // ----- Offline analysis hex dump (Plan A diagnostic) ---------------------
    //
    // The inline canary scan failed (0 hits in 16 slots, e3bytes=0 everywhere)
    // because the FMallocBinned2 canary check lives 2+ levels deep or uses an
    // encoding we don't recognize. To resolve this without further guesswork,
    // dump raw code bytes for vtable[0..N] plus all unique call/jmp rel32
    // targets discovered, so they can be analyzed in Ghidra / ndisasm offline.

    static void HexDumpRegion(const char* label, const void* addr, size_t bytes)
    {
        if (!addr || bytes == 0) return;
        if (!IsReadableLen(addr, 16))
        {
            QM_LOG_INFO("[Alloc][HexDump] %s addr=0x%p UNREADABLE (skipped)", label, addr);
            return;
        }

        QM_LOG_INFO("[Alloc][HexDump] %s BEGIN addr=0x%p size=%zu", label, addr, bytes);

        const uint8_t* p = static_cast<const uint8_t*>(addr);
        constexpr size_t kRowBytes = 32;
        const size_t rows = (bytes + kRowBytes - 1) / kRowBytes;

        for (size_t r = 0; r < rows; ++r)
        {
            const size_t rowStart = r * kRowBytes;
            const size_t rowLen   = (rowStart + kRowBytes > bytes)
                                  ? (bytes - rowStart) : kRowBytes;

            if (!IsReadableLen(p + rowStart, rowLen))
            {
                QM_LOG_INFO("[Alloc][HexDump] %s +0x%03zX: <unreadable - stopping>",
                    label, rowStart);
                break;
            }

            char line[8 + 3 * kRowBytes + 1];
            char* out = line;
            __try
            {
                for (size_t i = 0; i < rowLen; ++i)
                {
                    const unsigned v = p[rowStart + i];
                    *out++ = "0123456789ABCDEF"[(v >> 4) & 0xF];
                    *out++ = "0123456789ABCDEF"[v & 0xF];
                    *out++ = ' ';
                }
                *out = 0;
            }
            __except (EXCEPTION_EXECUTE_HANDLER)
            {
                QM_LOG_INFO("[Alloc][HexDump] %s +0x%03zX: <fault mid-row - stopping>",
                    label, rowStart);
                break;
            }

            QM_LOG_INFO("[Alloc][HexDump] %s +0x%03zX: %s", label, rowStart, line);
        }

        QM_LOG_INFO("[Alloc][HexDump] %s END", label);
    }

    // Scan a function's first `maxBytes` for call/jmp rel32 instructions and
    // record their absolute targets into `out[]` up to `maxTargets`. Stops at
    // ret or unconditional jmp/short-jmp. SEH-guarded.
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

                // Stop at ret or unconditional non-call jmp/short-jmp.
                if (hs.opcode == 0xC3 || hs.opcode == 0xC2) { offset += len; break; }
                if (hs.opcode == 0xE9 || hs.opcode == 0xEB) { offset += len; break; }

                offset += len;
            }
        }
        __except (EXCEPTION_EXECUTE_HANDLER) { /* truncate */ }

        return count;
    }

    void DumpVtableSlotsHexForOfflineAnalysis(int maxSlots, size_t bytesPerSlot)
    {
        if (!s_resolved || !s_gmallocPtr)
        {
            QM_LOG_WARN("[Alloc][HexDump] DumpVtableSlots: GMalloc not resolved - skipping");
            return;
        }
        if (maxSlots <= 0 || maxSlots > 16)        maxSlots     = 16;
        if (bytesPerSlot == 0 || bytesPerSlot > 4096) bytesPerSlot = 512;

        void* fmalloc = nullptr;
        __try { fmalloc = *s_gmallocPtr; }
        __except (EXCEPTION_EXECUTE_HANDLER) { return; }
        if (!fmalloc) return;

        void** vtable = nullptr;
        __try { vtable = *reinterpret_cast<void***>(fmalloc); }
        __except (EXCEPTION_EXECUTE_HANDLER) { return; }
        if (!vtable) return;
        if (!IsReadableLen(vtable, maxSlots * sizeof(void*))) return;

        QM_LOG_INFO("[Alloc][HexDump] === BEGIN offline analysis dump (vtable=0x%p, %d slots, %zu bytes/slot) ===",
            vtable, maxSlots, bytesPerSlot);
        QM_LOG_INFO("[Alloc][HexDump] === to disassemble: feed hex into ndisasm -b64 - (or Ghidra: load as raw binary at addr=<addr>) ===");

        // Phase 1: collect unique call/jmp targets across all slots into one
        // dedup'd table so we dump each callee only once.
        constexpr int kMaxAllTargets = 128;
        void* allTargets[kMaxAllTargets] = {};
        int   allTargetCount = 0;

        // Phase 2: dump each vtable slot + collect its call/jmp targets.
        for (int slot = 0; slot < maxSlots; ++slot)
        {
            void* fn = nullptr;
            __try { fn = vtable[slot]; }
            __except (EXCEPTION_EXECUTE_HANDLER) { continue; }
            if (!fn) continue;

            char label[24];
            // Two-digit slot for column alignment in log grep.
            snprintf(label, sizeof(label), "vtable[%2d]", slot);
            HexDumpRegion(label, fn, bytesPerSlot);

            // Collect targets from this slot (up to 16 per slot, dedup later).
            void* slotTargets[16] = {};
            const int found = CollectCallJmpTargets(fn, bytesPerSlot, slotTargets, 16);
            for (int i = 0; i < found; ++i)
            {
                void* t = slotTargets[i];
                if (!t) continue;
                bool dup = false;
                for (int j = 0; j < allTargetCount; ++j)
                    if (allTargets[j] == t) { dup = true; break; }
                if (!dup && allTargetCount < kMaxAllTargets)
                    allTargets[allTargetCount++] = t;
            }
        }

        // Phase 3: dump every unique callee.
        QM_LOG_INFO("[Alloc][HexDump] === %d unique call/jmp rel32 targets across all slots ===",
            allTargetCount);
        for (int i = 0; i < allTargetCount; ++i)
        {
            void* t = allTargets[i];
            if (!IsExecutable(t))
            {
                QM_LOG_INFO("[Alloc][HexDump] callTarget[%2d]=0x%p NOT EXECUTABLE (skipped)", i, t);
                continue;
            }
            char label[32];
            snprintf(label, sizeof(label), "callTarget[%2d]", i);
            HexDumpRegion(label, t, bytesPerSlot);
        }

        QM_LOG_INFO("[Alloc][HexDump] === END offline analysis dump ===");
    }

    // ========================================================================
    // Inner FMallocBinned2 bypass (Plan Phase 3)
    //
    // Pattern-scan .text for the FMallocProxy::callTarget[0] wrapper. Exact
    // 21-byte prologue + 5-byte tail-jmp:
    //
    //   48 85 C9              test  rcx, rcx
    //   B8 01 00 00 00        mov   eax, 1
    //   BA 10 00 00 00        mov   edx, 0x10
    //   48 0F 45 C1           cmovne rax, rcx
    //   48 8B C8              mov   rcx, rax
    //   E9 ?? ?? ?? ??        jmp   <inner FMalloc::Malloc>
    //
    // This sequence is unique enough that we expect 1-2 matches in shipping
    // UE5 binaries (FMallocProxy generates similar wrappers for Malloc /
    // Realloc / Free family). We accept the first match whose rel32 decodes
    // to an executable address with a sane FMallocBinned2-style prologue.
    // ========================================================================

    bool ResolveInnerMalloc(uintptr_t imageBase)
    {
        (void)imageBase; // unused - we drive from vtable now
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
        //   48 85 C9              test  rcx, rcx         (3)
        //   B8 01 00 00 00        mov   eax, 1            (5)
        //   BA 10 00 00 00        mov   edx, 0x10         (5)
        //   48 0F 45 C1           cmovne rax, rcx         (4)
        //   48 8B C8              mov   rcx, rax          (3)  -> 20 bytes
        //   E9 ?? ?? ?? ??        jmp   <inner FMalloc::Malloc> (5) -> 25
        //
        // Hex-dump analysis showed callTarget[0] (= a wrapper used by the
        // proxy's Realloc-shape vtable[4]) starts with EXACTLY this sequence.
        // Wrapper lives ~137MB away from vtable - probably in a different
        // module/section than the EXE's primary .text, so a section-scan
        // misses it. Instead we walk vtable[0..15] with hde64, collect every
        // E8/E9 rel32 target, dedup, then byte-check each target against the
        // signature. Guaranteed-reachable because the vtable itself
        // calls them.
        static const uint8_t kSig[20] = {
            0x48, 0x85, 0xC9,                              // test rcx, rcx
            0xB8, 0x01, 0x00, 0x00, 0x00,                  // mov eax, 1
            0xBA, 0x10, 0x00, 0x00, 0x00,                  // mov edx, 0x10
            0x48, 0x0F, 0x45, 0xC1,                        // cmovne rax, rcx
            0x48, 0x8B, 0xC8,                              // mov rcx, rax
        };

        const DWORD t0 = GetTickCount();

        // ---- Phase 1: collect all unique call/jmp targets across vtable[0..15] ----
        constexpr int kMaxTargets = 256;
        void* targets[kMaxTargets] = {};
        int   targetCount = 0;

        // Also seed with the vtable slots themselves - if a wrapper is
        // installed directly as a slot (rare but cheap to check), we still
        // find it.
        for (int slot = 0; slot < 16 && targetCount < kMaxTargets; ++slot)
        {
            void* fn = nullptr;
            __try { fn = vtable[slot]; }
            __except (EXCEPTION_EXECUTE_HANDLER) { continue; }
            if (!fn || !IsExecutable(fn)) continue;
            targets[targetCount++] = fn;
        }

        // Now walk each vtable slot and collect call/jmp rel32 targets.
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

        // ---- Phase 2: also follow one level deeper. callTarget[0] is reached
        // via vtable[N] -> [some helper] -> [our wrapper]. We disassemble the
        // FIRST tier of targets to extend the search. We cap the depth so the
        // scan stays bounded.
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

        // ---- Phase 3: pattern-check each collected target ----
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
            QM_LOG_WARN("[Alloc] ResolveInnerMalloc: no FMallocProxy wrapper pattern found among %d reachable targets - cannot bypass proxy",
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
