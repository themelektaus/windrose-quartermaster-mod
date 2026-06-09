// Quartermaster "XP for kills" - seed-free XP grant driven by enemy kills.
// ---------------------------------------------------------------------
// Outcome of the XP-grant RE for this workstream: Windrose grants XP only through
// the native scenario path R5ScenarioTask_AddExp::Execute, which PUBLISHES an
// "AddExp" command onto the BL command bus -> R5BLProgression_AddExpRule::Do_Impl
// does the authoritative TotalExp+=exp + level recompute + notification + save.
// There is NO BlueprintCallable mutator, and a raw record write proved cosmetic
// (UI level-up VFX only, not persistent). The working path is therefore to drive
// the engine's real Execute on a SYNTHETIC task built from nothing:
//
//   - byte-clone the R5ScenarioTask_AddExp CDO (always in GObjects, no POI seed)
//   - wire the gate-relevant fields: exp@+0x118, owner@+0xC8 (a PlayerState whose
//     BL-entity resolves), state@+0xC0 = 0, Outer@+0x20 = live World
//   - call the real Execute (image-base + RVA); the engine runs its genuine grant
//
// All five Execute preconditions are then satisfied; the only non-trivial one is
// G5a (the owner's BL-entity must resolve via the registry). In a multiplayer world
// several BP_R5PlayerState_C exist and only the LOCAL one passes G5a, so the owner
// is validated (cached + cheaply re-validated each grant). The result is persistent
// and indistinguishable from a real quest/POI reward. See FireConstructGrant.
//
// Triggers - each opt-in via a sentinel file next to dxgi.dll; no sentinel = zero
// cost (the PE net-hook is not even installed):
//   qm_killxp.txt                : arms the module (rides the global ProcessEvent
//                                  net-hook; kill detection + the triggers below)
//   qm_killxp_onkill.txt          : grant on every player kill. File content
//                                  (optional) = XP per kill; default 500.
//   qm_killxp_construct_grant.txt : one-shot manual test grant (rising-edge).

#define _CRT_SECURE_NO_WARNINGS
#include <windows.h>
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

#include "qm_killxp.hpp"
#include "qm_ue.hpp"
#include "qm_log.hpp"

namespace
{
    // ---- sentinel + armed state -------------------------------------------
    bool g_initDone = false;
    bool g_armed    = false;
    char g_dllDir[MAX_PATH] = { 0 };   // cached in Init for the trigger sentinels

    // Write the directory containing THIS DLL into `out` (no trailing sep).
    // Anchors on a local symbol so it resolves this module regardless of which
    // DLL shares the basename. Mirrors qm_weather.cpp's LocateDllDir.
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

    // ---- OnDamageDealt_Event param layout ---------------------------------
    // GA_Base_ApplyEffectForKill_C::OnDamageDealt_Event(parms) - fires on the
    // PLAYER's kill ability, so bIsKillDamage is a guaranteed local-player kill:
    //   AActor* TargetActor          @ 0x00
    //   float   IncomingDamage       @ 0x08
    //   float   DealtDamage          @ 0x0C
    //   float   ArmorDamageReduction @ 0x10
    //   bool    bIsKillDamage        @ 0x14
    constexpr size_t kOffDmgTargetActor = 0x00;
    constexpr size_t kOffDmgIsKill      = 0x14;
    const char* const kDmgDealtFuncName = "OnDamageDealt_Event";

    // ---- OnPawnEnemyDead param layout (the always-on scenario kill signal) -
    // Identical struct on R5ScenarioTracker_EnemiesKilledCount(_ManyClass) and
    // R5ScenarioListener_EnemyKilled / _AICharactersDead (Dumper-7 verified):
    //   APawn*             Pawn               @ 0x000  (the killed creature)
    //   FGameplayEffectSpec GameplayEffectSpec @ 0x008  (inline, 0x298 bytes)
    //   float              IncomingDamage     @ 0x2A0
    //   float              DealtDamage        @ 0x2A4
    constexpr size_t kOffEnemyDeadPawn     = 0x000;
    constexpr size_t kOffEnemyDeadIncoming = 0x2A0;
    constexpr size_t kOffEnemyDeadDealt    = 0x2A4;
    const char* const kEnemyDeadFuncName   = "OnPawnEnemyDead";

