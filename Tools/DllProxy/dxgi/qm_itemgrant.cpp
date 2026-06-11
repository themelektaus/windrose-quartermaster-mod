// Quartermaster item grant (see qm_itemgrant.hpp).

#include <windows.h>
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

#include "qm_itemgrant.hpp"
#include "qm_killxp.hpp"   // QmKillXp_PinGrantableOwner (shared G5a owner validation)
#include "qm_ue.hpp"
#include "qm_log.hpp"

namespace
{
    // Proven AddExp Execute entry (qm_killxp.cpp drives it on every XP grant).
    constexpr uintptr_t RVA_AddExpExecute = 0x9803390;

    // AddReward field offsets (Dumper-7 SDK).
    constexpr size_t OFF_Reward       = 0x118;   // TArray<FR5BLItemsStackData>
    constexpr size_t OFF_RewardAttrib = 0x128;   // FR5BLRewardWithAttributeModifier (0x10)
    constexpr size_t OFF_HideNotif    = 0x138;   // bool
    constexpr size_t SZ_StackData     = 0x60;    // FR5BLItemsStackData
    constexpr size_t OFF_StackCount   = 0x58;    // int32 Count
    constexpr size_t SZ_SoftPtr       = 0x28;    // TSoftObjectPtr<UR5BLInventoryItem>

    constexpr int kVtblScanSlots   = 0x180;
    constexpr int kMaxRewardDumps  = 8;
    constexpr int kMaxEntryDumps   = 4;
    constexpr int kMaxItemPdaDumps = 10;

    // ---- SEH primitives (destructor-free scopes) ---------------------------------

    bool SafeReadPtr(const void* p, void** out)
    {
        __try { *out = *reinterpret_cast<void* const*>(p); return true; }
        __except (EXCEPTION_EXECUTE_HANDLER) { return false; }
    }

    bool SafeReadI32(const void* p, int32_t* out)
    {
        __try { *out = *reinterpret_cast<const int32_t*>(p); return true; }
        __except (EXCEPTION_EXECUTE_HANDLER) { return false; }
    }

    bool SafeCopy(void* dst, const void* src, size_t n)
    {
        __try { memcpy(dst, src, n); return true; }
        __except (EXCEPTION_EXECUTE_HANDLER) { return false; }
    }

    bool SafeResolveName(const void* fnamePtr, char* out, int32_t cap)
    {
        out[0] = 0;
        __try
        {
            QmUE::FName n = *reinterpret_cast<const QmUE::FName*>(fnamePtr);
            if (n.IsNone()) { snprintf(out, (size_t)cap, "None"); return true; }
            return QmUE::ResolveFNameNarrow(n, out, cap);
        }
        __except (EXCEPTION_EXECUTE_HANDLER) { out[0] = 0; return false; }
    }

    // Outer-chain path "Package.Outer.Name" (up to 6 levels, SEH-guarded per link).
    void PathOf(QmUE::UObject* o, char* out, size_t outSz)
    {
        out[0] = 0;
        QmUE::UObject* chain[6] = {};
        int n = 0;
        QmUE::UObject* c = o;
        while (c && n < 6)
        {
            chain[n++] = c;
            void* nx = nullptr;
            if (!SafeReadPtr(&c->Outer, &nx)) break;
            c = reinterpret_cast<QmUE::UObject*>(nx);
        }
        for (int i = n - 1; i >= 0; --i)
        {
            char nm[128];
            if (!SafeResolveName(&chain[i]->Name, nm, sizeof(nm)) || !nm[0])
                snprintf(nm, sizeof(nm), "?");
            size_t len = strlen(out);
            snprintf(out + len, outSz - len, (i == n - 1) ? "%s" : ".%s", nm);
        }
    }

    void HexLine(const uint8_t* p, size_t n, char* out, size_t outSz)
    {
        out[0] = 0;
        for (size_t i = 0; i < n; ++i)
        {
            size_t len = strlen(out);
            if (len + 4 >= outSz) break;
            snprintf(out + len, outSz - len, "%02X ", p[i]);
        }
    }

