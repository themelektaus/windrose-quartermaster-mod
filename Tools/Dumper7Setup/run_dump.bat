@echo off
REM Inject Dumper-7.dll into Windrose-Win64-Shipping.exe.
REM Requires the game to be running already.
REM
REM Layout: this script lives in Tools/Dumper7Setup/ (versioned).
REM The actual Dumper7 build output lives in Tools/Dumper7/x64/Release/
REM (submodule, built via msbuild Dumper-7.sln).

setlocal
set DUMPER_DIR=%~dp0..\Dumper7
set DUMPER_DLL=%DUMPER_DIR%\x64\Release\Dumper-7.dll
set INJECTOR=%~dp0inject\inject.exe
set DUMPER_INI_SRC=%~dp0Dumper-7.ini
set DUMPER_INI_DST=%DUMPER_DIR%\Dumper-7.ini

if not exist "%DUMPER_DLL%" (
    echo [ERROR] Dumper-7.dll not built. Run: msbuild "%DUMPER_DIR%\Dumper-7.sln" /p:Configuration=Release /p:Platform=x64
    exit /b 1
)
if not exist "%INJECTOR%" (
    echo [ERROR] inject.exe not built. Run: "%~dp0inject\build.bat"
    exit /b 1
)
if not exist "%DUMPER_INI_DST%" (
    if exist "%DUMPER_INI_SRC%" (
        copy /Y "%DUMPER_INI_SRC%" "%DUMPER_INI_DST%" >nul
        echo [run_dump] Copied Dumper-7.ini into submodule dir
    )
)

echo [run_dump] Injecting %DUMPER_DLL%
echo [run_dump] into Windrose-Win64-Shipping.exe
echo [run_dump] Once injected: a console window opens inside the game (alt-tab if needed).
echo [run_dump] Dump starts after 3 seconds, or press F8 to trigger manually.
echo [run_dump] Press F6 inside the dumper console to unload when done.
echo.

"%INJECTOR%" Windrose-Win64-Shipping.exe "%DUMPER_DLL%"
set RC=%ERRORLEVEL%

if not %RC%==0 (
    echo.
    echo [ERROR] Injection failed with code %RC%.
    exit /b %RC%
)

echo.
echo [OK] Injector returned. Watch the in-game console for progress.
echo [OK] SDK output goes to: %DUMPER_DIR%\output\
