// Logging-only diagnostics: settings-class UFunction enumeration + tab/registry/parms layout
// dumps. Nothing in here modifies game state.

#define _CRT_SECURE_NO_WARNINGS
#include <windows.h>
#include <stdint.h>
#include <stdio.h>
#include <string.h>

#include "qm_modtab_internal.hpp"
#include "qm_log.hpp"

using namespace ModTab;

namespace
{
    constexpr uint32_t MT_FUNC_Native = 0x00000400;

    struct EnumTarget { const char* className; volatile LONG done; };
    EnumTarget g_enumTargets[] = {
        { "BP_Settings_SC_C",           0 },
        { "WBP_Settings_Screen_C",      0 },
        { "WBP_MetaUI_TabsGroup_C",     0 },
        { "WBP_MetaUI_Tab_Main_C",      0 },
        { "WBP_Settings_EntryHeader_C", 0 },   // header-row label setter (AddHeaderRow candidates)
    };
    constexpr int kEnumTargetCount = (int)(sizeof(g_enumTargets) / sizeof(g_enumTargets[0]));

    volatile LONG      g_enumAllDone  = 0;
    volatile LONG      g_enumAttempts = 0;
    volatile ULONGLONG g_enumLastTick = 0;
    constexpr LONG     kEnumMaxAttempts = 25;

    constexpr const char* kGetTabsFuncName = "GetTabs";
    volatile LONG g_getTabsDone = 0;   // one-shot (first open only)

    // Element-head dump window: covers the UGameSetting layout incl. the child-settings
    // TArray at +0x128 (UGameSettingCollection::Settings).
    constexpr int32_t kMaxTabObjDump = 0x140;

    // Walk a class' own + (bounded) inherited Children, logging each UFunction's name, flags
    // and exec pointer. exec==ProcessInternal means a BP function; otherwise a native thunk.
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

    // GetDevName (FName) + GetDisplayName (FText) on a live tab collection. Both are native
    // getters and dispatch through ProcessEvent directly. The returned FText holds one AddRef'd
    // shared ref we intentionally leak (recon-only, bounded).
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

    // The GetTabs return value is a TArray<UObject*> (UGameSettingCollection elements).
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
            if (tab) DumpCollectionLabels(tab, i);
        }

        QmUE::UObject* first = nullptr;
        __try { first = elems[0]; }
        __except (EXCEPTION_EXECUTE_HANDLER) { first = nullptr; }
        if (!first) { QM_LOG_INFO("[ModTab]   tab[0] is null - cannot dump element layout"); return; }

        QM_LOG_INFO("[ModTab]   tab[0] layout dump (first %d bytes - hunt label FText/FString + index/icon fields):",
                    kMaxTabObjDump);
        HexDump("tab[0]", reinterpret_cast<const uint8_t*>(first), kMaxTabObjDump);
    }

    // Compare the GetTabs by-value copy against the live backings (Screen::Tabs +
    // Registry::TopLevelSettings) to confirm the injection point.
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

        // Identity-list TopLevelSettings (no getter calls -> no ProcessInternal re-entrancy).
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
}

namespace ModTab
{
    // Driven from the rider on settings-screen dispatches (classes are live in GObjects then).
    // FindClassByName is a full GObjects scan, so this throttles to <=1 scan-pass/sec, is
    // attempt-capped, and latches off once every target class has been enumerated.
    void TryEnumerateSettingsClasses()
    {
        if (g_enumAllDone) return;

        ULONGLONG now  = GetTickCount64();
        ULONGLONG last = g_enumLastTick;
        if (last != 0 && (now - last) < 1000) return;
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

    // One-time GetTabs array-layout dump (first open only). Caller holds the rebuild guard
    // (the GetTabs dispatch re-enters the rider).
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
            DumpTabArrayElements(reinterpret_cast<const uint8_t*>(buf));
        }

        void* getTabsData = ok ? ReadPtr(buf) : nullptr;
        DumpLiveTabBacking(screen, getTabsData);
        // The returned TArray's heap backing is intentionally leaked - no element destructor
        // is available and this runs once.
    }
}
