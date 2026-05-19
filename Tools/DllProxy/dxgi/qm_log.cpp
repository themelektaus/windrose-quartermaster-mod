// Quartermaster log writer - file-backed timestamped logger.
// ----------------------------------------------------------
// One file per process at %LOCALAPPDATA%/R5/Saved/Logs/Quartermaster_Inject.log.
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

static void EnsureLogPath()
{
    if (g_logPath[0]) return;

    char appdata[MAX_PATH];
    if (FAILED(SHGetFolderPathA(NULL, CSIDL_LOCAL_APPDATA, NULL, 0, appdata)))
        return;

    char logDir[MAX_PATH];
    snprintf(logDir, sizeof(logDir), "%s\\R5\\Saved\\Logs", appdata);
    CreateDirectoryA(logDir, NULL);

    snprintf(g_logPath, sizeof(g_logPath), "%s\\Quartermaster_Inject.log", logDir);
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
