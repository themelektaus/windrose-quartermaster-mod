// Quartermaster weather control - PoC stage 1 impl. See qm_weather.hpp.

#define _CRT_SECURE_NO_WARNINGS
#include <windows.h>
#include <stdint.h>
#include <stdio.h>
#include <string.h>

#include "qm_ue.hpp"
#include "qm_log.hpp"

namespace
{
    // Write the directory containing this DLL into `out` (no trailing sep).
    // Mirrors qm_config.cpp's LocateConfigDir (which is TU-local there); kept
    // self-contained so the sentinel is located like the qm_items_*.json files
    // without exposing qm_config internals. Anchors on a local symbol so it
    // resolves THIS module, not whichever DLL happens to share the basename.
    bool LocateDllDir(char* out, size_t outSz)
    {
        if (!out || outSz == 0) return false;
        HMODULE self = nullptr;
        if (!GetModuleHandleExA(
                GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS | GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT,
                reinterpret_cast<LPCSTR>(&LocateDllDir), &self) || !self)
            return false;

        char dllPath[MAX_PATH];
        DWORD n = GetModuleFileNameA(self, dllPath, sizeof(dllPath));
        if (n == 0 || n >= sizeof(dllPath)) return false;

        char* lastSep = strrchr(dllPath, '\\');
        if (!lastSep) return false;
        *lastSep = '\0';

        size_t dlen = strlen(dllPath);
        if (dlen + 1 > outSz) return false;
        memcpy(out, dllPath, dlen + 1);
        return true;
    }

    // R5N_WeatherComponent property offsets (Dumper-7 R5Weather_classes.hpp):
    //   int8 CurrentWeatherID @ 0x120 (Net, RepNotify) - read-back only
    //   int8 CheatWeatherID   @ 0x122 (Net)            - the write lever
    constexpr size_t kOffCurrentWeatherID = 0x120;
    constexpr size_t kOffCheatWeatherID   = 0x122;

    constexpr int   kWeatherMin      = 0;
    constexpr int   kWeatherMax      = 13;
    constexpr DWORD kBeatIntervalMs  = 3000;

    // The one EventReceived phase that marks the actual consume (each use also
    // emits CanFinishAbility / Cmd.Interrupt phases which must NOT re-trigger).
    const char* const kSpendConsumableTag = "GAS.Consumable.SpendConsumable";

    // Reference-mod weather table (id -> name), log readability only.
    const char* WeatherName(int id)
    {
        static const char* kNames[] = {
            "Sunny", "Cloudy", "Fog", "Mist", "Rain", "RainHeavy", "Storm",
            "Windy", "HighPressure", "Rainbow", "Overcast", "AshlandsFog",
            "TortugaMist", "Default"
        };
        if (id < kWeatherMin || id > kWeatherMax) return "<?>";
        return kNames[id];
    }

    bool           g_enabled      = false;     // heartbeat pin active (pin file OR a fired trigger)
    int            g_weatherId    = -1;         // the id the heartbeat pins
    DWORD          g_lastBeatTick = 0;
    long           g_beatCount    = 0;
    int            g_lastApplied  = -2;        // last value we wrote (anti-spam)
    QmUE::UObject* g_cachedComp   = nullptr;

    // ---- "Set once" trigger semantics (no permanent spam) ------------------
    // A consumable trigger now applies the target weather ONCE and then stops
    // touching it, so the game's own weather system can move on naturally from
    // there (user-chosen behaviour 2026-06-06: "einmal setzen, und wenn es sich
    // durch das Spiel wieder aendert, passt das"). The permanent heartbeat pin
    // is reserved for the explicit qm_weather.txt test lever only. If the live
    // R5N_WeatherComponent is not reachable at the trigger moment, a BOUNDED
    // retry heartbeat keeps trying until the first write lands (or the window
    // expires), then disarms - so there is never an open-ended write loop.
    bool           g_permanentPin      = false;    // true ONLY for qm_weather.txt
    bool           g_applyOnceArmed     = false;    // bounded retry until first write lands
    ULONGLONG      g_applyOnceDeadline  = 0;        // GetTickCount64 deadline for that retry
    constexpr DWORD kApplyOnceWindowMs  = 15000;    // grace for the live comp to appear

