// Quartermaster "Mod Settings Tab" - RECON phase (logging-only).
// ---------------------------------------------------------------
// See qm_modtab.hpp for the architecture. This phase observes the three decisive settings-
// screen UFunctions as they dispatch through the ProcessInternal hook (CookTabs/SetData are
// Blueprint-internal calls that bypass ProcessEvent - see the hpp RECON FINDING) and dumps the
// layout we need to plan the injection:
//
//   - CookTabs            (BP_Settings_SC_C)        : the controller builds the tab list.
//   - SetData             (WBP_MetaUI_TabsGroup_C)  : the tab bar receives the tab data array.
//   - OnTabsStateChanged  (WBP_Settings_Screen_C)   : the screen reacts to tab selection.
//
// For CookTabs / SetData we hexdump the parms buffer and run a TArray-header heuristic over it
// to locate the TabsData array (Data ptr + Num + Max) and dump its first element bytes - that
// reveals the array offset inside parms AND the per-element struct stride, which is exactly
// what the future injection needs (grow that array by one entry named "Quartermaster").
//
// Logging-only: never touches parms, never suppresses dispatch.

#define _CRT_SECURE_NO_WARNINGS
#include <windows.h>
#include <stdint.h>
#include <stdio.h>
#include <string.h>

#include "qm_modtab.hpp"
#include "qm_ue.hpp"
#include "qm_log.hpp"
#include "qm_alloc.hpp"

namespace
{
    bool g_initDone = false;
    bool g_armed    = false;

    volatile LONG g_seq        = 0;   // decisive-dispatch sequence number
    volatile LONG g_traceCount = 0;   // bounded lifecycle-trace lines

    // Stop emitting the broad lifecycle trace after this many lines (keeps the log readable;
    // the three decisive functions are never capped). With the Tick flood tagged MT_NOISE and
    // skipped, the non-Tick click sequence is sparse, so this budget comfortably spans the
    // open -> click-through-tabs -> close window the refresh-trigger recon needs.
    constexpr LONG kMaxTraceLines = 400;
    // How many bytes of a parms buffer we hexdump (parms are small; this is a safety cap).
    constexpr int32_t kMaxParmsDump = 256;
    // How many bytes of a TArray element buffer we hexdump (enough to see struct + repetition).
    constexpr int32_t kMaxElemDump  = 192;

    // Write the directory containing THIS DLL into `out` (no trailing sep). Anchors on a local
    // symbol so it resolves this module regardless of which DLL shares the basename. Mirrors
    // qm_shanty.cpp / qm_killxp.cpp LocateDllDir.
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

    // Best-effort "ClassName'ObjectName'" for an object, into a caller buffer. Caller is inside
    // SEH. Mirrors qm_shanty.cpp / qm_killxp.cpp DescribeObject.
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

    // Lowercase-copy + substring scan (needle must already be lowercase). Mirrors qm_shanty.
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
    // Name+owner-class resolution runs ONCE per distinct UFunction; the hot path is then a
    // pointer compare + bit test. Direct-mapped; collisions just recompute (benign).
    constexpr uint8_t MT_VALID    = 0x80;
    constexpr uint8_t MT_COOKTABS = 0x01;   // CookTabs            (BP_Settings_SC_C)
    constexpr uint8_t MT_SETDATA  = 0x02;   // SetData             (WBP_MetaUI_TabsGroup_C)
    constexpr uint8_t MT_TABSTATE = 0x04;   // OnTabsStateChanged  (WBP_Settings_Screen_C)
    constexpr uint8_t MT_TRACE    = 0x08;   // other settings-screen lifecycle fn (bounded log)
    constexpr uint8_t MT_NOISE    = 0x10;   // per-frame flood (Tick): drives probes but never logged/capped
    constexpr uint8_t MT_ONEXIT   = 0x20;   // OnExit              (BP_Settings_SC_C) : settings closing (teardown)
    constexpr uint8_t MT_ENTER    = 0x40;   // OnEnter             (BP_Settings_SC_C) : settings (re)opening -> force re-mount
    constexpr uint8_t MT_DECISIVE = MT_COOKTABS | MT_SETDATA | MT_TABSTATE | MT_ONEXIT | MT_ENTER;
    constexpr uint8_t MT_ANY      = MT_DECISIVE | MT_TRACE;

    struct MtFuncMemo { void* fn; volatile uint8_t verdict; };
    constexpr uint32_t kMemoMask = (1u << 13) - 1;   // 8192 slots
    MtFuncMemo g_memo[kMemoMask + 1] = {};

    // ---- click-armed verbose window (Weg C: native/Tick rebuild recon) ------------------------
    // RECON FINDING (#10): navigating to another tab makes the injected 6th tab appear, but NO BP
    // refresh function (SetData/UpdateTabs/OnTabsStateChanged) fires - the rebuild is native or
    // Tick-driven, i.e. invisible to the rider. To SEE it we open a short verbose window when a tab
    // button is CLICKED; during that window the otherwise-skipped Tick flood plus the GameSettings
    // Panel/ListView dispatches are logged. To stay readable we emit only the FIRST Tick per distinct
    // widget instance per window (a newly created 6th tab widget => a fresh line; the steady per-frame
    // repeats are suppressed). The tick-seen table is invalidated per window by bumping a generation
    // counter instead of clearing it.
    volatile ULONGLONG g_verboseUntilTick = 0;
    volatile LONG      g_verboseGen       = 0;   // bumped each arm; stale gens count as "not seen"
    volatile LONG      g_verboseLines     = 0;   // session-wide hard cap on verbose lines
    constexpr ULONGLONG kVerboseWindowMs  = 2500;
    constexpr LONG      kMaxVerboseLines  = 600;
    struct TickSeen { void* obj; volatile LONG gen; };
    constexpr uint32_t kTickSeenMask = 255;      // 256 slots, direct-mapped (collisions re-log, benign)
    TickSeen g_tickSeen[kTickSeenMask + 1] = {};

    // A UFunction's Outer is its owning UClass - resolve the class name from there so the
    // verdict is fully determined by `func` alone (no need for `self` at memo time).
    uint8_t ComputeVerdict(QmUE::UFunction* func)
    {
        char fnNm[160] = { 0 }, clsNm[160] = { 0 };
        __try
        {
            QmUE::ResolveFNameNarrow(func->Name, fnNm, sizeof(fnNm));
            QmUE::UObject* owner = func->Outer;
            if (owner) QmUE::ResolveFNameNarrow(owner->Name, clsNm, sizeof(clsNm));
        }
        __except (EXCEPTION_EXECUTE_HANDLER) { return MT_VALID; }

        uint8_t v = MT_VALID;
        if (strcmp(fnNm, "CookTabs") == 0)                                    v |= MT_COOKTABS;
        else if (strcmp(fnNm, "SetData") == 0 && ContainsLc(clsNm, "tabsgroup")) v |= MT_SETDATA;
        else if (strcmp(fnNm, "OnTabsStateChanged") == 0)                     v |= MT_TABSTATE;
        else if (strcmp(fnNm, "OnExit") == 0 && ContainsLc(clsNm, "settings")) v |= MT_ONEXIT;
        else if (strcmp(fnNm, "OnEnter") == 0 && ContainsLc(clsNm, "settings")) v |= MT_ENTER;
        else if (ContainsLc(clsNm, "metaui_tab") ||
                 ContainsLc(clsNm, "settings_screen") ||
                 ContainsLc(clsNm, "settings_sc") ||
                 ContainsLc(clsNm, "gamesettingpanel") ||
                 ContainsLc(clsNm, "gamesettinglistview") ||
                 ContainsLc(clsNm, "gamesettinglistentry"))
        {
            v |= MT_TRACE;
            // Tick fires ~77x/frame per tab widget - it floods the trace cap before the user can
            // click. Keep it MT_TRACE (so it still drives the one-shot probes) but tag it NOISE so
            // the rider skips emission + cap. The refresh-trigger recon needs the NON-Tick click
            // sequence (UpdateTabs / SetData / OnTabsStateChanged) to survive until the click.
            if (strcmp(fnNm, "Tick") == 0) v |= MT_NOISE;
        }
        return v;
    }

    uint8_t GetVerdict(QmUE::UFunction* func)
    {
        MtFuncMemo& s = g_memo[(((uintptr_t)func) >> 4) & kMemoMask];
        if (s.fn == func && (s.verdict & MT_VALID))
            return s.verdict;
        uint8_t v = ComputeVerdict(func);
        s.verdict = 0;       // invalidate while publishing
        s.fn      = func;
        s.verdict = v;       // publish complete verdict last
        return v;
    }

    // ProcessEvent param-buffer size = UFunction::ParmsSize (params + return value), NOT
    // UStruct::StructSize/PropertiesSize - the latter also covers BP local variables (e.g. the
    // parameterless BP event GoToNextTab still has StructSize 249 from its locals). ProcessEvent only
    // copies ParmsSize bytes in/out, so that is the correct buffer size for a synthesized call.
    int32_t ParmsSize(QmUE::UFunction* func)
    {
        int32_t sz = 0;
        __try { sz = (int32_t)func->ParmsSize; }
        __except (EXCEPTION_EXECUTE_HANDLER) { sz = 0; }
        if (sz < 0) sz = 0;
        return sz;
    }

    // Hexdump up to `cap` bytes of [base..) as "+0xNN: XX XX .. | ascii" lines, one log line
    // each, prefixed by `tag`. SEH-guarded per 16-byte row so a bad page truncates cleanly.
    void HexDump(const char* tag, const uint8_t* base, int32_t cap)
    {
        if (!base || cap <= 0) { QM_LOG_INFO("[ModTab]   %s <null/empty>", tag); return; }
        for (int32_t off = 0; off < cap; off += 16)
        {
            char hex[16 * 3 + 1]; char asc[17];
            int hn = 0; int an = 0;
            bool faulted = false;
            __try
            {
                int row = (cap - off) < 16 ? (cap - off) : 16;
                for (int i = 0; i < row; ++i)
                {
                    uint8_t b = base[off + i];
                    hn += snprintf(hex + hn, sizeof(hex) - hn, "%02X ", b);
                    asc[an++] = (b >= 32 && b < 127) ? (char)b : '.';
                }
                asc[an] = '\0';
            }
            __except (EXCEPTION_EXECUTE_HANDLER) { faulted = true; }
            if (faulted) { QM_LOG_INFO("[ModTab]   %s +0x%02X: <fault>", tag, off); return; }
            QM_LOG_INFO("[ModTab]   %s +0x%02X: %-48s | %s", tag, off, hex, asc);
        }
    }

    // Scan a parms buffer for plausible TArray<T> headers ({void* Data; int32 Num; int32 Max}).
    // The settings tab data is carried as one such array; finding it pins down its byte offset
    // inside parms and lets us dump the element struct. Logs every candidate + dumps elem bytes.
    void ScanForTArrays(const uint8_t* parms, int32_t size)
    {
        if (!parms || size < 16) return;
        int found = 0;
        for (int32_t o = 0; o + 16 <= size; o += 8)
        {
            void*   data = nullptr; int32_t num = 0, max = 0;
            bool ok = false;
            __try
            {
                data = *reinterpret_cast<void* const*>(parms + o);
                num  = *reinterpret_cast<const int32_t*>(parms + o + 8);
                max  = *reinterpret_cast<const int32_t*>(parms + o + 12);
                ok   = true;
            }
            __except (EXCEPTION_EXECUTE_HANDLER) { ok = false; }
            if (!ok) continue;

            const bool plausible =
                data != nullptr &&
                reinterpret_cast<uintptr_t>(data) > 0x10000 &&
                num > 0 && num <= max && max <= 4096;
            if (!plausible) continue;

            ++found;
            int32_t stride = num > 0 ? (kMaxElemDump) : 0;   // unknown true stride; dump a window
            QM_LOG_INFO("[ModTab]   TArray-candidate @ parms+0x%02X: Data=0x%p Num=%d Max=%d "
                        "(dumping first %d bytes of the buffer; repetition reveals the element stride)",
                        o, data, num, max, kMaxElemDump);
            (void)stride;
            char etag[32];
            snprintf(etag, sizeof(etag), "elem@+0x%02X", o);
            HexDump(etag, reinterpret_cast<const uint8_t*>(data), kMaxElemDump);
        }
        if (found == 0)
            QM_LOG_INFO("[ModTab]   (no TArray-header candidate found in parms - array may be empty here or live behind a pointer)");
    }

    // ---- One-shot reflection enumeration of the settings-screen classes -----------------------
    // RECON FINDING (2026-06-10): the reference-derived functions CookTabs / SetData /
    // OnTabsStateChanged dispatch through NEITHER ProcessEvent NOR ProcessInternal (the latter is
    // now hooked and confirmed live - we see OnEnter/Construct/Tick on these very classes, but
    // never the three decisive functions). Two possibilities remain: they are native UFunctions
    // (their ExecFunction is a native thunk, not ProcessInternal, so a BP-funnel hook can't see
    // them), or the reference-derived names are simply wrong for this build. This dumps the ACTUAL
    // UFunctions on each settings class with name + FunctionFlags + ExecFunction so we can read off
    // the real names and decide whether each is BP (hookable via the rider) or native (needs a
    // per-UFunction exec hook like CreateTabsData/Consume). Logging-only.
    //
    // Driven lazily from the rider (so classes are already registered when settings open),
    // throttled to <=1 GObjects scan-pass/sec, attempt-capped, then latches off forever.
    constexpr uint32_t MT_FUNC_Native = 0x00000400;

    struct EnumTarget { const char* className; volatile LONG done; };
    EnumTarget g_enumTargets[] = {
        { "BP_Settings_SC_C",       0 },   // settings-screen controller (CookTabs lives here per ref)
        { "WBP_Settings_Screen_C",  0 },   // screen widget (OnTabsStateChanged per ref)
        { "WBP_MetaUI_TabsGroup_C", 0 },   // tab bar (SetData per ref)
        { "WBP_MetaUI_Tab_Main_C",  0 },   // one tab entry widget
    };
    constexpr int kEnumTargetCount = (int)(sizeof(g_enumTargets) / sizeof(g_enumTargets[0]));

    volatile LONG      g_enumAllDone  = 0;
    volatile LONG      g_enumAttempts = 0;
    volatile ULONGLONG g_enumLastTick = 0;
    constexpr LONG     kEnumMaxAttempts = 25;   // ~25s of in-settings time before giving up

    // Walk a class' own + (bounded) inherited Children, logging each UFunction's name, flags and
    // exec pointer. piAddr is ProcessInternal: exec==piAddr means a BP function (rider-hookable),
    // exec!=piAddr (or FUNC_Native set) means a native thunk (needs a per-function exec hook).
    void EnumerateClassFunctions(const char* targetName, QmUE::UClass* leaf, void* piAddr)
    {
        QM_LOG_INFO("[ModTab] enum '%s' (class=0x%p, ProcessInternal=0x%p) - listing UFunctions:",
                    targetName, (void*)leaf, piAddr);
        char nameBuf[256], clsNm[160];
        int logged = 0, depth = 0;
        for (QmUE::UStruct* s = leaf; s && depth < 4 && logged < 80; s = s->SuperStruct, ++depth)
        {
            clsNm[0] = '\0';
            __try { QmUE::ResolveFNameNarrow(s->Name, clsNm, sizeof(clsNm)); }
            __except (EXCEPTION_EXECUTE_HANDLER) {}
            for (QmUE::UField* f = s->Children; f && logged < 80; f = f->Next)
            {
                if (!f || !f->Class) continue;
                if ((f->Class->CastFlags & QmUE::CASTFLAG_Function) == 0) continue;
                if (!QmUE::ResolveFNameNarrow(f->Name, nameBuf, sizeof(nameBuf))) continue;
                QmUE::UFunction* fn = reinterpret_cast<QmUE::UFunction*>(f);
                void* exec = reinterpret_cast<void*>(fn->ExecFunction);
                bool nativeFlag = (fn->FunctionFlags & MT_FUNC_Native) != 0;
                bool execIsPI   = (exec == piAddr);
                QM_LOG_INFO("[ModTab]   %s::%s Flags=0x%08X ExecFn=0x%p %s%s",
                    clsNm[0] ? clsNm : "?", nameBuf, fn->FunctionFlags, exec,
                    nativeFlag ? "[FUNC_Native] " : "",
                    execIsPI ? "(exec=ProcessInternal -> BP, hookable via rider)"
                             : "(exec!=ProcessInternal -> native thunk, needs per-fn hook)");
                ++logged;
            }
        }
        QM_LOG_INFO("[ModTab] enum '%s' done - %d UFunction(s) logged (walked %d class level(s))",
                    targetName, logged, depth);
    }

    // Called from the rider on every settings-screen dispatch. Latches off once every target class
    // has been enumerated (or after the attempt cap). FindClassByName is a full GObjects scan, so
    // the 1s throttle keeps this from running per-Tick during the tab-bar render flood.
    void TryEnumerateSettingsClasses()
    {
        if (g_enumAllDone) return;

        ULONGLONG now  = GetTickCount64();
        ULONGLONG last = g_enumLastTick;
        if (last != 0 && (now - last) < 1000) return;   // <=1 scan-pass/sec
        g_enumLastTick = now;

        LONG  attempt = InterlockedIncrement(&g_enumAttempts);
        void* piAddr  = reinterpret_cast<void*>(QmUE::GetProcessInternalFn());

        int remaining = 0;
        for (int i = 0; i < kEnumTargetCount; ++i)
        {
            if (g_enumTargets[i].done) continue;
            QmUE::UClass* cls = QmUE::FindClassByName(g_enumTargets[i].className);
            if (!cls) { ++remaining; continue; }
            if (InterlockedCompareExchange(&g_enumTargets[i].done, 1, 0) != 0) continue;
            EnumerateClassFunctions(g_enumTargets[i].className, cls, piAddr);
        }

        if (remaining == 0)
        {
            InterlockedExchange(&g_enumAllDone, 1);
            QM_LOG_INFO("[ModTab] enum: all %d settings classes enumerated - latching off", kEnumTargetCount);
        }
        else if (attempt >= kEnumMaxAttempts)
        {
            InterlockedExchange(&g_enumAllDone, 1);
            QM_LOG_WARN("[ModTab] enum: giving up after %ld attempts - %d class(es) never appeared in GObjects "
                        "(not loaded in this menu, or the reference-derived name is wrong for this build):",
                        attempt, remaining);
            for (int i = 0; i < kEnumTargetCount; ++i)
                if (!g_enumTargets[i].done)
                    QM_LOG_WARN("[ModTab] enum:   NOT FOUND: '%s'", g_enumTargets[i].className);
        }
    }