    // The killer is carried in the spec's EffectContext. FGameplayEffectSpec has
    // FGameplayEffectContextHandle @ 0x278; the handle's first 8 bytes are the
    // FGameplayEffectContext* (TSharedPtr object ptr). On the context (stock UE5
    // layout: vtable @ 0x00) the inherited base members are:
    //   TWeakObjectPtr<AActor> Instigator   @ 0x08  (first int32 = GObjects index)
    //   TWeakObjectPtr<AActor> EffectCauser @ 0x10
    // EXPERIMENTAL: these are the stock-engine offsets; logged with "?" so they're
    // verified against the known player pawn before being trusted.
    constexpr size_t kOffEnemyDeadCtxHandle = 0x008 + 0x278;   // params -> context ptr
    constexpr size_t kOffCtxInstigator      = 0x08;            // within FGameplayEffectContext
    constexpr size_t kOffCtxEffectCauser    = 0x10;

    // ---- per-UFunction memoized verdict -----------------------------------
    // Name resolution + keyword scan runs ONCE per distinct UFunction; the hot
    // path is then a pointer compare + bit test. Direct-mapped; collisions just
    // recompute (benign). Races are benign (worst case: one extra recompute).
    constexpr uint8_t KX_VALID     = 0x80;
    constexpr uint8_t KX_KILLISH   = 0x01;   // name matches a kill/death/defeat keyword
    constexpr uint8_t KX_DMGDEALT  = 0x02;   // name == OnDamageDealt_Event (has kill flag)
    constexpr uint8_t KX_ENEMYDEAD = 0x04;   // name == OnPawnEnemyDead (rich param extraction)

    struct KxFuncMemo { void* fn; volatile uint8_t verdict; volatile ULONGLONG logTick; };
    constexpr uint32_t kMemoMask = (1u << 13) - 1;   // 8192 slots
    KxFuncMemo g_memo[kMemoMask + 1] = {};

    volatile LONG g_killHits      = 0;   // total OnDamageDealt kills seen (even when log-throttled)
    volatile LONG g_killGrants    = 0;   // kills that actually fired an XP grant
    volatile LONG g_enemyDeadHits = 0;   // total OnPawnEnemyDead dispatches seen
    volatile LONG g_reconHits     = 0;   // total kill/death/defeat dispatches seen

    constexpr DWORD kKillLogThrottleMs      = 300;    // per-func, DMGDEALT kill lines
    constexpr DWORD kEnemyDeadLogThrottleMs = 250;    // per-func, OnPawnEnemyDead lines
    constexpr DWORD kReconLogThrottleMs     = 2000;   // per-func, generic kill/death recon lines

    // ---- seed-free construct grant (the feature) --------------------------
    // RE-confirmed task field offsets + the Execute entry (file/IDA offsets at the
    // preferred base 0x140000000). G5a (FUN_149818cd0 resolve -> FUN_1457d9570 check)
    // is the only non-trivial gate and depends solely on the cached owner@+0xC8, so
    // it doubles as the owner-validity test for multiplayer-robust owner selection.
    constexpr size_t kTaskCloneCap = 0x400;
    __declspec(align(16)) uint8_t g_constructGrantBuf[kTaskCloneCap] = {};

    constexpr size_t OFF_TaskExp         = 0x118;   // int32 exp
    constexpr size_t OFF_TaskHideNotif   = 0x11C;   // uint8 bHideNotification
    constexpr size_t OFF_TaskStateByte   = 0xC0;    // scenario-state byte; the state virtual returns task+0xC0 (0 -> G3 passes)
    constexpr size_t OFF_TaskOwnerCached = 0xC8;    // cached owner = PlayerState (FUN_148295840 reads it)
    constexpr size_t OFF_TaskOuter       = 0x20;    // UObject::Outer (GetContext walks it to the World)
    constexpr uintptr_t RVA_ScenarioExecute = 0x9803390;   // R5ScenarioTask_AddExp::Execute
    constexpr uintptr_t RVA_GateResolveX    = 0x9818CD0;   // FUN_149818cd0(task)   -> entity (via owner@+0xC8)
    constexpr uintptr_t RVA_GateCheckX      = 0x57D9570;   // FUN_1457d9570(entity) -> active?

    constexpr int32_t kDefaultGrantAmount = 500;

    // Shared re-entrancy guard: Execute publishes onto the command bus, which can
    // re-enter the PE net-hook that calls our triggers; this prevents a recursive
    // grant. Also caches the validated local PlayerState so the per-kill hot path
    // skips the full GObjects scan (cheap re-validate; rescan only if it goes stale).
    volatile LONG  g_grantBusy      = 0;
    QmUE::UObject* g_grantableOwner = nullptr;

