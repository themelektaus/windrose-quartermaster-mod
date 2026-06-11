// Minimal JSON-subset toolkit shared by the DLL's runtime config readers
// (qm_config.cpp, qm_modtab_layout.cpp). Hand-rolled instead of a third-party
// header library to keep the DLL self-contained and the binary tiny.
//
// Parser supports: objects, arrays, "strings" with the common escapes
// (\" \\ \/ \n \t \r \b \f), numbers, true/false, and // line comments
// (not standard JSON but handy for hand-edited configs). No \uXXXX escapes.

#pragma once

#include <windows.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <string>

namespace QmJson
{

// UTF-8 (narrow) -> UTF-16 (wide).
inline std::wstring Utf8ToWide(const std::string& s)
{
    if (s.empty()) return std::wstring();
    int len = MultiByteToWideChar(CP_UTF8, 0, s.c_str(), (int)s.size(), nullptr, 0);
    if (len <= 0) return std::wstring();
    std::wstring out((size_t)len, L'\0');
    MultiByteToWideChar(CP_UTF8, 0, s.c_str(), (int)s.size(), &out[0], len);
    return out;
}

inline bool ReadWholeFile(const char* path, std::string& out)
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
inline void StripUtf8Bom(std::string& s)
{
    if (s.size() >= 3 &&
        (unsigned char)s[0] == 0xEF &&
        (unsigned char)s[1] == 0xBB &&
        (unsigned char)s[2] == 0xBF)
    {
        s.erase(0, 3);
    }
}

struct Parser
{
    const char* p;
    const char* end;
    bool        ok = true;
    const char* lastError = nullptr;

    Parser(const char* data, size_t len) : p(data), end(data + len) {}

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

    bool parseNumber(double& out)
    {
        skipWs();
        const char* start = p;
        while (p < end && (*p == '-' || *p == '+' || *p == '.' || *p == 'e' || *p == 'E' ||
                           (*p >= '0' && *p <= '9')))
            ++p;
        if (p == start) { ok = false; lastError = "expected number"; return false; }
        char buf[48];
        size_t n = (size_t)(p - start);
        if (n >= sizeof(buf)) n = sizeof(buf) - 1;
        memcpy(buf, start, n);
        buf[n] = '\0';
        out = atof(buf);
        return true;
    }

    bool parseBool(bool& out)
    {
        skipWs();
        if (end - p >= 4 && memcmp(p, "true", 4) == 0)  { p += 4; out = true;  return true; }
        if (end - p >= 5 && memcmp(p, "false", 5) == 0) { p += 5; out = false; return true; }
        ok = false;
        lastError = "expected true/false";
        return false;
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

} // namespace QmJson