    // ---- One-shot ACTIVE probe: call WBP_Settings_Screen_C::GetTabs on the live screen -----------
    // RECON FINDING (2026-06-10, enum): CookTabs / SetData / OnTabsStateChanged are genuine BP
    // functions (exec == ProcessInternal) but they cook the tab list exactly once at boot - BEFORE
    // our hook is live - and the result is cached, so re-opening settings never re-dispatches them.
    // We therefore can't catch the tab array passively. Instead we read it ACTIVELY: GetTabs is a BP
    // getter on the live screen widget that returns the TArray of tab data. Calling it via
    // CallProcessEvent and dumping the returned buffer reveals the array offset + per-element stride
    // directly - the exact layout the future injection needs (GetTabs -> append our entry -> SetData
    // on the TabsGroup). Still recon: we read the array, we don't modify it.
    constexpr const char* kSettingsScreenClass = "WBP_Settings_Screen_C";
    constexpr const char* kGetTabsFuncName     = "GetTabs";

    volatile LONG      g_getTabsDone     = 0;   // one-shot: the GetTabs array-layout recon dump (first open only)
    volatile ULONGLONG g_getTabsLastTick = 0;
    // The (re)build of our tab + panel re-enters this rider via the ProcessEvent dispatches it makes
    // (GetTabs, Create, AddChild, the tab-click sim). This guard makes those re-entrant polls no-ops so a
    // rebuild can't recurse into itself. It replaces the old screen-pointer/one-shot re-arm: the
    // WBP_Settings_Screen_C widget is POOLED (identical pointer across opens), so a pointer-compare could
    // never detect a reopen - self-heal keys off whether our collection is still in Screen::Tabs instead
    // (see OurCollectionPresentInTabs), which survives the pooling.
    volatile LONG      g_rebuildInProgress = 0;
    // Re-scan cadence for the live-screen lookup. The first dispatch after OnEnter runs immediately
    // (last==0); the WBP_Settings_Screen_C widget is usually not constructed yet, so we retry on later
    // dispatches. This throttle exists ONLY to keep the O(GObjects) instance walk off the full Tick
    // dispatch rate (~525 Hz) - one scan per frame catches the screen within ~1 frame of going live,
    // turning the old ~1 s open-to-tab latency into a few ms. Lower further only if the GObjects walk
    // stays cheap; 0 would scan on every dispatch and hammer the walk.
    constexpr ULONGLONG kGetTabsScanIntervalMs = 16;   // ~1 frame @ 60 fps

    // ---- Liveness test (MUTATING) -------------------------------------------------------------
    // Separate opt-in from the recon dump: only runs when qm_modtab_inject.txt is present. It
    // duplicates an existing tab collection pointer into BOTH live arrays (Screen::Tabs +
    // Registry::TopLevelSettings - the recon verdict says they are separate backing stores) and
    // then forces a re-cook to see whether a sixth (duplicate) tab renders. This pins down the
    // re-cook trigger before we build a real "Quartermaster" collection. We run on the game thread
    // inside a BP dispatch (the rider), so there is no cross-thread race with Slate; and we append
    // in-place ONLY when there is spare capacity (Num < Max) so no FMalloc realloc happens from our
    // thread (the cold-path AV from qm_inject).
    constexpr const char* kSettingsControllerClass = "BP_Settings_SC_C";
    // Weg B (refresh trigger): the only proven bar-reconcile path is a real tab-button click (recon
    // #11). Its BP entry point is the tab widget's OnButtonClicked delegate; invoking it on a live tab
    // widget instance (so `this` = that tab) replicates the click that natively rebuilds the bar.
    constexpr const char* kTabWidgetClass  = "WBP_MetaUI_Tab_Main_C";
    constexpr const char* kTabClickDelegate =
        "BndEvt__WBP_MetaUI_Tab_btn_Root_K2Node_ComponentBoundEvent_0_OnButtonClickedEvent__DelegateSignature";
    bool          g_injectArmed = false;
    // The injected Quartermaster collection (the tab DATA object), captured once at inject. #18c probed
    // whether the clicked tab WIDGET stores this pointer (it does not), so the gate keys off the tab widget
    // instead (g_ourTabWidget below). Retained as the canonical handle for the upcoming mount round.
    void*         g_ourCollection = nullptr;
    volatile LONG g_tabReconCount = 0;
    constexpr int         kMaxTabRecon   = 16;     // cap tab-click gate-log lines per session (readability)
    constexpr const char* kTabsGroupClass = "WBP_MetaUI_TabsGroup_C";
    // #18j: the LIVE tab bar. On a settings reopen the content tree is pooled (our panel survives mounted)
    // but the tab BAR is RE-COOKED - a fresh WBP_MetaUI_TabsGroup_C is Constructed while the previous one
    // lingers un-GC'd in GObjects. FindFirstInstanceOfClass then returns the STALE bar, so the gate resolved
    // a DEAD QM tab and never matched the freshly-clicked live tab ("Reopen zeigt nichts"). The rider sees
    // the fresh Construct on the new bar: latch its self here (only while settings is open, so an unrelated
    // menu's TabsGroup cannot clobber it) and walk THAT in ResolveOurTabWidget. Cleared on OnExit.
    void*         g_liveTabsGroup = nullptr;
    bool          g_settingsOpen  = false;
    // #18d: the live visibility-gate key - our Quartermaster tab WIDGET. We injected our collection as
    // the LAST tab data entry, so the bar's LAST WBP_MetaUI_Tab_Main_C is ours. A tab click rebuilds the
    // bar (fresh widget instances), so this is re-resolved on each click, compared (never dereferenced).
    void*         g_ourTabWidget = nullptr;
    // #18e: our own mounted content panel (a ScrollBox, sibling of Settings_Panel in the content
    // VerticalBox). Its visibility IS the gate output - SetVisibility(Visible) on our tab,
    // SetVisibility(Collapsed) on every other tab. Starts Collapsed (Quartermaster is never the default tab on
    // open). #18l: rebuilt FRESH on every (re)open - the screen tree is pooled but a REUSED widget loses its
    // Slate realization across a reopen and renders nothing, so ProbeViewPath discards the prior panel and
    // constructs a brand-new one each time. Kept on OnExit (only reset to Collapsed) until that next discard.
    void*         g_ourPanel = nullptr;
    // #18h/#18i/#18l: the content VerticalBox our ScrollBox is parented into (the mount point). OnEnter (the
    // proven per-(re)open BP signal) NULLS this pointer on every open, so OurPanelMounted() goes false and the
    // next self-heal poll runs ProbeViewPath. Under #18l that poll DISCARDS the stale panel (a reused widget
    // loses its Slate realization on reopen and renders nothing) and builds a brand-new one into the freshly
    // resolved content box; the collection is still in Screen::Tabs -> no duplicate tab. Re-set to the live box
    // by the fresh mount.
    void*         g_mountTarget = nullptr;
    // #18f: the native content host (WBP_Settings_Panel_C 'Settings_Panel'). Its content is a data-driven
    // GameSettingListView we cannot AddChild into, so our panel is a SIBLING that stacks BELOW it - which is
    // why the row sat at the bottom. The gate now ALSO collapses this on our tab (and restores it on every
    // other tab), so our panel takes the content area instead. Captured in ProbeViewPath; KEPT across reopen
    // (pooled tree) and restored to Visible on OnExit so the native settings are never left hidden.
    void*         g_nativePanel = nullptr;
    // (#18n click-time re-mount, #18o ProcessEvent-post re-mount and the g_everMounted gate are retired - the
    //  panel mount now rides the per-UFunction CookTabs-post hook, so none of those latches remain.)
    // #18t: the LIVE settings screen. The 18s log proved the game builds a BRAND-NEW WBP_Settings_Screen_C
    // hierarchy on every settings (re)open (new screen + TabsGroup + tab widgets all Tick) while the previous
    // instance lingers un-GC'd in GObjects - the same stale-instance pattern as g_liveTabsGroup above.
    // FindFirstInstanceOfClass returned the STALE screen, so every reopen post-cook mount landed - with a
    // perfectly consistent parentOK=1 readback - inside a DETACHED widget tree that never renders. That single
    // mis-resolution explains the whole "alles korrekt, trotzdem unsichtbar" series. OnTabsStateChanged (PLSF
    // thunk) fires INSIDE every cook with self = the live screen, milliseconds BEFORE our CookTabs post-mount
    // (proven for the first cook of a session too): latch it there, consume it in the post, clear on OnExit.
    void*         g_liveScreen = nullptr;

    // ===================== #18r PER-UFUNCTION HOOK via the global PLSF detour ==========================
    // The reopen render bug was never the widget or the gate - it was the MOUNT MOMENT. The reference mod
    // (R5ModSettings) builds + mounts its panel in the POST of a UE4SS RegisterHook on BP_Settings_SC_C::
    // CookTabs. #18p/#18q tried to replicate that with an ExecFunction-field swap (+0xD8) - and the 18q log
    // DISPROVED that layer: all three swaps stood verified in-field (enum readback showed our thunks), the
    // install provably preceded the first cook (the settings classes lazy-load at first open, our poll caught
    // them frames earlier), OnTabsStateChanged ran on every one of 8 logged tab clicks - and not one thunk
    // ever fired. Conclusion: BP-internal calls (BP -> own/other BP function) execute through the script-VM
    // funnel ProcessLocalScriptFunction (PLSF) and NEVER read ExecFunction for non-native functions; the
    // field is only consulted on the ProcessEvent->Invoke path, which these three never take. UE4SS wins
    // because it hooks PLSF globally (its HookProcessLocalScriptFunction layer).
    //
    // #18r therefore hooks PLSF itself (MinHook, qm_hook.cpp - a DIFFERENT body than ProcessInternal, no
    // collision with the lifecycle/prewarm detour). The detour matches FFrame::Node against the handles
    // below and routes to our thunks; the thunks forward through the PLSF trampoline. CookTabs-post = the
    // fresh build+mount (THE reopen fix). OnTabsStateChanged + SetData only DUMP their parms (Build-B input).
    // The working #18d click gate + the Screen::Tabs tab inject stay active this build.
    QmUE::UFunction*     g_fnCookTabs   = nullptr;   // resolved targets - matched against FFrame::Node in the PLSF detour
    QmUE::UFunction*     g_fnTabState   = nullptr;
    QmUE::UFunction*     g_fnSetData    = nullptr;
    QmUE::FNativeFuncPtr g_plsfOriginal = nullptr;   // MinHook trampoline to the real PLSF body (set by qm_hook.cpp BEFORE
                                                     // the detour goes live). The thunks forward through THIS - forwarding
                                                     // through ProcessInternal instead would re-enter the patched PLSF
                                                     // entry and recurse.
    volatile LONG        g_cookTabsHookLive    = 0;  // 1 only once our CookTabs thunk has FIRED and its post-mount landed.
                                                     // Until then the self-heal bootstrap mount keeps owning the mount, so
                                                     // a hook that never fires can never regress the first open (the #18p
                                                     // lesson).
    volatile LONG        g_cookTabsFiredCount  = 0;  // total thunk invocations; also answers WHEN CookTabs really fires
                                                     // (boot-only vs per-open was undecidable before - both observation
                                                     // layers were blind to BP-internal dispatch)
    volatile LONG        g_allFnHooksInstalled = 0;  // 1 once every target handle is resolved: stop retrying the poll
    volatile LONG        g_qmTabActive         = 0;  // #18s: 1 while the QM tab is the selected tab (latched by the #18d
                                                     // click gate, cleared on OnExit). CookTabs fires on EVERY tab click
                                                     // and runs AFTER the gate on that same click - its post-mount replaces
                                                     // the just-shown panel with a fresh Collapsed one (the proven 18r
                                                     // race). The cook-post re-applies the gate state from this latch.
    // EARLY RESOLVE: the settings BP classes lazy-load (this session: at first settings open, ~60ms before
    // OnEnter). The global ProcessEvent hook (live from engine start, game thread) drives a time-throttled
    // resolve poll so the handles are latched the moment the classes appear - before the first cook runs.
    volatile ULONGLONG   g_fnHookPollLastTick  = 0;  // GetTickCount64 of the last early-resolve attempt
    constexpr ULONGLONG  kFnHookEarlyPollMs    = 16; // ~1 attempt/frame while unresolved; latches off once resolved
    constexpr LONG       kFnDumpBudgetPerOpen  = 4;  // a handful of dumps per open is plenty to pin the layout
    volatile LONG        g_tabStateDumpBudget  = kFnDumpBudgetPerOpen;  // remaining OnTabsStateChanged parm dumps (re-armed on
    volatile LONG        g_setDataDumpBudget   = kFnDumpBudgetPerOpen;  //  OnEnter; pre-armed so BOOT-time fires dump too)
    constexpr uintptr_t  kFFrameNodeOff        = 0x10;  // FFrame::Node (the executing UFunction) - same offsets the rider uses
    constexpr uintptr_t  kFFrameLocalsOff      = 0x28;  // FFrame::Locals (the packed param block)

    // Dereference the GetTabs array's elements (recon dump confirmed it is TArray<UObject*>: a run
    // of Num 8-byte pointers, then the reserved Max slot). RECON FINDING (2026-06-10): the elements
    // are native UGameSettingCollection objects (Epic GameSettings framework), not WBP_MetaUI_Tab_*
    // widgets. So for each element we (a) identity-list it (class + instance name), (b) call its
    // GetDevName()/GetDisplayName() getters to read the internal id + visible label, and (c) hexdump
    // element[0]'s head out to +0x140 so the UGameSetting layout is visible incl. the child-settings
    // TArray at +0x128 (UGameSettingCollection::Settings). That fully pins down how a sixth
    // "Quartermaster" tab is identified + labelled. Read-only, SEH-guarded.
    constexpr int32_t kMaxTabObjDump = 0x140;

    // Read an FText's source string narrow (raw + SEH-guarded). UE5.6 layout (Dumper-7 Basic.hpp):
    //   FText     { FTextData* TextData; pad8 }            size 0x10
    //   FTextData { uint8 pad[0x20]; FString TextSource; } -> string lives at TextData+0x20
    //   FString   { wchar_t* Data; int32 Num; int32 Max }  (Num counts the null terminator)
    // Returns true and fills `out` only if a non-empty string was read. Non-ASCII bytes -> '?'.
    bool ReadFTextNarrow(const void* ftext, char* out, size_t outSz)
    {
        if (out && outSz) out[0] = '\0';
        if (!ftext || !out || outSz < 2) return false;
        __try
        {
            const uint8_t* textData = *reinterpret_cast<const uint8_t* const*>(ftext);
            if (!textData) return false;
            const QmUE::FString* src = reinterpret_cast<const QmUE::FString*>(textData + 0x20);
            const wchar_t* data = src->Data;
            int32_t        num  = src->Num;
            if (!data || num <= 0 || num > 4096) return false;
            size_t i = 0;
            for (; i + 1 < outSz && i < (size_t)num && data[i]; ++i)
                out[i] = (data[i] >= 32 && data[i] < 127) ? (char)data[i] : '?';
            out[i] = '\0';
            return i > 0;
        }
        __except (EXCEPTION_EXECUTE_HANDLER) { out[0] = '\0'; return false; }
    }

    // Call the UGameSetting getters GetDevName (FName) + GetDisplayName (FText) on a live tab
    // collection and log the resolved id + label. Param blocks (Dumper-7 Assertions.inl):
    //   GameSetting_GetDevName     : 0x08 { FName ReturnValue@0x00 }
    //   GameSetting_GetDisplayName : 0x10 { FText ReturnValue@0x00 }
    // Both are native getters, so they dispatch through ProcessEvent directly (no FUNC_Native flip
    // needed). CallProcessEvent is SEH-guarded. The returned FText holds one AddRef'd shared ref we
    // intentionally leak (recon-only, runs once per latched probe). Read-only otherwise.
    void DumpCollectionLabels(QmUE::UObject* coll, int idx)
    {
        if (!coll || !coll->Class) return;

        QmUE::UFunction* devFn = QmUE::FindFunctionOnClass(coll->Class, "GetDevName");
        if (devFn)
        {
            uint8_t pb[16]; memset(pb, 0, sizeof(pb));
            bool ok = QmUE::CallProcessEvent(coll, devFn, pb);
            char nm[160] = { 0 };
            if (ok)
            {
                QmUE::FName fn = { 0, 0 };
                __try { fn = *reinterpret_cast<const QmUE::FName*>(pb); }
                __except (EXCEPTION_EXECUTE_HANDLER) { fn = { 0, 0 }; }
                QmUE::ResolveFNameNarrow(fn, nm, sizeof(nm));
            }
            QM_LOG_INFO("[ModTab]   tab[%d] GetDevName ok=%d -> '%s'", idx, ok ? 1 : 0, nm[0] ? nm : "<unresolved>");
        }
        else
            QM_LOG_INFO("[ModTab]   tab[%d] GetDevName: not a UFunction on this class", idx);

        QmUE::UFunction* dnFn = QmUE::FindFunctionOnClass(coll->Class, "GetDisplayName");
        if (dnFn)
        {
            uint8_t pb[16]; memset(pb, 0, sizeof(pb));
            bool ok = QmUE::CallProcessEvent(coll, dnFn, pb);
            char label[256] = { 0 };
            bool got = ok && ReadFTextNarrow(pb, label, sizeof(label));
            QM_LOG_INFO("[ModTab]   tab[%d] GetDisplayName ok=%d -> label='%s'",
                        idx, ok ? 1 : 0, got ? label : "<empty/unreadable>");
        }
        else
            QM_LOG_INFO("[ModTab]   tab[%d] GetDisplayName: not a UFunction on this class", idx);
    }

