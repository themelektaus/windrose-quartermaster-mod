// Quartermaster dxgi.dll Proxy + MinHook Bootstrap
// =================================================
// Lifecycle:
//   1. Game maps our dxgi.dll. The 19 dxgi exports we declare (see
//      passthrough.def + passthrough_asm.asm) are 1-instruction MASM
//      trampolines that tail-jump through a g_real_* pointer table.
//   2. DllMain process-attach -> QmLogInit() + WriteInjectMarker() +
//      ResolveSystemDxgi() to populate the pointer table from the system's
//      real dxgi.dll (copied to %TEMP% so the loader treats it as a fresh
//      PE identity instead of dedup'ing against our own handle). Any export
//      the host's dxgi.dll doesn't provide (Wine-builtin is missing PIX +
//      D3D10-Layered + Compat shims) is routed to QmDxgiUnresolvedStub
//      which returns E_NOTIMPL instead of crashing on a nullptr-jmp.
//      Only an outright failure to load the system DLL aborts process load.
//   3. Spawn WorkerThread. Installs crash diagnostics, brings up MinHook
//      with a Sleep test hook (proof-of-life), then spawns the UE probe
//      thread.
//   4. UE probe (qm_hook) waits for GObjects, finds R5HFSM_BuildingPanel +
//      GetBuildingGroupsByCategoryTag, installs the detour. Detour runs the
//      inject pipeline (qm_inject) on every build-menu open.
//
// File layout (post-refactor):
//   main.cpp           - this file. DLL plumbing, marker, Sleep test, worker.
//   passthrough.cpp    - g_real_* pointer table + ResolveSystemDxgi().
//   passthrough_asm.asm- MASM jmp-trampolines, one per dxgi export.
//   passthrough.def    - PE export table (19 names, ordinals 1-19).
//   qm_log.*           - file-backed logger + level macros (QM_LOG_INFO/...).
//   qm_state.hpp       - ItemDataLayout, kBuildingItemsOffset, SEH helpers.
//   qm_ue.*            - hand-rolled UE5 reflection (FName, UObject, ...).
//   qm_scan.*          - runtime offset auto-discovery.
//   qm_crash.*         - VEH + UEF crash snapshot + state dump.
//   qm_inject.*        - capture donor + per-inject fresh-widget pipeline.
//   qm_diag.*          - read-only inspectors (compiled out in production).
//   qm_hook.*          - UFunction detour install + UE probe loop.

#define _CRT_SECURE_NO_WARNINGS
#include <windows.h>
#include <stdio.h>

#include "minhook/include/MinHook.h"
#include "qm_log.hpp"
#include "qm_crash.hpp"
#include "qm_hook.hpp"
#include "qm_config.hpp"
#include "qm_weather.hpp"

// Implemented in passthrough.cpp - resolves real dxgi.dll into the g_real_*
// table that the MASM trampolines in passthrough_asm.asm jmp through.
extern "C" bool ResolveSystemDxgi(HMODULE hSelf);

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

    // Load injectable-item list by scanning qm_items_*.json next to this DLL.
    // The GUI writes one file per deployed profile on "Build" / "Deploy"; no
    // matching files = no injects (DLL stays idle, no harm). Done off DllMain
    // to keep Loader-Lock clean.
    QmConfigLoad();

    // Weather PoC (Option B): sentinel-driven CheatWeatherID writer. Init here
    // so a weather-only deploy (no injectable items) still keeps the DLL active
    // - the heartbeat rides the lifecycle hook installed by the UE probe below.
    const bool weatherEnabled = QmWeather_Init();

    // Self-disable mode: when no JSON files matched or zero items merged, this DLL
    // is along for the ride (e.g. profile has only custom items / recipes -
    // those don't need injection). Skip MinHook + UE probe entirely so we have
    // zero per-frame overhead and zero crash surface. Re-loading requires a
    // game restart anyway (Build button replaces the pak too). The weather
    // sentinel overrides idle: weather needs the UE probe + lifecycle hook live.
    if (g_injectableItemCount == 0 && !weatherEnabled)
    {
        QM_LOG_INFO("[Config] no injectable items and no weather sentinel - DLL goes idle (no MinHook, no UE probe)");
        return 0;
    }

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

        // Populate the g_real_* pointer table from the system's real dxgi.dll
        // BEFORE we let the game call any of our exports. Stubs every missing
        // export internally; the only path that returns false here is an
        // outright failure to locate / load a system dxgi.dll at all - which
        // means we'd tail-jump through a nullptr stub array, so abort cleanly.
        if (!ResolveSystemDxgi(hModule))
        {
            QM_LOG_ERROR("[Passthrough] aborting process load: no system dxgi.dll loadable");
            return FALSE;
        }

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