    // manual one-shot test grant (qm_killxp_construct_grant.txt, rising-edge)
    volatile LONG g_constructGrantFired   = 0;
    ULONGLONG     g_lastConstructGrantTry = 0;
    constexpr DWORD kGrantRetryMs = 3000;

    // per-kill grant config (qm_killxp_onkill.txt; file content = XP per kill)
    bool      g_onKillArmed       = false;
    int32_t   g_onKillAmount      = kDefaultGrantAmount;
    ULONGLONG g_lastOnKillRefresh = 0;
    ULONGLONG g_lastKillGrantTick = 0;
    constexpr DWORD kOnKillRefreshMs     = 1500;   // re-read the sentinel/amount this often
    constexpr DWORD kKillGrantCooldownMs = 60;     // null-victim fallback: coalesce same-frame double-fires

    // Per-victim dedup for the OnPawnEnemyDead grant trigger. A single enemy death can
    // dispatch OnPawnEnemyDead on several tracker/listener objects in the same frame, all
    // carrying the same victim Pawn; without dedup that grants several times for one kill.
    // Keying on the victim pointer within a short window grants exactly once per death while
    // distinct simultaneous victims (AoE) each still grant. Lock-free ring; races are benign
    // (worst case one extra/missed grant).
    constexpr int   kVictimRingSize = 16;          // power of two
    constexpr DWORD kVictimDedupMs  = 750;         // same victim within this window = same death
    struct KxVictimSlot { void* pawn; ULONGLONG tick; };
    KxVictimSlot  g_victimRing[kVictimRingSize] = {};
    volatile LONG g_victimRingPos = 0;

    // True if this victim was already granted within the dedup window. On a first sighting it
    // records the victim (suppressing the rest of the same-death fan-out) and returns false.
    // A null victim is never recorded (caller falls back to the time cooldown).
    bool VictimGrantedRecently(void* pawn, ULONGLONG now)
    {
        if (!pawn) return false;
        for (int i = 0; i < kVictimRingSize; ++i)
            if (g_victimRing[i].pawn == pawn && (now - g_victimRing[i].tick) < kVictimDedupMs)
                return true;
        LONG pos = InterlockedIncrement(&g_victimRingPos) - 1;
        KxVictimSlot& s = g_victimRing[pos & (kVictimRingSize - 1)];
        s.pawn = pawn; s.tick = now;
        return false;
    }

    // Lowercase-copy + substring scan. UE names are PascalCase; we match
    // case-insensitively but pick keywords that avoid common false hits (e.g.
    // "killed"/"onkill" instead of bare "kill", so "Skill*" never matches).
    bool NameIsKillish(const char* name)
    {
        if (!name || !name[0]) return false;
        char lc[192];
        size_t i = 0;
        for (; name[i] && i < sizeof(lc) - 1; ++i)
        {
            char c = name[i];
            lc[i] = (c >= 'A' && c <= 'Z') ? (char)(c - 'A' + 'a') : c;
        }
        lc[i] = '\0';

        static const char* const kKeywords[] = {
            "killed", "onkill", "kill_", "enemykill", "killcount",
            "death", "dead", "defeat", "slain", "slay", "pawnenemy", "enemydead"
        };
        for (const char* k : kKeywords)
            if (strstr(lc, k)) return true;
        return false;
    }

    // Compute the verdict bits for a UFunction (resolves its name, SEH-guarded).
    uint8_t ComputeVerdict(QmUE::UFunction* func)
    {
        char fnNm[192] = { 0 };
        __try { QmUE::ResolveFNameNarrow(func->Name, fnNm, sizeof(fnNm)); }
        __except (EXCEPTION_EXECUTE_HANDLER) { return KX_VALID; }

        uint8_t v = KX_VALID;
        if (strcmp(fnNm, kDmgDealtFuncName) == 0)  v |= KX_DMGDEALT;
        if (strcmp(fnNm, kEnemyDeadFuncName) == 0) v |= KX_ENEMYDEAD;
        if (NameIsKillish(fnNm))                   v |= KX_KILLISH;
        return v;
    }

    uint8_t GetVerdict(QmUE::UFunction* func, KxFuncMemo*& slotOut)
    {
        KxFuncMemo& s = g_memo[(((uintptr_t)func) >> 4) & kMemoMask];
        slotOut = &s;
        if (s.fn == func && (s.verdict & KX_VALID))
            return s.verdict;
        uint8_t v = ComputeVerdict(func);
        s.verdict = 0;       // invalidate while publishing
        s.fn      = func;
        s.logTick = 0;
        s.verdict = v;       // publish complete verdict last
        return v;
    }