    void DumpTabArrayElements(const uint8_t* arrayHeader)
    {
        if (!arrayHeader) return;
        void*   data = nullptr; int32_t num = 0, max = 0;
        __try
        {
            data = *reinterpret_cast<void* const*>(arrayHeader);
            num  = *reinterpret_cast<const int32_t*>(arrayHeader + 8);
            max  = *reinterpret_cast<const int32_t*>(arrayHeader + 12);
        }
        __except (EXCEPTION_EXECUTE_HANDLER) { return; }

        if (!data || num <= 0 || num > max || max > 4096)
        {
            QM_LOG_INFO("[ModTab]   tab-elems: array header implausible (Data=0x%p Num=%d Max=%d) - skipping deref",
                        data, num, max);
            return;
        }

        QmUE::UObject* const* elems = reinterpret_cast<QmUE::UObject* const*>(data);
        for (int32_t i = 0; i < num; ++i)
        {
            QmUE::UObject* tab = nullptr;
            __try { tab = elems[i]; }
            __except (EXCEPTION_EXECUTE_HANDLER) { tab = nullptr; }
            char id[352];
            DescribeObject(tab, id, sizeof(id));
            QM_LOG_INFO("[ModTab]   tab[%d] = 0x%p %s", i, (void*)tab, id);
            if (tab) DumpCollectionLabels(tab, i);   // GetDevName + GetDisplayName via CallProcessEvent
        }

        QmUE::UObject* first = nullptr;
        __try { first = elems[0]; }
        __except (EXCEPTION_EXECUTE_HANDLER) { first = nullptr; }
        if (!first) { QM_LOG_INFO("[ModTab]   tab[0] is null - cannot dump element layout"); return; }

        QM_LOG_INFO("[ModTab]   tab[0] layout dump (first %d bytes - hunt label FText/FString + index/icon fields):",
                    kMaxTabObjDump);
        HexDump("tab[0]", reinterpret_cast<const uint8_t*>(first), kMaxTabObjDump);
    }

    // ---- Registry recon: read the LIVE tab backing, not the GetTabs copy ----------------------
    // RECON FINDING (2026-06-10, static Dumper-7 SDK): the settings screen is a UR5SettingScreen
    // (native parent of WBP_Settings_Screen_C) whose tabs flow through two native fields, and the
    // source of truth is the registry:
    //   UGameSettingScreen::Registry           @ 0x3A8  -> UR5GameSettingRegistry
    //   UR5SettingScreen::Tabs                 @ 0x3B8  (BlueprintReadOnly) <- what GetTabs returns
    //   UGameSettingRegistry::TopLevelSettings @ 0x088  <- SSOT: the top-level tab collections
    //   UGameSettingRegistry::RegisteredSettings @ 0x098
    //   UGameSettingRegistry::OwningLocalPlayer  @ 0x0A8
    // GetTabs returns the array BY VALUE, so its Data ptr is a COPY - appending there is useless.
    // This dump compares GetTabs.Data vs the live Screen::Tabs.Data vs Registry::TopLevelSettings.Data
    // to decide the clean injection point (SSOT that survives a re-cook). Read-only, SEH-guarded.
    constexpr uintptr_t kOff_Screen_Registry = 0x3A8;
    constexpr uintptr_t kOff_Screen_Tabs     = 0x3B8;
    constexpr uintptr_t kOff_Reg_TopLevel    = 0x88;
    constexpr uintptr_t kOff_Reg_Registered  = 0x98;
    constexpr uintptr_t kOff_Reg_OwningLP    = 0xA8;

    // Native UGameSetting framework-link fields (GameSettings_classes.hpp, SDK-known). A constructed
    // collection needs these copied from a sibling so the framework treats it as a real tab. The
    // DevName (FName) + DisplayName (FText) backing fields are UNNAMED natives in [0x28,0x70) - no
    // UFunction setters exist, so we raw-poke them at offsets located empirically (LocateLabelOffsets).
    constexpr uintptr_t kOff_GS_LocalPlayer    = 0x70;
    constexpr uintptr_t kOff_GS_SettingParent  = 0x78;
    constexpr uintptr_t kOff_GS_OwningRegistry = 0x80;

    // UGameSettingCollection::Settings TArray<UGameSetting*> @0x128 (SDK + recon: tab[0]+0x128 = Num=2).
    // The content panel renders one row per element; a constructed collection starts with this empty.
    constexpr uintptr_t kOff_Coll_Settings = 0x128;

    // UGameSettingAction native members live in Pad_128[0x48] (0x128..0x170, SDK). The FIRST member is
    // the ActionText (FText, 16 bytes) - the caption shown ON the action's BUTTON, distinct from the row
    // label DisplayName. The list-entry widget (GameSettingListEntrySetting_Action::OnSettingAssigned)
    // takes this FText and captions the button with it. Left empty, the button collapses to a thin pill
    // (the "cut off" symptom). No UFunction setter exists (native member), so we raw-poke a fresh FText.
    constexpr uintptr_t kOff_GSAction_Text = 0x128;

    // For cloning a fully engine-initialized action: copy only the native data region (everything past the
    // UObject header) so the fresh clone keeps its own valid vtable/class/name/outer but inherits the
    // engine's Initialize() state. kSize_GSAction = sizeof(UGameSettingAction) (SDK: 0x170).
    constexpr uintptr_t kOff_GS_NativeStart = 0x28;
    constexpr uintptr_t kSize_GSAction      = 0x170;

    struct ArrHdr { void* data; int32_t num; int32_t max; bool ok; };

    void* ReadPtr(const void* p)
    {
        void* v = nullptr;
        __try { v = *reinterpret_cast<void* const*>(p); }
        __except (EXCEPTION_EXECUTE_HANDLER) { v = nullptr; }
        return v;
    }

    ArrHdr ReadArrHdr(const void* p)
    {
        ArrHdr a{ nullptr, 0, 0, false };
        __try
        {
            a.data = *reinterpret_cast<void* const*>(p);
            a.num  = *reinterpret_cast<const int32_t*>(reinterpret_cast<const uint8_t*>(p) + 8);
            a.max  = *reinterpret_cast<const int32_t*>(reinterpret_cast<const uint8_t*>(p) + 12);
            a.ok   = true;
        }
        __except (EXCEPTION_EXECUTE_HANDLER) { a.ok = false; }
        return a;
    }

    void DumpLiveTabBacking(QmUE::UObject* screen, void* getTabsData)
    {
        if (!screen) return;
        const uint8_t* sb = reinterpret_cast<const uint8_t*>(screen);

        QmUE::UObject* registry = reinterpret_cast<QmUE::UObject*>(ReadPtr(sb + kOff_Screen_Registry));
        ArrHdr tabs = ReadArrHdr(sb + kOff_Screen_Tabs);
        char rid[352]; DescribeObject(registry, rid, sizeof(rid));
        QM_LOG_INFO("[ModTab] LIVE backing: Screen::Registry@0x3A8 = 0x%p %s", (void*)registry, rid);
        QM_LOG_INFO("[ModTab]   Screen::Tabs@0x3B8: Data=0x%p Num=%d Max=%d (ok=%d)",
                    tabs.data, tabs.num, tabs.max, tabs.ok ? 1 : 0);

        if (!registry)
        {
            QM_LOG_WARN("[ModTab]   no Registry on screen - cannot read TopLevelSettings (SSOT)");
            return;
        }

        const uint8_t* rb = reinterpret_cast<const uint8_t*>(registry);
        ArrHdr top = ReadArrHdr(rb + kOff_Reg_TopLevel);
        ArrHdr reg = ReadArrHdr(rb + kOff_Reg_Registered);
        QmUE::UObject* lp = reinterpret_cast<QmUE::UObject*>(ReadPtr(rb + kOff_Reg_OwningLP));
        char lpid[352]; DescribeObject(lp, lpid, sizeof(lpid));
        QM_LOG_INFO("[ModTab]   Registry::TopLevelSettings@0x88: Data=0x%p Num=%d Max=%d", top.data, top.num, top.max);
        QM_LOG_INFO("[ModTab]   Registry::RegisteredSettings@0x98: Data=0x%p Num=%d Max=%d", reg.data, reg.num, reg.max);
        QM_LOG_INFO("[ModTab]   Registry::OwningLocalPlayer@0xA8 = 0x%p %s", (void*)lp, lpid);

        // Identity-list TopLevelSettings (cheap: no getter calls -> no ProcessInternal re-entrancy).
        // Confirms TopLevelSettings == the 5 tab collections (GameplayCollection..VideoCollection).
        if (top.ok && top.data && top.num > 0 && top.num <= top.max && top.max <= 4096)
        {
            QmUE::UObject* const* els = reinterpret_cast<QmUE::UObject* const*>(top.data);
            int n = top.num < 12 ? top.num : 12;
            for (int i = 0; i < n; ++i)
            {
                QmUE::UObject* e = nullptr;
                __try { e = els[i]; } __except (EXCEPTION_EXECUTE_HANDLER) { e = nullptr; }
                char eid[352]; DescribeObject(e, eid, sizeof(eid));
                QM_LOG_INFO("[ModTab]   TopLevel[%d] = 0x%p %s", i, (void*)e, eid);
            }
        }

        // The decisive verdict: which array is the live render backing, and which is the SSOT.
        const bool getEqTabs = getTabsData && tabs.ok && getTabsData == tabs.data;
        const bool getEqTop  = getTabsData && top.ok  && getTabsData == top.data;
        const bool tabsEqTop = tabs.ok && top.ok && tabs.data == top.data;
        QM_LOG_INFO("[ModTab]   BACKING VERDICT: GetTabs.Data=0x%p  Screen::Tabs.Data=0x%p  TopLevel.Data=0x%p",
                    getTabsData, tabs.data, top.data);
        QM_LOG_INFO("[ModTab]   -> GetTabs==Screen::Tabs:%d  GetTabs==TopLevel:%d  Screen::Tabs==TopLevel:%d",
                    getEqTabs ? 1 : 0, getEqTop ? 1 : 0, tabsEqTop ? 1 : 0);
        QM_LOG_INFO("[ModTab]   -> INJECT POINT: %s",
            tabsEqTop
                ? "Screen::Tabs aliases TopLevel -> append to Registry::TopLevelSettings (SSOT) + force re-cook"
                : "Screen::Tabs is a SEPARATE list -> append to BOTH Screen::Tabs and Registry::TopLevelSettings, then re-cook");
    }

    // In-place append `dupPtr` to the TArray<T*> whose header is at `arrHdrAddr`, ONLY if there is
    // spare capacity (Num < Max) so no realloc is needed. Writes the element into the reserved slot
    // first, then publishes the bumped Num. Returns the new Num, or -1 if skipped/faulted.
    int32_t AppendDupToArray(void* arrHdrAddr, void* dupPtr, const char* tag)
    {
        int32_t result = -1;
        __try
        {
            void**   dataPP = reinterpret_cast<void**>(arrHdrAddr);
            int32_t* numP   = reinterpret_cast<int32_t*>(reinterpret_cast<uint8_t*>(arrHdrAddr) + 8);
            int32_t* maxP   = reinterpret_cast<int32_t*>(reinterpret_cast<uint8_t*>(arrHdrAddr) + 12);
            void*    data   = *dataPP;
            int32_t  num    = *numP;
            int32_t  max    = *maxP;
            if (!data || num < 0 || num >= max || max > 4096)
            {
                QM_LOG_WARN("[ModTab]   inject: %s has no spare slot (Num=%d Max=%d) - skipping (would realloc)",
                            tag, num, max);
                return -1;
            }
            void** slots = reinterpret_cast<void**>(data);
            slots[num] = dupPtr;        // fill the reserved slot first
            _ReadWriteBarrier();
            *numP = num + 1;            // then publish the new count
            result = num + 1;
            QM_LOG_INFO("[ModTab]   inject: %s appended dup 0x%p at index %d -> Num now %d (Max=%d, no realloc)",
                        tag, dupPtr, num, result, max);
        }
        __except (EXCEPTION_EXECUTE_HANDLER)
        {
            QM_LOG_WARN("[ModTab]   inject: %s append FAULTED", tag);
            result = -1;
        }
        return result;
    }

    // Call a BlueprintCallable function on an object to drive a UI refresh. The call re-enters the rider
    // (BP funcs route through ProcessInternal), but the inject latch is already claimed so that is
    // harmless. SEH-guarded inside CallProcessEvent. Returns true if dispatched.
    //
    // onlyIfParameterless: when true, dispatch ONLY if the real UFunction::ParmsSize is 0. A function
    // with parameters (e.g. CookTabs may take a tab array) would receive our zeroed buffer = an EMPTY
    // array, which could rebuild the bar with 0 tabs (clobber) or fault. In that case we log the real
    // size and skip - learning the param layout without risk.
    bool CallNavRefresh(QmUE::UObject* obj, const char* fnName, bool onlyIfParameterless = false)
    {
        if (!obj || !obj->Class) return false;
        QmUE::UFunction* fn = QmUE::FindFunctionOnClass(obj->Class, fnName);
        if (!fn)
        {
            QM_LOG_WARN("[ModTab]   inject: fn '%s' not found - skipping", fnName);
            return false;
        }
        int32_t sz = ParmsSize(fn);
        uint8_t buf[64];
        if (sz < 0 || sz > (int32_t)sizeof(buf))
        {
            QM_LOG_WARN("[ModTab]   inject: fn '%s' parms size %d out of range - skipping", fnName, sz);
            return false;
        }
        if (onlyIfParameterless && sz != 0)
        {
            QM_LOG_WARN("[ModTab]   inject: fn '%s' has parmsSize=%d (expects args) - NOT calling blindly "
                        "with a zeroed buffer", fnName, sz);
            return false;
        }
        memset(buf, 0, sizeof(buf));
        bool ok = QmUE::CallProcessEvent(obj, fn, buf);
        QM_LOG_INFO("[ModTab]   inject: called '%s' (parmsSize=%d) ok=%d", fnName, sz, ok ? 1 : 0);
        return ok;
    }

    // Locate the unnamed DevName (FName) + DisplayName (FText) native fields inside a known source
    // collection by matching what its getters return: GetDevName -> FName, GetDisplayName -> the
    // FText's TextData pointer. Scans the native member region [0x28,0x70). Returns true with both
    // offsets set. Empirical (not guessed) so the later raw-poke writes to verified addresses.
    bool LocateLabelOffsets(QmUE::UObject* srcColl, uintptr_t* devOff, uintptr_t* dispOff)
    {
        *devOff = 0; *dispOff = 0;
        if (!srcColl || !srcColl->Class) return false;

        QmUE::FName srcDev = { 0, 0 };
        if (QmUE::UFunction* devFn = QmUE::FindFunctionOnClass(srcColl->Class, "GetDevName"))
        {
            uint8_t pb[16]; memset(pb, 0, sizeof(pb));
            if (QmUE::CallProcessEvent(srcColl, devFn, pb))
                __try { srcDev = *reinterpret_cast<const QmUE::FName*>(pb); }
                __except (EXCEPTION_EXECUTE_HANDLER) { srcDev = { 0, 0 }; }
        }
        void* srcTextData = nullptr;
        if (QmUE::UFunction* dnFn = QmUE::FindFunctionOnClass(srcColl->Class, "GetDisplayName"))
        {
            uint8_t pb[16]; memset(pb, 0, sizeof(pb));
            if (QmUE::CallProcessEvent(srcColl, dnFn, pb))
                __try { srcTextData = *reinterpret_cast<void* const*>(pb); }
                __except (EXCEPTION_EXECUTE_HANDLER) { srcTextData = nullptr; }
        }
        QM_LOG_INFO("[ModTab]   build: src getters -> DevName{ci=%d num=%d} DisplayName.TextData=0x%p",
                    srcDev.ComparisonIndex, srcDev.Number, srcTextData);
        if (srcDev.ComparisonIndex == 0 && !srcTextData)
        {
            QM_LOG_WARN("[ModTab]   build: getters returned nothing usable (devIdx=%d textData=0x%p)",
                        srcDev.ComparisonIndex, srcTextData);
            return false;
        }

        // Scan the WHOLE native member region [0x28,0x128). The SDK splits it into two Dumper-7 pad
        // windows (Pad_28 @0x28..0x70 and Pad_88 @0x88..0x128) around the named link pointers; the
        // unnamed DevName/DisplayName live in one of them - recon #15 showed NOT in the first, so we
        // must also cover the second. Hexdump first so the exact offsets are readable if a match misses.
        const uint8_t* base = reinterpret_cast<const uint8_t*>(srcColl);
        __try
        {
            HexDump("collHead", base + 0x28, 0x100);
            for (uintptr_t o = 0x28; o + 8 <= 0x128; o += 4)
            {
                if (*devOff == 0 && srcDev.ComparisonIndex != 0)
                {
                    int32_t ci = *reinterpret_cast<const int32_t*>(base + o);
                    int32_t nm = *reinterpret_cast<const int32_t*>(base + o + 4);
                    if (ci == srcDev.ComparisonIndex && nm == srcDev.Number)
                    {
                        *devOff = o;
                        QM_LOG_INFO("[ModTab]   build: DevName FName located @ +0x%llx (idx=%d num=%d)",
                                    (unsigned long long)o, ci, nm);
                    }
                }
                if (*dispOff == 0 && srcTextData && (o % 8) == 0)
                {
                    void* p = *reinterpret_cast<void* const*>(base + o);
                    if (p == srcTextData)
                    {
                        *dispOff = o;
                        QM_LOG_INFO("[ModTab]   build: DisplayName FText located @ +0x%llx (TextData=0x%p)",
                                    (unsigned long long)o, p);
                    }
                }
            }
        }
        __except (EXCEPTION_EXECUTE_HANDLER)
        {
            QM_LOG_WARN("[ModTab]   build: offset scan FAULTED");
            return false;
        }
        if (*devOff == 0 || *dispOff == 0)
            QM_LOG_WARN("[ModTab]   build: offsets incomplete (devOff=0x%llx dispOff=0x%llx)",
                        (unsigned long long)*devOff, (unsigned long long)*dispOff);
        return (*devOff != 0) && (*dispOff != 0);
    }

