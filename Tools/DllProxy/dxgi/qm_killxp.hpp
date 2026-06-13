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
//   qm_killxp.txt                  : arms the module (kill detection + the triggers)
//   qm_killxp_onkill_<profile>.txt : grant on every player kill, with per-enemy XP.
//                                    Profile-bound (key=value: default=N, <ClassName>=N),
//                                    read once at startup; also arms the module on its own.
//   qm_killxp_construct_grant.txt  : one-shot manual test grant (rising-edge)

#pragma once

#include "qm_ue.hpp"

// Arm the module + read the per-kill reward config ONCE. Armed iff qm_killxp.txt
// OR any qm_killxp_onkill*.txt is present in the Quartermaster sidecar folder; the reward table is
// parsed here (not re-read later). Result is cached. Call once at startup (off
// DllMain) so the armed state is logged and ReconArmed() is warm before the probe
// loop reads it.
bool QmKillXp_Init();

// True after QmKillXp_Init() armed the module. Cheap cached read; also used by the
// probe loop to decide whether to install the global ProcessEvent net-hook.
bool QmKillXp_ReconArmed();

// Cheap per-ProcessEvent-call probe, called from the global net-hook for every
// dispatch. No-op until armed. Detects the OnPawnEnemyDead kill signal (per-UFunction
// memoized name verdict, so name resolution runs ONCE per distinct function) and -
// when the on-kill config is armed - fires the seed-free XP grant for the killed
// pawn's per-enemy amount. Also drives the one-shot manual test grant. Game-thread
// only (ProcessEvent dispatches in-thread). SEH-guarded internally.
void QmKillXp_OnProcessEvent(QmUE::UObject* self, QmUE::UFunction* func, void* parms);

// Pins a G5a-validated LOCAL PlayerState into owner@+0xC8 of the given scenario-task
// buffer and returns it (nullptr while not fully in-world). The G5a gate reads only
// base-class fields, so this works for ANY R5ScenarioTask-derived clone (AddExp,
// AddReward, ...). Shares this module's validated-owner cache: cheap re-validate on
// the fast path, full GObjects scan only when the cache is stale. Game-thread only.
QmUE::UObject* QmKillXp_PinGrantableOwner(void* taskBuf, bool verbose);

// Fire one seed-free XP grant (the proven AddExp construct path) for an arbitrary
// amount - the mod tab's "Add XP" button. Independent of the module's kill-recon
// arming. The engine gate is exp > 0, so this is add-only; amount <= 0 returns false.
// Returns true iff Execute fired on a fully-gated task (false while not fully
// in-world, or while another grant is mid-flight). Game-thread only.
bool QmKillXp_GrantXp(int32_t amount);
