// Quartermaster runtime offset auto-discovery
// --------------------------------------------
// Scans the live Windrose-Win64-Shipping.exe for the UE5 symbols we need so
// the DLL keeps working after Steam updates that shift the binary layout.
//
// Strategy per symbol:
//   GObjects     - validation-based scan of .data sections. We walk 8-byte
//                  aligned slots and test each as a TUObjectArray candidate
//                  (Objects ptr deref, MaxElements/NumChunks ranges, etc).
//                  Robust: doesn't depend on raw byte patterns.
//
//   ProcessEvent - vtable slot read. Once GObjects is populated, take any
//                  UObject, read its vtable[PROCESS_EVENT_VTBL_IDX]. The slot
//                  index is stable across patches within the same engine
//                  version.
//
//   AppendString - smoke-test the hardcoded offset (verify first bytes look
//                  like an x64 function prologue + verify .text containment).
//                  If smoke-test fails, return nullptr so caller knows to
//                  warn the user. A full pattern-scan fallback can be added
//                  later if Windrose updates start moving this symbol more
//                  than ~0x1000 bytes per patch.
//
// All scans are SEH-guarded and lifecycle-safe (they never write, only read,
// and always validate pointers before dereferencing).

#pragma once

#include <stdint.h>

namespace QmScan
{
    // Per-reason reject counters. When the scanner returns no match we want to
    // know which validation check eliminated the most candidates - if 99% fail
    // at "objects pointer null" the issue is different from 99% failing at
    // "validObjects < threshold". Mirrored in ScanResult so the caller logs them.
    struct RejectCounters
    {
        uint32_t notReadable;          // candidate's first 0x20 bytes not in committed/readable pages
        uint32_t objectsNull;          // Objects pointer (chunk table) is null
        uint32_t maxElemsRange;        // MaxElements outside accepted range
        uint32_t numElemsRange;        // NumElements outside [0, MaxElements]
        uint32_t chunksRange;          // MaxChunks/NumChunks outside accepted ranges
        uint32_t elemsPerChunkRange;   // MaxElements/MaxChunks outside accepted range
        uint32_t objectsUnreadable;    // Objects chunk-table itself unreadable
        uint32_t firstChunkNull;       // Objects[0] is null
        uint32_t firstChunkUnreadable; // Objects[0] points to unreadable memory
        uint32_t validObjectsTooFew;   // Walked chunk slots, didn't find enough UObject-shaped entries
        uint32_t sehFault;             // __except caught an AV during validation
    };

    // A "near-miss" candidate that passed enough checks to be interesting.
    // We keep the most promising N entries so the trace can show what GObjects
    // ACTUALLY looks like in the failing process when no candidate passes.
    struct NearMissCandidate
    {
        uintptr_t address;             // candidate address
        uint32_t  passedChecks;        // how many validation steps passed before reject
        uint32_t  rejectReasonBit;     // bit from RejectReasonBit enum (which step failed)
        int32_t   maxElements;         // raw values for the first few fields
        int32_t   numElements;
        int32_t   maxChunks;
        int32_t   numChunks;
        uintptr_t objectsPtr;
        // When passedChecks >= 8 we have a readable firstChunk - we dump the
        // first 128 bytes so the diagnostic log can show the FUObjectItem
        // layout in the actual target binary. This is how we identify whether
        // UE 5.6 changed the FUObjectItem stride or UObject member offsets.
        // firstChunkBytesValid==true iff bytes were actually captured.
        bool     firstChunkBytesValid;
        uint8_t  firstChunkBytes[128];
        // The 32-byte TUObjectArray header from the candidate address itself.
        // Reveals the +0x08 field (PreAllocatedObjects in UE5.4+) which we
        // probe as a fallback when Objects[0] is null/empty.
        bool     headerBytesValid;
        uint8_t  headerBytes[32];
        // First 128 bytes of PreAllocatedObjects buffer (+0x08 field).
        bool     preAllocBytesValid;
        uint8_t  preAllocBytes[128];
    };

    struct ScanResult
    {
        void*    gobjects;             // TUObjectArray*  (nullptr on failure)
        void*    appendString;         // void(*)(const FName*, FString*)
        void*    processEvent;         // void(*)(UObject*, UFunction*, void*)

        bool     gobjectsFromScan;     // true=runtime scan, false=hardcoded fallback
        bool     appendStringFromScan;
        bool     processEventFromScan;

        // Diagnostics: bytes inspected, candidates considered, time spent (ms).
        uint32_t gobjectsCandidatesTested;
        uint32_t gobjectsValidationFailures;
        uint32_t scanDurationMs;

        // Per-reason reject breakdown (sums to gobjectsValidationFailures).
        RejectCounters rejects;

        // Top "best near-misses" - up to 4 candidates that passed the most
        // validation steps before being rejected. Empty (passedChecks==0)
        // entries indicate fewer than N near-misses were found.
        static constexpr int kNearMissCap = 4;
        NearMissCandidate nearMisses[kNearMissCap];
    };

    // Resolve all three symbols. Always returns - on failure for any symbol,
    // falls back to <imageBase + fallback_offset> and sets *FromScan=false for
    // that symbol. Inspect the result.*FromScan flags to know which symbols
    // were auto-discovered vs hardcoded.
    //
    // The vtblIdx parameter is the well-known vtable slot for ProcessEvent
    // (currently 0x4C for UE5.6) - if the engine moves this between versions
    // we'd need to scan for it, but for our use case it's a constant.
    //
    // Pass 0 for any fallback offset you want to skip (the result for that
    // symbol will be nullptr if scan also fails).
    ScanResult ResolveAll(
        uintptr_t imageBase,
        uintptr_t fallbackGObjectsOff,
        uintptr_t fallbackAppendStringOff,
        uintptr_t fallbackProcessEventOff,
        int32_t   processEventVtblIdx);

    // Re-attempt GObjects scan without touching other symbols. Used by Init()
    // retry loop when the initial scan ran before the engine populated the
    // array. Idempotent + cheap once a valid candidate is found.
    void* RescanGObjects(uintptr_t imageBase);

    // Validate that a given pointer looks like a TUObjectArray with valid
    // chunked layout and a populated Objects table. Used by the retry loop
    // to confirm the hardcoded fallback is actually live data and not just
    // a stale stack slot.
    bool ValidateGObjectsCandidate(void* candidate);

} // namespace QmScan
