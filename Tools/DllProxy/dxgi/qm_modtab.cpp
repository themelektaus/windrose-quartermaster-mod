// Quartermaster "Mod Settings Tab" - module core: sentinel arming, the ProcessInternal rider
// (lifecycle + click gate), PLSF target resolve + thunk dispatch, and the self-heal driver.
//
// Dispatch-layer invariant the module is built around: the decisive settings functions
// (CookTabs -> TabsGroup.SetData -> OnTabsStateChanged) are called Blueprint-internally, which
// bypasses BOTH public entries - never ProcessEvent (the net-hook only sees engine->script
// dispatch) and never ProcessInternal (that is only the ProcessEvent->Invoke exec for BP
// functions; an in-field-verified ExecFunction swap never fired once). The script VM routes
// BP-internal calls straight into ProcessLocalScriptFunction (PLSF), hooked globally in
// qm_hook.cpp - the same layer UE4SS's HookProcessLocalScriptFunction provides. The
// ProcessInternal rider still sees OnEnter/OnExit (those DO dispatch via ProcessEvent->
// ProcessInternal) and carries the click gate; the PLSF CookTabs-post thunk owns the panel
// mount (CookTabs re-cooks the content tree on EVERY tab click - mounting in its post is the
// only moment the content box's Slate is provably live).

#define _CRT_SECURE_NO_WARNINGS
#include <windows.h>
#include <stdint.h>
#include <stdio.h>
#include <string.h>

#include "qm_modtab.hpp"
#include "qm_modtab_internal.hpp"
#include "qm_log.hpp"

using namespace ModTab;

namespace ModTab
{
    // Shared state (declared in qm_modtab_internal.hpp).
    void* g_liveTabsGroup = nullptr;
    void* g_ourPanel      = nullptr;
    void* g_mountTarget   = nullptr;
    void* g_nativePanel   = nullptr;
}

namespace
{
    bool g_initDone    = false;
    bool g_armed       = false;
    bool g_injectArmed = false;   // qm_modtab_inject.txt present: the MUTATING paths are live

    volatile LONG g_seq        = 0;   // decisive-dispatch sequence number
    volatile LONG g_traceCount = 0;   // bounded lifecycle-trace lines

    // Trace-line budget: keeps the log readable across an open -> click-through -> close
    // window; the decisive functions are never capped.
    constexpr LONG kMaxTraceLines = 400;

    // ---- per-UFunction memoized verdict --------------------------------------------------
    // Name+owner-class resolution runs ONCE per distinct UFunction; the hot path is then a
    // pointer compare + bit test. Direct-mapped; collisions just recompute (benign).
    constexpr uint8_t MT_VALID    = 0x80;
    constexpr uint8_t MT_COOKTABS = 0x01;   // CookTabs            (BP_Settings_SC_C)
    constexpr uint8_t MT_SETDATA  = 0x02;   // SetData             (WBP_MetaUI_TabsGroup_C)
    constexpr uint8_t MT_TABSTATE = 0x04;   // OnTabsStateChanged  (WBP_Settings_Screen_C)
    constexpr uint8_t MT_TRACE    = 0x08;   // other settings-screen lifecycle fn (bounded log)
    constexpr uint8_t MT_NOISE    = 0x10;   // per-frame flood (Tick): drives probes, never logged/capped
    constexpr uint8_t MT_ONEXIT   = 0x20;   // OnExit  (BP_Settings_SC_C): settings closing
    constexpr uint8_t MT_ENTER    = 0x40;   // OnEnter (BP_Settings_SC_C): settings (re)opening
    constexpr uint8_t MT_DECISIVE = MT_COOKTABS | MT_SETDATA | MT_TABSTATE | MT_ONEXIT | MT_ENTER;
    constexpr uint8_t MT_ANY      = MT_DECISIVE | MT_TRACE;

    struct MtFuncMemo { void* fn; volatile uint8_t verdict; };
    constexpr uint32_t kMemoMask = (1u << 13) - 1;   // 8192 slots
    MtFuncMemo g_memo[kMemoMask + 1] = {};

