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
// Resolution policy: best-effort. The 19 exports we expose are the full set
// the Microsoft DXGI implements, but other vendors (notably Wine-builtin
// dxgi.dll, used on the dedicated server under Linux + Wine) only export a
// subset (CreateDXGIFactory{,1,2}, DXGIGetDebugInterface1, ...). The PIX
// debugger entry points, the AppCompat shims, and the D3D10-Layered Device
// API have no counterpart there. Rather than aborting process load for the
// entire game session, any export GetProcAddress can't find is routed to
// QmDxgiUnresolvedStub() - a single tail-return-E_NOTIMPL function that
// honours the x64 ABI for HRESULT-returning calls. If something the host
// actually depends on hits the stub it'll log and the caller sees a clean
// failure HRESULT instead of an AV in our trampolines. Only an outright
// failure to load the system DLL is still treated as fatal.

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

// Substituted for any export the system dxgi.dll doesn't provide. x64 ABI:
// integer/HRESULT return values are passed in RAX, args in RCX/RDX/R8/R9 +
// stack, caller cleans the stack. A function returning a single 64-bit value
// is therefore safe to call with any signature - the caller just sees an
// E_NOTIMPL return (or treats it as a non-zero "something went wrong" if it
// expected void/BOOL/pointer). Logs once per distinct call-site name so a
// hot path doesn't flood the log.
static LONG g_stubHitCount = 0;
extern "C" __int64 QmDxgiUnresolvedStub()
{
    LONG n = InterlockedIncrement(&g_stubHitCount);
    if (n <= 8) {
        QM_LOG_WARN("[Passthrough] unresolved dxgi export hit (call #%ld) - returning E_NOTIMPL", n);
    } else if (n == 9) {
        QM_LOG_WARN("[Passthrough] further unresolved-export hits suppressed");
    }
    return 0x80004001LL; // E_NOTIMPL
}

#define QM_RESOLVE(name) do { \
    g_real_##name = (void*)GetProcAddress(h, #name); \
    if (!g_real_##name) { \
        QM_LOG_WARN("[Passthrough] export not provided by host dxgi.dll, stubbed: " #name); \
        g_real_##name = (void*)&QmDxgiUnresolvedStub; \
        ++stubbed; \
    } else { \
        ++resolved; \
    } \
} while (0)

static void ResolveAllExports(HMODULE h, int* outResolved, int* outStubbed)
{
    int resolved = 0;
    int stubbed = 0;
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
    if (outResolved) *outResolved = resolved;
    if (outStubbed) *outStubbed = stubbed;
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
    int resolved = 0;
    int stubbed = 0;
    ResolveAllExports(h, &resolved, &stubbed);
    QM_LOG_INFO("[Passthrough] %d export(s) resolved, %d stubbed (E_NOTIMPL on call)",
        resolved, stubbed);
    return true;
}
