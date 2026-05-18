// Quartermaster dxgi.dll Proxy + MinHook Bootstrap
// =================================================
// Stage 1: dxgi.dll-Hijack injects us into Windrose-Win64-Shipping.exe.
// Stage 2: MinHook bring-up + Sleep test hook (proof-of-life).
// Stage 3: UE5 reflection probe + GetBuildingGroupsByCategoryTag detour.
// Stage 4: Spawn own UR5BuildingItemWidget + redirect SoftPath to mod asset
//          + fan-out inject into every build category.
//
// File layout:
//   - section 1: PE forwarders + DLL plumbing
//   - section 2: logging primitives (QmLogA / QmLogF / EnsureLogPath)
//   - section 3: inject-marker line
//   - section 4: MinHook test (Sleep hook)
//   - section 5: shared layout helpers (ItemDataLayout) + small UObject probes
//   - section 6: read-only diagnostic inspectors (#if QM_DIAG)
//   - section 7: inject pipeline (Capture/Spawn/Override/Fanout/Inject)
//   - section 8: UFunction detour + install + UE probe loop
//   - section 9: worker thread + DllMain

#define _CRT_SECURE_NO_WARNINGS
#include <windows.h>
#include <shlobj.h>
#include <stdio.h>
#include <stdarg.h>
#include <string.h>

#include "minhook/include/MinHook.h"
#include "qm_log.hpp"
#include "qm_ue.hpp"

#pragma comment(lib, "Shell32.lib")

// ============================================================================
// 1. PE export forwarders - dxgi_org.dll is the real DXGI; we tunnel through.
// ============================================================================
#pragma comment(linker, "/EXPORT:ApplyCompatResolutionQuirking=dxgi_org.ApplyCompatResolutionQuirking,@1")
#pragma comment(linker, "/EXPORT:CompatString=dxgi_org.CompatString,@2")
#pragma comment(linker, "/EXPORT:CompatValue=dxgi_org.CompatValue,@3")
#pragma comment(linker, "/EXPORT:DXGIDumpJournal=dxgi_org.DXGIDumpJournal,@4")
#pragma comment(linker, "/EXPORT:PIXBeginCapture=dxgi_org.PIXBeginCapture,@5")
#pragma comment(linker, "/EXPORT:PIXEndCapture=dxgi_org.PIXEndCapture,@6")
#pragma comment(linker, "/EXPORT:PIXGetCaptureState=dxgi_org.PIXGetCaptureState,@7")
#pragma comment(linker, "/EXPORT:SetAppCompatStringPointer=dxgi_org.SetAppCompatStringPointer,@8")
#pragma comment(linker, "/EXPORT:UpdateHMDEmulationStatus=dxgi_org.UpdateHMDEmulationStatus,@9")
#pragma comment(linker, "/EXPORT:CreateDXGIFactory=dxgi_org.CreateDXGIFactory,@10")
#pragma comment(linker, "/EXPORT:CreateDXGIFactory1=dxgi_org.CreateDXGIFactory1,@11")
#pragma comment(linker, "/EXPORT:CreateDXGIFactory2=dxgi_org.CreateDXGIFactory2,@12")
#pragma comment(linker, "/EXPORT:DXGID3D10CreateDevice=dxgi_org.DXGID3D10CreateDevice,@13")
#pragma comment(linker, "/EXPORT:DXGID3D10CreateLayeredDevice=dxgi_org.DXGID3D10CreateLayeredDevice,@14")
#pragma comment(linker, "/EXPORT:DXGID3D10GetLayeredDeviceSize=dxgi_org.DXGID3D10GetLayeredDeviceSize,@15")
#pragma comment(linker, "/EXPORT:DXGID3D10RegisterLayers=dxgi_org.DXGID3D10RegisterLayers,@16")
#pragma comment(linker, "/EXPORT:DXGIDeclareAdapterRemovalSupport=dxgi_org.DXGIDeclareAdapterRemovalSupport,@17")
#pragma comment(linker, "/EXPORT:DXGIGetDebugInterface1=dxgi_org.DXGIGetDebugInterface1,@18")
#pragma comment(linker, "/EXPORT:DXGIReportAdapterConfiguration=dxgi_org.DXGIReportAdapterConfiguration,@19")

// ============================================================================
// 2. Logging - %LOCALAPPDATA%/R5/Saved/Logs/Quartermaster_Inject.log
//    QmLogA and QmLogF are declared in qm_log.hpp and used everywhere via the
//    QM_LOG_* macros. LogLine is the single FILE-write implementation.
// ============================================================================
static char g_logPath[MAX_PATH] = { 0 };
static CRITICAL_SECTION g_logLock;
static BOOL g_logLockInit = FALSE;

static void EnsureLogPath()
{
    if (g_logPath[0]) return;

    char appdata[MAX_PATH];
    if (FAILED(SHGetFolderPathA(NULL, CSIDL_LOCAL_APPDATA, NULL, 0, appdata)))
        return;

    char logDir[MAX_PATH];
    snprintf(logDir, sizeof(logDir), "%s\\R5\\Saved\\Logs", appdata);
    CreateDirectoryA(logDir, NULL);

    snprintf(g_logPath, sizeof(g_logPath), "%s\\Quartermaster_Inject.log", logDir);
}

// Single point that actually opens the log file. va_list-based so both the
// printf-style QmLogF() and the string-only QmLogA() can funnel here.
static void LogVPrintf(const char* fmt, va_list ap)
{
    EnsureLogPath();
    if (!g_logPath[0]) return;

    if (g_logLockInit) EnterCriticalSection(&g_logLock);

    FILE* f = fopen(g_logPath, "a");
    if (f)
    {
        SYSTEMTIME st;
        GetLocalTime(&st);
        fprintf(f, "[%04d-%02d-%02d %02d:%02d:%02d.%03d] [Quartermaster] ",
            st.wYear, st.wMonth, st.wDay, st.wHour, st.wMinute, st.wSecond, st.wMilliseconds);
        vfprintf(f, fmt, ap);
        fputc('\n', f);
        fclose(f);
    }

    if (g_logLockInit) LeaveCriticalSection(&g_logLock);
}

// Exposed via qm_log.hpp - cross-TU log forwarders.
extern "C" void QmLogA(const char* msg)
{
    if (!msg) return;
    // Use a const fmt + single arg so format-string injection from msg is impossible.
    QmLogF("%s", msg);
}

extern "C" void QmLogF(const char* fmt, ...)
{
    if (!fmt) return;
    va_list ap;
    va_start(ap, fmt);
    LogVPrintf(fmt, ap);
    va_end(ap);
}

// ============================================================================
// 3. Inject marker - first log lines so the user knows the proxy attached.
// ============================================================================
static void WriteInjectMarker(HMODULE hSelf)
{
    char hostExe[MAX_PATH] = { 0 };
    GetModuleFileNameA(NULL, hostExe, MAX_PATH);

    char selfPath[MAX_PATH] = { 0 };
    GetModuleFileNameA(hSelf, selfPath, MAX_PATH);

    QM_LOG_INFO("dxgi.dll proxy loaded");
    QM_LOG_INFO("  - HostExe : %s", hostExe);
    QM_LOG_INFO("  - SelfPath: %s", selfPath);
    QM_LOG_INFO("  - PID     : %lu, TID: %lu", GetCurrentProcessId(), GetCurrentThreadId());
#ifdef QM_BUILD_PRODUCTION
    QM_LOG_INFO("  - Build   : production (log-level=%d diag=%d)", QM_LOG_LEVEL, QM_DIAG);
#else
    QM_LOG_INFO("  - Build   : dev (log-level=%d diag=%d)", QM_LOG_LEVEL, QM_DIAG);
#endif
}

// ============================================================================
// 4. MinHook proof-of-life - hook kernel32!Sleep, log a few hits then go silent.
// ============================================================================
typedef VOID(WINAPI* Sleep_t)(DWORD);
static Sleep_t g_origSleep = NULL;
static volatile LONG g_sleepCallCount = 0;

static VOID WINAPI TestHook_Sleep(DWORD dwMilliseconds)
{
    LONG n = InterlockedIncrement(&g_sleepCallCount);
    if (n <= 5)
    {
        QM_LOG_DEBUG("[MinHook] Sleep(%lu) called - hit #%ld (TID: %lu)",
            dwMilliseconds, n, GetCurrentThreadId());
    }
    else if (n == 6)
    {
        QM_LOG_DEBUG("[MinHook] Sleep hook proven, going silent (further calls not logged)");
    }
    g_origSleep(dwMilliseconds);
}

