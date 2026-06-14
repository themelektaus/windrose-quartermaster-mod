// Quartermaster "Camp Deposit" recon - capture the DepositSimilar activation path.
// See qm_deposit.hpp for the rationale. RECON ONLY: writes nothing, mutates no game
// state, always forwards the original dispatch. SEH-guarded throughout.

#define _CRT_SECURE_NO_WARNINGS
#include <windows.h>
#include <stdint.h>
#include <stdio.h>
#include <string.h>

#include "qm_deposit.hpp"
#include "qm_ue.hpp"
#include "qm_log.hpp"
#include "minhook/include/MinHook.h"

namespace
{
    bool          g_initDone = false;
    bool          g_armed    = false;
    volatile LONG g_logCount = 0;

    // Per distinct UFunction we log at most once per this window, so a busy scene
    // (or a per-frame VM tick on an inventory class) can't flood while a discrete
    // manual deposit still emits one fresh, timestamped line per function involved.
    constexpr ULONGLONG kReLogMs = 2000;

    // In-context quick-deposit hotkey, polled on the game thread (see PollHotkey).
    constexpr int       kHotkeyVk     = VK_INSERT;   // press while a storage screen is open
    constexpr ULONGLONG kHotkeyPollMs = 40;          // throttle the GetAsyncKeyState poll

    // Write the Quartermaster sidecar dir (<dll dir>\Quartermaster) into `out` (no
    // trailing sep). Anchors on a local symbol so it resolves this module regardless
    // of which DLL shares the basename. Mirrors qm_shanty.cpp LocateSidecarDir.
    bool LocateSidecarDir(char* out, size_t outSz)
    {
        if (!out || outSz == 0) return false;
        HMODULE self = nullptr;
        if (!GetModuleHandleExA(
                GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS | GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT,
                reinterpret_cast<LPCSTR>(&LocateSidecarDir), &self) || !self)
            return false;

        char dllPath[MAX_PATH];
        DWORD n = GetModuleFileNameA(self, dllPath, sizeof(dllPath));
        if (n == 0 || n >= sizeof(dllPath)) return false;

        char* lastSep = strrchr(dllPath, '\\');
        if (!lastSep) return false;
        *lastSep = '\0';

        int w = snprintf(out, outSz, "%s\\Quartermaster", dllPath);
        return w > 0 && (size_t)w < outSz;
    }

    // Lowercase-copy + substring scan (needle must already be lowercase).
    bool ContainsLc(const char* hay, const char* needleLc)
    {
        char lc[192];
        size_t i = 0;
        for (; hay[i] && i < sizeof(lc) - 1; ++i)
        {
            char c = hay[i];
            lc[i] = (c >= 'A' && c <= 'Z') ? (char)(c - 'A' + 'a') : c;
        }
        lc[i] = '\0';
        return strstr(lc, needleLc) != nullptr;
    }

    // ---- per-class verdict memo -------------------------------------------------
    // Class-name resolution + keyword match runs ONCE per distinct UClass; the hot
    // path is then a pointer compare + bit test. Direct-mapped; collisions just
    // recompute (benign). Mirrors qm_hook.cpp PeClassVerdict.
    constexpr uint8_t DC_VALID    = 0x80;
    constexpr uint8_t DC_INTEREST = 0x01;   // class name matches an inventory/storage/deposit keyword
    constexpr uint8_t DC_STRONG   = 0x02;   // strongly deposit-related class -> dump args on every call

    struct ClsMemo { void* cls; volatile uint8_t verdict; };
    constexpr uint32_t kClsMask = (1u << 14) - 1;   // 16384 slots
    ClsMemo g_clsMemo[kClsMask + 1] = {};

    uint8_t ComputeClassVerdict(QmUE::UClass* cls)
    {
        char nm[192] = { 0 };
        __try { if (!QmUE::ResolveFNameNarrow(cls->Name, nm, sizeof(nm)) || !nm[0]) return DC_VALID; }
        __except (EXCEPTION_EXECUTE_HANDLER) { return DC_VALID; }

        uint8_t v = DC_VALID;
        // Strong: the deposit ability itself + the interact-option ability family it
        // derives from (R5Ability_InteractOption_DepositSimilar / _Base).
        if (ContainsLc(nm, "deposit") || ContainsLc(nm, "interactoption"))
            v |= (DC_INTEREST | DC_STRONG);
        // Interesting: inventory/storage receivers that carry the transfer surface.
        else if (ContainsLc(nm, "inventory") || ContainsLc(nm, "lootable") ||
                 ContainsLc(nm, "storage")   || ContainsLc(nm, "container") ||
                 ContainsLc(nm, "warehouse") || ContainsLc(nm, "chest"))
            v |= DC_INTEREST;
        return v;
    }

    uint8_t ClassVerdict(QmUE::UClass* cls)
    {
        ClsMemo& s = g_clsMemo[(((uintptr_t)cls) >> 4) & kClsMask];
        if (s.cls == cls && (s.verdict & DC_VALID)) return s.verdict;
        uint8_t v = ComputeClassVerdict(cls);
        s.verdict = 0;     // invalidate while publishing
        s.cls     = cls;
        s.verdict = v;     // publish complete verdict last
        return v;
    }

    // ---- per-UFunction verdict + rate-limit memo --------------------------------
    constexpr uint8_t DF_VALID  = 0x80;
    constexpr uint8_t DF_STRONG = 0x01;   // func name strongly deposit/transfer-related (catch any class, dump args)
    constexpr uint8_t DF_NOTIFY = 0x02;   // storage-notification / deposit-UI fingerprint (always-on breadcrumb)

    struct FnMemo { void* fn; volatile uint8_t verdict; volatile ULONGLONG lastTick; };
    constexpr uint32_t kFnMask = (1u << 13) - 1;   // 8192 slots
    FnMemo g_fnMemo[kFnMask + 1] = {};

    uint8_t ComputeFuncVerdict(QmUE::UFunction* func)
    {
        char nm[192] = { 0 };
        __try { if (!QmUE::ResolveFNameNarrow(func->Name, nm, sizeof(nm)) || !nm[0]) return DF_VALID; }
        __except (EXCEPTION_EXECUTE_HANDLER) { return DF_VALID; }

        uint8_t v = DF_VALID;
        if (ContainsLc(nm, "deposit")   || ContainsLc(nm, "transfer")  ||
            ContainsLc(nm, "additem")   || ContainsLc(nm, "removeitem")||
            ContainsLc(nm, "moveitem")  || ContainsLc(nm, "stackitem") ||
            ContainsLc(nm, "swapitem")  || ContainsLc(nm, "store"))
            v |= DF_STRONG;
        // Storage-activity fingerprint: these fire on EVERY real deposit regardless of how it
        // was triggered (radial GAS, Stack-All button, or our re-invoke). The breadcrumb logs
        // them even when the native path is active, so a deposit is never invisible again.
        if (ContainsLc(nm, "moveall")                  || ContainsLc(nm, "stackall") ||
            ContainsLc(nm, "oninventoryviewchanged")   ||
            ContainsLc(nm, "onstoragecomponentchanged"))
            v |= DF_NOTIFY;
        return v;
    }

    // Best-effort: if `val` looks like a UObject*, describe it as "ClassName 'Name'".
    // Validates conservatively (8-aligned heap pointer -> readable Class -> resolvable,
    // printable-ascii class name). Leaves `out` empty if it doesn't validate. Each
    // deref SEH-guarded; intended for the recon arg-dump only.
    void DescribeMaybeObject(uint64_t val, char* out, size_t outSz)
    {
        out[0] = '\0';
        if (val < 0x10000 || (val & 0x7) != 0) return;
        QmUE::UObject* o = reinterpret_cast<QmUE::UObject*>(val);
        char clsNm[160] = { 0 }, objNm[160] = { 0 };
        __try
        {
            QmUE::UClass* cls = o->Class;
            if (!cls || ((uintptr_t)cls & 0x7)) return;
            if (!QmUE::ResolveFNameNarrow(cls->Name, clsNm, sizeof(clsNm)) || !clsNm[0]) return;
            QmUE::ResolveFNameNarrow(o->Name, objNm, sizeof(objNm));
        }
        __except (EXCEPTION_EXECUTE_HANDLER) { return; }

        for (const char* p = clsNm; *p; ++p)
            if ((unsigned char)*p < 0x20 || (unsigned char)*p > 0x7E) return;   // not a real class name
        snprintf(out, outSz, " -> %s'%s'", clsNm, objNm[0] ? objNm : "?");
    }

    // Dump the packed param block as qwords (bounded by ParmsSize), annotating any
    // qword that resolves to a UObject*. Reveals the receivers/arguments (source
    // inventory, target storage, item refs) of the strong deposit/transfer calls.
    void DumpParms(QmUE::UFunction* func, void* parms, long n)
    {
        if (!parms) return;
        uint16_t sz = 0;
        __try { sz = func->ParmsSize; }
        __except (EXCEPTION_EXECUTE_HANDLER) { return; }
        if (sz == 0 || sz > 0x400) return;     // sanity: skip absurd sizes

        int qwords = sz / 8;
        if (qwords > 24) qwords = 24;          // cap log volume
        for (int i = 0; i < qwords; ++i)
        {
            uint64_t val = 0;
            __try { val = *reinterpret_cast<uint64_t*>(reinterpret_cast<uint8_t*>(parms) + (size_t)i * 8); }
            __except (EXCEPTION_EXECUTE_HANDLER) { continue; }
            char objDesc[336] = { 0 };
            DescribeMaybeObject(val, objDesc, sizeof(objDesc));
            QM_LOG_INFO("[Deposit] hit#%ld     parm[+0x%02X] = 0x%016llX%s",
                n, i * 8, (unsigned long long)val, objDesc);
        }
    }

