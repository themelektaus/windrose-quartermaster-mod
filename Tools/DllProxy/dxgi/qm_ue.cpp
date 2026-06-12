// Quartermaster minimal UE5.6 runtime helpers - implementation
#define _CRT_SECURE_NO_WARNINGS
#include <windows.h>
#include <string.h>
#include <stdio.h>

#include "qm_ue.hpp"
#include "qm_scan.hpp"
#include "qm_log.hpp"
#include "qm_alloc.hpp"

// External logger from main.cpp - we don't have access to its <cstdio>-free
// LogF macros here, so we use a thin forwarder declared as extern "C".
extern "C" void QmLogA(const char* msg);

namespace QmUE
{
    static uintptr_t       g_imageBase = 0;
    static TUObjectArray*  g_gobjects  = nullptr;
    static AppendStringFn  g_appendString = nullptr;
    static ProcessEventFn  g_processEvent = nullptr;
    static ProcessInternalFn g_processInternal = nullptr;

    // Symbol resolution state. We separate "address chosen" from "address
    // proven live" so the GObjects retry loop can re-validate without
    // re-running the expensive scan.
    static bool s_symbolsResolved   = false;
    static bool s_gobjectsFromScan  = false;
    static bool s_appendFromScan    = false;
    static bool s_processFromScan   = false;
    static bool s_gobjectsLiveLogged = false;

    uintptr_t GetImageBase()      { return g_imageBase; }
    TUObjectArray* GetGObjects()  { return g_gobjects; }
    AppendStringFn GetAppendStringFn() { return g_appendString; }
    ProcessEventFn GetProcessEventFn() { return g_processEvent; }

    bool IsReady()
    {
        return g_imageBase != 0
            && g_gobjects != nullptr
            && g_gobjects->Num() > 0
            && g_appendString != nullptr;
    }

