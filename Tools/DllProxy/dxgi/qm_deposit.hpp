// Quartermaster "Camp Deposit" recon - capture the DepositSimilar activation path.
// -------------------------------------------------------------------------------
// PARKED / UNWIRED (2026-06): camp-wide deposit was proven unreachable via every
// reflected path AND does not augment the user's actual deposit verb (Stack-All).
// All call sites into this module (main.cpp Init, the PE net-hook, the mod-tab
// quick_deposit button) have been removed - nothing calls these functions anymore;
// the translation unit still compiles but is inert (the linker strips it via /OPT:REF).
// Kept on disk as the full record of the investigation. Re-entry plan + the three
// hard negative findings live in Docs/PLAN-CampWideDeposit-WIP.md.
// -------------------------------------------------------------------------------
// Goal (feasibility-confirmed, see chat/PLAN): a "mass quick deposit" feature like
// the Nexus Camp-Deposit mod is reachable via our reflected ProcessEvent layer -
// storage are real AR5LootableInventoryBox actors, inventory runs through reflected
// VMs, and a vanilla "Deposit Similar" action exists as the reflected ability
// UR5Ability_InteractOption_DepositSimilar. The one open unknown is HOW that ability
// is activated at runtime (it is a GameplayAbility, not a one-shot UFunction call).
//
// This module answers that with a single in-game test: when armed it rides the
// global ProcessEvent net-hook and logs every UFunction dispatch whose owning class
// OR function name is inventory/storage/deposit-related, with a best-effort arg dump
// for the strong (deposit/transfer/item-move) calls. A manual "Deposit Similar" at a
// chest then reveals the exact call sequence + receivers + arguments to replicate.
//
// Trigger (opt-in via a sentinel next to dxgi.dll; no sentinel = zero cost - the
// module is not even consulted from the net-hook):
//   qm_deposit*.txt : arms the recon (qm_deposit_recon.txt for manual/dev use, or a
//                     profile-bound qm_deposit_<profile>.txt convention later).
//
// RECON ONLY: writes nothing, mutates no game state, always forwards the original
// dispatch. SEH-guarded throughout.

#pragma once

#include "qm_ue.hpp"

// Arm the module from any qm_deposit*.txt sentinel in the Quartermaster sidecar
// folder. Result cached. Call once at startup (off DllMain) so the armed state is
// logged and ReconArmed() is warm before the probe loop decides whether to install
// the global ProcessEvent net-hook.
bool QmDeposit_Init();

// True after QmDeposit_Init() armed the module. Cheap cached read; the probe loop
// uses it (alongside the other modules) to decide whether the global ProcessEvent
// net-hook is needed.
bool QmDeposit_ReconArmed();

// Cheap per-ProcessEvent-call probe, called from the global net-hook for every
// dispatch. No-op until armed. Two jobs: (1) polls the in-context quick-deposit hotkey
// (throttled GetAsyncKeyState on the game thread) and fires QuickDeposit on the rising
// edge - this is the working trigger, since MoveAll needs a bound inventory VM that only
// exists while a storage screen is open (the QM menu closes the chest). (2) per-class +
// per-UFunction memoized recon that logs deposit/inventory dispatches (rate-limited) and
// dumps args for strong matches. Never alters dispatch. Game-thread only. SEH-guarded.
void QmDeposit_OnProcessEvent(QmUE::UObject* self, QmUE::UFunction* func, void* parms);

// ---- V1 active "Quick Deposit (Similar)" action ---------------------------------
// Drives the reflected R5DefaultInventoryVM::MoveAll(containerTag, bOnlyStack=true) -
// the same "deposit similar" verb the inventory screen's Move-All button uses - for
// every UI inventory container the live inventory VM(s) currently hold (read from the
// VM's UIInventoryContainers map). Camp-wide coverage relies on the game's own
// building-center storage aggregation: when a camp chest is open the bound VM exposes
// the aggregate containers, so one pass deposits to the whole camp.
//
// Self-verifying + safe by construction:
//   - Each candidate tag is gated by the game's own CanMoveAll() check, so a bogus/
//     own-inventory tag (or a stale sparse-array slot) is simply skipped.
//   - MoveAll into storage is non-destructive (items remain retrievable).
//   - On no live VM / empty containers it writes nothing and logs what it saw - which
//     is exactly the datum that tells us whether the VM is reachable from the QM tab.
// Game-thread only (called from the mod-tab button dispatch). SEH-guarded throughout.
void QmDeposit_QuickDeposit();

