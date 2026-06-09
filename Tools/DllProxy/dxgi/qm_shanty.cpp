// Quartermaster "Always Shanties" - keep the crew shanty playing after you leave the helm.
// ---------------------------------------------------------------------------------------
// Vanilla: at the helm, B starts/stops the shanty; LEAVING the helm stops it. This module
// prevents ONLY the helm-leave stop - B start/stop at the helm stays vanilla, nothing more.
//
// How it tells a helm-leave apart from a manual B-stop (RECON-confirmed, see the table in
// the chat / PLAN doc). All three signals dispatch through the global ProcessEvent net-hook:
//   - the helm B toggle fires  InpActEvt_IA_ShipToggleShanty*  on the helm UI manager,
//     immediately (<100ms) BEFORE the resulting Enable/Disable on the audio component;
//   - a manual B-stop  =  ServerDisableShanty  closely PRECEDED by that toggle input;
//   - a helm-leave      =  ServerDisableShanty  with NO recent toggle input.
//
// So the implementation is offset-free: remember the tick of the last toggle input; an
// Enable (start) or a manual-stop Disable CONSUMES it. A ServerDisableShanty that finds no
// fresh (unconsumed) toggle input, while the component is the one we saw start, is a
// helm-leave -> we tell the net-hook NOT to forward the original ProcessEvent, so the
// disable never runs and the shanty keeps playing. No property reads, no re-play.
//
// Trigger (opt-in via a sentinel next to dxgi.dll; no sentinel = zero cost - the module is
// not even consulted from the net-hook):
//   qm_shanty*.txt : arms the keep-alive (qm_shanty.txt for manual/dev use, or the
//                    profile-bound qm_shanty_<profile>.txt the Configurator deploys).

#define _CRT_SECURE_NO_WARNINGS
#include <windows.h>
#include <stdint.h>
#include <stdio.h>
#include <string.h>

#include "qm_shanty.hpp"
#include "qm_ue.hpp"
#include "qm_log.hpp"

namespace
{
    bool g_initDone = false;
    bool g_armed    = false;

    // ---- helm-leave keep-alive state (game-thread only; net-hook is in-thread) -------
    // Tick of the most recent helm toggle input that has NOT yet been consumed by an
    // Enable / manual-stop Disable. 0 = none pending. A helm-leave never finds one fresh.
    ULONGLONG     g_lastHelmInputTick = 0;
    // The audio component we last saw start a shanty. We only suppress a helm-leave disable
    // for THIS component, so an unrelated disable (other ship, idle component) is untouched.
    QmUE::UObject* g_activeComponent  = nullptr;
    volatile LONG g_suppressedCount   = 0;

    // A manual B-stop's Disable lands ~80ms after its toggle input (recon); a helm-leave's
    // Disable is always seconds after the last input AND that input was already consumed by
    // the start. 1500ms is comfortably above the input->disable latency and, thanks to the
    // consume rule, the exact value is not load-bearing.
    constexpr DWORD kHelmInputWindowMs = 1500;

    // Write the directory containing THIS DLL into `out` (no trailing sep). Anchors on a
    // local symbol so it resolves this module regardless of which DLL shares the basename.
    // Mirrors qm_killxp.cpp / qm_weather.cpp LocateDllDir.
    bool LocateDllDir(char* out, size_t outSz)
    {
        if (!out || outSz == 0) return false;
        HMODULE self = nullptr;
        if (!GetModuleHandleExA(
                GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS | GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT,
                reinterpret_cast<LPCSTR>(&LocateDllDir), &self) || !self)
            return false;

        char dllPath[MAX_PATH];
        DWORD n = GetModuleFileNameA(self, dllPath, sizeof(dllPath));
        if (n == 0 || n >= sizeof(dllPath)) return false;

        char* lastSep = strrchr(dllPath, '\\');
        if (!lastSep) return false;
        *lastSep = '\0';

        size_t dlen = strlen(dllPath);
        if (dlen + 1 > outSz) return false;
        memcpy(out, dllPath, dlen + 1);
        return true;
    }

