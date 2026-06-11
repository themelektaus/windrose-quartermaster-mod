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
//   qm_modtab_layout.cpp  data-driven panel content: qm_modtab_layout.json -> PanelRow view
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

    // The item-spawner ComboBoxString (rebuilt with every panel build, dies with its panel).
    // Selection is only READ at dispatch time ("add_selected_item" click) - no delegate
    // binding; g_lastItemSel re-applies the last known selection across panel rebuilds.
    extern void* g_itemCombo;
    extern int   g_lastItemSel;

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

    // ---- qm_modtab_layout.cpp ----------------------------------------------------------------
    // kRowMods never reaches the build: GetPanelLayout expands it into text rows from
    // qm_modtab_mods.txt (the Configurator's pre-merged installed-mods file; flush-left
    // lines = mod names, indented lines = that mod's detail rows).
    enum : int { kRowText = 0, kRowHeader = 1, kRowButton = 2, kRowMods = 3, kRowItemDropdown = 4 };
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
    // Rows from qm_modtab_layout.json in the Quartermaster sidecar folder (re-read when its
    // write time changes),
    // falling back to a compiled-in default - never returns an empty layout.
    const PanelRow* GetPanelLayout(int* outCount);

    // Item catalog backing kRowItemDropdown rows: qm_modtab_items.txt (the Configurator's
    // pre-built "<AssetId>|<Display name>" list), loaded by GetPanelLayout alongside the rows.
    // Indices are dropdown option indices; pointers stay valid until the next GetPanelLayout.
    int            GetItemOptionCount();
    const wchar_t* GetItemOptionName(int idx);   // nullptr when out of range
    const char*    GetItemOptionKey(int idx);    // nullptr when out of range

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
