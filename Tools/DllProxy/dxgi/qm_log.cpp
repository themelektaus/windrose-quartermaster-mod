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

void QmLogInit()
{
    if (g_logLockInit) return;
    InitializeCriticalSection(&g_logLock);
    g_logLockInit = TRUE;
}

void QmLogShutdown()
{
    if (!g_logLockInit) return;
    DeleteCriticalSection(&g_logLock);
    g_logLockInit = FALSE;
}
