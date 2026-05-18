// Quartermaster dxgi.dll Proxy + MinHook Bootstrap
// =================================================
// Lifecycle:
//   1. PE forwarders make us a drop-in dxgi.dll (real DXGI = dxgi_org.dll).
//   2. DllMain process-attach -> QmLogInit() + WriteInjectMarker() + spawn
//      WorkerThread.
//   3. WorkerThread installs crash diagnostics, brings up MinHook with a
//      Sleep test hook (proof-of-life), then spawns the UE probe thread.
//   4. UE probe (qm_hook) waits for GObjects, finds R5HFSM_BuildingPanel +
//      GetBuildingGroupsByCategoryTag, installs the detour. Detour runs the
//      inject pipeline (qm_inject) on every build-menu open.
//
// File layout (post-refactor):
//   main.cpp     - this file. DLL plumbing, marker, Sleep test, worker thread.
//   qm_log.*     - file-backed logger + level macros (QM_LOG_INFO/...).
//   qm_state.hpp - ItemDataLayout, kBuildingItemsOffset, tiny SEH helpers.
//   qm_ue.*      - hand-rolled UE5 reflection (FName, UObject, UFunction).
//   qm_scan.*    - runtime offset auto-discovery (validation-based scan).
//   qm_crash.*   - VEH + UEF crash snapshot + Quartermaster-state dump.
//   qm_inject.*  - capture donor + per-inject fresh-widget pipeline.
//   qm_diag.*    - read-only inspectors (compiled out in production).
//   qm_hook.*    - UFunction detour install + UE probe loop.

#define _CRT_SECURE_NO_WARNINGS
#include <windows.h>
#include <stdio.h>

#include "minhook/include/MinHook.h"
#include "qm_log.hpp"
#include "qm_crash.hpp"
#include "qm_hook.hpp"

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
// 2. Inject marker - first log lines so the user knows the proxy attached.
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
// 3. MinHook proof-of-life - hook kernel32!Sleep, log a few hits then go silent.
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
// 4. Worker thread - crash handler, MinHook init, Sleep test, probe spawn.
// ============================================================================
static DWORD WINAPI WorkerThread(LPVOID /*lpParam*/)
{
    QM_LOG_INFO("[MinHook] WorkerThread start (TID: %lu)", GetCurrentThreadId());

    // Crash diagnostics first so any subsequent failure gets captured.
    QmCrashInstallHandler();

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

    HANDLE hUeProbe = CreateThread(NULL, 0, QmUeProbeThreadEntry, NULL, 0, NULL);
    if (hUeProbe) CloseHandle(hUeProbe);
    else QM_LOG_ERROR("[UE] CreateThread(UeProbeThread) FAILED: gle=%lu", GetLastError());
    return 0;
}

// ============================================================================
// 5. DllMain.
// ============================================================================
BOOL APIENTRY DllMain(HMODULE hModule, DWORD ul_reason_for_call, LPVOID /*lpReserved*/)
{
    switch (ul_reason_for_call)
    {
    case DLL_PROCESS_ATTACH:
    {
        DisableThreadLibraryCalls(hModule);

        QmLogInit();
        WriteInjectMarker(hModule);

        HANDLE hThread = CreateThread(NULL, 0, WorkerThread, NULL, 0, NULL);
        if (hThread) CloseHandle(hThread);
        else QM_LOG_ERROR("[MinHook] CreateThread FAILED (GetLastError=%lu)", GetLastError());
        break;
    }
    case DLL_PROCESS_DETACH:
        // Intentionally no MH_Uninitialize - process teardown can deadlock the loader.
        // QmLogShutdown is also skipped: late threads could log during DLL unload.
        break;
    }
    return TRUE;
}
