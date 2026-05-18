// Quartermaster diagnostic inspectors - impl. See qm_diag.hpp.
// All code in this TU is gated on QM_DIAG so a production build links cleanly
// with an empty object file.

#define _CRT_SECURE_NO_WARNINGS
#include "qm_diag.hpp"

#if QM_DIAG

#include <windows.h>
#include <stdint.h>
#include <stdio.h>
#include <string.h>

#include "qm_ue.hpp"
#include "qm_state.hpp"
#include "qm_inject.hpp"

// ----- Local helper: hex-dump len bytes from addr, grouped by 8 -------------
static void DiagHexDump(const void* addr, int len, char* out, size_t outSize)
{
    if (!addr || !out || outSize == 0) { if (out && outSize > 0) out[0] = '\0'; return; }
    const uint8_t* p = reinterpret_cast<const uint8_t*>(addr);
    size_t cursor = 0;
    for (int i = 0; i < len && cursor + 4 < outSize; ++i)
    {
        uint8_t b = 0;
        __try { b = p[i]; } __except (EXCEPTION_EXECUTE_HANDLER) { snprintf(out + cursor, outSize - cursor, "<FAULT@%d>", i); return; }
        cursor += snprintf(out + cursor, outSize - cursor, "%02X", b);
        if ((i & 7) == 7 && i + 1 < len)
            cursor += snprintf(out + cursor, outSize - cursor, " ");
    }
}

