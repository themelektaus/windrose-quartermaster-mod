// Quartermaster item grant - recon stage (see qm_itemgrant.hpp).

#include <windows.h>
#include <stdint.h>
#include <stdio.h>
#include <string.h>

#include "qm_itemgrant.hpp"
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