// ---- camp-wide retarget recon (read-only) ---------------------------------------
// MoveAll deposits the player panes into the *open* chest only (confirmed: items land
// in one chest). To go camp-wide we must retarget the deposit per neighbour chest, and
// that hinges on the live view topology we have never yet observed: how the bound VM's
// HandledInventories TSet<UR5BLInventoryView*> (the implicit deposit target of the open
// chest) maps onto the building-center aggregator's InventoryViews[] (all camp chests).
//
// This scan decodes, read-only: the player's UR5ProximityStorageComponent.PlayerInventoryView,
// the UR5BuildingCenterStorageComponent.Inventories.InventoryViews[] (every camp chest
// view), and each live VM's HandledInventories set - then cross-references them so the log
// shows which handled view is the player, which is the open chest, and the full list of
// chest views to retarget over. Mutates nothing; the actual swap is built once this map is
// confirmed. Game-thread only, SEH-guarded.
void QmDeposit_CampScan();

// ---- swap-proof: does overwriting the HandledInventories target retarget MoveAll? ----
// The decisive experiment before any camp-wide build. Hypothesis: MoveAll deposits the
// player pane into the NON-source UR5BLInventoryView* held in the bound VM's
// HandledInventories TSet (i.e. the open chest). This enumerates the live VMs, identifies
// the player source view (the one shared across VMs) and the active VM's open-chest slot,
// collects other chest views as retarget candidates, then for each candidate: overwrites
// that one slot, asks the game's own CanMoveAll, and restores - read-only in effect. For
// the first candidate the game accepts it does the one real write: swap slot -> fire
// MoveAll(similar) -> restore immediately. Single-threaded (game thread, inside the PE
// hook), the deposit ProcessEvent is synchronous, so no other code observes the swapped
// state. Non-destructive (items stay retrievable). SEH-guarded throughout. If items land
// in the candidate chest the reflected retarget works (-> V2 loops it over all camp
// chests); if they stay in the open chest, MoveAll ignores the slot (-> pivot native/GAS).
void QmDeposit_SwapProof();

// ---- native camp-wide deposit (getter MinHook + body-caller capture) ------------
// The radial "Deposit Similar" transfer is NATIVE: across a whole session no
// DepositSimilar/MoveAll/Transfer UFunction ever crosses the ProcessEvent net-hook;
// only the post-deposit OnInventoryViewChanged/OnStorageComponentChanged notifications
// bubble up. So camp-wide deposit must augment the native path the way the (hash-matched,
// build-identical) reference mod does. Decoded from its known-build table (row client
// 2a4f36e9 = OUR build):
//   site1 (body)  RVA 0x08b08a6b : E9 30 16 00 00  jmp +0x1630 -> dispatcher F @ 0x08b0A0A0
//   site2 (gettr) RVA 0x08b0a0b8 : E8 E3 AD BB 00  call -> getter @ 0x096c4ea0
//   site3 (gettr entry) RVA 0x096c4ea0 : mov rax,[rcx+0x3E8]; ret  (the deposit target getter)
// The reference retargets by intercepting the getter (object-agnostic) and re-firing F per
// neighbour chest with the staged chest returned in place of [rcx+0x3E8]. Our lean port
// does both via MinHook: hook the getter ENTRY (site3) and the dispatcher F (site1's jmp
// target) - no hand-rolled call-site patcher. Both self-validate against their exact byte
// signatures, so a game update that shifts code simply fails to arm (no crash).
//
// Active mechanism: MyBody wraps the organic deposit (origF), the getter captures the clicked
// chest, then MyBody re-fires origF once per other camp chest with that chest staged as the
// getter return - so each camp chest receives the items that stack with its contents (vanilla
// Deposit-Similar, applied camp-wide). The re-invoke only runs for a deposit that captured a
// clicked target, so a failed capture writes nothing; each pass is SEH-guarded and capped.
// Both hooks self-validate against the build's byte signatures (a shifted build fails to arm,
// no crash). Idempotent; called from the PE net-hook once UE is ready.
void QmDeposit_EnsureNativeInstalled();
