// Data-driven panel content. The BASE layout is baked into the DLL at build time
// (build.bat generates qm_modtab_layout_default.h from the repo qm_modtab_layout.json), so
// it can never be missing, stale or replaced in the field. Optional user-extension files -
// qm_modtab_layout_*.json in the Quartermaster sidecar folder, each a JSON array in the
// same row schema - EXTEND it: their rows splice in at the base layout's "userLayout"
// placeholder row (appended at the bottom when no placeholder exists), files in file-name
// order. The extension files are re-read whenever their set or write times change
// (live-editable between settings opens, no game restart); an unusable file is skipped
// with a WARN, never fatal. The retired full-override qm_modtab_layout.json is ignored.
//
// Row schema (unknown keys are skipped for forward-compat):
//   type      "text" (default) | "header" | "button" | "modifications" | "itemDropdown"
//             | "categoryDropdown" | "itemSearch" | "itemCount" | "xpCount" | "attrCount"
//             | "talentCount" | "userLayout" (base layout only: splice marker, renders
//             nothing itself; inside extension files it is inert - no recursion)
//   text      row label (UTF-8); "{version}" expands to the Quartermaster version
//             (itemSearch rows: the box's hint text, default "Search...";
//             itemCount rows: the box's initial value, default "1")
//   size      font size (text rows + header TextBlock fallback)
//   color     "#RRGGBB" or "#RRGGBBAA", linear RGB / 255
//   wrap      true -> auto-wrap (text rows)
//   gap       vertical space above the row
//   align     "fill" | "left" | "center" | "right" (slot default: fill)
//   sameRow   true -> this row shares one horizontal row with the row above it (consecutive
//             sameRow rows pack left-to-right in a HorizontalBox; the lead row's gap is the
//             vertical space above the whole row, each follower's gap is its left spacing;
//             itemDropdown / categoryDropdown / itemSearch members fill the width, others
//             auto-size)
//   fill      inline-group members: Fill weight of the slot, relative to the other members
//             (e.g. 1 and 2 -> a 1:2 width split); 0 = auto-size to the content. Unset keeps
//             the type default described under sameRow
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
// folder - the Configurator's pre-built item catalog ("<AssetId>|<Display name>|<PackagePath>
// |<Category>" per line, pre-sorted; '#' lines are comments; a non-empty third field marks
// custom mod-pak items). Loaded mtime-watched like the mods file. "categoryDropdown" rows
// render the cascading category filter over the same catalog: the GetItemOption*() accessors
// are views over the ACTIVE category's slice ("All" at index 0 = whole catalog), so the item
// combo, the category poll (qm_modtab_widgets.cpp) and the click dispatch all share one
// filtered view. "itemSearch" rows render an EditableTextBox whose text is a case-insensitive
// substring filter over the item names, ANDed with the active category into the same view.
// "itemCount" rows render an EditableTextBox holding the grant count - read at click time by
// the "add_selected_item" dispatch (qm_modtab.cpp), persisted across rebuilds like the search.
// With no usable catalog the item row degrades to a fixed notice text row and the category /
// search / count rows render nothing, so the spawner can never come up dead.

#define _CRT_SECURE_NO_WARNINGS
#include <windows.h>
#include <stdint.h>
#include <stdio.h>
#include <string.h>
#include <wctype.h>
#include <string>
#include <vector>

#include "qm_modtab_internal.hpp"
#include "qm_json.hpp"
#include "qm_log.hpp"
#include "qm_version.h"
// kDefaultLayoutJson: the base layout, generated by build.bat from qm_modtab_layout.json.
#include "qm_modtab_layout_default.h"

using namespace ModTab;

namespace
{
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
        bool         sameRow  = false;
        float        fill     = -1.0f;   // -1 = unset -> type default
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

    std::vector<RowStorage> g_storage;   // base rows (compiled-in layout); owns the strings
    std::vector<PanelRow>   g_view;      // pointer view handed to the build
    bool                    g_loadedOnce = false;

