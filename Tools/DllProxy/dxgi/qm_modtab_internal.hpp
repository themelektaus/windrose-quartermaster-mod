// Quartermaster "Mod Settings Tab" - internal shared declarations.
// Public API: qm_modtab.hpp. This header is only included by the qm_modtab_* TUs.
//
// Module layout:
//   qm_modtab.cpp         core: profile-based arming, ProcessInternal rider (lifecycle +
//                         recon driver),
//                         PLSF thunks (cook-pre inject, cook-post mount, tab-state gate,
//                         SetData sync check), self-heal bootstrap
//   qm_modtab_util.cpp    generic SEH-guarded read/describe/dump primitives
//   qm_modtab_widgets.cpp reflected UMG layer: tree walks, visibility, panel build + mount
//                         (ProbeViewPath)
//   qm_modtab_layout.cpp  data-driven panel content: compiled-in base layout (baked from
//                         qm_modtab_layout.json at build time) + optional user-extension
//                         files (qm_modtab_layout_*.json) -> PanelRow view
//   qm_modtab_inject.cpp  tab data layer: Quartermaster collection build + array append
//   qm_modtab_recon.cpp   logging-only diagnostics (class enum + layout dumps; QM_DIAG only)

#pragma once

#include <windows.h>
#include <stdint.h>

#include "qm_ue.hpp"

namespace ModTab
{
    // Settings-screen BP classes (lazy-loaded at first settings open, NOT at boot).
    constexpr const char* kSettingsScreenClass     = "WBP_Settings_Screen_C";
    constexpr const char* kSettingsControllerClass = "BP_Settings_SC_C";
    constexpr const char* kTabsGroupClass          = "WBP_MetaUI_TabsGroup_C";

    // UR5SettingScreen / UGameSettingRegistry fields (Dumper-7 SDK).
    constexpr uintptr_t kOff_Screen_Registry = 0x3A8;   // UGameSettingScreen::Registry
    constexpr uintptr_t kOff_Screen_Tabs     = 0x3B8;   // UR5SettingScreen::Tabs
    constexpr uintptr_t kOff_Reg_TopLevel    = 0x88;    // UGameSettingRegistry::TopLevelSettings
    constexpr uintptr_t kOff_Reg_Registered  = 0x98;    // UGameSettingRegistry::RegisteredSettings
    constexpr uintptr_t kOff_Reg_OwningLP    = 0xA8;    // UGameSettingRegistry::OwningLocalPlayer

    // FFrame fields (the script-VM frame handed to PLSF/ProcessInternal).
    constexpr uintptr_t kFFrameNodeOff   = 0x10;   // FFrame::Node   (the executing UFunction)
    constexpr uintptr_t kFFrameLocalsOff = 0x28;   // FFrame::Locals (the packed param block)

    // ESlateVisibility values driven by the gate.
    enum : uint8_t { ESV_Visible = 0, ESV_Collapsed = 1 };

    // Hexdump cap for parms buffers (parms are small; this is a safety cap).
    constexpr int32_t kMaxParmsDump = 256;

    struct ArrHdr { void* data; int32_t num; int32_t max; bool ok; };

    // ---- shared state (defined in qm_modtab.cpp) --------------------------------------------
    // Mount handles, owned by ProbeViewPath. Stale-instance footgun applies to everything
    // around them: every settings (re)open builds a brand-new screen hierarchy while previous
    // instances linger un-GC'd in GObjects, so FindFirstInstanceOfClass returns the STALE one -
    // live instances are latched from their own fresh dispatches instead.
    extern void* g_ourPanel;        // our content ScrollBox (rebuilt fresh on every cook)
    extern void* g_mountTarget;     // the content VerticalBox the panel is parented into
    extern void* g_nativePanel;     // native WBP_Settings_Panel_C (inverse-gated vs our panel)

    // Click-action latches for the themed buttons INSIDE our panel. A click reaches us as a
    // BndEvt__*_OnClick ProcessEvent dispatch on exactly that widget instance (delegate
    // re-dispatch from its inner button) - matched by pointer in QmModTab_OnProcessEvent,
    // never dereferenced. Rebuilt with every panel build (count reset on discard);
    // command/argument point into the layout storage, which only reloads during a build.
    struct ButtonAction { void* widget; const char* command; const char* argument; };
    constexpr int kMaxButtonActions = 8;
    extern ButtonAction g_buttonActions[kMaxButtonActions];
    extern int          g_buttonActionCount;

    // The item-spawner ComboBoxStrings (rebuilt with every panel build, die with their panel).
    // The item combo's selection is only READ at dispatch time ("add_selected_item" click) -
    // no delegate binding; g_lastItemSel re-applies the last known selection across panel
    // rebuilds (the category pick itself persists in qm_modtab_layout.cpp's active-category
    // state). The CATEGORY combo broadcasts nothing without a bound delegate, so its
    // selection is polled (throttled pointer-held reads, no GObjects walk) from the
    // ProcessEvent hook; a change refills the item combo with that category's slice.
    extern void* g_itemCombo;
    extern int   g_lastItemSel;     // index into the FILTERED item list (active category)
    extern void* g_catCombo;

