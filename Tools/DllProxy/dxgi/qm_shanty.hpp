// Quartermaster "Always Shanties" - keep the crew shanty playing after you leave the helm.
// ---------------------------------------------------------------------------------------
// Vanilla: at the helm, B starts/stops the shanty; leaving the helm stops it. This module
// prevents ONLY the helm-leave stop - B start/stop at the helm stays vanilla, nothing more.
// It rides the global ProcessEvent net-hook and discriminates a helm-leave from a manual
// B-stop purely on dispatch timing (offset-free); see qm_shanty.cpp for the full rationale.
//
// Trigger (opt-in via a sentinel next to dxgi.dll; no sentinel = zero cost - the module is
// not even consulted from the net-hook):
//   qm_shanty*.txt : arms the keep-alive (qm_shanty.txt for manual/dev use, or the
//                    profile-bound qm_shanty_<profile>.txt the Configurator deploys).

#pragma once

#include "qm_ue.hpp"

// Arm the module from any qm_shanty*.txt sentinel next to this DLL. Result is cached.
// Call once at startup (off DllMain) so the armed state is logged and ReconArmed() is
// warm before the probe loop decides whether to install the ProcessEvent net-hook.
bool QmShanty_Init();

// True after QmShanty_Init() armed the module. Cheap cached read; the probe loop uses it
// (alongside the other modules) to decide whether the global ProcessEvent net-hook is needed.
bool QmShanty_ReconArmed();

// Cheap per-ProcessEvent-call probe, called from the global net-hook for every dispatch.
// No-op until armed (returns false). Tracks the helm toggle input + shanty enable/disable
// and, when a ServerDisableShanty is identified as a helm-leave (not a manual B-stop),
// returns TRUE to tell the net-hook NOT to forward the original ProcessEvent - so the
// disable never runs and the shanty keeps playing. Returns false for every other dispatch
// (forward normally). Per-UFunction memoized name verdict. Game-thread only. SEH-guarded.
bool QmShanty_OnProcessEvent(QmUE::UObject* self, QmUE::UFunction* func, void* parms);