// ============================================================================
// Inspect the params block of GetBuildingGroupsByCategoryTag (CategoryTag +
// SelectedBrush). All SEH-guarded - the call path may pack params differently.
// Logs BOTH reference- and value-style reads so we can confirm which path
// produces a real FName per call. With Stack != null also hex-dumps the first
// 0x60 bytes of the FFrame and 0x30 bytes of the params block itself.
// ============================================================================
void DiagInspectInputs(void* Result, void* Stack)
{
    if (!Result) { QM_LOG_DEBUG("[Inspect]   inputs: Result is null - skipping"); return; }

    uint8_t* paramsBase = reinterpret_cast<uint8_t*>(Result) - 0x10;

    // Reference-style read.
    QmUE::FGameplayTag* refPtr = nullptr;
    QmUE::FGameplayTag refTag = {};
    bool refDerefOk = false;
    __try { refPtr = *reinterpret_cast<QmUE::FGameplayTag**>(paramsBase + 0x00); }
    __except (EXCEPTION_EXECUTE_HANDLER) {}
    if (refPtr) { __try { refTag = *refPtr; refDerefOk = true; } __except (EXCEPTION_EXECUTE_HANDLER) {} }
    char refStr[256] = "<no-ref>";
    if (refDerefOk)
    {
        if (!QmUE::ResolveFNameNarrow(refTag, refStr, sizeof(refStr)))
            snprintf(refStr, sizeof(refStr), "<unresolved cmp=%d num=%u>", refTag.ComparisonIndex, refTag.Number);
    }
    else if (refPtr)
        snprintf(refStr, sizeof(refStr), "<FAULT deref ptr=0x%p>", refPtr);

    // Value-style read.
    QmUE::FGameplayTag valTag = {};
    bool valRead = false;
    __try { valTag = *reinterpret_cast<QmUE::FGameplayTag*>(paramsBase + 0x00); valRead = true; }
    __except (EXCEPTION_EXECUTE_HANDLER) {}
    char valStr[256] = "<no-tag>";
    if (valRead)
    {
        if (!QmUE::ResolveFNameNarrow(valTag, valStr, sizeof(valStr)))
            snprintf(valStr, sizeof(valStr), "<unresolved cmp=%d num=%u>", valTag.ComparisonIndex, valTag.Number);
    }
    else
        snprintf(valStr, sizeof(valStr), "<FAULT reading params@0x%p>", paramsBase);

    QmUE::UObject* brush = nullptr;
    bool brushRead = false;
    __try { brush = *reinterpret_cast<QmUE::UObject**>(paramsBase + 0x08); brushRead = true; }
    __except (EXCEPTION_EXECUTE_HANDLER) {}

    char brushCls[128] = "";
    char brushName[128] = "";
    if (brushRead && brush)
    {
        TryResolveContextClassName(brush, brushCls, sizeof(brushCls));
        __try { QmUE::ResolveFNameNarrow(brush->Name, brushName, sizeof(brushName)); }
        __except (EXCEPTION_EXECUTE_HANDLER) {}
    }

    QM_LOG_DEBUG("[Inspect]   in: CategoryTag(ref)='%s' ptr=0x%p / CategoryTag(val)='%s' / SelectedBrush=0x%p Cls='%s' Name='%s'",
        refStr, refPtr, valStr, brush,
        brushCls[0] ? brushCls : (brush ? "<?>" : "null"),
        brushName[0] ? brushName : (brush ? "<?>" : ""));

    // Hex-dump diagnostics - help locate CategoryTag when neither ref nor val
    // produces a real FName. Compares paramsBase against FFrame.Locals which
    // some UE5 call paths use as the real param storage.
    char hexParams[200] = "";
    DiagHexDump(paramsBase, 0x30, hexParams, sizeof(hexParams));
    QM_LOG_DEBUG("[Inspect]   raw params@0x%p [0x00..0x2F]: %s", paramsBase, hexParams);

    if (Stack)
    {
        char hexStack[300] = "";
        DiagHexDump(Stack, 0x60, hexStack, sizeof(hexStack));
        QM_LOG_DEBUG("[Inspect]   raw FFrame@0x%p [0x00..0x5F]: %s", Stack, hexStack);

        // FFrame.Locals @ +0x28 (UE5 layout: FOutputDevice 0x10 + Node 0x10 + Object 0x18 + Code 0x20 + Locals 0x28)
        void* locals = nullptr;
        __try { locals = *reinterpret_cast<void**>(reinterpret_cast<uint8_t*>(Stack) + 0x28); }
        __except (EXCEPTION_EXECUTE_HANDLER) {}
        if (locals && locals != paramsBase)
        {
            char hexLocals[200] = "";
            DiagHexDump(locals, 0x30, hexLocals, sizeof(hexLocals));
            QM_LOG_DEBUG("[Inspect]   FFrame.Locals=0x%p [0x00..0x2F]: %s", locals, hexLocals);

            // Try reading CategoryTag from Locals as a value (BP-VM canonical layout)
            QmUE::FGameplayTag locTag = {};
            bool locOk = false;
            __try { locTag = *reinterpret_cast<QmUE::FGameplayTag*>(locals); locOk = true; }
            __except (EXCEPTION_EXECUTE_HANDLER) {}
            char locStr[256] = "<no-locals>";
            if (locOk)
            {
                if (!locTag.IsNone())
                {
                    if (!QmUE::ResolveFNameNarrow(locTag, locStr, sizeof(locStr)))
                        snprintf(locStr, sizeof(locStr), "<unresolved cmp=%d num=%u>", locTag.ComparisonIndex, locTag.Number);
                }
                else strcpy(locStr, "<none>");
            }
            QM_LOG_DEBUG("[Inspect]   CategoryTag(from Locals)='%s'", locStr);
        }
        else if (locals == paramsBase)
        {
            QM_LOG_DEBUG("[Inspect]   FFrame.Locals == paramsBase (same buffer)");
        }
    }
}