    // qm_modtab_layout_*.json user-extension rows (concatenated, file-name order) and the
    // staleness signature of the file set (names + write times, re-checked per panel build).
    struct UserFileSig { std::string name; FILETIME write; };
    std::vector<RowStorage>  g_userRows;
    std::vector<UserFileSig> g_userSig;
    bool                     g_userLoadedOnce = false;
    int                      g_userFileCount  = 0;

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
                out.type = (v == "header")           ? kRowHeader
                         : (v == "button")           ? kRowButton
                         : (v == "modifications")    ? kRowMods
                         : (v == "itemDropdown")     ? kRowItemDropdown
                         : (v == "categoryDropdown") ? kRowCategoryDropdown
                         : (v == "itemSearch")       ? kRowItemSearch
                         : (v == "itemCount")        ? kRowItemCount
                         : (v == "xpCount")          ? kRowXpCount
                         : (v == "attrCount")        ? kRowAttrCount
                         : (v == "talentCount")      ? kRowTalentCount
                         : (v == "userLayout")       ? kRowUserLayout
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
            else if (key == "fill")
            {
                double v = 0.0;
                if (!jp.parseNumber(v)) return false;
                out.fill = v < 0.0 ? -1.0f : (float)v;
            }
            else if (key == "wrap")
            {
                if (!jp.parseBool(out.wrap)) return false;
            }
            else if (key == "sameRow")
            {
                if (!jp.parseBool(out.sameRow)) return false;
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

    // ---- qm_modtab_layout_*.json user extensions ----------------------------------------------

    // Enumerate the extension files in the sidecar folder, sorted by file name
    // (case-insensitive) so the splice order is deterministic across runs.
    void ScanUserLayoutFiles(const char* dir, std::vector<UserFileSig>& out)
    {
        char pattern[MAX_PATH];
        if (snprintf(pattern, sizeof(pattern), "%s\\qm_modtab_layout_*.json", dir) <= 0) return;
        WIN32_FIND_DATAA fd;
        HANDLE h = FindFirstFileA(pattern, &fd);
        if (h == INVALID_HANDLE_VALUE) return;
        do
        {
            if (fd.dwFileAttributes & FILE_ATTRIBUTE_DIRECTORY) continue;
            size_t n = strlen(fd.cFileName);   // 8.3-alias guard: the glob can over-match
            if (n < 5 || _stricmp(fd.cFileName + n - 5, ".json") != 0) continue;
            UserFileSig sig;
            sig.name  = fd.cFileName;
            sig.write = fd.ftLastWriteTime;
            out.push_back(static_cast<UserFileSig&&>(sig));
        } while (FindNextFileA(h, &fd));
        FindClose(h);
        for (size_t i = 1; i < out.size(); ++i)   // insertion sort - the set is tiny
            for (size_t k = i; k > 0 && _stricmp(out[k - 1].name.c_str(), out[k].name.c_str()) > 0; --k)
            {
                UserFileSig tmp  = static_cast<UserFileSig&&>(out[k]);
                out[k]     = static_cast<UserFileSig&&>(out[k - 1]);
                out[k - 1] = static_cast<UserFileSig&&>(tmp);
            }
    }

    // Parse every extension file into g_userRows (concatenated in file order). A file that
    // cannot be read or parsed is skipped with a WARN - the panel never fails over user input.
    void LoadUserRows(const char* dir, const std::vector<UserFileSig>& files)
    {
        g_userRows.clear();
        g_userFileCount = 0;
        for (size_t i = 0; i < files.size(); ++i)
        {
            char path[MAX_PATH];
            if (snprintf(path, sizeof(path), "%s\\%s", dir, files[i].name.c_str()) <= 0) continue;
            std::string body;
            if (!QmJson::ReadWholeFile(path, body))
            {
                QM_LOG_WARN("[ModTab] layout: cannot read %s - skipped", path);
                continue;
            }
            QmJson::StripUtf8Bom(body);
            std::vector<RowStorage> rows;
            if (!ParseLayout(body.data(), body.size(), rows))
            {
                QM_LOG_WARN("[ModTab] layout: %s unusable (parse error) - skipped", path);
                continue;
            }
            ++g_userFileCount;
            for (size_t r = 0; r < rows.size(); ++r)
            {
                if (rows[r].type == kRowUserLayout) continue;   // splice marker is base-only
                g_userRows.push_back(static_cast<RowStorage&&>(rows[r]));
            }
        }
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
            r.sameRow = false;   // mod list is always a vertical stack, never inline-grouped
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

    struct ItemOption { std::string key; std::wstring name; std::string pkg; std::string cat; };
    std::vector<ItemOption> g_itemOptions;
    bool                    g_itemsLoadedOnce = false;
    bool                    g_itemsWasThere   = false;
    FILETIME                g_itemsLastWrite  = {};

    // The cascading category filter over g_itemOptions. g_itemCategories[0] is the synthetic
    // "All"; g_itemFilter maps filtered (= item-combo option) indices to g_itemOptions
    // indices. Rebuilt by RebuildItemFilter on every catalog load and category switch; the
    // active category is remembered BY NAME so a catalog reload (Configurator rebuilt it
    // while the game runs) keeps the user's pick even when indices shift.
    struct ItemCategory { std::string key; std::wstring name; };
    std::vector<ItemCategory> g_itemCategories;
    std::vector<int>          g_itemFilter;
    int                       g_activeCategory = 0;

    // The search filter (kRowItemSearch): raw text re-seeds a fresh box across panel
    // rebuilds, the lowercased copy drives the match. ANDed with the active category.
    std::wstring g_itemSearchRaw;
    std::wstring g_itemSearchLc;

    // The grant count (kRowItemCount): persisted text only - parsed at click time by the
    // dispatch, never touches the filter. Empty = never touched (box seeds its row default).
    std::wstring g_itemCountRaw;

    // The XP amount (kRowXpCount): same lifecycle as the grant count.
    std::wstring g_xpCountRaw;

    // The attribute/talent point amounts (kRowAttrCount / kRowTalentCount): same lifecycle;
    // the text may be negative (the grant lowers the free pool, clamped to >= 0).
    std::wstring g_attrCountRaw;
    std::wstring g_talentCountRaw;

    bool NameMatchesSearch(const std::wstring& name)
    {
        if (g_itemSearchLc.empty()) return true;
        if (name.size() < g_itemSearchLc.size()) return false;
        // Case-insensitive substring (towlower per char; identity for CJK - exact match there).
        const size_t last = name.size() - g_itemSearchLc.size();
        for (size_t at = 0; at <= last; ++at)
        {
            size_t k = 0;
            while (k < g_itemSearchLc.size()
                   && (wchar_t)towlower(name[at + k]) == g_itemSearchLc[k]) ++k;
            if (k == g_itemSearchLc.size()) return true;
        }
        return false;
    }

    // Stable display order for the known Configurator groups; categories the file carries
    // beyond these (forward-compat) append behind in first-appearance order.
    const char* const kCategoryOrder[] = { "Weapons", "Armor", "Jewelry", "Tools", "Consumables",
                                           "Resources", "Trading", "Ship", "Recipes", "Misc",
                                           "Custom" };

    void RebuildItemFilter()
    {
        g_itemFilter.clear();
        if (g_itemOptions.empty()) { g_activeCategory = 0; return; }
        if (g_activeCategory < 0 || g_activeCategory >= (int)g_itemCategories.size())
            g_activeCategory = 0;
        const std::string* want = (g_activeCategory > 0)
            ? &g_itemCategories[(size_t)g_activeCategory].key : nullptr;
        g_itemFilter.reserve(g_itemOptions.size());
        for (size_t i = 0; i < g_itemOptions.size(); ++i)
            if ((!want || g_itemOptions[i].cat == *want) && NameMatchesSearch(g_itemOptions[i].name))
                g_itemFilter.push_back((int)i);
    }

    void RebuildItemCategories()
    {
        std::string keep = (g_activeCategory > 0 && g_activeCategory < (int)g_itemCategories.size())
            ? g_itemCategories[(size_t)g_activeCategory].key : std::string();

        g_itemCategories.clear();
        if (!g_itemOptions.empty())
        {
            g_itemCategories.push_back({ std::string(), L"All Items" });
            auto have = [&](const std::string& k)
            {
                for (size_t i = 1; i < g_itemCategories.size(); ++i)
                    if (g_itemCategories[i].key == k) return true;
                return false;
            };
            auto present = [&](const char* k)
            {
                for (size_t i = 0; i < g_itemOptions.size(); ++i)
                    if (g_itemOptions[i].cat == k) return true;
                return false;
            };
            for (const char* k : kCategoryOrder)
                if (present(k)) g_itemCategories.push_back({ k, QmJson::Utf8ToWide(k) });
            for (size_t i = 0; i < g_itemOptions.size(); ++i)
                if (!g_itemOptions[i].cat.empty() && !have(g_itemOptions[i].cat))
                    g_itemCategories.push_back({ g_itemOptions[i].cat,
                                                 QmJson::Utf8ToWide(g_itemOptions[i].cat) });
        }

        g_activeCategory = 0;
        if (!keep.empty())
            for (size_t i = 1; i < g_itemCategories.size(); ++i)
                if (g_itemCategories[i].key == keep) { g_activeCategory = (int)i; break; }
        RebuildItemFilter();
    }

    // "<AssetId>|<Display name>|<PackagePath>|<Category>" per line (the Configurator writes
    // the file pre-sorted by display name); '#' and empty lines are skipped, a line without
    // '|' is key-only. A non-empty third field marks a custom (mod-pak) item: its mounted
    // package path, used by the grant as a sync-load fallback when the PDA is not in memory
    // yet. A missing fourth field (pre-category catalog) degrades to "Misc".
    void LoadItemCatalog(const char* path)
    {
        g_itemOptions.clear();
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
                line = line.substr(b, e - b + 1);

                ItemOption opt;
                size_t sep = line.find('|');
                std::string rest = (sep == std::string::npos) ? line : line.substr(sep + 1);
                size_t sep2 = rest.find('|');
                if (sep2 != std::string::npos)
                {
                    std::string tail = rest.substr(sep2 + 1);   // "<PackagePath>[|<Category>]"
                    rest = rest.substr(0, sep2);
                    size_t sep3 = tail.find('|');
                    if (sep3 != std::string::npos)
                    {
                        opt.cat = tail.substr(sep3 + 1);
                        tail    = tail.substr(0, sep3);
                    }
                    opt.pkg = tail;
                }
                if (opt.cat.empty()) opt.cat = "Misc";
                opt.key  = (sep == std::string::npos) ? line : line.substr(0, sep);
                opt.name = QmJson::Utf8ToWide(rest);
                if (!opt.key.empty() && !opt.name.empty())
                    g_itemOptions.push_back(static_cast<ItemOption&&>(opt));
            }
        }
        RebuildItemCategories();
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
            r.sameRow  = s.sameRow;
            r.fill     = s.fill;
            r.command  = (s.type == kRowButton && !s.command.empty())  ? s.command.c_str()  : nullptr;
            r.argument = (s.type == kRowButton && !s.argument.empty()) ? s.argument.c_str() : nullptr;
            return r;
        };

