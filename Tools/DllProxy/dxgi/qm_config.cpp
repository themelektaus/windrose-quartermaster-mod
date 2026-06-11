// Quartermaster injectable-item config - runtime JSON loader.
// -----------------------------------------------------------
// Scans the directory containing this DLL for qm_items_*.json files at
// startup, parses each, and merges their items into one in-memory list.
// One file per deployed Quartermaster profile - the GUI ("Build" button)
// writes the per-profile file (qm_items_<profile>.json), deleting a pak
// in the mods tab deletes the matching JSON too, and CleanupGame() removes
// all of them. So the item list can change without rebuilding the DLL.
// Storage is owned here: the InjectableItem rows handed out to the rest of
// the DLL reference c_str() pointers into the file-static std::string /
// std::wstring vectors below, which live for the lifetime of the DLL.
//
// tabPurityFilter (a top-level string in each file) controls which build-
// menu tab the inject targets. If multiple files set conflicting values
// the first one (sorted by filename) wins and the conflict is logged -
// in practice all our deployed profiles target "BuildingBrushes" so the
// conflict path is never hit.
//
// JSON format (flat, only strings - no numbers or bools needed):
//   {
//     "tabPurityFilter": "BuildingBrushes",
//     "items": [
//       {
//         "name":                    "QmBldg_a1b2c3d4",
//         "className":               "R5BuildingItem",
//         "assetName":               "DA_BI_QmBldg_a1b2c3d4",
//         "packagePath":             "<mod-namespace>/DA_BI_QmBldg_a1b2c3d4",
//         "targetCategorySubstring": "BuildingBrushes"
//       }
//     ]
//   }
//
// packagePath is whatever absolute UE virtual path the C# build pipeline
// emits the cooked DA at (see WindrosePaths.ModItemsPackagePath); the DLL
// treats it as an opaque string and forwards it to LoadAssetByPath.
//
// Missing fields default to empty / null. "targetCategorySubstring" empty ->
// nullptr (match-all). "tabPurityFilter" empty -> nullptr (gate disabled).
//
#define _CRT_SECURE_NO_WARNINGS
#include <windows.h>
#include <stdio.h>
#include <string.h>
#include <algorithm>
#include <string>
#include <vector>

#include "qm_config.hpp"
#include "qm_json.hpp"
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

// Shared JSON-subset toolkit (qm_json.hpp).
using JsonParser = QmJson::Parser;
using QmJson::Utf8ToWide;
using QmJson::ReadWholeFile;
using QmJson::StripUtf8Bom;

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

