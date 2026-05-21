@echo off
setlocal
rem ============================================================
rem Quartermaster dxgi.dll Proxy Deploy Script
rem  Targets: E:\Games\steamapps\common\Windrose\R5\Binaries\Win64
rem    - dxgi.dll      : our proxy (built by build.bat)
rem    - dxgi_org.dll  : renamed copy of C:\Windows\System32\dxgi.dll
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

rem Re-deploy is fine - we control the file. dxgi_org.dll guard below ensures
rem we never overwrite a non-proxy dxgi.dll that shipped with the game.
if exist "%TARGET%\dxgi.dll" if not exist "%TARGET%\dxgi_org.dll" (
    echo [deploy] WARNING: %TARGET%\dxgi.dll exists but no dxgi_org.dll alongside.
    echo          Refusing to overwrite - could be a game-shipped dxgi.dll.
    exit /b 1
)

if not exist "%TARGET%\dxgi_org.dll" (
    echo [deploy] Copying C:\Windows\System32\dxgi.dll -^> %TARGET%\dxgi_org.dll
    copy /Y "C:\Windows\System32\dxgi.dll" "%TARGET%\dxgi_org.dll" >nul
    if errorlevel 1 (
        echo [deploy] Failed to copy system dxgi.dll.
        exit /b 1
    )
) else (
    echo [deploy] dxgi_org.dll already present, skipping copy
)

echo [deploy] Copying proxy: %SCRIPT_DIR%dxgi.dll -^> %TARGET%\dxgi.dll
copy /Y "%SCRIPT_DIR%dxgi.dll" "%TARGET%\dxgi.dll" >nul
if errorlevel 1 (
    echo [deploy] Failed to copy proxy.
    exit /b 1
)

rem qm_items.json is written by the build pipeline (GameDeployer.WriteItemsJson)
rem on every profile build. We MUST NOT copy a stale dev-spike stub from this
rem source folder over the freshly-built file - that's a silent regression
rem (build emits 3 items, this copy reverts to 2 items, custom buildings pop
rem in/out unpredictably). Earlier spike-style override removed 2026-05-21.
if exist "%TARGET%\qm_items.json" (
    echo [deploy] qm_items.json present in target - build pipeline owns it, left alone
) else (
    echo [deploy] No qm_items.json in target - DLL runs idle until next profile build
)

echo.
echo [deploy] Deploy complete. Files in target:
dir /b "%TARGET%\dxgi*.dll" "%TARGET%\qm_items.json" 2>nul

echo.
echo [deploy] Test plan:
echo   1. Start Windrose normally via Steam.
echo   2. Confirm the game launches without crash.
echo   3. Check log file:
echo      %%LOCALAPPDATA%%\R5\Saved\Logs\Quartermaster_Inject.log
echo      ^(should contain a timestamped 'dxgi.dll proxy loaded' line^)

endlocal
