// Quartermaster injectable-item config - runtime JSON loader.
// -----------------------------------------------------------
// Reads qm_items.json from the directory containing this DLL at startup.
// The GUI ("Build" button) writes that file when deploying a profile, so the
// item list can change without rebuilding the DLL. Storage is owned here:
// the InjectableItem rows handed out to the rest of the DLL reference c_str()
// pointers into the file-static std::string/std::wstring vectors below, which
// live for the lifetime of the DLL.
//
// JSON format (flat, only strings - no numbers or bools needed):
//   {
//     "tabPurityFilter": "BuildingDecoration",
//     "items": [
//       {
//         "name":                    "QmPainting_01",
//         "className":               "R5BuildingItem",
//         "assetName":               "DA_BI_QmPainting_01",
//         "packagePath":             "/Game/Quartermaster/Items/DA_BI_QmPainting_01",
//         "targetCategorySubstring": "BuildingDecoration"
//       }
//     ]
//   }
//
// Missing fields default to empty / null. "targetCategorySubstring" empty ->
// nullptr (match-all). "tabPurityFilter" empty -> nullptr (gate disabled).
//
// We use a small hand-rolled JSON-subset parser instead of a third-party
// header library to keep the DLL self-contained and the binary tiny.

#define _CRT_SECURE_NO_WARNINGS
#include <windows.h>
#include <stdio.h>
#include <string.h>
#include <string>
#include <vector>

#include "qm_config.hpp"
#include "qm_log.hpp"

// ---------------------------------------------------------------------------
// Public (variable) exports - all start out empty. QmConfigLoad() populates.
// ---------------------------------------------------------------------------
const InjectableItem* g_injectableItems     = nullptr;
int                   g_injectableItemCount = 0;
const char*           kTabPurityFilterSubstring = nullptr;

// ---------------------------------------------------------------------------
// Owned storage. Strings outlive the view because they are file-static.
// ---------------------------------------------------------------------------
namespace {

struct ItemStorage
{
    std::string  name;
    std::string  className;
    std::string  assetName;
    std::wstring packagePathW;
    std::wstring assetNameW;
    std::string  targetCategorySubstring;
};

std::vector<ItemStorage>    g_storage;     // owns the strings
std::vector<InjectableItem> g_view;        // c_str()-pointer view exposed to callers
std::string                 g_tabFilter;   // owns kTabPurityFilterSubstring backing

// ---------------------------------------------------------------------------
// UTF-8 (narrow) -> UTF-16 (wide). Used to populate packagePathW / assetNameW.
// ---------------------------------------------------------------------------
std::wstring Utf8ToWide(const std::string& s)
{
    if (s.empty()) return std::wstring();
    int len = MultiByteToWideChar(CP_UTF8, 0, s.c_str(), (int)s.size(), nullptr, 0);
    if (len <= 0) return std::wstring();
    std::wstring out((size_t)len, L'\0');
    MultiByteToWideChar(CP_UTF8, 0, s.c_str(), (int)s.size(), &out[0], len);
    return out;
}

// ---------------------------------------------------------------------------
// Tiny JSON-subset parser. Supports: objects, arrays, "strings" with the
// common escapes (\" \\ \/ \n \t \r). No numbers, bools, nulls, no unicode
// \uXXXX escapes. That is enough for our config shape and keeps the parser
// short enough to audit at a glance.
// ---------------------------------------------------------------------------
struct JsonParser
{
    const char* p;
    const char* end;
    bool        ok = true;
    const char* lastError = nullptr;

    JsonParser(const char* data, size_t len) : p(data), end(data + len) {}

    void skipWs()
    {
        while (p < end)
        {
            char c = *p;
            if (c == ' ' || c == '\t' || c == '\n' || c == '\r') { ++p; continue; }
            // Line comments are not standard JSON but very handy for hand-edited
            // configs. Treat // ... \n as whitespace.
            if (c == '/' && p + 1 < end && p[1] == '/')
            {
                p += 2;
                while (p < end && *p != '\n') ++p;
                continue;
            }
            break;
        }
    }