    // ---- click-armed verbose window -------------------------------------------------------
    // The native/Tick-driven rebuild after a tab click is invisible to the rider's normal
    // trace. A tab click arms a short window during which the otherwise-skipped Tick flood is
    // logged - but only the FIRST Tick per distinct widget instance per window (a freshly
    // created widget surfaces as a new line; the steady repeats are suppressed). The tick-seen
    // table is invalidated per window by bumping a generation counter.
    volatile ULONGLONG g_verboseUntilTick = 0;
    volatile LONG      g_verboseGen       = 0;
    volatile LONG      g_verboseLines     = 0;   // session-wide hard cap
    constexpr ULONGLONG kVerboseWindowMs  = 2500;
    constexpr LONG      kMaxVerboseLines  = 600;
    struct TickSeen { void* obj; volatile LONG gen; };
    constexpr uint32_t kTickSeenMask = 255;      // direct-mapped; collisions re-log (benign)
    TickSeen g_tickSeen[kTickSeenMask + 1] = {};

    // ---- session + gate state --------------------------------------------------------------
    bool          g_settingsOpen = false;
    // Our Quartermaster tab WIDGET - the visibility-gate key. A tab click rebuilds the bar
    // (fresh widget instances), so this is re-resolved on each click and only ever COMPARED.
    void*         g_ourTabWidget = nullptr;
    // The LIVE settings screen, latched from OnTabsStateChanged (fires nested inside every
    // cook with self = the live screen, before our CookTabs post-mount). Same stale-instance
    // footgun as g_liveTabsGroup: FindFirstInstanceOfClass returns the lingering un-GC'd
    // screen of a previous open, and a mount through it lands in a detached tree. Cleared on
    // OnExit.
    void*         g_liveScreen = nullptr;
    // 1 while the QM tab is the selected tab (latched by the click gate, cleared on OnExit).
    // CookTabs fires on EVERY tab click and runs AFTER the gate on that same click - its
    // post-mount replaces the just-shown panel with a fresh Collapsed one, so the cook-post
    // re-applies the gate from this latch.
    volatile LONG g_qmTabActive = 0;
    volatile LONG g_tabReconCount = 0;
    constexpr int kMaxTabRecon = 16;   // cap gate-log lines per session (readability)

    // The (re)build re-enters the rider via its own ProcessEvent dispatches (GetTabs, Create,
    // AddChild, the tab-click sim). This guard makes those re-entrant polls no-ops.
    volatile LONG g_rebuildInProgress = 0;

    // Self-heal scan throttle: keeps the O(GObjects) instance walk at ~1/frame instead of the
    // full Tick dispatch rate.
    volatile ULONGLONG g_getTabsLastTick = 0;
    constexpr ULONGLONG kGetTabsScanIntervalMs = 16;

    // ---- PLSF per-UFunction hook state -------------------------------------------------------
    // Resolved targets, matched against FFrame::Node in the global PLSF detour (qm_hook.cpp).
    QmUE::UFunction*     g_fnCookTabs   = nullptr;
    QmUE::UFunction*     g_fnTabState   = nullptr;
    QmUE::UFunction*     g_fnSetData    = nullptr;
    // MinHook trampoline to the real PLSF body (set by qm_hook.cpp BEFORE the detour goes
    // live). The thunks forward through THIS - forwarding through ProcessInternal instead
    // would re-enter the patched PLSF entry and recurse.
    QmUE::FNativeFuncPtr g_plsfOriginal = nullptr;
    // 1 only once our CookTabs thunk has FIRED and its post-mount LANDED. Until then the
    // self-heal bootstrap mount keeps owning the mount, so a hook that never fires can never
    // regress the first open.
    volatile LONG        g_cookTabsHookLive    = 0;
    volatile LONG        g_cookTabsFiredCount  = 0;
    volatile LONG        g_allFnHooksInstalled = 0;   // 1 once every target handle is resolved
    // Early-resolve poll driver state: the settings BP classes lazy-load at first settings
    // open; the global ProcessEvent hook (live from engine start) polls so the handles are
    // latched before the first cook runs.
    volatile ULONGLONG   g_fnHookPollLastTick  = 0;
    constexpr ULONGLONG  kFnHookEarlyPollMs    = 16;
    // Per-open parms-dump budgets (pre-armed so boot-time fires dump too; re-armed on OnEnter).
    constexpr LONG       kFnDumpBudgetPerOpen  = 4;
    volatile LONG        g_tabStateDumpBudget  = kFnDumpBudgetPerOpen;
    volatile LONG        g_setDataDumpBudget   = kFnDumpBudgetPerOpen;