// ============================================================================
// Inspect one ItemWidget's ItemData (SoftObjectPath + bools + WeakPtr).
// ============================================================================
static void DiagInspectOneItemWidget(QmUE::UObject* item, int groupIdx, int itemIdx)
{
    if (!item) { QM_LOG_DEBUG("[SoftPath] [G%d.I%d] item=null", groupIdx, itemIdx); return; }

    uint8_t* w = reinterpret_cast<uint8_t*>(item);

    char widgetCls[128] = "";
    char widgetName[128] = "";
    TryResolveContextClassName(item, widgetCls, sizeof(widgetCls));
    __try { QmUE::ResolveFNameNarrow(item->Name, widgetName, sizeof(widgetName)); }
    __except (EXCEPTION_EXECUTE_HANDLER) {}

    int32_t weakIdx = 0, weakSerial = 0;
    QmUE::FName pkgName = {}, assetName = {};
    char* subData = nullptr; int32_t subNum = 0, subMax = 0;
    uint8_t bSelected = 0, bFocused = 0, bNew = 0;
    bool readOK = true;

    __try
    {
        weakIdx    = *reinterpret_cast<int32_t*>(w + ItemDataLayout::kWeakPtr);
        weakSerial = *reinterpret_cast<int32_t*>(w + ItemDataLayout::kWeakPtr + 4);
        pkgName    = *reinterpret_cast<QmUE::FName*>(w + ItemDataLayout::kPackageName);
        assetName  = *reinterpret_cast<QmUE::FName*>(w + ItemDataLayout::kAssetName);
        subData    = *reinterpret_cast<char**>(w + ItemDataLayout::kSubPathData);
        subNum     = *reinterpret_cast<int32_t*>(w + ItemDataLayout::kSubPathNum);
        subMax     = *reinterpret_cast<int32_t*>(w + ItemDataLayout::kSubPathMax);
        bSelected  = *reinterpret_cast<uint8_t*>(w + ItemDataLayout::kBIsSelected);
        bFocused   = *reinterpret_cast<uint8_t*>(w + ItemDataLayout::kBIsFocused);
        bNew       = *reinterpret_cast<uint8_t*>(w + ItemDataLayout::kBIsNew);
    }
    __except (EXCEPTION_EXECUTE_HANDLER) { readOK = false; }
    (void)subMax;

    if (!readOK)
    {
        QM_LOG_WARN("[SoftPath] [G%d.I%d] widget=0x%p Cls='%s' Name='%s' <FAULT reading ItemData @ +0x340>",
            groupIdx, itemIdx, item,
            widgetCls[0] ? widgetCls : "<?>", widgetName[0] ? widgetName : "<?>");
        return;
    }

    char pkgStr[256] = "<unresolved>";
    char assetStr[256] = "<unresolved>";
    if (!pkgName.IsNone())
    {
        if (!QmUE::ResolveFNameNarrow(pkgName, pkgStr, sizeof(pkgStr)))
            snprintf(pkgStr, sizeof(pkgStr), "<unresolved cmp=%d num=%u>", pkgName.ComparisonIndex, pkgName.Number);
    }
    else strcpy(pkgStr, "<None>");
    if (!assetName.IsNone())
    {
        if (!QmUE::ResolveFNameNarrow(assetName, assetStr, sizeof(assetStr)))
            snprintf(assetStr, sizeof(assetStr), "<unresolved cmp=%d num=%u>", assetName.ComparisonIndex, assetName.Number);
    }
    else strcpy(assetStr, "<None>");

    char subStr[256] = "";
    bool subReadOK = true;
    if (subData && subNum > 0)
    {
        int copy = subNum;
        if (copy > (int)sizeof(subStr) - 1) copy = (int)sizeof(subStr) - 1;
        __try { memcpy(subStr, subData, copy); subStr[copy] = '\0'; }
        __except (EXCEPTION_EXECUTE_HANDLER) { subReadOK = false; }
    }

    QmUE::UObject* hydrated = ResolveWeakObjectPtr(weakIdx);
    char hydratedCls[128] = "";
    char hydratedName[128] = "";
    if (hydrated)
    {
        TryResolveContextClassName(hydrated, hydratedCls, sizeof(hydratedCls));
        __try { QmUE::ResolveFNameNarrow(hydrated->Name, hydratedName, sizeof(hydratedName)); }
        __except (EXCEPTION_EXECUTE_HANDLER) {}
    }

    QM_LOG_DEBUG("[SoftPath] [G%d.I%d] widget=0x%p Cls='%s' Name='%s'",
        groupIdx, itemIdx, item,
        widgetCls[0] ? widgetCls : "<?>", widgetName[0] ? widgetName : "<?>");
    QM_LOG_DEBUG("[SoftPath]   Pkg='%s' Asset='%s'%s%s",
        pkgStr, assetStr,
        (subNum > 0) ? " SubPath='" : "",
        (subNum > 0) ? (subReadOK ? subStr : "<FAULT>") : "");
    QM_LOG_DEBUG("[SoftPath]   WeakPtr={idx=%d serial=%d} hydrated=0x%p Cls='%s' Name='%s' flags={sel=%u focus=%u new=%u}",
        weakIdx, weakSerial, hydrated,
        hydrated ? (hydratedCls[0] ? hydratedCls : "<?>") : "",
        hydrated ? (hydratedName[0] ? hydratedName : "<?>") : "",
        bSelected, bFocused, bNew);
}