    // ---- rolling PE ring (recon: find the replayable activation/transfer event) -----------
    // The vanilla deposit reads its target chest from UR5Ability_InteractOption_Base::
    // TargetModel @ 0x3E8 (the getter the reference hooks is `mov rax,[this+0x3E8]`). The
    // reference re-fires the SAME ProcessEvent(self, func, parms) per neighbour chest with
    // 0x3E8 swapped. To replicate that through our PE net-hook we must know WHICH dispatch is
    // the deposit activation. So we keep a rolling ring of recent (class,func,self) - plus a
    // small snapshot of the param block for the interesting ones (the block is only valid
    // during the call) - and flush the entries leading up to the deposit ability when we see
    // it. That reveals the activation/transfer call (and its args) we capture + replay.
    constexpr size_t kAbilityTargetModelOff = 0x3E8;

    struct PeRingEntry { void* cls; void* func; void* self; uint16_t snapLen; uint8_t snap[48]; };
    constexpr uint32_t kRingMask     = 2047;        // 2048 entries
    PeRingEntry        g_peRing[kRingMask + 1] = {};
    volatile LONG      g_peRingHead  = 0;
    volatile ULONGLONG g_lastFlush   = 0;
    constexpr ULONGLONG kFlushCooldownMs = 1500;    // don't re-dump on every strong dispatch
    constexpr int       kRingDumpBack    = 60;      // how many recent dispatches to dump

    void FlushPeRing(LONG head)
    {
        QM_LOG_INFO("[Deposit] === ring flush: last %d ProcessEvent dispatches up to the deposit ability ===",
                    kRingDumpBack);
        for (int i = kRingDumpBack; i >= 1; --i)
        {
            LONG idx = head - i;
            if (idx < 0) continue;
            PeRingEntry e = g_peRing[(uint32_t)idx & kRingMask];
            if (!e.cls || !e.func) continue;
            char clsNm[160] = { 0 }, fnNm[160] = { 0 };
            __try
            {
                QmUE::ResolveFNameNarrow(reinterpret_cast<QmUE::UClass*>(e.cls)->Name, clsNm, sizeof(clsNm));
                QmUE::ResolveFNameNarrow(reinterpret_cast<QmUE::UFunction*>(e.func)->Name, fnNm, sizeof(fnNm));
            }
            __except (EXCEPTION_EXECUTE_HANDLER) { continue; }
            if (!clsNm[0] && !fnNm[0]) continue;
            uint8_t cv = ClassVerdict(reinterpret_cast<QmUE::UClass*>(e.cls));
            const char* flag = (cv & DC_STRONG)   ? "  <== ABILITY/deposit"
                             : (cv & DC_INTEREST) ? "  <- inventory/storage"
                                                  : "";
            QM_LOG_INFO("[Deposit]   ring[-%3d] %s::%s self=0x%p%s",
                        i, clsNm[0] ? clsNm : "?", fnNm[0] ? fnNm : "?", e.self, flag);
            // Decode the captured snapshot of this call's param block: each qword annotated if
            // it resolves to a UObject* - reveals the source/target/item refs of the call.
            for (int q = 0; q * 8 + 8 <= (int)e.snapLen; ++q)
            {
                uint64_t val = 0;
                memcpy(&val, e.snap + (size_t)q * 8, 8);
                char objDesc[336] = { 0 };
                DescribeMaybeObject(val, objDesc, sizeof(objDesc));
                QM_LOG_INFO("[Deposit]   ring[-%3d]     parm[+0x%02X] = 0x%016llX%s",
                            i, q * 8, (unsigned long long)val, objDesc);
            }
        }
    }
}

bool QmDeposit_Init()
{
    if (g_initDone) return g_armed;
    g_initDone = true;

    char dir[MAX_PATH];
    if (!LocateSidecarDir(dir, sizeof(dir)))
    {
        QM_LOG_WARN("[Deposit] could not locate DLL dir - recon disabled");
        g_armed = false;
        return false;
    }

    // Arm on ANY qm_deposit*.txt (manual qm_deposit_recon.txt or a future
    // profile-bound qm_deposit_<profile>.txt). Mirrors the shanty/weather/killxp glob.
    char pattern[MAX_PATH];
    snprintf(pattern, sizeof(pattern), "%s\\qm_deposit*.txt", dir);
    WIN32_FIND_DATAA fd;
    HANDLE h = FindFirstFileA(pattern, &fd);
    int files = 0;
    if (h != INVALID_HANDLE_VALUE)
    {
        do
        {
            if (!(fd.dwFileAttributes & FILE_ATTRIBUTE_DIRECTORY)) ++files;
        } while (FindNextFileA(h, &fd));
        FindClose(h);
    }
    g_armed = files > 0;

    if (g_armed)
        QM_LOG_INFO("[Deposit] *** ARMED *** native camp-wide deposit. Once UE is ready a MinHook installs on the "
                    "VM's native MoveAll exec - the verb the inventory 'Stack All' button triggers (the user's real "
                    "deposit path). Pressing Stack All on one open chest deposits there as usual, then the mod "
                    "re-invokes the same Deposit-Similar on every other bound camp chest (each gets the items that "
                    "stack with its contents). The GAS radial 'Deposit Similar' path stays hooked too. "
                    "%d sentinel file(s) matching %s\\qm_deposit*.txt",
                    files, dir);
    else
        QM_LOG_INFO("[Deposit] no qm_deposit*.txt - idle (zero cost)");
    return g_armed;
}

bool QmDeposit_ReconArmed()
{
    if (!g_initDone) QmDeposit_Init();
    return g_armed;
}

// ---- in-context quick-deposit hotkey --------------------------------------------
// The reflected MoveAll verb needs a *bound* inventory VM, which only exists while a
// storage screen is open - and the QM settings tab is a separate full-screen menu that
// closes the chest (confirmed in the log: from the menu CanMoveAll is always 'no'). So
// the deposit must be triggered from inside the storage context. We poll a hotkey on the
// game thread from the PE net-hook (GetAsyncKeyState reads the physical key state
// regardless of which UI owns focus, so it works while the storage screen has input) and
// fire QuickDeposit on the rising edge - the SAME execution context as the mod-tab button
// (both run inside a ProcessEvent dispatch via the net-hook), so the reflected calls are
// known-safe. Edge-latched + re-entrancy-guarded so the nested CanMoveAll/MoveAll
// dispatches (which funnel back through this hook) can't recurse.
namespace
{
    ULONGLONG     g_lastHotkeyPoll = 0;
    bool          g_hotkeyWasDown  = false;
    volatile LONG g_inQuickDeposit = 0;

    void PollHotkey()
    {
        ULONGLONG now = GetTickCount64();
        if (g_lastHotkeyPoll != 0 && (now - g_lastHotkeyPoll) < kHotkeyPollMs) return;
        g_lastHotkeyPoll = now;

        bool down   = (GetAsyncKeyState(kHotkeyVk) & 0x8000) != 0;
        bool rising = down && !g_hotkeyWasDown;
        g_hotkeyWasDown = down;
        if (!rising) return;

        // Guard against re-entry on the same thread (cheap; nothing here writes now).
        if (InterlockedCompareExchange(&g_inQuickDeposit, 1, 0) != 0) return;
        // GAS-recon mode: INSERT is deliberately inert (no write). The reflected MoveAll-slot
        // swap was disproven; this build only observes the vanilla radial deposit path.
        QM_LOG_INFO("[Deposit] hotkey (VK 0x%02X) pressed - recon mode, INSERT disabled (no write). Use the "
                    "vanilla radial 'Deposit Similar' interact action on a chest so the ring captures it.",
                    kHotkeyVk);
        InterlockedExchange(&g_inQuickDeposit, 0);
    }
}

// Defined in the native section at the end of this file.
bool QmDeposit_NativeActive();

// ---- always-on deposit/storage breadcrumb ---------------------------------------
// Runs even when the native path is active (which otherwise returns from OnProcessEvent
// before any recon). This guarantees that ANY deposit/storage event passing through
// ProcessEvent is logged - independent of whether our native F-hook caught it. It is the
// discriminator we have been missing: a 'breadcrumb' line together with 'organic deposit
// captured' means our native hook sits on the deposit path; breadcrumb lines WITHOUT the
// capture mean the user's deposit reaches storage by a path our F-hook does not cover
// (e.g. the reflected Stack-All button rather than the radial GAS action). Cheap on the
// hot path: memoized per-UFunction verdict (pointer compare + bit test) + per-fn rate-limit.
namespace
{
    void DepositBreadcrumb(QmUE::UObject* self, QmUE::UFunction* func)
    {
        if (!self || !func) return;
        __try
        {
            QmUE::UClass* cls = self->Class;
            if (!cls) return;

            FnMemo& fs = g_fnMemo[(((uintptr_t)func) >> 4) & kFnMask];
            uint8_t fv;
            if (fs.fn == func && (fs.verdict & DF_VALID))
            {
                fv = fs.verdict;
            }
            else
            {
                fv = ComputeFuncVerdict(func);
                fs.verdict  = 0;
                fs.fn       = func;
                fs.lastTick = 0;
                fs.verdict  = fv;
            }
            if (!(fv & (DF_STRONG | DF_NOTIFY))) return;

            ULONGLONG now = GetTickCount64();
            if (fs.lastTick != 0 && (now - fs.lastTick) < kReLogMs) return;
            fs.lastTick = now;

            char clsNm[160] = { 0 }, fnNm[160] = { 0 }, objNm[160] = { 0 };
            QmUE::ResolveFNameNarrow(cls->Name,  clsNm, sizeof(clsNm));
            QmUE::ResolveFNameNarrow(func->Name, fnNm,  sizeof(fnNm));
            QmUE::ResolveFNameNarrow(self->Name, objNm, sizeof(objNm));
            QM_LOG_INFO("[Deposit] breadcrumb: %s::%s self=0x%p '%s'%s%s",
                        clsNm[0] ? clsNm : "?", fnNm[0] ? fnNm : "?", (void*)self,
                        objNm[0] ? objNm : "?",
                        (fv & DF_STRONG) ? " [deposit/transfer]" : "",
                        (fv & DF_NOTIFY) ? " [storage-notify]"   : "");
        }
        __except (EXCEPTION_EXECUTE_HANDLER) {}
    }
}