        // One row from base or user storage -> view row(s); shared by both paths so user
        // rows get the same kRowMods / kRowItemDropdown expansions as base rows.
        auto emitRow = [&](const RowStorage& s)
        {
            if (s.type == kRowMods)
            {
                for (size_t m = 0; m < g_modRows.size(); ++m)
                    g_view.push_back(viewOf(g_modRows[m]));
                return;
            }
            if (s.type == kRowItemDropdown && g_itemOptions.empty())
            {
                // No usable catalog - a fixed notice instead of a dead dropdown.
                PanelRow r  = viewOf(s);
                r.type      = kRowText;
                r.text      = L"Item catalog not found - build a profile in the Quartermaster Configurator.";
                r.wrap      = true;
                g_view.push_back(r);
                return;
            }
            if ((s.type == kRowCategoryDropdown || s.type == kRowItemSearch ||
                 s.type == kRowItemCount)
                && g_itemOptions.empty())
                return;   // the item row's notice already covers the missing catalog
            g_view.push_back(viewOf(s));
        };

        g_view.clear();
        g_view.reserve(g_storage.size() + g_userRows.size() + g_modRows.size());
        bool userEmitted = false;
        for (size_t i = 0; i < g_storage.size(); ++i)
        {
            if (g_storage[i].type == kRowUserLayout)
            {
                // Splice point for the user-extension rows; the marker itself renders
                // nothing. Only the first marker expands, extras are inert.
                if (!userEmitted)
                    for (size_t u = 0; u < g_userRows.size(); ++u) emitRow(g_userRows[u]);
                userEmitted = true;
                continue;
            }
            emitRow(g_storage[i]);
        }
        if (!userEmitted)   // no marker in the base layout -> append at the bottom
            for (size_t u = 0; u < g_userRows.size(); ++u) emitRow(g_userRows[u]);
    }
}