// ============================================================================
// Dump the Groups TArray returned by GetBuildingGroupsByCategoryTag. With
// deep=true also dumps each Group's BuildingItems TArray header.
// ============================================================================
void DiagInspectGroupResult(void* Result, bool deep)
{
    if (!Result) { QM_LOG_DEBUG("[Inspect]   Result=null"); return; }

    QmUE::FTArrayHeader hdr = {};
    if (SafeReadTArrayHeader(Result, &hdr) != 0)
    {
        QM_LOG_WARN("[Inspect]   FAULT reading TArray header at Result=0x%p", Result);
        return;
    }

    QM_LOG_DEBUG("[Inspect]   ReturnValue TArray Data=0x%p Num=%d Max=%d", hdr.Data, hdr.Num, hdr.Max);
    if (!deep || !hdr.Data || hdr.Num <= 0) return;

    int dumpN = hdr.Num;
    if (dumpN > 40) { QM_LOG_DEBUG("[Inspect]   (clamping enumeration to first 40 of %d)", hdr.Num); dumpN = 40; }

    QmUE::UObject** widgets = reinterpret_cast<QmUE::UObject**>(hdr.Data);
    char clsName[128];
    char selfName[128];
    for (int i = 0; i < dumpN; ++i)
    {
        QmUE::UObject* w = nullptr;
        __try { w = widgets[i]; }
        __except (EXCEPTION_EXECUTE_HANDLER) { QM_LOG_WARN("[Inspect]   [%d] FAULT", i); continue; }
        if (!w) { QM_LOG_DEBUG("[Inspect]   [%d] null", i); continue; }

        clsName[0] = '\0';
        selfName[0] = '\0';
        TryResolveContextClassName(w, clsName, sizeof(clsName));
        __try { QmUE::ResolveFNameNarrow(w->Name, selfName, sizeof(selfName)); }
        __except (EXCEPTION_EXECUTE_HANDLER) {}

        int itemsNum = -1, itemsMax = -1;
        void* itemsData = nullptr;
        __try
        {
            QmUE::FTArrayHeader* items = reinterpret_cast<QmUE::FTArrayHeader*>(
                reinterpret_cast<uint8_t*>(w) + kBuildingItemsOffset);
            itemsData = items->Data; itemsNum = items->Num; itemsMax = items->Max;
        }
        __except (EXCEPTION_EXECUTE_HANDLER) { itemsNum = -2; }

        QM_LOG_DEBUG("[Inspect]   [%d] Widget=0x%p Cls='%s' Name='%s' Items={Data=0x%p Num=%d Max=%d}",
            i, w, clsName[0] ? clsName : "<?>", selfName[0] ? selfName : "<?>",
            itemsData, itemsNum, itemsMax);
    }
}

