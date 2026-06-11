// Quartermaster "Mod Settings Tab" - injects a "Quartermaster" tab into the native settings
// screen (tab-data append + own content panel mounted in the CookTabs-post moment), without a
// UE4SS dependency. Architecture details: qm_modtab.cpp / qm_modtab_internal.hpp.
//
// Arming: any qm_profile_*.json in the Quartermaster sidecar folder (the per-pak source of
// truth the Configurator deploys with every build). No profiles = zero cost, the module is
// never consulted - the DLL itself is removed alongside the last profile anyway.

#pragma once

#include "qm_ue.hpp"

// Arm the module from the installed profiles. Call once at startup (off DllMain) so the armed
// state is logged and ReconArmed() is warm before the probe loop runs.
bool QmModTab_Init();

// Cached armed state; gates whether the shared hooks consult this module at all.
bool QmModTab_ReconArmed();

// Rider on the shared ProcessInternal detour, called for every Blueprint script-function
// dispatch that takes the ProcessEvent->ProcessInternal path (OnEnter/OnExit, Construct,
// clicks). Carries the lifecycle handling + the tab-click visibility gate. `parms` is the
// function's packed param block (FFrame.Locals), laid out exactly like a ProcessEvent Parms
// buffer. Hot path: pointer compare + bit test (memoized verdict). Game thread. SEH-guarded.
void QmModTab_OnProcessInternal(QmUE::UObject* self, QmUE::UFunction* func, void* parms);

// Early fn-target resolve driver + nexus-button click watch on the global ProcessEvent hook's
// PRE position. ProcessEvent is live from engine start - the earliest safe moment to poll
// GObjects for the lazy-loaded settings BP classes and latch the PLSF target handles (throttled
// + latched). The click watch stays live afterwards: the panel's themed button dispatches its
// click as a BndEvt__*_OnClick ProcessEvent on itself - matched by pointer, opens the mod's
// nexusmods page. A modtab-only deploy installs the PE net-hook for exactly this. SEH-guarded.
void QmModTab_OnProcessEvent(QmUE::UObject* self, QmUE::UFunction* func, void* parms);

// Called from the global ProcessLocalScriptFunction detour (qm_hook.cpp) for EVERY Blueprint
// script-function execution - the only funnel that sees BP-internal calls. Matches FFrame::Node
// against the resolved target handles; on a match runs the corresponding thunk (which forwards
// through the PLSF trampoline itself) and returns true. false = caller forwards to the
// trampoline. Hot path: one SEH-guarded read + three pointer compares. Game thread.
bool QmModTab_OnScriptFunction(void* context, void* stack, void* result);

// Hands the module the MinHook trampoline to the real PLSF body. MUST be called before the
// detour is enabled: the thunks forward through it (forwarding through ProcessInternal instead
// would re-enter the patched PLSF entry and recurse).
void QmModTab_SetPlsfOriginal(QmUE::FNativeFuncPtr orig);