    static void LogScanResult(const QmScan::ScanResult& r)
    {
        char buf[768];

        // Use "(null)" if either appendString or processEvent ended up nullptr
        // (pattern scan + smoke-test both rejected the candidate). The
        // resolved line stays compact; pattern-scan stats are logged below.
        const auto offOrNull = [](void* p) -> unsigned long long {
            return p ? (unsigned long long)((uintptr_t)p - g_imageBase) : 0ULL;
        };

        _snprintf_s(buf, sizeof(buf), _TRUNCATE,
            "[Scan] resolved: GObjects=0x%llX (+0x%llX, %s) AppendString=0x%llX (+0x%llX, %s) ProcessEvent=0x%llX (+0x%llX, %s) tested=%u failed=%u in %ums",
            (unsigned long long)(uintptr_t)r.gobjects,
            offOrNull(r.gobjects),
            r.gobjectsFromScan ? "scan" : "fallback",
            (unsigned long long)(uintptr_t)r.appendString,
            offOrNull(r.appendString),
            r.appendString ? (r.appendStringFromScan ? "scan" : "smoke") : "unresolved",
            (unsigned long long)(uintptr_t)r.processEvent,
            offOrNull(r.processEvent),
            r.processEvent ? (r.processEventFromScan ? "scan" : "smoke") : "unresolved",
            r.gobjectsCandidatesTested, r.gobjectsValidationFailures, r.scanDurationMs);
        QmLogA(buf);

        // AppendString pattern-scan diagnostics. matches > 1 is suspicious
        // (the offline-verified pattern is unique in client + server), but
        // we still pick the first hit - log it so we can investigate.
        _snprintf_s(buf, sizeof(buf), _TRUNCATE,
            "[Scan] AppendString pattern: textBytes=%u hits=%u in %ums (path=%s)",
            r.appendStringBytesScanned, r.appendStringPatternMatches, r.appendStringScanMs,
            r.appendString ? (r.appendStringFromScan ? "pattern" : "smoke-fallback") : "unresolved");
        QmLogA(buf);

        // When we found one or more structurally-valid candidates, dump the
        // top-N by quality score. This is the diagnostic that distinguishes
        // a real GObjects (high vtableInText + classInData rates) from a
        // false-positive (other UE container sharing the same shape).
        if (r.matchesFound > 0)
        {
            _snprintf_s(buf, sizeof(buf), _TRUNCATE,
                "[Scan] matches found: %u (showing top %d by quality score)",
                r.matchesFound,
                static_cast<int>(QmScan::ScanResult::kMatchCap));
            QmLogA(buf);

            for (int i = 0; i < QmScan::ScanResult::kMatchCap; ++i)
            {
                const auto& m = r.matches[i];
                if (m.address == 0) continue;
                _snprintf_s(buf, sizeof(buf), _TRUNCATE,
                    "[Scan] match[%d]: addr=0x%llX (+0x%llX) score=%u  MaxElems=%d NumElems=%d MaxChunks=%d NumChunks=%d  probed=%u populated=%u vtableInText=%u classInData=%u layoutBonus=%u",
                    i,
                    (unsigned long long)m.address,
                    (unsigned long long)(m.address - g_imageBase),
                    m.qualityScore,
                    m.maxElements, m.numElements, m.maxChunks, m.numChunks,
                    m.uobjSlotsProbed, m.uobjSlotsPopulated,
                    m.uobjVtableInText, m.uobjClassInData, m.layoutBonus);
                QmLogA(buf);
            }
        }

        // When the scan didn't return a candidate, dump diag detail so the
        // next trace tells us WHICH validation step is killing every slot.
        // The most-frequent reject reason is the candidate fix target.
        if (!r.gobjectsFromScan && r.gobjectsCandidatesTested > 0)
        {
            const auto& c = r.rejects;
            _snprintf_s(buf, sizeof(buf), _TRUNCATE,
                "[Scan] rejects: notReadable=%u maxElemsRange=%u numElemsRange=%u chunksRange=%u elemsPerChunk=%u objectsNull=%u objectsUnreadable=%u firstChunkNull=%u firstChunkUnreadable=%u validObjectsTooFew=%u sehFault=%u",
                c.notReadable, c.maxElemsRange, c.numElemsRange, c.chunksRange,
                c.elemsPerChunkRange, c.objectsNull, c.objectsUnreadable,
                c.firstChunkNull, c.firstChunkUnreadable, c.validObjectsTooFew, c.sehFault);
            QmLogA(buf);

            // Top near-miss candidates - these are the slots that passed the
            // most checks before being rejected. If we see something with
            // passedChecks==8 or 9, the candidate IS GObjects (just one check
            // away from passing) and we know exactly which check to relax.
            for (int i = 0; i < QmScan::ScanResult::kNearMissCap; ++i)
            {
                const auto& nm = r.nearMisses[i];
                if (nm.passedChecks == 0) continue;
                const char* reasonName = "?";
                switch (nm.rejectReasonBit)
                {
                    case (1u << 0): reasonName = "notReadable"; break;
                    case (1u << 1): reasonName = "objectsNull"; break;
                    case (1u << 2): reasonName = "maxElemsRange"; break;
                    case (1u << 3): reasonName = "numElemsRange"; break;
                    case (1u << 4): reasonName = "chunksRange"; break;
                    case (1u << 5): reasonName = "elemsPerChunkRange"; break;
                    case (1u << 6): reasonName = "objectsUnreadable"; break;
                    case (1u << 7): reasonName = "firstChunkNull"; break;
                    case (1u << 8): reasonName = "firstChunkUnreadable"; break;
                    case (1u << 9): reasonName = "validObjectsTooFew"; break;
                    case (1u << 10): reasonName = "sehFault"; break;
                }
                _snprintf_s(buf, sizeof(buf), _TRUNCATE,
                    "[Scan] near-miss[%d]: addr=0x%llX (+0x%llX) passed=%u failedAt=%s  MaxElems=%d NumElems=%d MaxChunks=%d NumChunks=%d Objects=0x%llX",
                    i,
                    (unsigned long long)nm.address,
                    (unsigned long long)(nm.address - g_imageBase),
                    nm.passedChecks, reasonName,
                    nm.maxElements, nm.numElements, nm.maxChunks, nm.numChunks,
                    (unsigned long long)nm.objectsPtr);
                QmLogA(buf);

                // If we captured firstChunk bytes (passedChecks >= 8), dump
                // them as 16 qwords so we can see the actual FUObjectItem
                // layout. We expect UObject pointers (0x14xxxxxxxx for the
                // running server) at a regular stride - that stride IS the
                // FUObjectItem size in this UE build.
                if (nm.firstChunkBytesValid)
                {
                    const uint64_t* qw = reinterpret_cast<const uint64_t*>(nm.firstChunkBytes);
                    // 16 qwords = 128 bytes, in two lines of 8 qwords each
                    // to fit within reasonable log line length.
                    _snprintf_s(buf, sizeof(buf), _TRUNCATE,
                        "[Scan] near-miss[%d]: chunk[0x00..0x40]: %016llX %016llX %016llX %016llX %016llX %016llX %016llX %016llX",
                        i,
                        (unsigned long long)qw[0], (unsigned long long)qw[1],
                        (unsigned long long)qw[2], (unsigned long long)qw[3],
                        (unsigned long long)qw[4], (unsigned long long)qw[5],
                        (unsigned long long)qw[6], (unsigned long long)qw[7]);
                    QmLogA(buf);
                    _snprintf_s(buf, sizeof(buf), _TRUNCATE,
                        "[Scan] near-miss[%d]: chunk[0x40..0x80]: %016llX %016llX %016llX %016llX %016llX %016llX %016llX %016llX",
                        i,
                        (unsigned long long)qw[8],  (unsigned long long)qw[9],
                        (unsigned long long)qw[10], (unsigned long long)qw[11],
                        (unsigned long long)qw[12], (unsigned long long)qw[13],
                        (unsigned long long)qw[14], (unsigned long long)qw[15]);
                    QmLogA(buf);
                }

                // Dump the 32-byte TUObjectArray header itself - reveals the
                // +0x08 PreAllocatedObjects pointer (UE5.4+ layout).
                if (nm.headerBytesValid)
                {
                    const uint64_t* hq = reinterpret_cast<const uint64_t*>(nm.headerBytes);
                    _snprintf_s(buf, sizeof(buf), _TRUNCATE,
                        "[Scan] near-miss[%d]: header[0x00..0x20]: %016llX %016llX %016llX %016llX",
                        i,
                        (unsigned long long)hq[0], (unsigned long long)hq[1],
                        (unsigned long long)hq[2], (unsigned long long)hq[3]);
                    QmLogA(buf);
                }

                // Dump PreAllocatedObjects bytes - this is where UE5.6
                // dedicated-server stores initial UObjects before chunk[0]
                // is allocated. If chunk[0] dumped all zeros and these qwords
                // show 0x14xxxxxxxx pointers, we proved the layout.
                if (nm.preAllocBytesValid)
                {
                    const uint64_t* pq = reinterpret_cast<const uint64_t*>(nm.preAllocBytes);
                    _snprintf_s(buf, sizeof(buf), _TRUNCATE,
                        "[Scan] near-miss[%d]: preAlloc[0x00..0x40]: %016llX %016llX %016llX %016llX %016llX %016llX %016llX %016llX",
                        i,
                        (unsigned long long)pq[0], (unsigned long long)pq[1],
                        (unsigned long long)pq[2], (unsigned long long)pq[3],
                        (unsigned long long)pq[4], (unsigned long long)pq[5],
                        (unsigned long long)pq[6], (unsigned long long)pq[7]);
                    QmLogA(buf);
                    _snprintf_s(buf, sizeof(buf), _TRUNCATE,
                        "[Scan] near-miss[%d]: preAlloc[0x40..0x80]: %016llX %016llX %016llX %016llX %016llX %016llX %016llX %016llX",
                        i,
                        (unsigned long long)pq[8],  (unsigned long long)pq[9],
                        (unsigned long long)pq[10], (unsigned long long)pq[11],
                        (unsigned long long)pq[12], (unsigned long long)pq[13],
                        (unsigned long long)pq[14], (unsigned long long)pq[15]);
                    QmLogA(buf);
                }
            }
        }
    }

