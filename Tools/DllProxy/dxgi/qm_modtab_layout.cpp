// Data-driven panel content: qm_modtab_layout.json (in the Quartermaster sidecar folder next
// to the DLL) is a JSON array of row
// objects the panel build renders top-to-bottom. The file is re-read whenever its write time
// changes (live-editable between settings opens, no game restart); a missing or unusable file
// falls back to the compiled-in default below, so the panel can never come up empty.
//
// Row schema (unknown keys are skipped for forward-compat):
//   type      "text" (default) | "header" | "button" | "modifications"
//   text      row label (UTF-8); "{version}" expands to the Quartermaster version
//   size      font size (text rows + header TextBlock fallback)
//   color     "#RRGGBB" or "#RRGGBBAA", linear RGB / 255
//   wrap      true -> auto-wrap (text rows)
//   gap       vertical space above the row
//   align     "fill" | "left" | "center" | "right" (slot default: fill)
//   command   buttons: action id, currently "open_url"
//   arguments buttons: argument array; only the first entry is used today
//
// "modifications" rows expand into one text row per qm_profile_*.json in the sidecar folder
// (one per installed mod, sorted by name). "text" acts as the per-mod template ("{name}" ->
// profile name; default: bullet + name), the styling keys apply to every generated row. The
// expansion re-runs whenever the profile set or any profile's write time changes, so a GUI
// build/delete shows up on the next settings open without a game restart.

#define _CRT_SECURE_NO_WARNINGS
#include <windows.h>
#include <stdint.h>
#include <stdio.h>
#include <string.h>
#include <algorithm>
#include <string>
#include <vector>

#include "qm_modtab_internal.hpp"
#include "qm_json.hpp"
#include "qm_log.hpp"
#include "qm_version.h"

using namespace ModTab;

