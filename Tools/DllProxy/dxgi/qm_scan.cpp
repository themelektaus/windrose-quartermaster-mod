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

    // ---- EXE-section cache (for pointer-region quality scoring) --------------
    //
    // The quality score for a GObjects candidate measures how "UObject-like" the
    // pointers in its chunk are. A real UObject has its vtable somewhere in
    // the EXE's image (specifically .rdata in MSVC builds; verified by live
    // memory probe of WindroseServer-Win64-Shipping vtable @ 0x14c488db0 sits
    // in .rdata not .text). Class pointers in turn live in .data. Other UE
    // structures (allocator pools, internal caches) that share the
    // FChunkedFixedUObjectArray shape don't have this property.
    //
    // We cache the section bounds once and use them as fast range checks. The
    // "image RO" range is the full read-only image span (.text + .rdata) so
    // we don't have to know section names - we just take the union of all
    // non-writable mappings in the EXE.
    struct SectionCache
    {
        uintptr_t textStart;
        uintptr_t textEnd;
        // RO span: union of all non-writable PE sections (includes .text + .rdata).
        // vtables live in .rdata so checking just .text is too strict.
        uintptr_t imgRoStart;
        uintptr_t imgRoEnd;
        uintptr_t dataStart;
        uintptr_t dataEnd;
        bool      ready;
    };
    static SectionCache g_secCache{};

    static void RefreshSectionCache(uintptr_t imageBase)
    {
        g_secCache = {};
        SectionInfo sections[32]{};
        uint32_t n = EnumerateSections(imageBase, sections, 32);
        for (uint32_t i = 0; i < n; ++i)
        {
            const SectionInfo& s = sections[i];
            // .text: executable. There can be multiple but usually one big one.
            if (s.executable && s.size > (g_secCache.textEnd - g_secCache.textStart))
            {
                g_secCache.textStart = s.start;
                g_secCache.textEnd   = s.start + s.size;
            }
            // Image read-only span: every non-writable section (.text + .rdata).
            // vtables sit in .rdata which is non-writable but also non-executable.
            if (!s.writable)
            {
                if (g_secCache.imgRoStart == 0 || s.start < g_secCache.imgRoStart)
                    g_secCache.imgRoStart = s.start;
                uintptr_t e = s.start + s.size;
                if (e > g_secCache.imgRoEnd) g_secCache.imgRoEnd = e;
            }
            // .data: writable + not executable. There can be multiple
            // (.data, .bss-equivalent slot, /19 etc) - we take the union
            // by tracking min start and max end.
            if (s.writable && !s.executable)
            {
                if (g_secCache.dataStart == 0 || s.start < g_secCache.dataStart)
                    g_secCache.dataStart = s.start;
                uintptr_t e = s.start + s.size;
                if (e > g_secCache.dataEnd) g_secCache.dataEnd = e;
            }
        }
        g_secCache.ready = (g_secCache.textEnd > g_secCache.textStart)
                        && (g_secCache.dataEnd > g_secCache.dataStart);
    }

    static bool IsInExeText(const void* p)
    {
        if (!g_secCache.ready || !p) return false;
        uintptr_t v = reinterpret_cast<uintptr_t>(p);
        return v >= g_secCache.textStart && v < g_secCache.textEnd;
    }

    // True if p points into any non-writable image region (.text or .rdata).
    // Used for vtable-quality scoring - vtables can sit in either.
    static bool IsInExeImageReadOnly(const void* p)
    {
        if (!g_secCache.ready || !p) return false;
        uintptr_t v = reinterpret_cast<uintptr_t>(p);
        return v >= g_secCache.imgRoStart && v < g_secCache.imgRoEnd;
    }

    static bool IsInExeData(const void* p)
    {
        if (!g_secCache.ready || !p) return false;
        uintptr_t v = reinterpret_cast<uintptr_t>(p);
        return v >= g_secCache.dataStart && v < g_secCache.dataEnd;
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

    // Tunable validation ranges - exposed as constants so the diag detail
    // line in the log uses the same values the check enforces. Lowered floors
    // on 2026-05-26 to widen the dedicated-server compatibility window:
    //   - MaxElements floor 0x10000 -> 0x2000 (a fresh server starts with
    //     fewer UObjects; 0x2000 is still safely above stack/heap garbage
    //     that happens to land in the int32 readable range).
    //   - validObjects threshold 3 -> 1 (Wine's IsExecutable check on vtable
    //     entries reports differently than native Windows in headless mode;
    //     finding even one valid-looking UObject is strong evidence already
    //     given the prior chunked-layout checks all passed).
    //   - Probe more chunk slots (16 -> 64) since the first 16 might all be
    //     null padding in some configurations.
    static constexpr int32_t kMaxElemsFloor      = 0x2000;
    static constexpr int32_t kMaxElemsCeiling    = 0x600000;
    static constexpr int32_t kMaxChunksFloor     = 1;
    static constexpr int32_t kMaxChunksCeiling   = 200;
    // UE5.6 always uses exactly 0x10000 elements per chunk. Probing live
    // memory of a running dedicated server confirmed: among 1.06M tested
    // .data slots, only ONE (the real GObjects) matched ElemsPerChunk==0x10000
    // when also requiring NumElements>100. Loosening this range admits
    // thousands of false positives (other TArray-shaped containers).
    static constexpr int32_t kElemsPerChunk      = 0x10000;
    static constexpr int     kValidObjectsThresh = 1;
    static constexpr int     kChunkProbeSlots    = 64;
    // GObjects is considered "live" once NumElements crosses this. Before
    // that, the static struct in .data exists but has default-initialised
    // values (NumElements==8 from the disregard-for-GC permanent pool, but
    // Objects/PreAllocatedObjects=NULL because the chunk pool isn't yet
    // allocated). Returning a candidate in that state would lock us onto
    // either a stale match or a false-positive structural sibling. Scanner
    // returns nullptr below this threshold so the caller retries.
    static constexpr int32_t kPopulatedThresh    = 100;

    // Bit ids per reject reason - used both to populate RejectCounters and to
    // tag NearMissCandidate.rejectReasonBit so log messages can decode it.
    enum RejectReasonBit : uint32_t {
        REJ_NotReadable          = 1u << 0,
        REJ_ObjectsNull          = 1u << 1,
        REJ_MaxElemsRange        = 1u << 2,
        REJ_NumElemsRange        = 1u << 3,
        REJ_ChunksRange          = 1u << 4,
        REJ_ElemsPerChunkRange   = 1u << 5,
        REJ_ObjectsUnreadable    = 1u << 6,
        REJ_FirstChunkNull       = 1u << 7,
        REJ_FirstChunkUnreadable = 1u << 8,
        REJ_ValidObjectsTooFew   = 1u << 9,
        REJ_SehFault             = 1u << 10,
    };

    // Detailed validation that reports WHY a candidate was rejected. Used by
    // the scanner's diag-mode pass; the public ValidateGObjectsCandidate is a
    // simple bool wrapper. PassedChecks counts validation steps the candidate
    // SUCCEEDED at - the higher this count, the closer the candidate came to
    // being a real GObjects. Used to rank "near-miss" entries.
    struct ValidateDetail
    {
        bool     passed;
        uint32_t rejectReason;    // 0 if passed
        uint32_t passedChecks;    // monotonically increasing across the check sequence
        // Field snapshots (raw values for the diag log)
        int32_t  maxElements;
        int32_t  numElements;
        int32_t  maxChunks;
        int32_t  numChunks;
        uintptr_t objectsPtr;
        // Captured when passedChecks >= 8 (firstChunk readable). Used by
        // the near-miss diag log to dump the raw FUObjectItem layout.
        bool     firstChunkBytesValid;
        uint8_t  firstChunkBytes[128];
        // The 32 bytes of the TUObjectArray header itself - reveals the
        // PreAllocatedObjects pointer at +0x08 (UE5.4+ FChunkedFixedUObjectArray).
        bool     headerBytesValid;
        uint8_t  headerBytes[32];
        // First 128 bytes of PreAllocatedObjects buffer (the +0x08 field).
        // This is where UE5.6 stores the initial UObjects before any chunks
        // are dynamically allocated.
        bool     preAllocBytesValid;
        uint8_t  preAllocBytes[128];
        // Quality-score components - filled when passed==true and used to pick
        // the best match among multiple structurally-valid candidates. Higher
        // scores correlate with "real GObjects" rather than other UE5 containers
        // that happen to share the FChunkedFixedUObjectArray shape.
        uint32_t qualityScore;
        uint32_t uobjSlotsProbed;
        uint32_t uobjSlotsPopulated;
        uint32_t uobjVtableInText;
        uint32_t uobjClassInData;
        uint32_t layoutBonus;
    };

    static ValidateDetail ValidateGObjectsDetailed(void* candidate)
    {
        ValidateDetail d{};

        if (!candidate)
        {
            d.rejectReason = REJ_NotReadable;
            return d;
        }
        if (!IsReadable(candidate, 0x20))
        {
            d.rejectReason = REJ_NotReadable;
            return d;
        }
        d.passedChecks = 1;

        __try
        {
            auto* p = static_cast<uint8_t*>(candidate);

            void** objects     = *reinterpret_cast<void***>(p + 0x00);
            int32_t maxElems   = *reinterpret_cast<int32_t*>(p + 0x10);
            int32_t numElems   = *reinterpret_cast<int32_t*>(p + 0x14);
            int32_t maxChunks  = *reinterpret_cast<int32_t*>(p + 0x18);
            int32_t numChunks  = *reinterpret_cast<int32_t*>(p + 0x1C);

            d.maxElements = maxElems;
            d.numElements = numElems;
            d.maxChunks   = maxChunks;
            d.numChunks   = numChunks;
            d.objectsPtr  = reinterpret_cast<uintptr_t>(objects);

            if (maxElems < kMaxElemsFloor || maxElems > kMaxElemsCeiling)
            {
                d.rejectReason = REJ_MaxElemsRange;
                return d;
            }
            d.passedChecks = 2;

            if (numElems < 0 || numElems > maxElems)
            {
                d.rejectReason = REJ_NumElemsRange;
                return d;
            }
            d.passedChecks = 3;

            if (maxChunks < kMaxChunksFloor || maxChunks > kMaxChunksCeiling
             || numChunks < 1 || numChunks > maxChunks)
            {
                d.rejectReason = REJ_ChunksRange;
                return d;
            }
            d.passedChecks = 4;

            // UE5.6: MaxElements / MaxChunks must equal exactly 0x10000 (the
            // FChunkedFixedUObjectArray::NumElementsPerChunk constant). We
            // also reject if maxElems doesn't divide cleanly into maxChunks
            // chunks (sanity: catches near-misses that match by coincidence).
            const int32_t elemsPerChunk = maxElems / maxChunks;
            if (elemsPerChunk != kElemsPerChunk || maxElems % maxChunks != 0)
            {
                d.rejectReason = REJ_ElemsPerChunkRange;
                return d;
            }
            d.passedChecks = 5;

            if (!objects)
            {
                d.rejectReason = REJ_ObjectsNull;
                return d;
            }
            d.passedChecks = 6;

            if (!IsReadable(objects, static_cast<size_t>(numChunks) * sizeof(void*)))
            {
                d.rejectReason = REJ_ObjectsUnreadable;
                return d;
            }
            d.passedChecks = 7;

            // Capture the TUObjectArray header bytes (32 bytes) for diag.
            // This shows us the +0x08 field (PreAllocatedObjects in UE5.4+)
            // which we'll probe as a fallback below.
            memcpy(d.headerBytes, p, sizeof(d.headerBytes));
            d.headerBytesValid = true;

            // UE5.4+ FChunkedFixedUObjectArray has a PreAllocatedObjects buffer
            // at +0x08. Before any dynamic chunk is allocated, the first
            // NumElementsPerChunk UObjects live in this buffer. The dedicated
            // server on UE5.6 starts with NumElems=8 but Objects[0]==NULL -
            // the 8 UObjects are in PreAllocatedObjects, not in Objects[0].
            void* preAllocatedObjects = *reinterpret_cast<void**>(p + 0x08);

            void* firstChunk = objects[0];

            // The "chunks" array we'll probe in order: firstChunk (if non-null
            // and readable), then PreAllocatedObjects, then objects[1..N-1].
            // Stop as soon as we find >= kValidObjectsThresh UObjects.
            //
            // We allow firstChunk==null as long as PreAllocatedObjects yields
            // valid objects. This is the UE5.6 dedicated-server case.

            d.passedChecks = 8; // (kept for log-format compatibility)

            // Capture the first 128 bytes of firstChunk for the diag log.
            // We only copy as much as is readable - if only the first 32
            // bytes are mapped we don't want to AV here. If firstChunk is
            // null, leave the dump empty (caller logs all zeros) and we'll
            // capture PreAllocatedObjects bytes into preAllocBytes instead.
            if (firstChunk && IsReadable(firstChunk, sizeof(void*) * 4))
            {
                size_t copyBytes = sizeof(d.firstChunkBytes);
                uint8_t* src = static_cast<uint8_t*>(firstChunk);
                size_t actuallyReadable = 0;
                while (actuallyReadable < copyBytes
                       && IsReadable(src + actuallyReadable, 8))
                {
                    actuallyReadable += 8;
                }
                if (actuallyReadable > 0)
                {
                    memcpy(d.firstChunkBytes, src, actuallyReadable);
                    if (actuallyReadable < copyBytes)
                        memset(d.firstChunkBytes + actuallyReadable, 0,
                               copyBytes - actuallyReadable);
                    d.firstChunkBytesValid = true;
                }
                d.passedChecks = 9;
            }

            // Capture the first 128 bytes of PreAllocatedObjects too.
            if (preAllocatedObjects && IsReadable(preAllocatedObjects, sizeof(void*) * 4))
            {
                size_t copyBytes = sizeof(d.preAllocBytes);
                uint8_t* src = static_cast<uint8_t*>(preAllocatedObjects);
                size_t actuallyReadable = 0;
                while (actuallyReadable < copyBytes
                       && IsReadable(src + actuallyReadable, 8))
                {
                    actuallyReadable += 8;
                }
                if (actuallyReadable > 0)
                {
                    memcpy(d.preAllocBytes, src, actuallyReadable);
                    if (actuallyReadable < copyBytes)
                        memset(d.preAllocBytes + actuallyReadable, 0,
                               copyBytes - actuallyReadable);
                    d.preAllocBytesValid = true;
                }
            }

            // UObject probing - deep stats variant. Walks ALL slots in each
            // probe chunk (no early-break) so we can score how UObject-like
            // the candidate is. probeStats accumulates across firstChunk,
            // PreAllocatedObjects and objects[1..3].
            //
            // The score components track three orthogonal properties:
            //   slotsPopulated  - how many slots have a non-null Object pointer
            //   vtableInText    - how many of those vtables resolve to .text
            //                     of the host EXE (strong UObject signal)
            //   classInData     - how many class pointers resolve to .data
            //                     of the host EXE (also strong UObject signal)
            //
            // Real GObjects in a booted server has hundreds of populated slots
            // with vtables in .text; false positives (other UE containers)
            // typically have few populated slots or vtables pointing into
            // module .text of unrelated DLLs.
            struct ProbeStats
            {
                uint32_t slotsProbed;
                uint32_t slotsPopulated;
                uint32_t vtableInText;
                uint32_t classInData;
            };

            ProbeStats stats{};

            auto probeForUObjects = [&](void* chunk) {
                if (!chunk) return;
                // Best-effort: probe what's readable, don't break early.
                uint8_t* cb = static_cast<uint8_t*>(chunk);
                for (int i = 0; i < kChunkProbeSlots; ++i) {
                    if (!IsReadable(cb + i * 0x18, 0x18)) break;
                    ++stats.slotsProbed;

                    void* obj = *reinterpret_cast<void**>(cb + i * 0x18);
                    if (!obj) continue;
                    if (!IsReadable(obj, 0x28)) continue;

                    ++stats.slotsPopulated;

                    void* vtable = *reinterpret_cast<void**>(static_cast<uint8_t*>(obj) + 0x00);
                    void* cls    = *reinterpret_cast<void**>(static_cast<uint8_t*>(obj) + 0x10);

                    // Two-part vtable check: vtable itself sits in .rdata of
                    // the host EXE, AND vtable[0] (the first virtual function
                    // address) points into .text. Either signal alone counts
                    // - a UObject vtable from a DLL passes the .text check via
                    // its first member function instead.
                    if (vtable && IsInExeImageReadOnly(vtable)) {
                        ++stats.vtableInText;
                    } else if (vtable && IsReadable(vtable, sizeof(void*))) {
                        void* vfn0 = *reinterpret_cast<void**>(vtable);
                        if (vfn0 && IsInExeText(vfn0)) {
                            ++stats.vtableInText;
                        }
                    }

                    if (cls && IsInExeData(cls)) {
                        ++stats.classInData;
                    }
                }
            };

            // Probe firstChunk, PreAllocatedObjects, and a few additional
            // allocated chunks. Stop at chunk 4 to keep scan cost bounded.
            probeForUObjects(firstChunk);
            probeForUObjects(preAllocatedObjects);
            {
                int probeMax = numChunks < 4 ? numChunks : 4;
                for (int ci = 1; ci < probeMax; ++ci) {
                    if (!IsReadable(objects + ci, sizeof(void*))) break;
                    void* otherChunk = objects[ci];
                    if (!otherChunk) continue;
                    probeForUObjects(otherChunk);
                }
            }

            if (stats.slotsPopulated < static_cast<uint32_t>(kValidObjectsThresh))
            {
                d.uobjSlotsProbed    = stats.slotsProbed;
                d.uobjSlotsPopulated = stats.slotsPopulated;
                d.uobjVtableInText   = stats.vtableInText;
                d.uobjClassInData    = stats.classInData;
                d.rejectReason = REJ_ValidObjectsTooFew;
                return d;
            }
            d.passedChecks = 10;

            // Compute quality score. Weights chosen so that:
            //   - vtableInText is the dominant signal (real GObjects has
            //     near 100% rate; false-positives much lower)
            //   - classInData is the secondary confirmer
            //   - slotsPopulated alone is weak (false-positives may have
            //     a few populated slots; real GObjects has tens to hundreds)
            //   - layout plausibility bonus rewards classical
            //     MaxChunks * 0x10000 ~ MaxElements relationship
            uint32_t score = 0;
            score += stats.vtableInText * 8;
            score += stats.classInData  * 4;
            score += stats.slotsPopulated;

            // Structural plausibility bonus: classical UE5 layout has
            // MaxChunks * ElementsPerChunk (=0x10000) within a few % of
            // MaxElements. A degenerate "1 chunk, 5M elements" candidate
            // (likely a false-positive non-chunked container repurposing
            // the same field layout) earns no bonus.
            uint32_t layoutBonus = 0;
            if (maxChunks > 0) {
                const int64_t expectedElems = static_cast<int64_t>(maxChunks) * 0x10000;
                const int64_t actualElems   = static_cast<int64_t>(maxElems);
                // Within 5% slack to allow for ceiling-rounding.
                int64_t diff = expectedElems - actualElems;
                if (diff < 0) diff = -diff;
                if (diff * 20 <= expectedElems) {
                    layoutBonus = 32;
                }
            }
            score += layoutBonus;

            d.uobjSlotsProbed    = stats.slotsProbed;
            d.uobjSlotsPopulated = stats.slotsPopulated;
            d.uobjVtableInText   = stats.vtableInText;
            d.uobjClassInData    = stats.classInData;
            d.layoutBonus        = layoutBonus;
            d.qualityScore       = score;

            d.passed = true;
            return d;
        }
        __except (EXCEPTION_EXECUTE_HANDLER)
        {
            d.rejectReason = REJ_SehFault;
            return d;
        }
    }

    bool ValidateGObjectsCandidate(void* candidate)
    {
        return ValidateGObjectsDetailed(candidate).passed;
    }

    // Accumulate one reject reason into the counters.
    static void TallyReject(RejectCounters& c, uint32_t reason)
    {
        if (reason & REJ_NotReadable)          ++c.notReadable;
        if (reason & REJ_ObjectsNull)          ++c.objectsNull;
        if (reason & REJ_MaxElemsRange)        ++c.maxElemsRange;
        if (reason & REJ_NumElemsRange)        ++c.numElemsRange;
        if (reason & REJ_ChunksRange)          ++c.chunksRange;
        if (reason & REJ_ElemsPerChunkRange)   ++c.elemsPerChunkRange;
        if (reason & REJ_ObjectsUnreadable)    ++c.objectsUnreadable;
        if (reason & REJ_FirstChunkNull)       ++c.firstChunkNull;
        if (reason & REJ_FirstChunkUnreadable) ++c.firstChunkUnreadable;
        if (reason & REJ_ValidObjectsTooFew)   ++c.validObjectsTooFew;
        if (reason & REJ_SehFault)             ++c.sehFault;
    }

    // Insert a candidate into the "top near-miss" array if it passed more
    // checks than any current entry. Keeps the array sorted descending by
    // passedChecks. O(N) per insert, but N=4 so negligible.
    static void InsertNearMiss(NearMissCandidate* arr, int cap,
                               uintptr_t addr, const ValidateDetail& d)
    {
        // Find insertion slot (entries with lower passedChecks shift right).
        int slot = -1;
        for (int i = 0; i < cap; ++i)
        {
            if (d.passedChecks > arr[i].passedChecks)
            {
                slot = i;
                break;
            }
        }
        if (slot < 0) return;

        // Shift right.
        for (int i = cap - 1; i > slot; --i) arr[i] = arr[i - 1];

        // Write.
        arr[slot].address              = addr;
        arr[slot].passedChecks         = d.passedChecks;
        arr[slot].rejectReasonBit      = d.rejectReason;
        arr[slot].maxElements          = d.maxElements;
        arr[slot].numElements          = d.numElements;
        arr[slot].maxChunks            = d.maxChunks;
        arr[slot].numChunks            = d.numChunks;
        arr[slot].objectsPtr           = d.objectsPtr;
        arr[slot].firstChunkBytesValid = d.firstChunkBytesValid;
        if (d.firstChunkBytesValid)
            memcpy(arr[slot].firstChunkBytes, d.firstChunkBytes, sizeof(arr[slot].firstChunkBytes));
        else
            memset(arr[slot].firstChunkBytes, 0, sizeof(arr[slot].firstChunkBytes));

        arr[slot].headerBytesValid = d.headerBytesValid;
        if (d.headerBytesValid)
            memcpy(arr[slot].headerBytes, d.headerBytes, sizeof(arr[slot].headerBytes));
        else
            memset(arr[slot].headerBytes, 0, sizeof(arr[slot].headerBytes));

        arr[slot].preAllocBytesValid = d.preAllocBytesValid;
        if (d.preAllocBytesValid)
            memcpy(arr[slot].preAllocBytes, d.preAllocBytes, sizeof(arr[slot].preAllocBytes));
        else
            memset(arr[slot].preAllocBytes, 0, sizeof(arr[slot].preAllocBytes));
    }

    // ---- GObjects scan --------------------------------------------------------

    // Insert a passed candidate into the top-N match array sorted descending
    // by quality score. Cap-bounded so the scan cost stays predictable even
    // if hundreds of structurally-valid candidates exist.
    static void InsertMatch(ScanResult::MatchCandidate* arr, int cap,
                            uintptr_t addr, const ValidateDetail& d)
    {
        int slot = -1;
        for (int i = 0; i < cap; ++i)
        {
            if (d.qualityScore > arr[i].qualityScore)
            {
                slot = i;
                break;
            }
        }
        if (slot < 0) return;

        // Shift right.
        for (int i = cap - 1; i > slot; --i) arr[i] = arr[i - 1];

        arr[slot].address            = addr;
        arr[slot].qualityScore       = d.qualityScore;
        arr[slot].maxElements        = d.maxElements;
        arr[slot].numElements        = d.numElements;
        arr[slot].maxChunks          = d.maxChunks;
        arr[slot].numChunks          = d.numChunks;
        arr[slot].objectsPtr         = d.objectsPtr;
        arr[slot].uobjSlotsProbed    = d.uobjSlotsProbed;
        arr[slot].uobjSlotsPopulated = d.uobjSlotsPopulated;
        arr[slot].uobjVtableInText   = d.uobjVtableInText;
        arr[slot].uobjClassInData    = d.uobjClassInData;
        arr[slot].layoutBonus        = d.layoutBonus;
    }

    static void* ScanGObjectsImpl(uintptr_t imageBase,
                                  uint32_t* outTested, uint32_t* outFailed,
                                  RejectCounters* outRejects,
                                  NearMissCandidate* outNearMisses, int nearMissCap,
                                  ScanResult::MatchCandidate* outMatches, int matchCap,
                                  uint32_t* outMatchesFound)
    {
        if (outTested) *outTested = 0;
        if (outFailed) *outFailed = 0;
        if (outRejects) *outRejects = {};
        if (outNearMisses && nearMissCap > 0)
            for (int i = 0; i < nearMissCap; ++i) outNearMisses[i] = {};
        if (outMatches && matchCap > 0)
            for (int i = 0; i < matchCap; ++i) outMatches[i] = {};
        if (outMatchesFound) *outMatchesFound = 0;
        if (!imageBase) return nullptr;

        // Refresh the section cache - the EXE layout never changes at runtime
        // but we re-read it on every scan to keep ScanGObjectsImpl stateless
        // from the caller's perspective.
        RefreshSectionCache(imageBase);

        SectionInfo sections[32]{};
        uint32_t secCount = EnumerateSections(imageBase, sections, 32);
        if (secCount == 0) return nullptr;

        uint32_t matchesFound = 0;

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

                ValidateDetail d = ValidateGObjectsDetailed(reinterpret_cast<void*>(addr));
                if (d.passed)
                {
                    // Collect into match-candidates ranked by quality score
                    // - DON'T return early. Multiple containers in .data
                    // share the FChunkedFixedUObjectArray shape; we want
                    // the best-scoring one.
                    if (outMatches && matchCap > 0)
                    {
                        InsertMatch(outMatches, matchCap, addr, d);
                    }
                    ++matchesFound;
                    continue;
                }

                if (outFailed) ++(*outFailed);
                if (outRejects) TallyReject(*outRejects, d.rejectReason);
                if (outNearMisses && nearMissCap > 0 && d.passedChecks >= 2)
                {
                    // Only consider candidates that passed at least the first
                    // readability check - the universe of "interesting"
                    // candidates is small (Wine reports most slots as unreadable).
                    InsertNearMiss(outNearMisses, nearMissCap, addr, d);
                }
            }
        }

        if (outMatchesFound) *outMatchesFound = matchesFound;

        // Best-match wins, BUT we require NumElements > kPopulatedThresh.
        // GObjects is a static struct in .data - its address is fixed across
        // process lifetime. Before UE allocates the chunk pool, all matching
        // candidates show NumElements==8 (the disregard-for-GC permanent pool
        // size) with Objects==NULL. Returning any of those would lock us onto
        // a structurally-identical but uninitialised sibling. By failing the
        // scan here, the caller's retry loop gets another shot once UE
        // allocates the pool (then exactly one candidate populates).
        if (outMatches && matchCap > 0 && outMatches[0].address != 0
            && outMatches[0].numElements > kPopulatedThresh)
        {
            return reinterpret_cast<void*>(outMatches[0].address);
        }
        return nullptr;
    }

    void* RescanGObjects(uintptr_t imageBase)
    {
        // Lightweight rescan: ignore match diagnostics, just return best.
        // Caller is the retry loop that fires every 500ms - keep this cheap.
        ScanResult::MatchCandidate matches[ScanResult::kMatchCap]{};
        uint32_t matchesFound = 0;
        return ScanGObjectsImpl(imageBase, nullptr, nullptr, nullptr,
                                nullptr, 0, matches, ScanResult::kMatchCap,
                                &matchesFound);
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
                // Wine-compat: IsExecutable() can falsely reject PE-mapped .text
                // pages in headless server builds. IsReadable() is a sufficient
                // safety check given the candidate was reached via a real
                // UObject's vtable.
                if (!IsReadable(peCandidate, 1)) continue;

                return peCandidate;
            }
        }
        __except (EXCEPTION_EXECUTE_HANDLER)
        {
            return nullptr;
        }

        return nullptr;
    }

    // ---- AppendString pattern scan --------------------------------------------
    //
    // The pattern below is a 20-instruction (77-byte) window starting at the
    // prologue of FName::AppendString in UE 5.6 shipping builds. It was
    // extracted offline from the CLIENT EXE at the Dumper-7-reported offset
    // (+0x14E53F0) and verified to be unique within:
    //   - Windrose-Win64-Shipping.exe        (client)     -> 1 hit  @ +0x14E53F0
    //   - WindroseServer-Win64-Shipping.exe  (dedicated)  -> 1 hit  @ +0x14551C0
    //
    // Mask layout (kAppendStringMask): 1 = literal byte, 0 = wildcard. The
    // wildcards cover the rel32 displacements of:
    //   - cmp byte ptr [rip+rel32], 0       (static-init flag check, +0x10..+0x14)
    //   - lea r8, [rip+rel32]               (debug "AppendString" string,  +0x22..+0x25)
    //   - lea rcx, [rip+rel32]              (debug helper arg,             +0x2B..+0x2E)
    //   - call rel32                        (debug helper,                 +0x30..+0x33)
    //   - mov byte ptr [rip+rel32], 1       (set initialized flag,         +0x39..+0x3C)
    // Everything else is the FName comparison-index load + chunk math, which
    // is byte-identical across client/server (same UE source, same MSVC).

    static constexpr uint8_t kAppendStringPattern[] = {
        0x48, 0x89, 0x5C, 0x24, 0x10, 0x48, 0x89, 0x74, 0x24, 0x18, 0x57, 0x48, 0x83, 0xEC, 0x20, 0x80,
        0x3D, 0x00, 0x00, 0x00, 0x00, 0x00, 0x48, 0x8B, 0xF2, 0x8B, 0x19, 0x48, 0x8B, 0xF9, 0x74, 0x09,
        0x4C, 0x8D, 0x05, 0x00, 0x00, 0x00, 0x00, 0xEB, 0x16, 0x48, 0x8D, 0x0D, 0x00, 0x00, 0x00, 0x00,
        0xE8, 0x00, 0x00, 0x00, 0x00, 0x4C, 0x8B, 0xC0, 0xC6, 0x05, 0x00, 0x00, 0x00, 0x00, 0x01, 0x8B,
        0xCB, 0x0F, 0xB7, 0xC3, 0xC1, 0xE9, 0x10, 0x89, 0x4C, 0x24, 0x30, 0x8B, 0xD1
    };
    static constexpr uint8_t kAppendStringMask[] = {
        1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1,
        1, 0, 0, 0, 0, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1,
        1, 1, 1, 0, 0, 0, 0, 1, 1, 1, 1, 1, 0, 0, 0, 0,
        1, 0, 0, 0, 0, 1, 1, 1, 1, 1, 0, 0, 0, 0, 1, 1,
        1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1
    };
    static_assert(sizeof(kAppendStringPattern) == sizeof(kAppendStringMask),
                  "AppendString pattern and mask must be the same length");
    static constexpr size_t kAppendStringPatLen = sizeof(kAppendStringPattern);

    // Pattern-scan .text for AppendString. Returns the address of the first
    // hit, or nullptr if no candidate matches. outBytesScanned/outMatches are
    // optional and populated for diagnostic logging.
    //
    // The scanner walks byte-by-byte (not 8-aligned) because function entries
    // are not aligned beyond 16-byte CPU cache lines (and MSVC sometimes
    // packs them tighter via nop padding). For a 195MB .text scan this is
    // <50ms on commodity hardware.
    static void* ScanAppendStringImpl(uintptr_t imageBase,
                                      uint32_t* outBytesScanned,
                                      uint32_t* outMatches)
    {
        if (outBytesScanned) *outBytesScanned = 0;
        if (outMatches)      *outMatches      = 0;
        if (!imageBase) return nullptr;

        if (!g_secCache.ready) RefreshSectionCache(imageBase);
        if (!g_secCache.ready) return nullptr;

        const uint8_t* const textStart = reinterpret_cast<const uint8_t*>(g_secCache.textStart);
        const uint8_t* const textEnd   = reinterpret_cast<const uint8_t*>(g_secCache.textEnd);
        if (textEnd <= textStart) return nullptr;
        if (static_cast<size_t>(textEnd - textStart) < kAppendStringPatLen) return nullptr;

        const uint8_t* const lastPos = textEnd - kAppendStringPatLen;
        void* firstHit = nullptr;
        uint32_t hits = 0;

        __try
        {
            for (const uint8_t* p = textStart; p <= lastPos; ++p)
            {
                // Fast first-byte gate. Pattern starts with 0x48 (REX.W) which
                // is extremely common in x64 - but the second byte (0x89) and
                // the 4th byte (0x24) make a 3-byte prefix that filters >99%.
                if (p[0] != kAppendStringPattern[0]) continue;
                if (p[1] != kAppendStringPattern[1]) continue;
                if (p[3] != kAppendStringPattern[3]) continue;

                bool match = true;
                for (size_t j = 0; j < kAppendStringPatLen; ++j)
                {
                    if (kAppendStringMask[j] && p[j] != kAppendStringPattern[j])
                    {
                        match = false;
                        break;
                    }
                }
                if (match)
                {
                    if (!firstHit) firstHit = const_cast<uint8_t*>(p);
                    ++hits;
                    // Continue scanning to count duplicates (cheap once we know
                    // hits are rare). Cap at 16 to avoid wasted work if the
                    // pattern accidentally matches something repetitive.
                    if (hits >= 16) break;
                    // Skip past this hit to avoid matching nested overlaps.
                    p += kAppendStringPatLen - 1;
                }
            }
        }
        __except (EXCEPTION_EXECUTE_HANDLER)
        {
            // .text should always be readable - if we hit an AV here something
            // is fundamentally wrong with our section bounds. Return nullptr
            // so the caller falls back to the smoke-tested hardcoded offset.
            firstHit = nullptr;
        }

        if (outBytesScanned) *outBytesScanned = static_cast<uint32_t>(textEnd - textStart);
        if (outMatches)      *outMatches      = hits;
        return firstHit;
    }

    // ---- AppendString smoke-test ----------------------------------------------
    //
    // Last-resort path when ScanAppendStringImpl returns nullptr: smoke-test
    // the hardcoded offset by reading the first few bytes, checking they look
    // like a plausible x64 function prologue, and that the address lies in a
    // .text-style executable section.
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
        void* scannedGObjects = ScanGObjectsImpl(imageBase,
                                                 &candidatesTested, &validationFails,
                                                 &r.rejects,
                                                 r.nearMisses, ScanResult::kNearMissCap,
                                                 r.matches, ScanResult::kMatchCap,
                                                 &r.matchesFound);
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

        // ----- AppendString: pattern-scan first, then smoke-test fallback.
        // The hardcoded fallback is the CLIENT offset; on the dedicated
        // server it points to non-code bytes (garbage), so smoke-test will
        // reject it. The pattern scan over .text is the reliable path.
        {
            const DWORD ts = GetTickCount();
            uint32_t bytesScanned = 0, hits = 0;
            void* scanned = ScanAppendStringImpl(imageBase, &bytesScanned, &hits);
            r.appendStringBytesScanned   = bytesScanned;
            r.appendStringPatternMatches = hits;
            r.appendStringScanMs         = GetTickCount() - ts;

            if (scanned)
            {
                r.appendString         = scanned;
                r.appendStringFromScan = true;
            }
            else if (fallbackAppendStringOff)
            {
                void* smoke = SmokeTestCodePointer(imageBase, fallbackAppendStringOff);
                // If the smoke-test rejects the fallback (server case where
                // the bytes at the client offset are non-code), set to
                // nullptr - the caller treats this as "AppendString not
                // resolved" and avoids any call that would dereference it.
                r.appendString         = smoke;
                r.appendStringFromScan = false;
            }
        }

        r.scanDurationMs = GetTickCount() - t0;
        return r;
    }

    void* RescanProcessEvent(void* validGObjects, int32_t vtblSlotIdx)
    {
        return ScanProcessEventViaVtable(validGObjects, vtblSlotIdx);
    }

} // namespace QmScan
