// Tab data layer (MUTATING): builds the "Quartermaster"
// UGameSettingCollection and appends it to the live tab arrays. Runs on the game thread inside
// a BP dispatch, so there is no cross-thread race with Slate; arrays are only appended in-place
// when there is spare capacity (Num < Max), so no FMalloc realloc ever happens from our thread.

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
    constexpr const char* kTabWidgetClass = "WBP_MetaUI_Tab_Main_C";
    // The only proven tab-bar reconcile is a real tab-button click; this is its BP entry point.
    // Invoking it on a live tab widget instance (`this` = that tab) replicates the click.
    constexpr const char* kTabClickDelegate =
        "BndEvt__WBP_MetaUI_Tab_btn_Root_K2Node_ComponentBoundEvent_0_OnButtonClickedEvent__DelegateSignature";

    // Native UGameSetting framework-link fields (GameSettings_classes.hpp, Dumper-7 SDK). A
    // constructed collection needs these copied from a sibling so the framework treats it as a
    // real tab.
    constexpr uintptr_t kOff_GS_LocalPlayer    = 0x70;
    constexpr uintptr_t kOff_GS_SettingParent  = 0x78;
    constexpr uintptr_t kOff_GS_OwningRegistry = 0x80;

    // Our injected collection (the tab DATA object), captured once at inject. Only ever
    // COMPARED against array elements, never dereferenced.
    void* g_ourCollection = nullptr;

    // In-place append to a TArray<T*> header, ONLY if there is spare capacity (no realloc).
    // Writes the element into the reserved slot first, then publishes the bumped Num.
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
            QM_LOG_DEBUG("[ModTab]   inject: %s appended dup 0x%p at index %d -> Num now %d (Max=%d, no realloc)",
                         tag, dupPtr, num, result, max);
        }
        __except (EXCEPTION_EXECUTE_HANDLER)
        {
            QM_LOG_WARN("[ModTab]   inject: %s append FAULTED", tag);
            result = -1;
        }
        return result;
    }

    // Call a BlueprintCallable function to drive a UI refresh. onlyIfParameterless: a function
    // with parameters would receive our zeroed buffer (e.g. an EMPTY array) and could clobber
    // or fault - log the real size and skip instead.
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
        QM_LOG_DEBUG("[ModTab]   inject: called '%s' (parmsSize=%d) ok=%d", fnName, sz, ok ? 1 : 0);
        return ok;
    }

    // Locate the unnamed DevName (FName) + DisplayName (FText) native fields inside a live
    // source collection by matching what its getters return. The fields are UNNAMED natives
    // with no UFunction setters, so the later raw-poke needs empirically verified offsets.
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
        QM_LOG_DEBUG("[ModTab]   build: src getters -> DevName{ci=%d num=%d} DisplayName.TextData=0x%p",
                     srcDev.ComparisonIndex, srcDev.Number, srcTextData);
        if (srcDev.ComparisonIndex == 0 && !srcTextData)
        {
            QM_LOG_WARN("[ModTab]   build: getters returned nothing usable (devIdx=%d textData=0x%p)",
                        srcDev.ComparisonIndex, srcTextData);
            return false;
        }

        // Scan the whole native member region [0x28,0x128) - the unnamed fields live in one of
        // the SDK's pad windows. Hexdump first so the offsets are readable if a match misses.
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
                        QM_LOG_DEBUG("[ModTab]   build: DevName FName located @ +0x%llx (idx=%d num=%d)",
                                     (unsigned long long)o, ci, nm);
                    }
                }
                if (*dispOff == 0 && srcTextData && (o % 8) == 0)
                {
                    void* p = *reinterpret_cast<void* const*>(base + o);
                    if (p == srcTextData)
                    {
                        *dispOff = o;
                        QM_LOG_DEBUG("[ModTab]   build: DisplayName FText located @ +0x%llx (TextData=0x%p)",
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

    // Construct a real "Quartermaster" UGameSettingCollection (own DevName + DisplayName).
    // The collection's Settings array stays EMPTY on purpose: it only owns the named tab; the
    // tab CONTENT is our own mounted widget panel (ProbeViewPath). UGameSetting children built
    // from scratch are a dead end - without the native (unreflectable) Initialize() they render
    // as collapsed pills and AV when the panel rebuilds their rows.
    // Returns nullptr on any failure (caller falls back to the dup-append).
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
        QM_LOG_DEBUG("[ModTab]   build: constructed %s", nid);

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
            // Framework links copied from the sibling so the panel treats it as a real tab.
            *reinterpret_cast<void**>(nb + kOff_GS_LocalPlayer)    = *reinterpret_cast<void* const*>(sb2 + kOff_GS_LocalPlayer);
            *reinterpret_cast<void**>(nb + kOff_GS_SettingParent)  = *reinterpret_cast<void* const*>(sb2 + kOff_GS_SettingParent);
            *reinterpret_cast<void**>(nb + kOff_GS_OwningRegistry) = *reinterpret_cast<void* const*>(sb2 + kOff_GS_OwningRegistry);
        }
        __except (EXCEPTION_EXECUTE_HANDLER)
        {
            QM_LOG_WARN("[ModTab]   build: field poke FAULTED");
            return nullptr;
        }

        // Verify: read the label back through the getter (inline FText reads cleanly, unlike
        // the localized stock labels).
        char chk[160] = { 0 };
        if (QmUE::UFunction* dnFn = QmUE::FindFunctionOnClass(obj->Class, "GetDisplayName"))
        {
            uint8_t pb[16]; memset(pb, 0, sizeof(pb));
            if (QmUE::CallProcessEvent(obj, dnFn, pb)) ReadFTextNarrow(pb, chk, sizeof(chk));
        }
        QM_LOG_DEBUG("[ModTab]   build: poked DevName@+0x%llx + DisplayName@+0x%llx -> readback label='%s'",
                     (unsigned long long)devOff, (unsigned long long)dispOff, chk[0] ? chk : "<empty>");

        return obj;
    }
}