    // One FR5BLItemsStackData entry: Count, raw soft-ptr bytes, and both plausible
    // FTopLevelAssetPath positions inside the soft ptr (shipping layout has no
    // TagAtLastTest -> names at +0x08/+0x10; editor-style layout -> +0x10/+0x18).
    // The dump decides which one is real.
    void DumpStackEntry(const uint8_t* entry, int idx)
    {
        uint8_t soft[SZ_SoftPtr] = {};
        int32_t count = -1;
        bool softOk = SafeCopy(soft, entry, SZ_SoftPtr);
        SafeReadI32(entry + OFF_StackCount, &count);

        char hex[3 * SZ_SoftPtr + 4] = {};
        if (softOk) HexLine(soft, SZ_SoftPtr, hex, sizeof(hex));

        char pkgA[160], assetA[160], pkgB[160], assetB[160];
        SafeResolveName(soft + 0x08, pkgA, sizeof(pkgA));
        SafeResolveName(soft + 0x10, assetA, sizeof(assetA));
        SafeResolveName(soft + 0x10, pkgB, sizeof(pkgB));
        SafeResolveName(soft + 0x18, assetB, sizeof(assetB));

        QM_LOG_INFO("[ItemGrant]     entry#%d Count=%d softOk=%d raw=%s", idx, count, softOk ? 1 : 0, hex);
        QM_LOG_INFO("[ItemGrant]       layoutA(+08/+10): pkg='%s' asset='%s'", pkgA, assetA);
        QM_LOG_INFO("[ItemGrant]       layoutB(+10/+18): pkg='%s' asset='%s'", pkgB, assetB);
    }

    // ---- the grant ----------------------------------------------------------------
    // Task base-class fields proven by the qm_killxp construct grant (same base).
    constexpr size_t OFF_TaskStateByte = 0xC0;   // 0 -> the G3 state gate passes
    constexpr size_t OFF_TaskOwner     = 0xC8;   // pinned by QmKillXp_PinGrantableOwner
    constexpr size_t OFF_TaskOuter     = 0x20;   // UObject::Outer (GetContext walks it to the World)

    // Fallback only; the primary resolve derives Execute live from the shared vtable
    // slot (recon-proven: vtbl[101]), which survives slot-stable relinks.
    constexpr uintptr_t RVA_AddRewardExecuteFallback = 0x98036D0;

    // Soft-ptr at-rest layout (FSoftObjectPath = 0x20 per SDK; shipping has no
    // TagAtLastTest): +0x00 FWeakObjectPtr(8, left INVALID), +0x08 FName PackageName,
    // +0x10 FName AssetName, +0x18 FString SubPath (empty). This is exactly the state
    // of a soft ptr deserialized from an asset, which Execute must resolve anyway.
    constexpr size_t OFF_SoftWeakIdx  = 0x00;
    constexpr size_t OFF_SoftPkgName  = 0x08;
    constexpr size_t OFF_SoftAssetNm  = 0x10;

    constexpr size_t kTaskCloneCap = 0x400;
    __declspec(align(16)) uint8_t g_fireTaskBuf[kTaskCloneCap] = {};
    __declspec(align(16)) uint8_t g_fireEntryBuf[SZ_StackData] = {};
    volatile LONG g_fireBusy = 0;

    bool IsLiveUObject(void* p)
    {
        int32_t idx = -1;
        if (!SafeReadI32(reinterpret_cast<uint8_t*>(p) + 0x0C, &idx)) return false;
        QmUE::TUObjectArray* g = QmUE::GetGObjects();
        if (!g || idx < 0 || idx >= g->Num()) return false;
        return g->GetByIndex(idx) == reinterpret_cast<QmUE::UObject*>(p);
    }