    bool peek(char c) { skipWs(); return p < end && *p == c; }

    bool expect(char c)
    {
        skipWs();
        if (p < end && *p == c) { ++p; return true; }
        ok = false;
        lastError = "unexpected character";
        return false;
    }

    bool parseString(std::string& out)
    {
        out.clear();
        skipWs();
        if (p >= end || *p != '"') { ok = false; lastError = "expected '\"'"; return false; }
        ++p;
        while (p < end && *p != '"')
        {
            if (*p == '\\' && p + 1 < end)
            {
                ++p;
                switch (*p)
                {
                    case '"':  out.push_back('"');  break;
                    case '\\': out.push_back('\\'); break;
                    case '/':  out.push_back('/');  break;
                    case 'n':  out.push_back('\n'); break;
                    case 't':  out.push_back('\t'); break;
                    case 'r':  out.push_back('\r'); break;
                    case 'b':  out.push_back('\b'); break;
                    case 'f':  out.push_back('\f'); break;
                    default:   out.push_back(*p);   break;  // tolerant
                }
                ++p;
            }
            else
            {
                out.push_back(*p++);
            }
        }
        if (p >= end) { ok = false; lastError = "unterminated string"; return false; }
        ++p;  // closing "
        return true;
    }

    // Skip the value at the current cursor regardless of its type. Used to
    // tolerate unknown keys without aborting the whole parse.
    bool skipValue()
    {
        skipWs();
        if (p >= end) { ok = false; lastError = "unexpected EOF"; return false; }
        char c = *p;
        if (c == '"') { std::string dummy; return parseString(dummy); }
        if (c == '{') return skipObject();
        if (c == '[') return skipArray();
        // Scalar literal (number / true / false / null): scan until separator.
        while (p < end && *p != ',' && *p != '}' && *p != ']' &&
               *p != ' ' && *p != '\t' && *p != '\n' && *p != '\r')
            ++p;
        return true;
    }

    bool skipObject()
    {
        if (!expect('{')) return false;
        skipWs();
        if (peek('}')) { ++p; return true; }
        for (;;)
        {
            std::string k;
            if (!parseString(k)) return false;
            if (!expect(':'))    return false;
            if (!skipValue())    return false;
            skipWs();
            if (peek(',')) { ++p; continue; }
            if (peek('}')) { ++p; return true; }
            ok = false; lastError = "expected ',' or '}'"; return false;
        }
    }

