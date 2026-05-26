// Quartermaster log writer - file-backed timestamped logger.
// ----------------------------------------------------------
// Preferred location: %LOCALAPPDATA%/R5/Saved/Logs/Quartermaster_Inject.log
//   (Windows convention; matches where UE5 itself puts user-scope logs.)
// Fallback 1: same directory as our host EXE (deterministic; survives
//   missing/misbehaving shell folders - encountered under Wine when explorer.exe
//   cannot start in headless dedicated-server mode, in which case
//   SHGetFolderPath(CSIDL_LOCAL_APPDATA) silently returns USERPROFILE instead
//   of AppData/Local).
// Fallback 2: %TEMP%.
// CRITICAL_SECTION protects concurrent fopen+append from multiple game threads.
//
// QmLogInit() must be called once before any QmLogA / QmLogF call (DllMain
// process-attach). QmLogShutdown() is optional and cleans up the lock.

#define _CRT_SECURE_NO_WARNINGS
#include <windows.h>
#include <shlobj.h>
#include <stdio.h>
#include <stdarg.h>
#include <string.h>

#include "qm_log.hpp"

#pragma comment(lib, "Shell32.lib")

static char              g_logPath[MAX_PATH] = { 0 };
static CRITICAL_SECTION  g_logLock;
static BOOL              g_logLockInit = FALSE;

// Recursive CreateDirectory - splits at every '\\' and creates each segment.
// Returns TRUE if the final directory exists (was created or already there).
static BOOL EnsureDirRecursive(const char* path)
{
    if (!path || !path[0]) return FALSE;

    char tmp[MAX_PATH];
    strncpy(tmp, path, sizeof(tmp) - 1);
    tmp[sizeof(tmp) - 1] = '\0';

    // Skip the drive prefix (e.g. "C:\") so we don't try to CreateDirectory("C:").
    char* p = tmp;
    if (strlen(p) >= 3 && p[1] == ':' && (p[2] == '\\' || p[2] == '/')) p += 3;

    for (; *p; ++p)
    {
        if (*p == '\\' || *p == '/')
        {
            char saved = *p;
            *p = '\0';
            CreateDirectoryA(tmp, NULL);
            *p = saved;
        }
    }
    CreateDirectoryA(tmp, NULL);

    DWORD attr = GetFileAttributesA(tmp);
    return (attr != INVALID_FILE_ATTRIBUTES && (attr & FILE_ATTRIBUTE_DIRECTORY)) ? TRUE : FALSE;
}

// Open+close a test file to verify the directory is actually writable from
// this process (filesystem ACLs, read-only mounts, Wine-quirk dirs).
static BOOL ProbeWritable(const char* dir)
{
    char probe[MAX_PATH];
    snprintf(probe, sizeof(probe), "%s\\.qm_writeprobe", dir);
    HANDLE h = CreateFileA(probe, GENERIC_WRITE, 0, NULL, CREATE_ALWAYS,
                           FILE_ATTRIBUTE_NORMAL | FILE_FLAG_DELETE_ON_CLOSE, NULL);
    if (h == INVALID_HANDLE_VALUE) return FALSE;
    CloseHandle(h);
    return TRUE;
}

// Returns dir containing the running EXE (DLL lives next to it -> writable).
static BOOL GetExeDir(char* out, size_t outsz)
{
    char path[MAX_PATH];
    DWORD n = GetModuleFileNameA(NULL, path, MAX_PATH);
    if (n == 0 || n >= MAX_PATH) return FALSE;
    char* slash = strrchr(path, '\\');
    if (!slash) return FALSE;
    *slash = '\0';
    strncpy(out, path, outsz - 1);
    out[outsz - 1] = '\0';
    return TRUE;
}

// Case-insensitive substring search (strcasestr/_stricmp aren't portable).
static BOOL ContainsInsensitive(const char* hay, const char* needle)
{
    if (!hay || !needle) return FALSE;
    size_t nlen = strlen(needle);
    if (nlen == 0) return TRUE;
    for (const char* p = hay; *p; ++p)
    {
        if (_strnicmp(p, needle, nlen) == 0) return TRUE;
    }
    return FALSE;
}