    // Build a single visible TEST control for the Quartermaster tab: a UGameSettingAction (renders as
    // a button in the content panel). SpawnObject runs the class's real default-construction, so all
    // base fields (edit-condition arrays, FTexts, flags) are valid-empty - we only override the labels
    // + framework links. The native DoAction delegate stays unset: the button is visible and clickable
    // but runs no logic yet (the chosen smallest step). devOff/dispOff are the base UGameSetting label
    // offsets already located by the caller (UGameSettingAction shares the same UGameSetting base).
    QmUE::UObject* BuildTestActionSetting(QmUE::UObject* registry, QmUE::UObject* parentColl,
                                          QmUE::UObject* srcColl, uintptr_t devOff, uintptr_t dispOff,
                                          QmUE::UObject** outTemplate)
    {
        if (outTemplate) *outTemplate = nullptr;
        QmUE::UClass* actionClass = QmUE::FindClassByName("GameSettingAction");
        if (!actionClass) { QM_LOG_WARN("[ModTab]   build: class 'GameSettingAction' not found"); return nullptr; }

        QmUE::UObject* obj = QmUE::SpawnObjectViaUFunction(actionClass, registry ? registry : parentColl);
        if (!obj) { QM_LOG_WARN("[ModTab]   build: SpawnObject(GameSettingAction) returned null"); return nullptr; }
        char nid[352]; DescribeObject(obj, nid, sizeof(nid));
        QM_LOG_INFO("[ModTab]   build: constructed test action %s", nid);

        QmUE::FName devName = { 0, 0 };
        if (!QmUE::FNameFromString(L"QuartermasterTestAction", &devName))
        { QM_LOG_WARN("[ModTab]   build: action FNameFromString failed"); return nullptr; }
        uint8_t dispText[16]; memset(dispText, 0, sizeof(dispText));
        if (!QmUE::TextFromString(L"Test Button", dispText))
        { QM_LOG_WARN("[ModTab]   build: action TextFromString failed"); return nullptr; }
        // The button caption (ActionText) - distinct from the row label above. Empty = thin-pill button.
        uint8_t actText[16]; memset(actText, 0, sizeof(actText));
        bool haveActText = QmUE::TextFromString(L"Run", actText);
        if (!haveActText) QM_LOG_WARN("[ModTab]   build: action ActionText TextFromString failed - button may stay thin");

        __try
        {
            uint8_t*       nb  = reinterpret_cast<uint8_t*>(obj);
            const uint8_t* sb2 = reinterpret_cast<const uint8_t*>(srcColl);
            *reinterpret_cast<QmUE::FName*>(nb + devOff) = devName;
            memcpy(nb + dispOff, dispText, 16);
            if (haveActText) memcpy(nb + kOff_GSAction_Text, actText, 16);   // button caption @0x128
            *reinterpret_cast<void**>(nb + kOff_GS_LocalPlayer)    = *reinterpret_cast<void* const*>(sb2 + kOff_GS_LocalPlayer);
            *reinterpret_cast<void**>(nb + kOff_GS_SettingParent)  = parentColl;   // child of our collection
            *reinterpret_cast<void**>(nb + kOff_GS_OwningRegistry) = registry;
        }
        __except (EXCEPTION_EXECUTE_HANDLER)
        {
            QM_LOG_WARN("[ModTab]   build: action field poke FAULTED");
            return nullptr;
        }

        char chk[160] = { 0 };
        if (QmUE::UFunction* dnFn = QmUE::FindFunctionOnClass(obj->Class, "GetDisplayName"))
        {
            uint8_t pb[16]; memset(pb, 0, sizeof(pb));
            if (QmUE::CallProcessEvent(obj, dnFn, pb)) ReadFTextNarrow(pb, chk, sizeof(chk));
        }
        QM_LOG_INFO("[ModTab]   build: test action label readback='%s'", chk[0] ? chk : "<empty>");

        // Recon safety net: hexdump OUR action's native block [0x128,0x170) (so the ActionText poke is
        // visible) and, if the game has any real UGameSettingAction registered, hexdump ITS block too -
        // a side-by-side diff pins ActionText + any other native field the entry widget needs. Read-only
        // on the template. Walk Registry::RegisteredSettings (all settings across every collection).
        // Recon #17: also dump the BASE native region [0x28,0x128) - that is where the native Initialize()/
        // OnInitialized() pass sets bReady/edit-condition/Description state. A side-by-side diff against the
        // engine template pins exactly which native fields a from-scratch object is missing (the cause of
        // the collapsed thin pill), so we can replicate the init directly instead of memcpy'ing a template.
        __try { HexDump("our-action[0x28]", reinterpret_cast<const uint8_t*>(obj) + 0x28, 0x100); }
        __except (EXCEPTION_EXECUTE_HANDLER) {}
        __try { HexDump("our-action[0x128]", reinterpret_cast<const uint8_t*>(obj) + 0x128, 0x48); }
        __except (EXCEPTION_EXECUTE_HANDLER) {}
        if (registry)
        {
            ArrHdr reg = ReadArrHdr(reinterpret_cast<const uint8_t*>(registry) + kOff_Reg_Registered);
            if (reg.ok && reg.data && reg.num > 0 && reg.num <= reg.max && reg.max <= 8192)
            {
                QmUE::UObject* const* els = reinterpret_cast<QmUE::UObject* const*>(reg.data);
                bool found = false;
                for (int i = 0; i < reg.num && !found; ++i)
                {
                    QmUE::UObject* e = nullptr;
                    __try { e = els[i]; } __except (EXCEPTION_EXECUTE_HANDLER) { e = nullptr; }
                    if (!e || e == obj) continue;
                    char eid[352]; DescribeObject(e, eid, sizeof(eid));
                    if (!ContainsLc(eid, "gamesettingaction")) continue;
                    QM_LOG_INFO("[ModTab]   build: real action template = 0x%p %s", (void*)e, eid);
                    __try { HexDump("tmpl-action[0x28]", reinterpret_cast<const uint8_t*>(e) + 0x28, 0x100); }
                    __except (EXCEPTION_EXECUTE_HANDLER) {}
                    __try { HexDump("tmpl-action[0x128]", reinterpret_cast<const uint8_t*>(e) + 0x128, 0x48); }
                    __except (EXCEPTION_EXECUTE_HANDLER) {}
                    if (outTemplate) *outTemplate = e;
                    found = true;
                }
                if (!found)
                    QM_LOG_INFO("[ModTab]   build: no real UGameSettingAction in RegisteredSettings (Num=%d) - "
                                "ActionText@0x128 is a best-guess offset", reg.num);
            }
        }
        return obj;
    }

    // Point an (empty) collection's Settings TArray @0x128 at a fresh backing store holding `kids[0..n)`.
    // The collection starts Num=0/Max=0/Data=null, so an in-place append is impossible - we allocate the
    // backing through the ENGINE allocator (FMallocBinned2, canary-correct) so a later engine realloc/free
    // of this array does not trip the canary check. Returns false (tab stays empty, no crash) if the
    // engine allocator is unresolved or the poke faults.
    bool SetCollectionChildren(QmUE::UObject* coll, void* const* kids, int n)
    {
        if (!coll || !kids || n <= 0) return false;
        if (!QmAlloc::IsInnerMallocResolved())
        {
            QM_LOG_WARN("[ModTab]   build: engine allocator unresolved - cannot back the Settings array "
                        "(tab stays empty)");
            return false;
        }
        const int32_t cap = (n < 4) ? 4 : n;
        void* buf = QmAlloc::InnerMalloc((size_t)cap * sizeof(void*), 16);
        if (!buf) { QM_LOG_WARN("[ModTab]   build: InnerMalloc for Settings backing failed"); return false; }

        bool ok = false;
        __try
        {
            void** slots = reinterpret_cast<void**>(buf);
            for (int i = 0; i < cap; ++i) slots[i] = (i < n) ? kids[i] : nullptr;
            _ReadWriteBarrier();
            uint8_t* arr = reinterpret_cast<uint8_t*>(coll) + kOff_Coll_Settings;
            *reinterpret_cast<void**>(arr)        = buf;   // Data
            *reinterpret_cast<int32_t*>(arr + 8)  = n;     // Num
            *reinterpret_cast<int32_t*>(arr + 12) = cap;   // Max
            ok = true;
        }
        __except (EXCEPTION_EXECUTE_HANDLER)
        {
            QM_LOG_WARN("[ModTab]   build: Settings array poke FAULTED");
            return false;
        }
        QM_LOG_INFO("[ModTab]   build: Settings backed @+0x%llx -> Num=%d Max=%d (engine buf 0x%p)",
                    (unsigned long long)kOff_Coll_Settings, n, cap, buf);
        return ok;
    }

    // Recon #16: clone a fully engine-initialized UGameSettingAction into a fresh object we can host under
    // OUR collection - WITHOUT mutating the live original (still owned by its source collection, still
    // shown in the real settings menu). Last run we appended the template by raw pointer; it never rendered
    // because it keeps its original SettingParent and the panel's parent-based filter rejects it under our
    // collection. SpawnObject gives a header-valid GameSettingAction; we memcpy only the native data region
    // [0x28,0x170) (past the UObject header) to inherit the engine's native Initialize() state, then
    // retarget the framework links to our collection so the filter accepts it, and repoint DisplayName so
    // the row is visually distinct from row 0. The copied native pointers (FTexts, edit-condition arrays)
    // are shared read-only with the original - safe for a transient open->look->close run (settings are
    // never applied here, so no realloc/free races). Diagnostic only. Returns the clone or nullptr.
    QmUE::UObject* CloneActionRetargeted(QmUE::UObject* tmpl, QmUE::UObject* registry,
                                         QmUE::UObject* parentColl, uintptr_t dispOff)
    {
        if (!tmpl) return nullptr;
        QmUE::UClass* actionClass = QmUE::FindClassByName("GameSettingAction");
        if (!actionClass) { QM_LOG_WARN("[ModTab]   build: clone - class 'GameSettingAction' not found"); return nullptr; }
        QmUE::UObject* clone = QmUE::SpawnObjectViaUFunction(actionClass, registry ? registry : parentColl);
        if (!clone) { QM_LOG_WARN("[ModTab]   build: clone - SpawnObject returned null"); return nullptr; }

        uint8_t dispText[16]; memset(dispText, 0, sizeof(dispText));
        bool haveDisp = QmUE::TextFromString(L"Engine Clone", dispText);

        __try
        {
            uint8_t*       db = reinterpret_cast<uint8_t*>(clone);
            const uint8_t* sb = reinterpret_cast<const uint8_t*>(tmpl);
            memcpy(db + kOff_GS_NativeStart, sb + kOff_GS_NativeStart, kSize_GSAction - kOff_GS_NativeStart);
            // Retarget so the panel's parent-based filter places it under the Quartermaster collection.
            *reinterpret_cast<void**>(db + kOff_GS_SettingParent)  = parentColl;
            *reinterpret_cast<void**>(db + kOff_GS_OwningRegistry) = registry;
            if (haveDisp && dispOff) memcpy(db + dispOff, dispText, 16);   // distinct row label
        }
        __except (EXCEPTION_EXECUTE_HANDLER)
        {
            QM_LOG_WARN("[ModTab]   build: clone - native copy/retarget FAULTED");
            return nullptr;
        }

        char cid[352]; DescribeObject(clone, cid, sizeof(cid));
        QM_LOG_INFO("[ModTab]   build: cloned engine action -> %s (parent retargeted to our collection)", cid);
        return clone;
    }

    // Construct a REAL "Quartermaster" UGameSettingCollection (own DevName + DisplayName) instead of
    // cloning a sibling pointer. Returns the initialized collection, or nullptr on any failure (caller
    // then falls back to the proven dup-append). srcColl is a live sibling we read offsets + links from.
    QmUE::UObject* BuildQuartermasterCollection(QmUE::UObject* registry, QmUE::UObject* srcColl)
    {
        if (!srcColl) return nullptr;

        uintptr_t devOff = 0, dispOff = 0;
        if (!LocateLabelOffsets(srcColl, &devOff, &dispOff))
        {
            QM_LOG_WARN("[ModTab]   build: could not locate label offsets - using dup instead");
            return nullptr;
        }

        QmUE::UClass* collClass = QmUE::FindClassByName("GameSettingCollection");
        if (!collClass) { QM_LOG_WARN("[ModTab]   build: class 'GameSettingCollection' not found"); return nullptr; }

        QmUE::UObject* obj = QmUE::SpawnObjectViaUFunction(collClass, registry ? registry : srcColl);
        if (!obj) { QM_LOG_WARN("[ModTab]   build: SpawnObject(GameSettingCollection) returned null"); return nullptr; }
        char nid[352]; DescribeObject(obj, nid, sizeof(nid));
        QM_LOG_INFO("[ModTab]   build: constructed %s", nid);

        QmUE::FName devName = { 0, 0 };
        if (!QmUE::FNameFromString(L"QuartermasterCollection", &devName))
        { QM_LOG_WARN("[ModTab]   build: FNameFromString failed"); return nullptr; }
        uint8_t dispText[16]; memset(dispText, 0, sizeof(dispText));
        if (!QmUE::TextFromString(L"Quartermaster", dispText))
        { QM_LOG_WARN("[ModTab]   build: TextFromString failed"); return nullptr; }

        __try
        {
            uint8_t*       nb  = reinterpret_cast<uint8_t*>(obj);
            const uint8_t* sb2 = reinterpret_cast<const uint8_t*>(srcColl);
            *reinterpret_cast<QmUE::FName*>(nb + devOff) = devName;
            memcpy(nb + dispOff, dispText, 16);
            // Framework links copied from the sibling (SDK offsets) so the panel treats it as a real tab.
            *reinterpret_cast<void**>(nb + kOff_GS_LocalPlayer)    = *reinterpret_cast<void* const*>(sb2 + kOff_GS_LocalPlayer);
            *reinterpret_cast<void**>(nb + kOff_GS_SettingParent)  = *reinterpret_cast<void* const*>(sb2 + kOff_GS_SettingParent);
            *reinterpret_cast<void**>(nb + kOff_GS_OwningRegistry) = *reinterpret_cast<void* const*>(sb2 + kOff_GS_OwningRegistry);
        }
        __except (EXCEPTION_EXECUTE_HANDLER)
        {
            QM_LOG_WARN("[ModTab]   build: field poke FAULTED");
            return nullptr;
        }

        // Verify: read the label back through the getter (inline FText reads cleanly, unlike the
        // localized stock labels which returned '???').
        char chk[160] = { 0 };
        if (QmUE::UFunction* dnFn = QmUE::FindFunctionOnClass(obj->Class, "GetDisplayName"))
        {
            uint8_t pb[16]; memset(pb, 0, sizeof(pb));
            if (QmUE::CallProcessEvent(obj, dnFn, pb)) ReadFTextNarrow(pb, chk, sizeof(chk));
        }
        QM_LOG_INFO("[ModTab]   build: poked DevName@+0x%llx + DisplayName@+0x%llx -> readback label='%s'",
                    (unsigned long long)devOff, (unsigned long long)dispOff, chk[0] ? chk : "<empty>");

        // CONTENT PIVOT (Round 3): the hand-built UGameSettingAction child path is DEAD. It collapses to a
        // thin pill (missing native Initialize - three null heap structures vs the engine template) and,
        // worse, AVs the game when the native panel rebuilds this collection's rows on tab activation
        // (confirmed: Quartermaster tab-click -> READ at 0xFFFF...FFFF). We now build content the way the
        // reference mod (R5ModSettings) does: our OWN widget panel via the View-Path (CreateWidget the native
        // WBP_Settings_Entry* blueprints, mounted in ProbeViewPath) - NOT UGameSetting children. So this
        // collection stays EMPTY: it exists only to own the named "Quartermaster" tab; its content is rendered
        // by our own mounted ScrollBox. (BuildTestActionSetting/CloneActionRetargeted/SetCollectionChildren
        // are retained as recon references, now unused; devOff/dispOff/srcColl still feed the tab label above.)
        (void)srcColl;

        return obj;
    }

    // ===================== VIEW-PATH RECON (#18a, read-only + one unmounted create) =====================
    // The reference mod (R5ModSettings) sidesteps the UGameSetting data layer entirely: it builds its own
    // ScrollBox panel and CreateWidget()s the game's WBP_Settings_Entry* blueprints into it (AddChild), so
    // each row renders itself - no native UGameSetting::Initialize needed (that native pass is exactly what
    // collapses our hand-built action into the thin pill). This probe:
    //   (1) maps the live settings-screen widget tree (screen->WidgetTree->RootWidget, walked via the
    //       reflected GetChildrenCount/GetChildAt) and captures the content VerticalBox as the mount point;
    //   (2) resolves the reflected primitives (WidgetBlueprintLibrary::Create, PanelWidget::AddChild,
    //       Widget::GetOwningPlayer);
    //   (3) CREATES one WBP_Settings_EntrySwitcher_C via Create();
    //   (4) MOUNTS it into the captured VerticalBox (AddChild) - decisive proof a native entry renders
    //       itself without UGameSetting::Initialize. SEH-guarded throughout.

    // Exact param layouts (SDK Dumper7 UMG_parameters.hpp). Buffers may be oversized; ProcessEvent only
    // touches the function's own properties, so trailing pad is harmless.
    struct P_Create          { void* WorldContextObject; void* WidgetType; void* OwningPlayer; void* ReturnValue; };
    struct P_GetChildCount   { int32_t ReturnValue; int32_t _pad; };
    struct P_GetChildAt      { int32_t Index; int32_t _pad; void* ReturnValue; };
    struct P_GetOwningPlayer { void* ReturnValue; };
    struct P_AddChild        { void* Content; void* ReturnValue; };
    struct P_SetVisibility   { uint8_t InVisibility; uint8_t _pad[7]; };
    struct P_GetVisibility   { uint8_t ReturnValue; uint8_t _pad[7]; };   // UWidget::GetVisibility -> ESlateVisibility
    struct P_GetParent       { void* ReturnValue; };                       // UWidget::GetParent -> UPanelWidget*

