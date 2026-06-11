@echo off
setlocal
rem ============================================================
rem Quartermaster dxgi.dll Proxy Deploy Script
rem  Targets: E:\Games\steamapps\common\Windrose\R5\Binaries\Win64
rem    - dxgi.dll       : our proxy (built by build.bat). Self-loading;
rem                       resolves the real system dxgi.dll at runtime
rem                       via a %TEMP%-copy, no companion DLL needed.
rem    - dxgi.dll.qm    : marker written next to dxgi.dll so a later
rem                       deploy can recognise the proxy as ours.
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

rem Re-deploy is fine - we control the file. The guard below refuses to
rem overwrite a foreign dxgi.dll. Proof-of-ownership: dxgi.dll.qm marker.
if exist "%TARGET%\dxgi.dll" (
    if not exist "%TARGET%\dxgi.dll.qm" (
        echo [deploy] WARNING: %TARGET%\dxgi.dll exists but no dxgi.dll.qm marker alongside.
        echo          Refusing to overwrite - could be a game-shipped dxgi.dll.
        exit /b 1
    )
)

echo [deploy] Copying proxy: %SCRIPT_DIR%dxgi.dll -^> %TARGET%\dxgi.dll
copy /Y "%SCRIPT_DIR%dxgi.dll" "%TARGET%\dxgi.dll" >nul
if errorlevel 1 (
    echo [deploy] Failed to copy proxy.
    exit /b 1
)

echo [deploy] Writing marker: %TARGET%\dxgi.dll.qm
>"%TARGET%\dxgi.dll.qm" echo Quartermaster dxgi.dll proxy marker (deploy.bat)

rem qm_modtab_layout.json drives the settings-panel content (the DLL re-reads
rem it on change, no restart needed). Seed it once; an existing copy in the
rem target is user-owned (live-edited) and must not be overwritten.
if exist "%SCRIPT_DIR%qm_modtab_layout.json" (
    if exist "%TARGET%\qm_modtab_layout.json" (
        echo [deploy] qm_modtab_layout.json present in target - user-owned, left alone
    ) else (
        echo [deploy] Seeding default layout: %TARGET%\qm_modtab_layout.json
        copy /Y "%SCRIPT_DIR%qm_modtab_layout.json" "%TARGET%\qm_modtab_layout.json" >nul
    )
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
dir /b "%TARGET%\dxgi*.dll" "%TARGET%\dxgi.dll.qm" "%TARGET%\qm_items_*.json" 2>nul

echo.
echo [deploy] Test plan:
echo   1. Start Windrose normally via Steam.
echo   2. Confirm the game launches without crash.
echo   3. Check log file:
echo      %%LOCALAPPDATA%%\R5\Saved\Logs\Quartermaster_Inject.log
echo      ^(should contain a timestamped 'dxgi.dll proxy loaded' line
echo       plus a '[Passthrough] 19 exports resolved via temp-copy' line^)

endlocal
