@echo off
setlocal
rem ============================================================
rem Quartermaster dxgi.dll Proxy Deploy Script
rem  Targets: E:\Games\steamapps\common\Windrose\R5\Binaries\Win64
rem    - dxgi.dll      : our proxy (built by build.bat)
rem    - dxgi_original.dll  : renamed copy of C:\Windows\System32\dxgi.dll
rem ============================================================

set SCRIPT_DIR=%~dp0
set TARGET=E:\Games\steamapps\common\Windrose\R5\Binaries\Win64

if not exist "%SCRIPT_DIR%dxgi.dll" (
    echo [deploy] dxgi.dll not built yet - run build.bat first
    exit /b 1
)

if not exist "%TARGET%" (
    echo [deploy] Target directory not found: %TARGET%
    exit /b 1
)

rem Re-deploy is fine - we control the file. dxgi_original.dll guard below ensures
rem we never overwrite a non-proxy dxgi.dll that shipped with the game.
if exist "%TARGET%\dxgi.dll" if not exist "%TARGET%\dxgi_original.dll" (
    echo [deploy] WARNING: %TARGET%\dxgi.dll exists but no dxgi_original.dll alongside.
    echo          Refusing to overwrite - could be a game-shipped dxgi.dll.
    exit /b 1
)

if not exist "%TARGET%\dxgi_original.dll" (
    echo [deploy] Copying C:\Windows\System32\dxgi.dll -^> %TARGET%\dxgi_original.dll
    copy /Y "C:\Windows\System32\dxgi.dll" "%TARGET%\dxgi_original.dll" >nul
    if errorlevel 1 (
        echo [deploy] Failed to copy system dxgi.dll.
        exit /b 1
    )
) else (
    echo [deploy] dxgi_original.dll already present, skipping copy
)

echo [deploy] Copying proxy: %SCRIPT_DIR%dxgi.dll -^> %TARGET%\dxgi.dll
copy /Y "%SCRIPT_DIR%dxgi.dll" "%TARGET%\dxgi.dll" >nul
if errorlevel 1 (
    echo [deploy] Failed to copy proxy.
    exit /b 1
)

rem qm_items_<profile>.json files are written per-profile by the build pipeline
rem (GameDeployer.WriteItemsJson) on every profile build. We MUST NOT copy any
rem stale dev-spike stub from this source folder over the freshly-built files.
rem Earlier spike-style override removed 2026-05-21.
dir /b "%TARGET%\qm_items_*.json" 2>nul | findstr /r ".*" >nul
if errorlevel 1 (
    echo [deploy] No qm_items_*.json in target - DLL runs idle until next profile build
) else (
    echo [deploy] qm_items_*.json files present in target - build pipeline owns them, left alone
)

echo.
echo [deploy] Deploy complete. Files in target:
dir /b "%TARGET%\dxgi*.dll" "%TARGET%\qm_items_*.json" 2>nul

echo.
echo [deploy] Test plan:
echo   1. Start Windrose normally via Steam.
echo   2. Confirm the game launches without crash.
echo   3. Check log file:
echo      %%LOCALAPPDATA%%\R5\Saved\Logs\Quartermaster_Inject.log
echo      ^(should contain a timestamped 'dxgi.dll proxy loaded' line^)

endlocal