    bool Init()
    {
        if (IsReady()) return true;

        // Module base = the EXE (game) module. GetModuleHandle(NULL) returns it.
        if (!g_imageBase)
            g_imageBase = reinterpret_cast<uintptr_t>(GetModuleHandleA(NULL));
        if (!g_imageBase) return false;

        // One-time symbol resolution via runtime scan with hardcoded fallback.
        if (!s_symbolsResolved)
        {
            QmScan::ScanResult r = QmScan::ResolveAll(
                g_imageBase,
                OFFSET_GObjects,
                OFFSET_AppendString,
                OFFSET_ProcessEvent,
                PROCESS_EVENT_VTBL_IDX);

            g_gobjects      = reinterpret_cast<TUObjectArray*>(r.gobjects);
            g_appendString  = reinterpret_cast<AppendStringFn>(r.appendString);
            g_processEvent  = reinterpret_cast<ProcessEventFn>(r.processEvent);

            s_gobjectsFromScan = r.gobjectsFromScan;
            s_appendFromScan   = r.appendStringFromScan;
            s_processFromScan  = r.processEventFromScan;
            s_symbolsResolved  = true;

            LogScanResult(r);
        }

        if (!g_appendString)        return false;

        // Decide if we need to (re)scan. Reasons:
        //   1. g_gobjects is null (initial scan returned nullptr because
        //      GObjects wasn't yet populated; fallback offset is also bad).
        //   2. g_gobjects came from the hardcoded fallback (steam-patch
        //      drift may have shifted the static struct).
        //   3. g_gobjects looks unpopulated (NumElements < kPopulatedThresh).
        //      The scanner's strict-mode normally prevents picking those,
        //      but this is a belt-and-braces check for the case where a
        //      stale pointer survived from a previous tick.
        // SEH-guard the Num() read because g_gobjects may point into
        // unmapped memory if the hardcoded fallback offset is out of range
        // (which it IS on the dedicated server: CLIENT's offset 0x10A570D0
        // falls beyond the server's .data section).
        bool gobjectsLooksDead = !g_gobjects;
        if (!gobjectsLooksDead)
        {
            __try
            {
                if (!g_gobjects->Objects) gobjectsLooksDead = true;
                else if (g_gobjects->Num() < 100) gobjectsLooksDead = true;
            }
            __except (EXCEPTION_EXECUTE_HANDLER)
            {
                gobjectsLooksDead = true;
            }
        }

        if (gobjectsLooksDead)
        {
            void* rescan = QmScan::RescanGObjects(g_imageBase);
            if (rescan && rescan != g_gobjects)
            {
                char buf[256];
                _snprintf_s(buf, sizeof(buf), _TRUNCATE,
                    "[Scan] rescan: GObjects %s -> +0x%llX (Num=%d MaxElements=%d NumChunks=%d)",
                    g_gobjects ? "relocated" : "found",
                    (unsigned long long)((uintptr_t)rescan - g_imageBase),
                    reinterpret_cast<TUObjectArray*>(rescan)->NumElements,
                    reinterpret_cast<TUObjectArray*>(rescan)->MaxElements,
                    reinterpret_cast<TUObjectArray*>(rescan)->NumChunks);
                QmLogA(buf);
                g_gobjects         = reinterpret_cast<TUObjectArray*>(rescan);
                s_gobjectsFromScan = true;
            }
            else
            {
                // Rescan didn't find anything (yet). If we still have a
                // candidate that AV-crashes when read (e.g. CLIENT's
                // fallback offset that points beyond the server's .data),
                // null it out so this Init() returns false cleanly and the
                // probe-loop retries on the next tick.
                __try
                {
                    if (g_gobjects && !g_gobjects->Objects)
                        g_gobjects = nullptr;
                }
                __except (EXCEPTION_EXECUTE_HANDLER)
                {
                    g_gobjects = nullptr;
                }
                return false;
            }
        }

        if (!g_gobjects)            return false;

        // GObjects may be allocated lazily during early engine init. If Num()
        // is 0 or Objects is null, caller should retry. SEH-guarded because
        // an in-flight GC walk could observe a torn pointer (extremely rare
        // but cheap to guard against).
        bool ready = false;
        __try
        {
            ready = (g_gobjects->Objects != nullptr) && (g_gobjects->Num() > 0);
        }
        __except (EXCEPTION_EXECUTE_HANDLER)
        {
            ready = false;
        }
        if (!ready) return false;

        // First time we observe a live, populated GObjects: log the live
        // characteristics so post-update issues are obvious from the log.
        if (!s_gobjectsLiveLogged)
        {
            s_gobjectsLiveLogged = true;
            char buf[256];
            _snprintf_s(buf, sizeof(buf), _TRUNCATE,
                "[Scan] GObjects live: +0x%llX Num=%d MaxElements=%d NumChunks=%d (via %s)",
                (unsigned long long)((uintptr_t)g_gobjects - g_imageBase),
                g_gobjects->NumElements, g_gobjects->MaxElements, g_gobjects->NumChunks,
                s_gobjectsFromScan ? "scan" : "hardcoded");
            QmLogA(buf);
        }

        // Now that GObjects is populated we can re-derive ProcessEvent via
        // vtable[slot]. The initial scan during ResolveAll() ran before the
        // engine populated GObjects, so ProcessEvent stayed on the fallback
        // (smoke-tested or null). On the dedicated server the fallback is
        // the CLIENT offset which points to non-code bytes - calling it
        // would crash the wineserver. Rescan once, only when not already
        // resolved via the live vtable.
        if (!s_processFromScan)
        {
            void* pe = QmScan::RescanProcessEvent(g_gobjects, PROCESS_EVENT_VTBL_IDX);
            if (pe && pe != reinterpret_cast<void*>(g_processEvent))
            {
                char buf[256];
                _snprintf_s(buf, sizeof(buf), _TRUNCATE,
                    "[Scan] ProcessEvent rescan: %s -> 0x%llX (+0x%llX)",
                    g_processEvent ? "relocated" : "found",
                    (unsigned long long)(uintptr_t)pe,
                    (unsigned long long)((uintptr_t)pe - g_imageBase));
                QmLogA(buf);
                g_processEvent    = reinterpret_cast<ProcessEventFn>(pe);
                s_processFromScan = true;
            }
        }

        return true;
    }