    // ---- Consumable-use trigger config (qm_weather_trigger.txt) -------------
    // One or more lines of "<substring> <weatherId>". Each maps a ConsumableData
    // name substring to a weather id. The GUI emits one line per DISTINCT weather
    // picked across all Weather Whistle items (the clone name carries the weather
    // suffix, e.g. DA_ConsumableAbilityData_QmWeatherWhistle_Storm -> matched by
    // the substring "QmWeatherWhistle_Storm"). Blank lines and lines starting
    // with '#' are ignored. Backward compatible with the single-line PoC file.
    struct WeatherTrigger { char substr[128]; int weatherId; };
    constexpr int  kMaxTriggers       = 32;
    WeatherTrigger g_triggers[kMaxTriggers] = {};
    int            g_triggerCount     = 0;
    bool           g_triggerArmed     = false;     // true iff g_triggerCount > 0
    ULONGLONG      g_lastTriggerTick  = 0;         // GetTickCount64 of last fire (debounce)
    char           g_lastTriggerName[160] = {};    // ConsumableData name of last fire (name-keyed debounce)
    // A single use of one consumable fires several hook points (spend EventReceived,
    // then OnMontageEnd + FinishAbility at the montage end), all carrying the SAME
    // ConsumableData name within a few seconds. We collapse those to ONE weather set.
    // The window only ever blocks RE-USE OF THE SAME item (idempotent, harmless); a
    // DIFFERENT item (different weather -> different clone name) is NEVER debounced,
    // so back-to-back Storm->Sunny always both fire. (Bug before 2026-06-07: this was
    // a single global timer, so the 2nd of any two whistles within 1.5s was dropped.)
    constexpr DWORD kSameItemDebounceMs = 2500;

    // ---- EObjectFlags (UE5) used to reject non-live objects ----
    //   RF_ClassDefaultObject 0x10 - the CDO (named "Default__...")
    //   RF_ArchetypeObject    0x20 - archetype / template objects
    // Stage-1 PoC latched onto a NON-live R5N_WeatherComponent in the
    // persistent/CDO region (addr 0x00007FF4...., CurrentWeatherID frozen at 0
    // across every beat). That object is the default-subobject TEMPLATE living
    // inside the R5NatureLogicActor class-default object - its name is NOT
    // "Default__..." so the old name-only filter let it through. Templates never
    // tick, so the write had no effect. We now reject:
    //   (a) the component itself if it is a CDO/archetype, and
    //   (b) any component whose owning actor (Outer) is a CDO/archetype - that
    //       is exactly the template-inside-the-actor-CDO case.
    // The surviving match is the runtime-spawned, live component (heap addr).
    constexpr uint32_t RF_ClassDefaultObject = 0x00000010;
    constexpr uint32_t RF_ArchetypeObject    = 0x00000020;

    bool IsCdoOrArchetype(const QmUE::UObject* o)
    {
        return o && (o->Flags & (RF_ClassDefaultObject | RF_ArchetypeObject)) != 0;
    }

    // Iterate GObjects for the LIVE R5N_WeatherComponent: match class name, then
    // reject CDO/archetype + template-inside-actor-CDO via EObjectFlags. Read-only;
    // game-thread safe. Mirrors the FindObjectByClassAndName pattern in qm_ue.cpp.
    QmUE::UObject* FindLiveWeatherComponent()
    {
        if (!QmUE::IsReady()) return nullptr;
        QmUE::TUObjectArray* arr = QmUE::GetGObjects();
        const QmUE::int32 total = arr->Num();
        int seen = 0, skippedSelf = 0, skippedTemplate = 0;
        char clsBuf[128];
        for (QmUE::int32 i = 0; i < total; ++i)
        {
            QmUE::UObject* obj = arr->GetByIndex(i);
            if (!obj || !obj->Class) continue;
            if (!QmUE::ResolveFNameNarrow(obj->Class->Name, clsBuf, sizeof(clsBuf))) continue;
            if (strcmp(clsBuf, "R5N_WeatherComponent") != 0) continue;
            ++seen;

            // (a) reject the component CDO / archetype itself
            if (IsCdoOrArchetype(obj)) { ++skippedSelf; continue; }

            // (b) reject the default-subobject template (Outer is an actor CDO/archetype)
            QmUE::UObject* owner = obj->Outer;
            if (!owner || IsCdoOrArchetype(owner)) { ++skippedTemplate; continue; }

            // Surviving object is the runtime-spawned, live component.
            char ownerCls[128] = { 0 };
            if (owner->Class)
                QmUE::ResolveFNameNarrow(owner->Class->Name, ownerCls, sizeof(ownerCls));
            QM_LOG_INFO("[Weather] LIVE component @ 0x%p owner='%s' flags=0x%X (seen=%d skippedSelf=%d skippedTpl=%d)",
                obj, ownerCls, obj->Flags, seen, skippedSelf, skippedTemplate);
            return obj;
        }
        if (seen > 0)
            QM_LOG_TRACE("[Weather] %d R5N_WeatherComponent(s) found but all CDO/template (skippedSelf=%d skippedTpl=%d) - live one not spawned yet",
                seen, skippedSelf, skippedTemplate);
        return nullptr;
    }

