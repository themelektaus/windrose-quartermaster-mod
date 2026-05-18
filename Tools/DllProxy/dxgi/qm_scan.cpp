// Quartermaster runtime offset auto-discovery - implementation
#define _CRT_SECURE_NO_WARNINGS
#include <windows.h>
#include <stdint.h>
#include <string.h>

#include "qm_scan.hpp"

namespace QmScan
{
    // ---- PE section enumeration -----------------------------------------------

    struct SectionInfo
    {
        uintptr_t start;
        uintptr_t size;
        bool      readable;
        bool      writable;
        bool      executable;
        char      name[9];   // 8 + null
    };

    // Walk the IMAGE_NT_HEADERS section table of the main exe. We fill up to
    // maxOut entries; returns the count written. Section iteration is bounded
    // so a malformed PE header can't run us off into the weeds.
    static uint32_t EnumerateSections(uintptr_t imageBase, SectionInfo* out, uint32_t maxOut)
    {
        if (!imageBase || !out || maxOut == 0) return 0;
        uint32_t written = 0;

        __try
        {
            auto dos = reinterpret_cast<const IMAGE_DOS_HEADER*>(imageBase);
            if (dos->e_magic != IMAGE_DOS_SIGNATURE) return 0;

            auto nt = reinterpret_cast<const IMAGE_NT_HEADERS64*>(imageBase + dos->e_lfanew);
            if (nt->Signature != IMAGE_NT_SIGNATURE) return 0;
            if (nt->FileHeader.NumberOfSections > 96) return 0; // sanity

            auto sec = IMAGE_FIRST_SECTION(nt);
            for (WORD i = 0; i < nt->FileHeader.NumberOfSections && written < maxOut; ++i)
            {
                SectionInfo& info = out[written++];
                info.start      = imageBase + sec[i].VirtualAddress;
                info.size       = sec[i].Misc.VirtualSize;
                info.readable   = (sec[i].Characteristics & IMAGE_SCN_MEM_READ)    != 0;
                info.writable   = (sec[i].Characteristics & IMAGE_SCN_MEM_WRITE)   != 0;
                info.executable = (sec[i].Characteristics & IMAGE_SCN_MEM_EXECUTE) != 0;
                memcpy(info.name, sec[i].Name, 8);
                info.name[8] = '\0';
            }
        }
        __except (EXCEPTION_EXECUTE_HANDLER)
        {
            return 0;
        }

        return written;
    }

    // ---- Safe pointer probing -------------------------------------------------

    // Probe a memory range with VirtualQuery. Returns true if every byte is in
    // a committed page with readable protection. Skip-fast on uncommitted/free.
    static bool IsReadable(const void* ptr, size_t bytes)
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

    // ---- GObjects validation --------------------------------------------------
    //
    // TUObjectArray layout we expect (qm_ue.hpp):
    //   0x00 FUObjectItem** Objects     (chunked: Objects[chunk][indexInChunk])
    //   0x08 padding 8 bytes
    //   0x10 int32 MaxElements
    //   0x14 int32 NumElements
    //   0x18 int32 MaxChunks
    //   0x1C int32 NumChunks
    //
    // FUObjectItem layout: { UObject* Object; uint8 _pad[0x10]; } size 0x18.
    //
    // ElementsPerChunk is hard-coded to 0x10000 in our runtime helpers.

    bool ValidateGObjectsCandidate(void* candidate)
    {
        if (!candidate) return false;
        if (!IsReadable(candidate, 0x20)) return false;

        __try
        {
            auto* p = static_cast<uint8_t*>(candidate);

            void** objects     = *reinterpret_cast<void***>(p + 0x00);
            int32_t maxElems   = *reinterpret_cast<int32_t*>(p + 0x10);
            int32_t numElems   = *reinterpret_cast<int32_t*>(p + 0x14);
            int32_t maxChunks  = *reinterpret_cast<int32_t*>(p + 0x18);
            int32_t numChunks  = *reinterpret_cast<int32_t*>(p + 0x1C);

            // Range checks - tuned for typical UE5 builds.
            if (maxElems < 0x10000 || maxElems > 0x600000) return false;
            if (numElems < 0 || numElems > maxElems) return false;
            if (maxChunks < 1 || maxChunks > 100) return false;
            if (numChunks < 1 || numChunks > maxChunks) return false;

            // ElementsPerChunk = MaxElements / MaxChunks - should be 0x10000.
            const int32_t elemsPerChunk = maxElems / maxChunks;
            if (elemsPerChunk < 0x4000 || elemsPerChunk > 0x100000) return false;

            // Objects table itself must be readable - 8 bytes per chunk pointer.
            if (!objects) return false;
            if (!IsReadable(objects, static_cast<size_t>(numChunks) * sizeof(void*))) return false;

            // Probe first chunk pointer - must point to readable memory.
            void* firstChunk = objects[0];
            if (!firstChunk) return false;
            if (!IsReadable(firstChunk, sizeof(void*) * 4)) return false;

            // First FUObjectItem.Object - should be a UObject pointer (or null
            // if the very first slot is unused). Probe a few slots to find a
            // valid UObject and sanity-check its layout.
            uint8_t* chunkBytes = static_cast<uint8_t*>(firstChunk);
            int validObjects = 0;
            for (int i = 0; i < 16; ++i)
            {
                void* obj = *reinterpret_cast<void**>(chunkBytes + i * 0x18);
                if (!obj) continue;
                if (!IsReadable(obj, 0x28)) continue;

                // UObject layout: vtable @ 0, class @ +0x10, name @ +0x18.
                void* vtable = *reinterpret_cast<void**>(static_cast<uint8_t*>(obj) + 0x00);
                void* cls    = *reinterpret_cast<void**>(static_cast<uint8_t*>(obj) + 0x10);
                if (!vtable || !IsExecutable(*reinterpret_cast<void**>(vtable))) continue;
                if (!cls) continue;
                ++validObjects;
                if (validObjects >= 3) break;
            }

            // Need at least 3 plausible UObject entries to call this a real
            // TUObjectArray. Less than that is likely a fluke alignment.
            if (validObjects < 3) return false;

            return true;
        }
        __except (EXCEPTION_EXECUTE_HANDLER)
        {
            return false;
        }
    }