// ============================================================================
// 5. Shared layout helpers + small UObject probes
// ============================================================================
//
// FR5BuildingItemRuntimeData @ UR5BuildingItemWidget+0x340 (Dumper-7 SDK):
//   0x000  TSoftObjectPtr<IR5BuildingItemInterface> ItemInterface (0x28 bytes)
//          0x000  FWeakObjectPtr WeakPtr           (8 bytes: idx + serial)
//          0x008  FSoftObjectPath ObjectID
//                 0x008  FTopLevelAssetPath AssetPath
//                        0x008  FName PackageName  (8 bytes)
//                        0x010  FName AssetName    (8 bytes)
//                 0x018  FUtf8String SubPathString (16 bytes)
//   0x028  bool bIsSelected
//   0x029  bool bIsFocused
//   0x02A  bool bIsNew
struct ItemDataLayout
{
    static constexpr size_t kItemData    = 0x340;
    static constexpr size_t kWeakPtr     = kItemData + 0x00;  // FWeakObjectPtr (8B)
    static constexpr size_t kPackageName = kItemData + 0x08;  // FName (8B)
    static constexpr size_t kAssetName   = kItemData + 0x10;  // FName (8B)
    static constexpr size_t kSubPathData = kItemData + 0x18;  // char* (UTF-8)
    static constexpr size_t kSubPathNum  = kItemData + 0x20;
    static constexpr size_t kSubPathMax  = kItemData + 0x24;
    static constexpr size_t kBIsSelected = kItemData + 0x28;
    static constexpr size_t kBIsFocused  = kItemData + 0x29;
    static constexpr size_t kBIsNew      = kItemData + 0x2A;
    static constexpr size_t kSize        = 0x30;   // full ItemData struct
};

// UR5BuildingGroupWidget::BuildingItems @ +0x350 (Dumper-7 R5_classes.hpp).
static constexpr size_t kBuildingItemsOffset = 0x350;

// Best-effort UObject->Class->Name resolver, SEH-guarded. On any failure
// writes an empty string.
static void TryResolveContextClassName(QmUE::UObject* ctx, char* out, int outCap)
{
    if (out && outCap > 0) out[0] = '\0';
    if (!ctx || !out || outCap <= 0) return;
    __try
    {
        if (!ctx->Class) return;
        QmUE::ResolveFNameNarrow(ctx->Class->Name, out, outCap);
    }
    __except (EXCEPTION_EXECUTE_HANDLER) { out[0] = '\0'; }
}

// Read a TArray header from `at`. Returns 0 on success, -1 on SEH fault.
static int SafeReadTArrayHeader(void* at, QmUE::FTArrayHeader* out)
{
    if (!at || !out) return -1;
    __try { *out = *reinterpret_cast<QmUE::FTArrayHeader*>(at); return 0; }
    __except (EXCEPTION_EXECUTE_HANDLER) { return -1; }
}

// ============================================================================
// 6. Read-only diagnostic inspectors - QM_DIAG-gated, never mutates state.
// ============================================================================
#if QM_DIAG

// Resolve the WeakObjectPtr's ObjectIndex via GObjects. Returns nullptr if
// the soft-ref hasn't been hydrated yet or the index is out of bounds.
static QmUE::UObject* DiagResolveWeakRef(int32_t objectIndex)
{
    if (objectIndex <= 0) return nullptr;
    if (!QmUE::IsReady()) return nullptr;
    QmUE::TUObjectArray* arr = QmUE::GetGObjects();
    if (objectIndex >= arr->Num()) return nullptr;
    return arr->GetByIndex(objectIndex);
}

// Inspect the params block of GetBuildingGroupsByCategoryTag (CategoryTag +
// SelectedBrush). All SEH-guarded - the call path may pack params differently.
static void DiagInspectInputs(void* Result)
{
    if (!Result) { QM_LOG_DEBUG("[Inspect]   inputs: Result is null - skipping"); return; }

    uint8_t* paramsBase = reinterpret_cast<uint8_t*>(Result) - 0x10;

    QmUE::FGameplayTag tag = {};
    bool tagRead = false;
    __try { tag = *reinterpret_cast<QmUE::FGameplayTag*>(paramsBase + 0x00); tagRead = true; }
    __except (EXCEPTION_EXECUTE_HANDLER) {}

    char tagName[256] = "<no-tag>";
    if (tagRead)
    {
        if (!QmUE::ResolveFNameNarrow(tag, tagName, sizeof(tagName)))
            snprintf(tagName, sizeof(tagName), "<unresolved cmp=%d num=%u>", tag.ComparisonIndex, tag.Number);
    }
    else
        snprintf(tagName, sizeof(tagName), "<FAULT reading params@0x%p>", paramsBase);

    QmUE::UObject* brush = nullptr;
    bool brushRead = false;
    __try { brush = *reinterpret_cast<QmUE::UObject**>(paramsBase + 0x08); brushRead = true; }
    __except (EXCEPTION_EXECUTE_HANDLER) {}

    char brushCls[128] = "";
    char brushName[128] = "";
    if (brushRead && brush)
    {
        TryResolveContextClassName(brush, brushCls, sizeof(brushCls));
        __try { QmUE::ResolveFNameNarrow(brush->Name, brushName, sizeof(brushName)); }
        __except (EXCEPTION_EXECUTE_HANDLER) {}
    }

    QM_LOG_DEBUG("[Inspect]   in: CategoryTag='%s' SelectedBrush=0x%p Cls='%s' Name='%s'",
        tagName, brush, brushCls[0] ? brushCls : (brush ? "<?>" : "null"),
        brushName[0] ? brushName : (brush ? "<?>" : ""));
}

// Dump the Groups TArray returned by GetBuildingGroupsByCategoryTag. With
// deep=true also dumps each Group's BuildingItems TArray header.
static void DiagInspectGroupResult(void* Result, bool deep)
{
    if (!Result) { QM_LOG_DEBUG("[Inspect]   Result=null"); return; }

    QmUE::FTArrayHeader hdr = {};
    if (SafeReadTArrayHeader(Result, &hdr) != 0)
    {
        QM_LOG_WARN("[Inspect]   FAULT reading TArray header at Result=0x%p", Result);
        return;
    }

    QM_LOG_DEBUG("[Inspect]   ReturnValue TArray Data=0x%p Num=%d Max=%d", hdr.Data, hdr.Num, hdr.Max);
    if (!deep || !hdr.Data || hdr.Num <= 0) return;

    int dumpN = hdr.Num;
    if (dumpN > 40) { QM_LOG_DEBUG("[Inspect]   (clamping enumeration to first 40 of %d)", hdr.Num); dumpN = 40; }

    QmUE::UObject** widgets = reinterpret_cast<QmUE::UObject**>(hdr.Data);
    char clsName[128];
    char selfName[128];
    for (int i = 0; i < dumpN; ++i)
    {
        QmUE::UObject* w = nullptr;
        __try { w = widgets[i]; }
        __except (EXCEPTION_EXECUTE_HANDLER) { QM_LOG_WARN("[Inspect]   [%d] FAULT", i); continue; }
        if (!w) { QM_LOG_DEBUG("[Inspect]   [%d] null", i); continue; }

        clsName[0] = '\0';
        selfName[0] = '\0';
        TryResolveContextClassName(w, clsName, sizeof(clsName));
        __try { QmUE::ResolveFNameNarrow(w->Name, selfName, sizeof(selfName)); }
        __except (EXCEPTION_EXECUTE_HANDLER) {}

        int itemsNum = -1, itemsMax = -1;
        void* itemsData = nullptr;
        __try
        {
            QmUE::FTArrayHeader* items = reinterpret_cast<QmUE::FTArrayHeader*>(
                reinterpret_cast<uint8_t*>(w) + kBuildingItemsOffset);
            itemsData = items->Data; itemsNum = items->Num; itemsMax = items->Max;
        }
        __except (EXCEPTION_EXECUTE_HANDLER) { itemsNum = -2; }

        QM_LOG_DEBUG("[Inspect]   [%d] Widget=0x%p Cls='%s' Name='%s' Items={Data=0x%p Num=%d Max=%d}",
            i, w, clsName[0] ? clsName : "<?>", selfName[0] ? selfName : "<?>",
            itemsData, itemsNum, itemsMax);
    }
}