    // ESlateVisibility (SDK UMG enums) - the single byte arg to UWidget::SetVisibility.
    enum : uint8_t { ESV_Visible = 0, ESV_Collapsed = 1 };

    constexpr uintptr_t kOff_UserWidget_WidgetTree = 0x2D8;   // UUserWidget::WidgetTree  (SDK UMG_classes)
    constexpr uintptr_t kOff_WidgetTree_RootWidget = 0x30;    // UWidgetTree::RootWidget  (SDK UMG_classes)
    constexpr const char* kEntrySwitcherClass = "WBP_Settings_EntrySwitcher_C";
    // #18i: the game's own themed button widget (UUserWidget: tiled art + a txt_Name TextBlock + a
    // SetData(FText) label setter + an OnClick delegate). Built via WidgetBlueprintLibrary::Create like the
    // EntrySwitcher; matches the native menu styling instead of a raw unstyled UButton.
    constexpr const char* kArtButtonClass     = "WBP_ArtButton_TiledText_C";

    // Recursively log a widget subtree. A node whose class has no GetChildrenCount (i.e. not a UPanelWidget)
    // is a leaf and stops the recursion. Depth- and budget-capped so a deep tree can't flood the log.
    // Up to two independent FIRST-match captures: if captureMatchN is set, the first node whose
    // "Class'Name'" description contains it (case-insensitive) is stored in *outMatchN - lets one walk grab
    // several live targets (e.g. the chrome VerticalBox AND the per-tab Settings_Panel).
    void DumpWidgetSubtree(QmUE::UObject* widget, int depth, int& budget,
                           const char* captureMatch, QmUE::UObject** outMatch,
                           const char* captureMatch2 = nullptr, QmUE::UObject** outMatch2 = nullptr)
    {
        if (!widget || depth > 8 || budget <= 0) return;
        char wid[352]; DescribeObject(widget, wid, sizeof(wid));
        char indent[33]; int sp = depth * 2; if (sp > 32) sp = 32;
        memset(indent, ' ', sp); indent[sp] = '\0';
        QM_LOG_INFO("[ModTab]   tree %s%s", indent, wid);
        --budget;
        if (captureMatch  && outMatch  && !*outMatch  && ContainsLc(wid, captureMatch))  *outMatch  = widget;
        if (captureMatch2 && outMatch2 && !*outMatch2 && ContainsLc(wid, captureMatch2)) *outMatch2 = widget;

        QmUE::UFunction* fnCount = QmUE::FindFunctionOnClass(widget->Class, "GetChildrenCount");
        QmUE::UFunction* fnAt    = QmUE::FindFunctionOnClass(widget->Class, "GetChildAt");
        if (!fnCount || !fnAt) return;   // not a panel -> leaf
        P_GetChildCount cc; cc.ReturnValue = 0; cc._pad = 0;
        if (!QmUE::CallProcessEvent(widget, fnCount, &cc)) return;
        int n = cc.ReturnValue;
        if (n <= 0 || n > 256) return;
        for (int i = 0; i < n && budget > 0; ++i)
        {
            P_GetChildAt ga; ga.Index = i; ga._pad = 0; ga.ReturnValue = nullptr;
            if (!QmUE::CallProcessEvent(widget, fnAt, &ga) || !ga.ReturnValue) continue;
            DumpWidgetSubtree(reinterpret_cast<QmUE::UObject*>(ga.ReturnValue), depth + 1, budget,
                              captureMatch, outMatch, captureMatch2, outMatch2);
        }
    }

    // #18d: silently walk a widget subtree (no per-node logging, unlike DumpWidgetSubtree) and return the
    // LAST descendant whose "Class'Name'" contains `classSub` (case-insensitive). Depth/budget capped.
    // DFS visits siblings in order, so the last match returned is the last sibling of that class - exactly
    // our injected tab (last data entry -> last tab widget). Safe to call per click (rare event).
    QmUE::UObject* CollectLastMatch(QmUE::UObject* widget, const char* classSub, int depth, int& budget)
    {
        if (!widget || depth > 8 || budget <= 0 || !widget->Class) return nullptr;
        --budget;
        QmUE::UObject* found = nullptr;
        char wid[352]; DescribeObject(widget, wid, sizeof(wid));
        if (ContainsLc(wid, classSub)) found = widget;   // tentative; a later sibling/descendant overwrites

        QmUE::UFunction* fnCount = QmUE::FindFunctionOnClass(widget->Class, "GetChildrenCount");
        QmUE::UFunction* fnAt    = QmUE::FindFunctionOnClass(widget->Class, "GetChildAt");
        if (fnCount && fnAt)
        {
            P_GetChildCount cc; cc.ReturnValue = 0; cc._pad = 0;
            if (QmUE::CallProcessEvent(widget, fnCount, &cc) && cc.ReturnValue > 0 && cc.ReturnValue <= 256)
            {
                int n = cc.ReturnValue;
                for (int i = 0; i < n && budget > 0; ++i)
                {
                    P_GetChildAt ga; ga.Index = i; ga._pad = 0; ga.ReturnValue = nullptr;
                    if (!QmUE::CallProcessEvent(widget, fnAt, &ga) || !ga.ReturnValue) continue;
                    QmUE::UObject* sub = CollectLastMatch(reinterpret_cast<QmUE::UObject*>(ga.ReturnValue),
                                                          classSub, depth + 1, budget);
                    if (sub) found = sub;   // deeper/later match wins -> ends on the last tab in the bar
                }
            }
        }
        return found;
    }

    // #18m: like CollectLastMatch but returns the FIRST matching descendant (pre-order). Used to pick a
    // non-QM tab (tab 0) to drive the native content Slate rebuild on reopen without selecting our tab.
    QmUE::UObject* CollectFirstMatch(QmUE::UObject* widget, const char* classSub, int depth, int& budget)
    {
        if (!widget || depth > 8 || budget <= 0 || !widget->Class) return nullptr;
        --budget;
        char wid[352]; DescribeObject(widget, wid, sizeof(wid));
        if (ContainsLc(wid, classSub)) return widget;   // first hit wins (pre-order)

        QmUE::UFunction* fnCount = QmUE::FindFunctionOnClass(widget->Class, "GetChildrenCount");
        QmUE::UFunction* fnAt    = QmUE::FindFunctionOnClass(widget->Class, "GetChildAt");
        if (fnCount && fnAt)
        {
            P_GetChildCount cc; cc.ReturnValue = 0; cc._pad = 0;
            if (QmUE::CallProcessEvent(widget, fnCount, &cc) && cc.ReturnValue > 0 && cc.ReturnValue <= 256)
            {
                int n = cc.ReturnValue;
                for (int i = 0; i < n && budget > 0; ++i)
                {
                    P_GetChildAt ga; ga.Index = i; ga._pad = 0; ga.ReturnValue = nullptr;
                    if (!QmUE::CallProcessEvent(widget, fnAt, &ga) || !ga.ReturnValue) continue;
                    QmUE::UObject* sub = CollectFirstMatch(reinterpret_cast<QmUE::UObject*>(ga.ReturnValue),
                                                           classSub, depth + 1, budget);
                    if (sub) return sub;   // earliest descendant wins
                }
            }
        }
        return nullptr;
    }

    // #18j/#18m: RootWidget of the LIVE tab bar - the group latched from its most recent Construct (so a
    // reopen's re-cooked bar wins over the lingering un-GC'd one), else the global lookup before the first
    // Construct is observed. The latched pointer may have been freed if a click races a teardown, so it is
    // SEH-validated before use. Shared by the gate's QM-tab resolver and the reopen Slate-rebuild trigger.
    QmUE::UObject* ResolveLiveTabBarRoot()
    {
        QmUE::UObject* grp = nullptr;
        if (g_liveTabsGroup)
        {
            QmUE::UObject* cand = reinterpret_cast<QmUE::UObject*>(g_liveTabsGroup);
            __try { if (cand->Class) grp = cand; }
            __except (EXCEPTION_EXECUTE_HANDLER) { grp = nullptr; }
        }
        if (!grp) grp = QmUE::FindFirstInstanceOfClass(kTabsGroupClass);
        if (!grp || !grp->Class) return nullptr;
        QmUE::UObject* root = nullptr;
        __try
        {
            QmUE::UObject* wt = reinterpret_cast<QmUE::UObject*>(
                *reinterpret_cast<void* const*>(reinterpret_cast<const uint8_t*>(grp) + kOff_UserWidget_WidgetTree));
            if (wt) root = reinterpret_cast<QmUE::UObject*>(
                *reinterpret_cast<void* const*>(reinterpret_cast<const uint8_t*>(wt) + kOff_WidgetTree_RootWidget));
        }
        __except (EXCEPTION_EXECUTE_HANDLER) { root = nullptr; }
        return root;
    }

    // #18d: resolve our Quartermaster tab widget = the LAST WBP_MetaUI_Tab_Main_C in the live TabsGroup
    // bar (we inject our collection last, so our tab is the last leaf). Read-only, SEH-guarded, silent.
    // Returns nullptr if the bar isn't built yet.
    QmUE::UObject* ResolveOurTabWidget()
    {
        QmUE::UObject* root = ResolveLiveTabBarRoot();
        if (!root) return nullptr;
        QmUE::UObject* last = nullptr;
        int budget = 250;
        __try { last = CollectLastMatch(root, "tab_main", 0, budget); }
        __except (EXCEPTION_EXECUTE_HANDLER) { last = nullptr; }
        return last;
    }

    // #18m: the FIRST live tab_main (tab 0, a native tab). Sim-clicking it on reopen forces the native
    // content Slate rebuild that realizes our re-mounted panel, WITHOUT switching the active tab to
    // Quartermaster (our tab is the LAST leaf, so the first leaf is always a native tab).
    QmUE::UObject* ResolveFirstLiveTab()
    {
        QmUE::UObject* root = ResolveLiveTabBarRoot();
        if (!root) return nullptr;
        QmUE::UObject* first = nullptr;
        int budget = 250;
        __try { first = CollectFirstMatch(root, "tab_main", 0, budget); }
        __except (EXCEPTION_EXECUTE_HANDLER) { first = nullptr; }
        return first;
    }

    // #18e: set a widget's ESlateVisibility via the reflected UWidget::SetVisibility(ESlateVisibility).
    // Returns true if dispatched. CallProcessEvent is itself SEH-guarded. Drives the click-keyed gate.
    bool SetWidgetVisibility(QmUE::UObject* widget, uint8_t vis)
    {
        if (!widget || !widget->Class) return false;
        QmUE::UFunction* fn = QmUE::FindFunctionOnClass(widget->Class, "SetVisibility");
        if (!fn) return false;
        P_SetVisibility p; memset(&p, 0, sizeof(p)); p.InVisibility = vis;
        return QmUE::CallProcessEvent(widget, fn, &p);
    }

    // #18k READBACK: actual ESlateVisibility currently on the widget (not just "did SetVisibility dispatch").
    // Returns -1 when unreadable. SEH-guarded.
    int GetWidgetVisibility(QmUE::UObject* widget)
    {
        if (!widget) return -1;
        int r = -1;
        __try
        {
            if (!widget->Class) return -1;
            QmUE::UFunction* fn = QmUE::FindFunctionOnClass(widget->Class, "GetVisibility");
            if (!fn) return -1;
            P_GetVisibility p; memset(&p, 0, sizeof(p));
            if (QmUE::CallProcessEvent(widget, fn, &p)) r = (int)p.ReturnValue;
        }
        __except (EXCEPTION_EXECUTE_HANDLER) { r = -1; }
        return r;
    }

    // #18k READBACK: the widget's current parent UPanelWidget (null when detached/orphaned). SEH-guarded.
    void* GetWidgetParent(QmUE::UObject* widget)
    {
        if (!widget) return nullptr;
        void* r = nullptr;
        __try
        {
            if (!widget->Class) return nullptr;
            QmUE::UFunction* fn = QmUE::FindFunctionOnClass(widget->Class, "GetParent");
            if (!fn) return nullptr;
            P_GetParent p; memset(&p, 0, sizeof(p));
            if (QmUE::CallProcessEvent(widget, fn, &p)) r = p.ReturnValue;
        }
        __except (EXCEPTION_EXECUTE_HANDLER) { r = nullptr; }
        return r;
    }