    bool CallProcessEvent(UObject* self, UFunction* func, void* parms)
    {
        if (!self || !func || !g_processEvent) return false;
        __try
        {
            g_processEvent(self, func, parms);
            return true;
        }
        __except (EXCEPTION_EXECUTE_HANDLER)
        {
            return false;
        }
    }

    UObject* GetClassDefaultObject(UClass* cls)
    {
        if (!cls) return nullptr;
        __try { return cls->ClassDefaultObject; }
        __except (EXCEPTION_EXECUTE_HANDLER) { return nullptr; }
    }

    // ---- UGameplayStatics::SpawnObject UFunction wrapper ----
    //
    // Function:    Engine.GameplayStatics.SpawnObject
    // Signature:   UObject* SpawnObject(TSubclassOf<UObject> ObjectClass, UObject* Outer)
    // Param block: { UClass* ObjectClass; UObject* Outer; UObject* ReturnValue; }
    //              size 0x18, align 0x08 (verified via Dumper-7 Assertions.inl)
    //
    // Cached after first successful lookup so repeated spawns don't pay the
    // GObjects-walk cost.
    static UClass*    s_gameplayStaticsClass = nullptr;
    static UFunction* s_spawnObjectFunc      = nullptr;
    static UObject*   s_gameplayStaticsCDO   = nullptr;

    UObject* SpawnObjectViaUFunction(UClass* objectClass, UObject* outer)
    {
        if (!objectClass) return nullptr;
        if (!IsReady()) return nullptr;
        if (!g_processEvent) return nullptr;

        // Lazy resolve of GameplayStatics CDO + SpawnObject UFunction.
        if (!s_gameplayStaticsClass)
            s_gameplayStaticsClass = FindClassByName("GameplayStatics");
        if (!s_gameplayStaticsClass) return nullptr;

        if (!s_spawnObjectFunc)
            s_spawnObjectFunc = FindFunctionOnClass(s_gameplayStaticsClass, "SpawnObject");
        if (!s_spawnObjectFunc) return nullptr;

        if (!s_gameplayStaticsCDO)
            s_gameplayStaticsCDO = GetClassDefaultObject(s_gameplayStaticsClass);
        if (!s_gameplayStaticsCDO) return nullptr;

        // Param block layout (Engine_parameters.hpp:11728):
        //   0x00 UClass* ObjectClass
        //   0x08 UObject* Outer
        //   0x10 UObject* ReturnValue
        struct Params {
            UClass*  ObjectClass;
            UObject* Outer;
            UObject* ReturnValue;
        };
        Params parms = {};
        parms.ObjectClass = objectClass;
        parms.Outer       = outer ? outer : s_gameplayStaticsCDO;
        parms.ReturnValue = nullptr;

        // The SDK code (Engine_functions.cpp:31969-31974) temporarily ORs in
        // FUNC_Native (0x400) to bypass the BlueprintCallable script frame
        // check. Mirror that exactly - otherwise ProcessEvent might dispatch
        // through the bytecode interpreter path and fail in shipping builds.
        uint32 oldFlags = s_spawnObjectFunc->FunctionFlags;
        s_spawnObjectFunc->FunctionFlags = oldFlags | 0x400;

        bool ok = CallProcessEvent(s_gameplayStaticsCDO, s_spawnObjectFunc, &parms);

        s_spawnObjectFunc->FunctionFlags = oldFlags;

        if (!ok) return nullptr;
        return parms.ReturnValue;
    }

    // ---- UKismetStringLibrary::Conv_StringToName UFunction wrapper ----
    //
    // Function:    Engine.KismetStringLibrary.Conv_StringToName
    // Signature:   FName Conv_StringToName(FString InString)
    // Param block: { FString InString; FName ReturnValue; } size 0x18, align 0x08
    //              (verified via Dumper-7 Assertions.inl).
    //
    // This is the runtime equivalent of `FName(TEXT("foo"))` - it interns the
    // string in the global FName pool (if not already there) and returns the
    // {ComparisonIndex, Number} pair. We use it to construct PackageName +
    // AssetName for a SoftObjectPath that the game has never seen, so we can
    // point a widget at an asset that isn't loaded into GObjects yet.
    //
    // Cached after first lookup so per-hit retries don't repeatedly walk GObjects.
    static UClass*    s_kismetStringLibClass = nullptr;
    static UFunction* s_convStringToNameFunc = nullptr;
    static UObject*   s_kismetStringLibCDO   = nullptr;