// Inspect one ItemWidget's ItemData (SoftObjectPath + bools + WeakPtr).
static void DiagInspectOneItemWidget(QmUE::UObject* item, int groupIdx, int itemIdx)
{
    if (!item) { QM_LOG_DEBUG("[SoftPath] [G%d.I%d] item=null", groupIdx, itemIdx); return; }

    uint8_t* w = reinterpret_cast<uint8_t*>(item);

    char widgetCls[128] = "";
    char widgetName[128] = "";
    TryResolveContextClassName(item, widgetCls, sizeof(widgetCls));
    __try { QmUE::ResolveFNameNarrow(item->Name, widgetName, sizeof(widgetName)); }
    __except (EXCEPTION_EXECUTE_HANDLER) {}

    int32_t weakIdx = 0, weakSerial = 0;
    QmUE::FName pkgName = {}, assetName = {};
    char* subData = nullptr; int32_t subNum = 0, subMax = 0;
    uint8_t bSelected = 0, bFocused = 0, bNew = 0;
    bool readOK = true;

    __try
    {
        weakIdx    = *reinterpret_cast<int32_t*>(w + ItemDataLayout::kWeakPtr);
        weakSerial = *reinterpret_cast<int32_t*>(w + ItemDataLayout::kWeakPtr + 4);
        pkgName    = *reinterpret_cast<QmUE::FName*>(w + ItemDataLayout::kPackageName);
        assetName  = *reinterpret_cast<QmUE::FName*>(w + ItemDataLayout::kAssetName);
        subData    = *reinterpret_cast<char**>(w + ItemDataLayout::kSubPathData);
        subNum     = *reinterpret_cast<int32_t*>(w + ItemDataLayout::kSubPathNum);
        subMax     = *reinterpret_cast<int32_t*>(w + ItemDataLayout::kSubPathMax);
        bSelected  = *reinterpret_cast<uint8_t*>(w + ItemDataLayout::kBIsSelected);
        bFocused   = *reinterpret_cast<uint8_t*>(w + ItemDataLayout::kBIsFocused);
        bNew       = *reinterpret_cast<uint8_t*>(w + ItemDataLayout::kBIsNew);
    }
    __except (EXCEPTION_EXECUTE_HANDLER) { readOK = false; }
    (void)subMax;

    if (!readOK)
    {
        QM_LOG_WARN("[SoftPath] [G%d.I%d] widget=0x%p Cls='%s' Name='%s' <FAULT reading ItemData @ +0x340>",
            groupIdx, itemIdx, item,
            widgetCls[0] ? widgetCls : "<?>", widgetName[0] ? widgetName : "<?>");
        return;
    }

    char pkgStr[256] = "<unresolved>";
    char assetStr[256] = "<unresolved>";
    if (!pkgName.IsNone())
    {
        if (!QmUE::ResolveFNameNarrow(pkgName, pkgStr, sizeof(pkgStr)))
            snprintf(pkgStr, sizeof(pkgStr), "<unresolved cmp=%d num=%u>", pkgName.ComparisonIndex, pkgName.Number);
    }
    else strcpy(pkgStr, "<None>");
    if (!assetName.IsNone())
    {
        if (!QmUE::ResolveFNameNarrow(assetName, assetStr, sizeof(assetStr)))
            snprintf(assetStr, sizeof(assetStr), "<unresolved cmp=%d num=%u>", assetName.ComparisonIndex, assetName.Number);
    }
    else strcpy(assetStr, "<None>");

    char subStr[256] = "";
    bool subReadOK = true;
    if (subData && subNum > 0)
    {
        int copy = subNum;
        if (copy > (int)sizeof(subStr) - 1) copy = (int)sizeof(subStr) - 1;
        __try { memcpy(subStr, subData, copy); subStr[copy] = '\0'; }
        __except (EXCEPTION_EXECUTE_HANDLER) { subReadOK = false; }
    }

    QmUE::UObject* hydrated = DiagResolveWeakRef(weakIdx);
    char hydratedCls[128] = "";
    char hydratedName[128] = "";
    if (hydrated)
    {
        TryResolveContextClassName(hydrated, hydratedCls, sizeof(hydratedCls));
        __try { QmUE::ResolveFNameNarrow(hydrated->Name, hydratedName, sizeof(hydratedName)); }
        __except (EXCEPTION_EXECUTE_HANDLER) {}
    }

    QM_LOG_DEBUG("[SoftPath] [G%d.I%d] widget=0x%p Cls='%s' Name='%s'",
        groupIdx, itemIdx, item,
        widgetCls[0] ? widgetCls : "<?>", widgetName[0] ? widgetName : "<?>");
    QM_LOG_DEBUG("[SoftPath]   Pkg='%s' Asset='%s'%s%s",
        pkgStr, assetStr,
        (subNum > 0) ? " SubPath='" : "",
        (subNum > 0) ? (subReadOK ? subStr : "<FAULT>") : "");
    QM_LOG_DEBUG("[SoftPath]   WeakPtr={idx=%d serial=%d} hydrated=0x%p Cls='%s' Name='%s' flags={sel=%u focus=%u new=%u}",
        weakIdx, weakSerial, hydrated,
        hydrated ? (hydratedCls[0] ? hydratedCls : "<?>") : "",
        hydrated ? (hydratedName[0] ? hydratedName : "<?>") : "",
        bSelected, bFocused, bNew);
}

// Top-level recon: dump SoftPath info for the first 3 items in Groups[0].
static void DiagInspectFirstGroupSoftPaths(void* Result)
{
    if (!Result) return;

    QmUE::FTArrayHeader grpHdr = {};
    if (SafeReadTArrayHeader(Result, &grpHdr) != 0)
    { QM_LOG_WARN("[SoftPath] FAULT reading group TArray"); return; }

    if (!grpHdr.Data || grpHdr.Num < 1)
    { QM_LOG_DEBUG("[SoftPath] no groups to inspect (Num=%d)", grpHdr.Num); return; }

    QmUE::UObject* group0 = nullptr;
    __try { group0 = reinterpret_cast<QmUE::UObject**>(grpHdr.Data)[0]; }
    __except (EXCEPTION_EXECUTE_HANDLER) { QM_LOG_WARN("[SoftPath] FAULT reading group[0] pointer"); return; }
    if (!group0) { QM_LOG_DEBUG("[SoftPath] group[0]=null"); return; }

    QmUE::FTArrayHeader itemsHdr = {};
    if (SafeReadTArrayHeader(reinterpret_cast<uint8_t*>(group0) + kBuildingItemsOffset, &itemsHdr) != 0)
    { QM_LOG_WARN("[SoftPath] FAULT reading items TArray @ group+0x350"); return; }

    QM_LOG_DEBUG("[SoftPath] Group=0x%p Items={Data=0x%p Num=%d Max=%d} - inspecting up to 3 items",
        group0, itemsHdr.Data, itemsHdr.Num, itemsHdr.Max);
    if (!itemsHdr.Data || itemsHdr.Num < 1) return;

    int dumpN = itemsHdr.Num > 3 ? 3 : itemsHdr.Num;
    for (int i = 0; i < dumpN; ++i)
    {
        QmUE::UObject* item = nullptr;
        __try { item = reinterpret_cast<QmUE::UObject**>(itemsHdr.Data)[i]; }
        __except (EXCEPTION_EXECUTE_HANDLER) { QM_LOG_WARN("[SoftPath] [G0.I%d] FAULT reading slot", i); continue; }
        DiagInspectOneItemWidget(item, 0, i);
    }
}

// Walk GObjects for any UFunction matching `funcName`. Used during probe to
// confirm the function exists somewhere even if our Children walk misses it.
static int DiagFindUFunctionsByName(const char* funcName, int maxLog)
{
    using namespace QmUE;
    if (!IsReady()) return 0;

    TUObjectArray* arr = GetGObjects();
    const int32 total = arr->Num();
    int hits = 0;
    char nameBuf[256]; char outerBuf[256];

    for (int32 i = 0; i < total; ++i)
    {
        UObject* obj = arr->GetByIndex(i);
        if (!obj || !obj->Class) continue;
        if ((obj->Class->CastFlags & CASTFLAG_Function) == 0) continue;
        if (!ResolveFNameNarrow(obj->Name, nameBuf, sizeof(nameBuf))) continue;
        if (strcmp(nameBuf, funcName) != 0) continue;

        hits++;
        if (hits <= maxLog)
        {
            UFunction* fn = reinterpret_cast<UFunction*>(obj);
            const char* outerName = "<no-outer>";
            if (obj->Outer && ResolveFNameNarrow(obj->Outer->Name, outerBuf, sizeof(outerBuf)))
                outerName = outerBuf;
            QM_LOG_DEBUG("[UE]   diag hit #%d UFunction '%s' @ 0x%p (idx=%d) Outer='%s' ExecFn=0x%p Flags=0x%08X",
                hits, nameBuf, fn, obj->Index, outerName, (void*)fn->ExecFunction, fn->FunctionFlags);
        }
    }
    return hits;
}

