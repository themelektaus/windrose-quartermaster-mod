// Generic SEH-guarded helpers shared by the qm_modtab_* TUs.

#define _CRT_SECURE_NO_WARNINGS
#include <windows.h>
#include <stdint.h>
#include <stdio.h>
#include <string.h>

#include "qm_modtab_internal.hpp"
#include "qm_log.hpp"

namespace
{
    // Hexdump window for TArray element buffers (enough to see struct + repetition).
    constexpr int32_t kMaxElemDump = 192;
}

namespace ModTab
{
    // Directory containing THIS DLL (no trailing sep). Anchors on a local symbol so it resolves
    // this module regardless of which loaded DLL shares the basename.
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

    // Best-effort "ClassName'ObjectName'" into a caller buffer. Safe on any pointer.
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

    // ProcessEvent param-buffer size = UFunction::ParmsSize (params + return value), NOT
    // UStruct::StructSize/PropertiesSize - the latter also covers BP local variables (e.g. a
    // parameterless BP event can still have StructSize 249 from its locals). ProcessEvent only
    // copies ParmsSize bytes in/out, so that is the correct buffer size for a synthesized call.
    int32_t ParmsSize(QmUE::UFunction* func)
    {
        int32_t sz = 0;
        __try { sz = (int32_t)func->ParmsSize; }
        __except (EXCEPTION_EXECUTE_HANDLER) { sz = 0; }
        if (sz < 0) sz = 0;
        return sz;
    }

    // Hexdump up to `cap` bytes as "+0xNN: XX XX .. | ascii" log lines, prefixed by `tag`.
    // SEH-guarded per 16-byte row so a bad page truncates cleanly.
    void HexDump(const char* tag, const uint8_t* base, int32_t cap)
    {
        if (!base || cap <= 0) { QM_LOG_INFO("[ModTab]   %s <null/empty>", tag); return; }
        for (int32_t off = 0; off < cap; off += 16)
        {
            char hex[16 * 3 + 1]; char asc[17];
            int hn = 0; int an = 0;
            bool faulted = false;
            __try
            {
                int row = (cap - off) < 16 ? (cap - off) : 16;
                for (int i = 0; i < row; ++i)
                {
                    uint8_t b = base[off + i];
                    hn += snprintf(hex + hn, sizeof(hex) - hn, "%02X ", b);
                    asc[an++] = (b >= 32 && b < 127) ? (char)b : '.';
                }
                asc[an] = '\0';
            }
            __except (EXCEPTION_EXECUTE_HANDLER) { faulted = true; }
            if (faulted) { QM_LOG_INFO("[ModTab]   %s +0x%02X: <fault>", tag, off); return; }
            QM_LOG_INFO("[ModTab]   %s +0x%02X: %-48s | %s", tag, off, hex, asc);
        }
    }

    // Scan a parms buffer for plausible TArray<T> headers ({void* Data; int32 Num; int32 Max})
    // and dump each candidate's element bytes - pins the array offset + element stride.
    void ScanForTArrays(const uint8_t* parms, int32_t size)
    {
        if (!parms || size < 16) return;
        int found = 0;
        for (int32_t o = 0; o + 16 <= size; o += 8)
        {
            void*   data = nullptr; int32_t num = 0, max = 0;
            bool ok = false;
            __try
            {
                data = *reinterpret_cast<void* const*>(parms + o);
                num  = *reinterpret_cast<const int32_t*>(parms + o + 8);
                max  = *reinterpret_cast<const int32_t*>(parms + o + 12);
                ok   = true;
            }
            __except (EXCEPTION_EXECUTE_HANDLER) { ok = false; }
            if (!ok) continue;

            const bool plausible =
                data != nullptr &&
                reinterpret_cast<uintptr_t>(data) > 0x10000 &&
                num > 0 && num <= max && max <= 4096;
            if (!plausible) continue;

            ++found;
            QM_LOG_INFO("[ModTab]   TArray-candidate @ parms+0x%02X: Data=0x%p Num=%d Max=%d "
                        "(dumping first %d bytes of the buffer; repetition reveals the element stride)",
                        o, data, num, max, kMaxElemDump);
            char etag[32];
            snprintf(etag, sizeof(etag), "elem@+0x%02X", o);
            HexDump(etag, reinterpret_cast<const uint8_t*>(data), kMaxElemDump);
        }
        if (found == 0)
            QM_LOG_INFO("[ModTab]   (no TArray-header candidate found in parms - array may be empty here or live behind a pointer)");
    }

    void* ReadPtr(const void* p)
    {
        void* v = nullptr;
        __try { v = *reinterpret_cast<void* const*>(p); }
        __except (EXCEPTION_EXECUTE_HANDLER) { v = nullptr; }
        return v;
    }

    ArrHdr ReadArrHdr(const void* p)
    {
        ArrHdr a{ nullptr, 0, 0, false };
        __try
        {
            a.data = *reinterpret_cast<void* const*>(p);
            a.num  = *reinterpret_cast<const int32_t*>(reinterpret_cast<const uint8_t*>(p) + 8);
            a.max  = *reinterpret_cast<const int32_t*>(reinterpret_cast<const uint8_t*>(p) + 12);
            a.ok   = true;
        }
        __except (EXCEPTION_EXECUTE_HANDLER) { a.ok = false; }
        return a;
    }

    // Read an FText's source string narrow. UE5.6 layout (Dumper-7 Basic.hpp):
    //   FText     { FTextData* TextData; pad8 }            size 0x10
    //   FTextData { uint8 pad[0x20]; FString TextSource; } -> string at TextData+0x20
    //   FString   { wchar_t* Data; int32 Num; int32 Max }  (Num counts the null terminator)
    // True only if a non-empty string was read. Non-ASCII bytes -> '?'.
    bool ReadFTextNarrow(const void* ftext, char* out, size_t outSz)
    {
        if (out && outSz) out[0] = '\0';
        if (!ftext || !out || outSz < 2) return false;
        __try
        {
            const uint8_t* textData = *reinterpret_cast<const uint8_t* const*>(ftext);
            if (!textData) return false;
            const QmUE::FString* src = reinterpret_cast<const QmUE::FString*>(textData + 0x20);
            const wchar_t* data = src->Data;
            int32_t        num  = src->Num;
            if (!data || num <= 0 || num > 4096) return false;
            size_t i = 0;
            for (; i + 1 < outSz && i < (size_t)num && data[i]; ++i)
                out[i] = (data[i] >= 32 && data[i] < 127) ? (char)data[i] : '?';
            out[i] = '\0';
            return i > 0;
        }
        __except (EXCEPTION_EXECUTE_HANDLER) { out[0] = '\0'; return false; }
    }
}
