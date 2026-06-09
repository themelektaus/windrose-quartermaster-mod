// Quartermaster "XP for kills" - seed-free XP grant driven by enemy kills.
// ---------------------------------------------------------------------
// Windrose grants XP only through the native scenario path
// R5ScenarioTask_AddExp::Execute (publishes an "AddExp" command -> the BL rule does
// TotalExp+=exp + level recompute + notification + save). There is NO reflected
// mutator and a raw record write proved cosmetic, so this module drives the engine's
// real Execute on a task byte-cloned from the AddExp CDO (no POI seed) with the
// gate-relevant fields wired. The grant is persistent and indistinguishable from a
// real quest/POI reward. Implementation + full RE notes live in qm_killxp.cpp.
//
// Triggers (each opt-in via a sentinel next to dxgi.dll; no sentinel = zero cost):
//   qm_killxp.txt                 : arms the module (kill detection + the triggers)
//   qm_killxp_onkill.txt           : grant on every player kill; content = XP/kill
//   qm_killxp_construct_grant.txt  : one-shot manual test grant (rising-edge)

#pragma once

#include "qm_ue.hpp"

// Read the sentinel `qm_killxp.txt` next to this DLL. Returns true iff present
// (armed). Result is cached. Call once at startup (off DllMain) so the armed
// state is logged and ReconArmed() is warm before the probe loop reads it.
bool QmKillXp_Init();

// True after QmKillXp_Init() found the sentinel. Cheap cached read; also used by
// the probe loop to decide whether to install the global ProcessEvent net-hook.
bool QmKillXp_ReconArmed();

// Cheap per-ProcessEvent-call probe, called from the global net-hook for every
// dispatch. No-op until armed. Detects the player's OnDamageDealt_Event kill flag
// (per-UFunction memoized name verdict, so name resolution runs ONCE per distinct
// function) and - when qm_killxp_onkill.txt is armed - fires the seed-free XP grant.
// Also drives the throttled config refresh + the one-shot manual test grant. Game-
// thread only (ProcessEvent dispatches in-thread). SEH-guarded internally.
void QmKillXp_OnProcessEvent(QmUE::UObject* self, QmUE::UFunction* func, void* parms);