    // ---- PLSF thunks --------------------------------------------------------------------------
    // Each matches FNativeFuncPtr - the exact PLSF signature, with Stack being the function's
    // OWN frame (Node = the function, Locals = its params). Reached ONLY from the PLSF detour's
    // Node match (QmModTab_OnScriptFunction); they forward through the PLSF trampoline and add
    // our pre/post work. SEH around our work only - the forward runs unguarded so the game's
    // own dispatch is never altered.

    // CookTabs POST = the panel (re)mount moment. The whole thunk holds g_rebuildInProgress:
    // calling the original re-enters our ProcessInternal rider (the guard stops the self-heal
    // from injecting/mounting mid-cook), and ProbeViewPath's own PE dispatches re-enter it too.
    void ThunkCookTabs(void* ctx, void* stack, void* result)
    {
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
                    // Mount into the LIVE screen latched by the nested OnTabsStateChanged fire
                    // of THIS cook; FindFirstInstanceOfClass is only the bootstrap fallback
                    // (correct while only a single instance exists).
                    QmUE::UObject* screen = reinterpret_cast<QmUE::UObject*>(g_liveScreen);
                    bool live = (screen != nullptr);
                    if (!screen) screen = QmUE::FindFirstInstanceOfClass(kSettingsScreenClass);
                    if (screen)
                    {
                        QM_LOG_WARN("[ModTab] *** #18r COOKTABS-POST *** native cook done - mounting a fresh panel into "
                                    "the just-cooked content tree of screen=0x%p (%s) - the proven-live Slate moment "
                                    "the reference uses", (void*)screen, live ? "#18t LIVE latch" : "first-instance fallback");
                        ProbeViewPath(screen);   // fresh discard+build+mount, starts Collapsed; self-defers if the box isn't live yet
                        // Hand the mount authority to this hook ONLY once its post-mount
                        // actually LANDED (see g_cookTabsHookLive).
                        if (OurPanelMounted())
                        {
                            InterlockedExchange(&g_cookTabsHookLive, 1);
                            // Gate re-apply: this cook may have been triggered by a QM-tab
                            // click whose gate already ran - the fresh mount just replaced the
                            // shown panel with a Collapsed one. Re-show from the latch; on
                            // native-tab clicks / the reopen cook the latch is 0.
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
        // self IS the live WBP_Settings_Screen_C (only that class carries this UFunction);
        // fires nested inside every cook BEFORE our CookTabs post-mount. Gated on the open
        // session so a teardown-phase fire cannot park a dying pointer here.
        if (ctx && g_settingsOpen) g_liveScreen = ctx;
        QmUE::FNativeFuncPtr orig = g_plsfOriginal;
        if (orig) orig(ctx, stack, result);          // POST: dump the selection state
        __try { DumpFnParms("OnTabsStateChanged", ctx, stack, g_fnTabState, &g_tabStateDumpBudget); }
        __except (EXCEPTION_EXECUTE_HANDLER) {}
    }

    void ThunkSetData(void* ctx, void* stack, void* result)
    {
        __try { DumpFnParms("SetData(TabsGroup)", ctx, stack, g_fnSetData, &g_setDataDumpBudget); }   // PRE: the incoming tab array
        __except (EXCEPTION_EXECUTE_HANDLER) {}
        QmUE::FNativeFuncPtr orig = g_plsfOriginal;
        if (orig) orig(ctx, stack, result);
    }

    // Deferred target-handle resolution. A target whose BP class is not in GObjects yet is
    // retried on the next call; latches off once all three are resolved. Driven primarily from
    // the global ProcessEvent hook (live from engine start), with the rider's self-heal as
    // backstop. Read-only.
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
            if (*t.fnSlot) continue;

            QmUE::UClass* cls = QmUE::FindClassByName(t.cls);
            if (!cls)
            {
                // The settings BP classes load together (same UI package): while the FIRST
                // target's class is still absent, bail after ONE O(GObjects) walk per poll
                // instead of three.
                if (i == 0) return;
                ++remaining; continue;
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

    // Self-heal driver, called from the rider on every settings-screen dispatch: resolves the
    // live screen, runs the one-shot recon dump, injects the tab when absent, and bootstraps
    // the panel mount until the CookTabs-post hook owns it.
    void TryDumpTabsViaGetTabs()
    {
        ULONGLONG now  = GetTickCount64();
        ULONGLONG last = g_getTabsLastTick;
        if (last != 0 && (now - last) < kGetTabsScanIntervalMs) return;
        g_getTabsLastTick = now;

        // Install backstop BEFORE the screen-instance gate: the resolve needs only the CLASSES
        // in GObjects - waiting for an instance would miss a cook that runs before any
        // instance-gated poll.
        TryInstallSettingsFnHooks();

        QmUE::UObject* screen = QmUE::FindFirstInstanceOfClass(kSettingsScreenClass);
        if (!screen) return;   // settings not live yet - keep watching across reopens

        if (g_rebuildInProgress) return;

        // Once the CookTabs-post mount has landed at least once, that hook owns the per-open
        // mount and this poll's job shrinks to injecting the TAB when absent. Until then a
        // present tab with no live panel falls through to the bootstrap mount.
        bool tabPresent = OurCollectionPresentInTabs(screen);
        if (tabPresent && (InterlockedCompareExchange(&g_cookTabsHookLive, 0, 0) || OurPanelMounted())) return;

        if (InterlockedCompareExchange(&g_rebuildInProgress, 1, 0) != 0) return;
        __try
        {
            DumpGetTabsReconOnce(screen);
            if (g_injectArmed)
                TryLivenessInjectDupTab(screen,
                    /*bootstrapMount=*/InterlockedCompareExchange(&g_cookTabsHookLive, 0, 0) == 0);
        }
        __finally { InterlockedExchange(&g_rebuildInProgress, 0); }
    }

    // A UFunction's Outer is its owning UClass - the verdict is fully determined by `func`.
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
            // Tick fires ~77x/frame per tab widget and would flood the trace cap before the
            // user can click. Keep it MT_TRACE (it still drives the probes) but tag it NOISE
            // so the rider skips emission + cap.
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

    // Separate opt-in for the MUTATING paths (tab inject + panel mount): exact sentinel name,
    // not the glob.
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

        // We are inside a settings-screen dispatch, so the settings classes are live in
        // GObjects now. Both probes throttle + latch internally; placed before the trace-cap
        // return so the flood can't starve them.
        TryEnumerateSettingsClasses();
        TryDumpTabsViaGetTabs();

        if (v & MT_TRACE)
        {
            ULONGLONG now = GetTickCount64();

            // Tick flood: skipped outside a verbose window; inside it, only the FIRST Tick per
            // distinct widget instance per window is logged.
            if (v & MT_NOISE)
            {
                if (now >= g_verboseUntilTick) return;
                LONG gen = g_verboseGen;
                TickSeen& ts = g_tickSeen[(((uintptr_t)self) >> 4) & kTickSeenMask];
                if (ts.obj == self && ts.gen == gen) return;
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

            // Latch the LIVE tab bar from its fresh Construct (see g_liveTabsGroup - the
            // global lookup would return the stale, un-GC'd bar of a previous open). Scoped to
            // the open session so an unrelated menu's TabsGroup cannot clobber it.
            if (g_settingsOpen && ContainsLc(fnNm, "construct"))
            {
                char cslf[352]; DescribeObject(self, cslf, sizeof(cslf));
                if (ContainsLc(cslf, "tabsgroup")) g_liveTabsGroup = self;
            }

            // A tab button click: arm the verbose window (before the trace cap so it survives
            // a saturated log) and drive the visibility gate.
            if (ContainsLc(fnNm, "onbuttonclick"))
            {
                InterlockedIncrement(&g_verboseGen);
                g_verboseUntilTick = now + kVerboseWindowMs;
                QM_LOG_WARN("[ModTab] *** VERBOSE WINDOW ARMED *** (%llu ms) by tab click - now logging "
                            "Tick (1st/widget) + Draw + Panel/ListView rebuild dispatches",
                            (unsigned long long)kVerboseWindowMs);

                // GATE: our tab is identified STRUCTURALLY - we inject our collection as the
                // last tab data entry, so ours is the last tab widget in the bar. A click
                // rebuilds the bar (fresh instances), so the last tab_main is re-resolved from
                // the live bar on EVERY click and compared to the clicked self.
                {
                    char tabSlf[352]; DescribeObject(self, tabSlf, sizeof(tabSlf));
                    if (ContainsLc(tabSlf, "tab_main"))
                    {
                        LONG rc = InterlockedIncrement(&g_tabReconCount);
                        QmUE::UObject* ourTab = ResolveOurTabWidget();
                        if (ourTab) g_ourTabWidget = ourTab;
                        bool match = (g_ourTabWidget && self == g_ourTabWidget);
                        // Latch the selection for the CookTabs-post gate re-apply (the cook
                        // this very click triggers runs AFTER this gate).
                        InterlockedExchange(&g_qmTabActive, match ? 1 : 0);
                        // Show our panel only on our tab; INVERSE-gate the native content host
                        // so our panel takes the content area instead of stacking below it.
                        // The panel is already mounted + Slate-realized by the CookTabs-post
                        // hook - this is a pure show/hide. SetWidgetVisibility is SEH-guarded
                        // for the rare teardown window with stale handles.
                        bool setVis = false, setNat = false;
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
                        // Self-verifying readback: the ACTUAL visibility + parent now on the
                        // wire, not just "did SetVisibility dispatch".
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
            // parmsSize disambiguates the rebuild call (carries the tab array) from trivial
            // callbacks (size 0) when reading the click sequence.
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

        // Settings is closing. The screen tree may be pooled across opens, so the handles are
        // KEPT (a genuinely rebuilt tree is healed by the next mount); only the visibility is
        // reset to the closed/default state: our panel Collapsed (a reopen lands on a native
        // tab), native content host Visible (never leave the native settings hidden).
        if (v & MT_ONEXIT)
        {
            InterlockedExchange(&g_qmTabActive, 0);
            if (g_ourPanel)    SetWidgetVisibility(reinterpret_cast<QmUE::UObject*>(g_ourPanel),    ESV_Collapsed);
            if (g_nativePanel) SetWidgetVisibility(reinterpret_cast<QmUE::UObject*>(g_nativePanel), ESV_Visible);
            // Close the session + drop the live-instance latches: both are re-cooked on the
            // next open and a freed pointer must never be walked before the fresh re-latch.
            g_settingsOpen  = false;
            g_liveTabsGroup = nullptr;
            g_liveScreen    = nullptr;
        }

        // Settings is (re)opening. The per-open panel mount is owned by the CookTabs-post
        // hook; OnEnter only opens the session (so the next TabsGroup Construct is latched)
        // and re-arms the per-open diagnostic dump budgets.
        if (v & MT_ENTER)
        {
            g_settingsOpen = true;
            InterlockedExchange(&g_tabStateDumpBudget, kFnDumpBudgetPerOpen);
            InterlockedExchange(&g_setDataDumpBudget,  kFnDumpBudgetPerOpen);
        }

        // CookTabs / SetData carry the tab data, OnTabsStateChanged the new tab selection -
        // dump the parms + hunt the array on all three. OnExit has no useful parms.
        if ((v & (MT_COOKTABS | MT_SETDATA | MT_TABSTATE)) && parms && psize > 0)
        {
            int32_t cap = psize < kMaxParmsDump ? psize : kMaxParmsDump;
            HexDump("parms", reinterpret_cast<const uint8_t*>(parms), cap);
            ScanForTArrays(reinterpret_cast<const uint8_t*>(parms), cap);
        }
    }
    __except (EXCEPTION_EXECUTE_HANDLER) {}
}

// EARLY FN-TARGET RESOLVE DRIVER: ProcessEvent dispatches from engine start on the game thread,
// the earliest safe moment to poll GObjects for the settings BP classes. Cheap fast path once
// resolved/disarmed.
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

void QmModTab_SetPlsfOriginal(QmUE::FNativeFuncPtr orig)
{
    g_plsfOriginal = orig;
}

bool QmModTab_OnScriptFunction(void* context, void* stack, void* result)
{
    // HOT PATH: runs for EVERY Blueprint script-function execution in the game. One guarded
    // read + three pointer compares; the handles stay null until resolved.
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