void QmDeposit_OnProcessEvent(QmUE::UObject* self, QmUE::UFunction* func, void* parms)
{
    if (!g_armed) return;

    // Native camp-wide deposit path: install the getter/body MinHooks once UE is ready,
    // then retire the (disproven) reflected ProcessEvent recon - the native path augments
    // the organic radial deposit directly, so neither the ring nor the INSERT hotkey is used.
    QmDeposit_EnsureNativeInstalled();
    if (QmDeposit_NativeActive())
    {
        // Always-on breadcrumb: even though the native path owns the deposit, keep logging every
        // deposit/storage event so a real deposit is never invisible (and we can tell whether it
        // reached our F-hook vs. bypassed it). Then return - the recon ring/hotkey below is dead
        // code on the native path.
        DepositBreadcrumb(self, func);
        return;
    }

    // In-context hotkey: fire QuickDeposit when pressed while a storage screen is open
    // (the only context where the inventory VM is bound to a deposit target).
    PollHotkey();

    if (!self || !func) return;

    __try
    {
        QmUE::UClass* cls = self->Class;
        if (!cls) return;

        uint8_t cv = ClassVerdict(cls);

        // Per-func verdict (memoized) - is this a strongly deposit/transfer-named call?
        FnMemo& fs = g_fnMemo[(((uintptr_t)func) >> 4) & kFnMask];
        uint8_t fv;
        if (fs.fn == func && (fs.verdict & DF_VALID))
        {
            fv = fs.verdict;
        }
        else
        {
            fv = ComputeFuncVerdict(func);
            fs.verdict  = 0;        // invalidate while publishing
            fs.fn       = func;
            fs.lastTick = 0;        // fresh func -> allow an immediate first log
            fs.verdict  = fv;       // publish last
        }

        const bool interesting = (cv & DC_INTEREST) || (fv & DF_STRONG);

        // Record every dispatch into the rolling ring (cheap: one increment + 3 stores). For
        // interesting calls also snapshot the head of the param block (valid only during the
        // call) so the flush can show the activation/transfer args after the fact.
        {
            LONG h = InterlockedIncrement(&g_peRingHead);
            PeRingEntry& e = g_peRing[(uint32_t)(h - 1) & kRingMask];
            e.cls = cls; e.func = func; e.self = self; e.snapLen = 0;
            if (interesting && parms)
            {
                uint16_t ps = 0;
                __try { ps = func->ParmsSize; } __except (EXCEPTION_EXECUTE_HANDLER) { ps = 0; }
                if (ps > sizeof(e.snap)) ps = (uint16_t)sizeof(e.snap);
                if (ps > 0)
                {
                    __try { memcpy(e.snap, parms, ps); e.snapLen = ps; }
                    __except (EXCEPTION_EXECUTE_HANDLER) { e.snapLen = 0; }
                }
            }
        }

        ULONGLONG now = GetTickCount64();

        // A strong deposit/interact-option ability dispatch is our anchor: read its
        // TargetModel (the chest the vanilla deposit aims at) to confirm the 0x3E8 field,
        // and flush the ring (cooldown-gated) to reveal the transfer call that preceded it.
        if (cv & DC_STRONG)
        {
            void* tm = nullptr;
            __try { tm = *reinterpret_cast<void**>(reinterpret_cast<uint8_t*>(self) + kAbilityTargetModelOff); }
            __except (EXCEPTION_EXECUTE_HANDLER) { tm = nullptr; }
            char tmDesc[336] = { 0 };
            DescribeMaybeObject((uint64_t)tm, tmDesc, sizeof(tmDesc));
            QM_LOG_INFO("[Deposit] seed: ability self=0x%p TargetModel@0x%X = 0x%p%s",
                        (void*)self, (unsigned)kAbilityTargetModelOff, tm,
                        tmDesc[0] ? tmDesc : " -> <none/native>");

            if (g_lastFlush == 0 || (now - g_lastFlush) >= kFlushCooldownMs)
            {
                g_lastFlush = now;
                FlushPeRing(g_peRingHead);
            }
        }

        if (!interesting) return;

        // Rate-limit per UFunction so menu navigation / VM ticks can't flood.
        if (fs.lastTick != 0 && (now - fs.lastTick) < kReLogMs) return;
        fs.lastTick = now;

        long n = InterlockedIncrement(&g_logCount);
        char clsNm[160] = { 0 }, fnNm[160] = { 0 }, objNm[160] = { 0 };
        QmUE::ResolveFNameNarrow(cls->Name,  clsNm, sizeof(clsNm));
        QmUE::ResolveFNameNarrow(func->Name, fnNm,  sizeof(fnNm));
        QmUE::ResolveFNameNarrow(self->Name, objNm, sizeof(objNm));
        QM_LOG_INFO("[Deposit] hit#%ld %s::%s self=0x%p '%s'%s%s",
            n, clsNm[0] ? clsNm : "?", fnNm[0] ? fnNm : "?", (void*)self, objNm[0] ? objNm : "?",
            (cv & DC_STRONG) ? " [strong-cls]" : "",
            (fv & DF_STRONG) ? " [strong-fn]"  : "");

        if ((cv & DC_STRONG) || (fv & DF_STRONG))
            DumpParms(func, parms, n);
    }
    __except (EXCEPTION_EXECUTE_HANDLER) {}
}

// ---- V1 active "Quick Deposit (Similar)" action ---------------------------------
namespace
{
    // EObjectFlags bit for the class-default object (template, not a live instance).
    constexpr uint32_t kRF_ClassDefaultObject = 0x00000010;

    // Stride of one TSet element in UIInventoryContainers
    // (TMap<FGameplayTag, FR5InventoryContainerInstanceData>):
    //   TPair{ FGameplayTag Key(0x08) @0x00; FR5InventoryContainerInstanceData Value(0x58) @0x08 } = 0x60
    //   TSetElement adds {int32 HashNextId; int32 HashIndex} = +0x08  ->  0x68
    // The FGameplayTag key sits at element offset 0x00. Verified against R5_structs.hpp.
    constexpr size_t kContainerElemStride = 0x68;

    // UIInventoryContainers lives at R5BaseInventoryVM +0x78 (the TMap header). Its first
    // qword is the sparse-array data pointer; the int32 at +0x08 is the element count.
    constexpr size_t kUIContainersOff = 0x78;

    // R5BaseInventoryVM::HandledInventories is a TSet at +0xC8; the int32 at +0xC8+0x08
    // is the inner sparse-array element count (allocated slots incl. holes). Used purely
    // as a "is this VM bound to live inventories (player + an open chest)?" indicator.
    constexpr size_t kHandledInvOff = 0xC8;

    int32_t VmHandledInventoriesCount(QmUE::UObject* vm)
    {
        int32_t n = -1;
        __try { n = *reinterpret_cast<int32_t*>(reinterpret_cast<uint8_t*>(vm) + kHandledInvOff + 0x08); }
        __except (EXCEPTION_EXECUTE_HANDLER) { n = -1; }
        return n;
    }

    // Call CanMoveAll(tag, bOnlyStack) on `vm`. Returns true only if the call succeeded
    // AND the game says the move is valid. SEH lives in CallProcessEvent.
    bool VmCanMoveAll(QmUE::UObject* vm, QmUE::UFunction* fn, const QmUE::FGameplayTag& tag, bool onlyStack)
    {
        struct CanParms { QmUE::FGameplayTag Tag; uint8_t bOnlyStack; uint8_t bReturn; uint8_t pad[2]; };
        CanParms p; memset(&p, 0, sizeof(p));
        p.Tag = tag; p.bOnlyStack = onlyStack ? 1 : 0;
        if (!QmUE::CallProcessEvent(vm, fn, &p)) return false;
        return p.bReturn != 0;
    }

    // Call MoveAll(tag, bOnlyStack=true) on `vm`. Returns whether the dispatch ran.
    bool VmMoveAll(QmUE::UObject* vm, QmUE::UFunction* fn, const QmUE::FGameplayTag& tag)
    {
        struct MoveParms { QmUE::FGameplayTag Tag; uint8_t bOnlyStack; uint8_t pad[3]; };
        MoveParms p; memset(&p, 0, sizeof(p));
        p.Tag = tag; p.bOnlyStack = 1;
        return QmUE::CallProcessEvent(vm, fn, &p);
    }
}