    // live World via GWorld (single OR double deref depending on build layout)
    void* ResolveWorld(uintptr_t base)
    {
        void* w1 = nullptr;
        if (!SafeReadPtr(reinterpret_cast<void*>(base + QmUE::OFFSET_GWorld), &w1) || !w1) return nullptr;
        if (IsLiveUObject(w1)) return w1;
        void* w2 = nullptr;
        if (SafeReadPtr(w1, &w2) && w2 && IsLiveUObject(w2)) return w2;
        return nullptr;
    }

    // AddReward::Execute = the slot in the AddReward CDO vtable where the AddExp CDO
    // vtable holds the proven AddExp Execute entry (Execute is virtual, recon-proven).
    void* ResolveAddRewardExecute(uintptr_t base, QmUE::UObject* expCdo, QmUE::UObject* rwdCdo)
    {
        void* expVt = nullptr; void* rwdVt = nullptr;
        SafeReadPtr(&expCdo->VTable, &expVt);
        SafeReadPtr(&rwdCdo->VTable, &rwdVt);
        void* target = reinterpret_cast<void*>(base + RVA_AddExpExecute);
        for (int slot = 0; slot < kVtblScanSlots && expVt && rwdVt; ++slot)
        {
            void* fn = nullptr;
            if (!SafeReadPtr(reinterpret_cast<uint8_t*>(expVt) + (size_t)slot * 8, &fn)) break;
            if (fn != target) continue;
            void* rfn = nullptr;
            if (SafeReadPtr(reinterpret_cast<uint8_t*>(rwdVt) + (size_t)slot * 8, &rfn) && rfn)
                return rfn;
        }
        return reinterpret_cast<void*>(base + RVA_AddRewardExecuteFallback);
    }

    QmUE::UObject* OutermostOf(QmUE::UObject* o)
    {
        for (int i = 0; o && i < 8; ++i)
        {
            void* nx = nullptr;
            if (!SafeReadPtr(&o->Outer, &nx) || !nx) break;
            o = reinterpret_cast<QmUE::UObject*>(nx);
        }
        return o;
    }

    // Case-insensitive match of a LOADED R5BLInventoryItem PDA by asset name
    // (CDO/archetypes excluded). Name resolves run only on class hits.
    QmUE::UObject* FindItemPdaByName(QmUE::UClass* itemCls, const char* assetName)
    {
        QmUE::TUObjectArray* g = QmUE::GetGObjects();
        if (!g || !itemCls || !assetName || !*assetName) return nullptr;
        int n = g->Num();
        for (int i = 0; i < n; ++i)
        {
            QmUE::UObject* o = g->GetByIndex(i);
            if (!o || o->Class != itemCls) continue;
            if (o->Flags & 0x30) continue;   // RF_ClassDefaultObject | RF_ArchetypeObject
            char nm[160];
            if (!SafeResolveName(&o->Name, nm, sizeof(nm)) || !nm[0]) continue;
            if (_stricmp(nm, assetName) == 0) return o;
        }
        return nullptr;
    }
}