    bool FNameFromString(const wchar_t* str, FName* outName)
    {
        if (!outName) return false;
        outName->ComparisonIndex = 0;
        outName->Number          = 0;
        if (!str || !str[0]) return false;
        if (!IsReady()) return false;
        if (!g_processEvent) return false;

        if (!s_kismetStringLibClass)
            s_kismetStringLibClass = FindClassByName("KismetStringLibrary");
        if (!s_kismetStringLibClass) return false;

        if (!s_convStringToNameFunc)
            s_convStringToNameFunc = FindFunctionOnClass(s_kismetStringLibClass, "Conv_StringToName");
        if (!s_convStringToNameFunc) return false;

        if (!s_kismetStringLibCDO)
            s_kismetStringLibCDO = GetClassDefaultObject(s_kismetStringLibClass);
        if (!s_kismetStringLibCDO) return false;

        // Param block (Assertions.inl: size 0x18, align 0x08):
        //   0x00 FString InString (16 bytes)
        //   0x10 FName   ReturnValue (8 bytes)
        struct Params {
            FString InString;
            FName   ReturnValue;
        };

        // FString needs Num to include the null terminator (UE5 convention).
        // The function should treat InString as read-only - if it tries to
        // realloc through FMemory we'd crash, but Conv_StringToName is a
        // trivial intern lookup so a stack buffer is safe in practice.
        wchar_t buf[1024];
        size_t len = 0;
        while (str[len] && len < 1022) { buf[len] = str[len]; ++len; }
        buf[len] = L'\0';

        Params parms = {};
        parms.InString.Data = buf;
        parms.InString.Num  = static_cast<int32>(len + 1);  // includes null
        parms.InString.Max  = static_cast<int32>(len + 1);
        parms.ReturnValue   = {0, 0};

        // Mirror SpawnObject's flag-flip: temporarily OR in FUNC_Native (0x400)
        // so shipping-build dispatch doesn't try the bytecode interpreter path.
        uint32 oldFlags = s_convStringToNameFunc->FunctionFlags;
        s_convStringToNameFunc->FunctionFlags = oldFlags | 0x400;

        bool ok = CallProcessEvent(s_kismetStringLibCDO, s_convStringToNameFunc, &parms);

        s_convStringToNameFunc->FunctionFlags = oldFlags;

        if (!ok) return false;
        *outName = parms.ReturnValue;
        return !outName->IsNone();
    }

    // ---- UKismetTextLibrary::Conv_StringToText UFunction wrapper ----
    //
    // Function:    Engine.KismetTextLibrary.Conv_StringToText
    // Signature:   FText Conv_StringToText(FString InString)
    // Param block: { FString InString (0x10); FText ReturnValue (0x10); } size 0x20
    //              (verified via Engine_parameters.hpp KismetTextLibrary_Conv_StringToText).
    //
    // Runtime equivalent of `FText::FromString(...)`: the returned FText carries the
    // source string inline, so it reads back without a localization lookup. We use it
    // to give a constructed GameSettingCollection a visible "Quartermaster" label.
    static UClass*    s_kismetTextLibClass    = nullptr;
    static UFunction* s_convStringToTextFunc  = nullptr;
    static UObject*   s_kismetTextLibCDO      = nullptr;

    bool TextFromString(const wchar_t* str, void* outText16)
    {
        if (!outText16) return false;
        memset(outText16, 0, 16);
        if (!str) return false;
        if (!IsReady() || !g_processEvent) return false;

        if (!s_kismetTextLibClass)
            s_kismetTextLibClass = FindClassByName("KismetTextLibrary");
        if (!s_kismetTextLibClass) return false;

        if (!s_convStringToTextFunc)
            s_convStringToTextFunc = FindFunctionOnClass(s_kismetTextLibClass, "Conv_StringToText");
        if (!s_convStringToTextFunc) return false;

        if (!s_kismetTextLibCDO)
            s_kismetTextLibCDO = GetClassDefaultObject(s_kismetTextLibClass);
        if (!s_kismetTextLibCDO) return false;

        // Param block: 0x00 FString InString; 0x10 FText ReturnValue (16 bytes).
        struct Params {
            FString InString;
            uint8   ReturnValue[16];   // FText, copied out raw
        };

        wchar_t buf[512];
        size_t len = 0;
        while (str[len] && len < 510) { buf[len] = str[len]; ++len; }
        buf[len] = L'\0';

        Params parms = {};
        parms.InString.Data = buf;
        parms.InString.Num  = static_cast<int32>(len + 1);  // includes null
        parms.InString.Max  = static_cast<int32>(len + 1);

        uint32 oldFlags = s_convStringToTextFunc->FunctionFlags;
        s_convStringToTextFunc->FunctionFlags = oldFlags | 0x400;

        bool ok = CallProcessEvent(s_kismetTextLibCDO, s_convStringToTextFunc, &parms);

        s_convStringToTextFunc->FunctionFlags = oldFlags;

        if (!ok) return false;
        memcpy(outText16, parms.ReturnValue, 16);
        return true;
    }

    // ---- UKismetTextLibrary::Conv_TextToString UFunction wrapper ----
    //
    // Function:    Engine.KismetTextLibrary.Conv_TextToString
    // Signature:   FString Conv_TextToString(FText InText)
    // Param block: { FText InText (0x10); FString ReturnValue (0x10); } size 0x20
    //              (verified via Engine_parameters.hpp KismetTextLibrary_Conv_TextToString).
    //
    // The returned FString's character buffer is engine-allocated; it is handed back
    // to FMemory via QmAlloc::Realloc(ptr, 0) when the allocator is resolved. The raw
    // FText copy in the parm block holds an un-released shared ref (same documented
    // trade-off as TextFromString) - bounded, callers only convert on text CHANGE.
    static UFunction* s_convTextToStringFunc = nullptr;