    void ProbeViewPath(QmUE::UObject* screen)
    {
        if (!screen) return;
        QM_LOG_WARN("[ModTab] *** VIEW-PATH #18e *** map screen tree + build our own ScrollBox as a sibling of "
                    "Settings_Panel, fill it, mount it into the content VerticalBox, start it hidden "
                    "(visibility gated by #18d tab click)");

        // (1) Map the live widget tree (read-only).
        QmUE::UObject* widgetTree = nullptr;
        QmUE::UObject* rootWidget = nullptr;
        __try
        {
            widgetTree = reinterpret_cast<QmUE::UObject*>(
                *reinterpret_cast<void* const*>(reinterpret_cast<const uint8_t*>(screen) + kOff_UserWidget_WidgetTree));
            if (widgetTree)
                rootWidget = reinterpret_cast<QmUE::UObject*>(
                    *reinterpret_cast<void* const*>(reinterpret_cast<const uint8_t*>(widgetTree) + kOff_WidgetTree_RootWidget));
        }
        __except (EXCEPTION_EXECUTE_HANDLER) { widgetTree = nullptr; rootWidget = nullptr; }
        char wtid[352]; DescribeObject(widgetTree, wtid, sizeof(wtid));
        QM_LOG_INFO("[ModTab]   view: screen->WidgetTree@0x2D8 = 0x%p %s", (void*)widgetTree, wtid);
        QmUE::UObject* mountTarget   = nullptr;   // first VerticalBox in the reachable tree = current mount point
        QmUE::UObject* settingsPanel = nullptr;   // WBP_Settings_Panel_C = the per-tab content host (UserWidget)
        if (rootWidget)
        {
            int budget = 250;
            __try { DumpWidgetSubtree(rootWidget, 0, budget, "verticalbox", &mountTarget,
                                      "settings_panel", &settingsPanel); }
            __except (EXCEPTION_EXECUTE_HANDLER) { QM_LOG_WARN("[ModTab]   view: tree walk FAULTED"); }
        }
        else QM_LOG_WARN("[ModTab]   view: no RootWidget - cannot map tree");

        // #18i REOPEN GUARD: OnEnter nulls g_mountTarget on every (re)open to force a re-mount; the first
        // self-heal poll after that can land BEFORE the content tree is rebuilt, leaving mountTarget unresolved.
        // Bail cleanly then - no detach, no construct, no leak - and let the next poll retry once the tree is
        // live. (On a genuine first open mountTarget always resolves here, so this never trips the happy path.)
        if (!mountTarget)
        {
            QM_LOG_INFO("[ModTab]   view: content VerticalBox not resolved yet (tree mid-rebuild) - deferring (re)build to next poll");
            return;
        }

        // #18l FRESH REBUILD EACH OPEN: on every reopen the pooled content tree's Slate layer is re-realized,
        // but our previously-mounted ScrollBox never comes back to life - by every reflectable measure it is
        // Visible, correctly parented, native panel collapsed (proven by the #18k readback: ourVis=0,
        // parentOK=1, natVis=1, byte-identical to the working first open) yet it renders NOTHING. The render is
        // lost in the Slate layer this reflection-based rider cannot see or fix: reusing the pooled widget
        // (#18k no-op) and re-AddChild-ing the same widget (#18h) both leave the dead Slate realization in
        // place. The first open ALWAYS works because it builds a brand-new widget, whose TakeWidget() forces a
        // fresh Slate realization. So make EVERY open a first open: detach + discard the stale panel here and
        // fall through to the full fresh build below. The discarded ScrollBox (+ its EntrySwitcher/button) is
        // left to GC once unparented - a few orphaned widgets per reopen, the accepted cost of guaranteed
        // rendering. RemoveFromParent first so the new panel is the ONLY one in the pooled VerticalBox.
        if (g_ourPanel)
        {
            QmUE::UObject* panel = reinterpret_cast<QmUE::UObject*>(g_ourPanel);
            __try
            {
                if (panel->Class)
                    if (QmUE::UFunction* fnRm = QmUE::FindFunctionOnClass(panel->Class, "RemoveFromParent"))
                    {
                        char rmbuf[16]; memset(rmbuf, 0, sizeof(rmbuf));
                        QmUE::CallProcessEvent(panel, fnRm, rmbuf);   // unparent the stale (dead-Slate) panel
                    }
            }
            __except (EXCEPTION_EXECUTE_HANDLER) {}
            QM_LOG_WARN("[ModTab] *** VIEW-PATH #18l FRESH REBUILD *** discarded stale panel=0x%p (dead Slate on "
                        "reopen) - building a brand-new ScrollBox into content VerticalBox 0x%p",
                        (void*)g_ourPanel, (void*)mountTarget);
            g_ourPanel = nullptr;   // the full fresh build below replaces it
        }

        // RECON (this round): hop one level into Settings_Panel. It is a UserWidget, so the outer walk stops
        // at it, but the REAL per-tab content (rows of the active tab) lives in ITS OWN WidgetTree. Dump that
        // inner tree to find the actual scrollable content container - that is where we mount next round,
        // instead of the shared chrome VerticalBox that puts our row on every tab and at the bottom.
        if (settingsPanel)
        {
            QmUE::UObject* innerTree = nullptr;
            QmUE::UObject* innerRoot = nullptr;
            __try
            {
                innerTree = reinterpret_cast<QmUE::UObject*>(
                    *reinterpret_cast<void* const*>(reinterpret_cast<const uint8_t*>(settingsPanel) + kOff_UserWidget_WidgetTree));
                if (innerTree)
                    innerRoot = reinterpret_cast<QmUE::UObject*>(
                        *reinterpret_cast<void* const*>(reinterpret_cast<const uint8_t*>(innerTree) + kOff_WidgetTree_RootWidget));
            }
            __except (EXCEPTION_EXECUTE_HANDLER) { innerTree = nullptr; innerRoot = nullptr; }
            char itid[352]; DescribeObject(innerTree, itid, sizeof(itid));
            QM_LOG_WARN("[ModTab]   recon: Settings_Panel 0x%p -> inner WidgetTree@0x2D8 = 0x%p %s",
                        (void*)settingsPanel, (void*)innerTree, itid);
            if (innerRoot)
            {
                int ibudget = 250;
                QmUE::UObject* innerScroll = nullptr;
                __try { DumpWidgetSubtree(innerRoot, 0, ibudget, "scrollbox", &innerScroll); }
                __except (EXCEPTION_EXECUTE_HANDLER) { QM_LOG_WARN("[ModTab]   recon: inner tree walk FAULTED"); }
                QM_LOG_WARN("[ModTab]   recon: inner content ScrollBox candidate = 0x%p", (void*)innerScroll);
            }
            else QM_LOG_WARN("[ModTab]   recon: Settings_Panel has no inner RootWidget");
        }
        else QM_LOG_WARN("[ModTab]   recon: no Settings_Panel in outer tree");

        // (2) Owning player (Create's 3rd arg). Widget::GetOwningPlayer is inherited by the screen UserWidget.
        QmUE::UObject* owningPlayer = nullptr;
        if (QmUE::UFunction* fnOP = QmUE::FindFunctionOnClass(screen->Class, "GetOwningPlayer"))
        {
            P_GetOwningPlayer op; op.ReturnValue = nullptr;
            if (QmUE::CallProcessEvent(screen, fnOP, &op)) owningPlayer = reinterpret_cast<QmUE::UObject*>(op.ReturnValue);
        }
        char opid[352]; DescribeObject(owningPlayer, opid, sizeof(opid));
        QM_LOG_INFO("[ModTab]   view: GetOwningPlayer -> 0x%p %s", (void*)owningPlayer, opid);

        // (3) The EntrySwitcher blueprint class (loaded into GObjects only if the screen referenced it).
        QmUE::UClass* entryClass = QmUE::FindClassByName(kEntrySwitcherClass);
        QM_LOG_INFO("[ModTab]   view: class '%s' = 0x%p (%s)", kEntrySwitcherClass, (void*)entryClass,
                    entryClass ? "loaded" : "NOT loaded - round 2 must LoadAsset it");

        // (4) Resolve Create + actually create ONE EntrySwitcher (NOT mounted -> does not render -> safe).
        QmUE::UClass*    wblClass  = QmUE::FindClassByName("WidgetBlueprintLibrary");
        QmUE::UObject*   wblCDO    = wblClass ? QmUE::GetClassDefaultObject(wblClass) : nullptr;
        QmUE::UFunction* fnCreate  = wblClass ? QmUE::FindFunctionOnClass(wblClass, "Create") : nullptr;
        QM_LOG_INFO("[ModTab]   view: WidgetBlueprintLibrary CDO=0x%p Create=0x%p", (void*)wblCDO, (void*)fnCreate);

        QmUE::UObject* createdWidget = nullptr;
        if (entryClass && wblCDO && fnCreate)
        {
            P_Create cp; memset(&cp, 0, sizeof(cp));
            cp.WorldContextObject = screen;
            cp.WidgetType         = entryClass;
            cp.OwningPlayer       = owningPlayer;
            if (QmUE::CallProcessEvent(wblCDO, fnCreate, &cp))
                createdWidget = reinterpret_cast<QmUE::UObject*>(cp.ReturnValue);
        }
        char cwid[352]; DescribeObject(createdWidget, cwid, sizeof(cwid));
        QM_LOG_INFO("[ModTab]   view: Create(EntrySwitcher) -> 0x%p %s (%s)", (void*)createdWidget, cwid,
                    createdWidget ? "CREATE PRIMITIVE WORKS" : "create failed - check args above");

        // Confirm the created widget built its own internal tree (sign it is a real, usable widget).
        if (createdWidget)
        {
            QmUE::UObject* cwt = nullptr;
            __try { cwt = reinterpret_cast<QmUE::UObject*>(
                *reinterpret_cast<void* const*>(reinterpret_cast<const uint8_t*>(createdWidget) + kOff_UserWidget_WidgetTree)); }
            __except (EXCEPTION_EXECUTE_HANDLER) { cwt = nullptr; }
            QM_LOG_INFO("[ModTab]   view: created widget WidgetTree@0x2D8 = 0x%p (%s)", (void*)cwt,
                        cwt ? "constructed" : "null - widget may need further init");
        }

        // (5) MOUNT (#18e). The reference mod (R5ModSettings) mounts its own panel as a SIBLING of
        // Settings_Panel in the content column (here mountTarget = VerticalBox_0, the box that holds
        // slot_ExtraInfo + Settings_Panel + Setting_Info) and gates ITS visibility per selected tab - the
        // shared box is fine because the GATE, not the container, makes the row tab-scoped. (The Settings_Panel
        // content host is a data-driven GameSettingListView you cannot AddChild into, hence sibling + gate, not
        // child.) So: build our own ScrollBox (Outer = WidgetTree), drop the created EntrySwitcher into it,
        // AddChild it into the content VerticalBox, and start it Collapsed (Quartermaster is never the default
        // tab on open). The #18d click gate then flips it Visible on our tab / Collapsed elsewhere. #18l rebuilds
        // this fresh on every (re)open (the prior panel is discarded above), so the handle here is always brand
        // new. OnTabsStateChanged is a NATIVE UFunction invisible to this BP hook, which is why the gate keys
        // off the observable per-tab OnButtonClicked click instead.
        // Full build (#18l: g_ourPanel was just discarded above on a reopen, or is null on a genuine first open).
        QmUE::UClass*  scrollClass = QmUE::FindClassByName("ScrollBox");
        QmUE::UObject* ourPanel    = scrollClass ? QmUE::SpawnObjectViaUFunction(scrollClass, widgetTree) : nullptr;
        char opnl[352]; DescribeObject(ourPanel, opnl, sizeof(opnl));
        QM_LOG_WARN("[ModTab]   view: own ScrollBox class=0x%p -> panel=0x%p %s (%s)", (void*)scrollClass,
                    (void*)ourPanel, opnl, ourPanel ? "CONSTRUCTED" : "construct FAILED");

        if (ourPanel && createdWidget)
        {
            if (QmUE::UFunction* fnAdd = QmUE::FindFunctionOnClass(ourPanel->Class, "AddChild"))
            {
                P_AddChild ac; ac.Content = createdWidget; ac.ReturnValue = nullptr;
                bool ok = QmUE::CallProcessEvent(ourPanel, fnAdd, &ac);
                char sl[352]; DescribeObject(reinterpret_cast<QmUE::UObject*>(ac.ReturnValue), sl, sizeof(sl));
                QM_LOG_WARN("[ModTab]   view: FILL EntrySwitcher -> our ScrollBox ok=%d slot=0x%p %s",
                            ok, ac.ReturnValue, sl);
            }
        }

        // BUTTON (#18i): use the GAME'S OWN themed button widget (kArtButtonClass = WBP_ArtButton_TiledText_C)
        // so it matches the native menu styling instead of a raw unstyled UButton. It is a UUserWidget, so it is
        // built through the same WidgetBlueprintLibrary::Create path as the EntrySwitcher (wblCDO/fnCreate/
        // owningPlayer resolved above), then SetData(FText) sets its label, then AddChild drops it into our
        // ScrollBox. If the themed class is not loaded into GObjects (the settings screen may not reference it)
        // Create returns null and we FALL BACK to a raw Button+TextBlock so a button always appears - the log
        // says which path won (a fallback means round 2 must LoadAsset the themed class). STRICTLY ADDITIVE +
        // SEH-isolated: any failure is swallowed and never aborts the panel mount or the reopen fix. Rides along
        // on every re-mount for free (it is a child of the reused ScrollBox).
        if (ourPanel)
        {
            __try
            {
                bool themed = false, labelled = false, mounted = false;
                QmUE::UObject* btn = nullptr;

                // (a) preferred: the game-themed button via Create() + its SetData(FText) label setter.
                QmUE::UClass* artClass = QmUE::FindClassByName(kArtButtonClass);
                if (artClass && wblCDO && fnCreate)
                {
                    P_Create cp; memset(&cp, 0, sizeof(cp));
                    cp.WorldContextObject = screen;
                    cp.WidgetType         = artClass;
                    cp.OwningPlayer       = owningPlayer;
                    if (QmUE::CallProcessEvent(wblCDO, fnCreate, &cp))
                        btn = reinterpret_cast<QmUE::UObject*>(cp.ReturnValue);
                    if (btn && btn->Class)
                    {
                        themed = true;
                        if (QmUE::UFunction* fnSet = QmUE::FindFunctionOnClass(btn->Class, "SetData"))
                        {
                            uint8_t ft[16]; memset(ft, 0, sizeof(ft));   // P_SetData = { FText Data; } (16 bytes)
                            if (QmUE::TextFromString(L"Quartermaster", ft))
                                labelled = QmUE::CallProcessEvent(btn, fnSet, ft);
                        }
                    }
                }

                // (b) fallback: raw Button + TextBlock (unstyled, but guarantees a button is present).
                if (!btn)
                {
                    QmUE::UClass*  btnClass = QmUE::FindClassByName("Button");
                    QmUE::UClass*  txtClass = QmUE::FindClassByName("TextBlock");
                    btn = btnClass ? QmUE::SpawnObjectViaUFunction(btnClass, widgetTree) : nullptr;
                    QmUE::UObject* txt = txtClass ? QmUE::SpawnObjectViaUFunction(txtClass, widgetTree) : nullptr;
                    if (txt && txt->Class)
                        if (QmUE::UFunction* fnSet = QmUE::FindFunctionOnClass(txt->Class, "SetText"))
                        {
                            uint8_t ft[16]; memset(ft, 0, sizeof(ft));
                            if (QmUE::TextFromString(L"Quartermaster", ft))
                                labelled = QmUE::CallProcessEvent(txt, fnSet, ft);
                        }
                    if (btn && btn->Class && txt)    // UButton is a UContentWidget -> AddChild sets its content
                        if (QmUE::UFunction* fnAdd = QmUE::FindFunctionOnClass(btn->Class, "AddChild"))
                        {
                            P_AddChild ac; ac.Content = txt; ac.ReturnValue = nullptr;
                            QmUE::CallProcessEvent(btn, fnAdd, &ac);
                        }
                }

                // (c) mount whichever button we ended up with into our ScrollBox.
                if (btn && ourPanel->Class)
                    if (QmUE::UFunction* fnAdd = QmUE::FindFunctionOnClass(ourPanel->Class, "AddChild"))
                    {
                        P_AddChild ac; ac.Content = btn; ac.ReturnValue = nullptr;
                        mounted = QmUE::CallProcessEvent(ourPanel, fnAdd, &ac);
                    }
                QM_LOG_WARN("[ModTab]   view: BUTTON build btn=0x%p themed=%d labelled=%d mounted=%d (%s)",
                            (void*)btn, themed, labelled, mounted,
                            themed ? "game-themed WBP_ArtButton_TiledText" : "raw fallback - themed class not loaded");
            }
            __except (EXCEPTION_EXECUTE_HANDLER) { QM_LOG_WARN("[ModTab]   view: BUTTON build FAULTED"); }
        }

        if (ourPanel && mountTarget)
        {
            if (QmUE::UFunction* fnAdd = QmUE::FindFunctionOnClass(mountTarget->Class, "AddChild"))
            {
                P_AddChild ac; ac.Content = ourPanel; ac.ReturnValue = nullptr;
                bool ok = QmUE::CallProcessEvent(mountTarget, fnAdd, &ac);
                char sl[352]; DescribeObject(reinterpret_cast<QmUE::UObject*>(ac.ReturnValue), sl, sizeof(sl));
                QM_LOG_WARN("[ModTab]   view: MOUNT ScrollBox -> content VerticalBox ok=%d slot=0x%p %s",
                            ok, ac.ReturnValue, sl);
                if (ok)
                {
                    g_ourPanel    = ourPanel;
                    g_mountTarget = mountTarget;      // #18h: remember the content box for the mount-liveness check
                    g_nativePanel = settingsPanel;   // gate collapses this on our tab so our panel takes the content area
                    // QM is never the default tab on open: our panel starts hidden, native content stays visible.
                    bool vok = SetWidgetVisibility(ourPanel, ESV_Collapsed);
                    bool nok = settingsPanel ? SetWidgetVisibility(settingsPanel, ESV_Visible) : false;
                    QM_LOG_WARN("[ModTab]   view: gate INIT -> our panel Collapsed ok=%d, native Settings_Panel(0x%p) Visible ok=%d",
                                vok, (void*)settingsPanel, nok);
                }
            }
        }
        QM_LOG_WARN("[ModTab] *** VIEW-PATH #18e DONE *** panel=0x%p mountTarget=0x%p - visibility gated by the "
                    "#18d tab click (starts hidden, shows on Quartermaster tab)", (void*)g_ourPanel, (void*)mountTarget);
    }

    // SELF-HEAL key: is our last-injected collection still present in Screen::Tabs? Returns false when we
    // never injected (g_ourCollection null) or the native re-cook on reopen wiped it - both mean "(re)build
    // needed". The pointer is only COMPARED against the array elements, never dereferenced, so a stale/freed
    // g_ourCollection is safe to test. This is the reopen signal that survives the pooled screen pointer.
    bool OurCollectionPresentInTabs(QmUE::UObject* screen)
    {
        if (!screen || !g_ourCollection) return false;
        ArrHdr tabs = ReadArrHdr(reinterpret_cast<const uint8_t*>(screen) + kOff_Screen_Tabs);
        if (!tabs.ok || !tabs.data || tabs.num <= 0 || tabs.num > 4096) return false;
        bool found = false;
        __try
        {
            void* const* els = reinterpret_cast<void* const*>(tabs.data);
            for (int32_t i = 0; i < tabs.num; ++i)
                if (els[i] == g_ourCollection) { found = true; break; }
        }
        __except (EXCEPTION_EXECUTE_HANDLER) { found = false; }
        return found;
    }

    // #18h MOUNT-LIVENESS: is our ScrollBox STILL a live child of the content VerticalBox? Returns false when
    // we never mounted (g_ourPanel/g_mountTarget null) OR the reopen re-cook rebuilt the content tree and
    // orphaned our panel (UObject alive but detached -> invisible). This is the reopen signal the old
    // collection-presence check could never see: the tab DATA survives pooling while the WIDGET TREE does
    // not. A stale g_mountTarget (its instance rebuilt) faults the child walk -> returns false -> a re-mount
    // fires and ProbeViewPath re-resolves the fresh mount target. Read-only, SEH-guarded, silent.
    bool OurPanelMounted()
    {
        if (!g_ourPanel || !g_mountTarget) return false;
        QmUE::UObject* mt = reinterpret_cast<QmUE::UObject*>(g_mountTarget);
        bool found = false;
        __try
        {
            if (!mt->Class) return false;
            QmUE::UFunction* fnCount = QmUE::FindFunctionOnClass(mt->Class, "GetChildrenCount");
            QmUE::UFunction* fnAt    = QmUE::FindFunctionOnClass(mt->Class, "GetChildAt");
            if (fnCount && fnAt)
            {
                P_GetChildCount cc; cc.ReturnValue = 0; cc._pad = 0;
                if (QmUE::CallProcessEvent(mt, fnCount, &cc) && cc.ReturnValue > 0 && cc.ReturnValue <= 256)
                {
                    int n = cc.ReturnValue;
                    for (int i = 0; i < n; ++i)
                    {
                        P_GetChildAt ga; ga.Index = i; ga._pad = 0; ga.ReturnValue = nullptr;
                        if (QmUE::CallProcessEvent(mt, fnAt, &ga) && ga.ReturnValue == g_ourPanel) { found = true; break; }
                    }
                }
            }
        }
        __except (EXCEPTION_EXECUTE_HANDLER) { found = false; }
        return found;
    }

