// Quartermaster crash diagnostics - impl. See qm_crash.hpp.

#define _CRT_SECURE_NO_WARNINGS
#include <windows.h>
#include <stdint.h>
#include <stdio.h>

#include "qm_log.hpp"
#include "qm_crash.hpp"
#include "qm_inject.hpp"   // QmInjectSnapshot

static volatile LONG g_crashHandlerInstalled = 0;
static volatile LONG g_crashLogged           = 0;

static const char* QmExceptionCodeName(DWORD code)
{
    switch (code) {
    case EXCEPTION_ACCESS_VIOLATION:         return "ACCESS_VIOLATION";
    case EXCEPTION_STACK_OVERFLOW:           return "STACK_OVERFLOW";
    case EXCEPTION_ILLEGAL_INSTRUCTION:      return "ILLEGAL_INSTRUCTION";
    case EXCEPTION_PRIV_INSTRUCTION:         return "PRIV_INSTRUCTION";
    case EXCEPTION_INT_DIVIDE_BY_ZERO:       return "INT_DIVIDE_BY_ZERO";
    case EXCEPTION_INT_OVERFLOW:             return "INT_OVERFLOW";
    case EXCEPTION_DATATYPE_MISALIGNMENT:    return "DATATYPE_MISALIGNMENT";
    case EXCEPTION_IN_PAGE_ERROR:            return "IN_PAGE_ERROR";
    case EXCEPTION_NONCONTINUABLE_EXCEPTION: return "NONCONTINUABLE";
    case 0xE06D7363:                         return "CXX_EXCEPTION";  // MSVC C++ exc
    default:                                 return "?";
    }
}

static bool QmIsHardCrash(DWORD code)
{
    return code == EXCEPTION_ACCESS_VIOLATION
        || code == EXCEPTION_STACK_OVERFLOW
        || code == EXCEPTION_ILLEGAL_INSTRUCTION
        || code == EXCEPTION_PRIV_INSTRUCTION
        || code == EXCEPTION_IN_PAGE_ERROR
        || code == EXCEPTION_NONCONTINUABLE_EXCEPTION;
}

static void QmLogCrashSnapshot(EXCEPTION_POINTERS* info, const char* via)
{
    if (!info || !info->ExceptionRecord) return;
    EXCEPTION_RECORD* er = info->ExceptionRecord;

    // Resolve module owning the crash address.
    char modName[MAX_PATH] = "<?>";
    HMODULE hMod = nullptr;
    uintptr_t offset = 0;
    if (GetModuleHandleExA(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS | GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT,
        (LPCSTR)er->ExceptionAddress, &hMod) && hMod)
    {
        GetModuleFileNameA(hMod, modName, sizeof(modName));
        offset = (uintptr_t)er->ExceptionAddress - (uintptr_t)hMod;
    }

    QM_LOG_ERROR("[Crash] *** %s *** code=0x%08X (%s) addr=0x%p TID=%lu",
        via, er->ExceptionCode, QmExceptionCodeName(er->ExceptionCode),
        er->ExceptionAddress, GetCurrentThreadId());
    QM_LOG_ERROR("[Crash] module='%s' base=0x%p offset=0x%llX",
        modName, (void*)hMod, (unsigned long long)offset);

    if (er->ExceptionCode == EXCEPTION_ACCESS_VIOLATION && er->NumberParameters >= 2)
    {
        const char* op = er->ExceptionInformation[0] == 0 ? "READ"
                       : er->ExceptionInformation[0] == 1 ? "WRITE"
                       : er->ExceptionInformation[0] == 8 ? "DEP/EXEC"
                       : "?";
        QM_LOG_ERROR("[Crash] AV: %s at 0x%p", op, (void*)er->ExceptionInformation[1]);
    }

    CONTEXT* ctx = info->ContextRecord;
    if (ctx)
    {
        QM_LOG_ERROR("[Crash] RIP=0x%p RSP=0x%p RBP=0x%p",
            (void*)ctx->Rip, (void*)ctx->Rsp, (void*)ctx->Rbp);
        QM_LOG_ERROR("[Crash] RAX=0x%p RCX=0x%p RDX=0x%p R8=0x%p R9=0x%p",
            (void*)ctx->Rax, (void*)ctx->Rcx, (void*)ctx->Rdx, (void*)ctx->R8, (void*)ctx->R9);
        QM_LOG_ERROR("[Crash] R10=0x%p R11=0x%p R12=0x%p R13=0x%p R14=0x%p R15=0x%p",
            (void*)ctx->R10, (void*)ctx->R11, (void*)ctx->R12,
            (void*)ctx->R13, (void*)ctx->R14, (void*)ctx->R15);
    }

    // Quartermaster state snapshot (owned by qm_inject).
    QmInjectSnapshot snap = QmGetInjectSnapshot();
    QM_LOG_ERROR("[Crash] qm-state: hits=%ld injects=%ld already-present=%ld donor=0x%p sourceGroup=0x%p pool-size=%d",
        snap.hookHits, snap.injectsDone, snap.alreadyPresent,
        snap.donorItem, snap.donorSourceGroup, snap.spawnedPoolCount);
}

static LONG WINAPI QmVectoredHandler(EXCEPTION_POINTERS* info)
{
    if (!info || !info->ExceptionRecord) return EXCEPTION_CONTINUE_SEARCH;
    DWORD code = info->ExceptionRecord->ExceptionCode;
    if (!QmIsHardCrash(code)) return EXCEPTION_CONTINUE_SEARCH;
    // Log only once (avoid spam if VEH fires repeatedly during unwind).
    if (InterlockedCompareExchange(&g_crashLogged, 1, 0) == 0)
        QmLogCrashSnapshot(info, "VEH FIRST-CHANCE");
    return EXCEPTION_CONTINUE_SEARCH;
}

static LONG WINAPI QmUnhandledExceptionFilter(EXCEPTION_POINTERS* info)
{
    if (info && info->ExceptionRecord && InterlockedCompareExchange(&g_crashLogged, 1, 0) == 0)
        QmLogCrashSnapshot(info, "UEF UNHANDLED");
    return EXCEPTION_CONTINUE_SEARCH;  // let OS terminate normally
}

void QmCrashInstallHandler()
{
    if (InterlockedExchange(&g_crashHandlerInstalled, 1)) return;
    AddVectoredExceptionHandler(0 /* last in chain */, QmVectoredHandler);
    SetUnhandledExceptionFilter(QmUnhandledExceptionFilter);
    QM_LOG_INFO("[Crash] handlers installed (VEH first-chance + UEF unhandled)");
}