    // ---- GObjects scan --------------------------------------------------------

    static void* ScanGObjectsImpl(uintptr_t imageBase, uint32_t* outTested, uint32_t* outFailed)
    {
        if (outTested) *outTested = 0;
        if (outFailed) *outFailed = 0;
        if (!imageBase) return nullptr;

        SectionInfo sections[32]{};
        uint32_t secCount = EnumerateSections(imageBase, sections, 32);
        if (secCount == 0) return nullptr;

        for (uint32_t s = 0; s < secCount; ++s)
        {
            const SectionInfo& sec = sections[s];

            // We want writable data sections (.data, .bss). Skip code (.text)
            // and read-only (.rdata) to keep the scan fast.
            if (!sec.writable || sec.executable) continue;
            if (sec.size == 0) continue;
            // Sections with very short names ("/x" etc.) - keep them, they
            // might be .bss equivalents in some link configs.

            // Walk 8-byte aligned positions. TUObjectArray is 0x20 bytes -
            // we need at least that much trailing room.
            const uintptr_t start = (sec.start + 7) & ~7ull;
            const uintptr_t end   = sec.start + sec.size;
            if (end < start || end - start < 0x20) continue;

            for (uintptr_t addr = start; addr + 0x20 <= end; addr += 8)
            {
                if (outTested) ++(*outTested);

                if (ValidateGObjectsCandidate(reinterpret_cast<void*>(addr)))
                {
                    return reinterpret_cast<void*>(addr);
                }
                else
                {
                    if (outFailed) ++(*outFailed);
                }
            }
        }

        return nullptr;
    }

    void* RescanGObjects(uintptr_t imageBase)
    {
        return ScanGObjectsImpl(imageBase, nullptr, nullptr);
    }

    // ---- ProcessEvent via vtable ---------------------------------------------
    //
    // ProcessEvent lives at a known vtable slot in every UObject. Once we have
    // a valid UObject (from GObjects), we just read vtable[slot].

    static void* ScanProcessEventViaVtable(void* gobjects, int32_t slotIdx)
    {
        if (!gobjects || slotIdx < 0 || slotIdx > 0x400) return nullptr;

        __try
        {
            auto* p = static_cast<uint8_t*>(gobjects);
            void** objects = *reinterpret_cast<void***>(p + 0x00);
            if (!objects) return nullptr;
            void* firstChunk = objects[0];
            if (!firstChunk) return nullptr;

            uint8_t* chunkBytes = static_cast<uint8_t*>(firstChunk);
            for (int i = 0; i < 64; ++i)
            {
                void* obj = *reinterpret_cast<void**>(chunkBytes + i * 0x18);
                if (!obj) continue;
                if (!IsReadable(obj, 0x08)) continue;

                void** vtable = *reinterpret_cast<void***>(obj);
                if (!vtable) continue;
                if (!IsReadable(vtable, static_cast<size_t>(slotIdx + 1) * sizeof(void*))) continue;

                void* peCandidate = vtable[slotIdx];
                if (!peCandidate) continue;
                if (!IsExecutable(peCandidate)) continue;

                return peCandidate;
            }
        }
        __except (EXCEPTION_EXECUTE_HANDLER)
        {
            return nullptr;
        }

        return nullptr;
    }

