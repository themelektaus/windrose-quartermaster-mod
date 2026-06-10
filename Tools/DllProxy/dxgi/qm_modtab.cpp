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
    // the three decisive functions are never capped).
    constexpr LONG kMaxTraceLines = 80;
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
    constexpr uint8_t MT_DECISIVE = MT_COOKTABS | MT_SETDATA | MT_TABSTATE;
    constexpr uint8_t MT_ANY      = MT_DECISIVE | MT_TRACE;

    struct MtFuncMemo { void* fn; volatile uint8_t verdict; };
    constexpr uint32_t kMemoMask = (1u << 13) - 1;   // 8192 slots
    MtFuncMemo g_memo[kMemoMask + 1] = {};

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
                 ContainsLc(clsNm, "settings_sc"))                            v |= MT_TRACE;
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

    // UFunction parms-buffer size = UStruct::StructSize (PropertiesSize) for the function.
    int32_t ParmsSize(QmUE::UFunction* func)
    {
        int32_t sz = 0;
        __try { sz = func->StructSize; }
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
    constexpr LONG     kGetTabsMaxAttempts = 25;

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

    void TryDumpTabsViaGetTabs()
    {
        if (g_getTabsDone) return;

        ULONGLONG now  = GetTickCount64();
        ULONGLONG last = g_getTabsLastTick;
        if (last != 0 && (now - last) < 1000) return;   // <=1 GObjects scan/sec (instance lookup walks GObjects)
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
            if (InterlockedIncrement(&g_traceCount) > kMaxTraceLines) return;
            char fnNm[128] = { 0 }, slf[352];
            QmUE::ResolveFNameNarrow(func->Name, fnNm, sizeof(fnNm));
            DescribeObject(self, slf, sizeof(slf));
            QM_LOG_INFO("[ModTab] trace: %s on %s", fnNm[0] ? fnNm : "?", slf);
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
