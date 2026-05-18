// Minimal CreateRemoteThread + LoadLibraryW injector.
// Usage: inject.exe <process_name> <absolute_path_to_dll>
// Example: inject.exe Windrose-Win64-Shipping.exe E:\Windrose\Mods\Quartermaster\Tools\Dumper7\x64\Release\Dumper-7.dll

#include <windows.h>
#include <tlhelp32.h>
#include <stdio.h>
#include <string.h>
#include <wchar.h>

static DWORD FindProcessByName(const char* exeName)
{
    HANDLE snap = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
    if (snap == INVALID_HANDLE_VALUE) return 0;

    PROCESSENTRY32 pe{};
    pe.dwSize = sizeof(pe);
    DWORD pid = 0;

    if (Process32First(snap, &pe))
    {
        do
        {
            if (_stricmp(pe.szExeFile, exeName) == 0)
            {
                pid = pe.th32ProcessID;
                break;
            }
        } while (Process32Next(snap, &pe));
    }
    CloseHandle(snap);
    return pid;
}

int main(int argc, char** argv)
{
    if (argc < 3)
    {
        fprintf(stderr, "Usage: %s <process_name.exe> <absolute_path_to_dll>\n", argv[0]);
        return 1;
    }

    const char* procName = argv[1];
    const char* dllPathAnsi = argv[2];

    DWORD pid = FindProcessByName(procName);
    if (!pid)
    {
        fprintf(stderr, "[inject] Process '%s' not running.\n", procName);
        return 2;
    }
    printf("[inject] Found '%s' as PID %lu\n", procName, pid);

    HANDLE hProc = OpenProcess(
        PROCESS_CREATE_THREAD | PROCESS_QUERY_INFORMATION |
        PROCESS_VM_OPERATION | PROCESS_VM_WRITE | PROCESS_VM_READ,
        FALSE, pid);
    if (!hProc)
    {
        fprintf(stderr, "[inject] OpenProcess failed: %lu\n", GetLastError());
        return 3;
    }

    // Convert ANSI path to UTF-16 since we use LoadLibraryW.
    wchar_t dllPathW[MAX_PATH];
    int wlen = MultiByteToWideChar(CP_ACP, 0, dllPathAnsi, -1, dllPathW, MAX_PATH);
    if (wlen <= 0)
    {
        fprintf(stderr, "[inject] DLL path conversion failed.\n");
        CloseHandle(hProc);
        return 4;
    }

    SIZE_T pathBytes = (SIZE_T)wlen * sizeof(wchar_t);

    LPVOID remotePath = VirtualAllocEx(hProc, NULL, pathBytes,
                                       MEM_COMMIT | MEM_RESERVE,
                                       PAGE_READWRITE);
    if (!remotePath)
    {
        fprintf(stderr, "[inject] VirtualAllocEx failed: %lu\n", GetLastError());
        CloseHandle(hProc);
        return 5;
    }

    SIZE_T written = 0;
    if (!WriteProcessMemory(hProc, remotePath, dllPathW, pathBytes, &written) || written != pathBytes)
    {
        fprintf(stderr, "[inject] WriteProcessMemory failed: %lu\n", GetLastError());
        VirtualFreeEx(hProc, remotePath, 0, MEM_RELEASE);
        CloseHandle(hProc);
        return 6;
    }

    HMODULE kernel32 = GetModuleHandleA("kernel32.dll");
    LPTHREAD_START_ROUTINE loadLibW =
        (LPTHREAD_START_ROUTINE)GetProcAddress(kernel32, "LoadLibraryW");
    if (!loadLibW)
    {
        fprintf(stderr, "[inject] GetProcAddress(LoadLibraryW) failed.\n");
        VirtualFreeEx(hProc, remotePath, 0, MEM_RELEASE);
        CloseHandle(hProc);
        return 7;
    }

    HANDLE hThread = CreateRemoteThread(hProc, NULL, 0, loadLibW, remotePath, 0, NULL);
    if (!hThread)
    {
        fprintf(stderr, "[inject] CreateRemoteThread failed: %lu\n", GetLastError());
        VirtualFreeEx(hProc, remotePath, 0, MEM_RELEASE);
        CloseHandle(hProc);
        return 8;
    }
    printf("[inject] Remote thread spawned. Waiting for LoadLibraryW to return ...\n");

    WaitForSingleObject(hThread, INFINITE);

    DWORD exitCode = 0;
    GetExitCodeThread(hThread, &exitCode);
    // exitCode is the lower 32 bits of the HMODULE returned by LoadLibraryW.
    // 0 means LoadLibrary failed.
    if (exitCode == 0)
    {
        fprintf(stderr, "[inject] LoadLibraryW returned 0 - DLL load failed inside target.\n");
    }
    else
    {
        printf("[inject] LoadLibraryW returned 0x%08lx (low 32 bits of HMODULE). OK.\n", exitCode);
    }

    CloseHandle(hThread);
    VirtualFreeEx(hProc, remotePath, 0, MEM_RELEASE);
    CloseHandle(hProc);
    return (exitCode == 0) ? 9 : 0;
}
