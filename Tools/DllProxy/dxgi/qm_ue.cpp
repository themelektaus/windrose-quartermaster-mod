// Quartermaster minimal UE5.6 runtime helpers - implementation
#define _CRT_SECURE_NO_WARNINGS
#include <windows.h>
#include <string.h>
#include <stdio.h>

#include "qm_ue.hpp"
#include "qm_scan.hpp"

// External logger from main.cpp - we don't have access to its <cstdio>-free
// LogF macros here, so we use a thin forwarder declared as extern "C".
extern "C" void QmLogA(const char* msg);

namespace QmUE
{
    static uintptr_t       g_imageBase = 0;
    static TUObjectArray*  g_gobjects  = nullptr;
    static AppendStringFn  g_appendString = nullptr;
    static ProcessEventFn  g_processEvent = nullptr;

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
        char buf[512];

        _snprintf_s(buf, sizeof(buf), _TRUNCATE,
            "[Scan] resolved: GObjects=0x%llX (+0x%llX, %s) AppendString=0x%llX (+0x%llX, %s) ProcessEvent=0x%llX (+0x%llX, %s) tested=%u failed=%u in %ums",
            (unsigned long long)(uintptr_t)r.gobjects,
            (unsigned long long)((uintptr_t)r.gobjects - g_imageBase),
            r.gobjectsFromScan ? "scan" : "fallback",
            (unsigned long long)(uintptr_t)r.appendString,
            (unsigned long long)((uintptr_t)r.appendString - g_imageBase),
            r.appendStringFromScan ? "scan" : "fallback",
            (unsigned long long)(uintptr_t)r.processEvent,
            (unsigned long long)((uintptr_t)r.processEvent - g_imageBase),
            r.processEventFromScan ? "scan" : "fallback",
            r.gobjectsCandidatesTested, r.gobjectsValidationFailures, r.scanDurationMs);
        QmLogA(buf);
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

        if (!g_gobjects)            return false;
        if (!g_appendString)        return false;

        // If we're on the hardcoded fallback for GObjects, try a rescan each
        // tick - the hardcoded offset may be stale after a Steam patch, in
        // which case our retry loop would otherwise spin forever.
        if (!s_gobjectsFromScan)
        {
            if (!g_gobjects->Objects || g_gobjects->Num() <= 0)
            {
                void* rescan = QmScan::RescanGObjects(g_imageBase);
                if (rescan && rescan != g_gobjects)
                {
                    char buf[256];
                    _snprintf_s(buf, sizeof(buf), _TRUNCATE,
                        "[Scan] rescan: GObjects relocated from +0x%llX to +0x%llX (hardcoded stale, scan now valid)",
                        (unsigned long long)((uintptr_t)g_gobjects - g_imageBase),
                        (unsigned long long)((uintptr_t)rescan - g_imageBase));
                    QmLogA(buf);
                    g_gobjects        = reinterpret_cast<TUObjectArray*>(rescan);
                    s_gobjectsFromScan = true;
                }
            }
        }

        // GObjects may be allocated lazily during early engine init. If Num()
        // is 0 or Objects is null, caller should retry.
        if (!g_gobjects->Objects || g_gobjects->Num() <= 0)
            return false;

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
} // namespace QmUE