    // Best-effort "ClassName 'ObjectName'" for an object, into a caller buffer.
    void DescribeObject(QmUE::UObject* obj, char* out, size_t outSz)
    {
        out[0] = '\0';
        if (!obj) { snprintf(out, outSz, "<null>"); return; }
        char clsNm[160] = { 0 }, objNm[160] = { 0 };
        __try
        {
            QmUE::UClass* cls = obj->Class;
            if (cls) QmUE::ResolveFNameNarrow(cls->Name, clsNm, sizeof(clsNm));
            QmUE::ResolveFNameNarrow(obj->Name, objNm, sizeof(objNm));
        }
        __except (EXCEPTION_EXECUTE_HANDLER) {}
        snprintf(out, outSz, "%s'%s'", clsNm[0] ? clsNm : "?", objNm[0] ? objNm : "?");
    }

    // Resolve a TWeakObjectPtr (8 bytes: int32 ObjectIndex, int32 SerialNumber)
    // to its UObject via GObjects and describe it. Index-only resolution (no
    // serial validation), so a recycled slot could mislabel - acceptable for
    // recon, hence the "?" the caller prints. Caller is inside SEH.
    void DescribeWeakActor(const void* weakPtr, char* out, size_t outSz)
    {
        out[0] = '\0';
        int32_t idx = *reinterpret_cast<const int32_t*>(weakPtr);
        if (idx <= 0) { snprintf(out, outSz, "<none>"); return; }
        QmUE::TUObjectArray* g = QmUE::GetGObjects();
        QmUE::UObject* obj = g ? g->GetByIndex(idx) : nullptr;
        if (!obj) { snprintf(out, outSz, "<idx %d unresolved>", idx); return; }
        DescribeObject(obj, out, outSz);
    }

    // --- per-read SEH so one bad address can't abort a multi-step probe ------
    bool SafeReadPtr(const void* addr, void** out)
    {
        __try { *out = *reinterpret_cast<void* const*>(addr); return true; }
        __except (EXCEPTION_EXECUTE_HANDLER) { return false; }
    }
    bool SafeReadInt(const void* addr, int32_t* out)
    {
        __try { *out = *reinterpret_cast<const int32_t*>(addr); return true; }
        __except (EXCEPTION_EXECUTE_HANDLER) { return false; }
    }

    // Cheap "could be a live UE heap pointer" gate (8-aligned, multi-TB FMalloc
    // range). Real engine objects sit well above 4GB; a stale sub-4GB value is
    // rejected so it can't fault a deeper deref.
    bool LooksLikePtr(const void* p)
    {
        uintptr_t v = reinterpret_cast<uintptr_t>(p);
        return v >= 0x100000000ULL && v < 0x7FFFFFFFFFFFULL && (v & 0x7) == 0;
    }

    // Exact "is this a live UObject" test: a real UObject's Index field (+0x0C)
    // round-trips through GObjects (GetByIndex(idx) == p). Cheap and forgery-proof.
    bool IsLiveUObject(void* p)
    {
        if (!LooksLikePtr(p)) return false;
        int32_t idx = -1;
        if (!SafeReadInt(reinterpret_cast<uint8_t*>(p) + 0x0C, &idx)) return false;
        QmUE::TUObjectArray* g = QmUE::GetGObjects();
        if (!g || idx < 0 || idx >= g->Num()) return false;
        return g->GetByIndex(idx) == reinterpret_cast<QmUE::UObject*>(p);
    }

    // Evaluate Execute's G5a gate (FUN_149818cd0 resolve -> FUN_1457d9570 check) on a
    // task buffer whose owner@+0xC8 is already pinned. G5a is the ONLY gate a from-CDO
    // task can fail, and the RE proved it depends solely on the owner: FUN_149818cd0
    // resolves the owner's PlayerState BL-entity from the registry, FUN_1457d9570 then
    // checks the entity is active. Returns 1 = resolves+checks (grantable owner),
    // 0 = resolves but check fails, -1 = resolver null (wrong/remote PlayerState),
    // -2 = faulted. Read-only (no Execute, no record write); SEH-guarded.
    int EvalOwnerG5a(void* task)
    {
        const uintptr_t base = QmUE::GetImageBase();
        if (!base || !task) return -2;
        int r = -1;
        __try
        {
            using FnR = void* (__fastcall*)(void*);
            void* x = reinterpret_cast<FnR>(base + RVA_GateResolveX)(task);
            if (x)
            {
                using FnC = char (__fastcall*)(void*);
                r = reinterpret_cast<FnC>(base + RVA_GateCheckX)(x) ? 1 : 0;
            }
        }
        __except (EXCEPTION_EXECUTE_HANDLER) { r = -2; }
        return r;
    }