void QmDeposit_QuickDeposit()
{
    if (!QmUE::IsReady())
    {
        QM_LOG_WARN("[Deposit] quick_deposit: UE not ready (not in-world yet)");
        return;
    }

    QmUE::UClass* vmCls = QmUE::FindClassByName("R5DefaultInventoryVM");
    if (!vmCls)
    {
        QM_LOG_WARN("[Deposit] quick_deposit: R5DefaultInventoryVM class not found");
        return;
    }

    QmUE::UFunction* fnCan  = QmUE::FindFunctionOnClass(vmCls, "CanMoveAll");
    QmUE::UFunction* fnMove = QmUE::FindFunctionOnClass(vmCls, "MoveAll");
    if (!fnCan || !fnMove)
    {
        QM_LOG_WARN("[Deposit] quick_deposit: reflected verbs missing (CanMoveAll=0x%p MoveAll=0x%p)",
                    (void*)fnCan, (void*)fnMove);
        return;
    }

    QmUE::TUObjectArray* go = QmUE::GetGObjects();
    if (!go) { QM_LOG_WARN("[Deposit] quick_deposit: GObjects unavailable"); return; }

    int vmCount = 0, tagsSeen = 0, deposited = 0;
    const int32_t total = go->Num();
    for (int32_t i = 0; i < total; ++i)
    {
        // Pointer-compare against the resolved class (cheap, exact) + skip the CDO.
        QmUE::UObject* vm = nullptr;
        __try
        {
            QmUE::UObject* obj = go->GetByIndex(i);
            if (obj && obj->Class == vmCls && !(obj->Flags & kRF_ClassDefaultObject))
                vm = obj;
        }
        __except (EXCEPTION_EXECUTE_HANDLER) { continue; }
        if (!vm) continue;

        ++vmCount;
        char vmNm[160] = { 0 };
        QmUE::ResolveFNameNarrow(vm->Name, vmNm, sizeof(vmNm));

        // Read the UIInventoryContainers sparse-array data pointer + element count.
        void*   sparse = nullptr;
        int32_t num    = 0;
        __try
        {
            uint8_t* base = reinterpret_cast<uint8_t*>(vm);
            sparse = *reinterpret_cast<void**>(base + kUIContainersOff);
            num    = *reinterpret_cast<int32_t*>(base + kUIContainersOff + 0x08);
        }
        __except (EXCEPTION_EXECUTE_HANDLER) { sparse = nullptr; num = 0; }

        // HandledInventories count is the binding indicator: an unbound player VM holds
        // only its own inventories; opening a chest binds that storage in too (count rises).
        int32_t handled = VmHandledInventoriesCount(vm);
        QM_LOG_INFO("[Deposit] quick_deposit: VM#%d '%s' UIContainers=%d HandledInv=%d",
                    vmCount, vmNm[0] ? vmNm : "?", (int)num, (int)handled);
        if (!sparse || num <= 0) continue;
        if (num > 64) num = 64;     // sanity cap (a freshly-bound inventory holds a handful)

        for (int32_t k = 0; k < num; ++k)
        {
            QmUE::FGameplayTag tag{};
            __try
            {
                tag = *reinterpret_cast<QmUE::FGameplayTag*>(
                          reinterpret_cast<uint8_t*>(sparse) + (size_t)k * kContainerElemStride);
            }
            __except (EXCEPTION_EXECUTE_HANDLER) { continue; }
            if (tag.IsNone()) continue;     // None / stale free-list slot

            char tagNm[200] = { 0 };
            if (!QmUE::ResolveFNameNarrow(tag, tagNm, sizeof(tagNm)) || !tagNm[0]) continue;
            ++tagsSeen;

            // Diagnose both verbs: bOnlyStack=true is "Deposit Similar", false is "Deposit
            // All". Seeing which (if either) the game permits when a chest is open pins down
            // exactly what the bound VM allows.
            bool canSimilar = VmCanMoveAll(vm, fnCan, tag, true);
            bool canAll     = VmCanMoveAll(vm, fnCan, tag, false);
            QM_LOG_INFO("[Deposit] quick_deposit:   tag[%d]='%s' canMoveAll(similar)=%s canMoveAll(all)=%s",
                        k, tagNm, canSimilar ? "yes" : "no", canAll ? "yes" : "no");
            if (!canSimilar) continue;

            bool fired = VmMoveAll(vm, fnMove, tag);
            if (fired) ++deposited;
            QM_LOG_INFO("[Deposit] quick_deposit:   *** MoveAll(similar) tag='%s' -> %s ***",
                        tagNm, fired ? "fired" : "call-failed");
        }
    }

    QM_LOG_INFO("[Deposit] quick_deposit: done - %d live VM(s), %d tag(s) seen, %d deposit call(s) fired",
                vmCount, tagsSeen, deposited);
}

// ---- camp-wide retarget recon (read-only) ---------------------------------------
namespace
{
    // UR5BuildingCenterStorageComponent::Inventories (UR5InventoryAggregatorComponent*).
    constexpr size_t kBCS_InventoriesOff    = 0xD0;
    // UR5InventoryAggregatorComponent::InventoryViews - TArray<UR5BLInventoryView*>
    // (data ptr @ +0x00, int32 Num @ +0x08).
    constexpr size_t kAgg_InventoryViewsOff = 0xC8;
    // UR5ProximityStorageComponent::PlayerInventoryView (UR5BLInventoryView*).
    constexpr size_t kPSC_PlayerViewOff     = 0xE8;
    // HandledInventories is a TSet<UR5BLInventoryView*>: a TSparseArray of
    // TSetElement<ptr>{ ptr Value@0x00; int32 HashNextId; int32 HashIndex } = 0x10 stride.
    // The element Value sits at element offset 0x00. Holes fail the pointer validation
    // in DescribeMaybeObject, so iterating 0..Num-1 and validating is safe.
    constexpr size_t kHandledSetStride      = 0x10;

    void* ScanReadPtr(void* base, size_t off)
    {
        void* p = nullptr;
        __try { p = *reinterpret_cast<void**>(reinterpret_cast<uint8_t*>(base) + off); }
        __except (EXCEPTION_EXECUTE_HANDLER) { p = nullptr; }
        return p;
    }
    int32_t ScanReadI32(void* base, size_t off)
    {
        int32_t v = 0;
        __try { v = *reinterpret_cast<int32_t*>(reinterpret_cast<uint8_t*>(base) + off); }
        __except (EXCEPTION_EXECUTE_HANDLER) { v = 0; }
        return v;
    }
}

void QmDeposit_CampScan()
{
    if (!QmUE::IsReady()) { QM_LOG_WARN("[Deposit] camp-scan: UE not ready"); return; }

    // 1) Player inventory view - positively identifies the 'self' entry in HandledInventories.
    void* playerView = nullptr;
    if (QmUE::UObject* psc = QmUE::FindFirstInstanceOfClass("R5ProximityStorageComponent"))
    {
        playerView = ScanReadPtr(psc, kPSC_PlayerViewOff);
        char d[336] = { 0 };
        DescribeMaybeObject((uint64_t)playerView, d, sizeof(d));
        QM_LOG_INFO("[Deposit] camp-scan: ProximityStorage=0x%p PlayerInventoryView=0x%p%s",
                    (void*)psc, playerView, d[0] ? d : "");
    }
    else QM_LOG_INFO("[Deposit] camp-scan: no live R5ProximityStorageComponent");

    // 2) Every live default inventory VM -> decode HandledInventories, collect the views.
    void* handled[64]; int handledN = 0;
    QmUE::UClass* vmCls = QmUE::FindClassByName("R5DefaultInventoryVM");
    QmUE::TUObjectArray* go = QmUE::GetGObjects();
    if (vmCls && go)
    {
        int vmN = 0;
        const int32_t total = go->Num();
        for (int32_t i = 0; i < total; ++i)
        {
            QmUE::UObject* vm = nullptr;
            __try
            {
                QmUE::UObject* obj = go->GetByIndex(i);
                if (obj && obj->Class == vmCls && !(obj->Flags & kRF_ClassDefaultObject)) vm = obj;
            }
            __except (EXCEPTION_EXECUTE_HANDLER) { continue; }
            if (!vm) continue;
            ++vmN;

            char vmNm[160] = { 0 };
            QmUE::ResolveFNameNarrow(vm->Name, vmNm, sizeof(vmNm));
            void*   setData = ScanReadPtr(vm, kHandledInvOff);
            int32_t setNum  = ScanReadI32(vm, kHandledInvOff + 0x08);
            QM_LOG_INFO("[Deposit] camp-scan: VM#%d '%s' HandledInventories data=0x%p slots=%d",
                        vmN, vmNm[0] ? vmNm : "?", setData, (int)setNum);
            if (!setData || setNum <= 0) continue;
            if (setNum > 64) setNum = 64;
            for (int32_t k = 0; k < setNum; ++k)
            {
                void* view = ScanReadPtr(setData, (size_t)k * kHandledSetStride);
                if (!view) continue;
                char d[336] = { 0 };
                DescribeMaybeObject((uint64_t)view, d, sizeof(d));
                if (!d[0]) continue;     // not a valid UObject* (sparse-array hole) -> skip
                if (handledN < 64) handled[handledN++] = view;
                QM_LOG_INFO("[Deposit] camp-scan:   handled[%d]=0x%p%s%s",
                            k, view, d, (view == playerView) ? "  [=PlayerView]" : "");
            }
        }
        if (vmN == 0) QM_LOG_INFO("[Deposit] camp-scan: no live R5DefaultInventoryVM");
    }
    else QM_LOG_WARN("[Deposit] camp-scan: VM class / GObjects unavailable");

    // 3) Building-center aggregator -> all camp chest views; classify each against the
    //    player view + the currently-handled views. A chest view that is handled right now
    //    is the OPEN chest (the current MoveAll target); the rest are RETARGET candidates.
    int retargetCandidates = 0;
    if (QmUE::UObject* bcs = QmUE::FindFirstInstanceOfClass("R5BuildingCenterStorageComponent"))
    {
        void*   agg      = ScanReadPtr(bcs, kBCS_InventoriesOff);
        void*   viewData = agg ? ScanReadPtr(agg, kAgg_InventoryViewsOff)          : nullptr;
        int32_t viewNum  = agg ? ScanReadI32(agg, kAgg_InventoryViewsOff + 0x08)   : 0;
        QM_LOG_INFO("[Deposit] camp-scan: BuildingCenterStorage=0x%p aggregator=0x%p InventoryViews=%d",
                    (void*)bcs, agg, (int)viewNum);
        if (viewData && viewNum > 0)
        {
            if (viewNum > 64) viewNum = 64;
            for (int32_t i = 0; i < viewNum; ++i)
            {
                void* v = ScanReadPtr(viewData, (size_t)i * 8);
                if (!v) continue;
                char d[336] = { 0 };
                DescribeMaybeObject((uint64_t)v, d, sizeof(d));
                bool isHandled = false;
                for (int h = 0; h < handledN; ++h) if (handled[h] == v) { isHandled = true; break; }
                const char* tag = (v == playerView) ? "  [=PlayerView]"
                                : isHandled         ? "  [=OPEN chest (current MoveAll target)]"
                                                    : "  [retarget candidate]";
                if (!isHandled && v != playerView) ++retargetCandidates;
                QM_LOG_INFO("[Deposit] camp-scan:   chestView[%d]=0x%p%s%s", i, v, d[0] ? d : "", tag);
            }
        }
    }
    else QM_LOG_INFO("[Deposit] camp-scan: no live R5BuildingCenterStorageComponent");

    QM_LOG_INFO("[Deposit] camp-scan: done - %d handled view(s), %d retarget candidate chest(s)",
                handledN, retargetCandidates);
}