    bool StringFromText(const void* text16, wchar_t* out, size_t outCap)
    {
        if (!out || outCap == 0) return false;
        out[0] = L'\0';
        if (!text16) return false;
        if (!IsReady() || !g_processEvent) return false;

        if (!s_kismetTextLibClass)
            s_kismetTextLibClass = FindClassByName("KismetTextLibrary");
        if (!s_kismetTextLibClass) return false;

        if (!s_convTextToStringFunc)
            s_convTextToStringFunc = FindFunctionOnClass(s_kismetTextLibClass, "Conv_TextToString");
        if (!s_convTextToStringFunc) return false;

        if (!s_kismetTextLibCDO)
            s_kismetTextLibCDO = GetClassDefaultObject(s_kismetTextLibClass);
        if (!s_kismetTextLibCDO) return false;

        // Param block: 0x00 FText InText (16 bytes); 0x10 FString ReturnValue.
        struct Params {
            uint8   InText[16];
            FString ReturnValue;
        };

        Params parms = {};
        memcpy(parms.InText, text16, 16);

        uint32 oldFlags = s_convTextToStringFunc->FunctionFlags;
        s_convTextToStringFunc->FunctionFlags = oldFlags | 0x400;

        bool ok = CallProcessEvent(s_kismetTextLibCDO, s_convTextToStringFunc, &parms);

        s_convTextToStringFunc->FunctionFlags = oldFlags;

        if (!ok) return false;

        if (parms.ReturnValue.Data && parms.ReturnValue.Num > 0)
        {
            size_t n = (size_t)parms.ReturnValue.Num;   // includes the terminator
            if (n > outCap) n = outCap;
            memcpy(out, parms.ReturnValue.Data, (n - 1) * sizeof(wchar_t));
            out[n - 1] = L'\0';
            if (QmAlloc::IsResolved())
                QmAlloc::Realloc(parms.ReturnValue.Data, 0, 0);
        }
        return true;
    }

    // ---- UKismetSystemLibrary::LoadAsset_Blocking UFunction wrapper ----
    //
    // Function:    Engine.KismetSystemLibrary.LoadAsset_Blocking
    // Signature:   UObject* LoadAsset_Blocking(TSoftObjectPtr<UObject> Asset)
    // Param block: { TSoftObjectPtr Asset (0x28); UObject* ReturnValue (0x08); }
    //              total size 0x30, align 0x08 (verified via Dumper-7
    //              Engine_parameters.hpp KismetSystemLibrary_LoadAsset_Blocking).
    //
    // TSoftObjectPtr<UObject> layout (0x28 bytes):
    //   0x00 FSoftObjectPath {
    //     0x00 FTopLevelAssetPath {
    //       0x00 FName PackageName    (8 bytes)
    //       0x08 FName AssetName      (8 bytes)
    //     }
    //     0x10 FUtf8String SubPathString (16 bytes, empty for top-level assets)
    //   }
    //   0x20 TWeakObjectPtr WeakObjectPointer (8 bytes, zero-init = unresolved)
    //
    // Cached after first lookup so the per-init pre-warm loop doesn't repeatedly
    // walk GObjects for the same UFunction.
    static UClass*    s_kismetSysLibClass     = nullptr;
    static UFunction* s_loadAssetBlockingFunc = nullptr;
    static UObject*   s_kismetSysLibCDO       = nullptr;

