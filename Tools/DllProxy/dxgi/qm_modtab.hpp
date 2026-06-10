// Quartermaster "Mod Settings Tab" (RECON phase) - inject a "Quartermaster" tab into the
// native settings screen with simple content.
// -----------------------------------------------------------------------------------------
// Goal: replicate what the "Windrose Mod Settings" reference does (a UE4SS C++ mod) but from
// our own DXGI proxy - no UE4SS dependency. The reference adds a tab to the native settings
// screen by hooking the screen's tab-building UFunctions and growing the tab data array.
//
// The vanilla settings screen (reverse-engineered from the reference's main.dll strings):
//   - BP_Settings_SC_C            : settings screen controller. Fn: CookTabs (builds the tab
//                                   list), GoToNextTab / GoToPreviousTab, OnExit.
//   - WBP_MetaUI_TabsGroup_C      : the tab bar widget.        Fn: SetData(TabsData array).
//   - WBP_Settings_Screen_C       : the screen widget.         Fn: OnTabsStateChanged.
//   - WBP_MetaUI_Tab_Main_C       : one tab (txt_TabName).
// RECON FINDING (2026-06-10): these three decisive functions are called Blueprint-to-Blueprint
// INTERNALLY (CookTabs -> SetData -> OnTabsStateChanged), which bypasses the public
// UObject::ProcessEvent entry entirely. The global ProcessEvent net-hook only sees engine->script
// dispatch (lifecycle events, Tick, input, RPCs) - it caught Construct/Tick on these widgets but
// never CookTabs/SetData. So this module instead rides UObject::ProcessInternal, the Blueprint
// VM's universal script-function funnel (same vector UE4SS uses), which DOES see the BP-internal
// calls. NOTE: every pure-BP UFunction's ExecFunction *is* ProcessInternal, so the lifecycle
// pre-warm hook (sitting on ReceiveBeginPlay's ExecFunction) already occupies that single
// address - MinHook forbids a second hook there. This module therefore piggybacks on that one
// detour via DispatchProcessInternalRiders (qm_hook.cpp) instead of installing its own.
//
// THIS FILE IS THE RECON PHASE: logging-only. It NEVER modifies parms or suppresses dispatch.
// It observes the three decisive UFunctions and dumps enough (self identity, parms size, a
// hexdump of the parms buffer, and a TArray-header heuristic) to pin down the TabsData array
// offset + element stride before we write the actual injection.
//
// Trigger (opt-in via a sentinel next to dxgi.dll; no sentinel = zero cost - the module is
// not even consulted from the net-hook):
//   qm_modtab*.txt : arms the recon (qm_modtab.txt for manual/dev use, or a profile-bound
//                    qm_modtab_<profile>.txt a future Configurator deploy would write).

#pragma once

#include "qm_ue.hpp"

// Arm the module from any qm_modtab*.txt sentinel next to this DLL. Result is cached.
// Call once at startup (off DllMain) so the armed state is logged and ReconArmed() is warm
// before the probe loop runs. The recon rides the shared ProcessInternal detour (the lifecycle
// pre-warm hook), so no separate hook install is gated on it.
bool QmModTab_Init();

// True after QmModTab_Init() armed the module. Cheap cached read; gates whether the
// ProcessInternal-rider dispatch in the shared lifecycle detour consults this module.
bool QmModTab_ReconArmed();

// Cheap per-call probe, called from the ProcessInternal hook for every Blueprint script-function
// dispatch. No-op until armed. Recon phase: logging only - inspects the settings-screen UFunctions
// and logs their dispatch + parms layout. Never modifies anything. `parms` is the function's
// packed param block (FFrame.Locals from the ProcessInternal frame), laid out exactly like a
// ProcessEvent Parms buffer. Per-UFunction memoized name verdict keeps the hot path to a pointer
// compare + bit test. Game-thread only. SEH-guarded.
void QmModTab_OnProcessInternal(QmUE::UObject* self, QmUE::UFunction* func, void* parms);