// Dump raw bytes around 0x40 of a UClass for layout verification.
static void DiagDumpClassBytes(QmUE::UClass* cls, const char* label)
{
    if (!cls) return;
    const uint8_t* p = reinterpret_cast<const uint8_t*>(cls);
    char hex[256]; int n = 0;
    for (int i = 0; i < 0x20; ++i)
        n += snprintf(hex + n, sizeof(hex) - n, "%02X ", p[0x40 + i]);
    QM_LOG_DEBUG("[UE] %s raw[0x40..0x5F]: %s", label, hex);
    void* superStruct = *reinterpret_cast<void* const*>(p + 0x40);
    void* children    = *reinterpret_cast<void* const*>(p + 0x48);
    void* childProps  = *reinterpret_cast<void* const*>(p + 0x50);
    QM_LOG_DEBUG("[UE] %s   Super@0x40 = 0x%p  Children@0x48 = 0x%p  ChildProps@0x50 = 0x%p",
        label, superStruct, children, childProps);
}

#endif // QM_DIAG

// ============================================================================
// 7. Inject pipeline - capture donor, spawn own widget, override SoftPath,
//    fan-out into every group in the result.
//
// State machine:
//   - g_donorItem == null: first hit captures Groups[0].Items[0] as donor and
//     records its source group (skip-list anchor).
//   - g_mySpawnedItem == null + donor != null: spawn a fresh widget via
//     UGameplayStatics::SpawnObject and memcpy ItemData from donor.
//   - g_overrideTarget.resolved == false: try to FName-construct the
//     QmBedrl PackageName/AssetName via KismetStringLibrary::Conv_StringToName.
//     Retry every hit until it sticks (handles late-mounted paks).
//   - g_overrideApplied == 0: once the target is resolved AND we have a
//     spawned widget, rewrite its ItemData FNames + zero the WeakPtr so the
//     IoStore SoftRef resolver re-hydrates to our path.
//   - Every hit: fan out to every group in Result (except donor's home),
//     append the spawned widget pointer to BuildingItems unless already there.
// ============================================================================

// ---- Override target (kept in struct so we can pass it around if needed) --
struct OverrideTarget
{
    bool         resolved;       // true once FNames are constructed
    QmUE::FName  assetName;      // 'DA_BI_QmBedrl_01'
    QmUE::FName  packageName;    // '/Game/Gameplay/Building/BuildingDecoration/DA_BI_QmBedrl_01'
    QmUE::UObject* targetObj;    // unused in Strategy A, kept for forward compat
};

static constexpr const wchar_t* kOverridePackagePathW = L"/Game/Gameplay/Building/BuildingDecoration/DA_BI_QmBedrl_01";
static constexpr const wchar_t* kOverrideAssetNameW   = L"DA_BI_QmBedrl_01";
static constexpr const char*    kOverrideAssetName    = "DA_BI_QmBedrl_01";   // log only
static constexpr const char*    kOverrideClassName    = "R5BuildingItem";     // log only

// ---- Inject pipeline state -------------------------------------------------
static void*           g_donorItem            = nullptr;     // first vanilla item seen
static void*           g_donorSourceGroup     = nullptr;     // its owner group (skip on inject)
static char            g_donorAssetName[128]  = "<?>";

static QmUE::UObject*  g_mySpawnedItem        = nullptr;     // our own widget
static QmUE::UClass*   g_itemWidgetClass      = nullptr;     // donor->Class
static volatile LONG   g_spawnAttempts        = 0;
static volatile LONG   g_spawnSuccesses       = 0;

static OverrideTarget  g_overrideTarget       = {};
static volatile LONG   g_overrideLookupAttempts = 0;
static volatile LONG   g_overrideApplied      = 0;

static volatile LONG   g_foreignInjectsDone   = 0;
static volatile LONG   g_foreignAlreadyPresent = 0;
static volatile LONG   g_foreignSkippedCategory = 0;

// Returns true if `needle` already appears anywhere in items[0..num).
// SEH-guarded; on fault returns false so we don't double-inject blindly.
static bool IsPointerInItemsArray(void* itemsData, int num, void* needle)
{
    if (!itemsData || num <= 0 || !needle) return false;
    __try
    {
        void** arr = reinterpret_cast<void**>(itemsData);
        for (int i = 0; i < num; ++i)
            if (arr[i] == needle) return true;
    }
    __except (EXCEPTION_EXECUTE_HANDLER) { return false; }
    return false;
}

// Construct the override target's FNames at runtime via Conv_StringToName.
// Returns true on first success (cached thereafter). Failures get sparse log.
static bool ResolveOverrideTarget()
{
    if (g_overrideTarget.resolved) return true;

    InterlockedIncrement(&g_overrideLookupAttempts);

    QmUE::FName pkgName = {0, 0};
    QmUE::FName assetName = {0, 0};

    if (!QmUE::FNameFromString(kOverridePackagePathW, &pkgName))
    {
        if (g_overrideLookupAttempts == 1 || (g_overrideLookupAttempts % 5) == 0)
            QM_LOG_WARN("[Override] FNameFromString FAILED for Pkg='%ls' (attempt#%ld) - using donor-clone fallback",
                kOverridePackagePathW, g_overrideLookupAttempts);
        return false;
    }
    if (!QmUE::FNameFromString(kOverrideAssetNameW, &assetName))
    {
        if (g_overrideLookupAttempts == 1 || (g_overrideLookupAttempts % 5) == 0)
            QM_LOG_WARN("[Override] FNameFromString FAILED for Asset='%ls' (attempt#%ld) - using donor-clone fallback",
                kOverrideAssetNameW, g_overrideLookupAttempts);
        return false;
    }

    g_overrideTarget.resolved    = true;
    g_overrideTarget.assetName   = assetName;
    g_overrideTarget.packageName = pkgName;
    g_overrideTarget.targetObj   = nullptr;

    // Round-trip-verify so the log shows what the FName pool actually interned.
    char pkgBuf[256] = "<?>";
    char assetBuf[128] = "<?>";
    QmUE::ResolveFNameNarrow(pkgName,   pkgBuf,   sizeof(pkgBuf));
    QmUE::ResolveFNameNarrow(assetName, assetBuf, sizeof(assetBuf));
    QM_LOG_INFO("[Override] *** RESOLVED *** target='%s' via FName-from-String Pkg='%s' (cmp=%d num=%u) Asset='%s' (cmp=%d num=%u)",
        kOverrideAssetName,
        pkgBuf,   pkgName.ComparisonIndex,   pkgName.Number,
        assetBuf, assetName.ComparisonIndex, assetName.Number);
    return true;
}

// Apply the resolved override to a spawned widget. Returns true on success.
static bool ApplyOverrideToSpawned(QmUE::UObject* spawned)
{
    if (!spawned || !g_overrideTarget.resolved) return false;

    bool ok = false;
    __try
    {
        uint8_t* base = reinterpret_cast<uint8_t*>(spawned);
        *reinterpret_cast<QmUE::FName*>(base + ItemDataLayout::kPackageName) = g_overrideTarget.packageName;
        *reinterpret_cast<QmUE::FName*>(base + ItemDataLayout::kAssetName)   = g_overrideTarget.assetName;
        *reinterpret_cast<int32_t*>(base + ItemDataLayout::kWeakPtr)         = 0;
        *reinterpret_cast<int32_t*>(base + ItemDataLayout::kWeakPtr + 4)     = 0;
        ok = true;
    }
    __except (EXCEPTION_EXECUTE_HANDLER) { ok = false; }

    if (ok)
    {
        InterlockedIncrement(&g_overrideApplied);
        QM_LOG_INFO("[Override] APPLIED to spawned=0x%p (Pkg+Asset rewritten, WeakPtr zeroed) -> SoftRef will re-resolve", spawned);
    }
    else
        QM_LOG_WARN("[Override] FAULT during write to spawned=0x%p", spawned);
    return ok;
}

