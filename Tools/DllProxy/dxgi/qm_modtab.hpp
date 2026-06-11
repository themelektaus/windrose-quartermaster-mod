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
// RECON FINDING (2026-06-10, sharpened 2026-06-11 by the 18q log): these three decisive functions
// are called Blueprint-to-Blueprint INTERNALLY (CookTabs -> SetData -> OnTabsStateChanged), which
// bypasses BOTH public entries: never ProcessEvent (the net-hook only sees engine->script dispatch)
// and - 18q-disproven assumption - never ProcessInternal either (that is only the ProcessEvent->
// Invoke exec for BP functions; an in-field-verified ExecFunction swap never fired once). The VM
// routes BP-internal calls straight into ProcessLocalScriptFunction (PLSF), the script-VM body
// executor EVERYTHING funnels through. #18r therefore hooks PLSF globally (qm_hook.cpp; its own
// body, no MinHook collision with the lifecycle detour on ProcessInternal) - the same layer UE4SS's
// HookProcessLocalScriptFunction provides, which is how the reference mod catches CookTabs. The
// ProcessInternal rider (DispatchProcessInternalRiders) stays for OnEnter/OnExit, which DO
// dispatch via ProcessEvent->ProcessInternal.
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

// EARLY FN-TARGET RESOLVE DRIVER, called from the global ProcessEvent hook's PRE position for every
// dispatch. ProcessEvent is live from engine start (game thread) - the earliest safe moment to poll GObjects
// for the settings BP classes and latch the three target UFunction handles the PLSF detour matches against.
// Throttled (~1/frame) + latched internally; cheap no-op once resolved or when modtab is not armed.
// SEH-guarded. A modtab-only deploy installs the PE net-hook for exactly this driver.
void QmModTab_OnProcessEvent(QmUE::UObject* self, QmUE::UFunction* func, void* parms);

// #18r: called from the global ProcessLocalScriptFunction detour (qm_hook.cpp) for EVERY Blueprint
// script-function execution - the only funnel that sees BP-internal calls. Matches FFrame::Node (+0x10)
// against the resolved target handles; on a match it runs the corresponding thunk (which forwards through
// the PLSF trampoline itself) and returns true. false = caller forwards to the trampoline. Hot path: one
// SEH-guarded read + three pointer compares. Game thread.
bool QmModTab_OnScriptFunction(void* context, void* stack, void* result);

// Hands the module the MinHook trampoline to the real PLSF body. MUST be called before the detour is
// enabled: the thunks forward through it (forwarding through ProcessInternal instead would re-enter the
// patched PLSF entry and recurse).
void QmModTab_SetPlsfOriginal(QmUE::FNativeFuncPtr orig);