    // Best-effort: is `comp` still a valid R5N_WeatherComponent? Cheap re-check
    // so we don't rescan all of GObjects every beat once we have a hit.
    bool CachedCompStillValid(QmUE::UObject* comp)
    {
        if (!comp) return false;
        __try
        {
            char clsBuf[128];
            return comp->Class
                && QmUE::ResolveFNameNarrow(comp->Class->Name, clsBuf, sizeof(clsBuf))
                && strcmp(clsBuf, "R5N_WeatherComponent") == 0;
        }
        __except (EXCEPTION_EXECUTE_HANDLER) { return false; }
    }

    // Cached-or-find the live component. Updates g_cachedComp. Read-only scan,
    // game-thread safe. (FindLiveWeatherComponent logs the LIVE line on a hit.)
    QmUE::UObject* ResolveWeatherComp()
    {
        QmUE::UObject* comp = g_cachedComp;
        if (!CachedCompStillValid(comp))
        {
            comp = FindLiveWeatherComponent();
            g_cachedComp = comp;
        }
        return comp;
    }

    // SEH-guarded raw write of CheatWeatherID, capturing the before-values for
    // logging. Shared by the heartbeat (pin) and the consumable trigger. Returns
    // false on a fault (caller drops the cache). `comp` must be non-null.
    bool WriteCheatWeather(QmUE::UObject* comp, int id, int8_t* outCur, int8_t* outCheat)
    {
        if (!comp) return false;
        __try
        {
            uint8_t* base = reinterpret_cast<uint8_t*>(comp);
            if (outCur)   *outCur   = *reinterpret_cast<int8_t*>(base + kOffCurrentWeatherID);
            if (outCheat) *outCheat = *reinterpret_cast<int8_t*>(base + kOffCheatWeatherID);
            *reinterpret_cast<int8_t*>(base + kOffCheatWeatherID) = static_cast<int8_t>(id);
            return true;
        }
        __except (EXCEPTION_EXECUTE_HANDLER) { return false; }
    }

    // Parse one "<substring> <weatherId>" trigger file and APPEND its mappings to
    // g_triggers (respecting kMaxTriggers / the running g_triggerCount). Blank and
    // '#' comment lines are ignored. Returns the number of mappings added. Shared
    // by the per-profile glob in QmWeather_Init so every deployed profile's
    // qm_weather_<profile>.txt contributes instead of one file winning.
    int LoadTriggerFile(const char* path)
    {
        FILE* f = fopen(path, "rb");
        if (!f) return 0;

        int added = 0;
        char line[256];
        while (g_triggerCount < kMaxTriggers && fgets(line, sizeof(line), f))
        {
            const char* p = line;
            while (*p == ' ' || *p == '\t' || *p == '\r' || *p == '\n') ++p;
            if (*p == '\0' || *p == '#') continue;

            char substr[128] = { 0 };
            int  id = -1;
            if (sscanf(p, "%127s %d", substr, &id) == 2 && substr[0]
                && id >= kWeatherMin && id <= kWeatherMax)
            {
                WeatherTrigger& t = g_triggers[g_triggerCount];
                strncpy(t.substr, substr, sizeof(t.substr) - 1);
                t.substr[sizeof(t.substr) - 1] = '\0';
                t.weatherId = id;
                ++g_triggerCount;
                ++added;
                QM_LOG_INFO("[Weather] trigger[%d]: ConsumableData substring='%s' -> weather id=%d (%s)  [%s]",
                    g_triggerCount - 1, t.substr, id, WeatherName(id), path);
            }
            else
            {
                QM_LOG_WARN("[Weather] %s line ignored (expected '<substring> <0..13>'): %.80s", path, p);
            }
        }
        fclose(f);
        return added;
    }
}