    // The MUTATING (re)build (gated on qm_modtab_inject.txt). The caller holds g_rebuildInProgress, which
    // makes the re-entrant rider polls from the dispatches below (ProbeViewPath's Create/AddChild + the
    // tab-click sim) no-ops - so this runs strictly once per rebuild without recursing.
    void TryLivenessInjectDupTab(QmUE::UObject* screen)
    {
        if (!screen) return;

        // #18p BOOTSTRAP MOUNT: this self-heal path owns the mount until the CookTabs-post hook has PROVEN
        // itself (g_cookTabsHookLive = its post-mount actually landed at least once). Install alone must not
        // disarm this: the very first cook runs before the deferred install can catch the class, and the first
        // session showed CookTabs may never re-dispatch through ExecFunction at all - gating on "installed"
        // left NOBODY mounting (first-open regression).
        if (!InterlockedCompareExchange(&g_cookTabsHookLive, 0, 0))
            ProbeViewPath(screen);

        // #18h: only inject the collection (-> tab) when it is ABSENT. On a reopen the screen + tab data are
        // pooled, so our collection is still in Screen::Tabs (the native cook rebuilds the bar FROM that data,
        // so our tab reappears on its own) - re-appending would stack a DUPLICATE tab. Nothing to do.
        if (OurCollectionPresentInTabs(screen))
            return;

        QM_LOG_WARN("[ModTab] *** LIVENESS INJECT *** duplicating an existing tab pointer into both live "
                    "arrays, then forcing a re-cook (this MUTATES game state)");

        const uint8_t* sb = reinterpret_cast<const uint8_t*>(screen);
        QmUE::UObject* registry = reinterpret_cast<QmUE::UObject*>(ReadPtr(sb + kOff_Screen_Registry));

        // Duplicate source: TopLevel[0] (GameplayCollection); fall back to Screen::Tabs[0].
        void* dupPtr = nullptr;
        if (registry)
        {
            ArrHdr top = ReadArrHdr(reinterpret_cast<const uint8_t*>(registry) + kOff_Reg_TopLevel);
            if (top.ok && top.data && top.num > 0) dupPtr = ReadPtr(top.data);
        }
        if (!dupPtr)
        {
            ArrHdr tabs = ReadArrHdr(sb + kOff_Screen_Tabs);
            if (tabs.ok && tabs.data && tabs.num > 0) dupPtr = ReadPtr(tabs.data);
        }
        if (!dupPtr)
        {
            QM_LOG_WARN("[ModTab]   inject: no existing tab pointer to duplicate - aborting");
            return;
        }
        char did[352]; DescribeObject(reinterpret_cast<QmUE::UObject*>(dupPtr), did, sizeof(did));
        QM_LOG_INFO("[ModTab]   inject: sibling/source collection = 0x%p %s", dupPtr, did);

        // Prefer a REAL constructed "Quartermaster" collection over cloning the sibling pointer. If
        // construction/offset-location fails for any reason, fall back to the proven dup append so the
        // test still yields a visible tab + a clear log of what to fix.
        void*       injectPtr  = nullptr;
        const char* injectKind = "dup (fallback)";
        QmUE::UObject* realColl = BuildQuartermasterCollection(registry, reinterpret_cast<QmUE::UObject*>(dupPtr));
        if (realColl) { injectPtr = realColl; injectKind = "real Quartermaster collection"; }
        else            injectPtr = dupPtr;
        QM_LOG_WARN("[ModTab]   inject: appending %s (0x%p)", injectKind, injectPtr);
        g_ourCollection = injectPtr;   // canonical handle to our injected collection (aligned ptr write, atomic on x64)

        // Append to BOTH lists (verdict: separate backing stores).
        AppendDupToArray(const_cast<uint8_t*>(sb) + kOff_Screen_Tabs, injectPtr, "Screen::Tabs");
        if (registry)
            AppendDupToArray(const_cast<uint8_t*>(reinterpret_cast<const uint8_t*>(registry)) + kOff_Reg_TopLevel,
                             injectPtr, "Registry::TopLevelSettings");

        // RECON FINDING (#12-13, 2026-06-10) -> Weg B: the BP nav paths all failed to reconcile the
        // tab BAR. GoToNextTab/GoToPreviousTab only switch the active CONTENT collection. CookTabs is
        // the literal bar rebuild but takes an 8-byte arg (parmsSize=8, NOT parameterless), so calling
        // it blindly with a zeroed buffer would clobber/fault - its param layout is still unknown. The
        // ONLY proven bar-reconcile (recon #11) is a real tab-button click, which drives a native Slate
        // rebuild that constructs the 6th tab widget. Its BP entry point is the tab widget's
        // OnButtonClicked delegate. So we invoke that delegate on a live WBP_MetaUI_Tab_Main_C widget:
        // calling it on the widget instance means `this` = that tab, so the handler "clicks" that tab.
        // This may visibly jump to the clicked tab - acceptable for the test; we refine the jump away
        // (e.g. SelectFirstCollection back to tab 0) once the reconcile itself is proven to fire.
        QmUE::UObject* tabw = QmUE::FindFirstInstanceOfClass(kTabWidgetClass);
        if (!tabw)
        {
            QM_LOG_WARN("[ModTab]   inject: no live '%s' - cannot simulate a tab click (tab will appear "
                        "on the next manual tab switch)", kTabWidgetClass);
            return;
        }
        char tid[352]; DescribeObject(tabw, tid, sizeof(tid));
        QM_LOG_INFO("[ModTab]   inject: simulating tab-button click on %s", tid);
        CallNavRefresh(tabw, kTabClickDelegate, /*onlyIfParameterless=*/false);

        // Post-click state: append must have survived (Num still 6, Data unchanged = no realloc/rebuild).
        ArrHdr after = ReadArrHdr(sb + kOff_Screen_Tabs);
        QM_LOG_INFO("[ModTab]   inject: POST-click Screen::Tabs Num=%d Max=%d Data=0x%p",
                    after.num, after.max, after.data);
        QM_LOG_WARN("[ModTab] *** LIVENESS INJECT DONE *** -> the 6th (duplicate) tab should now appear "
                    "WITHOUT a manual click");
    }

    // ===================== #18p PER-UFUNCTION HOOK: thunks + deferred install =========================
    // DIAGNOSTIC: dump a hooked function's parms (self + parmsSize + hexdump + TArray scan). Budgeted per
    // open so a chatty SetData cannot flood. `stack` is the FFrame; its Locals (+0x28) is the param block,
    // laid out exactly like a ProcessEvent Parms buffer. SEH-guarded by the caller's thunk.
    void DumpFnParms(const char* tag, void* ctx, void* stack, QmUE::UFunction* fn, volatile LONG* budget)
    {
        if (!budget || InterlockedDecrement(budget) < 0) return;
        void* locals = nullptr;
        __try { locals = *reinterpret_cast<void**>(reinterpret_cast<uint8_t*>(stack) + kFFrameLocalsOff); }
        __except (EXCEPTION_EXECUTE_HANDLER) { locals = nullptr; }
        char slf[352]; DescribeObject(reinterpret_cast<QmUE::UObject*>(ctx), slf, sizeof(slf));
        int32_t psize = fn ? ParmsSize(fn) : 0;
        QM_LOG_WARN("[ModTab] *** #18p FN-HOOK %s *** self=%s parms=0x%p parmsSize=%d (capturing the real layout the "
                    "reference-faithful gate/sync needs - Build B)", tag, slf, locals, psize);
        if (locals && psize > 0)
        {
            int32_t cap = psize < kMaxParmsDump ? psize : kMaxParmsDump;
            HexDump("fnparm", reinterpret_cast<const uint8_t*>(locals), cap);
            ScanForTArrays(reinterpret_cast<const uint8_t*>(locals), cap);
        }
    }

    // The three thunks. Each matches FNativeFuncPtr (void(Context, FFrame& Stack, void* Result)) - the exact
    // PLSF signature, with Stack being the function's OWN frame (Node = the function, Locals = its params) on
    // BOTH dispatch paths. Reached ONLY from the global PLSF detour's Node match (QmModTab_OnScriptFunction);
    // they forward through the PLSF trampoline (g_plsfOriginal) and add our pre/post work. SEH around our work
    // only - the forward must run unguarded so the game's own dispatch is never altered.
    //
    // CookTabs POST = THE reopen fix: once the native cook returns, the tab content tree is freshly (re)built
    // and the content box's Slate is live RIGHT NOW - the exact moment every #18e..#18o mount missed. We mount
    // a brand-new panel (Collapsed) into it. The whole thunk holds g_rebuildInProgress because calling the
    // original re-enters our ProcessInternal rider for CookTabs (and its sub-calls) - the guard stops the
    // self-heal from injecting/mounting mid-cook - and because ProbeViewPath's own PE dispatches
    // (Create/AddChild/SetVisibility) re-enter it too. We acquire ONCE here and own the post-mount, so there is
    // no double-acquire. The user's real QM-tab click then flips the panel Visible via the unchanged #18d gate.
    void ThunkCookTabs(void* ctx, void* stack, void* result)
    {
        // Unconditional fired-marker (small cap): its timestamps answer the open question whether CookTabs
        // fires boot-only or per-open - both prior observation layers (ProcessInternal rider, PE hook) were
        // blind to BP-internal dispatch, so all earlier "cooks only once" findings were blind-spot artifacts.
        LONG nfire = InterlockedIncrement(&g_cookTabsFiredCount);
        if (nfire <= 16)
            QM_LOG_WARN("[ModTab] *** #18r COOKTABS FIRED (PLSF) *** #%d ctx=0x%p (caught on the script-VM funnel)",
                        nfire, ctx);
        bool owned = (InterlockedCompareExchange(&g_rebuildInProgress, 1, 0) == 0);
        QmUE::FNativeFuncPtr orig = g_plsfOriginal;
        if (orig) orig(ctx, stack, result);          // run the native cook first (POST hook)
        if (owned)
        {
            __try
            {
                if (g_armed && g_injectArmed)
                {
                    // #18t: mount into the LIVE screen latched by the nested OnTabsStateChanged fire of THIS
                    // cook. FindFirstInstanceOfClass is only the bootstrap fallback (single-instance first
                    // open) - on a reopen it returns the lingering STALE screen and the mount lands in a
                    // detached tree that never renders (the proven root cause of the whole reopen series).
                    QmUE::UObject* screen = reinterpret_cast<QmUE::UObject*>(g_liveScreen);
                    bool live = (screen != nullptr);
                    if (!screen) screen = QmUE::FindFirstInstanceOfClass(kSettingsScreenClass);
                    if (screen)
                    {
                        QM_LOG_WARN("[ModTab] *** #18r COOKTABS-POST *** native cook done - mounting a fresh panel into "
                                    "the just-cooked content tree of screen=0x%p (%s) - the proven-live Slate moment "
                                    "the reference uses", (void*)screen, live ? "#18t LIVE latch" : "first-instance fallback");
                        ProbeViewPath(screen);   // #18l fresh discard+build+mount, starts Collapsed; self-defers if the box isn't live yet
                        // Hand the mount authority to this hook ONLY once its post-mount actually LANDED.
                        // Until then the self-heal bootstrap keeps owning the mount - so a thunk that never
                        // fires (or fires while the box is not live yet) can never regress the first open.
                        if (OurPanelMounted())
                        {
                            InterlockedExchange(&g_cookTabsHookLive, 1);
                            // #18s GATE RE-APPLY: CookTabs fires on EVERY tab click and runs AFTER the #18d
                            // gate on that same click - so the fresh Collapsed mount just replaced the panel
                            // the gate showed milliseconds ago (the proven 18r race: SHOW at .173, discard at
                            // .186). If the QM tab is the current selection, show the FRESH panel now - this
                            // is the reference's post-cook gate, applied at the only spot that runs after the
                            // rebuild. On native-tab clicks / the reopen cook the latch is 0 and the panel
                            // stays Collapsed as before.
                            if (InterlockedCompareExchange(&g_qmTabActive, 0, 0) && g_ourPanel)
                            {
                                bool sv = SetWidgetVisibility(reinterpret_cast<QmUE::UObject*>(g_ourPanel), ESV_Visible);
                                bool sn = g_nativePanel
                                    ? SetWidgetVisibility(reinterpret_cast<QmUE::UObject*>(g_nativePanel), ESV_Collapsed)
                                    : false;
                                QM_LOG_WARN("[ModTab] *** #18s GATE-REAPPLY *** QM tab is the live selection - showing the "
                                            "fresh post-cook panel=0x%p (setVis=%d natCollapsed=%d)",
                                            (void*)g_ourPanel, sv, sn);
                            }
                        }
                    }
                }
            }
            __except (EXCEPTION_EXECUTE_HANDLER) {}
            InterlockedExchange(&g_rebuildInProgress, 0);
        }
    }
    void ThunkTabState(void* ctx, void* stack, void* result)
    {
        // #18t: self IS the live WBP_Settings_Screen_C (only that class carries this UFunction). This fires
        // nested inside every cook, BEFORE our CookTabs post-mount - latch it so the post mounts into the
        // CURRENT screen instead of the stale first-in-GObjects instance. Gated on the open session like the
        // TabsGroup latch, so a teardown-phase fire cannot park a dying pointer here.
        if (ctx && g_settingsOpen) g_liveScreen = ctx;
        QmUE::FNativeFuncPtr orig = g_plsfOriginal;
        if (orig) orig(ctx, stack, result);          // POST: dump the selection state the gate will read
        __try { DumpFnParms("OnTabsStateChanged", ctx, stack, g_fnTabState, &g_tabStateDumpBudget); }
        __except (EXCEPTION_EXECUTE_HANDLER) {}
    }
    void ThunkSetData(void* ctx, void* stack, void* result)
    {
        __try { DumpFnParms("SetData(TabsGroup)", ctx, stack, g_fnSetData, &g_setDataDumpBudget); }   // PRE: see the incoming tab array
        __except (EXCEPTION_EXECUTE_HANDLER) {}
        QmUE::FNativeFuncPtr orig = g_plsfOriginal;
        if (orig) orig(ctx, stack, result);
    }

    // Deferred target-handle resolution. #18r: NO ExecFunction swap anymore - the 18q log proved that field
    // is never read for these BP functions at runtime (swap stood verified in-field, zero fires across a
    // session with 8 tab clicks). The handles are matched against FFrame::Node inside the global PLSF detour
    // (qm_hook.cpp), which sees every script execution regardless of dispatch path. A target whose BP class
    // is not in GObjects yet is retried on the next call; latches off once all three are resolved. Driven
    // primarily from the global ProcessEvent hook (throttled, live from engine start), with the rider's
    // self-heal as backstop. Read-only: nothing in the game is modified here.
    void TryInstallSettingsFnHooks()
    {
        if (InterlockedCompareExchange(&g_allFnHooksInstalled, 0, 0) != 0) return;

        struct Target { const char* cls; const char* fn; QmUE::UFunction** fnSlot; };
        static Target targets[] = {
            { kSettingsControllerClass, "CookTabs",           &g_fnCookTabs },
            { kSettingsScreenClass,     "OnTabsStateChanged", &g_fnTabState },
            { kTabsGroupClass,          "SetData",            &g_fnSetData  },
        };
        constexpr int kTargetCount = (int)(sizeof(targets) / sizeof(targets[0]));

        int remaining = 0;
        for (int i = 0; i < kTargetCount; ++i)
        {
            Target& t = targets[i];
            if (*t.fnSlot) continue;   // already resolved

            QmUE::UClass* cls = QmUE::FindClassByName(t.cls);
            if (!cls)
            {
                // The settings BP classes load together (same UI package). While the FIRST target's
                // class is still absent (the whole pre-lobby boot phase), bail after ONE O(GObjects) walk
                // per poll instead of three - keeps the early-poll cheap on the game thread.
                if (i == 0) return;
                ++remaining; continue;                            // class not loaded yet - retry later
            }
            QmUE::UFunction* fn = QmUE::FindFunctionOnClass(cls, t.fn);
            if (!fn) { ++remaining; continue; }

            *t.fnSlot = fn;
            QM_LOG_WARN("[ModTab] *** #18r FN-TARGET RESOLVED *** %s::%s fn=0x%p (matched by FFrame::Node in the "
                        "global PLSF detour)", t.cls, t.fn, (void*)fn);
        }
        if (remaining == 0)
        {
            InterlockedExchange(&g_allFnHooksInstalled, 1);
            QM_LOG_WARN("[ModTab] *** #18r FN-TARGETS COMPLETE *** all three resolved - the PLSF detour now routes "
                        "their executions to our thunks; self-heal bootstrap keeps owning the mount until a "
                        "COOKTABS FIRED post-mount lands; OnTabsStateChanged + SetData parm dumps armed for Build B");
        }
    }

    // One-time GetTabs array-layout recon dump (first open only). Pure diagnostics; pins the array offset
    // + stride in the log. Runs inside the rebuild guard (the GetTabs dispatch re-enters the rider).
    void DumpGetTabsReconOnce(QmUE::UObject* screen)
    {
        if (g_getTabsDone) return;

        QmUE::UFunction* fn = QmUE::FindFunctionOnClass(screen->Class, kGetTabsFuncName);
        if (!fn)
        {
            InterlockedExchange(&g_getTabsDone, 1);
            QM_LOG_WARN("[ModTab] GetTabs: '%s' not found on '%s' - cannot read tab array actively",
                        kGetTabsFuncName, kSettingsScreenClass);
            return;
        }

        int32_t structSize = ParmsSize(fn);
        uint8_t buf[256];
        if (structSize <= 0 || structSize > (int32_t)sizeof(buf))
        {
            InterlockedExchange(&g_getTabsDone, 1);
            QM_LOG_WARN("[ModTab] GetTabs: parms size %d outside [1..%zu] - aborting active read",
                        structSize, sizeof(buf));
            return;
        }
        InterlockedExchange(&g_getTabsDone, 1);

        memset(buf, 0, sizeof(buf));
        char slf[352];
        DescribeObject(screen, slf, sizeof(slf));
        bool ok = QmUE::CallProcessEvent(screen, fn, buf);
        QM_LOG_INFO("[ModTab] GetTabs: called %s::%s on %s (parmsSize=%d) ok=%d - dumping return buffer:",
                    kSettingsScreenClass, kGetTabsFuncName, slf, structSize, ok ? 1 : 0);

        if (ok)
        {
            int32_t cap = structSize < kMaxParmsDump ? structSize : kMaxParmsDump;
            HexDump("GetTabs.ret", reinterpret_cast<const uint8_t*>(buf), cap);
            ScanForTArrays(reinterpret_cast<const uint8_t*>(buf), cap);
            // The return value is a TArray<UObject*> at +0x00 (recon-confirmed). Deref its elements to
            // reveal the tab object's class + head layout - the label/index offsets the injection needs.
            DumpTabArrayElements(reinterpret_cast<const uint8_t*>(buf));
        }

        // Registry recon: read the LIVE tab backing (Screen::Tabs @ 0x3B8 + Registry::TopLevelSettings
        // @ 0x88) and compare to the GetTabs copy - this pins the clean injection point (SSOT).
        void* getTabsData = ok ? ReadPtr(buf) : nullptr;
        DumpLiveTabBacking(screen, getTabsData);
        // NOTE: the returned TArray's heap backing-store is intentionally leaked - we have no element
        // destructor and this runs once. For a recon-only path the one-shot leak is fine.
    }