    UObject* LoadAssetByPath(const wchar_t* packagePathW, const wchar_t* assetNameW)
    {
        if (!packagePathW || !packagePathW[0] || !assetNameW || !assetNameW[0])
        {
            QM_LOG_WARN("[LoadAsset] FAIL: empty pkg or asset name input");
            return nullptr;
        }
        if (!IsReady() || !g_processEvent)
        {
            QM_LOG_WARN("[LoadAsset] FAIL: UE not ready or ProcessEvent unresolved (IsReady=%d g_processEvent=0x%p)",
                IsReady() ? 1 : 0, (void*)g_processEvent);
            return nullptr;
        }

        if (!s_kismetSysLibClass)
            s_kismetSysLibClass = FindClassByName("KismetSystemLibrary");
        if (!s_kismetSysLibClass)
        {
            QM_LOG_WARN("[LoadAsset] FAIL: FindClassByName('KismetSystemLibrary') returned null");
            return nullptr;
        }

        if (!s_loadAssetBlockingFunc)
        {
            s_loadAssetBlockingFunc = FindFunctionOnClass(s_kismetSysLibClass, "LoadAsset_Blocking");
            if (s_loadAssetBlockingFunc)
                QM_LOG_INFO("[LoadAsset] resolved UFunction LoadAsset_Blocking @ 0x%p Flags=0x%08X ExecFn=0x%p",
                    s_loadAssetBlockingFunc, s_loadAssetBlockingFunc->FunctionFlags,
                    (void*)s_loadAssetBlockingFunc->ExecFunction);
        }
        if (!s_loadAssetBlockingFunc)
        {
            QM_LOG_WARN("[LoadAsset] FAIL: FindFunctionOnClass('LoadAsset_Blocking') returned null");
            return nullptr;
        }

        if (!s_kismetSysLibCDO)
            s_kismetSysLibCDO = GetClassDefaultObject(s_kismetSysLibClass);
        if (!s_kismetSysLibCDO)
        {
            QM_LOG_WARN("[LoadAsset] FAIL: KismetSystemLibrary CDO is null");
            return nullptr;
        }

        // Intern PackageName + AssetName in the global FName pool first.
        FName pkgFName   = {0, 0};
        FName assetFName = {0, 0};
        if (!FNameFromString(packagePathW, &pkgFName))
        {
            QM_LOG_WARN("[LoadAsset] FAIL: FNameFromString(pkg='%ls') returned false", packagePathW);
            return nullptr;
        }
        if (!FNameFromString(assetNameW, &assetFName))
        {
            QM_LOG_WARN("[LoadAsset] FAIL: FNameFromString(asset='%ls') returned false", assetNameW);
            return nullptr;
        }

        QM_LOG_INFO("[LoadAsset] FNames pkg='%ls' (cmp=%d num=%u) asset='%ls' (cmp=%d num=%u)",
            packagePathW, pkgFName.ComparisonIndex, pkgFName.Number,
            assetNameW,   assetFName.ComparisonIndex, assetFName.Number);

        // Param block (0x30 bytes total) - CORRECTED LAYOUT.
        //
        // TSoftObjectPtr is NOT laid out as { Path, WeakPtr } - it's a subclass
        // of TPersistentObjectPtr<FSoftObjectPath> which puts the WeakPtr FIRST
        // (Basic.hpp:568-583):
        //
        //   TPersistentObjectPtr (0x28 total):
        //     0x00 FWeakObjectPtr WeakPtr            (8 bytes, zero = unresolved)
        //     0x08 FSoftObjectPath ObjectID (0x20):
        //       0x08 FTopLevelAssetPath.PackageName  (FName, 8 bytes)
        //       0x10 FTopLevelAssetPath.AssetName    (FName, 8 bytes)
        //       0x18 FUtf8String SubPathString       (16 bytes, zero = empty)
        //
        // The PREVIOUS layout had PackageName at 0x00, which is where WeakPtr
        // lives. UE5 then read our cmp/num as (ObjectIndex, SerialNumber),
        // failed to resolve as a weak ref, fell back to FindObject(Outer=null,
        // Name=AssetName) and logged "Object Keine.DA_BI_xxx not found". Both
        // vanilla AND mod DAs failed identically - the smoking gun.
        struct Params {
            uint64_t WeakObjectPtr;     // 0x00 - FWeakObjectPtr (zero = unresolved)
            FName    PackageName;       // 0x08 - FTopLevelAssetPath.PackageName
            FName    AssetName;         // 0x10 - FTopLevelAssetPath.AssetName
            uint8_t  SubPathString[16]; // 0x18 - FUtf8String (Data,Num,Max), zero = empty
            UObject* ReturnValue;       // 0x28
        };
        static_assert(sizeof(Params) == 0x30, "LoadAsset_Blocking param block size must be 0x30");

        Params parms = {};
        parms.WeakObjectPtr = 0;
        parms.PackageName   = pkgFName;
        parms.AssetName     = assetFName;
        parms.ReturnValue   = nullptr;

        // Mirror SpawnObject/Conv_StringToName: temporarily OR in FUNC_Native
        // (0x400) so shipping-build dispatch doesn't try the bytecode
        // interpreter path (which would NULL-deref the empty script frame).
        uint32 oldFlags = s_loadAssetBlockingFunc->FunctionFlags;
        s_loadAssetBlockingFunc->FunctionFlags = oldFlags | 0x400;

        QM_LOG_DEBUG("[LoadAsset] calling ProcessEvent(KismetSysLib::LoadAsset_Blocking, pkg='%ls') ...",
            packagePathW);
        bool ok = CallProcessEvent(s_kismetSysLibCDO, s_loadAssetBlockingFunc, &parms);

        s_loadAssetBlockingFunc->FunctionFlags = oldFlags;

        QM_LOG_INFO("[LoadAsset] ProcessEvent done: ok=%d ReturnValue=0x%p pkg='%ls'",
            ok ? 1 : 0, (void*)parms.ReturnValue, packagePathW);

        if (!ok) return nullptr;
        return parms.ReturnValue;
    }

    bool ResolveFName(const FName& name, wchar_t* outBuf, int32 outCap, int32& outNum)
    {
        outNum = 0;
        if (!outBuf || outCap < 2) return false;
        outBuf[0] = L'\0';
        if (name.IsNone() || !g_appendString) return false;

        // FString must own a pointer to outBuf with Max=outCap and Num=0. The
        // game function will overwrite outBuf and set Num to the resulting
        // length (NOT including null). It may also realloc if Max < required,
        // but we give it a large fixed buffer to avoid that path.
        FString fs;
        fs.Data = outBuf;
        fs.Num  = 0;
        fs.Max  = outCap;

        // SEH guard - if AppendString blows up (e.g. GObjects half-initialized
        // during very early call) we don't want to take down the game.
        __try
        {
            g_appendString(&name, &fs);
        }
        __except (EXCEPTION_EXECUTE_HANDLER)
        {
            outBuf[0] = L'\0';
            return false;
        }

        outNum = fs.Num;
        if (outNum < 0 || outNum >= outCap)
        {
            outBuf[0] = L'\0';
            return false;
        }
        outBuf[outNum] = L'\0';
        return true;
    }

    bool ResolveFNameNarrow(const FName& name, char* outBuf, int32 outCap)
    {
        if (!outBuf || outCap < 2) return false;
        outBuf[0] = '\0';

        wchar_t wide[1024];
        int32 wideNum = 0;
        if (!ResolveFName(name, wide, 1024, wideNum)) return false;

        int n = WideCharToMultiByte(CP_UTF8, 0, wide, wideNum, outBuf, outCap - 1, NULL, NULL);
        if (n < 0) n = 0;
        if (n >= outCap) n = outCap - 1;
        outBuf[n] = '\0';
        return true;
    }

    UClass* FindClassByName(const char* shortName)
    {
        if (!IsReady() || !shortName) return nullptr;

        const int32 total = g_gobjects->Num();
        char nameBuf[256];

        for (int32 i = 0; i < total; ++i)
        {
            UObject* obj = g_gobjects->GetByIndex(i);
            if (!obj || !obj->Class) continue;

            // Cheap pre-filter: only consider objects whose class has the Class
            // cast flag (i.e. the object IS a UClass instance).
            UClass* asClass = obj->Class;
            if ((asClass->CastFlags & CASTFLAG_Class) == 0) continue;

            if (!ResolveFNameNarrow(obj->Name, nameBuf, sizeof(nameBuf))) continue;
            if (strcmp(nameBuf, shortName) == 0)
            {
                return reinterpret_cast<UClass*>(obj);
            }
        }
        return nullptr;
    }