    // Best-effort "ClassName 'ObjectName'" for an object, into a caller buffer. Caller is
    // inside SEH. Mirrors qm_killxp.cpp DescribeObject.
    void DescribeObject(QmUE::UObject* obj, char* out, size_t outSz)
    {
        out[0] = '\0';
        if (!obj) { snprintf(out, outSz, "<null>"); return; }
        char clsNm[160] = { 0 }, objNm[160] = { 0 };
        __try
        {
            QmUE::UClass* cls = obj->Class;
            if (cls) QmUE::ResolveFNameNarrow(cls->Name, clsNm, sizeof(clsNm));
            QmUE::ResolveFNameNarrow(obj->Name, objNm, sizeof(objNm));
        }
        __except (EXCEPTION_EXECUTE_HANDLER) {}
        snprintf(out, outSz, "%s'%s'", clsNm[0] ? clsNm : "?", objNm[0] ? objNm : "?");
    }

    // Lowercase-copy + substring scan (needle must already be lowercase).
    bool ContainsLc(const char* hay, const char* needleLc)
    {
        char lc[192];
        size_t i = 0;
        for (; hay[i] && i < sizeof(lc) - 1; ++i)
        {
            char c = hay[i];
            lc[i] = (c >= 'A' && c <= 'Z') ? (char)(c - 'A' + 'a') : c;
        }
        lc[i] = '\0';
        return strstr(lc, needleLc) != nullptr;
    }

    // ---- per-UFunction memoized verdict -----------------------------------
    // Name resolution runs ONCE per distinct UFunction; the hot path is then a pointer
    // compare + bit test. Direct-mapped; collisions just recompute (benign).
    constexpr uint8_t SH_VALID     = 0x80;
    constexpr uint8_t SH_ENABLE    = 0x01;   // ServerEnableShanty    (vanilla start)
    constexpr uint8_t SH_DISABLE   = 0x02;   // ServerDisableShanty   (B-stop AND helm-leave)
    constexpr uint8_t SH_HELMINPUT = 0x04;   // InpActEvt_IA_ShipToggleShanty* (helm B toggle)
    constexpr uint8_t SH_DECISIVE  = SH_ENABLE | SH_DISABLE | SH_HELMINPUT;

    struct ShFuncMemo { void* fn; volatile uint8_t verdict; };
    constexpr uint32_t kMemoMask = (1u << 13) - 1;   // 8192 slots
    ShFuncMemo g_memo[kMemoMask + 1] = {};

    uint8_t ComputeVerdict(QmUE::UFunction* func)
    {
        char nm[192] = { 0 };
        __try { QmUE::ResolveFNameNarrow(func->Name, nm, sizeof(nm)); }
        __except (EXCEPTION_EXECUTE_HANDLER) { return SH_VALID; }

        uint8_t v = SH_VALID;
        if (strcmp(nm, "ServerEnableShanty") == 0)       v |= SH_ENABLE;
        else if (strcmp(nm, "ServerDisableShanty") == 0) v |= SH_DISABLE;
        else if (ContainsLc(nm, "shiptoggleshanty"))     v |= SH_HELMINPUT;   // helm B input event
        return v;
    }

    uint8_t GetVerdict(QmUE::UFunction* func)
    {
        ShFuncMemo& s = g_memo[(((uintptr_t)func) >> 4) & kMemoMask];
        if (s.fn == func && (s.verdict & SH_VALID))
            return s.verdict;
        uint8_t v = ComputeVerdict(func);
        s.verdict = 0;       // invalidate while publishing
        s.fn      = func;
        s.verdict = v;       // publish complete verdict last
        return v;
    }
}