namespace
{
    // Compiled-in fallback - kept in sync with the shipped qm_modtab_layout.json.
    constexpr const char* kDefaultLayoutJson = R"json([
  { "type": "header", "text": "Quartermaster", "size": 26, "color": "#FFCC59", "gap": 96 },
  { "type": "text",   "text": "v{version} - Developed by TheMelekTaus", "size": 14, "color": "#AA9966", "wrap": true, "gap": 4 },
  { "type": "text",   "text": "Configurable mods and tweaks - built with the Quartermaster Configurator.", "size": 16, "color": "#C7C7C7", "wrap": true, "gap": 16 },
  { "type": "header", "text": "Active Modifications", "size": 20, "color": "#FFCC59", "gap": 32 },
  { "type": "modifications", "gap": 4 },
  { "type": "button", "text": "Visit nexusmods.com", "command": "open_url", "arguments": ["https://www.nexusmods.com/windrose/mods/375"], "gap": 32, "align": "left" }
])json";

    struct RowStorage
    {
        int          type     = kRowText;
        std::wstring text;
        float        size     = 0.0f;
        bool         hasColor = false;
        float        color[4] = { 1.0f, 1.0f, 1.0f, 1.0f };
        bool         wrap     = false;
        float        gap      = 0.0f;
        uint8_t      halign   = 255;
        std::string  command;
        std::string  argument;
    };

    std::vector<RowStorage> g_storage;   // owns the strings
    std::vector<PanelRow>   g_view;      // pointer view handed to the build
    bool                    g_loadedOnce   = false;
    bool                    g_fileWasThere = false;
    bool                    g_fromFile     = false;
    FILETIME                g_lastWrite    = {};

    // "#RRGGBB" / "#RRGGBBAA" -> linear RGBA 0..1 (plain /255, no gamma).
    bool ParseColor(const std::string& s, float out[4])
    {
        size_t n = s.size();
        if ((n != 7 && n != 9) || s[0] != '#') return false;
        uint8_t bytes[4] = { 0, 0, 0, 255 };
        for (size_t i = 1; i < n; i += 2)
        {
            int hi = -1, lo = -1;
            for (int k = 0; k < 2; ++k)
            {
                char c = s[i + k];
                int v = (c >= '0' && c <= '9') ? c - '0'
                      : (c >= 'a' && c <= 'f') ? c - 'a' + 10
                      : (c >= 'A' && c <= 'F') ? c - 'A' + 10 : -1;
                if (k == 0) hi = v; else lo = v;
            }
            if (hi < 0 || lo < 0) return false;
            bytes[(i - 1) / 2] = (uint8_t)(hi * 16 + lo);
        }
        for (int i = 0; i < 4; ++i) out[i] = bytes[i] / 255.0f;
        return true;
    }

    // One row object. Unknown keys are skipped; wrong-typed known keys abort the parse (the
    // caller then falls back to the compiled-in default and logs).
    bool ParseRow(QmJson::Parser& jp, RowStorage& out)
    {
        if (!jp.expect('{')) return false;
        if (jp.peek('}')) { ++jp.p; return true; }
        for (;;)
        {
            std::string key;
            if (!jp.parseString(key)) return false;
            if (!jp.expect(':'))      return false;

            if (key == "type")
            {
                std::string v;
                if (!jp.parseString(v)) return false;
                out.type = (v == "header")        ? kRowHeader
                         : (v == "button")        ? kRowButton
                         : (v == "modifications") ? kRowMods
                                                  : kRowText;
            }
            else if (key == "text")
            {
                std::string v;
                if (!jp.parseString(v)) return false;
                for (size_t pos = v.find("{version}"); pos != std::string::npos; pos = v.find("{version}", pos))
                    v.replace(pos, sizeof("{version}") - 1, QM_VERSION_STR);
                out.text = QmJson::Utf8ToWide(v);
            }
            else if (key == "color")
            {
                std::string v;
                if (!jp.parseString(v)) return false;
                out.hasColor = ParseColor(v, out.color);
                if (!out.hasColor)
                    QM_LOG_WARN("[ModTab] layout: bad color '%s' (want #RRGGBB or #RRGGBBAA) - ignored", v.c_str());
            }
            else if (key == "align")
            {
                std::string v;
                if (!jp.parseString(v)) return false;
                out.halign = (v == "fill")   ? (uint8_t)0
                           : (v == "left")   ? (uint8_t)1
                           : (v == "center") ? (uint8_t)2
                           : (v == "right")  ? (uint8_t)3 : (uint8_t)255;
            }
            else if (key == "command")
            {
                if (!jp.parseString(out.command)) return false;
            }
            else if (key == "arguments")
            {
                if (!jp.expect('[')) return false;
                bool first = true;
                if (!jp.peek(']'))
                    for (;;)
                    {
                        jp.skipWs();
                        if (jp.p < jp.end && *jp.p == '"')
                        {
                            std::string v;
                            if (!jp.parseString(v)) return false;
                            if (first) { out.argument = v; first = false; }
                        }
                        else if (!jp.skipValue()) return false;
                        if (jp.peek(',')) { ++jp.p; continue; }
                        break;
                    }
                if (!jp.expect(']')) return false;
            }
            else if (key == "size" || key == "gap")
            {
                double v = 0.0;
                if (!jp.parseNumber(v)) return false;
                if (key == "size") out.size = (float)v; else out.gap = (float)v;
            }
            else if (key == "wrap")
            {
                if (!jp.parseBool(out.wrap)) return false;
            }
            else
            {
                if (!jp.skipValue()) return false;
            }

            if (jp.peek(',')) { ++jp.p; continue; }
            if (jp.peek('}')) { ++jp.p; return true; }
            jp.ok = false; jp.lastError = "expected ',' or '}' in row object"; return false;
        }
    }

    // Top level: a bare row array (the documented shape); an object with a "rows" array is
    // accepted too.
    bool ParseLayout(const char* data, size_t len, std::vector<RowStorage>& out)
    {
        QmJson::Parser jp(data, len);
        jp.skipWs();
        if (jp.p < jp.end && *jp.p == '{')
        {
            if (!jp.expect('{')) return false;
            for (;;)
            {
                std::string key;
                if (!jp.parseString(key)) return false;
                if (!jp.expect(':'))      return false;
                if (key == "rows") break;
                if (!jp.skipValue())      return false;
                if (jp.peek(',')) { ++jp.p; continue; }
                jp.ok = false; jp.lastError = "no \"rows\" array"; return false;
            }
        }
        if (!jp.expect('[')) return false;
        if (!jp.peek(']'))
            for (;;)
            {
                RowStorage row;
                if (!ParseRow(jp, row)) return false;
                out.push_back(static_cast<RowStorage&&>(row));
                if (jp.peek(',')) { ++jp.p; continue; }
                break;
            }
        return jp.expect(']');
    }

    // ---- "modifications" row expansion --------------------------------------------------------

    std::vector<RowStorage> g_modRows;       // expanded per-profile rows (own their strings)
    uint64_t                g_modsSig = 0;   // change signature of the qm_profile_*.json set

    // Top-level "name" of a profile JSON.
    bool ParseProfileName(const char* data, size_t len, std::string& out)
    {
        QmJson::Parser jp(data, len);
        if (!jp.expect('{')) return false;
        if (jp.peek('}'))    return false;
        for (;;)
        {
            std::string key;
            if (!jp.parseString(key)) return false;
            if (!jp.expect(':'))      return false;
            if (key == "name")        return jp.parseString(out) && !out.empty();
            if (!jp.skipValue())      return false;
            if (jp.peek(',')) { ++jp.p; continue; }
            return false;
        }
    }

    // Lists the qm_profile_*.json file names and returns an FNV-1a signature over names + write
    // times, so the expansion only re-runs when the installed-mod set actually changed.
    uint64_t ScanProfiles(const char* dir, std::vector<std::string>& outFiles)
    {
        uint64_t h = 1469598103934665603ull;
        auto mix = [&h](const void* p, size_t n)
        {
            const uint8_t* b = (const uint8_t*)p;
            for (size_t i = 0; i < n; ++i) { h ^= b[i]; h *= 1099511628211ull; }
        };

        char pattern[MAX_PATH];
        if (snprintf(pattern, sizeof(pattern), "%s\\qm_profile_*.json", dir) <= 0) return h;
        WIN32_FIND_DATAA fd;
        HANDLE find = FindFirstFileA(pattern, &fd);
        if (find == INVALID_HANDLE_VALUE) return h;
        do
        {
            if (fd.dwFileAttributes & FILE_ATTRIBUTE_DIRECTORY) continue;
            mix(fd.cFileName, strlen(fd.cFileName));
            mix(&fd.ftLastWriteTime, sizeof(fd.ftLastWriteTime));
            outFiles.push_back(fd.cFileName);
        } while (FindNextFileA(find, &fd));
        FindClose(find);
        return h;
    }

    // One text row per profile, sorted by display name; the kRowMods row acts as the styling +
    // text template. A fixed notice row when no profile is installed.
    void ExpandModRows(const char* dir, const std::vector<std::string>& files, const RowStorage& tpl)
    {
        g_modRows.clear();

        std::vector<std::wstring> names;
        for (size_t i = 0; i < files.size(); ++i)
        {
            std::string name;
            char path[MAX_PATH];
            std::string body;
            if (snprintf(path, sizeof(path), "%s\\%s", dir, files[i].c_str()) > 0 &&
                QmJson::ReadWholeFile(path, body))
            {
                QmJson::StripUtf8Bom(body);
                if (!ParseProfileName(body.data(), body.size(), name)) name.clear();
            }
            if (name.empty())
            {
                // Fallback: the file name stem, qm_profile_<name>.json.
                name = files[i];
                constexpr size_t kPre = sizeof("qm_profile_") - 1, kExt = sizeof(".json") - 1;
                if (name.size() > kPre + kExt) name = name.substr(kPre, name.size() - kPre - kExt);
            }
            names.push_back(QmJson::Utf8ToWide(name));
        }
        std::sort(names.begin(), names.end(),
                  [](const std::wstring& a, const std::wstring& b)
                  { return _wcsicmp(a.c_str(), b.c_str()) < 0; });

        const std::wstring tplText = tpl.text.empty() ? std::wstring(L"\x2022 {name}") : tpl.text;
        for (size_t i = 0; i < names.size(); ++i)
        {
            RowStorage r = tpl;
            r.type = kRowText;
            r.text = tplText;
            for (size_t pos = r.text.find(L"{name}"); pos != std::wstring::npos;
                 pos = r.text.find(L"{name}", pos))
            {
                r.text.replace(pos, sizeof("{name}") - 1, names[i]);
                pos += names[i].size();
            }
            g_modRows.push_back(static_cast<RowStorage&&>(r));
        }
        if (g_modRows.empty())
        {
            RowStorage r = tpl;
            r.type = kRowText;
            r.text = L"No active modifications - build a profile in the Quartermaster Configurator.";
            g_modRows.push_back(static_cast<RowStorage&&>(r));
        }
    }

    void RebuildView()
    {
        auto viewOf = [](const RowStorage& s)
        {
            PanelRow r;
            r.type     = s.type;
            r.text     = s.text.c_str();
            r.size     = s.size;
            r.color    = s.hasColor ? s.color : nullptr;
            r.wrap     = s.wrap;
            r.gap      = s.gap;
            r.halign   = s.halign;
            r.command  = (s.type == kRowButton && !s.command.empty())  ? s.command.c_str()  : nullptr;
            r.argument = (s.type == kRowButton && !s.argument.empty()) ? s.argument.c_str() : nullptr;
            return r;
        };

        g_view.clear();
        g_view.reserve(g_storage.size() + g_modRows.size());
        for (size_t i = 0; i < g_storage.size(); ++i)
        {
            if (g_storage[i].type == kRowMods)
            {
                for (size_t m = 0; m < g_modRows.size(); ++m)
                    g_view.push_back(viewOf(g_modRows[m]));
                continue;
            }
            g_view.push_back(viewOf(g_storage[i]));
        }
    }
}

