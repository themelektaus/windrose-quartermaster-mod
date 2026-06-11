// Data-driven panel content: qm_modtab_layout.json (in the Quartermaster sidecar folder next
// to the DLL) is a JSON array of row
// objects the panel build renders top-to-bottom. The file is re-read whenever its write time
// changes (live-editable between settings opens, no game restart); a missing or unusable file
// falls back to the compiled-in default below, so the panel can never come up empty.
//
// Row schema (unknown keys are skipped for forward-compat):
//   type      "text" (default) | "header" | "button" | "modifications" | "itemDropdown"
//   text      row label (UTF-8); "{version}" expands to the Quartermaster version
//   size      font size (text rows + header TextBlock fallback)
//   color     "#RRGGBB" or "#RRGGBBAA", linear RGB / 255
//   wrap      true -> auto-wrap (text rows)
//   gap       vertical space above the row
//   align     "fill" | "left" | "center" | "right" (slot default: fill)
//   command   buttons: action id, dispatched by DispatchButtonCommand (qm_modtab.cpp)
//   arguments buttons: argument array; only the first entry is used today
//
// "modifications" rows expand into text rows from qm_modtab_mods.txt in the sidecar folder -
// the pre-merged installed-mods file the Configurator regenerates on every profile build/
// delete (empty and '#' lines are skipped, order is rendered verbatim). Flush-left lines are
// mod NAME rows; lines with leading whitespace are DETAIL rows of the mod above them. "text"
// acts as the name-row template ("{name}" -> the line; default: the plain name, no bullet).
// Both generated row kinds style via optional keys on the modifications row:
//   titleSize / titleColor / titleGap   name rows (default: the row's own size/color/gap)
//   textSize / textColor / textGap      detail rows (default: name size - 2, dim gray, 0)
//   textIndent                          detail rows' left indent (default 18; details always wrap)
// The file's write time is re-checked on every panel build, so a GUI build/delete shows up on
// the next settings open without a game restart.
//
// "itemDropdown" rows render as a ComboBoxString over qm_modtab_items.txt in the sidecar
// folder - the Configurator's pre-built item catalog ("<AssetId>|<Display name>" per line,
// pre-sorted; '#' lines are comments). Loaded mtime-watched like the mods file; the catalog
// is exposed to the build + the click dispatch via GetItemOption*(). With no usable catalog
// the row degrades to a fixed notice text row, so the dropdown can never come up dead.

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
#include "qm_version.h"

using namespace ModTab;

namespace
{
    // Compiled-in fallback - kept in sync with the shipped qm_modtab_layout.json.
    constexpr const char* kDefaultLayoutJson = R"json([
  { "type": "header", "text": "Quartermaster", "size": 26, "color": "#FFCC59", "gap": 4 },
  { "type": "text",   "text": "v{version} - Developed by TheMelekTaus", "size": 14, "color": "#AA9966", "wrap": true, "gap": 4 },
  { "type": "text",   "text": "Configurable mods and tweaks - built with the Quartermaster Configurator. Thank you for using Quartermaster!", "size": 16, "color": "#C7C7C7", "wrap": true, "gap": 12 },
  { "type": "button", "text": "Leave an Endorsement", "command": "open_url", "arguments": ["https://www.nexusmods.com/windrose/mods/375"], "gap": 24, "align": "left" },
  { "type": "header", "text": "Item Spawner", "size": 20, "color": "#FFCC59", "gap": 24 },
  { "type": "itemDropdown", "gap": 8 },
  { "type": "button", "text": "Add Item", "command": "add_selected_item", "arguments": ["1"], "align": "left", "gap": 8 },
  { "type": "header", "text": "Active Modifications", "size": 20, "color": "#FFCC59", "gap": 24 },
  { "type": "modifications", "gap": 4, "titleSize": 18, "titleColor": "#EBE09E", "titleGap": 16, "textSize": 14, "textColor": "#9A9A9A", "textGap": 2 },
  { "type": "text",   "text": "", "gap": 24 }
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
        float        indent   = 0.0f;
        std::string  command;
        std::string  argument;

