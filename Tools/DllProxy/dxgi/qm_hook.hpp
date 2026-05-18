// Quartermaster UFunction hook + UE probe loop
// --------------------------------------------
// Phase 1 of game-thread bring-up: wait for GObjects to populate, find
// R5HFSM_BuildingPanel, walk its Children for GetBuildingGroupsByCategoryTag,
// install a MinHook detour, return.
//
// Phase 2 of game-thread runtime: detour fires on every build-menu open.
// It forwards to the original UFunction, then runs the inject pipeline
// (qm_inject) and emits per-hit log lines.

#pragma once

#include <windows.h>

// Entry point: launched as a worker thread from main.cpp's WorkerThread.
// Returns 0 on successful install, 1 on timeout/failure.
DWORD WINAPI QmUeProbeThreadEntry(LPVOID lpParam);
