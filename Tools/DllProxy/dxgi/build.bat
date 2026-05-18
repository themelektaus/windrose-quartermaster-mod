@echo off
setlocal
rem ============================================================
rem Quartermaster dxgi.dll Proxy Build Script
rem
rem Usage:
rem   build.bat            - dev build (verbose logs + diagnostic code)
rem   build.bat release    - production build (info-only logs, no diag)
rem
rem The release switch defines QM_BUILD_PRODUCTION which trims:
rem  - per-hit debug/trace log lines (only ERROR/WARN/INFO remain)
rem  - all #if QM_DIAG read-only inspectors and class-byte dumps
rem ============================================================

set SCRIPT_DIR=%~dp0
pushd "%SCRIPT_DIR%"

rem Setup VS 2022 Community x64 environment
call "C:\Program Files\Microsoft Visual Studio\2022\Community\VC\Auxiliary\Build\vcvars64.bat" >nul
if errorlevel 1 (
    echo [build] vcvars64.bat failed.
    popd
    exit /b 1
)

rem Clean previous outputs
if exist *.obj del /q *.obj
if exist dxgi.dll del /q dxgi.dll
if exist dxgi.exp del /q dxgi.exp
if exist dxgi.lib del /q dxgi.lib

set MH_DIR=minhook
if not exist "%MH_DIR%\include\MinHook.h" (
    echo [build] MinHook source missing under %MH_DIR%\ - run: git clone https://github.com/TsudaKageyu/minhook.git %MH_DIR%
    popd
    exit /b 1
)

set CONFIG_DEFINE=
set CONFIG_LABEL=dev
if /I "%~1"=="release" (
    set CONFIG_DEFINE=/DQM_BUILD_PRODUCTION
    set CONFIG_LABEL=production
)

set COMMON_FLAGS=/nologo /c /O2 /MT /W3 /DWIN32_LEAN_AND_MEAN /I"%MH_DIR%\include" %CONFIG_DEFINE%

echo [build] config: %CONFIG_LABEL%
echo [build] cl /c Quartermaster C++ sources ...
cl %COMMON_FLAGS% /EHa main.cpp qm_log.cpp qm_ue.cpp qm_scan.cpp qm_crash.cpp qm_config.cpp qm_inject.cpp qm_diag.cpp qm_hook.cpp
if errorlevel 1 ( echo [build] cl C++ sources failed. & popd & exit /b 1 )

echo [build] cl /c MinHook sources ...
cl %COMMON_FLAGS% "%MH_DIR%\src\buffer.c" "%MH_DIR%\src\hook.c" "%MH_DIR%\src\trampoline.c" "%MH_DIR%\src\hde\hde64.c"
if errorlevel 1 ( echo [build] cl minhook failed. & popd & exit /b 1 )

echo [build] link /DLL ...
link /nologo /DLL /MACHINE:X64 /OUT:dxgi.dll ^
    main.obj qm_log.obj qm_ue.obj qm_scan.obj qm_crash.obj qm_config.obj qm_inject.obj qm_diag.obj qm_hook.obj ^
    buffer.obj hook.obj trampoline.obj hde64.obj ^
    kernel32.lib user32.lib shell32.lib advapi32.lib
if errorlevel 1 ( echo [build] link failed. & popd & exit /b 1 )

echo.
echo [build] Success (%CONFIG_LABEL%): %SCRIPT_DIR%dxgi.dll
dir dxgi.dll | findstr dxgi.dll

popd
endlocal