namespace ModTab
{
    const PanelRow* GetPanelLayout(int* outCount)
    {
        char dir[MAX_PATH];
        bool havePath = LocateSidecarDir(dir, sizeof(dir));

        // The base layout is compiled in (baked from the repo qm_modtab_layout.json by
        // build.bat) - parsed exactly once, it cannot change while the process lives.
        bool baseStale = !g_loadedOnce;
        if (baseStale)
        {
            g_loadedOnce = true;
            g_storage.clear();
            ParseLayout(kDefaultLayoutJson, strlen(kDefaultLayoutJson), g_storage);

            // The retired full-override file is ignored; say so once if a copy lingers.
            char legacy[MAX_PATH];
            if (havePath && snprintf(legacy, sizeof(legacy), "%s\\qm_modtab_layout.json", dir) > 0 &&
                GetFileAttributesA(legacy) != INVALID_FILE_ATTRIBUTES)
                QM_LOG_INFO("[ModTab] layout: %s is obsolete and ignored - the base layout is "
                            "compiled in; qm_modtab_layout_*.json files extend it", legacy);
        }

        // The user-extension files are re-checked on EVERY call (one per panel build):
        // they are live-editable while the game runs (add/edit/delete, then reopen settings).
        std::vector<UserFileSig> files;
        if (havePath) ScanUserLayoutFiles(dir, files);
        bool userStale = !g_userLoadedOnce || files.size() != g_userSig.size();
        for (size_t i = 0; !userStale && i < files.size(); ++i)
            userStale = files[i].name != g_userSig[i].name ||
                        CompareFileTime(&files[i].write, &g_userSig[i].write) != 0;
        if (userStale)
        {
            g_userLoadedOnce = true;
            g_userSig = static_cast<std::vector<UserFileSig>&&>(files);
            if (havePath) LoadUserRows(dir, g_userSig);
            else          { g_userRows.clear(); g_userFileCount = 0; }
        }

        bool stale = baseStale || userStale;

        // The mods file is checked on EVERY call (one per panel build): the Configurator
        // regenerates qm_modtab_mods.txt independently of the layout file when it builds or
        // deletes a mod while the game runs.
        const RowStorage* modsTpl = nullptr;
        for (size_t i = 0; i < g_storage.size() && !modsTpl; ++i)
            if (g_storage[i].type == kRowMods) modsTpl = &g_storage[i];
        for (size_t i = 0; i < g_userRows.size() && !modsTpl; ++i)
            if (g_userRows[i].type == kRowMods) modsTpl = &g_userRows[i];

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
            if (g_storage[i].type == kRowItemDropdown ||
                g_storage[i].type == kRowCategoryDropdown ||
                g_storage[i].type == kRowItemSearch) haveDropdown = true;
        for (size_t i = 0; i < g_userRows.size() && !haveDropdown; ++i)
            if (g_userRows[i].type == kRowItemDropdown ||
                g_userRows[i].type == kRowCategoryDropdown ||
                g_userRows[i].type == kRowItemSearch) haveDropdown = true;

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
                QM_LOG_INFO("[ModTab] item catalog: %d item(s) in %d categor(ies) from %s",
                            (int)g_itemOptions.size(),
                            (int)g_itemCategories.size() ? (int)g_itemCategories.size() - 1 : 0,
                            itemsThere ? itemsPath : "(missing file)");
            }
        }
        else if (!g_itemOptions.empty()) { g_itemOptions.clear(); itemsChanged = true; }

        if (stale || modsChanged || itemsChanged)
        {
            RebuildView();
            QM_LOG_INFO("[ModTab] layout: %d row(s) - compiled-in base, %d user row(s) from "
                        "%d extension file(s), %d mod row(s)",
                        (int)g_view.size(), (int)g_userRows.size(), g_userFileCount,
                        (int)g_modRows.size());
        }

        if (outCount) *outCount = (int)g_view.size();
        return g_view.empty() ? nullptr : g_view.data();
    }

    int GetItemOptionCount()
    {
        return (int)g_itemFilter.size();
    }

    const wchar_t* GetItemOptionName(int idx)
    {
        if (idx < 0 || idx >= (int)g_itemFilter.size()) return nullptr;
        return g_itemOptions[(size_t)g_itemFilter[(size_t)idx]].name.c_str();
    }

    const char* GetItemOptionKey(int idx)
    {
        if (idx < 0 || idx >= (int)g_itemFilter.size()) return nullptr;
        return g_itemOptions[(size_t)g_itemFilter[(size_t)idx]].key.c_str();
    }

    const char* GetItemOptionPkg(int idx)
    {
        if (idx < 0 || idx >= (int)g_itemFilter.size()) return nullptr;
        const std::string& pkg = g_itemOptions[(size_t)g_itemFilter[(size_t)idx]].pkg;
        return pkg.empty() ? nullptr : pkg.c_str();
    }

    int GetItemCategoryCount()
    {
        return (int)g_itemCategories.size();
    }

    const wchar_t* GetItemCategoryName(int idx)
    {
        if (idx < 0 || idx >= (int)g_itemCategories.size()) return nullptr;
        return g_itemCategories[(size_t)idx].name.c_str();
    }

    int GetActiveItemCategory()
    {
        return g_activeCategory;
    }

    void SetActiveItemCategory(int idx)
    {
        if (idx < 0 || idx >= (int)g_itemCategories.size()) idx = 0;
        if (idx == g_activeCategory) return;
        g_activeCategory = idx;
        RebuildItemFilter();
    }

    bool SetItemSearchText(const wchar_t* text)
    {
        // Whitespace-only input is the empty filter (a stray space must not hide everything
        // the user can't see why).
        std::wstring raw = text ? text : L"";
        size_t b = raw.find_first_not_of(L" \t");
        size_t e = raw.find_last_not_of(L" \t");
        raw = (b == std::wstring::npos) ? std::wstring() : raw.substr(b, e - b + 1);

        std::wstring lc(raw);
        for (size_t i = 0; i < lc.size(); ++i) lc[i] = (wchar_t)towlower(lc[i]);
        if (lc == g_itemSearchLc) { g_itemSearchRaw = raw; return false; }

        g_itemSearchRaw = raw;
        g_itemSearchLc  = lc;
        RebuildItemFilter();
        return true;
    }

    const wchar_t* GetItemSearchText()
    {
        return g_itemSearchRaw.c_str();
    }

    void SetItemCountText(const wchar_t* text)
    {
        g_itemCountRaw = text ? text : L"";
    }

    const wchar_t* GetItemCountText()
    {
        return g_itemCountRaw.c_str();
    }

    void SetXpCountText(const wchar_t* text)
    {
        g_xpCountRaw = text ? text : L"";
    }

    const wchar_t* GetXpCountText()
    {
        return g_xpCountRaw.c_str();
    }

    void SetAttrCountText(const wchar_t* text)
    {
        g_attrCountRaw = text ? text : L"";
    }

    const wchar_t* GetAttrCountText()
    {
        return g_attrCountRaw.c_str();
    }

    void SetTalentCountText(const wchar_t* text)
    {
        g_talentCountRaw = text ? text : L"";
    }

    const wchar_t* GetTalentCountText()
    {
        return g_talentCountRaw.c_str();
    }
}