    // Multiplayer-robust owner selection. The world holds SEVERAL BP_R5PlayerState_C
    // (the local player plus remote/placeholder states) and only the LOCAL one resolves
    // a live BL-entity, so picking the first was PlayerState-roulette: a miss left G5a=0
    // and Execute skipped the grant. Here we pin each candidate onto `probe` (a task
    // buffer already wired with state+outer) and keep the first whose G5a resolves+checks.
    // Read-only (only the const gate resolver runs). Leaves the LAST-probed owner pinned,
    // so the caller MUST re-pin the returned winner. Out: total enumerated + #grantable;
    // returns the validated owner, or nullptr if none resolved (caller retries - the local
    // entity may still be registering).
    QmUE::UObject* FindGrantableOwner(void* probe, int* enumerated, int* grantable, bool verbose)
    {
        *enumerated = 0; *grantable = 0;
        QmUE::TUObjectArray* g = QmUE::GetGObjects();
        QmUE::UClass* psCls = QmUE::FindClassByName("BP_R5PlayerState_C");
        if (!g || !psCls || !probe) return nullptr;
        QmUE::UObject* best = nullptr;
        int n = g->Num();
        for (int i = 0; i < n; ++i)
        {
            QmUE::UObject* o = g->GetByIndex(i);
            if (!o || o->Class != psCls) continue;
            if (o->Flags & 0x30) continue;   // RF_ClassDefaultObject | RF_ArchetypeObject
            *reinterpret_cast<void**>(reinterpret_cast<uint8_t*>(probe) + OFF_TaskOwnerCached) = o;
            int g5a = EvalOwnerG5a(probe);
            if (verbose)
            {
                char nm[200]; DescribeObject(o, nm, sizeof(nm));
                QM_LOG_INFO("[KillXP]   owner cand#%d ps=0x%p g5a=%d %s", *enumerated, o, g5a, nm);
            }
            (*enumerated)++;
            if (g5a == 1) { (*grantable)++; if (!best) best = o; }
        }
        return best;
    }