// ============================================================================
// Top-level recon: dump SoftPath info for the first 3 items in Groups[0].
// ============================================================================
void DiagInspectFirstGroupSoftPaths(void* Result)
{
    if (!Result) return;

    QmUE::FTArrayHeader grpHdr = {};
    if (SafeReadTArrayHeader(Result, &grpHdr) != 0)
    { QM_LOG_WARN("[SoftPath] FAULT reading group TArray"); return; }

    if (!grpHdr.Data || grpHdr.Num < 1)
    { QM_LOG_DEBUG("[SoftPath] no groups to inspect (Num=%d)", grpHdr.Num); return; }

    QmUE::UObject* group0 = nullptr;
    __try { group0 = reinterpret_cast<QmUE::UObject**>(grpHdr.Data)[0]; }
    __except (EXCEPTION_EXECUTE_HANDLER) { QM_LOG_WARN("[SoftPath] FAULT reading group[0] pointer"); return; }
    if (!group0) { QM_LOG_DEBUG("[SoftPath] group[0]=null"); return; }

    QmUE::FTArrayHeader itemsHdr = {};
    if (SafeReadTArrayHeader(reinterpret_cast<uint8_t*>(group0) + kBuildingItemsOffset, &itemsHdr) != 0)
    { QM_LOG_WARN("[SoftPath] FAULT reading items TArray @ group+0x350"); return; }

    QM_LOG_DEBUG("[SoftPath] Group=0x%p Items={Data=0x%p Num=%d Max=%d} - inspecting up to 3 items",
        group0, itemsHdr.Data, itemsHdr.Num, itemsHdr.Max);
    if (!itemsHdr.Data || itemsHdr.Num < 1) return;

    int dumpN = itemsHdr.Num > 3 ? 3 : itemsHdr.Num;
    for (int i = 0; i < dumpN; ++i)
    {
        QmUE::UObject* item = nullptr;
        __try { item = reinterpret_cast<QmUE::UObject**>(itemsHdr.Data)[i]; }
        __except (EXCEPTION_EXECUTE_HANDLER) { QM_LOG_WARN("[SoftPath] [G0.I%d] FAULT reading slot", i); continue; }
        DiagInspectOneItemWidget(item, 0, i);
    }
}

// ============================================================================
// Walk GObjects for any UFunction matching `funcName`.
// ============================================================================
int DiagFindUFunctionsByName(const char* funcName, int maxLog)
{
    using namespace QmUE;
    if (!IsReady()) return 0;

    TUObjectArray* arr = GetGObjects();
    const int32 total = arr->Num();
    int hits = 0;
    char nameBuf[256]; char outerBuf[256];

    for (int32 i = 0; i < total; ++i)
    {
        UObject* obj = arr->GetByIndex(i);
        if (!obj || !obj->Class) continue;
        if ((obj->Class->CastFlags & CASTFLAG_Function) == 0) continue;
        if (!ResolveFNameNarrow(obj->Name, nameBuf, sizeof(nameBuf))) continue;
        if (strcmp(nameBuf, funcName) != 0) continue;

        hits++;
        if (hits <= maxLog)
        {
            UFunction* fn = reinterpret_cast<UFunction*>(obj);
            const char* outerName = "<no-outer>";
            if (obj->Outer && ResolveFNameNarrow(obj->Outer->Name, outerBuf, sizeof(outerBuf)))
                outerName = outerBuf;
            QM_LOG_DEBUG("[UE]   diag hit #%d UFunction '%s' @ 0x%p (idx=%d) Outer='%s' ExecFn=0x%p Flags=0x%08X",
                hits, nameBuf, fn, obj->Index, outerName, (void*)fn->ExecFunction, fn->FunctionFlags);
        }
    }
    return hits;
}

// ============================================================================
// Dump raw bytes around 0x40 of a UClass for layout verification.
// ============================================================================
void DiagDumpClassBytes(QmUE::UClass* cls, const char* label)
{
    if (!cls) return;
    const uint8_t* p = reinterpret_cast<const uint8_t*>(cls);
    char hex[256]; int n = 0;
    for (int i = 0; i < 0x20; ++i)
        n += snprintf(hex + n, sizeof(hex) - n, "%02X ", p[0x40 + i]);
    QM_LOG_DEBUG("[UE] %s raw[0x40..0x5F]: %s", label, hex);
    void* superStruct = *reinterpret_cast<void* const*>(p + 0x40);
    void* children    = *reinterpret_cast<void* const*>(p + 0x48);
    void* childProps  = *reinterpret_cast<void* const*>(p + 0x50);
    QM_LOG_DEBUG("[UE] %s   Super@0x40 = 0x%p  Children@0x48 = 0x%p  ChildProps@0x50 = 0x%p",
        label, superStruct, children, childProps);
}

#endif // QM_DIAG