// Spawn the own widget on first call. Idempotent. Returns nullptr on failure
// (caller falls back to donor-clone visuals).
static QmUE::UObject* EnsureSpawnedItem(QmUE::UObject* donor)
{
    if (g_mySpawnedItem) return g_mySpawnedItem;
    if (!donor) return nullptr;

    QmUE::UClass* itemCls = nullptr;
    QmUE::UObject* outer = nullptr;
    __try { itemCls = donor->Class; outer = donor->Outer; }
    __except (EXCEPTION_EXECUTE_HANDLER) { itemCls = nullptr; outer = nullptr; }

    if (!itemCls)
    {
        QM_LOG_WARN("[Spawn] FAULT reading donor->Class for donor=0x%p - cannot spawn", donor);
        return nullptr;
    }

    char itemClsName[128] = "<?>";
    __try { QmUE::ResolveFNameNarrow(itemCls->Name, itemClsName, sizeof(itemClsName)); }
    __except (EXCEPTION_EXECUTE_HANDLER) {}

    InterlockedIncrement(&g_spawnAttempts);
    g_itemWidgetClass = itemCls;

    QmUE::UObject* spawned = QmUE::SpawnObjectViaUFunction(itemCls, outer);
    if (!spawned)
    {
        QM_LOG_WARN("[Spawn] SpawnObjectViaUFunction returned null (Cls=%s @0x%p outer=0x%p)",
            itemClsName, itemCls, outer);
        return nullptr;
    }

    bool copyOK = false;
    __try
    {
        uint8_t* src = reinterpret_cast<uint8_t*>(donor)   + ItemDataLayout::kItemData;
        uint8_t* dst = reinterpret_cast<uint8_t*>(spawned) + ItemDataLayout::kItemData;
        memcpy(dst, src, ItemDataLayout::kSize);
        copyOK = true;
    }
    __except (EXCEPTION_EXECUTE_HANDLER) { copyOK = false; }

    if (!copyOK)
        QM_LOG_WARN("[Spawn] FAULT during ItemData memcpy donor=0x%p -> spawned=0x%p", donor, spawned);

    g_mySpawnedItem = spawned;
    InterlockedIncrement(&g_spawnSuccesses);
    QM_LOG_INFO("[Spawn] *** SUCCESS *** %s spawned @ 0x%p (outer=0x%p) ItemData=%s",
        itemClsName, spawned, outer, copyOK ? "cloned-from-donor" : "FAULT");

    if (ResolveOverrideTarget())
        ApplyOverrideToSpawned(spawned);

    return spawned;
}

// Apply override later if it wasn't ready when we spawned. Cheap if applied.
static void MaybeRetryOverride()
{
    if (!g_mySpawnedItem) return;
    if (g_overrideApplied > 0) return;
    if (!ResolveOverrideTarget()) return;
    ApplyOverrideToSpawned(g_mySpawnedItem);
}

// Currently-best widget pointer to inject (spawned preferred; donor fallback).
static void* GetInjectTarget()
{
    return g_mySpawnedItem ? static_cast<void*>(g_mySpawnedItem) : g_donorItem;
}

// ---- Category targeting (Plan B - identify groups by first item's path) ---
//
// Strategy: Each group's Items[0] points to a UR5BuildingItemWidget whose
// ItemData.PackageName is something like
//   /Game/Gameplay/Building/BuildingDecoration/DA_BI_Bedroll_01
//   /Game/Gameplay/Building/BuildingItems/DA_BI_BasementT01_BBB_A
//   /Game/Gameplay/Building/BuildingUtilities/DA_BI_Utilities_BuildingCenterT01
// The middle segment ("BuildingDecoration", "BuildingItems", "BuildingUtilities")
// classifies the group. We pick a substring to match against -- groups whose
// first item's package contains it are considered injection targets.
//
// QmBedrl is a bedroll => Decoration group, so we filter on "BuildingDecoration".
// Set kTargetGroupPathSubstring to nullptr to disable filtering (legacy fan-out).
static constexpr const char* kTargetGroupPathSubstring = "BuildingDecoration";

// Probe report - filled by ProbeGroupCategory.
struct GroupCategoryProbe
{
    void* firstItem;       // Items[0] widget pointer (or null on empty)
    char  pkgName[256];    // resolved package path string ("" on fault)
    char  tagName[128];    // hydrated UR5BuildingItem::BuildingItemTag string ("" if unhydrated/fault)
    bool  hasItems;        // true if BuildingItems TArray had at least one entry
    bool  pkgValid;        // true if pkgName resolved to a non-empty string
};

// FGameplayTag is a 1-FName struct - offset of BuildingItemTag on UR5BuildingItem
// (Dumper-7 R5_classes.hpp: UR5BuildingItem @ 0x0048).
static constexpr size_t kBuildingItemTagOffset = 0x48;

// FWeakObjectPtr packs {int32 ObjectIndex, int32 ObjectSerialNumber}. Used to
// hydrate the soft-ref to the actual UR5BuildingItem data asset.
static QmUE::UObject* ResolveWeakObjectPtr(int32_t objectIndex)
{
    if (objectIndex <= 0) return nullptr;
    if (!QmUE::IsReady()) return nullptr;
    QmUE::TUObjectArray* arr = QmUE::GetGObjects();
    if (objectIndex >= arr->Num()) return nullptr;
    return arr->GetByIndex(objectIndex);
}

// Read group's first item, resolve its package name and (if hydrated) its
// building tag. SEH-guarded; on fault leaves out->* in safe-empty state.
static void ProbeGroupCategory(void* group, GroupCategoryProbe* out)
{
    if (!out) return;
    out->firstItem = nullptr;
    out->pkgName[0] = '\0';
    out->tagName[0] = '\0';
    out->hasItems = false;
    out->pkgValid = false;
    if (!group) return;

    QmUE::FTArrayHeader itemsHdr = {};
    if (SafeReadTArrayHeader(
        reinterpret_cast<uint8_t*>(group) + kBuildingItemsOffset, &itemsHdr) != 0) return;
    if (!itemsHdr.Data || itemsHdr.Num < 1) return;
    out->hasItems = true;

    void* firstItem = nullptr;
    QmUE::FName pkgName = {};
    int32_t weakIdx = 0;
    __try
    {
        firstItem = reinterpret_cast<void**>(itemsHdr.Data)[0];
        if (firstItem)
        {
            uint8_t* w = reinterpret_cast<uint8_t*>(firstItem);
            pkgName = *reinterpret_cast<QmUE::FName*>(w + ItemDataLayout::kPackageName);
            weakIdx = *reinterpret_cast<int32_t*>(w + ItemDataLayout::kWeakPtr);
        }
    }
    __except (EXCEPTION_EXECUTE_HANDLER) { return; }
    if (!firstItem) return;
    out->firstItem = firstItem;

    if (!pkgName.IsNone())
    {
        if (QmUE::ResolveFNameNarrow(pkgName, out->pkgName, sizeof(out->pkgName))
            && out->pkgName[0])
            out->pkgValid = true;
    }

    // Try to hydrate the BuildingItemTag if the soft-ref is already resolved.
    if (weakIdx > 0)
    {
        QmUE::UObject* hydrated = ResolveWeakObjectPtr(weakIdx);
        if (hydrated)
        {
            __try
            {
                QmUE::FName tag = *reinterpret_cast<QmUE::FName*>(
                    reinterpret_cast<uint8_t*>(hydrated) + kBuildingItemTagOffset);
                if (!tag.IsNone())
                    QmUE::ResolveFNameNarrow(tag, out->tagName, sizeof(out->tagName));
            }
            __except (EXCEPTION_EXECUTE_HANDLER) { out->tagName[0] = '\0'; }
        }
    }
}

// Return true if this group's category matches our target (or filter disabled).
static bool GroupMatchesTargetCategory(const GroupCategoryProbe& probe)
{
    if (!kTargetGroupPathSubstring) return true;   // disabled => match all
    if (!probe.pkgValid) return false;             // no path => can't classify
    return strstr(probe.pkgName, kTargetGroupPathSubstring) != nullptr;
}

// Per-group injection outcome - filled by InjectIntoGroup, consumed by logging.
struct ForeignInjectReport
{
    void* targetGroup;
    void* donorItem;
    int oldNum;
    int newNum;
    int max;
    const char* status;    // "captured", "injected", "already-present",
                           // "skipped-same-group", "skipped-no-slack",
                           // "skipped-empty", "skipped-no-target",
                           // "skipped-category", nullptr if FAULT
};