// ---- swap-proof: prove the reflected retarget by swapping the deposit target ----------
namespace
{
    // Read the validated UR5BLInventoryView* at HandledInventories slot `k` (sparse-array
    // element @ k*stride, view pointer at element offset 0x00). Returns null on a hole /
    // invalid pointer (DescribeMaybeObject does the conservative UObject validation).
    void* ReadHandledView(void* setData, int k)
    {
        void* v = ScanReadPtr(setData, (size_t)k * kHandledSetStride);
        if (!v) return nullptr;
        char d[336] = { 0 };
        DescribeMaybeObject((uint64_t)v, d, sizeof(d));
        return d[0] ? v : nullptr;
    }

    // Overwrite the view pointer at HandledInventories slot `k`. SEH-guarded single store.
    bool WriteHandledView(void* setData, int k, void* view)
    {
        bool ok = false;
        __try
        {
            *reinterpret_cast<void**>(reinterpret_cast<uint8_t*>(setData) + (size_t)k * kHandledSetStride) = view;
            ok = true;
        }
        __except (EXCEPTION_EXECUTE_HANDLER) { ok = false; }
        return ok;
    }
}

void QmDeposit_SwapProof()
{
    if (!QmUE::IsReady()) { QM_LOG_WARN("[Deposit] swap-proof: UE not ready (not in-world yet)"); return; }

    QmUE::UClass* vmCls = QmUE::FindClassByName("R5DefaultInventoryVM");
    if (!vmCls) { QM_LOG_WARN("[Deposit] swap-proof: R5DefaultInventoryVM class not found"); return; }
    QmUE::UFunction* fnCan  = QmUE::FindFunctionOnClass(vmCls, "CanMoveAll");
    QmUE::UFunction* fnMove = QmUE::FindFunctionOnClass(vmCls, "MoveAll");
    if (!fnCan || !fnMove)
    {
        QM_LOG_WARN("[Deposit] swap-proof: reflected verbs missing (CanMoveAll=0x%p MoveAll=0x%p)",
                    (void*)fnCan, (void*)fnMove);
        return;
    }
    QmUE::TUObjectArray* go = QmUE::GetGObjects();
    if (!go) { QM_LOG_WARN("[Deposit] swap-proof: GObjects unavailable"); return; }

    // 1) Collect every live VM with a non-empty HandledInventories set + its validated views.
    struct VmRow
    {
        QmUE::UObject* vm;
        void*          setData;
        int            viewCount;
        void*          views[8];
        int            slots[8];
        char           name[96];
    };
    VmRow rows[32]; int nRows = 0;

    const int32_t total = go->Num();
    for (int32_t i = 0; i < total && nRows < 32; ++i)
    {
        QmUE::UObject* vm = nullptr;
        __try
        {
            QmUE::UObject* obj = go->GetByIndex(i);
            if (obj && obj->Class == vmCls && !(obj->Flags & kRF_ClassDefaultObject)) vm = obj;
        }
        __except (EXCEPTION_EXECUTE_HANDLER) { continue; }
        if (!vm) continue;

        void*   setData = ScanReadPtr(vm, kHandledInvOff);
        int32_t setNum  = ScanReadI32(vm, kHandledInvOff + 0x08);
        if (!setData || setNum <= 0) continue;
        if (setNum > 8) setNum = 8;

        VmRow& r = rows[nRows];
        r.vm = vm; r.setData = setData; r.viewCount = 0;
        QmUE::ResolveFNameNarrow(vm->Name, r.name, sizeof(r.name));
        for (int32_t k = 0; k < setNum; ++k)
        {
            void* view = ReadHandledView(setData, k);
            if (!view) continue;
            r.views[r.viewCount] = view;
            r.slots[r.viewCount] = k;
            r.viewCount++;
        }
        if (r.viewCount > 0) ++nRows;
    }
    if (nRows == 0)
    {
        QM_LOG_WARN("[Deposit] swap-proof: no bound VM with handled views - OPEN a chest screen first");
        return;
    }

    // 2) Source (player) view = the view present in the most VMs (the player's own inventory
    //    recurs across every bound VM; chest views differ). Frequency = number of VMs holding it.
    void* sourceView = nullptr; int sourceFreq = 0;
    for (int a = 0; a < nRows; ++a)
        for (int x = 0; x < rows[a].viewCount; ++x)
        {
            void* cand = rows[a].views[x];
            int freq = 0;
            for (int b = 0; b < nRows; ++b)
                for (int y = 0; y < rows[b].viewCount; ++y)
                    if (rows[b].views[y] == cand) { ++freq; break; }
            if (freq > sourceFreq) { sourceFreq = freq; sourceView = cand; }
        }
    QM_LOG_INFO("[Deposit] swap-proof: %d bound VM(s); source(player) view=0x%p (in %d VM(s))",
                nRows, sourceView, sourceFreq);

    // 3) Active VM = the one whose UIInventoryContainers tag the game accepts for CanMoveAll
    //    (similar). That marks the bound VM + the working source tag.
    QmUE::UObject*     activeVm = nullptr;
    QmUE::FGameplayTag workingTag{};
    char               workingTagNm[200] = { 0 };
    for (int a = 0; a < nRows && !activeVm; ++a)
    {
        void*   sparse = ScanReadPtr(rows[a].vm, kUIContainersOff);
        int32_t num    = ScanReadI32(rows[a].vm, kUIContainersOff + 0x08);
        if (!sparse || num <= 0) continue;
        if (num > 64) num = 64;
        for (int32_t k = 0; k < num; ++k)
        {
            QmUE::FGameplayTag tag{};
            __try
            {
                tag = *reinterpret_cast<QmUE::FGameplayTag*>(
                          reinterpret_cast<uint8_t*>(sparse) + (size_t)k * kContainerElemStride);
            }
            __except (EXCEPTION_EXECUTE_HANDLER) { continue; }
            if (tag.IsNone()) continue;
            char tagNm[200] = { 0 };
            if (!QmUE::ResolveFNameNarrow(tag, tagNm, sizeof(tagNm)) || !tagNm[0]) continue;
            if (VmCanMoveAll(rows[a].vm, fnCan, tag, true))
            {
                activeVm = rows[a].vm;
                workingTag = tag;
                strncpy(workingTagNm, tagNm, sizeof(workingTagNm) - 1);
                break;
            }
        }
    }
    if (!activeVm)
    {
        QM_LOG_WARN("[Deposit] swap-proof: no VM accepts CanMoveAll(similar) - is a chest open? aborting (no write)");
        return;
    }

    // 4) In the active VM, the non-source handled view is the OPEN chest = our swap target.
    VmRow* ar = nullptr;
    for (int a = 0; a < nRows; ++a) if (rows[a].vm == activeVm) { ar = &rows[a]; break; }
    int   openSlot      = -1;
    void* openChestView = nullptr;
    for (int x = 0; ar && x < ar->viewCount; ++x)
        if (ar->views[x] != sourceView) { openSlot = ar->slots[x]; openChestView = ar->views[x]; break; }
    if (openSlot < 0)
    {
        QM_LOG_WARN("[Deposit] swap-proof: active VM has no non-source (chest) view to retarget - aborting");
        return;
    }
    {
        char d[336] = { 0 }; DescribeMaybeObject((uint64_t)openChestView, d, sizeof(d));
        QM_LOG_INFO("[Deposit] swap-proof: active VM '%s' tag='%s' openChestSlot=%d openChestView=0x%p%s",
                    ar->name[0] ? ar->name : "?", workingTagNm, openSlot, openChestView, d);
    }

    // Baseline: CanMoveAll for the unswapped open chest (expected yes - we deposit there today).
    bool baseSim = VmCanMoveAll(activeVm, fnCan, workingTag, true);
    bool baseAll = VmCanMoveAll(activeVm, fnCan, workingTag, false);
    QM_LOG_INFO("[Deposit] swap-proof: BASELINE (open chest) canMoveAll(similar)=%s (all)=%s",
                baseSim ? "yes" : "no", baseAll ? "yes" : "no");

    // 5) Retarget candidates = distinct validated views (any VM) that are neither the source
    //    nor the open chest = OTHER camp chest views already resident in memory.
    void* cands[32]; int nCands = 0;
    for (int a = 0; a < nRows; ++a)
        for (int x = 0; x < rows[a].viewCount; ++x)
        {
            void* v = rows[a].views[x];
            if (v == sourceView || v == openChestView) continue;
            bool dup = false;
            for (int c = 0; c < nCands; ++c) if (cands[c] == v) { dup = true; break; }
            if (!dup && nCands < 32) cands[nCands++] = v;
        }
    QM_LOG_INFO("[Deposit] swap-proof: %d distinct retarget candidate view(s)", nCands);
    if (nCands == 0)
    {
        QM_LOG_WARN("[Deposit] swap-proof: no second chest view in memory - cannot prove retarget. Open a "
                    "DIFFERENT chest once (then re-open this one) so a second view lingers, and retry.");
        return;
    }

    // 6) For each candidate: swap the open-chest slot, ask CanMoveAll, restore (read-only in
    //    effect). Remember the first candidate the game accepts as the firing target.
    void* fireCand = nullptr; char fireDesc[336] = { 0 };
    for (int c = 0; c < nCands; ++c)
    {
        void* cand = cands[c];
        char  cd[336] = { 0 }; DescribeMaybeObject((uint64_t)cand, cd, sizeof(cd));
        if (!WriteHandledView(ar->setData, openSlot, cand))
        {
            QM_LOG_WARN("[Deposit] swap-proof:   candidate[%d]=0x%p%s - WRITE failed, skipping", c, cand, cd);
            continue;
        }
        bool cSim = VmCanMoveAll(activeVm, fnCan, workingTag, true);
        bool cAll = VmCanMoveAll(activeVm, fnCan, workingTag, false);
        WriteHandledView(ar->setData, openSlot, openChestView);   // restore immediately
        QM_LOG_INFO("[Deposit] swap-proof:   candidate[%d]=0x%p%s -> canMoveAll(similar)=%s (all)=%s",
                    c, cand, cd, cSim ? "yes" : "no", cAll ? "yes" : "no");
        if (cSim && !fireCand) { fireCand = cand; strncpy(fireDesc, cd, sizeof(fireDesc) - 1); }
    }

    if (!fireCand)
    {
        QM_LOG_INFO("[Deposit] swap-proof: NO candidate accepted CanMoveAll after the swap. Either MoveAll's "
                    "target is not read from HandledInventories (swap does not retarget) or the candidates are "
                    "stale. No deposit fired. (Compare to BASELINE above.)");
        return;
    }

    // 7) The one real write: retarget the slot, fire MoveAll(similar), restore immediately.
    bool  wrote    = WriteHandledView(ar->setData, openSlot, fireCand);
    bool  fired    = wrote ? VmMoveAll(activeVm, fnMove, workingTag) : false;
    bool  restored = WriteHandledView(ar->setData, openSlot, openChestView);
    void* readback = ReadHandledView(ar->setData, openSlot);
    QM_LOG_INFO("[Deposit] swap-proof: *** RETARGET FIRED *** MoveAll(similar) tag='%s' target swapped "
                "openChest=0x%p -> candidate=0x%p%s | wrote=%s fired=%s restored=%s (slot now 0x%p%s)",
                workingTagNm, openChestView, fireCand, fireDesc,
                wrote ? "yes" : "no", fired ? "yes" : "no", restored ? "yes" : "no",
                readback, (readback == openChestView) ? " =openChest OK" : " !! MISMATCH");
    QM_LOG_INFO("[Deposit] swap-proof: CHECK IN GAME - if 'similar' items moved into a DIFFERENT chest (the "
                "candidate) instead of the open one, the reflected retarget WORKS -> V2 enumerates all camp "
                "chests and loops this swap. If items stayed in the open chest, MoveAll ignores the swapped "
                "slot -> pivot to GAS 0x3E8 / native.");
}

