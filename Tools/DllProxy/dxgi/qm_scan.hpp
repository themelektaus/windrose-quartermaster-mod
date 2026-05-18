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