// Writes the directory containing this DLL into `out`. No trailing
// separator. Used as the scan-root for qm_items_*.json files.
bool LocateConfigDir(char* out, size_t outSz)
{
    if (!out || outSz == 0) return false;

    // Use the address of QmConfigLoad to locate our own module - more robust
    // than GetModuleHandleA("dxgi.dll") which could match any DLL with that
    // basename if the loader ever has two of them registered.
    HMODULE self = nullptr;
    if (!GetModuleHandleExA(
            GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS | GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT,
            (LPCSTR)&QmConfigLoad, &self) || !self)
    {
        QM_LOG_WARN("[Config] GetModuleHandleEx failed (gle=%lu) - cannot locate config dir",
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

    size_t dlen = strlen(dllPath);
    if (dlen + 1 > outSz) return false;
    memcpy(out, dllPath, dlen + 1);
    return true;
}

// Scans `dir` for files matching qm_items_*.json (top-level only) and
// returns absolute paths sorted by filename (so deterministic order across
// runs and across machines). Empty result is OK - the DLL just stays idle.
std::vector<std::string> EnumerateConfigFiles(const char* dir)
{
    std::vector<std::string> out;
    if (!dir || !*dir) return out;

    char pattern[MAX_PATH];
    int written = snprintf(pattern, sizeof(pattern), "%s\\qm_items_*.json", dir);
    if (written <= 0 || (size_t)written >= sizeof(pattern)) return out;

    WIN32_FIND_DATAA fd = {};
    HANDLE h = FindFirstFileA(pattern, &fd);
    if (h == INVALID_HANDLE_VALUE) return out;
    do
    {
        if (fd.dwFileAttributes & FILE_ATTRIBUTE_DIRECTORY) continue;
        char full[MAX_PATH];
        int w = snprintf(full, sizeof(full), "%s\\%s", dir, fd.cFileName);
        if (w <= 0 || (size_t)w >= sizeof(full)) continue;
        out.emplace_back(full);
    } while (FindNextFileA(h, &fd));
    FindClose(h);

    // Sort by filename so a build's log output is stable across runs and
    // the "first non-empty tabPurityFilter wins" rule is deterministic.
    std::sort(out.begin(), out.end());
    return out;
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
    // Reset to empty so a reload doesn't show stale state if every parse fails.
    g_storage.clear();
    g_tabFilter.clear();
    RebuildView();

    char dir[MAX_PATH];
    if (!LocateConfigDir(dir, sizeof(dir)))
        return false;

    auto files = EnumerateConfigFiles(dir);
    if (files.empty())
    {
        QM_LOG_INFO("[Config] no qm_items_*.json files in %s - DLL stays idle", dir);
        return true;   // not-an-error: GUI may not have deployed any profile yet
    }

    QM_LOG_INFO("[Config] scanning %zu file(s) under %s", files.size(), dir);

    std::vector<ItemStorage> mergedItems;
    std::string mergedTabFilter;
    std::string mergedTabFilterSource;  // filename that won the conflict, for log
    bool anyParseError = false;

    for (const auto& path : files)
    {
        const char* fname = strrchr(path.c_str(), '\\');
        fname = fname ? fname + 1 : path.c_str();

        std::string body;
        if (!ReadWholeFile(path.c_str(), body))
        {
            QM_LOG_WARN("[Config]   %s: read failed - skipping", fname);
            continue;
        }
        StripUtf8Bom(body);

        JsonParser jp(body.data(), body.size());
        std::string tabFilter;
        std::vector<ItemStorage> items;
        bool parsed = ParseRoot(jp, tabFilter, items);
        if (!parsed || !jp.ok)
        {
            long offset = (long)(jp.p - body.data());
            QM_LOG_ERROR("[Config]   %s: parse error: %s at byte offset %ld - skipping",
                         fname, jp.lastError ? jp.lastError : "unknown", offset);
            anyParseError = true;
            continue;
        }

        QM_LOG_INFO("[Config]   %s: %zu item(s), tabPurityFilter='%s'",
                    fname, items.size(),
                    tabFilter.empty() ? "<disabled>" : tabFilter.c_str());

        // First non-empty tabPurityFilter wins. If a later file has a
        // different non-empty filter we log it but keep the winner.
        if (!tabFilter.empty())
        {
            if (mergedTabFilter.empty())
            {
                mergedTabFilter       = tabFilter;
                mergedTabFilterSource = fname;
            }
            else if (mergedTabFilter != tabFilter)
            {
                QM_LOG_WARN("[Config]   %s: tabPurityFilter='%s' conflicts with '%s' from %s - keeping the first",
                            fname, tabFilter.c_str(),
                            mergedTabFilter.c_str(), mergedTabFilterSource.c_str());
            }
        }

        // Merge items. We don't dedupe by name - if two profiles ship a
        // building with the same internal name (extremely unlikely given the
        // QmBldg_<8hex> scheme) they'd both inject; the second one would
        // simply collide on the FName slot and the inject pipeline would
        // skip the duplicate at runtime.
        for (auto& it : items)
        {
            mergedItems.push_back(std::move(it));
        }
    }

    g_storage   = std::move(mergedItems);
    g_tabFilter = std::move(mergedTabFilter);
    RebuildView();

    QM_LOG_INFO("[Config] merged total %d item(s), tabPurityFilter='%s'",
                g_injectableItemCount,
                kTabPurityFilterSubstring ? kTabPurityFilterSubstring : "<disabled>");
    for (int i = 0; i < g_injectableItemCount; ++i)
    {
        const InjectableItem& it = g_injectableItems[i];
        QM_LOG_INFO("[Config]   item[%d] name='%s' class='%s' asset='%s' pkg='%ls' target='%s'",
                    i, it.name, it.className, it.assetName, it.packagePathW,
                    it.targetCategorySubstring ? it.targetCategorySubstring : "<match-all>");
    }

    // Return false only if there was a hard parse error on at least one
    // file. Empty results from "all files were silently skipped" still
    // count as success (we logged the per-file failures).
    return !anyParseError;
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