struct ForeignFanoutReport
{
    int total;
    int injected;
    int skipped;
    int faulted;
};

// Inject the current target into a single group's BuildingItems array.
// Returns 0 on success, -1 on skip (status reason in out->status), -2 on SEH.
static int InjectIntoGroup(void* group, ForeignInjectReport* out)
{
    if (out) { out->targetGroup = group; out->donorItem = nullptr;
               out->oldNum = out->newNum = out->max = -1; out->status = nullptr; }
    if (!group) { if (out) out->status = "skipped-empty"; return -1; }

    void* injectTarget = GetInjectTarget();
    if (out) out->donorItem = injectTarget;
    if (!injectTarget) { if (out) out->status = "skipped-no-target"; return -1; }

    // Don't list the target in its own donor group (would render duplicated
    // right next to its original slot).
    if (group == g_donorSourceGroup) { if (out) out->status = "skipped-same-group"; return -1; }

    // Category-targeting (Plan B): only inject into groups whose first item's
    // package path matches our target category. Skip everything else.
    if (kTargetGroupPathSubstring)
    {
        GroupCategoryProbe probe = {};
        ProbeGroupCategory(group, &probe);
        if (!GroupMatchesTargetCategory(probe))
        {
            InterlockedIncrement(&g_foreignSkippedCategory);
            if (out) out->status = "skipped-category";
            return -1;
        }
    }

    QmUE::FTArrayHeader* itemsArr = reinterpret_cast<QmUE::FTArrayHeader*>(
        reinterpret_cast<uint8_t*>(group) + kBuildingItemsOffset);

    QmUE::FTArrayHeader itemsHdr = {};
    if (SafeReadTArrayHeader(itemsArr, &itemsHdr) != 0) return -2;
    if (out) { out->oldNum = itemsHdr.Num; out->max = itemsHdr.Max; }

    if (IsPointerInItemsArray(itemsHdr.Data, itemsHdr.Num, injectTarget))
    {
        InterlockedIncrement(&g_foreignAlreadyPresent);
        if (out) out->status = "already-present";
        return -1;
    }


    if (!itemsHdr.Data)              { if (out) out->status = "skipped-empty";    return -1; }
    if (itemsHdr.Num >= itemsHdr.Max){ if (out) out->status = "skipped-no-slack"; return -1; }

    __try
    {
        reinterpret_cast<void**>(itemsHdr.Data)[itemsHdr.Num] = injectTarget;
        itemsArr->Num = itemsHdr.Num + 1;
    }
    __except (EXCEPTION_EXECUTE_HANDLER) { return -2; }

    InterlockedIncrement(&g_foreignInjectsDone);
    if (out) { out->newNum = itemsHdr.Num + 1; out->status = "injected"; }
    return 0;
}

// Top-level per-hit step: capture-or-inject. Fills `out` with the primary
// outcome (first inject's details, or "captured" if this was the capture hit)
// and `fanout` with totals across every group in Result.
static int CaptureOrInjectForeignItem(void* Result, ForeignInjectReport* out,
                                      ForeignFanoutReport* fanout)
{
    if (out) { out->targetGroup = nullptr; out->donorItem = nullptr;
               out->oldNum = out->newNum = out->max = -1; out->status = nullptr; }
    if (fanout) { fanout->total = fanout->injected = fanout->skipped = fanout->faulted = 0; }
    if (!Result) { if (out) out->status = "skipped-empty"; return -1; }

    QmUE::FTArrayHeader grpHdr = {};
    if (SafeReadTArrayHeader(Result, &grpHdr) != 0) return -2;
    if (!grpHdr.Data || grpHdr.Num < 1) { if (out) out->status = "skipped-empty"; return -1; }

    void* group0 = nullptr;
    __try { group0 = reinterpret_cast<void**>(grpHdr.Data)[0]; }
    __except (EXCEPTION_EXECUTE_HANDLER) { return -2; }
    if (!group0) { if (out) out->status = "skipped-empty"; return -1; }
    if (out) out->targetGroup = group0;

    // ----- Capture phase ---------------------------------------------------
    bool capturedThisCall = false;
    if (!g_donorItem)
    {
        QmUE::FTArrayHeader itemsHdr = {};
        if (SafeReadTArrayHeader(
            reinterpret_cast<uint8_t*>(group0) + kBuildingItemsOffset, &itemsHdr) != 0)
            return -2;
        if (out) { out->oldNum = itemsHdr.Num; out->max = itemsHdr.Max; }
        if (!itemsHdr.Data || itemsHdr.Num < 1) { if (out) out->status = "skipped-empty"; return -1; }

        void* firstItem = nullptr;
        QmUE::FName assetName = {};
        __try
        {
            firstItem = reinterpret_cast<void**>(itemsHdr.Data)[0];
            if (firstItem)
                assetName = *reinterpret_cast<QmUE::FName*>(
                    reinterpret_cast<uint8_t*>(firstItem) + ItemDataLayout::kAssetName);
        }
        __except (EXCEPTION_EXECUTE_HANDLER) { return -2; }
        if (!firstItem) { if (out) out->status = "skipped-empty"; return -1; }

        g_donorItem = firstItem;
        g_donorSourceGroup = group0;
        if (!assetName.IsNone())
        {
            if (!QmUE::ResolveFNameNarrow(assetName, g_donorAssetName, sizeof(g_donorAssetName)))
                snprintf(g_donorAssetName, sizeof(g_donorAssetName), "<unresolved cmp=%d num=%u>",
                    assetName.ComparisonIndex, assetName.Number);
        }
        else strcpy(g_donorAssetName, "<None>");

        EnsureSpawnedItem(reinterpret_cast<QmUE::UObject*>(firstItem));

        if (out) { out->donorItem = firstItem; out->status = "captured"; }
        capturedThisCall = true;
    }

    // ----- Inject phase: walk groups, inject into the FIRST matching one ---
    // Stop after the first successful inject per hit - we want the donor to
    // appear exactly once, not once per matching decoration subgroup.
    if (fanout)
    {
        for (int g = 0; g < grpHdr.Num; ++g)
        {
            void* gp = nullptr;
            __try { gp = reinterpret_cast<void**>(grpHdr.Data)[g]; }
            __except (EXCEPTION_EXECUTE_HANDLER) { fanout->faulted++; continue; }
            if (!gp) { fanout->skipped++; continue; }

            ForeignInjectReport sub = {};
            int rc = InjectIntoGroup(gp, &sub);
            fanout->total++;
            if (rc == 0)       fanout->injected++;
            else if (rc == -2) fanout->faulted++;
            else               fanout->skipped++;

            if (rc == 0)
            {
                if (out && (out->status == nullptr ||
                    strcmp(out->status, "captured") != 0))
                {
                    *out = sub;
                }
                break; // single-inject policy: one slot per hit, done
            }
        }
    }

    return capturedThisCall ? 0 : (fanout && fanout->injected > 0 ? 0 : -1);
}

// ============================================================================
// 8. UFunction detour - GetBuildingGroupsByCategoryTag.
//    Forwards to original, then runs capture+inject pipeline.
// ============================================================================
static QmUE::FNativeFuncPtr g_origGetBuildingGroups = nullptr;
static volatile LONG g_getBuildingGroupsHits = 0;