bool QmWeather_Init()
{
    g_enabled          = false;
    g_weatherId        = -1;
    g_permanentPin     = false;
    g_applyOnceArmed   = false;
    g_applyOnceDeadline = 0;
    g_triggerArmed     = false;
    g_triggerCount     = 0;

    char dir[MAX_PATH];
    if (!LocateDllDir(dir, sizeof(dir)))
    {
        QM_LOG_WARN("[Weather] cannot locate DLL dir - weather module disabled");
        return false;
    }

    // ---- (1) permanent pin: qm_weather.txt (Stage 1) -----------------------
    {
        char path[MAX_PATH];
        if (snprintf(path, sizeof(path), "%s\\qm_weather.txt", dir) > 0)
        {
            FILE* f = fopen(path, "rb");
            if (f)
            {
                int id = -1;
                int got = fscanf(f, "%d", &id);
                fclose(f);
                if (got == 1 && id >= kWeatherMin && id <= kWeatherMax)
                {
                    g_weatherId   = id;
                    g_enabled     = true;
                    g_permanentPin = true;   // qm_weather.txt = explicit permanent pin
                    QM_LOG_INFO("[Weather] *** PIN ARMED *** target weather id=%d (%s) from %s",
                        id, WeatherName(id), path);
                }
                else
                {
                    QM_LOG_WARN("[Weather] qm_weather.txt present but value invalid (parsed=%d, need %d..%d) - pin disabled",
                        id, kWeatherMin, kWeatherMax);
                }
            }
            else
            {
                QM_LOG_INFO("[Weather] no qm_weather.txt - permanent pin disabled");
            }
        }
    }

    // ---- (2) consumable-use trigger: qm_weather_*.txt (per profile) ---------
    // Glob every per-profile trigger file (qm_weather_<profile>.txt) plus any
    // legacy single qm_weather_trigger.txt - it matches the same pattern - and
    // merge all mappings. Mirrors the qm_items_*.json multi-profile model so two
    // deployed Weather-Whistle profiles coexist instead of the last GUI build
    // clobbering the others. The permanent-pin qm_weather.txt has no underscore
    // after "weather" so it is NOT matched here (handled in section 1 above).
    {
        char pattern[MAX_PATH];
        if (snprintf(pattern, sizeof(pattern), "%s\\qm_weather_*.txt", dir) > 0)
        {
            WIN32_FIND_DATAA fd = {};
            HANDLE h = FindFirstFileA(pattern, &fd);
            int files = 0;
            if (h != INVALID_HANDLE_VALUE)
            {
                do
                {
                    if (fd.dwFileAttributes & FILE_ATTRIBUTE_DIRECTORY) continue;
                    char full[MAX_PATH];
                    int w = snprintf(full, sizeof(full), "%s\\%s", dir, fd.cFileName);
                    if (w <= 0 || (size_t)w >= sizeof(full)) continue;
                    LoadTriggerFile(full);
                    ++files;
                } while (g_triggerCount < kMaxTriggers && FindNextFileA(h, &fd));
                FindClose(h);
            }

            g_triggerArmed = (g_triggerCount > 0);
            if (g_triggerArmed)
                QM_LOG_INFO("[Weather] *** TRIGGER ARMED *** %d mapping(s) from %d file(s) matching %s",
                    g_triggerCount, files, pattern);
            else if (files > 0)
                QM_LOG_WARN("[Weather] %d trigger file(s) present but no valid mapping parsed - trigger disabled", files);
            else
                QM_LOG_INFO("[Weather] no qm_weather_*.txt - consumable trigger disabled");
        }
    }

    if (!g_enabled && !g_triggerArmed)
    {
        QM_LOG_INFO("[Weather] neither pin nor trigger configured - weather module idle");
        return false;
    }
    return true;
}