    // THE GRANT. Byte-clones the R5ScenarioTask_AddExp CDO (always in GObjects, no POI
    // seed), wires the gate-relevant fields (exp, owner, state, Outer=World), validates
    // a grantable owner, then calls the REAL Execute so the engine runs its genuine grant
    // FROM NOTHING (points + level + notification + save, persistent). Returns true iff
    // Execute fired on a fully-gated task. The owner is the only non-trivial gate (G5a):
    // we re-validate the cached local PlayerState cheaply, rescanning only when it goes
    // stale. Re-entrancy-guarded (g_grantBusy) since Execute can re-enter the PE net-hook
    // that calls our triggers; fully SEH-guarded so a bad field fails safe (save untouched).
    bool FireConstructGrant(int32_t amount, const char* reason, bool verbose)
    {
        if (amount <= 0) return false;
        if (InterlockedCompareExchange(&g_grantBusy, 1, 0) != 0) return false;

        bool fired = false;
        uintptr_t base = QmUE::GetImageBase();

        QmUE::UClass*  taskCls = base ? QmUE::FindClassByName("R5ScenarioTask_AddExp") : nullptr;
        QmUE::UObject* cdo     = taskCls ? QmUE::GetClassDefaultObject(taskCls) : nullptr;
        if (!cdo) { InterlockedExchange(&g_grantBusy, 0); return false; }   // transient - retry

        // live World via GWorld (single OR double deref depending on build layout)
        void* world = nullptr; void* w1 = nullptr;
        if (SafeReadPtr(reinterpret_cast<void*>(base + QmUE::OFFSET_GWorld), &w1) && w1)
        {
            if (IsLiveUObject(w1)) world = w1;
            else { void* w2 = nullptr; if (SafeReadPtr(w1, &w2) && IsLiveUObject(w2)) world = w2; }
        }

        // clone size from UStruct::StructSize (cls+0x58), clamped to the buffer
        int32_t ss = 0; SafeReadInt(reinterpret_cast<uint8_t*>(taskCls) + 0x58, &ss);
        size_t sz = (ss > 0x120 && ss <= (int)kTaskCloneCap) ? (size_t)ss : 0x200;
        if (sz > kTaskCloneCap) sz = kTaskCloneCap;

        memcpy(g_constructGrantBuf, cdo, sz);
        *reinterpret_cast<int32_t*>(g_constructGrantBuf + OFF_TaskExp) = amount;
        g_constructGrantBuf[OFF_TaskHideNotif] = 0;                                  // show the XP notification
        g_constructGrantBuf[OFF_TaskStateByte] = 0;                                  // G3 state gate passes
        if (world) *reinterpret_cast<void**>(g_constructGrantBuf + OFF_TaskOuter) = world;

        // OWNER (G2/G4 + the decisive G5a). Fast path: re-validate the cached local
        // PlayerState (one gate call, no scan/log). Slow path: full scan + cache.
        QmUE::UObject* ps = nullptr;
        if (g_grantableOwner)
        {
            *reinterpret_cast<void**>(g_constructGrantBuf + OFF_TaskOwnerCached) = g_grantableOwner;
            if (EvalOwnerG5a(g_constructGrantBuf) == 1) ps = g_grantableOwner;
            else g_grantableOwner = nullptr;   // stale - rescan below
        }
        int psEnum = 0, psGrantable = 0;
        if (!ps)
        {
            ps = FindGrantableOwner(g_constructGrantBuf, &psEnum, &psGrantable, verbose);
            g_grantableOwner = ps;
        }
        if (!ps)
        {
            if (verbose)
                QM_LOG_INFO("[KillXP] GRANT(%s) no grantable PlayerState yet (%d enumerated) - "
                            "load fully into the world; local entity may still be registering",
                            reason, psEnum);
            InterlockedExchange(&g_grantBusy, 0);
            return false;
        }
        *reinterpret_cast<void**>(g_constructGrantBuf + OFF_TaskOwnerCached) = ps;

        // fire the REAL Execute on the synthetic task; SEH so a bad field fails safe
        __try
        {
            using ExecFn = void(__fastcall*)(void*);
            reinterpret_cast<ExecFn>(base + RVA_ScenarioExecute)(g_constructGrantBuf);
            fired = true;
        }
        __except (EXCEPTION_EXECUTE_HANDLER) { fired = false; }

        if (verbose)
        {
            char psd[200]; DescribeObject(ps, psd, sizeof(psd));
            if (fired)
                QM_LOG_INFO("[KillXP] *** GRANT(%s) *** +%d XP fired on synthetic from-CDO task "
                            "(seed-free, persistent via engine) owner=%s. Verify in-game.",
                            reason, amount, psd);
            else
                QM_LOG_INFO("[KillXP] *** GRANT(%s) FAULTED *** Execute raised on the synthetic task "
                            "- save untouched. owner=%s", reason, psd);
        }

        InterlockedExchange(&g_grantBusy, 0);
        return fired;
    }

    // One-shot manual test grant gated by qm_killxp_construct_grant.txt (rising-edge):
    // fires a single verbose +kDefaultGrantAmount grant the first time the sentinel is
    // seen. Consumes the rising edge only on success, so it auto-fires once the player +
    // CDO are live. Throttled so a not-yet-ready retry stays cheap.
    void TryConstructGrant()
    {
        if (!g_dllDir[0]) return;
        ULONGLONG now = GetTickCount64();
        if (g_lastConstructGrantTry != 0 && (now - g_lastConstructGrantTry) < kGrantRetryMs) return;
        g_lastConstructGrantTry = now;

        char path[MAX_PATH];
        snprintf(path, sizeof(path), "%s\\qm_killxp_construct_grant.txt", g_dllDir);
        DWORD attr = GetFileAttributesA(path);
        bool armed = (attr != INVALID_FILE_ATTRIBUTES) && !(attr & FILE_ATTRIBUTE_DIRECTORY);
        if (!armed)                { g_constructGrantFired = 0; return; }
        if (g_constructGrantFired) { return; }

        if (FireConstructGrant(kDefaultGrantAmount, "sentinel", true))
            g_constructGrantFired = 1;   // consume the rising edge only once it actually fired
    }

