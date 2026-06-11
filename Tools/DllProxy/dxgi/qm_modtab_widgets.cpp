// Reflected UMG layer: widget-tree walks, live-instance resolvers, visibility primitives and
// the panel build + mount (ProbeViewPath).

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
    // Exact param layouts (Dumper-7 UMG_parameters.hpp). Buffers may be oversized; ProcessEvent
    // only touches the function's own properties, so trailing pad is harmless.
    struct P_Create          { void* WorldContextObject; void* WidgetType; void* OwningPlayer; void* ReturnValue; };
    struct P_GetChildCount   { int32_t ReturnValue; int32_t _pad; };
    struct P_GetChildAt      { int32_t Index; int32_t _pad; void* ReturnValue; };
    struct P_GetOwningPlayer { void* ReturnValue; };
    struct P_AddChild        { void* Content; void* ReturnValue; };
    struct P_SetVisibility   { uint8_t InVisibility; uint8_t _pad[7]; };
    struct P_GetVisibility   { uint8_t ReturnValue; uint8_t _pad[7]; };

    constexpr uintptr_t kOff_UserWidget_WidgetTree = 0x2D8;   // UUserWidget::WidgetTree  (SDK)
    constexpr uintptr_t kOff_WidgetTree_RootWidget = 0x30;    // UWidgetTree::RootWidget  (SDK)

    constexpr const char* kEntrySwitcherClass = "WBP_Settings_EntrySwitcher_C";
    // The game's own themed button widget (tiled art + txt_Name + SetData(FText) label setter).
    constexpr const char* kArtButtonClass     = "WBP_ArtButton_TiledText_C";

    // Recursively log a widget subtree. A node without GetChildrenCount (not a UPanelWidget) is
    // a leaf. Depth- and budget-capped. Up to two independent FIRST-match captures by
    // case-insensitive "Class'Name'" substring.
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
        if (!fnCount || !fnAt) return;
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

}

namespace ModTab
{
    bool SetWidgetVisibility(QmUE::UObject* widget, uint8_t vis)
    {
        if (!widget || !widget->Class) return false;
        QmUE::UFunction* fn = QmUE::FindFunctionOnClass(widget->Class, "SetVisibility");
        if (!fn) return false;
        P_SetVisibility p; memset(&p, 0, sizeof(p)); p.InVisibility = vis;
        return QmUE::CallProcessEvent(widget, fn, &p);
    }

    // Actual ESlateVisibility currently on the widget (not just "did SetVisibility dispatch").
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

