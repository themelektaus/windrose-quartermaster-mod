// Quartermaster weather control - PoC stage 1 (sentinel-driven)
// -------------------------------------------------------------
// Background: the weather recon (Docs/PLAN-WeatherControl-WIP.md) showed that
// an on-USE weather change is NOT reachable via pure pak/data modding - no
// GameplayAbility/Effect/Cue touches the weather subsystem, and authoring a new
// BPGC ability is blocked by the usmap/CDO wall. Weather is only changed by
// (a) scenario-graph tasks, (b) the weather subsystem, or (c) writing the live
// R5N_WeatherComponent::CheatWeatherID - which is exactly what the UE4SS
// "Windrose Weather Control" reference mod does. Option B (chosen) replicates
// that lever through THIS dxgi proxy instead of UE4SS.
//
// This stage DECOUPLES the one genuinely risky unknown - can a foreign-DLL
// write to the replicated CheatWeatherID actually change the weather in-game? -
// from item-use detection. It is driven by a sentinel file `qm_weather.txt`
// sitting next to dxgi.dll, containing a single integer weather id (0..13, see
// WeatherName). When present + valid, a game-thread heartbeat (ridden on the
// existing lifecycle hook) finds the live weather component and pins
// CheatWeatherID to that id. Delete the file -> the DLL stops touching weather.
//
// Once this proves the write works, stage 2 wires the "Weather Control"
// consumable's USE to a weather id instead of the sentinel.

#pragma once

// Read qm_weather.txt next to the DLL. Returns true iff a valid weather id
// (0..13) parsed and the PoC should run. Call once at startup (off DllMain).
bool QmWeather_Init();

// True after QmWeather_Init() armed either the permanent pin (qm_weather.txt)
// or the consumable-use trigger (qm_weather_trigger.txt). Used by the idle-gate.
bool QmWeather_IsEnabled();

// Game-thread heartbeat: cheap internal throttle, finds the live
// R5N_WeatherComponent (skipping the CDO) and writes CheatWeatherID. No-op
// until QmUE::IsReady() and the pin is active. MUST run on the game thread
// (we ride the lifecycle hook, which dispatches in-thread) - a raw property
// write is GC-safe, unlike the off-thread asset loads that crashed before.
void QmWeather_Heartbeat();

// ---- Stage 2b: consumable-use trigger -------------------------------------
// Reads a second sentinel `qm_weather_trigger.txt` (next to the DLL) of the
// form `<substring> <weatherId>` (e.g. `Bandages 6`). When armed, the consume
// hook calls QmWeather_TryConsumableTrigger() on every R5ConsumeAbility::
// EventReceived. If the consumed ConsumableData's name contains <substring>
// AND the event is the actual spend phase (GAS.Consumable.SpendConsumable),
// the target weather is set + pinned (reusing the proven heartbeat write path).
// This de-risks the full detection->weather-write chain using a real,
// obtainable vanilla consumable BEFORE any custom "Weather Control" item is
// authored - swapping the trigger substring for the custom item's
// ConsumableData name is all that stage 2c/3 needs.

// True if qm_weather_trigger.txt parsed a valid mapping.
bool QmWeather_TriggerArmed();

// Called from the consume hook for every EventReceived. consumableDataName is
// Params_0's object name (the R5ConsumeAbilityData, e.g.
// "DA_ConsumableAbilityData_Bandages_T01"); eventTag is the resolved
// FGameplayTag string (e.g. "GAS.Consumable.SpendConsumable"). Returns the
// weather id applied (>=0) when this call triggered a change, or -1 otherwise.
// Game-thread only (the consume hook dispatches in-thread).
int QmWeather_TryConsumableTrigger(const char* consumableDataName, const char* eventTag);

// ---- Stage 2c: spawner-whistle (completion-phase) trigger ------------------
// The boar-whistle activates GA_SpawnerConsumableAbility_C - an EMPTY, purely
// native UR5ConsumeAbility subclass (no BP ubergraph). Its entry EventReceived
// runs natively and never hits the script-VM exec thunk we detour, so the
// spend-phase trigger above never fires for it. The whistle DOES play a montage
// though, and montage-task callbacks (OnMontageEnd / FinishAbility) are bound as
// dynamic delegates -> invoked via ProcessEvent -> hookable. This entry is
// called from those completion hooks: it matches Params_0's name against the
// configured substring WITHOUT the spend-tag gate (the substring is the
// discriminator; food/bandage never carry "SpawnerBoar"), debounced so the two
// completion functions firing in one use don't double-apply. viaFn is the
// originating UFunction name (for the log). Returns the applied id or -1.
int QmWeather_TryConsumableTriggerOnComplete(const char* consumableDataName, const char* viaFn);