bool QmWeather_IsEnabled()  { return g_enabled || g_triggerArmed; }
bool QmWeather_TriggerArmed() { return g_triggerArmed; }

void QmWeather_Heartbeat()
{
    if (!g_enabled || !QmUE::IsReady()) return;

    const DWORD now = GetTickCount();
    if (g_lastBeatTick != 0 && (now - g_lastBeatTick) < kBeatIntervalMs) return;
    g_lastBeatTick = now;
    const long beat = ++g_beatCount;

    // SET-ONCE retry: this heartbeat is only armed (without a permanent pin)
    // when a trigger fired but no live component was reachable yet. If the
    // grace window elapsed without ever landing a write, stop trying.
    if (g_applyOnceArmed && !g_permanentPin && GetTickCount64() > g_applyOnceDeadline)
    {
        g_applyOnceArmed = false;
        g_enabled        = false;
        QM_LOG_WARN("[Weather] set-once window expired (no live R5N_WeatherComponent within %lums) - giving up",
            kApplyOnceWindowMs);
        return;
    }

    // Re-find the component only when the cached pointer went stale.
    const bool hadCache = CachedCompStillValid(g_cachedComp);
    QmUE::UObject* comp = ResolveWeatherComp();
    if (!hadCache)
    {
        if (comp)
            QM_LOG_INFO("[Weather] beat#%ld live R5N_WeatherComponent found @ 0x%p", beat, comp);
        else if (beat <= 10 || (beat % 20) == 0)
            QM_LOG_TRACE("[Weather] beat#%ld no live R5N_WeatherComponent yet (not in a gameplay world?)", beat);
    }
    if (!comp) return;

    int8_t curBefore = 0, cheatBefore = 0;
    if (!WriteCheatWeather(comp, g_weatherId, &curBefore, &cheatBefore))
    {
        QM_LOG_WARN("[Weather] beat#%ld fault accessing comp=0x%p - dropping cache, will re-find", beat, comp);
        g_cachedComp = nullptr;
        return;
    }

    // Log on first writes + whenever something interesting changes, then go
    // quiet. Lets the tester watch CheatWeatherID hold at the target.
    const bool interesting = (g_lastApplied != g_weatherId)
                          || (cheatBefore != static_cast<int8_t>(g_weatherId))
                          || (curBefore   != static_cast<int8_t>(g_weatherId));
    if (beat <= 6 || interesting)
    {
        QM_LOG_INFO("[Weather] beat#%ld wrote CheatWeatherID %d -> %d (%s); CurrentWeatherID=%d (comp=0x%p)",
            beat, (int)cheatBefore, g_weatherId, WeatherName(g_weatherId), (int)curBefore, comp);
    }
    g_lastApplied = g_weatherId;

    // SET-ONCE: the retry just landed its single write. Disarm the heartbeat so
    // we never spam - the game keeps weather control from here on. (Only the
    // explicit qm_weather.txt permanent pin keeps writing every beat.)
    if (g_applyOnceArmed && !g_permanentPin)
    {
        g_applyOnceArmed = false;
        g_enabled        = false;
        QM_LOG_INFO("[Weather] set-once write landed on beat#%ld - heartbeat disarmed (game keeps weather control)",
            beat);
    }
}

// Return the index of the first configured trigger whose substring is contained
// in the ConsumableData name, or -1 if none. Substring match: a clone named
// "...QmWeatherWhistle_Storm" matches the "QmWeatherWhistle_Storm" mapping.
static int MatchTrigger(const char* consumableDataName)
{
    for (int i = 0; i < g_triggerCount; ++i)
        if (g_triggers[i].substr[0] && strstr(consumableDataName, g_triggers[i].substr))
            return i;
    return -1;
}

// Name-keyed debounce. Returns true if this name fired within kSameItemDebounceMs.
// Only the SAME ConsumableData name is debounced - a different weather clone has a
// different name and so always fires immediately.
static bool TriggerDebounced(const char* consumableDataName)
{
    const ULONGLONG now = GetTickCount64();
    const bool sameItem = g_lastTriggerName[0]
        && strcmp(g_lastTriggerName, consumableDataName) == 0;
    return sameItem && g_lastTriggerTick != 0 && (now - g_lastTriggerTick) < kSameItemDebounceMs;
}