namespace ModTab
{
    // Is our injected collection still present in Screen::Tabs? False when never injected or a
    // native re-cook wiped it - both mean "(re)inject needed". Pointer compared, never deref'd,
    // so a stale/freed g_ourCollection is safe to test.
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

    // Identity test for the tab-state gate: is Screen::Tabs[idx] our injected collection?
    // Pointer compared, never deref'd.
    bool IsOurCollectionAt(QmUE::UObject* screen, int32_t idx)
    {
        if (!screen || !g_ourCollection || idx < 0) return false;
        ArrHdr tabs = ReadArrHdr(reinterpret_cast<const uint8_t*>(screen) + kOff_Screen_Tabs);
        if (!tabs.ok || !tabs.data || idx >= tabs.num || tabs.num > 4096) return false;
        bool ours = false;
        __try { ours = (reinterpret_cast<void* const*>(tabs.data)[idx] == g_ourCollection); }
        __except (EXCEPTION_EXECUTE_HANDLER) { ours = false; }
        return ours;
    }

    // Append-only inject: build the Quartermaster collection and append it to the live tab
    // arrays when absent. NO UI reconcile here - the caller owns that (the CookTabs-pre call
    // relies on the native cook that follows; the self-heal bootstrap simulates a tab click).
    // Skipped when already present: the Registry append persists across reopens (every fresh
    // screen sources its Tabs from it), and re-appending would stack a duplicate tab.
    // Returns true only when it appended just now.
    bool EnsureTabInjected(QmUE::UObject* screen)
    {
        if (!screen) return false;
        if (OurCollectionPresentInTabs(screen)) return false;

        QM_LOG_INFO("[ModTab] *** TAB INJECT *** appending our collection to the live tab arrays "
                    "(this MUTATES game state)");

        const uint8_t* sb = reinterpret_cast<const uint8_t*>(screen);
        QmUE::UObject* registry = reinterpret_cast<QmUE::UObject*>(ReadPtr(sb + kOff_Screen_Registry));

        // Duplicate source: TopLevel[0]; fall back to Screen::Tabs[0].
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
            return false;
        }
        char did[352]; DescribeObject(reinterpret_cast<QmUE::UObject*>(dupPtr), did, sizeof(did));
        QM_LOG_DEBUG("[ModTab]   inject: sibling/source collection = 0x%p %s", dupPtr, did);

        // Prefer a real constructed "Quartermaster" collection; fall back to the dup append so
        // the run still yields a visible tab + a clear log of what to fix.
        void*       injectPtr  = nullptr;
        const char* injectKind = "dup (fallback)";
        QmUE::UObject* realColl = BuildQuartermasterCollection(registry, reinterpret_cast<QmUE::UObject*>(dupPtr));
        if (realColl) { injectPtr = realColl; injectKind = "real Quartermaster collection"; }
        else            injectPtr = dupPtr;
        QM_LOG_DEBUG("[ModTab]   inject: appending %s (0x%p)", injectKind, injectPtr);
        g_ourCollection = injectPtr;   // aligned ptr write, atomic on x64

        // Append to BOTH lists (separate backing stores).
        int32_t appended = AppendDupToArray(const_cast<uint8_t*>(sb) + kOff_Screen_Tabs, injectPtr, "Screen::Tabs");
        if (registry)
            AppendDupToArray(const_cast<uint8_t*>(reinterpret_cast<const uint8_t*>(registry)) + kOff_Reg_TopLevel,
                             injectPtr, "Registry::TopLevelSettings");
        return appended > 0;
    }

    // Self-heal bootstrap: panel mount + inject + forced bar reconcile. Only the fallback for a
    // dead CookTabs hook - once that hook is live, its cook-pre inject runs before any cook and
    // this path never sees an absent tab. Caller holds the rebuild guard - the ProcessEvent
    // dispatches below re-enter the rider, and the guard makes those polls no-ops.
    void TryLivenessInjectDupTab(QmUE::UObject* screen, bool bootstrapMount)
    {
        if (!screen) return;

        // Bootstrap mount: this self-heal path owns the panel mount until the CookTabs-post
        // hook has proven itself (a hook that never fires must never regress the first open).
        if (bootstrapMount)
            ProbeViewPath(screen);

        if (!EnsureTabInjected(screen))
            return;

        // Nothing re-cooked the bar for this append (the cook hook was not the injector):
        // force the reconcile via a simulated tab click - the only proven path (the BP nav
        // functions only switch the active content; CookTabs takes args we cannot fake).
        QmUE::UObject* tabw = QmUE::FindFirstInstanceOfClass(kTabWidgetClass);
        if (!tabw)
        {
            QM_LOG_WARN("[ModTab]   inject: no live '%s' - cannot simulate a tab click (tab will appear "
                        "on the next manual tab switch)", kTabWidgetClass);
            return;
        }
        char tid[352]; DescribeObject(tabw, tid, sizeof(tid));
        QM_LOG_DEBUG("[ModTab]   inject: simulating tab-button click on %s", tid);
        CallNavRefresh(tabw, kTabClickDelegate, /*onlyIfParameterless=*/false);

        ArrHdr after = ReadArrHdr(reinterpret_cast<const uint8_t*>(screen) + kOff_Screen_Tabs);
        QM_LOG_DEBUG("[ModTab]   inject: POST-click Screen::Tabs Num=%d Max=%d Data=0x%p",
                     after.num, after.max, after.data);
    }
}