// ============================================================================
// Native camp-wide deposit  (getter MinHook + body-caller capture)
// ----------------------------------------------------------------------------
// See qm_deposit.hpp for the full rationale. Mechanism decoded from the reference
// mod's known-build table, row "client 2a4f36e9" (= our exact binary, SHA-verified).
// Iteration 1 here is VALIDATE-FIRST: install + self-validate both MinHooks, let the
// organic radial deposit run unchanged, capture the clicked target via the getter, and
// dry-log the neighbour chests. It writes NOTHING to inventory. Iteration 2 flips on the
// staged camp-wide re-invoke loop.
namespace
{
    // RVAs for our build (offsets from the Windrose exe image base).
    constexpr uintptr_t kBodySiteRVA    = 0x08b08a6b;   // jmp rel32 -> dispatcher F
    constexpr uint8_t   kBodySiteOp     = 0xE9;         // jmp rel32
    constexpr int32_t   kBodySiteDisp   = 0x00001630;   // orig5 @ site1 = E9 30 16 00 00
    constexpr uintptr_t kGetterEntryRVA = 0x096c4ea0;   // the deposit-target getter

    // Getter entry signature. NOTE: the reference's known-build table stores ONLY the three
    // site RVAs (no byte signatures), and site3 (0x096c4ea0) is the original call TARGET of
    // site2's call - i.e. the real game-side deposit-target getter. It is NOT a trivial
    // `mov rax,[rcx+0x3E8]; ret` leaf (that earlier guess was wrong); on this build it is a
    // non-leaf accessor whose prologue is the 8 bytes below (read live from 0x096c4ea0:
    // 40 53 48 83 EC 20 = push rbx; sub rsp,0x20). A mismatch means the build moved -> don't patch.
    const uint8_t kGetterSig[8] = { 0x40, 0x53, 0x48, 0x83, 0xEC, 0x20, 0x48, 0x8B };

    using GetterFn = void* (__fastcall*)(void* self);
    using BodyFn   = void* (__fastcall*)(void*, void*, void*, void*);

    GetterFn       g_fpOrigGetter  = nullptr;   // MinHook trampoline -> original getter
    BodyFn         g_fpOrigF        = nullptr;   // MinHook trampoline -> original dispatcher F
    volatile void* g_staged         = nullptr;   // iter-2: getter returns this when set (retarget)
    volatile void* g_clickedTarget  = nullptr;   // captured: the target of the organic deposit
    volatile LONG  g_inBody         = 0;         // re-entrancy / "a deposit is executing now"
    volatile LONG  g_nativeState    = 0;         // 0=not tried, 1=installed, -1=failed/disabled
    volatile ULONGLONG g_lastBodyLog = 0;        // throttle the F-entry breadcrumb (see MyBody)

    // ---- Stack-All / MoveAll path: the user's REAL deposit verb -------------------
    // The user's deposit is the inventory-panel 'Stack All' button -> the VM's reflected
    // MoveAll verb, whose NATIVE exec deposits into the open chest. The dispatcher F hooked
    // above is the GAS radial path and never fires for Stack-All (proven in the log), so the
    // camp-wide behaviour is driven from HERE: we hook the MoveAll exec, let the organic
    // open-chest deposit run unchanged, then re-invoke the reflected MoveAll on every OTHER
    // bound camp VM (one per nearby chest), gated by CanMoveAll(similar). Each re-invoke is the
    // engine's own Deposit-Similar into that chest (only items that stack with its contents
    // move; items go INTO storage, nothing is deleted). Our re-invokes route back through this
    // same exec, so a re-entrancy latch makes the nested calls run the original ONLY - the
    // multipass never recurses, and a burst within the log window runs at most one pass.
    using ExecThunkFn = void(__fastcall*)(void* ctx, void* stack, void* result);
    ExecThunkFn        g_fpOrigMoveAllExec = nullptr;
    volatile ULONGLONG g_lastMoveAllLog    = 0;
    volatile LONG      g_inMoveAll         = 0;   // re-entrancy latch (our own re-invokes re-enter)

    void RunMoveAllCampWide(void* originVm);      // defined below RunCampWideDeposit