bool QmShanty_Init()
{
    if (g_initDone) return g_armed;
    g_initDone = true;

    char dir[MAX_PATH];
    if (!LocateDllDir(dir, sizeof(dir)))
    {
        QM_LOG_WARN("[Shanty] could not locate DLL dir - keep-alive disabled");
        g_armed = false;
        return false;
    }

    // Arm on ANY qm_shanty*.txt (manual qm_shanty.txt or a profile-bound
    // qm_shanty_<profile>.txt the Configurator deploys). Mirrors the weather/killxp glob.
    char pattern[MAX_PATH];
    snprintf(pattern, sizeof(pattern), "%s\\qm_shanty*.txt", dir);
    WIN32_FIND_DATAA fd;
    HANDLE h = FindFirstFileA(pattern, &fd);
    int files = 0;
    if (h != INVALID_HANDLE_VALUE)
    {
        do
        {
            if (!(fd.dwFileAttributes & FILE_ATTRIBUTE_DIRECTORY)) ++files;
        } while (FindNextFileA(h, &fd));
        FindClose(h);
    }
    g_armed = files > 0;

    if (g_armed)
        QM_LOG_INFO("[Shanty] *** ARMED *** keep-alive active (%d sentinel file(s) matching %s\\qm_shanty*.txt) - "
                    "the helm-leave shanty stop will be suppressed; B start/stop at the helm stays vanilla "
                    "(rides the global ProcessEvent net-hook)", files, dir);
    else
        QM_LOG_INFO("[Shanty] no qm_shanty*.txt - idle (zero cost)");
    return g_armed;
}

bool QmShanty_ReconArmed()
{
    if (!g_initDone) QmShanty_Init();
    return g_armed;
}

bool QmShanty_OnProcessEvent(QmUE::UObject* self, QmUE::UFunction* func, void* /*parms*/)
{
    if (!g_armed || !func) return false;

    bool suppress = false;
    __try
    {
        uint8_t v = GetVerdict(func);
        if (!(v & SH_DECISIVE)) return false;

        ULONGLONG now = GetTickCount64();

        if (v & SH_HELMINPUT)
        {
            // Helm B toggle pressed. Arm the "manual intent" window; the Enable/Disable
            // that follows within kHelmInputWindowMs will consume it.
            g_lastHelmInputTick = now;
            return false;
        }

        if (v & SH_ENABLE)
        {
            // Vanilla start (only happens at the helm). Track the component and consume the
            // toggle input that triggered it, so a later helm-leave disable can't see it.
            g_activeComponent   = self;
            g_lastHelmInputTick = 0;
            return false;
        }

        // SH_DISABLE: fires for BOTH a manual B-stop and a helm-leave.
        bool freshInput = (g_lastHelmInputTick != 0) &&
                          ((now - g_lastHelmInputTick) <= kHelmInputWindowMs);

        if (freshInput)
        {
            // Manual B-stop at the helm -> honour it. Consume input + clear active.
            g_lastHelmInputTick = 0;
            if (self == g_activeComponent) g_activeComponent = nullptr;
            return false;
        }

        if (self == g_activeComponent)
        {
            // No fresh toggle input + this is the component that started -> helm-leave.
            // Suppress: tell the net-hook NOT to forward, so the shanty keeps playing.
            // g_activeComponent stays set: returning to the helm and pressing B will land a
            // fresh-input disable that we forward (honour) and clear it.
            suppress = true;
            LONG n = InterlockedIncrement(&g_suppressedCount);
            char slf[352];
            DescribeObject(self, slf, sizeof(slf));
            QM_LOG_INFO("[Shanty] helm-leave #%ld -> SUPPRESS ServerDisableShanty (keep playing) self=0x%p %s",
                        n, (void*)self, slf);
        }
        // else: disable on an idle/other component -> forward unchanged (no-op for us).
    }
    __except (EXCEPTION_EXECUTE_HANDLER) { suppress = false; }
    return suppress;
}
