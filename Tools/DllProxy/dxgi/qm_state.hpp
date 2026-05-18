// Quartermaster shared state + tiny layout helpers
// -------------------------------------------------
// This header is the "common base" included by every Quartermaster TU.
// It contains:
//   - ItemDataLayout: byte offsets inside UR5BuildingItemWidget at +0x340
//   - kBuildingItemsOffset: byte offset of BuildingItems TArray inside
//     UR5BuildingGroupWidget at +0x350
//   - Two small SEH-guarded read helpers reused from diag + inject + hook
//     (TryResolveContextClassName, SafeReadTArrayHeader)
//
// Bigger state (donor pointers, inject counters, hook hit count, spawned pool)
// is owned by qm_inject.cpp / qm_hook.cpp. The handful of values needed by the
// crash handler are exposed via the QmInjectSnapshot() helper in qm_inject.hpp.

#pragma once

#include <windows.h>
#include <stdint.h>
#include <string.h>

#include "qm_ue.hpp"
#include "qm_log.hpp"

// ----- FR5BuildingItemRuntimeData @ UR5BuildingItemWidget+0x340 (Dumper-7) ---
//   0x000  TSoftObjectPtr<IR5BuildingItemInterface> ItemInterface (0x28 bytes)
//          0x000  FWeakObjectPtr WeakPtr           (8 bytes: idx + serial)
//          0x008  FSoftObjectPath ObjectID
//                 0x008  FTopLevelAssetPath AssetPath
//                        0x008  FName PackageName  (8 bytes)
//                        0x010  FName AssetName    (8 bytes)
//                 0x018  FUtf8String SubPathString (16 bytes)
//   0x028  bool bIsSelected
//   0x029  bool bIsFocused
//   0x02A  bool bIsNew
struct ItemDataLayout
{
    static constexpr size_t kItemData    = 0x340;
    static constexpr size_t kWeakPtr     = kItemData + 0x00;  // FWeakObjectPtr (8B)
    static constexpr size_t kPackageName = kItemData + 0x08;  // FName (8B)
    static constexpr size_t kAssetName   = kItemData + 0x10;  // FName (8B)
    static constexpr size_t kSubPathData = kItemData + 0x18;  // char* (UTF-8)
    static constexpr size_t kSubPathNum  = kItemData + 0x20;
    static constexpr size_t kSubPathMax  = kItemData + 0x24;
    static constexpr size_t kBIsSelected = kItemData + 0x28;
    static constexpr size_t kBIsFocused  = kItemData + 0x29;
    static constexpr size_t kBIsNew      = kItemData + 0x2A;
    static constexpr size_t kSize        = 0x30;   // full ItemData struct
};

// UR5BuildingGroupWidget::BuildingItems @ +0x350 (Dumper-7 R5_classes.hpp).
static constexpr size_t kBuildingItemsOffset = 0x350;

// FGameplayTag offset on UR5BuildingItem (Dumper-7: UR5BuildingItem @ 0x0048).
static constexpr size_t kBuildingItemTagOffset = 0x48;

// ----- Tiny SEH-guarded read helpers ----------------------------------------
// Header-inline so they compile in every TU without LTO. The cost is a few
// extra bytes of code per call site; the benefit is no symbol pollution and
// no extra .cpp file just for two 4-line helpers.

// Best-effort UObject->Class->Name resolver. On any failure writes "".
static inline void TryResolveContextClassName(QmUE::UObject* ctx, char* out, int outCap)
{
    if (out && outCap > 0) out[0] = '\0';
    if (!ctx || !out || outCap <= 0) return;
    __try
    {
        if (!ctx->Class) return;
        QmUE::ResolveFNameNarrow(ctx->Class->Name, out, outCap);
    }
    __except (EXCEPTION_EXECUTE_HANDLER) { out[0] = '\0'; }
}

// Read a TArray header from `at`. Returns 0 on success, -1 on SEH fault.
static inline int SafeReadTArrayHeader(void* at, QmUE::FTArrayHeader* out)
{
    if (!at || !out) return -1;
    __try { *out = *reinterpret_cast<QmUE::FTArrayHeader*>(at); return 0; }
    __except (EXCEPTION_EXECUTE_HANDLER) { return -1; }
}

// Hydrate a UE WeakObjectPtr's ObjectIndex via GObjects. Returns null if the
// index is invalid or out of bounds.
static inline QmUE::UObject* ResolveWeakObjectPtr(int32_t objectIndex)
{
    if (objectIndex <= 0) return nullptr;
    if (!QmUE::IsReady()) return nullptr;
    QmUE::TUObjectArray* arr = QmUE::GetGObjects();
    if (objectIndex >= arr->Num()) return nullptr;
    return arr->GetByIndex(objectIndex);
}