namespace ModTab
{
    const PanelRow* GetPanelLayout(int* outCount)
    {
        char path[MAX_PATH] = { 0 };
        char dir[MAX_PATH];
        bool havePath = LocateSidecarDir(dir, sizeof(dir)) &&
                        snprintf(path, sizeof(path), "%s\\qm_modtab_layout.json", dir) > 0;

        WIN32_FILE_ATTRIBUTE_DATA fad = {};
        bool fileThere = havePath &&
                         GetFileAttributesExA(path, GetFileExInfoStandard, &fad) &&
                         !(fad.dwFileAttributes & FILE_ATTRIBUTE_DIRECTORY);

        bool stale = !g_loadedOnce ||
                     fileThere != g_fileWasThere ||
                     (fileThere && CompareFileTime(&fad.ftLastWriteTime, &g_lastWrite) != 0);
        if (stale)
        {
            g_loadedOnce   = true;
            g_fileWasThere = fileThere;
            if (fileThere) g_lastWrite = fad.ftLastWriteTime;

            std::vector<RowStorage> rows;
            bool fromFile = false;
            if (fileThere)
            {
                std::string body;
                if (QmJson::ReadWholeFile(path, body))
                {
                    QmJson::StripUtf8Bom(body);
                    if (ParseLayout(body.data(), body.size(), rows) && !rows.empty())
                        fromFile = true;
                    else
                        QM_LOG_WARN("[ModTab] layout: %s unusable (parse error or zero rows) - "
                                    "using the compiled-in default", path);
                }
                else QM_LOG_WARN("[ModTab] layout: cannot read %s - using the compiled-in default", path);
            }
            if (!fromFile)
            {
                rows.clear();
                ParseLayout(kDefaultLayoutJson, strlen(kDefaultLayoutJson), rows);
            }
            g_storage      = static_cast<std::vector<RowStorage>&&>(rows);
            g_fromFile     = fromFile;
        }

        // The mods expansion is checked on EVERY call (one per panel build): the profile set
        // changes independently of the layout file when the Configurator builds or deletes a mod
        // while the game runs.
        const RowStorage* modsTpl = nullptr;
        for (size_t i = 0; i < g_storage.size() && !modsTpl; ++i)
            if (g_storage[i].type == kRowMods) modsTpl = &g_storage[i];

        bool modsChanged = false;
        if (modsTpl)
        {
            std::vector<std::string> files;
            uint64_t sig = havePath ? ScanProfiles(dir, files) : 0;
            if (stale || sig != g_modsSig)
            {
                g_modsSig = sig;
                ExpandModRows(dir, files, *modsTpl);
                modsChanged = true;
            }
        }
        else if (!g_modRows.empty()) { g_modRows.clear(); modsChanged = true; }

        if (stale || modsChanged)
        {
            RebuildView();
            if (modsTpl)
                QM_LOG_INFO("[ModTab] layout: %d row(s) from %s (%d mod row(s))",
                            (int)g_view.size(), g_fromFile ? path : "the compiled-in default",
                            (int)g_modRows.size());
            else
                QM_LOG_INFO("[ModTab] layout: %d row(s) from %s",
                            (int)g_view.size(), g_fromFile ? path : "the compiled-in default");
        }

        if (outCount) *outCount = (int)g_view.size();
        return g_view.empty() ? nullptr : g_view.data();
    }
}