    bool skipArray()
    {
        if (!expect('[')) return false;
        skipWs();
        if (peek(']')) { ++p; return true; }
        for (;;)
        {
            if (!skipValue()) return false;
            skipWs();
            if (peek(',')) { ++p; continue; }
            if (peek(']')) { ++p; return true; }
            ok = false; lastError = "expected ',' or ']'"; return false;
        }
    }
};

// Parse a single item-object: { "name": "...", "assetName": "...", ... }.
// Unknown keys are silently skipped to keep forward-compat with the GUI side.
bool ParseItemObject(JsonParser& jp, ItemStorage& out)
{
    if (!jp.expect('{')) return false;
    if (jp.peek('}')) { ++jp.p; return true; }
    for (;;)
    {
        std::string key;
        if (!jp.parseString(key)) return false;
        if (!jp.expect(':'))      return false;

        // All known fields are strings. Unknown keys are skipped via skipValue.
        jp.skipWs();
        if (jp.p < jp.end && *jp.p == '"')
        {
            std::string val;
            if (!jp.parseString(val)) return false;

            if      (key == "name")                    out.name = val;
            else if (key == "className")               out.className = val;
            else if (key == "assetName")               out.assetName = val;
            else if (key == "packagePath")             out.packagePathW = Utf8ToWide(val);
            else if (key == "targetCategorySubstring") out.targetCategorySubstring = val;
            // "packagePathW" and "assetNameW" would be redundant - we derive
            // them via Utf8ToWide(packagePath / assetName).
        }
        else
        {
            if (!jp.skipValue()) return false;
        }

        if (jp.peek(',')) { ++jp.p; continue; }
        if (jp.peek('}')) { ++jp.p; break; }
        jp.ok = false; jp.lastError = "expected ',' or '}' in item object"; return false;
    }

    // Wide asset name is always derived from the narrow form.
    if (!out.assetName.empty()) out.assetNameW = Utf8ToWide(out.assetName);
    return true;
}

// Parse the top-level object: { "tabPurityFilter": "...", "items": [ ... ] }.
bool ParseRoot(JsonParser& jp, std::string& tabFilterOut, std::vector<ItemStorage>& itemsOut)
{
    if (!jp.expect('{')) return false;
    if (jp.peek('}')) { ++jp.p; return true; }
    for (;;)
    {
        std::string key;
        if (!jp.parseString(key)) return false;
        if (!jp.expect(':'))      return false;

        if (key == "tabPurityFilter")
        {
            jp.skipWs();
            if (jp.p < jp.end && *jp.p == '"')
            {
                if (!jp.parseString(tabFilterOut)) return false;
            }
            else
            {
                if (!jp.skipValue()) return false;  // tolerate null / unknown
            }
        }
        else if (key == "items")
        {
            if (!jp.expect('[')) return false;
            if (!jp.peek(']'))
            {
                for (;;)
                {
                    ItemStorage it;
                    if (!ParseItemObject(jp, it)) return false;
                    if (!it.name.empty() && !it.packagePathW.empty())
                        itemsOut.push_back(std::move(it));
                    if (jp.peek(',')) { ++jp.p; continue; }
                    if (jp.peek(']')) break;
                    jp.ok = false; jp.lastError = "expected ',' or ']' in items array"; return false;
                }
            }
            if (!jp.expect(']')) return false;
        }
        else
        {
            if (!jp.skipValue()) return false;
        }

        if (jp.peek(',')) { ++jp.p; continue; }
        if (jp.peek('}')) { ++jp.p; break; }
        jp.ok = false; jp.lastError = "expected ',' or '}' at top level"; return false;
    }
    return true;
}

// ---------------------------------------------------------------------------
// Disk I/O and self-locating helpers.
// ---------------------------------------------------------------------------
bool LocateConfigPath(char* out, size_t outSz)
{
    if (!out || outSz == 0) return false;

    // Use the address of QmConfigLoad to locate our own module - more robust
    // than GetModuleHandleA("dxgi.dll") which also matches dxgi_org.dll on
    // some host process layouts.
    HMODULE self = nullptr;
    if (!GetModuleHandleExA(
            GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS | GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT,
            (LPCSTR)&QmConfigLoad, &self) || !self)
    {
        QM_LOG_WARN("[Config] GetModuleHandleEx failed (gle=%lu) - cannot locate qm_items.json",
                    GetLastError());
        return false;
    }

    char dllPath[MAX_PATH];
    DWORD n = GetModuleFileNameA(self, dllPath, sizeof(dllPath));
    if (n == 0 || n >= sizeof(dllPath))
    {
        QM_LOG_WARN("[Config] GetModuleFileName failed (gle=%lu)", GetLastError());
        return false;
    }

    char* lastSep = strrchr(dllPath, '\\');
    if (!lastSep)
    {
        QM_LOG_WARN("[Config] DLL path has no directory separator: '%s'", dllPath);
        return false;
    }
    *lastSep = '\0';

    int written = snprintf(out, outSz, "%s\\qm_items.json", dllPath);
    if (written <= 0 || (size_t)written >= outSz) return false;
    return true;
}

bool ReadWholeFile(const char* path, std::string& out)
{
    FILE* f = fopen(path, "rb");
    if (!f) return false;
    if (fseek(f, 0, SEEK_END) != 0) { fclose(f); return false; }
    long sz = ftell(f);
    if (sz < 0) { fclose(f); return false; }
    if (fseek(f, 0, SEEK_SET) != 0) { fclose(f); return false; }
    out.resize((size_t)sz);
    size_t read = sz > 0 ? fread(&out[0], 1, (size_t)sz, f) : 0;
    fclose(f);
    out.resize(read);
    return true;
}

// Strip a UTF-8 BOM (EF BB BF) if the file started with one. Some editors
// (Notepad on older Windows builds) insert it on Save-As.
void StripUtf8Bom(std::string& s)
{
    if (s.size() >= 3 &&
        (unsigned char)s[0] == 0xEF &&
        (unsigned char)s[1] == 0xBB &&
        (unsigned char)s[2] == 0xBF)
    {
        s.erase(0, 3);
    }
}

// After parse: build the c_str()-pointer view from owned storage. Called even
// when the storage is empty (then view is empty too and exports are zeroed).
void RebuildView()
{
    g_view.clear();
    g_view.reserve(g_storage.size());
    for (size_t i = 0; i < g_storage.size(); ++i)
    {
        const ItemStorage& s = g_storage[i];
        InjectableItem it;
        it.name                    = s.name.c_str();
        it.className               = s.className.c_str();
        it.assetName               = s.assetName.c_str();
        it.packagePathW            = s.packagePathW.c_str();
        it.assetNameW              = s.assetNameW.c_str();
        it.targetCategorySubstring = s.targetCategorySubstring.empty()
                                     ? nullptr
                                     : s.targetCategorySubstring.c_str();
        g_view.push_back(it);
    }
    g_injectableItems         = g_view.empty() ? nullptr : g_view.data();
    g_injectableItemCount     = (int)g_view.size();
    kTabPurityFilterSubstring = g_tabFilter.empty() ? nullptr : g_tabFilter.c_str();
}

} // namespace