static void EnsureLogPath()
{
    if (g_logPath[0]) return;

    char logDir[MAX_PATH] = { 0 };
    const char* via = "none";

    // Attempt 1: %LOCALAPPDATA%\R5\Saved\Logs (Windows convention).
    // Sanity-check the returned path looks AppData-shaped - Wine can return
    // USERPROFILE here when explorer.exe failed to start (headless server in
    // a Docker container with no display driver). If the path doesn't contain
    // "AppData", treat the call as failed and move on to the deterministic
    // EXE-directory fallback.
    char appdata[MAX_PATH] = { 0 };
    if (SUCCEEDED(SHGetFolderPathA(NULL, CSIDL_LOCAL_APPDATA, NULL, SHGFP_TYPE_CURRENT, appdata))
        && appdata[0]
        && ContainsInsensitive(appdata, "AppData"))
    {
        char candidate[MAX_PATH];
        snprintf(candidate, sizeof(candidate), "%s\\R5\\Saved\\Logs", appdata);
        if (EnsureDirRecursive(candidate) && ProbeWritable(candidate))
        {
            strncpy(logDir, candidate, sizeof(logDir) - 1);
            logDir[sizeof(logDir) - 1] = '\0';
            via = "LOCALAPPDATA";
        }
    }

    // Attempt 2: directory containing the host EXE (DLL lives next to it).
    // Deterministic, no shell-folder dependency, identical behaviour Win/Wine.
    if (!logDir[0])
    {
        char exeDir[MAX_PATH];
        if (GetExeDir(exeDir, sizeof(exeDir)) && ProbeWritable(exeDir))
        {
            strncpy(logDir, exeDir, sizeof(logDir) - 1);
            logDir[sizeof(logDir) - 1] = '\0';
            via = "ExeDir";
        }
    }

    // Attempt 3: %TEMP% as a last-resort writable anchor.
    if (!logDir[0])
    {
        char tempDir[MAX_PATH];
        DWORD n = GetTempPathA(MAX_PATH, tempDir);
        if (n > 0 && n < MAX_PATH)
        {
            // GetTempPathA returns trailing backslash; strip it for consistency.
            if (tempDir[n - 1] == '\\' || tempDir[n - 1] == '/') tempDir[n - 1] = '\0';
            if (ProbeWritable(tempDir))
            {
                strncpy(logDir, tempDir, sizeof(logDir) - 1);
                logDir[sizeof(logDir) - 1] = '\0';
                via = "TEMP";
            }
        }
    }

    if (!logDir[0])
    {
        // Last-resort diagnostic crumbs - this string lands in WINEDEBUG/DebugView.
        OutputDebugStringA("[Quartermaster] EnsureLogPath: no writable directory found "
                           "(SHGetFolderPath, ExeDir, TEMP all failed). Logging disabled.\n");
        return;
    }

    snprintf(g_logPath, sizeof(g_logPath), "%s\\Quartermaster_Inject.log", logDir);

    // Drop a breadcrumb so future Wine traces show which branch was taken.
    char dbg[MAX_PATH + 64];
    snprintf(dbg, sizeof(dbg), "[Quartermaster] log path resolved via %s -> %s\n",
             via, g_logPath);
    OutputDebugStringA(dbg);
}

// Single point that actually opens the log file. va_list-based so both the
// printf-style QmLogF() and the string-only QmLogA() can funnel here.
static void LogVPrintf(const char* fmt, va_list ap)
{
    EnsureLogPath();
    if (!g_logPath[0]) return;

    if (g_logLockInit) EnterCriticalSection(&g_logLock);

    FILE* f = fopen(g_logPath, "a");
    if (f)
    {
        SYSTEMTIME st;
        GetLocalTime(&st);
        fprintf(f, "[%04d-%02d-%02d %02d:%02d:%02d.%03d] [Quartermaster] ",
            st.wYear, st.wMonth, st.wDay, st.wHour, st.wMinute, st.wSecond, st.wMilliseconds);
        vfprintf(f, fmt, ap);
        fputc('\n', f);
        fclose(f);
    }

    if (g_logLockInit) LeaveCriticalSection(&g_logLock);
}

// ----- Public C-linkage forwarders (used by every TU via qm_log.hpp) --------

extern "C" void QmLogA(const char* msg)
{
    if (!msg) return;
    // Use a const fmt + single arg so format-string injection from msg is impossible.
    QmLogF("%s", msg);
}

extern "C" void QmLogF(const char* fmt, ...)
{
    if (!fmt) return;
    va_list ap;
    va_start(ap, fmt);
    LogVPrintf(fmt, ap);
    va_end(ap);
}

// ----- Lifecycle ------------------------------------------------------------

// Rotate an existing Quartermaster_Inject.log out of the way before the new
// session starts writing. Name carries the file's last-write timestamp so the
// rotated copy reflects when the previous session ended (not when this one began).
// Falls back to "_001", "_002" suffixes if the timestamp-derived name already
// exists (e.g. same-millisecond rotation after a fast crash-restart).
static void RotateExistingLog()
{
    if (!g_logPath[0]) return;

    WIN32_FILE_ATTRIBUTE_DATA fad;
    if (!GetFileAttributesExA(g_logPath, GetFileExInfoStandard, &fad))
        return; // no previous log -> nothing to rotate

    FILETIME localFt;
    SYSTEMTIME st;
    if (!FileTimeToLocalFileTime(&fad.ftLastWriteTime, &localFt) ||
        !FileTimeToSystemTime(&localFt, &st))
        return;

    char dir[MAX_PATH];
    strncpy(dir, g_logPath, sizeof(dir) - 1);
    dir[sizeof(dir) - 1] = '\0';
    char* lastSep = strrchr(dir, '\\');
    if (!lastSep) return;
    *lastSep = '\0';

    char target[MAX_PATH];
    snprintf(target, sizeof(target),
        "%s\\Quartermaster_Inject_%04d-%02d-%02d_%02d%02d%02d_%03d.log",
        dir, st.wYear, st.wMonth, st.wDay,
        st.wHour, st.wMinute, st.wSecond, st.wMilliseconds);

    // Collision retry (paranoia, e.g. clock skew): _001..._999
    if (GetFileAttributesA(target) != INVALID_FILE_ATTRIBUTES)
    {
        for (int i = 1; i < 1000; ++i)
        {
            char retry[MAX_PATH];
            snprintf(retry, sizeof(retry),
                "%s\\Quartermaster_Inject_%04d-%02d-%02d_%02d%02d%02d_%03d_%03d.log",
                dir, st.wYear, st.wMonth, st.wDay,
                st.wHour, st.wMinute, st.wSecond, st.wMilliseconds, i);
            if (GetFileAttributesA(retry) == INVALID_FILE_ATTRIBUTES)
            {
                MoveFileA(g_logPath, retry);
                return;
            }
        }
        return; // give up - new session will append to existing log
    }

    MoveFileA(g_logPath, target);
}

void QmLogInit()
{
    if (g_logLockInit) return;
    InitializeCriticalSection(&g_logLock);
    g_logLockInit = TRUE;
    EnsureLogPath();
    RotateExistingLog();
}

void QmLogShutdown()
{
    if (!g_logLockInit) return;
    DeleteCriticalSection(&g_logLock);
    g_logLockInit = FALSE;
}