    // Build + mount our content panel into the screen's content VerticalBox.
    //
    // Invariants this encodes (all empirically proven, none reflectable):
    //  - The panel must be built FRESH on every (re)open: a reused widget keeps a dead Slate
    //    realization across a reopen and renders nothing despite a byte-identical reflected
    //    state (Visible, correctly parented). Any prior panel is detached + discarded first;
    //    the orphans are left to GC.
    //  - The mount target is the content VerticalBox; our panel is a SIBLING of the native
    //    Settings_Panel (its content host is a data-driven GameSettingListView that cannot be
    //    AddChild'd into). The tab-state gate, not the container, makes the panel tab-scoped.
    //  - The panel starts Collapsed (Quartermaster is never the default tab on open); the
    //    tab-state gate flips visibility per selection, inverse-gating the native panel.
    //  - When the content box is not resolvable yet (tree mid-rebuild), defer cleanly to the
    //    next call - no detach, no construct, no leak.
    void ProbeViewPath(QmUE::UObject* screen)
    {
        if (!screen) return;
        QM_LOG_WARN("[ModTab] *** VIEW-PATH *** map screen tree + build our own ScrollBox as a sibling of "
                    "Settings_Panel, fill it, mount it into the content VerticalBox, start it hidden "
                    "(visibility owned by the tab-state gate)");

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

        QmUE::UObject* mountTarget   = nullptr;   // first VerticalBox in the reachable tree
        QmUE::UObject* settingsPanel = nullptr;   // WBP_Settings_Panel_C (per-tab content host)
        if (rootWidget)
        {
            int budget = 250;
            __try { DumpWidgetSubtree(rootWidget, 0, budget, "verticalbox", &mountTarget,
                                      "settings_panel", &settingsPanel); }
            __except (EXCEPTION_EXECUTE_HANDLER) { QM_LOG_WARN("[ModTab]   view: tree walk FAULTED"); }
        }
        else QM_LOG_WARN("[ModTab]   view: no RootWidget - cannot map tree");

        if (!mountTarget)
        {
            QM_LOG_INFO("[ModTab]   view: content VerticalBox not resolved yet (tree mid-rebuild) - deferring (re)build to next poll");
            return;
        }

        // Discard any prior panel (dead Slate on reopen - see the invariants above).
        if (g_ourPanel)
        {
            QmUE::UObject* panel = reinterpret_cast<QmUE::UObject*>(g_ourPanel);
            __try
            {
                if (panel->Class)
                    if (QmUE::UFunction* fnRm = QmUE::FindFunctionOnClass(panel->Class, "RemoveFromParent"))
                    {
                        char rmbuf[16]; memset(rmbuf, 0, sizeof(rmbuf));
                        QmUE::CallProcessEvent(panel, fnRm, rmbuf);
                    }
            }
            __except (EXCEPTION_EXECUTE_HANDLER) {}
            QM_LOG_WARN("[ModTab] *** FRESH REBUILD *** discarded stale panel=0x%p (dead Slate on "
                        "reopen) - building a brand-new ScrollBox into content VerticalBox 0x%p",
                        (void*)g_ourPanel, (void*)mountTarget);
            g_ourPanel = nullptr;
        }

        // Diagnostic: map Settings_Panel's INNER WidgetTree (its real per-tab content lives
        // there, behind the UserWidget boundary the outer walk stops at).
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

        // Owning player (Create's 3rd arg).
        QmUE::UObject* owningPlayer = nullptr;
        if (QmUE::UFunction* fnOP = QmUE::FindFunctionOnClass(screen->Class, "GetOwningPlayer"))
        {
            P_GetOwningPlayer op; op.ReturnValue = nullptr;
            if (QmUE::CallProcessEvent(screen, fnOP, &op)) owningPlayer = reinterpret_cast<QmUE::UObject*>(op.ReturnValue);
        }
        char opid[352]; DescribeObject(owningPlayer, opid, sizeof(opid));
        QM_LOG_INFO("[ModTab]   view: GetOwningPlayer -> 0x%p %s", (void*)owningPlayer, opid);

        QmUE::UClass* entryClass = QmUE::FindClassByName(kEntrySwitcherClass);
        QM_LOG_INFO("[ModTab]   view: class '%s' = 0x%p (%s)", kEntrySwitcherClass, (void*)entryClass,
                    entryClass ? "loaded" : "NOT loaded - round 2 must LoadAsset it");

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

        if (createdWidget)
        {
            QmUE::UObject* cwt = nullptr;
            __try { cwt = reinterpret_cast<QmUE::UObject*>(
                *reinterpret_cast<void* const*>(reinterpret_cast<const uint8_t*>(createdWidget) + kOff_UserWidget_WidgetTree)); }
            __except (EXCEPTION_EXECUTE_HANDLER) { cwt = nullptr; }
            QM_LOG_INFO("[ModTab]   view: created widget WidgetTree@0x2D8 = 0x%p (%s)", (void*)cwt,
                        cwt ? "constructed" : "null - widget may need further init");
        }

        // Our own ScrollBox panel (Outer = the screen's WidgetTree).
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

        // Button: prefer the game-themed widget (matches native styling); fall back to a raw
        // Button+TextBlock so a button always appears. Strictly additive + SEH-isolated: any
        // failure is swallowed and never aborts the panel mount.
        if (ourPanel)
        {
            __try
            {
                bool themed = false, labelled = false, mounted = false;
                QmUE::UObject* btn = nullptr;

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
                            uint8_t ft[16]; memset(ft, 0, sizeof(ft));   // P_SetData = { FText Data; }
                            if (QmUE::TextFromString(L"Quartermaster", ft))
                                labelled = QmUE::CallProcessEvent(btn, fnSet, ft);
                        }
                    }
                }

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
                    g_mountTarget = mountTarget;
                    g_nativePanel = settingsPanel;
                    bool vok = SetWidgetVisibility(ourPanel, ESV_Collapsed);
                    bool nok = settingsPanel ? SetWidgetVisibility(settingsPanel, ESV_Visible) : false;
                    QM_LOG_WARN("[ModTab]   view: gate INIT -> our panel Collapsed ok=%d, native Settings_Panel(0x%p) Visible ok=%d",
                                vok, (void*)settingsPanel, nok);
                }
            }
        }
        QM_LOG_WARN("[ModTab] *** VIEW-PATH DONE *** panel=0x%p mountTarget=0x%p - starts hidden, the tab-state "
                    "gate shows it on the Quartermaster tab", (void*)g_ourPanel, (void*)mountTarget);
    }

    // Is our panel STILL a live child of the content VerticalBox? False when never mounted OR
    // a re-cook rebuilt the content tree and orphaned it (UObject alive but detached). A stale
    // g_mountTarget faults the walk -> false -> the next mount re-resolves the fresh target.
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
}
