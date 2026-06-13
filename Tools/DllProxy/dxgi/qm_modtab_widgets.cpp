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

    // Item-spawner dropdown (UComboBoxString). No SetFont UFunction exists on the class, so
    // the game font is raw-written into the Font property BEFORE the mount - Slate reads it
    // once, when the SComboBox is built (same pre-construct rule as the scrollbar style).
    constexpr uintptr_t kOff_Combo_MaxListHeight = 0x1900;  // UComboBoxString::MaxListHeight (SDK)
    constexpr uintptr_t kOff_Combo_Font          = 0x1908;  // UComboBoxString::Font          (SDK)
    constexpr float     kComboMaxListHeight      = 420.0f;

    // Dropdown game-look. The engine default is white list / blue selection; no game widget BP
    // uses ComboBoxString, so there is no native donor to clone the combo styles from. Instead
    // the button/list/selection styles are built from flat palette solids (a brush with no
    // resource and DrawAs Image renders Slate's default white texture tinted - the
    // FSlateColorBrush pattern), while the open-list scrollbar reuses the proven native-list
    // clone. All styles are written pre-mount: Slate reads them once, at SComboBox build.
    constexpr uintptr_t kOff_Combo_WidgetStyle     = 0x190;   // UComboBoxString::WidgetStyle     (SDK)
    constexpr uintptr_t kOff_Combo_ItemStyle       = 0x750;   // UComboBoxString::ItemStyle       (SDK)
    constexpr uintptr_t kOff_Combo_ScrollBarStyle  = 0x12A0;  // UComboBoxString::ScrollBarStyle  (SDK)
    constexpr uintptr_t kOff_Combo_ContentPadding  = 0x18F0;  // UComboBoxString::ContentPadding  (SDK)
    constexpr uintptr_t kOff_Combo_ForegroundColor = 0x1968;  // UComboBoxString::ForegroundColor (SDK)
    constexpr uintptr_t kOff_CBS_ButtonStyle    = 0x10 + 0x10;   // FComboBoxStyle::ComboButtonStyle.ButtonStyle
    constexpr uintptr_t kOff_CBS_DownArrow      = 0x10 + 0x3A0;  // ..ComboButtonStyle.DownArrowImage
    constexpr uintptr_t kOff_CBS_MenuBorder     = 0x10 + 0x470;  // ..ComboButtonStyle.MenuBorderBrush
    constexpr uintptr_t kOff_CBS_MenuRowPadding = 0x5B0;         // FComboBoxStyle::MenuRowPadding
    constexpr uintptr_t kOff_BtnStyle_Normal    = 0x10;    // FButtonStyle: Normal/Hovered/Pressed/
    constexpr uintptr_t kOff_BtnStyle_Hovered   = 0xC0;    // Disabled brushes, then 4 consecutive
    constexpr uintptr_t kOff_BtnStyle_Pressed   = 0x170;   // foreground FSlateColors (0x14 apart)
    constexpr uintptr_t kOff_BtnStyle_Disabled  = 0x220;
    constexpr uintptr_t kOff_BtnStyle_NormalFg  = 0x2D0;
    constexpr uintptr_t kOff_Row_SelectorFocused     = 0x10;   // FTableRowStyle brushes/colors (SDK)
    constexpr uintptr_t kOff_Row_ActiveHovered       = 0xC0;
    constexpr uintptr_t kOff_Row_Active              = 0x170;
    constexpr uintptr_t kOff_Row_InactiveHovered     = 0x220;
    constexpr uintptr_t kOff_Row_Inactive            = 0x2D0;
    constexpr uintptr_t kOff_Row_EvenHovered         = 0x4F0;
    constexpr uintptr_t kOff_Row_Even                = 0x5A0;
    constexpr uintptr_t kOff_Row_OddHovered          = 0x650;
    constexpr uintptr_t kOff_Row_Odd                 = 0x700;
    constexpr uintptr_t kOff_Row_TextColor           = 0x7B0;
    constexpr uintptr_t kOff_Row_SelectedText        = 0x7C4;
    constexpr uintptr_t kOff_Row_ActiveHighlighted   = 0x9F0;
    constexpr uintptr_t kOff_Row_InactiveHighlighted = 0xAA0;
    // Item-spawner search box (UEditableTextBox). Same pre-construct rule as the combo:
    // WidgetStyle is raw-written before the mount, Slate reads it once at SEditableTextBox
    // build. No SetFont UFunction exists either - the game font goes into TextStyle.Font.
    constexpr uintptr_t kOff_Edit_WidgetStyle  = 0x190;   // UEditableTextBox::WidgetStyle (SDK)
    constexpr uintptr_t kOff_ETB_BackNormal    = 0x010;   // FEditableTextBoxStyle brushes  (SDK)
    constexpr uintptr_t kOff_ETB_BackHovered   = 0x0C0;
    constexpr uintptr_t kOff_ETB_BackFocused   = 0x170;
    constexpr uintptr_t kOff_ETB_BackReadOnly  = 0x220;
    constexpr uintptr_t kOff_ETB_Padding       = 0x2D0;   // FEditableTextBoxStyle::Padding
    constexpr uintptr_t kOff_ETB_TextStyle     = 0x2E0;   // ..::TextStyle (FTextBlockStyle)
    constexpr uintptr_t kOff_ETB_Foreground    = 0x5C0;   // ..::ForegroundColor
    constexpr uintptr_t kOff_ETB_FocusedFg     = 0x5FC;   // ..::FocusedForegroundColor
    constexpr uintptr_t kOff_TBS_Font          = 0x008;   // FTextBlockStyle::Font
    constexpr uintptr_t kOff_TBS_Color         = 0x068;   // FTextBlockStyle::ColorAndOpacity

    constexpr uintptr_t kOff_Brush_Tint         = 0x08;    // FSlateBrush internals (SDK)
    constexpr uintptr_t kOff_Brush_DrawAs       = 0x1C;    // 0=NoDraw 3=Image 4=RoundedBox
    constexpr uintptr_t kOff_Brush_Tiling       = 0x1D;
    constexpr uintptr_t kOff_Brush_Margin       = 0x28;
    constexpr uintptr_t kOff_Brush_ResourceObj  = 0x38;
    constexpr uintptr_t kOff_Brush_Outline      = 0x40;    // FSlateBrushOutlineSettings: CornerRadii
    constexpr uintptr_t kOff_Brush_ResourceName = 0xA8;    // (FVector4=4 doubles) +0x20 Color +0x34 Width

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

    // The raw FScrollBarStyle copy shared by the panel ScrollBox and the dropdown's open list
    // (see the constants above for the ResourceHandle footgun). Caller provides SEH.
    void CopyScrollBarStyleRaw(uint8_t* dst, const uint8_t* src)
    {
        memcpy(dst + kBarStyleFirstBrush, src + kBarStyleFirstBrush, kScrollBarStyleSize - kBarStyleFirstBrush);
        for (int i = 0; i < kBarStyleBrushCount; ++i)
            memset(dst + kBarStyleFirstBrush + i * kBrushSize + kOff_Brush_ResourceHandle, 0, kResourceHandleSize);
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
            CopyScrollBarStyleRaw(dst, src);

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
                              float gapAbove = 0.0f, uint8_t hAlign = 255, float indent = 0.0f,
                              QmUE::UObject** outSlot = nullptr)
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
                if (outSlot) *outSlot = reinterpret_cast<QmUE::UObject*>(ac.ReturnValue);
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
                      QmUE::UObject*& donor, const PanelRow& row, bool& wired,
                      QmUE::UObject** outSlot = nullptr)
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
        if (outSlot) *outSlot = reinterpret_cast<QmUE::UObject*>(ac.ReturnValue);

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

    // Read a numeric box's text back into its persistence setter (no-op when the latch is
    // null; SEH turns a dead widget into "keep the previous value").
    void SnapshotBoxText(void* boxPtr, void (*setter)(const wchar_t*))
    {
        QmUE::UObject* box = reinterpret_cast<QmUE::UObject*>(boxPtr);
        if (!box) return;
        __try
        {
            if (box->Class)
                if (QmUE::UFunction* fn = QmUE::FindFunctionOnClass(box->Class, "GetText"))
                {
                    uint8_t ft[16]; memset(ft, 0, sizeof(ft));
                    wchar_t buf[64];
                    if (QmUE::CallProcessEvent(box, fn, ft) &&
                        QmUE::StringFromText(ft, buf, 64))
                        setter(buf);
                }
        }
        __except (EXCEPTION_EXECUTE_HANDLER) {}
    }

    // Persist the dropdown selection AND the search text across panel rebuilds: the widgets
    // die with their panel, so the last live state is read back right before the discard
    // (the search text additionally covers type-then-close faster than the 250ms poll).
    // Across a reopen the instances may already be GC'd - the SEH guard turns that into
    // "keep the previous value".
    void SnapshotItemSelection()
    {
        QmUE::UObject* combo = reinterpret_cast<QmUE::UObject*>(g_itemCombo);
        if (combo)
        {
            __try
            {
                if (combo->Class)
                    if (QmUE::UFunction* fn = QmUE::FindFunctionOnClass(combo->Class, "GetSelectedIndex"))
                    {
                        int32_t p[2] = { -1, 0 };
                        if (QmUE::CallProcessEvent(combo, fn, p) && p[0] >= 0) g_lastItemSel = p[0];
                    }
            }
            __except (EXCEPTION_EXECUTE_HANDLER) {}
        }

        QmUE::UObject* box = reinterpret_cast<QmUE::UObject*>(g_searchBox);
        if (box)
        {
            __try
            {
                if (box->Class)
                    if (QmUE::UFunction* fn = QmUE::FindFunctionOnClass(box->Class, "GetText"))
                    {
                        uint8_t ft[16]; memset(ft, 0, sizeof(ft));
                        wchar_t buf[256];
                        if (QmUE::CallProcessEvent(box, fn, ft) &&
                            QmUE::StringFromText(ft, buf, 256))
                            SetItemSearchText(buf);   // filter view rebuilds; panel is dying anyway
                    }
            }
            __except (EXCEPTION_EXECUTE_HANDLER) {}
        }

        SnapshotBoxText(g_countBox,  SetItemCountText);
        SnapshotBoxText(g_xpBox,     SetXpCountText);
    }

    struct QmColor { float r, g, b, a; };

    void WriteSlateColor(uint8_t* dst, const QmColor& c)
    {
        memcpy(dst, &c, 0x10);
        dst[0x10] = 0;   // ESlateColorStylingMode::UseColor_Specified
    }

    // Turn a CDO-initialized FSlateBrush into a flat palette solid; optional thin outline via
    // RoundedBox. Fields are written individually - FSlateBrush is polymorphic, offset 0 holds
    // a vtable that a blanket memset would destroy (the brush is destructed on panel discard).
    void MakeSolidBrush(uint8_t* b, const QmColor& tint, float corner = 0.0f,
                        const QmColor& outline = { 0, 0, 0, 0 }, float outlineW = 0.0f)
    {
        WriteSlateColor(b + kOff_Brush_Tint, tint);
        b[kOff_Brush_DrawAs] = (tint.a <= 0.0f && outlineW <= 0.0f) ? 0       // NoDrawType
                             : (corner > 0.0f)                      ? 4       // RoundedBox
                                                                    : 3;      // Image (solid)
        b[kOff_Brush_Tiling] = 0;                                             // NoTile
        memset(b + kOff_Brush_Margin, 0, 0x10);
        *reinterpret_cast<void**>(b + kOff_Brush_ResourceObj) = nullptr;
        double* radii = reinterpret_cast<double*>(b + kOff_Brush_Outline);    // FVector4 CornerRadii
        radii[0] = radii[1] = radii[2] = radii[3] = corner;
        WriteSlateColor(b + kOff_Brush_Outline + 0x20, outline);
        *reinterpret_cast<float*>(b + kOff_Brush_Outline + 0x34) = outlineW;
        b[kOff_Brush_Outline + 0x38] = 0;                                     // FixedRadius
        memset(b + kOff_Brush_ResourceHandle, 0, kResourceHandleSize);
        *reinterpret_cast<uint64_t*>(b + kOff_Brush_ResourceName) = 0;        // NAME_None
    }

    // Game-look the spawned combo before its Slate widget exists. Dark solids with the panel's
    // gold accent for button/menu/selection; row backgrounds stay transparent so the menu
    // border brush provides one seamless backdrop; the open-list scrollbar is cloned from the
    // same native settings list that themes the panel bar.
    void StyleItemCombo(QmUE::UObject* combo, QmUE::UObject* barDonor)
    {
        const QmColor btnNormal  = { 0.035f, 0.030f, 0.020f, 0.94f };
        const QmColor btnHover   = { 0.100f, 0.082f, 0.045f, 1.00f };
        const QmColor btnPressed = { 0.190f, 0.150f, 0.065f, 1.00f };
        const QmColor goldLine   = { 0.830f, 0.740f, 0.340f, 0.45f };
        const QmColor goldArrow  = { 0.830f, 0.740f, 0.340f, 1.00f };
        const QmColor menuBack   = { 0.016f, 0.014f, 0.010f, 0.98f };
        const QmColor rowClear   = { 0.000f, 0.000f, 0.000f, 0.00f };
        const QmColor rowHover   = { 0.110f, 0.092f, 0.050f, 0.90f };
        const QmColor rowSel     = { 0.330f, 0.250f, 0.085f, 1.00f };
        const QmColor rowSelHov  = { 0.420f, 0.320f, 0.115f, 1.00f };
        const QmColor textNorm   = { 0.760f, 0.745f, 0.700f, 1.00f };
        const QmColor textSel    = { 1.000f, 0.920f, 0.560f, 1.00f };
        const QmColor foreground = { 0.920f, 0.900f, 0.820f, 1.00f };

        __try
        {
            uint8_t* base = reinterpret_cast<uint8_t*>(combo);

            // Combo button: dark rounded solids with a thin gold outline + gold arrow.
            uint8_t* bs = base + kOff_Combo_WidgetStyle + kOff_CBS_ButtonStyle;
            MakeSolidBrush(bs + kOff_BtnStyle_Normal,   btnNormal,  3.0f, goldLine, 1.0f);
            MakeSolidBrush(bs + kOff_BtnStyle_Hovered,  btnHover,   3.0f, goldLine, 1.0f);
            MakeSolidBrush(bs + kOff_BtnStyle_Pressed,  btnPressed, 3.0f, goldLine, 1.0f);
            MakeSolidBrush(bs + kOff_BtnStyle_Disabled, btnNormal,  3.0f, goldLine, 1.0f);
            for (int i = 0; i < 4; ++i)
                WriteSlateColor(bs + kOff_BtnStyle_NormalFg + i * 0x14, foreground);
            WriteSlateColor(base + kOff_Combo_WidgetStyle + kOff_CBS_DownArrow + kOff_Brush_Tint, goldArrow);
            MakeSolidBrush(base + kOff_Combo_WidgetStyle + kOff_CBS_MenuBorder, menuBack, 3.0f, goldLine, 1.0f);
            float rowPad[4] = { 10.0f, 4.0f, 10.0f, 4.0f };   // FMargin {L,T,R,B}
            memcpy(base + kOff_Combo_WidgetStyle + kOff_CBS_MenuRowPadding, rowPad, 0x10);

            // List rows: transparent at rest (menu border shows through), warm hover, gold
            // selection. Active/Inactive = selected row focused/unfocused - styled alike.
            uint8_t* is = base + kOff_Combo_ItemStyle;
            MakeSolidBrush(is + kOff_Row_SelectorFocused,     rowHover);
            MakeSolidBrush(is + kOff_Row_ActiveHovered,       rowSelHov);
            MakeSolidBrush(is + kOff_Row_Active,              rowSel);
            MakeSolidBrush(is + kOff_Row_InactiveHovered,     rowSelHov);
            MakeSolidBrush(is + kOff_Row_Inactive,            rowSel);
            MakeSolidBrush(is + kOff_Row_EvenHovered,         rowHover);
            MakeSolidBrush(is + kOff_Row_Even,                rowClear);
            MakeSolidBrush(is + kOff_Row_OddHovered,          rowHover);
            MakeSolidBrush(is + kOff_Row_Odd,                 rowClear);
            MakeSolidBrush(is + kOff_Row_ActiveHighlighted,   rowSel);
            MakeSolidBrush(is + kOff_Row_InactiveHighlighted, rowSel);
            WriteSlateColor(is + kOff_Row_TextColor,    textNorm);
            WriteSlateColor(is + kOff_Row_SelectedText, textSel);

            WriteSlateColor(base + kOff_Combo_ForegroundColor, foreground);
            float contentPad[4] = { 10.0f, 4.0f, 10.0f, 4.0f };
            memcpy(base + kOff_Combo_ContentPadding, contentPad, 0x10);

            if (barDonor)
                CopyScrollBarStyleRaw(base + kOff_Combo_ScrollBarStyle,
                                      reinterpret_cast<const uint8_t*>(barDonor) + kOff_ListView_ScrollBarStyle);
            QM_LOG_DEBUG("[ModTab]   view: combo game-look applied (scrollbar clone=%d)",
                         barDonor ? 1 : 0);
        }
        __except (EXCEPTION_EXECUTE_HANDLER)
        {
            QM_LOG_WARN("[ModTab]   view: combo style FAULTED (default look stays)");
        }
    }

    // Spawn one game-styled ComboBoxString (font + bounded open-list height + game-look),
    // not yet filled or mounted. Shared by the item and the category dropdown rows.
    QmUE::UObject* SpawnStyledCombo(QmUE::UObject* widgetTree, const uint8_t* font,
                                    const PanelRow& row, QmUE::UObject* barDonor)
    {
        QmUE::UClass* comboClass = QmUE::FindClassByName("ComboBoxString");
        if (!comboClass) return nullptr;
        QmUE::UObject* combo = QmUE::SpawnObjectViaUFunction(comboClass, widgetTree);
        if (!combo || !combo->Class) return nullptr;

        // Pre-construct property writes (game font + a bounded open-list height for the
        // ~1000-entry catalog; the inner list view virtualizes its rows).
        __try
        {
            uint8_t* base = reinterpret_cast<uint8_t*>(combo);
            *reinterpret_cast<float*>(base + kOff_Combo_MaxListHeight) = kComboMaxListHeight;
            if (font)
            {
                memcpy(base + kOff_Combo_Font, font, kFontInfoSize);
                *reinterpret_cast<float*>(base + kOff_Combo_Font + kOff_FontInfo_Size) =
                    row.size > 0.0f ? row.size : kDefFontSizeBody;
            }
        }
        __except (EXCEPTION_EXECUTE_HANDLER) {}

        StyleItemCombo(combo, barDonor);
        return combo;
    }

    struct FStringParm { const wchar_t* data; int32_t num; int32_t max; };

    // Fill (or REFILL) the item combo from the filtered catalog view and re-apply the last
    // known selection. Runs pre-mount at panel build (no Slate widget yet = cheap) and on a
    // LIVE combo when the category poll switches the filter (the closed SComboBox only marks
    // its virtualized list dirty, so a refill is bounded by the AddOption dispatch cost).
    int FillItemCombo(QmUE::UObject* combo)
    {
        if (!combo || !combo->Class) return 0;
        if (QmUE::UFunction* fnClr = QmUE::FindFunctionOnClass(combo->Class, "ClearOptions"))
        {
            uint8_t none[8] = { 0 };
            QmUE::CallProcessEvent(combo, fnClr, none);
        }
        int added = 0;
        if (QmUE::UFunction* fnOpt = QmUE::FindFunctionOnClass(combo->Class, "AddOption"))
        {
            const int n = GetItemOptionCount();
            for (int i = 0; i < n; ++i)
            {
                const wchar_t* name = GetItemOptionName(i);
                if (!name) continue;
                int32_t len = (int32_t)wcslen(name) + 1;
                FStringParm p = { name, len, len };
                if (QmUE::CallProcessEvent(combo, fnOpt, &p)) ++added;
            }
        }
        if (QmUE::UFunction* fnSel = QmUE::FindFunctionOnClass(combo->Class, "SetSelectedIndex"))
        {
            int32_t idx[2] = { g_lastItemSel, 0 };
            if (idx[0] < 0 || idx[0] >= added) idx[0] = 0;
            QmUE::CallProcessEvent(combo, fnSel, idx);
        }
        return added;
    }

    // The item-spawner dropdown: spawn a ComboBoxString, fill it with the ACTIVE category's
    // slice of the item catalog, append it to the panel and latch it into g_itemCombo for
    // the click-time "add_selected_item" dispatch (qm_modtab.cpp).
    bool AddItemDropdownRow(QmUE::UObject* panel, QmUE::UObject* widgetTree,
                            const uint8_t* font, const PanelRow& row, QmUE::UObject* barDonor,
                            QmUE::UObject** outSlot = nullptr)
    {
        if (!panel || !panel->Class) return false;
        QmUE::UObject* combo = SpawnStyledCombo(widgetTree, font, row, barDonor);
        if (!combo) return false;

        int added = FillItemCombo(combo);

        QmUE::UFunction* fnAdd = QmUE::FindFunctionOnClass(panel->Class, "AddChild");
        if (!fnAdd) return false;
        P_AddChild ac; ac.Content = combo; ac.ReturnValue = nullptr;
        if (!QmUE::CallProcessEvent(panel, fnAdd, &ac)) return false;
        if (ac.ReturnValue)
            StyleSlot(reinterpret_cast<QmUE::UObject*>(ac.ReturnValue), row.gap, row.halign, row.indent);
        if (outSlot) *outSlot = reinterpret_cast<QmUE::UObject*>(ac.ReturnValue);

        g_itemCombo = combo;
        QM_LOG_DEBUG("[ModTab]   view: item dropdown combo=0x%p options=%d sel=%d cat=%d",
                     (void*)combo, added, g_lastItemSel, GetActiveItemCategory());
        return true;
    }

    // The cascading category filter: a ComboBoxString over the catalog's category list
    // ("All Items" + the groups the catalog carries). The selection is re-applied from the
    // persistent active category and latched into g_catCombo for the PE-hook poll - a pick
    // there refills the item combo with the new slice (PollCategoryDropdown below).
    bool AddCategoryDropdownRow(QmUE::UObject* panel, QmUE::UObject* widgetTree,
                                const uint8_t* font, const PanelRow& row, QmUE::UObject* barDonor,
                                QmUE::UObject** outSlot = nullptr)
    {
        if (!panel || !panel->Class) return false;
        QmUE::UObject* combo = SpawnStyledCombo(widgetTree, font, row, barDonor);
        if (!combo) return false;

        int added = 0;
        if (QmUE::UFunction* fnOpt = QmUE::FindFunctionOnClass(combo->Class, "AddOption"))
        {
            const int n = GetItemCategoryCount();
            for (int i = 0; i < n; ++i)
            {
                const wchar_t* name = GetItemCategoryName(i);
                if (!name) continue;
                int32_t len = (int32_t)wcslen(name) + 1;
                FStringParm p = { name, len, len };
                if (QmUE::CallProcessEvent(combo, fnOpt, &p)) ++added;
            }
        }
        if (QmUE::UFunction* fnSel = QmUE::FindFunctionOnClass(combo->Class, "SetSelectedIndex"))
        {
            int32_t idx[2] = { GetActiveItemCategory(), 0 };
            if (idx[0] < 0 || idx[0] >= added) idx[0] = 0;
            QmUE::CallProcessEvent(combo, fnSel, idx);
        }

        QmUE::UFunction* fnAdd = QmUE::FindFunctionOnClass(panel->Class, "AddChild");
        if (!fnAdd) return false;
        P_AddChild ac; ac.Content = combo; ac.ReturnValue = nullptr;
        if (!QmUE::CallProcessEvent(panel, fnAdd, &ac)) return false;
        if (ac.ReturnValue)
            StyleSlot(reinterpret_cast<QmUE::UObject*>(ac.ReturnValue), row.gap, row.halign, row.indent);
        if (outSlot) *outSlot = reinterpret_cast<QmUE::UObject*>(ac.ReturnValue);

        g_catCombo = combo;
        QM_LOG_DEBUG("[ModTab]   view: category dropdown combo=0x%p options=%d active=%d",
                     (void*)combo, added, GetActiveItemCategory());
        return true;
    }

    // Game-look the spawned search box pre-mount (Slate reads WidgetStyle once, at
    // SEditableTextBox build): dark rounded solids matching the combo button, gold outline
    // that brightens on focus, game font + parchment text. SEH-framed like the combo style -
    // a fault leaves the engine default look, the filter still works.
    void StyleSearchBox(QmUE::UObject* box, const uint8_t* font, float fontSize)
    {
        const QmColor back     = { 0.035f, 0.030f, 0.020f, 0.94f };
        const QmColor backHi   = { 0.055f, 0.047f, 0.030f, 0.97f };
        const QmColor goldLine = { 0.830f, 0.740f, 0.340f, 0.45f };
        const QmColor goldHot  = { 0.830f, 0.740f, 0.340f, 0.85f };
        const QmColor textCol  = { 0.920f, 0.900f, 0.820f, 1.00f };

        __try
        {
            uint8_t* st = reinterpret_cast<uint8_t*>(box) + kOff_Edit_WidgetStyle;
            MakeSolidBrush(st + kOff_ETB_BackNormal,   back,   3.0f, goldLine, 1.0f);
            MakeSolidBrush(st + kOff_ETB_BackHovered,  backHi, 3.0f, goldLine, 1.0f);
            MakeSolidBrush(st + kOff_ETB_BackFocused,  backHi, 3.0f, goldHot,  1.0f);
            MakeSolidBrush(st + kOff_ETB_BackReadOnly, back,   3.0f, goldLine, 1.0f);
            float pad[4] = { 10.0f, 6.0f, 10.0f, 6.0f };   // FMargin {L,T,R,B}
            memcpy(st + kOff_ETB_Padding, pad, 0x10);
            if (font)
            {
                memcpy(st + kOff_ETB_TextStyle + kOff_TBS_Font, font, kFontInfoSize);
                *reinterpret_cast<float*>(st + kOff_ETB_TextStyle + kOff_TBS_Font + kOff_FontInfo_Size) = fontSize;
            }
            WriteSlateColor(st + kOff_ETB_TextStyle + kOff_TBS_Color, textCol);
            WriteSlateColor(st + kOff_ETB_Foreground, textCol);
            WriteSlateColor(st + kOff_ETB_FocusedFg,  textCol);
            QM_LOG_DEBUG("[ModTab]   view: search box game-look applied");
        }
        __except (EXCEPTION_EXECUTE_HANDLER)
        {
            QM_LOG_WARN("[ModTab]   view: search box style FAULTED (default look stays)");
        }
    }

    // The item-spawner search box: spawn an EditableTextBox, hint it, re-seed the persistent
    // search text onto the fresh instance and latch it into g_searchBox for the PE-hook poll
    // (PollSpawnerControls) - its text is a live substring filter over the item combo.
    bool AddItemSearchRow(QmUE::UObject* panel, QmUE::UObject* widgetTree,
                          const uint8_t* font, const PanelRow& row,
                          QmUE::UObject** outSlot = nullptr)
    {
        if (!panel || !panel->Class) return false;
        QmUE::UClass* boxClass = QmUE::FindClassByName("EditableTextBox");
        if (!boxClass) return false;
        QmUE::UObject* box = QmUE::SpawnObjectViaUFunction(boxClass, widgetTree);
        if (!box || !box->Class) return false;

        StyleSearchBox(box, font, row.size > 0.0f ? row.size : kDefFontSizeBody);

        if (QmUE::UFunction* fnHint = QmUE::FindFunctionOnClass(box->Class, "SetHintText"))
        {
            uint8_t ft[16]; memset(ft, 0, sizeof(ft));
            const wchar_t* hint = (row.text && row.text[0]) ? row.text : L"Search...";
            if (QmUE::TextFromString(hint, ft)) QmUE::CallProcessEvent(box, fnHint, ft);
        }
        const wchar_t* prev = GetItemSearchText();
        if (prev && prev[0])
            if (QmUE::UFunction* fnSet = QmUE::FindFunctionOnClass(box->Class, "SetText"))
            {
                uint8_t ft[16]; memset(ft, 0, sizeof(ft));
                if (QmUE::TextFromString(prev, ft)) QmUE::CallProcessEvent(box, fnSet, ft);
            }

        QmUE::UFunction* fnAdd = QmUE::FindFunctionOnClass(panel->Class, "AddChild");
        if (!fnAdd) return false;
        P_AddChild ac; ac.Content = box; ac.ReturnValue = nullptr;
        if (!QmUE::CallProcessEvent(panel, fnAdd, &ac)) return false;
        if (ac.ReturnValue)
            StyleSlot(reinterpret_cast<QmUE::UObject*>(ac.ReturnValue), row.gap, row.halign, row.indent);
        if (outSlot) *outSlot = reinterpret_cast<QmUE::UObject*>(ac.ReturnValue);

        g_searchBox = box;
        QM_LOG_DEBUG("[ModTab]   view: item search box=0x%p seeded-len=%d",
                     (void*)box, prev ? (int)wcslen(prev) : 0);
        return true;
    }

    // A numeric EditableTextBox row (kRowItemCount / kRowXpCount): seeded with the persisted
    // value (the row's "text" as the first-run default, defSeed when both are unset) and
    // latched into `latch`. Never polled - the dispatch reads it at click time
    // (qm_modtab.cpp), like the item combo's selection.
    bool AddCountBoxRow(QmUE::UObject* panel, QmUE::UObject* widgetTree,
                        const uint8_t* font, const PanelRow& row,
                        void** latch, const wchar_t* prev, const wchar_t* defSeed,
                        const char* logTag, QmUE::UObject** outSlot = nullptr)
    {
        if (!panel || !panel->Class || !latch) return false;
        QmUE::UClass* boxClass = QmUE::FindClassByName("EditableTextBox");
        if (!boxClass) return false;
        QmUE::UObject* box = QmUE::SpawnObjectViaUFunction(boxClass, widgetTree);
        if (!box || !box->Class) return false;

        StyleSearchBox(box, font, row.size > 0.0f ? row.size : kDefFontSizeBody);

        const wchar_t* seed = (prev && prev[0]) ? prev
                            : (row.text && row.text[0]) ? row.text : defSeed;
        if (QmUE::UFunction* fnSet = QmUE::FindFunctionOnClass(box->Class, "SetText"))
        {
            uint8_t ft[16]; memset(ft, 0, sizeof(ft));
            if (QmUE::TextFromString(seed, ft)) QmUE::CallProcessEvent(box, fnSet, ft);
        }

        QmUE::UFunction* fnAdd = QmUE::FindFunctionOnClass(panel->Class, "AddChild");
        if (!fnAdd) return false;
        P_AddChild ac; ac.Content = box; ac.ReturnValue = nullptr;
        if (!QmUE::CallProcessEvent(panel, fnAdd, &ac)) return false;
        if (ac.ReturnValue)
            StyleSlot(reinterpret_cast<QmUE::UObject*>(ac.ReturnValue), row.gap, row.halign, row.indent);
        if (outSlot) *outSlot = reinterpret_cast<QmUE::UObject*>(ac.ReturnValue);

        *latch = box;
        QM_LOG_DEBUG("[ModTab]   view: %s box=0x%p seed='%ls'", logTag, (void*)box, seed);
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
                                float gapAbove = 0.0f, QmUE::UObject** outSlot = nullptr)
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
                if (outSlot) *outSlot = reinterpret_cast<QmUE::UObject*>(ac.ReturnValue);
                return hdr;
            }
        }
        return nullptr;
    }

    // Vertical-box slot styling assumes a column; an inline (sameRow) group instead lives in a
    // HorizontalBox where the row's gap means "left spacing" and children center vertically. This
    // re-styles a member's slot AFTER its Add*Row call (which set column-style top padding): left
    // padding for spacing, vertical-center, and a Fill/Auto size rule.
    void StyleHSlot(QmUE::UObject* slot, float leftPad, float fillWeight)
    {
        if (!slot || !slot->Class) return;
        if (QmUE::UFunction* fnPad = QmUE::FindFunctionOnClass(slot->Class, "SetPadding"))
        {
            float margin[4] = { leftPad, 0.0f, 0.0f, 0.0f };   // FMargin {L,T,R,B}
            QmUE::CallProcessEvent(slot, fnPad, margin);
        }
        if (QmUE::UFunction* fnVA = QmUE::FindFunctionOnClass(slot->Class, "SetVerticalAlignment"))
        {
            uint8_t p[8]; memset(p, 0, sizeof(p)); p[0] = 2;   // VAlign_Center
            QmUE::CallProcessEvent(slot, fnVA, p);
        }
        if (QmUE::UFunction* fnSize = QmUE::FindFunctionOnClass(slot->Class, "SetSize"))
        {
            // FSlateChildSize: Automatic=0, Fill=1; the Fill Value is the weight relative to the
            // sibling slots' weights (1 and 2 -> a 1:2 width split).
            bool fill = fillWeight > 0.0f;
            struct { float Value; uint8_t SizeRule; uint8_t pad[3]; } sz =
                { fill ? fillWeight : 1.0f, (uint8_t)(fill ? 1 : 0), {} };
            QmUE::CallProcessEvent(slot, fnSize, &sz);
        }
    }

    // Render one PanelRow into `panel` (the content column, or a HorizontalBox for an inline
    // group). Mirrors the build's type switch, bumps the per-kind counters, and on success hands
    // back the child's panel-slot via outSlot for inline horizontal fix-up. Returns whether a
    // widget landed (so the caller can count it).
    bool RenderRowInto(QmUE::UObject* panel, const PanelRow& r,
                       QmUE::UObject* screen, QmUE::UObject* owningPlayer, QmUE::UObject* wblCDO,
                       QmUE::UFunction* fnCreate, QmUE::UObject*& donor, QmUE::UObject* widgetTree,
                       QmUE::UObject* barDonor, const uint8_t* font,
                       int& headers, int& buttons, int& combos, int& wired,
                       QmUE::UObject** outSlot)
    {
        if (outSlot) *outSlot = nullptr;
        if (r.type == kRowHeader)
        {
            if (AddHeaderRow(panel, screen, owningPlayer, wblCDO, fnCreate, r.text, r.gap, outSlot))
                { ++headers; return true; }
            return AddTextRow(panel, widgetTree, r.text, r.color, font,
                              r.size > 0.0f ? r.size : kDefFontSizeHeader, r.wrap, r.gap, r.halign,
                              0.0f, outSlot) != nullptr;
        }
        if (r.type == kRowButton)
        {
            bool w = false;
            if (AddButtonRow(panel, widgetTree, screen, owningPlayer, wblCDO, fnCreate, donor, r, w, outSlot))
                { ++buttons; if (w) ++wired; return true; }
            return false;
        }
        if (r.type == kRowItemDropdown)
        {
            if (AddItemDropdownRow(panel, widgetTree, font, r, barDonor, outSlot))
                { ++combos; return true; }
            return false;
        }
        if (r.type == kRowCategoryDropdown)
        {
            if (AddCategoryDropdownRow(panel, widgetTree, font, r, barDonor, outSlot))
                { ++combos; return true; }
            return false;
        }
        if (r.type == kRowItemSearch)
            return AddItemSearchRow(panel, widgetTree, font, r, outSlot);
        if (r.type == kRowItemCount)
            return AddCountBoxRow(panel, widgetTree, font, r,
                                  &g_countBox, GetItemCountText(), L"1", "item count", outSlot);
        if (r.type == kRowXpCount)
            return AddCountBoxRow(panel, widgetTree, font, r,
                                  &g_xpBox, GetXpCountText(), L"100", "xp amount", outSlot);
        return AddTextRow(panel, widgetTree, r.text, r.color, font,
                          r.size > 0.0f ? r.size : kDefFontSizeBody, r.wrap, r.gap, r.halign,
                          r.indent, outSlot) != nullptr;
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
            SnapshotItemSelection();
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
            g_buttonActionCount = 0;        // actions die with their panel; re-latched by the build
            g_itemCombo         = nullptr;  // so do the dropdown/search/count/xp latches
            g_catCombo          = nullptr;
            g_searchBox         = nullptr;
            g_countBox          = nullptr;
            g_xpBox             = nullptr;
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
        // the mount - Slate reads WidgetBarStyle once, when the SScrollBox is built. The donor
        // outlives this block: the item dropdown clones the same style for its open list.
        QmUE::UObject* barDonor = nullptr;
        if (ourPanel)
        {
            barDonor = ResolveNativeSettingsList(settingsPanel);
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
                int rows = 0, headers = 0, buttons = 0, wiredCount = 0, combos = 0;
                g_buttonActionCount = 0;
                g_itemCombo         = nullptr;
                g_catCombo          = nullptr;
                int hboxes = 0;
                for (int i = 0; layout && i < rowCount; )
                {
                    // A row plus any consecutive sameRow followers form one inline group; a lone
                    // row (group size 1) renders straight into the content column as before.
                    int j = i + 1;
                    while (j < rowCount && layout[j].sameRow) ++j;
                    if (j - i == 1)
                    {
                        if (RenderRowInto(ourPanel, layout[i], screen, owningPlayer, wblCDO, fnCreate,
                                          donor, widgetTree, barDonor, font,
                                          headers, buttons, combos, wiredCount, nullptr))
                            ++rows;
                        i = j;
                        continue;
                    }

                    // Inline group: a HorizontalBox hosts the members; it carries the lead row's
                    // vertical gap into the column, members pack left-to-right inside it.
                    QmUE::UObject* hbox = nullptr;
                    if (QmUE::UClass* hbCls = QmUE::FindClassByName("HorizontalBox"))
                        hbox = QmUE::SpawnObjectViaUFunction(hbCls, widgetTree);
                    if (hbox && hbox->Class)
                    {
                        if (QmUE::UFunction* fnAdd = QmUE::FindFunctionOnClass(ourPanel->Class, "AddChild"))
                        {
                            P_AddChild ac; ac.Content = hbox; ac.ReturnValue = nullptr;
                            if (QmUE::CallProcessEvent(ourPanel, fnAdd, &ac) && ac.ReturnValue)
                                StyleSlot(reinterpret_cast<QmUE::UObject*>(ac.ReturnValue),
                                          layout[i].gap, layout[i].halign);
                        }
                        ++hboxes;
                        for (int k = i; k < j; ++k)
                        {
                            QmUE::UObject* slot = nullptr;
                            if (RenderRowInto(hbox, layout[k], screen, owningPlayer, wblCDO, fnCreate,
                                              donor, widgetTree, barDonor, font,
                                              headers, buttons, combos, wiredCount, &slot))
                            {
                                ++rows;
                                // Lead has no left spacing; dropdown members fill the width
                                // (pushing trailing buttons to the right), others auto-size.
                                // An explicit "fill" weight on the row overrides the type
                                // default (weights are relative -> 1 and 2 = a 1:2 split).
                                float w = layout[k].fill;
                                if (w < 0.0f)
                                    w = (layout[k].type == kRowItemDropdown ||
                                         layout[k].type == kRowCategoryDropdown ||
                                         layout[k].type == kRowItemSearch) ? 1.0f : 0.0f;
                                StyleHSlot(slot, k == i ? 0.0f : layout[k].gap, w);
                            }
                        }
                    }
                    else
                    {
                        // HorizontalBox unavailable: degrade to stacked vertical rows (never lose content).
                        for (int k = i; k < j; ++k)
                            if (RenderRowInto(ourPanel, layout[k], screen, owningPlayer, wblCDO, fnCreate,
                                              donor, widgetTree, barDonor, font,
                                              headers, buttons, combos, wiredCount, nullptr))
                                ++rows;
                    }
                    i = j;
                }
                QM_LOG_DEBUG("[ModTab]   view: CONTENT build rows=%d/%d headers=%d buttons=%d wired=%d "
                             "combos=%d inlineRows=%d gameFont=%d (wired clicks via the PE BndEvt watch)",
                             rows, rowCount, headers, buttons, wiredCount, combos, hboxes, font ? 1 : 0);
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

    // Settings closed: the screen tree is about to be released (or pooled), and the next
    // world travel GCs every widget we latched. Recycled UObject memory does NOT fault, so
    // SEH cannot turn a stale dereference into a no-op - it lands on a live FOREIGN object
    // and dispatches reflected calls into it (the dead-HUD/input bug). Therefore the
    // persistence state is read back NOW, while the widgets are provably alive, our panel
    // is unparented (a pooled tree must not carry a second copy into the next mount), and
    // every widget latch is dropped. The next open rebuilds everything from scratch.
    void DropPanelLatches()
    {
        SnapshotItemSelection();
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
        }
        g_ourPanel          = nullptr;
        g_mountTarget       = nullptr;
        g_buttonActionCount = 0;
        g_itemCombo         = nullptr;
        g_catCombo          = nullptr;
        g_searchBox         = nullptr;
        g_countBox          = nullptr;
        g_xpBox             = nullptr;
        QM_LOG_DEBUG("[ModTab] settings closed - spawner state snapshotted, panel unparented, "
                     "all widget latches dropped");
    }

    // Neither the category combo nor the search box broadcasts a ProcessEvent without a
    // bound delegate, so both are polled from the PE hook: gated on the live latches (all
    // null while no panel exists - zero cost outside an open settings session), throttled
    // to one reflected read per control per kSpawnerPollMs, NO GObjects walk (the lag
    // lesson). The search read is two-stage: GetText's FText data POINTER is compared per
    // tick, the string conversion only runs on an actual change (typing). Any change swaps
    // the filtered catalog view and refills the item combo in place.
    void PollSpawnerControls()
    {
        constexpr ULONGLONG kSpawnerPollMs = 250;
        static ULONGLONG s_lastPoll = 0;
        static void*     s_lastTextData = nullptr;   // FText::TextData identity of the last read

        if (!g_itemCombo || (!g_catCombo && !g_searchBox)) return;
        ULONGLONG now = GetTickCount64();
        if (now - s_lastPoll < kSpawnerPollMs) return;
        s_lastPoll = now;

        bool refill = false;

        if (g_catCombo)
        {
            QmUE::UObject* cat = reinterpret_cast<QmUE::UObject*>(g_catCombo);
            int idx = -1;
            __try
            {
                if (cat->Class)
                    if (QmUE::UFunction* fn = QmUE::FindFunctionOnClass(cat->Class, "GetSelectedIndex"))
                    {
                        int32_t p[2] = { -1, 0 };
                        if (QmUE::CallProcessEvent(cat, fn, p)) idx = p[0];
                    }
            }
            __except (EXCEPTION_EXECUTE_HANDLER) { idx = -1; }
            if (idx >= 0 && idx != GetActiveItemCategory() && idx < GetItemCategoryCount())
            {
                SetActiveItemCategory(idx);
                refill = true;
                QM_LOG_INFO("[ModTab] item category -> %d", idx);
            }
        }

        if (g_searchBox)
        {
            QmUE::UObject* box = reinterpret_cast<QmUE::UObject*>(g_searchBox);
            uint8_t ft[16]; memset(ft, 0, sizeof(ft));
            bool haveText = false;
            __try
            {
                if (box->Class)
                    if (QmUE::UFunction* fn = QmUE::FindFunctionOnClass(box->Class, "GetText"))
                        haveText = QmUE::CallProcessEvent(box, fn, ft);
            }
            __except (EXCEPTION_EXECUTE_HANDLER) { haveText = false; }
            if (haveText)
            {
                void* data = *reinterpret_cast<void**>(ft);
                if (data != s_lastTextData)
                {
                    s_lastTextData = data;
                    wchar_t buf[256];
                    if (QmUE::StringFromText(ft, buf, 256) && SetItemSearchText(buf))
                        refill = true;
                }
            }
        }

        if (!refill) return;
        g_lastItemSel = 0;   // the old selection indexes the previous slice - reset, don't carry
        int added = 0;
        __try
        {
            added = FillItemCombo(reinterpret_cast<QmUE::UObject*>(g_itemCombo));
        }
        __except (EXCEPTION_EXECUTE_HANDLER) {}
        QM_LOG_INFO("[ModTab] spawner filter: cat=%d search-len=%d -> %d item(s)",
                    GetActiveItemCategory(), (int)wcslen(GetItemSearchText()), added);
    }
}
