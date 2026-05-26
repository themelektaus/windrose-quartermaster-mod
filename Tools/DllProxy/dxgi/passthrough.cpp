// Quartermaster dxgi.dll Passthrough Resolver
// -------------------------------------------
// Replaces the old PE-forwarder-to-dxgi_original.dll mechanism. We no longer
// need a renamed copy of the system DXGI staged alongside us at deploy time;
// the proxy locates the real implementation at runtime, in DllMain, and
// populates the g_real_* pointer table that the MASM trampolines in
// passthrough_asm.asm tail-jump through.
//
// Called from DllMain BEFORE the worker thread spawns. Single strategy:
//
//   Temp-copy of the system dxgi.dll. Windows' loader deduplicates DLLs by
//   basename, so a direct LoadLibraryExW("C:\\Windows\\System32\\dxgi.dll")
//   returns our own handle. We copy the system file to a uniquely-named
//   temp file and LoadLibrary that copy - the renamed file is a fresh PE
//   identity for the loader, no dedup.
//
// On failure the function returns false and DllMain returns FALSE so the
// process aborts cleanly rather than launching and crashing on the first
// DXGI call.

#define _CRT_SECURE_NO_WARNINGS
#include <windows.h>
#include <stdio.h>
#include "qm_log.hpp"

// ---------------------------------------------------------------------------
// Pointer table populated by ResolveSystemDxgi. MASM trampolines in
// passthrough_asm.asm reference these symbols via EXTRN.
// extern "C" + x64 = no name mangling and no leading underscore, so the
// linker matches the names verbatim.
// ---------------------------------------------------------------------------
extern "C" {
void* g_real_ApplyCompatResolutionQuirking = nullptr;
void* g_real_CompatString = nullptr;
void* g_real_CompatValue = nullptr;
void* g_real_DXGIDumpJournal = nullptr;
void* g_real_PIXBeginCapture = nullptr;
void* g_real_PIXEndCapture = nullptr;
void* g_real_PIXGetCaptureState = nullptr;
void* g_real_SetAppCompatStringPointer = nullptr;
void* g_real_UpdateHMDEmulationStatus = nullptr;
void* g_real_CreateDXGIFactory = nullptr;
void* g_real_CreateDXGIFactory1 = nullptr;
void* g_real_CreateDXGIFactory2 = nullptr;
void* g_real_DXGID3D10CreateDevice = nullptr;
void* g_real_DXGID3D10CreateLayeredDevice = nullptr;
void* g_real_DXGID3D10GetLayeredDeviceSize = nullptr;
void* g_real_DXGID3D10RegisterLayers = nullptr;
void* g_real_DXGIDeclareAdapterRemovalSupport = nullptr;
void* g_real_DXGIGetDebugInterface1 = nullptr;
void* g_real_DXGIReportAdapterConfiguration = nullptr;
}

#define QM_RESOLVE(name) do { \
    g_real_##name = (void*)GetProcAddress(h, #name); \
    if (!g_real_##name) { \
        QM_LOG_ERROR("[Passthrough] missing export: " #name); \
        ++missing; \
    } \
} while (0)

static int ResolveAllExports(HMODULE h)
{
    int missing = 0;
    QM_RESOLVE(ApplyCompatResolutionQuirking);
    QM_RESOLVE(CompatString);
    QM_RESOLVE(CompatValue);
    QM_RESOLVE(DXGIDumpJournal);
    QM_RESOLVE(PIXBeginCapture);
    QM_RESOLVE(PIXEndCapture);
    QM_RESOLVE(PIXGetCaptureState);
    QM_RESOLVE(SetAppCompatStringPointer);
    QM_RESOLVE(UpdateHMDEmulationStatus);
    QM_RESOLVE(CreateDXGIFactory);
    QM_RESOLVE(CreateDXGIFactory1);
    QM_RESOLVE(CreateDXGIFactory2);
    QM_RESOLVE(DXGID3D10CreateDevice);
    QM_RESOLVE(DXGID3D10CreateLayeredDevice);
    QM_RESOLVE(DXGID3D10GetLayeredDeviceSize);
    QM_RESOLVE(DXGID3D10RegisterLayers);
    QM_RESOLVE(DXGIDeclareAdapterRemovalSupport);
    QM_RESOLVE(DXGIGetDebugInterface1);
    QM_RESOLVE(DXGIReportAdapterConfiguration);
    return missing;
}

#undef QM_RESOLVE

// Copies the system dxgi.dll to %TEMP%\qm_dxgi_passthrough.dll and loads
// that copy. The renamed file presents a fresh PE identity to the loader,
// so basename-dedup doesn't return our own handle. Always tries a fresh
// copy first; falls back to reusing an existing one if a previous instance
// left it locked.
static HMODULE LoadFreshSystemDxgi(HMODULE hSelf)
{
    wchar_t sysDxgi[MAX_PATH];
    UINT n = GetSystemDirectoryW(sysDxgi, MAX_PATH);
    if (n == 0 || n >= MAX_PATH - 16) {
        QM_LOG_WARN("[Passthrough] GetSystemDirectoryW failed (n=%u, gle=%lu)",
            n, GetLastError());
        return NULL;
    }
    wcscat_s(sysDxgi, MAX_PATH, L"\\dxgi.dll");

    if (GetFileAttributesW(sysDxgi) == INVALID_FILE_ATTRIBUTES) {
        QM_LOG_WARN("[Passthrough] system dxgi.dll not found at %ls (gle=%lu)",
            sysDxgi, GetLastError());
        return NULL;
    }

    wchar_t tempDir[MAX_PATH];
    if (GetTempPathW(MAX_PATH, tempDir) == 0) {
        QM_LOG_WARN("[Passthrough] GetTempPathW failed (gle=%lu)", GetLastError());
        return NULL;
    }
    wchar_t tempPath[MAX_PATH];
    swprintf_s(tempPath, MAX_PATH, L"%sqm_dxgi_passthrough.dll", tempDir);

    if (!CopyFileW(sysDxgi, tempPath, FALSE /* fail if exists */)) {
        DWORD err = GetLastError();
        if (err == ERROR_FILE_EXISTS) {
            QM_LOG_INFO("[Passthrough] temp copy already present, reusing: %ls",
                tempPath);
        } else {
            QM_LOG_WARN("[Passthrough] CopyFile %ls -> %ls failed (gle=%lu)",
                sysDxgi, tempPath, err);
            return NULL;
        }
    } else {
        QM_LOG_INFO("[Passthrough] copied system dxgi -> %ls", tempPath);
    }

    HMODULE h = LoadLibraryW(tempPath);
    if (!h) {
        QM_LOG_WARN("[Passthrough] LoadLibrary(%ls) failed (gle=%lu)",
            tempPath, GetLastError());
        return NULL;
    }
    if (h == hSelf) {
        // Should not happen with a uniquely-named copy, but treat defensively.
        QM_LOG_WARN("[Passthrough] temp-copy load returned own handle (basename collision)");
        FreeLibrary(h);
        return NULL;
    }
    return h;
}

extern "C" bool ResolveSystemDxgi(HMODULE hSelf)
{
    HMODULE h = LoadFreshSystemDxgi(hSelf);
    if (!h) {
        QM_LOG_ERROR("[Passthrough] FATAL: cannot load system dxgi.dll via temp-copy");
        return false;
    }
    int missing = ResolveAllExports(h);
    if (missing > 0) {
        QM_LOG_ERROR("[Passthrough] %d export(s) unresolved - process abort", missing);
        return false;
    }
    QM_LOG_INFO("[Passthrough] 19 exports resolved");
    return true;
}