    void __fastcall MyMoveAllExec(void* ctx, void* stack, void* result)
    {
        LONG prev = InterlockedExchange(&g_inMoveAll, 1);

        // Always run the real deposit (the organic open-chest pass, or our own neighbour re-invoke).
        if (g_fpOrigMoveAllExec) g_fpOrigMoveAllExec(ctx, stack, result);

        // Only the OUTERMOST call (the user's actual Stack-All) drives the camp-wide multipass.
        // Tying it to the 500 ms log window also de-dups a rapid burst into a single pass.
        if (prev == 0)
        {
            ULONGLONG now = GetTickCount64();
            if (g_lastMoveAllLog == 0 || (now - g_lastMoveAllLog) >= 500)
            {
                g_lastMoveAllLog = now;
                char d[336] = { 0 };
                DescribeMaybeObject((uint64_t)ctx, d, sizeof(d));
                QM_LOG_INFO("[Deposit] native: *** MoveAll exec FIRED (outermost) *** ctx=0x%p%s - organic "
                            "open-chest deposit done; running camp-wide MoveAll multipass.",
                            ctx, d[0] ? d : " -> <unresolved>");
                __try { RunMoveAllCampWide(ctx); }
                __except (EXCEPTION_EXECUTE_HANDLER)
                { QM_LOG_ERROR("[Deposit] native: MoveAll camp-wide multipass faulted (outer SEH)"); }
            }
        }

        InterlockedExchange(&g_inMoveAll, prev);
    }

    // Object-agnostic getter intercept (mirrors the reference's FUN_18000e6b0). When staging
    // is armed (iter-2) it returns the staged chest in place of [rcx+0x3E8]; otherwise it
    // returns the real target and - while a deposit body is executing - captures it once.
    void* __fastcall MyGetter(void* self)
    {
        void* s = (void*)g_staged;
        if (s) return s;
        void* r = g_fpOrigGetter ? g_fpOrigGetter(self) : nullptr;
        if (g_inBody && r && !g_clickedTarget) g_clickedTarget = r;
        return r;
    }

    // Camp-wide re-invoke (the reference multipass, FUN_180017a50). For every other camp chest:
    //   g_staged = chest -> origF(captured args) -> g_staged = null
    // The getter (MyGetter) returns g_staged in place of the clicked chest, so F runs a vanilla
    // "Deposit Similar" into that chest. Only ever called for the outermost real deposit that
    // captured a clicked target, so a failed capture writes NOTHING. SEH-guarded + capped. Each
    // re-invoke moves only items that stack with that chest's existing contents (vanilla
    // Deposit-Similar semantics; items go INTO storage, nothing is deleted) - exactly the
    // reference behaviour. We deposit into every live R5LootableInventoryBox; a radius filter
    // (the reference's MaxCampRadiusMeters) is a later refinement - in the camp only camp chests
    // are loaded, and Deposit-Similar into a non-matching chest is a no-op.
    constexpr int kMaxNeighborChests = 24;

    void RunCampWideDeposit(void* a, void* b, void* c, void* d)
    {
        void* clicked = (void*)g_clickedTarget;
        char ta[336] = { 0 }; DescribeMaybeObject((uint64_t)clicked, ta, sizeof(ta));
        QM_LOG_INFO("[Deposit] native: *** organic deposit captured *** clickedTarget=0x%p%s -> camp-wide re-invoke",
                    clicked, ta[0] ? ta : " -> <unresolved>");

        QmUE::UClass* boxCls = QmUE::FindClassByName("R5LootableInventoryBox");
        QmUE::TUObjectArray* go = QmUE::GetGObjects();
        if (!boxCls || !go)
        {
            QM_LOG_WARN("[Deposit] native: R5LootableInventoryBox class / GObjects unavailable - no re-invoke");
            return;
        }

        int found = 0, fired = 0, skipped = 0, faulted = 0;
        const int32_t total = go->Num();
        for (int32_t i = 0; i < total && fired < kMaxNeighborChests; ++i)
        {
            QmUE::UObject* box = nullptr;
            __try
            {
                QmUE::UObject* o = go->GetByIndex(i);
                if (o && o->Class == boxCls && !(o->Flags & kRF_ClassDefaultObject)) box = o;
            }
            __except (EXCEPTION_EXECUTE_HANDLER) { continue; }
            if (!box) continue;
            ++found;

            // Skip the chest the user already deposited into (the organic pass handled it). If the
            // getter returns a target-model rather than the actor this won't match - harmless, the
            // re-deposit just finds no matching items left and moves nothing.
            if ((void*)box == clicked) { ++skipped; continue; }

            char nm[160] = { 0 };
            QmUE::ResolveFNameNarrow(box->Name, nm, sizeof(nm));

            // Stage this chest as the getter return, re-fire the captured deposit frame, unstage.
            bool ok = false;
            g_staged = box;
            __try { if (g_fpOrigF) { g_fpOrigF(a, b, c, d); ok = true; } }
            __except (EXCEPTION_EXECUTE_HANDLER) { ok = false; }
            g_staged = nullptr;

            if (ok) ++fired; else ++faulted;
            QM_LOG_INFO("[Deposit] native:   chest[%d]=0x%p '%s' -> deposit-similar re-invoke %s",
                        found - 1, (void*)box, nm[0] ? nm : "?", ok ? "fired" : "FAULTED(seh)");
        }

        QM_LOG_INFO("[Deposit] native: camp-wide done - %d chest(s) seen, %d re-invoked, %d skipped(clicked), "
                    "%d faulted (cap=%d). Deposit-Similar moves only items matching each chest's contents.",
                    found, fired, skipped, faulted, kMaxNeighborChests);
    }

    // The MoveAll camp-wide multipass (the Stack-All path, called from MyMoveAllExec). For every
    // bound camp VM except the one the user just deposited into, re-invoke the reflected
    // Deposit-Similar, gated by CanMoveAll(similar) - a chest with no matching items is simply
    // skipped (correct Deposit-Similar semantics). The reflected MoveAll routes back through
    // MyMoveAllExec, but the caller holds the re-entrancy latch, so each nested call runs the
    // original deposit only. Per-VM tag read + each reflected call is SEH-guarded (inside the
    // helpers); the loop is capped. The CanMoveAll verdict for every neighbour is logged so a
    // fully gated-out run is unambiguous (= MoveAll on a neighbour VM does not deposit -> pivot).
    void RunMoveAllCampWide(void* originVm)
    {
        QmUE::UClass* vmCls = QmUE::FindClassByName("R5DefaultInventoryVM");
        if (!vmCls) { QM_LOG_WARN("[Deposit] native: camp-wide MoveAll - VM class not found"); return; }
        QmUE::UFunction* fnCan  = QmUE::FindFunctionOnClass(vmCls, "CanMoveAll");
        QmUE::UFunction* fnMove = QmUE::FindFunctionOnClass(vmCls, "MoveAll");
        if (!fnCan || !fnMove)
        { QM_LOG_WARN("[Deposit] native: camp-wide MoveAll - reflected verbs missing (Can=0x%p Move=0x%p)", (void*)fnCan, (void*)fnMove); return; }
        QmUE::TUObjectArray* go = QmUE::GetGObjects();
        if (!go) { QM_LOG_WARN("[Deposit] native: camp-wide MoveAll - GObjects unavailable"); return; }

        int vmSeen = 0, neighbors = 0, fired = 0, gatedOut = 0;
        const int32_t total = go->Num();
        for (int32_t i = 0; i < total && fired < kMaxNeighborChests; ++i)
        {
            QmUE::UObject* vm = nullptr;
            __try
            {
                QmUE::UObject* o = go->GetByIndex(i);
                if (o && o->Class == vmCls && !(o->Flags & kRF_ClassDefaultObject)) vm = o;
            }
            __except (EXCEPTION_EXECUTE_HANDLER) { continue; }
            if (!vm) continue;
            ++vmSeen;

            char vmNm[160] = { 0 };
            QmUE::ResolveFNameNarrow(vm->Name, vmNm, sizeof(vmNm));
            bool isOrigin = ((void*)vm == originVm);

            void*   sparse = nullptr;
            int32_t num    = 0;
            __try
            {
                uint8_t* b = reinterpret_cast<uint8_t*>(vm);
                sparse = *reinterpret_cast<void**>(b + kUIContainersOff);
                num    = *reinterpret_cast<int32_t*>(b + kUIContainersOff + 0x08);
            }
            __except (EXCEPTION_EXECUTE_HANDLER) { sparse = nullptr; num = 0; }

            int32_t handled = VmHandledInventoriesCount(vm);
            QM_LOG_INFO("[Deposit] native:   VM#%d '%s'%s UIContainers=%d HandledInv=%d",
                        vmSeen, vmNm[0] ? vmNm : "?", isOrigin ? " [ORIGIN - open chest, already deposited]" : "",
                        (int)num, (int)handled);

            if (isOrigin) continue;                   // organic pass already deposited here
            if (!sparse || num <= 0) continue;        // unbound player VM / no containers
            ++neighbors;
            if (num > 64) num = 64;

            for (int32_t k = 0; k < num; ++k)
            {
                QmUE::FGameplayTag tag{};
                __try
                {
                    tag = *reinterpret_cast<QmUE::FGameplayTag*>(
                              reinterpret_cast<uint8_t*>(sparse) + (size_t)k * kContainerElemStride);
                }
                __except (EXCEPTION_EXECUTE_HANDLER) { continue; }
                if (tag.IsNone()) continue;

                char tagNm[200] = { 0 };
                if (!QmUE::ResolveFNameNarrow(tag, tagNm, sizeof(tagNm)) || !tagNm[0]) continue;

                bool canSim = VmCanMoveAll(vm, fnCan, tag, true);
                if (!canSim)
                {
                    ++gatedOut;
                    QM_LOG_INFO("[Deposit] native:     tag='%s' canMoveAll(similar)=no -> skip", tagNm);
                    continue;
                }
                bool ok = VmMoveAll(vm, fnMove, tag);
                if (ok) ++fired;
                QM_LOG_INFO("[Deposit] native:     tag='%s' canMoveAll(similar)=yes -> MoveAll %s",
                            tagNm, ok ? "FIRED" : "call-failed");
            }
        }

        QM_LOG_INFO("[Deposit] native: camp-wide MoveAll done - %d VM(s) seen, %d neighbour(s), %d deposit(s) fired, "
                    "%d tag(s) gated out (cap=%d). Items in neighbour chests => the MoveAll multipass works; "
                    "all gated out => a neighbour VM's MoveAll does not retarget (pivot to decompile).",
                    vmSeen, neighbors, fired, gatedOut, kMaxNeighborChests);
    }