    void TryDumpTabsViaGetTabs()
    {
        // Throttle the O(GObjects) instance walk to ~1/frame.
        ULONGLONG now  = GetTickCount64();
        ULONGLONG last = g_getTabsLastTick;
        if (last != 0 && (now - last) < kGetTabsScanIntervalMs) return;
        g_getTabsLastTick = now;

        // #18q: install backstop BEFORE the screen-instance gate. The primary driver is the global PE hook
        // (live from engine start); this rider-driven attempt only matters if PE was somehow not installed.
        // Deliberately NOT gated on a live screen: the swap needs only the CLASSES in GObjects - waiting for
        // an instance is exactly the bug that made Build A miss the one lobby-boot cook.
        TryInstallSettingsFnHooks();

        QmUE::UObject* screen = QmUE::FindFirstInstanceOfClass(kSettingsScreenClass);
        if (!screen) return;   // settings not live yet (or just closed) - keep watching across reopens

        // REENTRANCY: the rebuild below dispatches via ProcessEvent (GetTabs, Create, AddChild, the
        // tab-click sim), which re-enters this rider. Ignore those re-entrant polls so a rebuild can't
        // recurse into itself.
        if (g_rebuildInProgress) return;

        // #18p SELF-HEAL: g_cookTabsHookLive means the CookTabs-post mount has actually LANDED at least once
        // (not merely "hook installed") - only then does CookTabs-post own the per-open mount and this poll's
        // job shrinks to injecting the TAB when absent. Until then (including the case that CookTabs never
        // re-dispatches through ExecFunction at all), a present tab with no live panel falls through to the
        // bootstrap mount inside TryLivenessInjectDupTab - the proven first-open path.
        bool tabPresent = OurCollectionPresentInTabs(screen);
        if (tabPresent && (InterlockedCompareExchange(&g_cookTabsHookLive, 0, 0) || OurPanelMounted())) return;

        if (InterlockedCompareExchange(&g_rebuildInProgress, 1, 0) != 0) return;
        __try
        {
            DumpGetTabsReconOnce(screen);
            // Inject our tab when absent (first open); bootstrap-mount the panel only until CookTabs-post takes
            // over. Once the CookTabs hook is live this is a no-op past the recon dump.
            if (g_injectArmed) TryLivenessInjectDupTab(screen);
        }
        __finally { InterlockedExchange(&g_rebuildInProgress, 0); }
    }
}

bool QmModTab_Init()
{
    if (g_initDone) return g_armed;
    g_initDone = true;

    char dir[MAX_PATH];
    if (!LocateDllDir(dir, sizeof(dir)))
    {
        QM_LOG_WARN("[ModTab] could not locate DLL dir - recon disabled");
        g_armed = false;
        return false;
    }

    // Arm on ANY qm_modtab*.txt (manual qm_modtab.txt or a future profile-bound
    // qm_modtab_<profile>.txt). Mirrors the weather/killxp/shanty sentinel glob.
    char pattern[MAX_PATH];
    snprintf(pattern, sizeof(pattern), "%s\\qm_modtab*.txt", dir);
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
        QM_LOG_INFO("[ModTab] *** ARMED *** recon active (%d sentinel file(s) matching %s\\qm_modtab*.txt) - "
                    "observing settings-screen UFunctions (CookTabs / TabsGroup.SetData / OnTabsStateChanged) "
                    "via the ProcessInternal hook (catches BP-internal calls); logging-only, nothing is modified", files, dir);
    else
        QM_LOG_INFO("[ModTab] no qm_modtab*.txt - idle (zero cost)");

    // Separate opt-in for the MUTATING liveness test. The recon dump runs under qm_modtab.txt; the
    // inject only fires if this explicit sentinel is also present (exact name, not the glob).
    if (g_armed)
    {
        char injPath[MAX_PATH];
        snprintf(injPath, sizeof(injPath), "%s\\qm_modtab_inject.txt", dir);
        g_injectArmed = (GetFileAttributesA(injPath) != INVALID_FILE_ATTRIBUTES);
        if (g_injectArmed)
            QM_LOG_WARN("[ModTab] *** INJECT ARMED *** qm_modtab_inject.txt present - the liveness test WILL "
                        "MUTATE the live tab arrays (duplicate-pointer append + forced re-cook)");
    }
    return g_armed;
}

bool QmModTab_ReconArmed()
{
    if (!g_initDone) QmModTab_Init();
    return g_armed;
}

void QmModTab_OnProcessInternal(QmUE::UObject* self, QmUE::UFunction* func, void* parms)
{
    if (!g_armed || !func) return;

    __try
    {
        uint8_t v = GetVerdict(func);
        if (!(v & MT_ANY)) return;

        // We are inside a settings-screen UFunction dispatch, so the settings classes are live in
        // GObjects now. Drive the one-shot reflection enumeration (throttled + latched internally).
        // Placed before the trace-cap return below so it still runs once the trace cap is reached.
        TryEnumerateSettingsClasses();

        // Settings are open, so the live screen widget exists: actively call GetTabs on it to read
        // the cached tab array (the cook itself fired pre-hook at boot and never re-fires). One-shot,
        // throttled + latched internally. Also before the trace-cap return so the flood can't starve it.
        TryDumpTabsViaGetTabs();

        // Bounded lifecycle trace: helps map the open->switch->close ordering without
        // drowning the log. The three decisive functions below are never capped.
        if (v & MT_TRACE)
        {
            ULONGLONG now = GetTickCount64();

            // Per-frame flood (Tick): normally skipped. But during a verbose window (armed by a tab
            // click) we DO want it - that's where the native/Tick-driven rebuild runs. To stay
            // readable, emit only the FIRST Tick per distinct widget instance per window: a newly
            // created 6th tab widget surfaces as a fresh line; the steady per-frame repeats don't.
            if (v & MT_NOISE)
            {
                if (now >= g_verboseUntilTick) return;             // outside the window: skip as before
                LONG gen = g_verboseGen;
                TickSeen& ts = g_tickSeen[(((uintptr_t)self) >> 4) & kTickSeenMask];
                if (ts.obj == self && ts.gen == gen) return;       // already logged this widget this window
                if (InterlockedIncrement(&g_verboseLines) > kMaxVerboseLines) return;
                ts.obj = self; ts.gen = gen;
                char fnN[64] = { 0 }, sf[352];
                QmUE::ResolveFNameNarrow(func->Name, fnN, sizeof(fnN));
                DescribeObject(self, sf, sizeof(sf));
                QM_LOG_INFO("[ModTab] verbose: %s on %s (1st this window)", fnN[0] ? fnN : "?", sf);
                return;
            }

            char fnNm[128] = { 0 }, slf[352];
            QmUE::ResolveFNameNarrow(func->Name, fnNm, sizeof(fnNm));

            // #18j: latch the LIVE tab bar. On a reopen the bar is re-cooked (fresh Construct on a new
            // WBP_MetaUI_TabsGroup_C) while the old one lingers un-GC'd, so the global lookup returns the
            // stale bar and the gate resolves a dead QM tab. Capture the freshly-constructed bar here so
            // ResolveOurTabWidget walks the live one. Scoped to the open settings session so an unrelated
            // menu's TabsGroup cannot clobber it.
            if (g_settingsOpen && ContainsLc(fnNm, "construct"))
            {
                char cslf[352]; DescribeObject(self, cslf, sizeof(cslf));
                if (ContainsLc(cslf, "tabsgroup")) g_liveTabsGroup = self;
            }

            // A tab button CLICK is the navigation that (per recon #10) makes the injected 6th tab
            // appear. Arm a short verbose window - BEFORE the trace cap, so it survives even a
            // saturated log - so the otherwise-skipped Tick flood + Panel/ListView rebuild dispatches
            // that follow the click become visible.
            if (ContainsLc(fnNm, "onbuttonclick"))
            {
                InterlockedIncrement(&g_verboseGen);
                g_verboseUntilTick = now + kVerboseWindowMs;
                QM_LOG_WARN("[ModTab] *** VERBOSE WINDOW ARMED *** (%llu ms) by tab click - now logging "
                            "Tick (1st/widget) + Draw + Panel/ListView rebuild dispatches",
                            (unsigned long long)kVerboseWindowMs);

                // GATE #18d: recognize OUR Quartermaster tab among the clicked WBP_MetaUI_Tab_Main_C
                // instances so the visibility gate can show our panel only on our tab. #18c ruled out both
                // guessed signals (the clicked widget does NOT store our collection pointer; TabsGroup +0x90
                // is the tab COUNT, not the selected index). So we identify our tab STRUCTURALLY: it is the
                // last data entry we injected, hence the last tab widget in the bar. A tab click rebuilds the
                // bar (fresh instances), so we re-resolve the last tab_main from the current TabsGroup tree on
                // EVERY click and compare it to the clicked self - that IS the gate. (Mount + SetVisibility
                // wired next round; this round only proves the gate fires on the right tab.)
                {
                    char tabSlf[352]; DescribeObject(self, tabSlf, sizeof(tabSlf));
                    if (ContainsLc(tabSlf, "tab_main"))
                    {
                        LONG rc = InterlockedIncrement(&g_tabReconCount);
                        QmUE::UObject* ourTab = ResolveOurTabWidget();
                        if (ourTab) g_ourTabWidget = ourTab;
                        bool match = (g_ourTabWidget && self == g_ourTabWidget);
                        // #18s: latch the selection for the CookTabs-post gate re-apply. The cook this very
                        // click triggers runs AFTER this gate and replaces the panel with a fresh Collapsed
                        // one - the cook-post reads this latch to show the fresh panel again.
                        InterlockedExchange(&g_qmTabActive, match ? 1 : 0);
                        // #18e/#18f: drive the live gate. Show our panel only on our tab, and INVERSE-gate the
                        // native content host (collapse it on our tab so our panel takes the content area
                        // instead of stacking below it; restore it on every other tab). The screen tree is
                        // POOLED, so the handles stay valid across a settings reopen; SetWidgetVisibility is
                        // SEH-guarded for the rare genuine-teardown window before the self-heal re-acquires them.
                        bool setVis = false, setNat = false;
                        // #18p: the click gate now ONLY toggles visibility. The panel is already mounted AND
                        // Slate-realized by the CookTabs-post hook (the proven-live moment), so the old
                        // click-time RemoveFromParent + re-AddChild (#18n) - a workaround for mounting at the
                        // wrong Slate moment - is gone. This is the unchanged show/hide gate riding a panel that
                        // already renders.
                        if (g_ourPanel)
                            setVis = SetWidgetVisibility(reinterpret_cast<QmUE::UObject*>(g_ourPanel),
                                                         match ? ESV_Visible : ESV_Collapsed);
                        if (g_nativePanel)
                            setNat = SetWidgetVisibility(reinterpret_cast<QmUE::UObject*>(g_nativePanel),
                                                         match ? ESV_Collapsed : ESV_Visible);
                        if (rc <= kMaxTabRecon)
                            QM_LOG_WARN("[ModTab]   #18d click#%ld self=0x%p %s lastTab=0x%p match=%s -> gate %s "
                                        "panel=0x%p setVis=%d nativeHidden=%d setNat=%d", rc, (void*)self, tabSlf,
                                        (void*)g_ourTabWidget, match ? "YES" : "NO", match ? "SHOW" : "HIDE",
                                        (void*)g_ourPanel, setVis, match ? 1 : 0, setNat);
                        // #18k SELF-VERIFYING READBACK: don't trust "SetVisibility dispatched" - read the ACTUAL
                        // visibility now on the wire AND our panel's live parent. On a reopen where the panel
                        // truly renders, this must show ourVis=0 (Visible) + ourParent==mountTarget on a YES
                        // click; ourParent=0x0 would prove a detach, ourVis!=0 a losing visibility fight.
                        if (rc <= kMaxTabRecon)
                        {
                            int  ourVis = GetWidgetVisibility(reinterpret_cast<QmUE::UObject*>(g_ourPanel));
                            int  natVis = GetWidgetVisibility(reinterpret_cast<QmUE::UObject*>(g_nativePanel));
                            void* ourPar = GetWidgetParent(reinterpret_cast<QmUE::UObject*>(g_ourPanel));
                            QM_LOG_WARN("[ModTab]   #18k readback click#%ld ourVis=%d natVis=%d ourParent=0x%p "
                                        "mountTarget=0x%p parentOK=%d (vis: 0=Visible 1=Collapsed, -1=unreadable)",
                                        rc, ourVis, natVis, ourPar, g_mountTarget,
                                        (ourPar && ourPar == g_mountTarget) ? 1 : 0);
                        }
                    }
                }
            }

            if (InterlockedIncrement(&g_traceCount) > kMaxTraceLines) return;
            DescribeObject(self, slf, sizeof(slf));
            // parmsSize disambiguates the rebuild call (UpdateTabs/SetData carry the tab array, so
            // a non-trivial size) from trivial callbacks (size 0) when reading the click sequence.
            QM_LOG_INFO("[ModTab] trace: %s on %s parmsSize=%d", fnNm[0] ? fnNm : "?", slf, ParmsSize(func));
            return;
        }

        LONG n = InterlockedIncrement(&g_seq);
        const char* what = (v & MT_COOKTABS) ? "CookTabs"
                         : (v & MT_SETDATA)  ? "SetData(TabsGroup)"
                         : (v & MT_ONEXIT)   ? "OnExit(closing)"
                         : (v & MT_ENTER)    ? "OnEnter(opening)"
                         : (v & MT_TABSTATE) ? "OnTabsStateChanged"
                                             : "settings-fn";
        char slf[352];
        DescribeObject(self, slf, sizeof(slf));
        int32_t psize = ParmsSize(func);
        QM_LOG_INFO("[ModTab] #%ld %-18s self=0x%p %s parms=0x%p parmsSize=%d",
                    n, what, (void*)self, slf, parms, psize);

        // #18g: settings is closing, but the screen widget and its whole tree are POOLED - the same pointers
        // are reused across opens (proven by the recycled native tab widgets + our collection surviving in
        // Screen::Tabs on reopen). So our mounted panel SURVIVES the close: dropping the handles here was the
        // bug behind "kommt beim 2. Oeffnen nicht mehr" - the reopen gate then had a null panel handle and
        // could never re-show the still-live widget. So we KEEP the handles and only reset visibility to the
        // closed/default state: our panel Collapsed (reopen lands on a native tab), native content host
        // Visible (never leave the native settings hidden). If the tree is ever genuinely rebuilt (new screen,
        // e.g. after a level load), our collection is absent from the fresh Screen::Tabs and the self-heal
        // rebuild re-acquires every handle; SetWidgetVisibility is SEH-guarded, so a stale handle in that brief
        // window degrades to a no-op instead of crashing.
        if (v & MT_ONEXIT)
        {
            InterlockedExchange(&g_qmTabActive, 0);   // #18s: reopen lands on a native tab - never re-show on the reopen cook
            if (g_ourPanel)    SetWidgetVisibility(reinterpret_cast<QmUE::UObject*>(g_ourPanel),    ESV_Collapsed);
            if (g_nativePanel) SetWidgetVisibility(reinterpret_cast<QmUE::UObject*>(g_nativePanel), ESV_Visible);
            // #18j: settings closed - the latched tab bar is about to be re-cooked on the next open. Drop it
            // (and close the session) so a freed pointer is never walked before the fresh Construct re-latches.
            g_settingsOpen  = false;
            g_liveTabsGroup = nullptr;
            // #18t: same lifecycle as the tab bar - the screen is re-built fresh on the next open and the
            // nested OnTabsStateChanged of that open's first cook re-latches it before our post-mount runs.
            g_liveScreen    = nullptr;
        }

        // #18p REOPEN: OnEnter fires on EVERY settings (re)open. The per-open panel mount is now handled by the
        // CookTabs-post hook (it re-cooks + re-mounts fresh every open, the proven Slate moment), so OnEnter no
        // longer has to null the mount target or arm any re-mount latch. It only (a) opens the session so the
        // next TabsGroup Construct is latched for the gate's bar resolver, and (b) re-arms the per-open
        // diagnostic dump budgets so OnTabsStateChanged + SetData parms are captured each open (Build B input).
        if (v & MT_ENTER)
        {
            g_settingsOpen = true;
            InterlockedExchange(&g_tabStateDumpBudget, kFnDumpBudgetPerOpen);
            InterlockedExchange(&g_setDataDumpBudget,  kFnDumpBudgetPerOpen);
        }

        // CookTabs / SetData carry the tab data, and OnTabsStateChanged carries the new tab selection
        // (the reference reads SelectedTabIndex on this event to drive its show/hide gate) - dump the
        // parms + hunt the array on all three so we can read the index mechanic the visibility gate needs.
        // OnExit has no useful parms (it is just the teardown signal) - logged above, no dump.
        if ((v & (MT_COOKTABS | MT_SETDATA | MT_TABSTATE)) && parms && psize > 0)
        {
            int32_t cap = psize < kMaxParmsDump ? psize : kMaxParmsDump;
            HexDump("parms", reinterpret_cast<const uint8_t*>(parms), cap);
            ScanForTArrays(reinterpret_cast<const uint8_t*>(parms), cap);
        }
    }
    __except (EXCEPTION_EXECUTE_HANDLER) {}
}

// EARLY FN-TARGET RESOLVE DRIVER. ProcessEvent dispatches from engine start on the game thread - the
// earliest safe moment we can poll GObjects for the settings BP classes. Resolve the three target handles
// (throttled, ~1/frame) so the PLSF detour can match them from the first cook on. Cheap fast path: two
// reads + a time check once resolved/disarmed.
void QmModTab_OnProcessEvent(QmUE::UObject* self, QmUE::UFunction* func, void* parms)
{
    (void)self; (void)func; (void)parms;
    if (!g_armed) return;
    if (InterlockedCompareExchange(&g_allFnHooksInstalled, 0, 0) != 0) return;
    ULONGLONG now  = GetTickCount64();
    ULONGLONG last = g_fnHookPollLastTick;
    if (last != 0 && (now - last) < kFnHookEarlyPollMs) return;
    g_fnHookPollLastTick = now;
    __try { TryInstallSettingsFnHooks(); }
    __except (EXCEPTION_EXECUTE_HANDLER) {}
}

// #18r: trampoline wiring + per-execution dispatch for the global PLSF detour (qm_hook.cpp).
void QmModTab_SetPlsfOriginal(QmUE::FNativeFuncPtr orig)
{
    g_plsfOriginal = orig;
}

bool QmModTab_OnScriptFunction(void* context, void* stack, void* result)
{
    // HOT PATH: runs for EVERY Blueprint script-function execution in the game. One guarded read +
    // three pointer compares; the handles stay null until resolved, so unarmed/unresolved costs nothing.
    QmUE::UFunction* node = nullptr;
    __try
    {
        node = *reinterpret_cast<QmUE::UFunction**>(reinterpret_cast<uint8_t*>(stack) + kFFrameNodeOff);
    }
    __except (EXCEPTION_EXECUTE_HANDLER) { return false; }
    if (!node) return false;
    if (node == g_fnCookTabs) { ThunkCookTabs(context, stack, result); return true; }
    if (node == g_fnTabState) { ThunkTabState(context, stack, result); return true; }
    if (node == g_fnSetData)  { ThunkSetData(context, stack, result);  return true; }
    return false;
}