void QmItemGrant_ReconDump()
{
    if (!QmUE::IsReady())
    {
        QM_LOG_INFO("[ItemGrant] recon: QmUE not ready yet - try again once in-world");
        return;
    }
    uintptr_t base = QmUE::GetImageBase();

    QmUE::UClass* expCls  = QmUE::FindClassByName("R5ScenarioTask_AddExp");
    QmUE::UClass* rwdCls  = QmUE::FindClassByName("R5ScenarioTask_AddReward");
    QmUE::UClass* itemCls = QmUE::FindClassByName("R5BLInventoryItem");
    QM_LOG_INFO("[ItemGrant] recon: base=0x%p AddExpCls=0x%p AddRewardCls=0x%p InventoryItemCls=0x%p",
                (void*)base, expCls, rwdCls, itemCls);
    if (rwdCls)
        QM_LOG_INFO("[ItemGrant]   AddReward StructSize=0x%X (SDK expects 0x140)", rwdCls->StructSize);

    QmUE::UObject* expCdo = expCls ? QmUE::GetClassDefaultObject(expCls) : nullptr;
    QmUE::UObject* rwdCdo = rwdCls ? QmUE::GetClassDefaultObject(rwdCls) : nullptr;
    QM_LOG_INFO("[ItemGrant]   AddExpCDO=0x%p AddRewardCDO=0x%p", expCdo, rwdCdo);

    // ---- 1) Execute vtable slot probe -------------------------------------------
    // If Execute is a virtual on the shared task base, the proven AddExp RVA sits in
    // the AddExp CDO vtable; the SAME slot in the AddReward CDO vtable is then
    // AddReward::Execute - no offline disassembly needed.
    if (expCdo && rwdCdo && base)
    {
        void* expVt = nullptr; void* rwdVt = nullptr;
        SafeReadPtr(&expCdo->VTable, &expVt);
        SafeReadPtr(&rwdCdo->VTable, &rwdVt);
        QM_LOG_INFO("[ItemGrant]   vtbl: AddExp=0x%p AddReward=0x%p", expVt, rwdVt);

        void* target = reinterpret_cast<void*>(base + RVA_AddExpExecute);
        int hits = 0;
        for (int slot = 0; slot < kVtblScanSlots && expVt && rwdVt; ++slot)
        {
            void* fn = nullptr;
            if (!SafeReadPtr(reinterpret_cast<uint8_t*>(expVt) + (size_t)slot * 8, &fn)) break;
            if (fn != target) continue;
            void* rfn = nullptr;
            SafeReadPtr(reinterpret_cast<uint8_t*>(rwdVt) + (size_t)slot * 8, &rfn);
            QM_LOG_INFO("[ItemGrant]   *** EXECUTE SLOT *** vtbl[%d] (0x%X): AddExp=0x%p -> AddReward=0x%p (RVA 0x%llX)",
                        slot, slot * 8, fn, rfn,
                        rfn ? (unsigned long long)((uintptr_t)rfn - base) : 0ull);
            if (++hits >= 4) break;
        }
        if (!hits)
            QM_LOG_INFO("[ItemGrant]   AddExp Execute NOT in the first %d vtable slots - "
                        "not virtual (or devirtualized); fallback is offline disasm", kVtblScanSlots);
    }

    // ---- 2) live AddReward donors + 3) InventoryItem PDA census -----------------
    QmUE::TUObjectArray* g = QmUE::GetGObjects();
    if (!g) { QM_LOG_INFO("[ItemGrant] recon: GObjects unavailable"); return; }

    int rwdSeen = 0, rwdWithData = 0, itemSeen = 0;
    int n = g->Num();
    for (int i = 0; i < n; ++i)
    {
        QmUE::UObject* o = g->GetByIndex(i);
        if (!o) continue;

        if (rwdCls && o->Class == rwdCls && o != rwdCdo)
        {
            ++rwdSeen;
            uint8_t* op = reinterpret_cast<uint8_t*>(o);
            void* data = nullptr; int32_t num = -1, max = -1;
            SafeReadPtr(op + OFF_Reward, &data);
            SafeReadI32(op + OFF_Reward + 0x8, &num);
            SafeReadI32(op + OFF_Reward + 0xC, &max);
            if (num > 0) ++rwdWithData;

            if (rwdSeen <= kMaxRewardDumps)
            {
                char path[256]; PathOf(o, path, sizeof(path));
                uint8_t hideNotif = 0xFF; SafeCopy(&hideNotif, op + OFF_HideNotif, 1);
                uint8_t attrib[0x10] = {}; SafeCopy(attrib, op + OFF_RewardAttrib, sizeof(attrib));
                char attribHex[3 * 0x10 + 4]; HexLine(attrib, sizeof(attrib), attribHex, sizeof(attribHex));
                QM_LOG_INFO("[ItemGrant]   AddReward#%d obj=0x%p flags=0x%X Reward num=%d max=%d data=0x%p hideNotif=%d %s",
                            rwdSeen, o, o->Flags, num, max, data, hideNotif, path);
                QM_LOG_INFO("[ItemGrant]     RewardWithAttributeModifier raw=%s", attribHex);
                if (data && num > 0)
                {
                    int cap = (num < kMaxEntryDumps) ? num : kMaxEntryDumps;
                    for (int e = 0; e < cap; ++e)
                        DumpStackEntry(reinterpret_cast<uint8_t*>(data) + (size_t)e * SZ_StackData, e);
                }
            }
        }
        else if (itemCls && o->Class == itemCls)
        {
            ++itemSeen;
            if (itemSeen <= kMaxItemPdaDumps)
            {
                char path[256]; PathOf(o, path, sizeof(path));
                QM_LOG_INFO("[ItemGrant]   InventoryItem#%d obj=0x%p flags=0x%X %s", itemSeen, o, o->Flags, path);
            }
        }
    }
    QM_LOG_INFO("[ItemGrant] recon done: AddReward instances=%d (with Reward data=%d, dumped<=%d), "
                "InventoryItem PDAs=%d (dumped<=%d)",
                rwdSeen, rwdWithData, kMaxRewardDumps, itemSeen, kMaxItemPdaDumps);
}