    // Throttled re-read of qm_killxp_onkill.txt: presence = per-kill grant armed; file
    // content (optional) = XP per kill (default kDefaultGrantAmount). Logs only on a
    // state/amount change so editing the file live is visible without spamming.
    void RefreshOnKillConfig()
    {
        if (!g_dllDir[0]) return;
        ULONGLONG now = GetTickCount64();
        if (g_lastOnKillRefresh != 0 && (now - g_lastOnKillRefresh) < kOnKillRefreshMs) return;
        g_lastOnKillRefresh = now;

        char path[MAX_PATH];
        snprintf(path, sizeof(path), "%s\\qm_killxp_onkill.txt", g_dllDir);
        DWORD attr = GetFileAttributesA(path);
        bool armed = (attr != INVALID_FILE_ATTRIBUTES) && !(attr & FILE_ATTRIBUTE_DIRECTORY);

        int32_t amount = kDefaultGrantAmount;
        if (armed)
        {
            HANDLE h = CreateFileA(path, GENERIC_READ,
                                   FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE,
                                   nullptr, OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, nullptr);
            if (h != INVALID_HANDLE_VALUE)
            {
                char buf[32] = { 0 }; DWORD rd = 0;
                if (ReadFile(h, buf, sizeof(buf) - 1, &rd, nullptr) && rd > 0)
                {
                    buf[rd] = '\0';
                    long v = strtol(buf, nullptr, 10);
                    if (v > 0 && v <= 1000000) amount = (int32_t)v;   // empty/garbage -> default
                }
                CloseHandle(h);
            }
        }

        if (armed != g_onKillArmed || amount != g_onKillAmount)
            QM_LOG_INFO("[KillXP] on-kill grant %s (%d XP/kill)%s",
                        armed ? "ARMED" : "disabled", amount,
                        armed ? "" : " - create qm_killxp_onkill.txt to enable");
        g_onKillArmed  = armed;
        g_onKillAmount = amount;
    }
}

bool QmKillXp_Init()
{
    if (g_initDone) return g_armed;
    g_initDone = true;

    char dir[MAX_PATH];
    if (!LocateDllDir(dir, sizeof(dir)))
    {
        QM_LOG_WARN("[KillXP] could not locate DLL dir - recon disabled");
        g_armed = false;
        return false;
    }

    snprintf(g_dllDir, sizeof(g_dllDir), "%s", dir);   // cache for the trigger sentinels

    char path[MAX_PATH];
    snprintf(path, sizeof(path), "%s\\qm_killxp.txt", dir);
    DWORD attr = GetFileAttributesA(path);
    g_armed = (attr != INVALID_FILE_ATTRIBUTES) && !(attr & FILE_ATTRIBUTE_DIRECTORY);

    if (g_armed)
        QM_LOG_INFO("[KillXP] *** ARMED *** sentinel found (%s) - kill detection + seed-free XP "
                    "grant active (rides the global ProcessEvent net-hook). Add qm_killxp_onkill.txt "
                    "to grant XP on every kill.", path);
    else
        QM_LOG_INFO("[KillXP] no sentinel (%s) - idle (zero cost)", path);
    return g_armed;
}

bool QmKillXp_ReconArmed()
{
    if (!g_initDone) QmKillXp_Init();
    return g_armed;
}

