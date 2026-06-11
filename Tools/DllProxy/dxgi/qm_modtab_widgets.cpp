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
    // The game's own settings section-header widget (same one the reference mod builds its
    // section titles from). NOT in the offline SDK dump (loads lazily with the settings screen),
    // so its label setter is resolved at runtime from candidates.
    constexpr const char* kEntryHeaderClass = "WBP_Settings_EntryHeader_C";

    // Font clone: FSlateFontInfo is a POD value (object pointers + FName + scalars), so the
    // game font is lifted as a raw byte copy from the themed button's label TextBlock and
    // re-applied per row with only Size overridden.
    constexpr uintptr_t kOff_TextBlock_Font    = 0x1D0;   // UTextBlock::Font            (SDK)
    constexpr uintptr_t kOff_ArtButton_TxtName = 0x350;   // ..ArtButton_TiledText::txt_Name (SDK)
    constexpr uintptr_t kOff_FontInfo_Size     = 0x48;    // FSlateFontInfo::Size        (SDK)
    constexpr size_t    kFontInfoSize          = 0x60;    // sizeof(FSlateFontInfo)      (SDK)

    // Scrollbar style clone: FScrollBarStyle (9 consecutive FSlateBrushes + Thickness) is
    // lifted as a raw byte copy from the native settings list onto our ScrollBox. Footgun:
    // each FSlateBrush hides a non-reflected FSlateResourceHandle (TSharedPtr render cache)
    // that a byte copy would alias WITHOUT bumping the refcount - it must be zeroed in the
    // copy (the renderer re-resolves it lazily).
    constexpr uintptr_t kOff_GameSettingPanel_ListView    = 0x738;  // UGameSettingPanel::ListView_Settings (SDK)
    constexpr uintptr_t kOff_ListView_ScrollBarStyle      = 0x450;  // UListView::ScrollBarStyle      (SDK)
    constexpr uintptr_t kOff_ListView_ScrollBarPadding    = 0xAD0;  // UListView::ScrollBarPadding    (SDK)
    constexpr uintptr_t kOff_ScrollBox_WidgetBarStyle     = 0x4A0;  // UScrollBox::WidgetBarStyle     (SDK)
    constexpr uintptr_t kOff_ScrollBox_ScrollbarThickness = 0xAF8;  // UScrollBox::ScrollbarThickness (SDK)
    constexpr uintptr_t kOff_ScrollBox_ScrollBarPadding   = 0xB08;  // UScrollBox::ScrollBarPadding   (SDK)
    constexpr size_t    kScrollBarStyleSize               = 0x650;  // sizeof(FScrollBarStyle)        (SDK)
    constexpr uintptr_t kBarStyleFirstBrush               = 0x10;   // vtable + pad precede the brushes
    constexpr int       kBarStyleBrushCount               = 9;
    constexpr size_t    kBrushSize                        = 0xB0;   // sizeof(FSlateBrush)            (SDK)
    constexpr uintptr_t kOff_Brush_ResourceHandle         = 0x98;   // non-reflected TSharedPtr cache
    constexpr size_t    kResourceHandleSize               = 0x10;
    constexpr uintptr_t kOff_BarStyle_Thickness           = 0x640;  // FScrollBarStyle::Thickness     (SDK)

    // ---- panel content ------------------------------------------------------------------------
    // All content (texts, sizes, colors, gaps, button commands) comes from the layout rows
    // (qm_modtab_layout.cpp). These are only the per-kind font-size fallbacks for rows that
    // carry none; header sizes apply on the TextBlock fallback only (EntryHeader styles itself).
    constexpr float kDefFontSizeBody   = 16.0f;
    constexpr float kDefFontSizeHeader = 20.0f;

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
        QM_LOG_TRACE("[ModTab]   tree %s%s", indent, wid);
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

    // Lift the live FSlateFontInfo off a TextBlock (raw byte copy - see the constants above).
    bool CloneFontFromTextBlock(QmUE::UObject* txt, uint8_t out[kFontInfoSize])
    {
        if (!txt) return false;
        bool ok = false;
        __try
        {
            memcpy(out, reinterpret_cast<const uint8_t*>(txt) + kOff_TextBlock_Font, kFontInfoSize);
            ok = true;
        }
        __except (EXCEPTION_EXECUTE_HANDLER) {}
        return ok;
    }

    // The native settings list (GameSettingListView'ListView_Settings') - the scrollbar-style
    // donor. Direct member read off the panel first; inner-tree walk as the fallback in case
    // the panel class is not a GameSettingPanel after all (the read is name-validated).
    QmUE::UObject* ResolveNativeSettingsList(QmUE::UObject* settingsPanel)
    {
        if (!settingsPanel) return nullptr;
        QmUE::UObject* lv = nullptr;
        __try
        {
            QmUE::UObject* cand = reinterpret_cast<QmUE::UObject*>(
                *reinterpret_cast<void* const*>(reinterpret_cast<const uint8_t*>(settingsPanel) + kOff_GameSettingPanel_ListView));
            char id[352]; DescribeObject(cand, id, sizeof(id));
            if (cand && ContainsLc(id, "gamesettinglistview")) lv = cand;
        }
        __except (EXCEPTION_EXECUTE_HANDLER) {}
        if (lv) return lv;
        __try
        {
            QmUE::UObject* inner = reinterpret_cast<QmUE::UObject*>(
                *reinterpret_cast<void* const*>(reinterpret_cast<const uint8_t*>(settingsPanel) + kOff_UserWidget_WidgetTree));
            QmUE::UObject* innerRoot = inner ? reinterpret_cast<QmUE::UObject*>(
                *reinterpret_cast<void* const*>(reinterpret_cast<const uint8_t*>(inner) + kOff_WidgetTree_RootWidget)) : nullptr;
            int budget = 120;
            if (innerRoot) DumpWidgetSubtree(innerRoot, 0, budget, "gamesettinglistview", &lv);
        }
        __except (EXCEPTION_EXECUTE_HANDLER) {}
        return lv;
    }

    // Game-theme the scrollbar: raw-copy FScrollBarStyle from the native settings list onto
    // our ScrollBox (the proven raw-clone pattern, see the constants above for the
    // ResourceHandle footgun). Bar geometry (thickness/padding) is mirrored too - SScrollBox
    // takes those from the UScrollBox properties, not from the style.
    bool CloneScrollBarStyle(QmUE::UObject* ourPanel, QmUE::UObject* listView)
    {
        if (!ourPanel || !listView) return false;
        bool ok = false;
        __try
        {
            uint8_t*       dst = reinterpret_cast<uint8_t*>(ourPanel) + kOff_ScrollBox_WidgetBarStyle;
            const uint8_t* src = reinterpret_cast<const uint8_t*>(listView) + kOff_ListView_ScrollBarStyle;
            memcpy(dst + kBarStyleFirstBrush, src + kBarStyleFirstBrush, kScrollBarStyleSize - kBarStyleFirstBrush);
            for (int i = 0; i < kBarStyleBrushCount; ++i)
                memset(dst + kBarStyleFirstBrush + i * kBrushSize + kOff_Brush_ResourceHandle, 0, kResourceHandleSize);

            float thick = *reinterpret_cast<const float*>(src + kOff_BarStyle_Thickness);
            if (thick >= 2.0f && thick <= 64.0f)
            {
                double* tv = reinterpret_cast<double*>(reinterpret_cast<uint8_t*>(ourPanel) + kOff_ScrollBox_ScrollbarThickness);
                tv[0] = thick; tv[1] = thick;   // FVector2D is double-based in UE5
            }
            memcpy(reinterpret_cast<uint8_t*>(ourPanel) + kOff_ScrollBox_ScrollBarPadding,
                   reinterpret_cast<const uint8_t*>(listView) + kOff_ListView_ScrollBarPadding, 0x10);
            ok = true;
        }
        __except (EXCEPTION_EXECUTE_HANDLER) {}
        return ok;
    }

    // Style the ScrollBoxSlot a row landed in: gap above the row (FMargin top), optional left
    // indent (FMargin left) + optional horizontal alignment (255 = leave the slot default, Fill).
    void StyleSlot(QmUE::UObject* slot, float gapAbove, uint8_t hAlign = 255, float padLeft = 0.0f)
    {
        if (!slot || !slot->Class) return;
        if (gapAbove > 0.0f || padLeft > 0.0f)
            if (QmUE::UFunction* fnPad = QmUE::FindFunctionOnClass(slot->Class, "SetPadding"))
            {
                float margin[4] = { padLeft, gapAbove, 0.0f, 0.0f };   // FMargin {L,T,R,B}
                QmUE::CallProcessEvent(slot, fnPad, margin);
            }
        if (hAlign != 255)
            if (QmUE::UFunction* fnHA = QmUE::FindFunctionOnClass(slot->Class, "SetHorizontalAlignment"))
            {
                uint8_t p[8]; memset(p, 0, sizeof(p)); p[0] = hAlign;
                QmUE::CallProcessEvent(slot, fnHA, p);
            }
    }

    // One text row: spawn a TextBlock (Outer = the screen's WidgetTree), set text + optional
    // color + optional cloned game font (Size overridden per row) + optional auto-wrap, append
    // it to the panel. Returns the TextBlock on success.
    QmUE::UObject* AddTextRow(QmUE::UObject* panel, QmUE::UObject* widgetTree,
                              const wchar_t* text, const float* rgba,
                              const uint8_t* font, float fontSize, bool wrap,
                              float gapAbove = 0.0f, uint8_t hAlign = 255, float indent = 0.0f)
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
        if (font)
            if (QmUE::UFunction* fnFont = QmUE::FindFunctionOnClass(txt->Class, "SetFont"))
            {
                uint8_t fi[kFontInfoSize];
                memcpy(fi, font, kFontInfoSize);
                *reinterpret_cast<float*>(fi + kOff_FontInfo_Size) = fontSize;
                QmUE::CallProcessEvent(txt, fnFont, fi);
            }
        if (wrap)
            if (QmUE::UFunction* fnWrap = QmUE::FindFunctionOnClass(txt->Class, "SetAutoWrapText"))
            {
                uint8_t b[8]; memset(b, 0, sizeof(b)); b[0] = 1;
                QmUE::CallProcessEvent(txt, fnWrap, b);
            }
        if (QmUE::UFunction* fnAdd = QmUE::FindFunctionOnClass(panel->Class, "AddChild"))
        {
            P_AddChild ac; ac.Content = txt; ac.ReturnValue = nullptr;
            if (QmUE::CallProcessEvent(panel, fnAdd, &ac) && ac.ReturnValue)
            {
                StyleSlot(reinterpret_cast<QmUE::UObject*>(ac.ReturnValue), gapAbove, hAlign, indent);
                return txt;
            }
        }
        return nullptr;
    }

    // One button row. `donor` is the pre-created themed ArtButton (the font-clone source); the
    // first button row consumes it (un-parented until then), later rows create fresh instances.
    // Falls back to a raw Button+TextBlock: visible, but deliberately unwired - only the themed
    // widget re-dispatches its inner click as a BndEvt__*_OnClick ProcessEvent the click watch
    // can see. A mounted themed button carrying a command is latched into g_buttonActions.
    bool AddButtonRow(QmUE::UObject* panel, QmUE::UObject* widgetTree, QmUE::UObject* screen,
                      QmUE::UObject* owningPlayer, QmUE::UObject* wblCDO, QmUE::UFunction* fnCreate,
                      QmUE::UObject*& donor, const PanelRow& row, bool& wired)
    {
        wired = false;
        if (!panel || !panel->Class) return false;

        QmUE::UObject* btn = nullptr;
        bool themed = false;
        if (donor) { btn = donor; donor = nullptr; themed = true; }
        else if (wblCDO && fnCreate)
        {
            if (QmUE::UClass* artClass = QmUE::FindClassByName(kArtButtonClass))
            {
                P_Create cp; memset(&cp, 0, sizeof(cp));
                cp.WorldContextObject = screen;
                cp.WidgetType         = artClass;
                cp.OwningPlayer       = owningPlayer;
                if (QmUE::CallProcessEvent(wblCDO, fnCreate, &cp))
                    btn = reinterpret_cast<QmUE::UObject*>(cp.ReturnValue);
                themed = (btn && btn->Class);
                if (!themed) btn = nullptr;
            }
        }

        if (themed)
        {
            if (QmUE::UFunction* fnSet = QmUE::FindFunctionOnClass(btn->Class, "SetData"))
            {
                uint8_t ft[16]; memset(ft, 0, sizeof(ft));   // P_SetData = { FText Data; }
                if (QmUE::TextFromString(row.text, ft)) QmUE::CallProcessEvent(btn, fnSet, ft);
            }
        }
        else
        {
            QmUE::UClass*  btnClass = QmUE::FindClassByName("Button");
            QmUE::UClass*  txtClass = QmUE::FindClassByName("TextBlock");
            btn = btnClass ? QmUE::SpawnObjectViaUFunction(btnClass, widgetTree) : nullptr;
            QmUE::UObject* txt = txtClass ? QmUE::SpawnObjectViaUFunction(txtClass, widgetTree) : nullptr;
            if (txt && txt->Class)
                if (QmUE::UFunction* fnSet = QmUE::FindFunctionOnClass(txt->Class, "SetText"))
                {
                    uint8_t ft[16]; memset(ft, 0, sizeof(ft));
                    if (QmUE::TextFromString(row.text, ft)) QmUE::CallProcessEvent(txt, fnSet, ft);
                }
            if (btn && btn->Class && txt)    // UButton is a UContentWidget -> AddChild sets its content
                if (QmUE::UFunction* fnAdd = QmUE::FindFunctionOnClass(btn->Class, "AddChild"))
                {
                    P_AddChild ac; ac.Content = txt; ac.ReturnValue = nullptr;
                    QmUE::CallProcessEvent(btn, fnAdd, &ac);
                }
        }
        if (!btn) return false;

        QmUE::UFunction* fnAdd = QmUE::FindFunctionOnClass(panel->Class, "AddChild");
        if (!fnAdd) return false;
        P_AddChild ac; ac.Content = btn; ac.ReturnValue = nullptr;
        if (!QmUE::CallProcessEvent(panel, fnAdd, &ac)) return false;
        if (ac.ReturnValue)
            StyleSlot(reinterpret_cast<QmUE::UObject*>(ac.ReturnValue), row.gap, row.halign);

        if (themed && row.command && g_buttonActionCount < kMaxButtonActions)
        {
            ButtonAction& a = g_buttonActions[g_buttonActionCount];
            a.widget   = btn;
            a.command  = row.command;
            a.argument = row.argument;
            ++g_buttonActionCount;
            wired = true;
        }
        return true;
    }

    // One native section-header row: CreateWidget the game's own settings header blueprint and
    // label it. The label setter is resolved at runtime (class not in the offline SDK dump);
    // a candidate is only called when its parms are exactly one FText, and the row is only
    // accepted once the label actually dispatched. Any failure returns null and the caller
    // falls back to a font-cloned TextBlock, so the row can never go missing. Un-added
    // instances are left to GC.
    QmUE::UObject* AddHeaderRow(QmUE::UObject* panel, QmUE::UObject* screen,
                                QmUE::UObject* owningPlayer, QmUE::UObject* wblCDO,
                                QmUE::UFunction* fnCreate, const wchar_t* text,
                                float gapAbove = 0.0f)
    {
        if (!panel || !panel->Class || !wblCDO || !fnCreate) return nullptr;
        QmUE::UClass* cls = QmUE::FindClassByName(kEntryHeaderClass);
        if (!cls) return nullptr;

        P_Create cp; memset(&cp, 0, sizeof(cp));
        cp.WorldContextObject = screen;
        cp.WidgetType         = cls;
        cp.OwningPlayer       = owningPlayer;
        QmUE::UObject* hdr = nullptr;
        if (QmUE::CallProcessEvent(wblCDO, fnCreate, &cp))
            hdr = reinterpret_cast<QmUE::UObject*>(cp.ReturnValue);
        if (!hdr || !hdr->Class) return nullptr;

        static const char* const kSetters[] = { "SetData", "SetMainDescription", "SetText" };
        bool labelled = false;
        for (const char* setter : kSetters)
        {
            QmUE::UFunction* fn = QmUE::FindFunctionOnClass(hdr->Class, setter);
            if (!fn || ParmsSize(fn) != 16) continue;
            uint8_t ft[16]; memset(ft, 0, sizeof(ft));
            if (QmUE::TextFromString(text, ft) && QmUE::CallProcessEvent(hdr, fn, ft))
            {
                labelled = true;
                break;
            }
        }
        if (!labelled)
        {
            QM_LOG_WARN("[ModTab]   view: %s resolved but no FText label setter dispatched - "
                        "TextBlock fallback for this row", kEntryHeaderClass);
            return nullptr;
        }
        if (QmUE::UFunction* fnAdd = QmUE::FindFunctionOnClass(panel->Class, "AddChild"))
        {
            P_AddChild ac; ac.Content = hdr; ac.ReturnValue = nullptr;
            if (QmUE::CallProcessEvent(panel, fnAdd, &ac) && ac.ReturnValue)
            {
                StyleSlot(reinterpret_cast<QmUE::UObject*>(ac.ReturnValue), gapAbove);
                return hdr;
            }
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
        QM_LOG_DEBUG("[ModTab] *** VIEW-PATH *** map screen tree + build our own ScrollBox as a sibling of "
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
        QM_LOG_DEBUG("[ModTab]   view: screen->WidgetTree@0x2D8 = 0x%p %s", (void*)widgetTree, wtid);

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
            QM_LOG_DEBUG("[ModTab]   view: content VerticalBox not resolved yet (tree mid-rebuild) - deferring (re)build to next poll");
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
            QM_LOG_DEBUG("[ModTab] *** FRESH REBUILD *** discarded stale panel=0x%p (dead Slate on "
                         "reopen) - building a brand-new ScrollBox into content VerticalBox 0x%p",
                         (void*)g_ourPanel, (void*)mountTarget);
            g_ourPanel          = nullptr;
            g_buttonActionCount = 0;   // actions die with their panel; re-latched by the build
        }

        // Owning player (Create's 3rd arg).
        QmUE::UObject* owningPlayer = nullptr;
        if (QmUE::UFunction* fnOP = QmUE::FindFunctionOnClass(screen->Class, "GetOwningPlayer"))
        {
            P_GetOwningPlayer op; op.ReturnValue = nullptr;
            if (QmUE::CallProcessEvent(screen, fnOP, &op)) owningPlayer = reinterpret_cast<QmUE::UObject*>(op.ReturnValue);
        }
        char opid[352]; DescribeObject(owningPlayer, opid, sizeof(opid));
        QM_LOG_DEBUG("[ModTab]   view: GetOwningPlayer -> 0x%p %s", (void*)owningPlayer, opid);

        QmUE::UClass*    wblClass  = QmUE::FindClassByName("WidgetBlueprintLibrary");
        QmUE::UObject*   wblCDO    = wblClass ? QmUE::GetClassDefaultObject(wblClass) : nullptr;
        QmUE::UFunction* fnCreate  = wblClass ? QmUE::FindFunctionOnClass(wblClass, "Create") : nullptr;

        // Our own ScrollBox panel (Outer = the screen's WidgetTree).
        QmUE::UClass*  scrollClass = QmUE::FindClassByName("ScrollBox");
        QmUE::UObject* ourPanel    = scrollClass ? QmUE::SpawnObjectViaUFunction(scrollClass, widgetTree) : nullptr;
        char opnl[352]; DescribeObject(ourPanel, opnl, sizeof(opnl));
        if (ourPanel)
            QM_LOG_DEBUG("[ModTab]   view: own ScrollBox class=0x%p -> panel=0x%p %s (CONSTRUCTED)",
                         (void*)scrollClass, (void*)ourPanel, opnl);
        else
            QM_LOG_WARN("[ModTab]   view: own ScrollBox construct FAILED (class=0x%p)", (void*)scrollClass);

        // Game-themed scrollbar: clone the native list's FScrollBarStyle onto our panel before
        // the mount - Slate reads WidgetBarStyle once, when the SScrollBox is built.
        if (ourPanel)
        {
            QmUE::UObject* barDonor = ResolveNativeSettingsList(settingsPanel);
            bool themedBar = barDonor ? CloneScrollBarStyle(ourPanel, barDonor) : false;
            char bdid[352]; DescribeObject(barDonor, bdid, sizeof(bdid));
            QM_LOG_DEBUG("[ModTab]   view: scrollbar style clone ok=%d donor=0x%p %s",
                         themedBar, (void*)barDonor, bdid);
        }

        // Panel content from the layout rows (qm_modtab_layout.json or its compiled-in default).
        // Strictly additive + SEH-isolated: any failure is swallowed and never aborts the
        // panel mount.
        if (ourPanel)
        {
            __try
            {
                // A themed ArtButton is created up front as the font-clone donor: its label
                // TextBlock (txt_Name) carries the game font applied to the plain text rows.
                // The first button row consumes the instance (un-parented until then); with no
                // button rows it is left to GC.
                uint8_t gameFont[kFontInfoSize];
                const uint8_t* font = nullptr;
                QmUE::UObject* donor = nullptr;
                QmUE::UClass* artClass = QmUE::FindClassByName(kArtButtonClass);
                if (artClass && wblCDO && fnCreate)
                {
                    P_Create cp; memset(&cp, 0, sizeof(cp));
                    cp.WorldContextObject = screen;
                    cp.WidgetType         = artClass;
                    cp.OwningPlayer       = owningPlayer;
                    if (QmUE::CallProcessEvent(wblCDO, fnCreate, &cp))
                        donor = reinterpret_cast<QmUE::UObject*>(cp.ReturnValue);
                    if (donor && donor->Class)
                    {
                        QmUE::UObject* lbl = reinterpret_cast<QmUE::UObject*>(
                            ReadPtr(reinterpret_cast<const uint8_t*>(donor) + kOff_ArtButton_TxtName));
                        if (CloneFontFromTextBlock(lbl, gameFont)) font = gameFont;
                    }
                    else donor = nullptr;
                }

                int rowCount = 0;
                const PanelRow* layout = GetPanelLayout(&rowCount);
                int rows = 0, headers = 0, buttons = 0, wiredCount = 0;
                g_buttonActionCount = 0;
                for (int i = 0; layout && i < rowCount; ++i)
                {
                    const PanelRow& r = layout[i];
                    if (r.type == kRowHeader)
                    {
                        if (AddHeaderRow(ourPanel, screen, owningPlayer, wblCDO, fnCreate, r.text, r.gap))
                            { ++rows; ++headers; }
                        else if (AddTextRow(ourPanel, widgetTree, r.text, r.color, font,
                                            r.size > 0.0f ? r.size : kDefFontSizeHeader,
                                            r.wrap, r.gap, r.halign))
                            ++rows;
                    }
                    else if (r.type == kRowButton)
                    {
                        bool wired = false;
                        if (AddButtonRow(ourPanel, widgetTree, screen, owningPlayer, wblCDO,
                                         fnCreate, donor, r, wired))
                            { ++rows; ++buttons; if (wired) ++wiredCount; }
                    }
                    else
                    {
                        if (AddTextRow(ourPanel, widgetTree, r.text, r.color, font,
                                       r.size > 0.0f ? r.size : kDefFontSizeBody,
                                       r.wrap, r.gap, r.halign, r.indent))
                            ++rows;
                    }
                }
                QM_LOG_DEBUG("[ModTab]   view: CONTENT build rows=%d/%d headers=%d buttons=%d wired=%d "
                             "gameFont=%d (wired clicks via the PE BndEvt watch)",
                             rows, rowCount, headers, buttons, wiredCount, font ? 1 : 0);
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
                if (ok)
                    QM_LOG_DEBUG("[ModTab]   view: MOUNT ScrollBox -> content VerticalBox ok=%d slot=0x%p %s",
                                 ok, ac.ReturnValue, sl);
                else
                    QM_LOG_WARN("[ModTab]   view: MOUNT ScrollBox -> content VerticalBox FAILED (slot=0x%p)",
                                ac.ReturnValue);
                if (ok)
                {
                    // The VerticalBoxSlot defaults to Automatic sizing: the ScrollBox is granted
                    // its full desired height, the screen clips it, and with no internal overflow
                    // it never scrolls. Fill (like the native Settings_Panel sibling) constrains
                    // the panel to the visible area - that constraint is what enables scrolling.
                    QmUE::UObject* mountSlot = reinterpret_cast<QmUE::UObject*>(ac.ReturnValue);
                    if (mountSlot && mountSlot->Class)
                        if (QmUE::UFunction* fnSize = QmUE::FindFunctionOnClass(mountSlot->Class, "SetSize"))
                        {
                            struct { float Value; uint8_t SizeRule; uint8_t pad[3]; } sz = { 1.0f, 1, {} };   // FSlateChildSize, Fill
                            bool sok = QmUE::CallProcessEvent(mountSlot, fnSize, &sz);
                            QM_LOG_DEBUG("[ModTab]   view: mount slot SetSize(Fill 1.0) ok=%d (enables vertical scrolling)", sok);
                        }
                    g_ourPanel    = ourPanel;
                    g_mountTarget = mountTarget;
                    g_nativePanel = settingsPanel;
                    bool vok = SetWidgetVisibility(ourPanel, ESV_Collapsed);
                    bool nok = settingsPanel ? SetWidgetVisibility(settingsPanel, ESV_Visible) : false;
                    QM_LOG_DEBUG("[ModTab]   view: gate INIT -> our panel Collapsed ok=%d, native Settings_Panel(0x%p) Visible ok=%d",
                                 vok, (void*)settingsPanel, nok);
                }
            }
        }
        QM_LOG_DEBUG("[ModTab] *** VIEW-PATH DONE *** panel=0x%p mountTarget=0x%p - starts hidden, the tab-state "
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