    // ---- qm_modtab_util.cpp ------------------------------------------------------------------
    bool    LocateSidecarDir(char* out, size_t outSz);
    void    DescribeObject(QmUE::UObject* obj, char* out, size_t outSz);
    bool    ContainsLc(const char* hay, const char* needleLc);
    int32_t ParmsSize(QmUE::UFunction* func);
    void    HexDump(const char* tag, const uint8_t* base, int32_t cap);
    void    ScanForTArrays(const uint8_t* parms, int32_t size);
    void*   ReadPtr(const void* p);
    ArrHdr  ReadArrHdr(const void* p);
    bool    ReadFTextNarrow(const void* ftext, char* out, size_t outSz);

    // ---- qm_modtab_widgets.cpp ---------------------------------------------------------------
    bool    SetWidgetVisibility(QmUE::UObject* widget, uint8_t vis);
    int     GetWidgetVisibility(QmUE::UObject* widget);   // -1 when unreadable
    void    ProbeViewPath(QmUE::UObject* screen);
    bool    OurPanelMounted();
    // Throttled category-combo selection watch (PE-hook driven, see g_catCombo above).
    void    PollCategoryDropdown();

    // ---- qm_modtab_layout.cpp ----------------------------------------------------------------
    // kRowMods never reaches the build: GetPanelLayout expands it into text rows from
    // qm_modtab_mods.txt (the Configurator's pre-merged installed-mods file; flush-left
    // lines = mod names, indented lines = that mod's detail rows). kRowUserLayout never
    // reaches the build either: it marks where the rows from the optional
    // qm_modtab_layout_*.json user-extension files splice into the base layout.
    enum : int { kRowText = 0, kRowHeader = 1, kRowButton = 2, kRowMods = 3, kRowItemDropdown = 4,
                 kRowUserLayout = 5, kRowCategoryDropdown = 6 };
    // One content row, as a plain-pointer view over storage owned by qm_modtab_layout.cpp.
    // Pointers stay valid until the next GetPanelLayout call (the build consumes them within
    // one cook frame; the button actions latch them - see ButtonAction above).
    struct PanelRow
    {
        int            type;
        const wchar_t* text;
        float          size;       // font size; <= 0 -> kind default
        const float*   color;      // RGBA 0..1; nullptr -> widget default
        bool           wrap;       // auto-wrap (text rows)
        float          gap;        // vertical space above the row
        uint8_t        halign;     // EHorizontalAlignment; 255 = slot default (Fill)
        float          indent;     // left padding on the slot (mod detail rows)
        bool           sameRow;    // true -> this row shares a horizontal row with the one above it
        const char*    command;    // buttons: action id (see DispatchButtonCommand); nullptr otherwise
        const char*    argument;   // buttons: first argument; nullptr otherwise
    };
    // Rows from the compiled-in base layout (baked from the repo qm_modtab_layout.json at
    // build time), extended by optional qm_modtab_layout_*.json files in the Quartermaster
    // sidecar folder (re-read whenever their set or write times change) - never returns an
    // empty layout.
    const PanelRow* GetPanelLayout(int* outCount);

    // Item catalog backing kRowItemDropdown / kRowCategoryDropdown rows: qm_modtab_items.txt
    // (the Configurator's pre-built "<AssetId>|<Display name>|<PackagePath>|<Category>" list),
    // loaded by GetPanelLayout alongside the rows. The item accessors are views over the
    // FILTERED list (the active category's slice; category 0 = "All" = whole catalog), so
    // their indices are exactly the item combo's option indices. Pointers stay valid until
    // the next GetPanelLayout / SetActiveItemCategory.
    int            GetItemOptionCount();
    const wchar_t* GetItemOptionName(int idx);   // nullptr when out of range
    const char*    GetItemOptionKey(int idx);    // nullptr when out of range
    const char*    GetItemOptionPkg(int idx);    // custom items: mounted package path for the grant's sync-load fallback; nullptr otherwise
    int            GetItemCategoryCount();       // includes the synthetic "All" at index 0; 0 when no catalog
    const wchar_t* GetItemCategoryName(int idx); // nullptr when out of range
    int            GetActiveItemCategory();      // survives panel rebuilds; kept by NAME across catalog reloads
    void           SetActiveItemCategory(int idx); // clamps; rebuilds the filtered view

    // ---- qm_modtab_inject.cpp ----------------------------------------------------------------
    bool    OurCollectionPresentInTabs(QmUE::UObject* screen);
    bool    IsOurCollectionAt(QmUE::UObject* screen, int32_t idx);
    bool    EnsureTabInjected(QmUE::UObject* screen);
    // bootstrapMount: run the panel mount from this self-heal path too (true until the
    // CookTabs-post hook has proven itself - see g_cookTabsHookLive in qm_modtab.cpp).
    void    TryLivenessInjectDupTab(QmUE::UObject* screen, bool bootstrapMount);

    // ---- qm_modtab_recon.cpp -----------------------------------------------------------------
    void    TryEnumerateSettingsClasses();
    void    DumpGetTabsReconOnce(QmUE::UObject* screen);
}