void QmKillXp_OnProcessEvent(QmUE::UObject* self, QmUE::UFunction* func, void* parms)
{
    if (!g_armed || !func) return;

    __try
    {
        // Throttled config refresh (re-read qm_killxp_onkill.txt) + the one-shot manual
        // test grant (qm_killxp_construct_grant.txt). Both cheap when idle.
        RefreshOnKillConfig();
        TryConstructGrant();

        KxFuncMemo* slot = nullptr;
        uint8_t v = GetVerdict(func, slot);
        if (!(v & (KX_KILLISH | KX_DMGDEALT | KX_ENEMYDEAD)) || !slot) return;

        ULONGLONG now = GetTickCount64();

        // --- OnDamageDealt_Event with kill flag: observability only -----------
        // This was the original grant trigger ("fires on the player's kill ability, so
        // bIsKillDamage = guaranteed local kill"), but it proved ability/talent-dependent
        // and never dispatches for the current build. The grant therefore rides
        // OnPawnEnemyDead below (the reliable always-on kill signal). Kept as a pure log so
        // we still SEE this path if a build ever does dispatch it - and could re-promote it.
        if ((v & KX_DMGDEALT) && parms)
        {
            uint8_t isKill = *reinterpret_cast<uint8_t*>(
                reinterpret_cast<uint8_t*>(parms) + kOffDmgIsKill);
            if (isKill)
            {
                LONG n = InterlockedIncrement(&g_killHits);
                if (slot->logTick == 0 || (now - slot->logTick) >= kKillLogThrottleMs)
                {
                    slot->logTick = now;
                    QmUE::UObject* target = *reinterpret_cast<QmUE::UObject**>(
                        reinterpret_cast<uint8_t*>(parms) + kOffDmgTargetActor);
                    char tgt[352], slf[352];
                    DescribeObject(target, tgt, sizeof(tgt));
                    DescribeObject(self,   slf, sizeof(slf));
                    QM_LOG_INFO("[KillXP] DMG-KILL #%ld via OnDamageDealt_Event  target=%s  ability=%s "
                                "(observability; grant rides OnPawnEnemyDead)", n, tgt, slf);
                }
            }
            return;   // OnDamageDealt is handled; don't also recon-log it
        }

        // --- Always-on kill signal AND the grant trigger: OnPawnEnemyDead -----
        // The reliable per-enemy-death dispatch (the OnDamageDealt path above is talent-
        // dependent and never fires). It arrives on the scenario EnemiesKilled tracker, so
        // it is player/team-scoped by design. The grant runs on EVERY dispatch (the log is
        // throttled, the grant is not); a per-victim dedup coalesces the multi-listener
        // fan-out of one death into a single grant.
        if ((v & KX_ENEMYDEAD) && parms)
        {
            LONG n = InterlockedIncrement(&g_enemyDeadHits);
            uint8_t* p = reinterpret_cast<uint8_t*>(parms);
            QmUE::UObject* victimObj = *reinterpret_cast<QmUE::UObject**>(p + kOffEnemyDeadPawn);

            bool granted = false;
            if (g_onKillArmed)
            {
                // Per-victim dedup (multi-listener fan-out). A null/unreadable victim falls
                // back to the short time cooldown so we still cannot over-grant.
                bool dup = victimObj
                    ? VictimGrantedRecently(victimObj, now)
                    : !(g_lastKillGrantTick == 0 || (now - g_lastKillGrantTick) >= kKillGrantCooldownMs);
                if (!dup)
                {
                    if (!victimObj) g_lastKillGrantTick = now;
                    granted = FireConstructGrant(g_onKillAmount, "kill", false);
                    if (granted) InterlockedIncrement(&g_killGrants);
                }
            }

            if (slot->logTick == 0 || (now - slot->logTick) >= kEnemyDeadLogThrottleMs)
            {
                slot->logTick = now;
                float incoming = *reinterpret_cast<float*>(p + kOffEnemyDeadIncoming);
                float dealt    = *reinterpret_cast<float*>(p + kOffEnemyDeadDealt);

                char victim[352], slf[352];
                DescribeObject(victimObj, victim, sizeof(victim));
                DescribeObject(self,      slf,    sizeof(slf));

                // Killer via GAS context (offsets experimental -> "?"). Guard the
                // context ptr deref; a null/garbage handle yields "<none>".
                char causer[352] = "<n/a>", instig[352] = "<n/a>";
                void* ctx = *reinterpret_cast<void**>(p + kOffEnemyDeadCtxHandle);
                if (ctx)
                {
                    uint8_t* c = reinterpret_cast<uint8_t*>(ctx);
                    DescribeWeakActor(c + kOffCtxEffectCauser, causer, sizeof(causer));
                    DescribeWeakActor(c + kOffCtxInstigator,   instig, sizeof(instig));
                }

                if (g_onKillArmed)
                    QM_LOG_INFO("[KillXP] KILL #%ld  victim=%s  dealt=%.1f incoming=%.1f -> +%d XP "
                                "(granted=%d, grants=%ld)  causer?=%s",
                        n, victim, dealt, incoming, g_onKillAmount, (int)granted, g_killGrants, causer);
                else
                    QM_LOG_INFO("[KillXP] ENEMY-DEAD #%ld  victim=%s  dealt=%.1f incoming=%.1f  "
                                "causer?=%s  instigator?=%s  on %s  (arm qm_killxp_onkill.txt to grant)",
                        n, victim, dealt, incoming, causer, instig, slf);
            }
            return;   // handled; don't also generic-recon-log it
        }

        // --- Generic recon: any kill/death/defeat-shaped dispatch -------------
        if (v & KX_KILLISH)
        {
            LONG n = InterlockedIncrement(&g_reconHits);
            if (slot->logTick == 0 || (now - slot->logTick) >= kReconLogThrottleMs)
            {
                slot->logTick = now;
                char fnNm[192] = { 0 }, slf[352];
                QmUE::ResolveFNameNarrow(func->Name, fnNm, sizeof(fnNm));
                DescribeObject(self, slf, sizeof(slf));
                QM_LOG_INFO("[KillXP] recon#%ld  %s  on %s", n, fnNm[0] ? fnNm : "?", slf);
            }
        }
    }
    __except (EXCEPTION_EXECUTE_HANDLER) {}
}