    // Dispatcher-F intercept (the jmp target of body site1). Forwards the captured frame
    // (rcx,rdx,r8,r9) to the original F so the organic deposit runs unchanged, then - for the
    // outermost call that captured a real clicked target - re-fires the deposit into every other
    // camp chest (reference multipass). A failed capture re-invokes NOTHING.
    void* __fastcall MyBody(void* a, void* b, void* c, void* d)
    {
        LONG prev = InterlockedExchange(&g_inBody, 1);
        if (prev == 0)
        {
            g_clickedTarget = nullptr;
            // Loud F-entry breadcrumb: proves the dispatcher F (our deposit hook target) actually
            // fires when the user deposits. If this line never appears but 'breadcrumb' storage-
            // notify lines do, the deposit reaches storage by a path that does NOT route through F
            // -> our native RVA targets the wrong dispatcher for how the user deposits. Throttled
            // so an unexpectedly hot F can't flood; outermost entries only (our re-invoke is nested).
            ULONGLONG now = GetTickCount64();
            if (g_lastBodyLog == 0 || (now - g_lastBodyLog) >= 500)
            {
                g_lastBodyLog = now;
                QM_LOG_INFO("[Deposit] native: body-dispatcher F entered (outermost) - a deposit is "
                            "firing; capturing the clicked target via the getter");
            }
        }

        void* ret = g_fpOrigF ? g_fpOrigF(a, b, c, d) : nullptr;

        if (prev == 0 && g_clickedTarget)
        {
            __try { RunCampWideDeposit(a, b, c, d); }
            __except (EXCEPTION_EXECUTE_HANDLER)
            { g_staged = nullptr; QM_LOG_ERROR("[Deposit] native: camp-wide re-invoke faulted (outer SEH)"); }
        }

        InterlockedExchange(&g_inBody, prev);
        return ret;
    }
}

bool QmDeposit_NativeActive() { return g_nativeState == 1; }

void QmDeposit_EnsureNativeInstalled()
{
    if (!g_armed || g_nativeState != 0) return;     // armed once, install once
    if (!QmUE::IsReady()) return;                   // wait until the exe .text + GObjects exist
    g_nativeState = -1;                             // pessimistic: also stops per-call retry storms

    uintptr_t base = QmUE::GetImageBase();
    if (!base) { QM_LOG_ERROR("[Deposit] native: no image base - cannot install"); return; }

    uint8_t* site1  = reinterpret_cast<uint8_t*>(base + kBodySiteRVA);
    uint8_t* getter = reinterpret_cast<uint8_t*>(base + kGetterEntryRVA);

    // Self-validate body site1 (E9 30 16 00 00). A mismatch means the build shifted; we
    // refuse to patch rather than risk corrupting unrelated game code.
    uint8_t s1[5] = { 0 }; bool ok1 = false;
    __try { memcpy(s1, site1, 5); ok1 = true; } __except (EXCEPTION_EXECUTE_HANDLER) { ok1 = false; }
    if (!ok1 || s1[0] != kBodySiteOp || *reinterpret_cast<int32_t*>(s1 + 1) != kBodySiteDisp)
    {
        QM_LOG_ERROR("[Deposit] native: body site1 @0x%p signature mismatch (got %02X %02X %02X %02X %02X, "
                     "want E9 30 16 00 00) - build changed, NOT patching", (void*)site1,
                     s1[0], s1[1], s1[2], s1[3], s1[4]);
        return;
    }
    // Self-validate getter entry (40 53 48 83 EC 20 48 8B = push rbx; sub rsp,0x20; mov ...).
    uint8_t gs[8] = { 0 }; bool ok2 = false;
    __try { memcpy(gs, getter, 8); ok2 = true; } __except (EXCEPTION_EXECUTE_HANDLER) { ok2 = false; }
    if (!ok2 || memcmp(gs, kGetterSig, 8) != 0)
    {
        QM_LOG_ERROR("[Deposit] native: getter entry @0x%p signature mismatch (got %02X %02X %02X %02X %02X "
                     "%02X %02X %02X, want 40 53 48 83 EC 20 48 8B) - NOT patching", (void*)getter,
                     gs[0], gs[1], gs[2], gs[3], gs[4], gs[5], gs[6], gs[7]);
        return;
    }

    // Original dispatcher F = the jmp target of site1 (= the function the deposit tail-calls).
    void* origF = reinterpret_cast<void*>(base + kBodySiteRVA + 5 + (uintptr_t)(int64_t)kBodySiteDisp);

    MH_Initialize();   // idempotent: qm_hook already initialised MinHook; ALREADY_INITIALIZED is fine

    MH_STATUS st = MH_CreateHook(getter, reinterpret_cast<LPVOID>(&MyGetter),
                                 reinterpret_cast<LPVOID*>(&g_fpOrigGetter));
    if (st != MH_OK) { QM_LOG_ERROR("[Deposit] native: MH_CreateHook(getter @0x%p) FAILED: %s", (void*)getter, MH_StatusToString(st)); return; }

    st = MH_CreateHook(origF, reinterpret_cast<LPVOID>(&MyBody), reinterpret_cast<LPVOID*>(&g_fpOrigF));
    if (st != MH_OK)
    {
        QM_LOG_ERROR("[Deposit] native: MH_CreateHook(F @0x%p) FAILED: %s", origF, MH_StatusToString(st));
        MH_RemoveHook(getter);
        return;
    }

    if (MH_EnableHook(getter) != MH_OK || MH_EnableHook(origF) != MH_OK)
    {
        QM_LOG_ERROR("[Deposit] native: MH_EnableHook FAILED");
        MH_DisableHook(getter); MH_DisableHook(origF);
        MH_RemoveHook(getter);  MH_RemoveHook(origF);
        return;
    }

    g_nativeState = 1;
    QM_LOG_INFO("[Deposit] native: *** INSTALLED *** getter@0x%p F@0x%p origGetter=0x%p origF=0x%p "
                "(CAMP-WIDE ACTIVE - reference multipass). Stand in your camp and use the vanilla 'Deposit "
                "Similar' action on ONE chest: the mod captures that deposit and repeats it into every other "
                "camp chest (each gets the items that stack with its contents). Watch for 'organic deposit "
                "captured' + 'camp-wide done' in the log.",
                (void*)getter, origF, (void*)g_fpOrigGetter, (void*)g_fpOrigF);

    // Active camp-wide driver on the VM's native MoveAll exec - the verb the 'Stack All' button
    // triggers (the user's real deposit path). Resolve the dedicated native thunk, confirm it is
    // NOT ProcessInternal (which would catch every BP call), then hook it: the organic open-chest
    // deposit forwards unchanged, after which MyMoveAllExec re-invokes Deposit-Similar into every
    // other bound camp chest (gated by CanMoveAll). This is the Stack-All analogue of the GAS
    // multipass above.
    if (QmUE::UClass* vmCls = QmUE::FindClassByName("R5DefaultInventoryVM"))
    {
        QmUE::UFunction* fnMove = QmUE::FindFunctionOnClass(vmCls, "MoveAll");
        void* moveExec = nullptr;
        __try { if (fnMove) moveExec = (void*)fnMove->ExecFunction; }
        __except (EXCEPTION_EXECUTE_HANDLER) { moveExec = nullptr; }

        QmUE::ProcessInternalFn pi = QmUE::GetProcessInternalFn();
        if (!moveExec)
            QM_LOG_WARN("[Deposit] native: MoveAll UFunction/exec not resolvable - camp-wide Stack-All disabled");
        else if ((void*)pi == moveExec)
            QM_LOG_WARN("[Deposit] native: MoveAll exec == ProcessInternal (BP-routed, no dedicated native "
                        "thunk) - NOT hooking (would catch every BP call)");
        else
        {
            unsigned long long rva = (unsigned long long)((uintptr_t)moveExec - base);
            if (MH_CreateHook(moveExec, reinterpret_cast<LPVOID>(&MyMoveAllExec),
                              reinterpret_cast<LPVOID*>(&g_fpOrigMoveAllExec)) == MH_OK &&
                MH_EnableHook(moveExec) == MH_OK)
                QM_LOG_INFO("[Deposit] native: *** MoveAll camp-wide INSTALLED *** exec=0x%p (RVA +0x%llX) - open "
                            "a chest and press 'Stack All': it deposits there, then repeats into every other camp "
                            "chest. Watch for 'MoveAll exec FIRED (outermost)' + 'camp-wide MoveAll done'.",
                            moveExec, rva);
            else
                QM_LOG_ERROR("[Deposit] native: MoveAll-exec hook FAILED (exec=0x%p RVA +0x%llX)", moveExec, rva);
        }
    }
}