        // Per-kind style template (only read off a kRowMods row): title* styles the generated
        // mod-name rows, text* the detail rows beneath them. titleGap uses -1 as "unset" (falls
        // back to the row's own gap) so an explicit 0 stays expressible.
        float        titleSize     = 0.0f;
        bool         hasTitleColor = false;
        float        titleColor[4] = { 1.0f, 1.0f, 1.0f, 1.0f };
        float        titleGap      = -1.0f;
        float        textSize      = 0.0f;
        bool         hasTextColor  = false;
        float        textColor[4]  = { 1.0f, 1.0f, 1.0f, 1.0f };
        float        textGap       = 0.0f;
        float        textIndent    = 18.0f;
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
                         : (v == "itemDropdown")  ? kRowItemDropdown
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
            else if (key == "titleColor" || key == "textColor")
            {
                std::string v;
                if (!jp.parseString(v)) return false;
                bool&  has = (key == "titleColor") ? out.hasTitleColor : out.hasTextColor;
                float* dst = (key == "titleColor") ? out.titleColor    : out.textColor;
                has = ParseColor(v, dst);
                if (!has)
                    QM_LOG_WARN("[ModTab] layout: bad %s '%s' (want #RRGGBB or #RRGGBBAA) - ignored",
                                key.c_str(), v.c_str());
            }
            else if (key == "titleSize" || key == "titleGap" || key == "textSize" ||
                     key == "textGap"   || key == "textIndent")
            {
                double v = 0.0;
                if (!jp.parseNumber(v)) return false;
                if      (key == "titleSize") out.titleSize  = (float)v;
                else if (key == "titleGap")  out.titleGap   = (float)v;
                else if (key == "textSize")  out.textSize   = (float)v;
                else if (key == "textGap")   out.textGap    = (float)v;
                else                         out.textIndent = (float)v;
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

    std::vector<RowStorage> g_modRows;        // expanded per-mod rows (own their strings)
    bool                    g_modsLoadedOnce = false;
    bool                    g_modsWasThere   = false;
    FILETIME                g_modsLastWrite  = {};

    // Text rows from qm_modtab_mods.txt; the kRowMods row acts as the styling + text template.
    // Flush-left lines are mod names (styled by the title* keys), indented lines are detail
    // rows of the mod above (rendered verbatim, styled by the text* keys). Empty and '#' lines
    // are skipped, order is rendered verbatim (the Configurator writes the file pre-merged and
    // pre-sorted). A fixed notice row when the file is missing or lists nothing.
    void ExpandModRows(const char* path, const RowStorage& tpl)
    {
        g_modRows.clear();

        struct ModLine { std::wstring text; bool detail; };
        std::vector<ModLine> entries;
        std::string body;
        if (path && QmJson::ReadWholeFile(path, body))
        {
            QmJson::StripUtf8Bom(body);
            for (size_t pos = 0; pos < body.size();)
            {
                size_t eol = body.find('\n', pos);
                size_t len = (eol == std::string::npos ? body.size() : eol) - pos;
                std::string line = body.substr(pos, len);
                pos += len + 1;

                size_t b = line.find_first_not_of(" \t\r");
                if (b == std::string::npos || line[b] == '#') continue;
                size_t e = line.find_last_not_of(" \t\r");
                entries.push_back({ QmJson::Utf8ToWide(line.substr(b, e - b + 1)), b > 0 });
            }
        }

        const std::wstring nameTpl = tpl.text.empty() ? std::wstring(L"{name}") : tpl.text;
        const float defTextColor[4] = { 0.55f, 0.55f, 0.55f, 1.0f };
        const float nameSize = tpl.titleSize > 0.0f ? tpl.titleSize : tpl.size;
        for (size_t i = 0; i < entries.size(); ++i)
        {
            RowStorage r = tpl;
            r.type = kRowText;
            if (entries[i].detail)
            {
                r.text   = entries[i].text;
                r.size   = tpl.textSize > 0.0f ? tpl.textSize
                         : nameSize     > 0.0f ? nameSize - 2.0f : 0.0f;
                r.gap    = tpl.textGap;
                r.indent = tpl.textIndent;
                r.wrap   = true;
                if (tpl.hasTextColor) memcpy(r.color, tpl.textColor, sizeof(r.color));
                else                  memcpy(r.color, defTextColor,  sizeof(r.color));
                r.hasColor = true;
            }
            else
            {
                r.text = nameTpl;
                r.size = nameSize;
                if (tpl.titleGap >= 0.0f) r.gap = tpl.titleGap;
                if (tpl.hasTitleColor)
                {
                    memcpy(r.color, tpl.titleColor, sizeof(r.color));
                    r.hasColor = true;
                }
                for (size_t pos = r.text.find(L"{name}"); pos != std::wstring::npos;
                     pos = r.text.find(L"{name}", pos))
                {
                    r.text.replace(pos, sizeof("{name}") - 1, entries[i].text);
                    pos += entries[i].text.size();
                }
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

    // ---- "itemDropdown" row backing data -------------------------------------------------------

    struct ItemOption { std::string key; std::wstring name; };
    std::vector<ItemOption> g_itemOptions;
    bool                    g_itemsLoadedOnce = false;
    bool                    g_itemsWasThere   = false;
    FILETIME                g_itemsLastWrite  = {};

    // "<AssetId>|<Display name>" per line (the Configurator writes the file pre-sorted by
    // display name); '#' and empty lines are skipped, a line without '|' is key-only.
    void LoadItemCatalog(const char* path)
    {
        g_itemOptions.clear();
        std::string body;
        if (!path || !QmJson::ReadWholeFile(path, body)) return;
        QmJson::StripUtf8Bom(body);
        for (size_t pos = 0; pos < body.size();)
        {
            size_t eol = body.find('\n', pos);
            size_t len = (eol == std::string::npos ? body.size() : eol) - pos;
            std::string line = body.substr(pos, len);
            pos += len + 1;

            size_t b = line.find_first_not_of(" \t\r");
            if (b == std::string::npos || line[b] == '#') continue;
            size_t e = line.find_last_not_of(" \t\r");
            line = line.substr(b, e - b + 1);

            ItemOption opt;
            size_t sep = line.find('|');
            opt.key  = (sep == std::string::npos) ? line : line.substr(0, sep);
            opt.name = QmJson::Utf8ToWide(sep == std::string::npos ? line : line.substr(sep + 1));
            if (!opt.key.empty() && !opt.name.empty())
                g_itemOptions.push_back(static_cast<ItemOption&&>(opt));
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
            r.indent   = s.indent;
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
            if (g_storage[i].type == kRowItemDropdown && g_itemOptions.empty())
            {
                // No usable catalog - a fixed notice instead of a dead dropdown.
                PanelRow r  = viewOf(g_storage[i]);
                r.type      = kRowText;
                r.text      = L"Item catalog not found - build a profile in the Quartermaster Configurator.";
                r.wrap      = true;
                g_view.push_back(r);
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

        // The mods file is checked on EVERY call (one per panel build): the Configurator
        // regenerates qm_modtab_mods.txt independently of the layout file when it builds or
        // deletes a mod while the game runs.
        const RowStorage* modsTpl = nullptr;
        for (size_t i = 0; i < g_storage.size() && !modsTpl; ++i)
            if (g_storage[i].type == kRowMods) modsTpl = &g_storage[i];

        bool modsChanged = false;
        if (modsTpl)
        {
            char modsPath[MAX_PATH] = { 0 };
            WIN32_FILE_ATTRIBUTE_DATA mfad = {};
            bool modsThere = havePath &&
                             snprintf(modsPath, sizeof(modsPath), "%s\\qm_modtab_mods.txt", dir) > 0 &&
                             GetFileAttributesExA(modsPath, GetFileExInfoStandard, &mfad) &&
                             !(mfad.dwFileAttributes & FILE_ATTRIBUTE_DIRECTORY);
            bool modsStale = stale || !g_modsLoadedOnce ||
                             modsThere != g_modsWasThere ||
                             (modsThere && CompareFileTime(&mfad.ftLastWriteTime, &g_modsLastWrite) != 0);
            if (modsStale)
            {
                g_modsLoadedOnce = true;
                g_modsWasThere   = modsThere;
                if (modsThere) g_modsLastWrite = mfad.ftLastWriteTime;
                ExpandModRows(modsThere ? modsPath : nullptr, *modsTpl);
                modsChanged = true;
            }
        }
        else if (!g_modRows.empty()) { g_modRows.clear(); modsChanged = true; }

        // Same per-build re-check for the item catalog (regenerated by Configurator builds).
        bool haveDropdown = false;
        for (size_t i = 0; i < g_storage.size() && !haveDropdown; ++i)
            if (g_storage[i].type == kRowItemDropdown) haveDropdown = true;

        bool itemsChanged = false;
        if (haveDropdown)
        {
            char itemsPath[MAX_PATH] = { 0 };
            WIN32_FILE_ATTRIBUTE_DATA ifad = {};
            bool itemsThere = havePath &&
                              snprintf(itemsPath, sizeof(itemsPath), "%s\\qm_modtab_items.txt", dir) > 0 &&
                              GetFileAttributesExA(itemsPath, GetFileExInfoStandard, &ifad) &&
                              !(ifad.dwFileAttributes & FILE_ATTRIBUTE_DIRECTORY);
            bool itemsStale = stale || !g_itemsLoadedOnce ||
                              itemsThere != g_itemsWasThere ||
                              (itemsThere && CompareFileTime(&ifad.ftLastWriteTime, &g_itemsLastWrite) != 0);
            if (itemsStale)
            {
                g_itemsLoadedOnce = true;
                g_itemsWasThere   = itemsThere;
                if (itemsThere) g_itemsLastWrite = ifad.ftLastWriteTime;
                LoadItemCatalog(itemsThere ? itemsPath : nullptr);
                itemsChanged = true;
                QM_LOG_INFO("[ModTab] item catalog: %d item(s) from %s",
                            (int)g_itemOptions.size(), itemsThere ? itemsPath : "(missing file)");
            }
        }
        else if (!g_itemOptions.empty()) { g_itemOptions.clear(); itemsChanged = true; }

        if (stale || modsChanged || itemsChanged)
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

    int GetItemOptionCount()
    {
        return (int)g_itemOptions.size();
    }

    const wchar_t* GetItemOptionName(int idx)
    {
        if (idx < 0 || idx >= (int)g_itemOptions.size()) return nullptr;
        return g_itemOptions[(size_t)idx].name.c_str();
    }

    const char* GetItemOptionKey(int idx)
    {
        if (idx < 0 || idx >= (int)g_itemOptions.size()) return nullptr;
        return g_itemOptions[(size_t)idx].key.c_str();
    }
}
