// Data-driven panel content: qm_modtab_layout.json (next to the DLL) is a JSON array of row
// objects the panel build renders top-to-bottom. The file is re-read whenever its write time
// changes (live-editable between settings opens, no game restart); a missing or unusable file
// falls back to the compiled-in default below, so the panel can never come up empty.
//
// Row schema (unknown keys are skipped for forward-compat):
//   type      "text" (default) | "header" | "button"
//   text      row label (UTF-8)
//   size      font size (text rows + header TextBlock fallback)
//   color     "#RRGGBB" or "#RRGGBBAA", linear RGB / 255
//   wrap      true -> auto-wrap (text rows)
//   gap       vertical space above the row
//   align     "fill" | "left" | "center" | "right" (slot default: fill)
//   command   buttons: action id, currently "open_url"
//   arguments buttons: argument array; only the first entry is used today

#define _CRT_SECURE_NO_WARNINGS
#include <windows.h>
#include <stdint.h>
#include <stdio.h>
#include <string.h>
#include <string>
#include <vector>

#include "qm_modtab_internal.hpp"
#include "qm_json.hpp"
#include "qm_log.hpp"

using namespace ModTab;

namespace
{
    // Compiled-in fallback - kept in sync with the shipped qm_modtab_layout.json.
    constexpr const char* kDefaultLayoutJson = R"json([
  { "type": "header", "text": "Quartermaster", "size": 26, "color": "#FFCC59", "gap": 96 },
  { "type": "text",   "text": "Developed by TheMelekTaus", "size": 14, "color": "#AA9966", "wrap": true, "gap": 4 },
  { "type": "text",   "text": "Configurable mods and tweaks - built with the Quartermaster Configurator.", "size": 16, "color": "#C7C7C7", "wrap": true, "gap": 16 },
  { "type": "header", "text": "Active Mods", "size": 20, "color": "#FFCC59", "gap": 32 },
  { "type": "text",   "text": "Active mods will be listed here in the future", "size": 16, "wrap": true, "gap": 4 },
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
                out.type = (v == "header") ? kRowHeader
                         : (v == "button") ? kRowButton
                                           : kRowText;
            }
            else if (key == "text")
            {
                std::string v;
                if (!jp.parseString(v)) return false;
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

    void RebuildView()
    {
        g_view.clear();
        g_view.reserve(g_storage.size());
        for (size_t i = 0; i < g_storage.size(); ++i)
        {
            const RowStorage& s = g_storage[i];
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
            g_view.push_back(r);
        }
    }
}

namespace ModTab
{
    const PanelRow* GetPanelLayout(int* outCount)
    {
        char path[MAX_PATH] = { 0 };
        char dir[MAX_PATH];
        bool havePath = LocateDllDir(dir, sizeof(dir)) &&
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
            g_storage = static_cast<std::vector<RowStorage>&&>(rows);
            RebuildView();
            QM_LOG_INFO("[ModTab] layout: %d row(s) from %s", (int)g_view.size(),
                        fromFile ? path : "the compiled-in default");
        }

        if (outCount) *outCount = (int)g_view.size();
        return g_view.empty() ? nullptr : g_view.data();
    }
}