static void __fastcall Hook_GetBuildingGroupsByCategoryTag(void* Context, void* Stack, void* Result)
{
    LONG n = InterlockedIncrement(&g_getBuildingGroupsHits);

    // Logging policy:
    //   hits 1..3  : everything (header + inputs + diag + soft-paths + each item)
    //   hits 4..10 : header + per-hit inject
    //   later      : every 200th hit a ping
    const bool logHeader = (n <= 10) || (n % 200 == 0);
#if QM_DIAG
    const bool logDeep   = (n <= 3);
#endif

    if (logHeader)
    {
        char ctxCls[128] = { 0 };
        TryResolveContextClassName(reinterpret_cast<QmUE::UObject*>(Context), ctxCls, sizeof(ctxCls));
        QM_LOG_DEBUG("[Hook] GetBuildingGroupsByCategoryTag hit #%ld TID=%lu Ctx=0x%p Cls='%s' Stack=0x%p Result=0x%p",
            n, GetCurrentThreadId(), Context, ctxCls[0] ? ctxCls : "<?>", Stack, Result);
#if QM_DIAG
        DiagInspectInputs(Result);
#endif
    }

    // Forward to original.
    if (g_origGetBuildingGroups)
    {
        __try { g_origGetBuildingGroups(Context, Stack, Result); }
        __except (EXCEPTION_EXECUTE_HANDLER)
        {
            if (logHeader)
                QM_LOG_ERROR("[Hook] *** EXCEPTION inside original GetBuildingGroupsByCategoryTag ***");
        }
    }

    // Mutations on the freshly-populated Result happen below. Order matters:
    //   1. MaybeRetryOverride()  - fix late-loaded pak case (cheap if applied)
    //   2. CaptureOrInjectForeignItem - capture donor once, then fan-out inject
    //   3. (diag) deep result dump + soft-path recon
    MaybeRetryOverride();

    // Plan B - log a per-group category probe for the first few hits so we can
    // see which group's first-item path maps to which target classification.
    if (logHeader)
    {
        QmUE::FTArrayHeader grpHdr = {};
        if (SafeReadTArrayHeader(Result, &grpHdr) == 0 && grpHdr.Data && grpHdr.Num > 0)
        {
            for (int g = 0; g < grpHdr.Num; ++g)
            {
                void* gp = nullptr;
                __try { gp = reinterpret_cast<void**>(grpHdr.Data)[g]; }
                __except (EXCEPTION_EXECUTE_HANDLER) { continue; }
                if (!gp) continue;
                GroupCategoryProbe probe = {};
                ProbeGroupCategory(gp, &probe);
                const bool match = GroupMatchesTargetCategory(probe);
                QM_LOG_DEBUG("[Group] hit#%ld G%d=0x%p match=%d pkg='%s' tag='%s'",
                    n, g, gp, match ? 1 : 0,
                    probe.pkgValid ? probe.pkgName : "<unresolved>",
                    probe.tagName[0] ? probe.tagName : "<unhydrated>");
            }
        }
    }

    ForeignInjectReport fi = {};
    ForeignFanoutReport ff = {};
    int fiRc = CaptureOrInjectForeignItem(Result, &fi, &ff);

    if (fiRc == -2)
    {
        if (logHeader)
            QM_LOG_WARN("[Foreign] hit#%ld FAULT during capture-or-inject", n);
    }
    else if (fi.status && strcmp(fi.status, "captured") == 0)
    {
        QM_LOG_INFO("[Foreign] hit#%ld CAPTURED donor item=0x%p Asset='%s' from sourceGroup=0x%p",
            n, fi.donorItem, g_donorAssetName, g_donorSourceGroup);
        if (ff.total > 0)
            QM_LOG_INFO("[Foreign] hit#%ld FANOUT injected=%d skipped=%d faulted=%d (target=0x%p) - donor visible from first menu open",
                n, ff.injected, ff.skipped, ff.faulted, GetInjectTarget());
    }
    else if (fi.status && strcmp(fi.status, "injected") == 0)
    {
        if (logHeader)
        {
            QM_LOG_DEBUG("[Foreign] hit#%ld INJECTED donor=0x%p Asset='%s' -> targetGroup=0x%p slot[%d], Items.Num: %d -> %d (Max=%d) [total=%ld]",
                n, fi.donorItem, g_donorAssetName, fi.targetGroup, fi.newNum - 1,
                fi.oldNum, fi.newNum, fi.max, g_foreignInjectsDone);
        }
        else if (g_foreignInjectsDone <= 50 || g_foreignInjectsDone % 25 == 0)
        {
            QM_LOG_TRACE("[Foreign] hit#%ld inject#%ld -> targetGroup=0x%p Items %d->%d",
                n, g_foreignInjectsDone, fi.targetGroup, fi.oldNum, fi.newNum);
        }
    }
    else if (fi.status && strcmp(fi.status, "already-present") == 0)
    {
        if (g_foreignAlreadyPresent <= 5 || logHeader)
            QM_LOG_TRACE("[Foreign] hit#%ld already-present (donor in Items, skip) targetGroup=0x%p Items.Num=%d [skips=%ld]",
                n, fi.targetGroup, fi.oldNum, g_foreignAlreadyPresent);
    }
    else if (fi.status && (n <= 12 || (n % 100 == 0)))
    {
        QM_LOG_TRACE("[Foreign] hit#%ld %s targetGroup=0x%p Items.Num=%d Max=%d",
            n, fi.status, fi.targetGroup, fi.oldNum, fi.max);
    }

#if QM_DIAG
    if (logHeader)
        DiagInspectGroupResult(Result, logDeep);
    if (n <= 3)
        DiagInspectFirstGroupSoftPaths(Result);
#endif

    if (n == 1)
    {
        QM_LOG_INFO("[Hook] *** PHASE 2a SUCCESS *** GetBuildingGroupsByCategoryTag is reachable from our detour");
        QM_LOG_INFO("[Hook] active - spawn widget + Conv_StringToName SoftPath to '%s::%s' (fallback to donor-clone if FNameFromString fails)",
            kOverrideClassName, kOverrideAssetName);
        QM_LOG_INFO("[Hook] target-category-filter: %s (groups whose first item's package path lacks the substring are skipped)",
            kTargetGroupPathSubstring ? kTargetGroupPathSubstring : "<disabled - inject into every group>");
        QM_LOG_INFO("[Hook] inject-policy: single-shot (first matching group per hit only - donor appears exactly once per category open)");
    }

    if (n == 1 || (n % 50 == 0))
    {
        QM_LOG_DEBUG("[Spawn] state: spawned=0x%p (attempts=%ld successes=%ld) donor=0x%p target=0x%p override={resolved=%d applied=%ld attempts=%ld} cat-skips=%ld",
            g_mySpawnedItem, g_spawnAttempts, g_spawnSuccesses, g_donorItem, GetInjectTarget(),
            g_overrideTarget.resolved ? 1 : 0, g_overrideApplied, g_overrideLookupAttempts, g_foreignSkippedCategory);
    }
}

static bool g_groupsHookInstalled = false;
static bool InstallGetBuildingGroupsHook(QmUE::UFunction* target)
{
    if (g_groupsHookInstalled) return true;
    if (!target || !target->ExecFunction)
    {
        QM_LOG_ERROR("[Hook] cannot install - target or ExecFunction is null");
        return false;
    }

    LPVOID execAddr = reinterpret_cast<LPVOID>(target->ExecFunction);
    MH_STATUS st = MH_CreateHook(execAddr,
        reinterpret_cast<LPVOID>(&Hook_GetBuildingGroupsByCategoryTag),
        reinterpret_cast<LPVOID*>(&g_origGetBuildingGroups));
    if (st != MH_OK)
    {
        QM_LOG_ERROR("[Hook] MH_CreateHook(GetBuildingGroupsByCategoryTag @ 0x%p) FAILED: %s",
            execAddr, MH_StatusToString(st));
        return false;
    }

    st = MH_EnableHook(execAddr);
    if (st != MH_OK)
    {
        QM_LOG_ERROR("[Hook] MH_EnableHook(GetBuildingGroupsByCategoryTag @ 0x%p) FAILED: %s",
            execAddr, MH_StatusToString(st));
        return false;
    }

    g_groupsHookInstalled = true;
    QM_LOG_INFO("[Hook] *** INSTALLED *** GetBuildingGroupsByCategoryTag ExecFn=0x%p detour=0x%p trampoline=0x%p",
        execAddr, (void*)&Hook_GetBuildingGroupsByCategoryTag, (void*)g_origGetBuildingGroups);
    QM_LOG_INFO("[Hook] Now open Build mode (B-key) to trigger the function and verify the hook fires");
    return true;
}