    UFunction* FindFunctionOnClass(UStruct* cls, const char* funcName)
    {
        if (!cls || !funcName) return nullptr;

        char nameBuf[256];
        for (UStruct* s = cls; s != nullptr; s = s->SuperStruct)
        {
            for (UField* field = s->Children; field != nullptr; field = field->Next)
            {
                if (!field || !field->Class) continue;
                if ((field->Class->CastFlags & CASTFLAG_Function) == 0) continue;
                if (!ResolveFNameNarrow(field->Name, nameBuf, sizeof(nameBuf))) continue;
                if (strcmp(nameBuf, funcName) == 0)
                {
                    return reinterpret_cast<UFunction*>(field);
                }
            }
        }
        return nullptr;
    }

    UObject* FindObjectByClassAndName(const char* className, const char* objName)
    {
        if (!IsReady() || !className || !objName) return nullptr;

        const int32 total = g_gobjects->Num();
        char clsBuf[256];
        char nameBuf[256];

        for (int32 i = 0; i < total; ++i)
        {
            UObject* obj = g_gobjects->GetByIndex(i);
            if (!obj || !obj->Class) continue;

            // Class-name pre-filter (cheaper to compare strings than to resolve
            // the object's own name for every entry).
            if (!ResolveFNameNarrow(obj->Class->Name, clsBuf, sizeof(clsBuf))) continue;
            if (strcmp(clsBuf, className) != 0) continue;

            if (!ResolveFNameNarrow(obj->Name, nameBuf, sizeof(nameBuf))) continue;
            if (strcmp(nameBuf, objName) == 0)
            {
                return obj;
            }
        }
        return nullptr;
    }

    UObject* FindFirstInstanceOfClass(const char* className)
    {
        if (!IsReady() || !className) return nullptr;

        // EObjectFlags bit for the archetype-per-class default object. The CDO carries
        // template state, not the live widget's runtime data, so we must skip it.
        constexpr uint32 RF_ClassDefaultObject = 0x00000010;

        const int32 total = g_gobjects->Num();
        char clsBuf[256];

        for (int32 i = 0; i < total; ++i)
        {
            UObject* obj = g_gobjects->GetByIndex(i);
            if (!obj || !obj->Class) continue;
            if (obj->Flags & RF_ClassDefaultObject) continue;

            if (!ResolveFNameNarrow(obj->Class->Name, clsBuf, sizeof(clsBuf))) continue;
            if (strcmp(clsBuf, className) == 0)
            {
                return obj;
            }
        }
        return nullptr;
    }

    ProcessInternalFn GetProcessInternalFn()
    {
        if (g_processInternal) return g_processInternal;
        if (!IsReady()) return nullptr;

        // Reflection-only resolve: across all non-native UFunctions the single most common
        // ExecFunction value is &UObject::ProcessInternal (UFunction::Bind sets it for every
        // script function; native functions each carry their own exec). Tally the mode and
        // early-out once one candidate is conclusively dominant.
        constexpr uint32 LOCAL_FUNC_Native = 0x00000400;
        constexpr int    kSlots            = 16;
        constexpr int    kEarlyOutVotes    = 512;   // a single exec shared this widely == ProcessInternal
        void* cand[kSlots] = {};
        int   votes[kSlots] = {};
        int   distinct = 0;
        int   sampled  = 0;

        const int32 total = g_gobjects->Num();
        for (int32 i = 0; i < total; ++i)
        {
            UObject* obj = g_gobjects->GetByIndex(i);
            if (!obj || !obj->Class) continue;

            void* ex = nullptr;
            __try
            {
                if ((obj->Class->CastFlags & CASTFLAG_Function) == 0) continue;
                UFunction* fn = reinterpret_cast<UFunction*>(obj);
                if (fn->FunctionFlags & LOCAL_FUNC_Native) continue;   // native -> own exec
                ex = reinterpret_cast<void*>(fn->ExecFunction);
            }
            __except (EXCEPTION_EXECUTE_HANDLER) { continue; }
            if (!ex) continue;

            ++sampled;
            int hit = -1;
            for (int s = 0; s < distinct; ++s) { if (cand[s] == ex) { hit = s; break; } }
            if (hit < 0)
            {
                if (distinct < kSlots) { hit = distinct++; cand[hit] = ex; votes[hit] = 0; }
                else continue;   // table full of non-PI noise; the real mode is already tracked
            }
            if (++votes[hit] >= kEarlyOutVotes) break;   // conclusively dominant -> stop scanning
        }

        int best = -1;
        for (int s = 0; s < distinct; ++s) { if (best < 0 || votes[s] > votes[best]) best = s; }
        if (best < 0)
        {
            QM_LOG_WARN("[PI] no non-native UFunction found in GObjects - ProcessInternal unresolved (sampled=%d)", sampled);
            return nullptr;
        }

        void* pi = cand[best];
        bool  exec = false;
        MEMORY_BASIC_INFORMATION mbi;
        if (VirtualQuery(pi, &mbi, sizeof(mbi)) == sizeof(mbi))
        {
            const DWORD p = mbi.Protect;
            exec = (mbi.State == MEM_COMMIT) &&
                   (p & (PAGE_EXECUTE | PAGE_EXECUTE_READ | PAGE_EXECUTE_READWRITE | PAGE_EXECUTE_WRITECOPY)) != 0;
        }
        if (!exec)
        {
            QM_LOG_WARN("[PI] ProcessInternal candidate 0x%p not in executable memory (votes=%d/%d sampled=%d distinct=%d) - rejecting",
                        pi, votes[best], sampled, sampled, distinct);
            return nullptr;
        }

        g_processInternal = reinterpret_cast<ProcessInternalFn>(pi);
        QM_LOG_INFO("[PI] ProcessInternal resolved = 0x%p (+0x%llX) via BP-exec mode: %d/%d votes, %d distinct exec ptr(s)",
                    pi, (unsigned long long)((uintptr_t)pi - g_imageBase), votes[best], sampled, distinct);
        return g_processInternal;
    }

} // namespace QmUE