// ---------------------------------------------------------------------------
// Public entry points.
// ---------------------------------------------------------------------------
bool QmConfigLoad()
{
    // Reset to empty so a reload doesn't show stale state if parse fails.
    g_storage.clear();
    g_tabFilter.clear();
    RebuildView();

    char path[MAX_PATH];
    if (!LocateConfigPath(path, sizeof(path)))
        return false;

    QM_LOG_INFO("[Config] looking for %s", path);

    std::string body;
    if (!ReadWholeFile(path, body))
    {
        QM_LOG_INFO("[Config] file not present - no items will be injected (DLL stays idle)");
        return true;   // not-an-error: GUI may not have deployed yet
    }
    StripUtf8Bom(body);

    JsonParser jp(body.data(), body.size());
    std::string tabFilter;
    std::vector<ItemStorage> items;
    bool parsed = ParseRoot(jp, tabFilter, items);
    if (!parsed || !jp.ok)
    {
        long offset = (long)(jp.p - body.data());
        QM_LOG_ERROR("[Config] parse error: %s at byte offset %ld - injecting nothing",
                     jp.lastError ? jp.lastError : "unknown", offset);
        return false;
    }

    g_storage   = std::move(items);
    g_tabFilter = std::move(tabFilter);
    RebuildView();

    QM_LOG_INFO("[Config] loaded %d item(s), tabPurityFilter='%s'",
                g_injectableItemCount,
                kTabPurityFilterSubstring ? kTabPurityFilterSubstring : "<disabled>");
    for (int i = 0; i < g_injectableItemCount; ++i)
    {
        const InjectableItem& it = g_injectableItems[i];
        QM_LOG_INFO("[Config]   item[%d] name='%s' class='%s' asset='%s' pkg='%ls' target='%s'",
                    i, it.name, it.className, it.assetName, it.packagePathW,
                    it.targetCategorySubstring ? it.targetCategorySubstring : "<match-all>");
    }
    return true;
}

void QmConfigUnload()
{
    g_storage.clear();
    g_view.clear();
    g_tabFilter.clear();
    g_injectableItems         = nullptr;
    g_injectableItemCount     = 0;
    kTabPurityFilterSubstring = nullptr;
}