// ---- UE probe loop --------------------------------------------------------
// Wait for GObjects to populate, then find R5HFSM_BuildingPanel and walk its
// Children for GetBuildingGroupsByCategoryTag. Install the hook on success.
static bool UE_ProbePass(int passNumber)
{
    using namespace QmUE;

    UClass* panelClass = FindClassByName("R5HFSM_BuildingPanel");
    if (!panelClass)
    {
        QM_LOG_DEBUG("[UE] probe#%d R5HFSM_BuildingPanel NOT FOUND in GObjects", passNumber);
        return false;
    }

    int totalFields = 0;
    int funcCount = 0;
    UField* field = panelClass->Children;
    while (field)
    {
        totalFields++;
        if (field->Class && (field->Class->CastFlags & CASTFLAG_Function) != 0)
            funcCount++;
        field = field->Next;
    }

    UFunction* target = FindFunctionOnClass(panelClass, "GetBuildingGroupsByCategoryTag");

    QM_LOG_INFO("[UE] probe#%d panelClass=0x%p (idx=%d) Children=0x%p fields=%d funcs=%d target=%s",
        passNumber, panelClass, panelClass->Index,
        (void*)panelClass->Children, totalFields, funcCount,
        target ? "FOUND" : "missing");

    if (!target) return false;

    QM_LOG_INFO("[UE] *** GO *** UFunction GetBuildingGroupsByCategoryTag = 0x%p ExecFn=0x%p Flags=0x%08X",
        target, (void*)target->ExecFunction, target->FunctionFlags);

#if QM_DIAG
    // List all the panel's UFunctions - good for spotting newly-added
    // BlueprintCallable functions after game updates.
    char nameBuf[256];
    field = panelClass->Children;
    int idx = 0;
    while (field)
    {
        if (field->Class && (field->Class->CastFlags & CASTFLAG_Function) != 0)
        {
            if (ResolveFNameNarrow(field->Name, nameBuf, sizeof(nameBuf)))
            {
                UFunction* fn = reinterpret_cast<UFunction*>(field);
                QM_LOG_DEBUG("[UE]   fn[%d] = '%s' ExecFn=0x%p Flags=0x%08X",
                    idx++, nameBuf, (void*)fn->ExecFunction, fn->FunctionFlags);
            }
        }
        field = field->Next;
    }
#endif

    InstallGetBuildingGroupsHook(target);

    // Verify GameplayStatics/SpawnObject chain. Item-class lookup is lazy
    // (resolved from donor->Class at hit#1) because WBP_Building_Item_C only
    // gets registered after the player enters build mode.
    QmUE::UClass*    gsCls = QmUE::FindClassByName("GameplayStatics");
    QmUE::UFunction* sof   = gsCls ? QmUE::FindFunctionOnClass(gsCls, "SpawnObject") : nullptr;
    QmUE::UObject*   gsCDO = QmUE::GetClassDefaultObject(gsCls);

    QM_LOG_INFO("[Spawn] probe: GameplayStatics=0x%p SpawnObject=0x%p CDO=0x%p (item-class resolved lazily from donor at hit#1)",
        gsCls, sof, gsCDO);

    if (gsCls && sof && gsCDO)
        QM_LOG_INFO("[Spawn] *** READY *** SpawnObject UFunction reachable - spawn will fire on first donor capture");
    else
        QM_LOG_WARN("[Spawn] *** NOT READY *** SpawnObject UFunction unavailable - inject will use donor-fallback");

    return true;
}

static DWORD WINAPI UeProbeThread(LPVOID /*lpParam*/)
{
    QM_LOG_INFO("[UE] ProbeThread start (TID: %lu)", GetCurrentThreadId());

    HMODULE exeMod = GetModuleHandleA(NULL);
    QM_LOG_INFO("[UE] EXE base = 0x%p", exeMod);
    QM_LOG_DEBUG("[UE] expected GObjects @ 0x%p",     (void*)((uintptr_t)exeMod + QmUE::OFFSET_GObjects));
    QM_LOG_DEBUG("[UE] expected AppendString @ 0x%p", (void*)((uintptr_t)exeMod + QmUE::OFFSET_AppendString));

    // Phase 1: wait until GObjects is allocated and reasonably populated.
    // 100k threshold guarantees native class registration has finished.
    const int kInitMaxAttempts = 300;     // 300 * 500ms = 2.5 min
    int lastReported = 0;
    bool initOK = false;
    int initAttempts = 0;
    for (int attempt = 0; attempt < kInitMaxAttempts; ++attempt)
    {
        if (QmUE::Init())
        {
            QmUE::TUObjectArray* arr = QmUE::GetGObjects();
            int n = arr->Num();
            if (n >= lastReported + 50000 || (!initOK && n > 0))
            {
                QM_LOG_DEBUG("[UE] init progress attempt#%d GObjects.Num=%d NumChunks=%d",
                    attempt + 1, n, arr->NumChunks);
                lastReported = n;
            }
            if (n > 100000) { initOK = true; initAttempts = attempt + 1; break; }
        }
        Sleep(500);
    }

    if (!initOK)
    {
        QM_LOG_ERROR("[UE] init NEVER reached 100000 objects - aborting probe");
        return 1;
    }

    QmUE::TUObjectArray* arr = QmUE::GetGObjects();
    QM_LOG_INFO("[UE] init reached threshold on attempt#%d - GObjects.Num=%d", initAttempts, arr->Num());

    // Phase 2: probe loop. Try every 2s until we find the function or time out.
    const int kProbeMaxAttempts = 150;    // 150 * 2s = 5 min
#if QM_DIAG
    bool firstPass = true;
#endif
    bool found = false;
    for (int p = 0; p < kProbeMaxAttempts; ++p)
    {
        if (UE_ProbePass(p + 1)) { found = true; break; }
#if QM_DIAG
        if (firstPass)
        {
            firstPass = false;
            QmUE::UClass* panelClass = QmUE::FindClassByName("R5HFSM_BuildingPanel");
            if (panelClass) DiagDumpClassBytes(panelClass, "R5HFSM_BuildingPanel");
            int hits = DiagFindUFunctionsByName("GetBuildingGroupsByCategoryTag", 5);
            QM_LOG_DEBUG("[UE] diag: %d UFunction(s) named 'GetBuildingGroupsByCategoryTag' in GObjects", hits);
        }
#endif
        Sleep(2000);
    }

    if (!found)
    {
        QM_LOG_ERROR("[UE] *** TIMEOUT *** GetBuildingGroupsByCategoryTag never found via Children walk");
#if QM_DIAG
        int hits = DiagFindUFunctionsByName("GetBuildingGroupsByCategoryTag", 5);
        QM_LOG_DEBUG("[UE] final diag: %d direct-name UFunction hits in GObjects", hits);
#endif
    }
    return found ? 0 : 1;
}

// ============================================================================
// 9. Worker thread + DllMain
// ============================================================================
static DWORD WINAPI WorkerThread(LPVOID /*lpParam*/)
{
    QM_LOG_INFO("[MinHook] WorkerThread start (TID: %lu)", GetCurrentThreadId());

    MH_STATUS st = MH_Initialize();
    if (st != MH_OK)
    {
        QM_LOG_ERROR("[MinHook] MH_Initialize FAILED: %s", MH_StatusToString(st));
        return 1;
    }
    QM_LOG_INFO("[MinHook] MH_Initialize OK");

    st = MH_CreateHookApi(L"kernel32", "Sleep",
        (LPVOID)&TestHook_Sleep, (LPVOID*)&g_origSleep);
    if (st != MH_OK)
    {
        QM_LOG_ERROR("[MinHook] MH_CreateHookApi(Sleep) FAILED: %s", MH_StatusToString(st));
        return 2;
    }

    st = MH_EnableHook(MH_ALL_HOOKS);
    if (st != MH_OK)
    {
        QM_LOG_ERROR("[MinHook] MH_EnableHook FAILED: %s", MH_StatusToString(st));
        return 3;
    }

    QM_LOG_INFO("[MinHook] Sleep hook installed and enabled - waiting for first hit");

    HANDLE hUeProbe = CreateThread(NULL, 0, UeProbeThread, NULL, 0, NULL);
    if (hUeProbe) CloseHandle(hUeProbe);
    else QM_LOG_ERROR("[UE] CreateThread(UeProbeThread) FAILED: gle=%lu", GetLastError());
    return 0;
}

BOOL APIENTRY DllMain(HMODULE hModule, DWORD ul_reason_for_call, LPVOID /*lpReserved*/)
{
    switch (ul_reason_for_call)
    {
    case DLL_PROCESS_ATTACH:
    {
        DisableThreadLibraryCalls(hModule);

        InitializeCriticalSection(&g_logLock);
        g_logLockInit = TRUE;

        WriteInjectMarker(hModule);

        HANDLE hThread = CreateThread(NULL, 0, WorkerThread, NULL, 0, NULL);
        if (hThread) CloseHandle(hThread);
        else QM_LOG_ERROR("[MinHook] CreateThread FAILED (GetLastError=%lu)", GetLastError());
        break;
    }
    case DLL_PROCESS_DETACH:
        // Intentionally no MH_Uninitialize - process teardown can deadlock the loader.
        break;
    }
    return TRUE;
}