void QmItemGrant_Fire(const char* argument, const char* packagePath)
{
    if (!argument || !*argument)
    {
        QM_LOG_INFO("[ItemGrant] add_item_test: no argument - running the recon dump instead");
        QmItemGrant_ReconDump();
        return;
    }
    if (!QmUE::IsReady())
    {
        QM_LOG_INFO("[ItemGrant] grant: QmUE not ready yet - try again once in-world");
        return;
    }
    if (InterlockedCompareExchange(&g_fireBusy, 1, 0) != 0) return;

    // "<AssetName>[:<Count>]" - asset names never contain ':'
    char asset[160] = {};
    snprintf(asset, sizeof(asset), "%s", argument);
    int32_t count = 1;
    char* sep = strchr(asset, ':');
    if (sep) { *sep = 0; count = atoi(sep + 1); }
    if (count < 1)   count = 1;
    if (count > 999) count = 999;

    bool fired = false;
    do
    {
        uintptr_t base = QmUE::GetImageBase();
        QmUE::UClass*  expCls  = QmUE::FindClassByName("R5ScenarioTask_AddExp");
        QmUE::UClass*  rwdCls  = QmUE::FindClassByName("R5ScenarioTask_AddReward");
        QmUE::UClass*  itemCls = QmUE::FindClassByName("R5BLInventoryItem");
        QmUE::UObject* expCdo  = expCls ? QmUE::GetClassDefaultObject(expCls) : nullptr;
        QmUE::UObject* rwdCdo  = rwdCls ? QmUE::GetClassDefaultObject(rwdCls) : nullptr;
        if (!base || !expCdo || !rwdCdo || !itemCls)
        {
            QM_LOG_WARN("[ItemGrant] grant: surface not resolved (base=0x%p expCdo=0x%p rwdCdo=0x%p itemCls=0x%p)",
                        (void*)base, expCdo, rwdCdo, itemCls);
            break;
        }

        QmUE::UObject* pda = FindItemPdaByName(itemCls, asset);
        if (!pda && packagePath && *packagePath)
        {
            // Custom mod-pak item: its PDA is only in memory once something referenced it.
            // The catalog carries the mounted package path - one sync load hydrates it.
            wchar_t pkgW[256] = {}, assetW[160] = {};
            for (int i = 0; packagePath[i] && i < 255; ++i) pkgW[i]   = (wchar_t)(unsigned char)packagePath[i];
            for (int i = 0; asset[i]       && i < 159; ++i) assetW[i] = (wchar_t)(unsigned char)asset[i];
            QM_LOG_INFO("[ItemGrant] grant: '%s' not loaded - sync-loading %s ...", asset, packagePath);
            QmUE::UObject* loaded = QmUE::LoadAssetByPath(pkgW, assetW);
            if (loaded && loaded->Class == itemCls && !(loaded->Flags & 0x30))
                pda = loaded;
            else if (loaded)
                QM_LOG_WARN("[ItemGrant] grant: sync load returned 0x%p but not a plain "
                            "R5BLInventoryItem PDA (class=0x%p flags=0x%X) - ignored",
                            loaded, loaded->Class, loaded->Flags);
        }
        if (!pda)
        {
            QM_LOG_WARN("[ItemGrant] grant: no LOADED R5BLInventoryItem PDA named '%s' - "
                        "run the recon census (empty button argument) for valid names", asset);
            break;
        }
        QmUE::UObject* pkg = OutermostOf(pda);
        char pdaPath[256]; PathOf(pda, pdaPath, sizeof(pdaPath));

        int32_t ss = rwdCls->StructSize;
        size_t  sz = (ss >= 0x140 && ss <= (int)kTaskCloneCap) ? (size_t)ss : 0x140;
        if (!SafeCopy(g_fireTaskBuf, rwdCdo, sz))
        {
            QM_LOG_WARN("[ItemGrant] grant: AddReward CDO clone faulted");
            break;
        }

        // One synthesized Reward entry. Zero-init = empty Attributes/Effects + None
        // ItemId - the plain-item-stack shape of a quest reward; only the PDA's two
        // FTopLevelAssetPath names go in (both FNames already exist - the PDA is live).
        memset(g_fireEntryBuf, 0, sizeof(g_fireEntryBuf));
        *reinterpret_cast<int32_t*>(g_fireEntryBuf + OFF_SoftWeakIdx)  = -1;          // FWeakObjectPtr: invalid (uncached)
        *reinterpret_cast<QmUE::FName*>(g_fireEntryBuf + OFF_SoftPkgName) = pkg->Name;
        *reinterpret_cast<QmUE::FName*>(g_fireEntryBuf + OFF_SoftAssetNm) = pda->Name;
        *reinterpret_cast<int32_t*>(g_fireEntryBuf + OFF_StackCount)   = count;

        uint8_t* t = g_fireTaskBuf;
        *reinterpret_cast<void**>(t + OFF_Reward)         = g_fireEntryBuf;
        *reinterpret_cast<int32_t*>(t + OFF_Reward + 0x8) = 1;                        // Num
        *reinterpret_cast<int32_t*>(t + OFF_Reward + 0xC) = 1;                        // Max
        t[OFF_HideNotif]     = 0;                                                     // show the reward notification
        t[OFF_TaskStateByte] = 0;                                                     // G3 state gate passes
        void* world = ResolveWorld(base);
        if (world) *reinterpret_cast<void**>(t + OFF_TaskOuter) = world;

        QmUE::UObject* owner = QmKillXp_PinGrantableOwner(t, /*verbose=*/true);
        if (!owner)
        {
            QM_LOG_WARN("[ItemGrant] grant: no grantable PlayerState - aborted (no Execute fired)");
            break;
        }

        void* exec = ResolveAddRewardExecute(base, expCdo, rwdCdo);
        QM_LOG_INFO("[ItemGrant] grant: firing Execute=0x%p (RVA 0x%llX) item='%s' x%d owner=0x%p world=0x%p (%s)",
                    exec, (unsigned long long)((uintptr_t)exec - base), asset, count, owner, world, pdaPath);

        __try
        {
            using ExecFn = void(__fastcall*)(void*);
            reinterpret_cast<ExecFn>(exec)(g_fireTaskBuf);
            fired = true;
        }
        __except (EXCEPTION_EXECUTE_HANDLER) { fired = false; }

        if (fired)
            QM_LOG_INFO("[ItemGrant] *** GRANT *** %dx '%s' fired on a synthetic from-CDO AddReward task "
                        "(engine-native, persistent) - verify in the inventory", count, asset);
        else
            QM_LOG_WARN("[ItemGrant] *** GRANT FAULTED *** Execute raised on the synthetic task - save untouched");
    } while (0);

    InterlockedExchange(&g_fireBusy, 0);
}