    // ---- AppendString smoke-test ----------------------------------------------
    //
    // We don't pattern-scan AppendString yet (would need disassembler or stable
    // anchor strings). Instead we smoke-test the hardcoded offset: read the
    // first few bytes, check they look like a plausible x64 function prologue,
    // and that the address lies in a .text-style executable section.
    //
    // Common x64 prologue first bytes:
    //   48 89 5C 24 ??     mov [rsp+x], rbx
    //   48 89 4C 24 ??     mov [rsp+x], rcx
    //   48 83 EC ??        sub rsp, x
    //   40 53              push rbx
    //   40 55              push rbp
    //   40 56              push rsi
    //   40 57              push rdi
    //   53                 push rbx
    //   55                 push rbp
    //   56                 push rsi
    //   57                 push rdi
    //   48 8B C4           mov rax, rsp   (frame setup)
    //   E9 ?? ?? ?? ??     jmp imm32  (tail call wrapper)

    static bool LooksLikeFunctionPrologue(const uint8_t* code)
    {
        if (!code) return false;
        if (!IsReadable(code, 8)) return false;

        const uint8_t b0 = code[0];
        const uint8_t b1 = code[1];

        if (b0 == 0x48 && (b1 == 0x89 || b1 == 0x83 || b1 == 0x8B || b1 == 0x81)) return true;
        if (b0 == 0x40 && (b1 == 0x53 || b1 == 0x54 || b1 == 0x55 || b1 == 0x56 || b1 == 0x57)) return true;
        if (b0 == 0x53 || b0 == 0x55 || b0 == 0x56 || b0 == 0x57) return true;
        if (b0 == 0xE9) return true; // tail-call jmp
        if (b0 == 0x4C && b1 == 0x8B) return true;
        if (b0 == 0x49 && b1 == 0x8B) return true;

        return false;
    }

    static void* SmokeTestCodePointer(uintptr_t imageBase, uintptr_t offset)
    {
        if (!imageBase || !offset) return nullptr;
        const uintptr_t addr = imageBase + offset;
        if (!IsExecutable(reinterpret_cast<void*>(addr))) return nullptr;
        if (!LooksLikeFunctionPrologue(reinterpret_cast<const uint8_t*>(addr))) return nullptr;
        return reinterpret_cast<void*>(addr);
    }

    // ---- ResolveAll -----------------------------------------------------------

    ScanResult ResolveAll(uintptr_t imageBase,
                          uintptr_t fallbackGObjectsOff,
                          uintptr_t fallbackAppendStringOff,
                          uintptr_t fallbackProcessEventOff,
                          int32_t   processEventVtblIdx)
    {
        ScanResult r{};
        if (!imageBase) return r;

        const DWORD t0 = GetTickCount();

        // ----- GObjects: scan first, fall back to hardcoded if scan fails or
        // returns something but it's not yet populated.
        uint32_t candidatesTested = 0;
        uint32_t validationFails  = 0;
        void* scannedGObjects = ScanGObjectsImpl(imageBase, &candidatesTested, &validationFails);
        r.gobjectsCandidatesTested  = candidatesTested;
        r.gobjectsValidationFailures = validationFails;

        if (scannedGObjects)
        {
            r.gobjects         = scannedGObjects;
            r.gobjectsFromScan = true;
        }
        else if (fallbackGObjectsOff)
        {
            void* fallback = reinterpret_cast<void*>(imageBase + fallbackGObjectsOff);
            // Don't validate yet - GObjects may not be populated at scan time.
            // The Init() retry loop will validate on each call.
            r.gobjects         = fallback;
            r.gobjectsFromScan = false;
        }

        // ----- ProcessEvent: prefer vtable read (works only if GObjects is
        // populated). Fall back to hardcoded offset with smoke-test.
        void* scannedPE = nullptr;
        if (r.gobjects && ValidateGObjectsCandidate(r.gobjects))
        {
            scannedPE = ScanProcessEventViaVtable(r.gobjects, processEventVtblIdx);
        }
        if (scannedPE)
        {
            r.processEvent         = scannedPE;
            r.processEventFromScan = true;
        }
        else if (fallbackProcessEventOff)
        {
            void* smoke = SmokeTestCodePointer(imageBase, fallbackProcessEventOff);
            r.processEvent         = smoke ? smoke : reinterpret_cast<void*>(imageBase + fallbackProcessEventOff);
            r.processEventFromScan = false;
        }

        // ----- AppendString: smoke-test hardcoded only (pattern-scan TBD).
        if (fallbackAppendStringOff)
        {
            void* smoke = SmokeTestCodePointer(imageBase, fallbackAppendStringOff);
            r.appendString         = smoke ? smoke : reinterpret_cast<void*>(imageBase + fallbackAppendStringOff);
            r.appendStringFromScan = false;
        }

        r.scanDurationMs = GetTickCount() - t0;
        return r;
    }

} // namespace QmScan