static void MarkTriggered(const char* consumableDataName)
{
    g_lastTriggerTick = GetTickCount64();
    strncpy(g_lastTriggerName, consumableDataName, sizeof(g_lastTriggerName) - 1);
    g_lastTriggerName[sizeof(g_lastTriggerName) - 1] = '\0';
}

// Shared "set once" apply: write the target weather a SINGLE time. On success
// we do NOT arm the permanent heartbeat - the game keeps weather control from
// here. Only if the live component is not reachable yet do we arm a bounded
// retry (kApplyOnceWindowMs) that disarms after the first landed write. `via`
// is a short label for the log. Returns the applied id. Game-thread only.
static int ApplyWeatherSetOnce(const char* consumableDataName, const char* matchedSubstr, int id, const char* via)
{
    g_weatherId = id;

    QmUE::UObject* comp = ResolveWeatherComp();
    int8_t curBefore = -1, cheatBefore = -1;
    const bool wrote = WriteCheatWeather(comp, id, &curBefore, &cheatBefore);
    if (wrote)
    {
        g_lastApplied    = id;
        g_applyOnceArmed = false;
        if (!g_permanentPin) g_enabled = false;   // no spam: one write and done
    }
    else
    {
        g_cachedComp        = nullptr;
        g_applyOnceArmed    = true;
        g_applyOnceDeadline = GetTickCount64() + kApplyOnceWindowMs;
        g_enabled           = true;               // bounded retry until it lands
    }

    QM_LOG_INFO("[Weather] *** TRIGGER (via %s) *** '%s' matched '%s' -> weather %d (%s) [%s; CheatWeatherID %d->%d CurrentWeatherID=%d comp=0x%p]",
        via ? via : "?", consumableDataName, matchedSubstr ? matchedSubstr : "?", id, WeatherName(id),
        wrote ? "set once - game keeps control" : "no live comp yet - bounded retry armed",
        (int)cheatBefore, id, (int)curBefore, comp);
    return id;
}

int QmWeather_TryConsumableTrigger(const char* consumableDataName, const char* eventTag)
{
    if (!g_triggerArmed) return -1;
    if (!consumableDataName || !consumableDataName[0]) return -1;

    // Only act on the actual spend phase (each use also emits CanFinishAbility /
    // Cmd.Interrupt phases - acting on those would double-fire / fire too early).
    if (!eventTag || strcmp(eventTag, kSpendConsumableTag) != 0) return -1;

    const int ti = MatchTrigger(consumableDataName);
    if (ti < 0) return -1;

    if (TriggerDebounced(consumableDataName)) return -1;
    MarkTriggered(consumableDataName);
    return ApplyWeatherSetOnce(consumableDataName, g_triggers[ti].substr, g_triggers[ti].weatherId, eventTag);
}

int QmWeather_TryConsumableTriggerOnComplete(const char* consumableDataName, const char* viaFn)
{
    if (!g_triggerArmed) return -1;
    if (!consumableDataName || !consumableDataName[0]) return -1;

    // Substring match against the configured ConsumableData token(s). This is the
    // sole discriminator on the completion path (no spend-tag gate) - vanilla
    // food/bandage completions never carry our token, so they can't false-trigger.
    // A custom whistle's Params_0 = DA_ConsumableAbilityData_QmWeatherWhistle_<W>
    // matches the configured "QmWeatherWhistle_<W>" mapping for its weather.
    const int ti = MatchTrigger(consumableDataName);
    if (ti < 0) return -1;

    // Name-keyed debounce: the spend-tag path + OnMontageEnd + FinishAbility all fire
    // for one use carrying the same name; collapse them to one weather set. A different
    // weather clone (different name) is never blocked - see kSameItemDebounceMs.
    if (TriggerDebounced(consumableDataName)) return -1;
    MarkTriggered(consumableDataName);

    return ApplyWeatherSetOnce(consumableDataName, g_triggers[ti].substr, g_triggers[ti].weatherId, viaFn);
}
