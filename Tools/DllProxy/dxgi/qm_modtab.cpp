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
    constexpr uint8_t MT_DECISIVE = MT_COOKTABS | MT_SETDATA | MT_TABSTATE;
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

    volatile LONG      g_getTabsDone     = 0;
    volatile LONG      g_getTabsAttempts = 0;
    volatile ULONGLONG g_getTabsLastTick = 0;
    // Re-scan cadence for the live-screen lookup. The first dispatch after OnEnter runs immediately
    // (last==0); the WBP_Settings_Screen_C widget is usually not constructed yet, so we retry on later
    // dispatches. This throttle exists ONLY to keep the O(GObjects) instance walk off the full Tick
    // dispatch rate (~525 Hz) - one scan per frame catches the screen within ~1 frame of going live,
    // turning the old ~1 s open-to-tab latency into a few ms. Lower further only if the GObjects walk
    // stays cheap; 0 would scan on every dispatch and hammer the walk.
    constexpr ULONGLONG kGetTabsScanIntervalMs = 16;   // ~1 frame @ 60 fps
    // Safety backstop if the screen never appears: maxAttempts * interval ~= give-up window (~4.8 s).
    constexpr LONG     kGetTabsMaxAttempts = 300;

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
    volatile LONG g_injectDone  = 0;

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
        return obj;
    }

    // The MUTATING liveness test (gated on qm_modtab_inject.txt). Strictly one-shot via CAS - it drives
    // a tab navigation which re-enters the rider, so the latch must be claimed first.
    void TryLivenessInjectDupTab(QmUE::UObject* screen)
    {
        if (InterlockedCompareExchange(&g_injectDone, 1, 0) != 0) return;
        if (!screen) return;

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

    void TryDumpTabsViaGetTabs()
    {
        if (g_getTabsDone) return;

        ULONGLONG now  = GetTickCount64();
        ULONGLONG last = g_getTabsLastTick;
        if (last != 0 && (now - last) < kGetTabsScanIntervalMs) return;   // cap the GObjects instance walk to ~1/frame
        g_getTabsLastTick = now;

        LONG attempt = InterlockedIncrement(&g_getTabsAttempts);

        QmUE::UObject* screen = QmUE::FindFirstInstanceOfClass(kSettingsScreenClass);
        if (!screen)
        {
            if (attempt >= kGetTabsMaxAttempts)
            {
                InterlockedExchange(&g_getTabsDone, 1);
                QM_LOG_WARN("[ModTab] GetTabs: no live '%s' instance after %ld attempts - giving up",
                            kSettingsScreenClass, attempt);
            }
            return;   // retry on a later dispatch until the screen widget exists
        }

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

        // Claim the one-shot latch BEFORE dispatching: GetTabs itself runs through ProcessInternal,
        // so our own rider re-enters during the call - the CAS makes the active call strictly once.
        if (InterlockedCompareExchange(&g_getTabsDone, 1, 0) != 0) return;

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

        // MUTATING liveness test - only when qm_modtab_inject.txt is present. Recon dump above always
        // runs (logs the pre-inject Num=5); this appends a duplicate + forces a re-cook.
        if (g_injectArmed) TryLivenessInjectDupTab(screen);
        // NOTE: the returned TArray's heap backing-store is intentionally leaked - we have no element
        // destructor and this runs once (latched). For a recon-only path the one-shot leak is fine.
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
                                             : "OnTabsStateChanged";
        char slf[352];
        DescribeObject(self, slf, sizeof(slf));
        int32_t psize = ParmsSize(func);
        QM_LOG_INFO("[ModTab] #%ld %-18s self=0x%p %s parms=0x%p parmsSize=%d",
                    n, what, (void*)self, slf, parms, psize);

        // CookTabs / SetData carry the tab data - dump the parms + hunt the array. The state-
        // changed callback usually has a tiny/empty parms, so just note its size.
        if ((v & (MT_COOKTABS | MT_SETDATA)) && parms && psize > 0)
        {
            int32_t cap = psize < kMaxParmsDump ? psize : kMaxParmsDump;
            HexDump("parms", reinterpret_cast<const uint8_t*>(parms), cap);
            ScanForTArrays(reinterpret_cast<const uint8_t*>(parms), cap);
        }
    }
    __except (EXCEPTION_EXECUTE_HANDLER) {}
}
