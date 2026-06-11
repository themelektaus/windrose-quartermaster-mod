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
    // FSlateColor: FLinearColor + ColorUseRule (0 = use the specified color).
    struct P_SetColorAndOpacity { float R, G, B, A; uint8_t Rule; uint8_t _pad[3]; };

    constexpr uintptr_t kOff_UserWidget_WidgetTree = 0x2D8;   // UUserWidget::WidgetTree  (SDK)
    constexpr uintptr_t kOff_WidgetTree_RootWidget = 0x30;    // UWidgetTree::RootWidget  (SDK)

    // The game's own themed button widget (tiled art + txt_Name + SetData(FText) label setter).
    constexpr const char* kArtButtonClass = "WBP_ArtButton_TiledText_C";

    // ---- panel content ------------------------------------------------------------------------
    constexpr const wchar_t* kTxtTitle    = L"Quartermaster - Made by TheMelekTaus";
    constexpr const wchar_t* kTxtDesc     = L"Konfigurierbare Mods und Tweaks - gebaut mit dem Quartermaster-Configurator.";
    constexpr const wchar_t* kTxtSubtitle = L"Aktive Mods";
    // Placeholder rows until the active mods / settings / modified values are wired up.
    constexpr const wchar_t* kTxtDummy[] = {
        L"(Platzhalter) Aktive Mods werden hier gelistet",
        L"(Platzhalter) Einstellungen und modifizierte Werte folgen",
    };
    constexpr const wchar_t* kTxtNexusBtn = L"Visit nexusmods.com";

    constexpr float kColTitle[4]    = { 1.00f, 0.80f, 0.35f, 1.0f };
    constexpr float kColDesc[4]     = { 0.78f, 0.78f, 0.78f, 1.0f };
    constexpr float kColSubtitle[4] = { 0.92f, 0.88f, 0.62f, 1.0f };

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

    // One text row: spawn a TextBlock (Outer = the screen's WidgetTree), set text + optional
    // color, append it to the panel. Returns the TextBlock on success.
    QmUE::UObject* AddTextRow(QmUE::UObject* panel, QmUE::UObject* widgetTree,
                              const wchar_t* text, const float* rgba)
    {
        if (!panel || !panel->Class) return nullptr;
        QmUE::UClass* txtClass = QmUE::FindClassByName("TextBlock");
        if (!txtClass) return nullptr;
        QmUE::UObject* txt = QmUE::SpawnObjectViaUFunction(txtClass, widgetTree);
        if (!txt || !txt->Class) return nullptr;

        if (QmUE::UFunction* fnSet = QmUE::FindFunctionOnClass(txt->Class, "SetText"))
        {
            uint8_t ft[16]; memset(ft, 0, sizeof(ft));
            if (QmUE::TextFromString(text, ft)) QmUE::CallProcessEvent(txt, fnSet, ft);
        }
        if (rgba)
            if (QmUE::UFunction* fnCol = QmUE::FindFunctionOnClass(txt->Class, "SetColorAndOpacity"))
            {
                P_SetColorAndOpacity c; memset(&c, 0, sizeof(c));
                c.R = rgba[0]; c.G = rgba[1]; c.B = rgba[2]; c.A = rgba[3];
                QmUE::CallProcessEvent(txt, fnCol, &c);
            }
        if (QmUE::UFunction* fnAdd = QmUE::FindFunctionOnClass(panel->Class, "AddChild"))
        {
            P_AddChild ac; ac.Content = txt; ac.ReturnValue = nullptr;
            if (QmUE::CallProcessEvent(panel, fnAdd, &ac) && ac.ReturnValue) return txt;
        }
        return nullptr;
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
            g_ourPanel    = nullptr;
            g_nexusButton = nullptr;   // dies with its panel; re-latched by the content build
        }

        // Owning player (Create's 3rd arg).
        QmUE::UObject* owningPlayer = nullptr;
        if (QmUE::UFunction* fnOP = QmUE::FindFunctionOnClass(screen->Class, "GetOwningPlayer"))
        {
            P_GetOwningPlayer op; op.ReturnValue = nullptr;
            if (QmUE::CallProcessEvent(screen, fnOP, &op)) owningPlayer = reinterpret_cast<QmUE::UObject*>(op.ReturnValue);
        }
        char opid[352]; DescribeObject(owningPlayer, opid, sizeof(opid));
        QM_LOG_INFO("[ModTab]   view: GetOwningPlayer -> 0x%p %s", (void*)owningPlayer, opid);

        QmUE::UClass*    wblClass  = QmUE::FindClassByName("WidgetBlueprintLibrary");
        QmUE::UObject*   wblCDO    = wblClass ? QmUE::GetClassDefaultObject(wblClass) : nullptr;
        QmUE::UFunction* fnCreate  = wblClass ? QmUE::FindFunctionOnClass(wblClass, "Create") : nullptr;

        // Our own ScrollBox panel (Outer = the screen's WidgetTree).
        QmUE::UClass*  scrollClass = QmUE::FindClassByName("ScrollBox");
        QmUE::UObject* ourPanel    = scrollClass ? QmUE::SpawnObjectViaUFunction(scrollClass, widgetTree) : nullptr;
        char opnl[352]; DescribeObject(ourPanel, opnl, sizeof(opnl));
        QM_LOG_WARN("[ModTab]   view: own ScrollBox class=0x%p -> panel=0x%p %s (%s)", (void*)scrollClass,
                    (void*)ourPanel, opnl, ourPanel ? "CONSTRUCTED" : "construct FAILED");

        // Panel content: title / description / "Aktive Mods" subtitle / placeholder rows / nexus
        // link button. Strictly additive + SEH-isolated: any failure is swallowed and never
        // aborts the panel mount.
        if (ourPanel)
        {
            __try
            {
                int rows = 0;
                if (AddTextRow(ourPanel, widgetTree, kTxtTitle,    kColTitle))    ++rows;
                if (AddTextRow(ourPanel, widgetTree, kTxtDesc,     kColDesc))     ++rows;
                if (AddTextRow(ourPanel, widgetTree, kTxtSubtitle, kColSubtitle)) ++rows;
                for (const wchar_t* dummy : kTxtDummy)
                    if (AddTextRow(ourPanel, widgetTree, dummy, nullptr)) ++rows;

                // Prefer the game-themed button: only ITS click is observable (it re-dispatches
                // the inner button's click as BndEvt__*_OnClick via ProcessEvent). The raw
                // Button+TextBlock fallback keeps the row visible but stays unwired (an empty
                // OnClicked broadcast dispatches nothing).
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
                            if (QmUE::TextFromString(kTxtNexusBtn, ft))
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
                            if (QmUE::TextFromString(kTxtNexusBtn, ft))
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
                g_nexusButton = (mounted && themed) ? btn : nullptr;
                QM_LOG_WARN("[ModTab]   view: CONTENT build rows=%d/%d nexusBtn=0x%p themed=%d labelled=%d "
                            "mounted=%d (click %s)", rows,
                            3 + (int)(sizeof(kTxtDummy) / sizeof(kTxtDummy[0])), (void*)btn,
                            themed, labelled, mounted,
                            g_nexusButton ? "wired via the PE BndEvt watch" : "NOT wired - raw fallback");
            }
            __except (EXCEPTION_EXECUTE_HANDLER) { QM_LOG_WARN("[ModTab]   view: CONTENT build FAULTED"); }
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
