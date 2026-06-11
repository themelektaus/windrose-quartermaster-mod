@echo off
setlocal
rem ============================================================
rem Quartermaster dxgi.dll Proxy Deploy Script
rem  Targets: E:\Games\steamapps\common\Windrose\R5\Binaries\Win64
rem    - dxgi.dll       : our proxy (built by build.bat). Self-loading;
rem                       resolves the real system dxgi.dll at runtime
rem                       via a %TEMP%-copy, no companion DLL needed.
rem                       MUST sit directly in Win64 (proxy load order).
rem                       Ownership proof is embedded in the DLL itself:
rem                       PE version resource ProductName=Quartermaster
rem                       (version.rc). A legacy dxgi.dll.qm marker from
rem                       older deploys is honored once and removed.
rem    - Quartermaster\ : sidecar folder; ALL qm_*.txt / qm_*.json the
rem                       DLL reads (sentinels, configs, layout, profile
rem                       JSONs) live here, not in the Win64 root.
rem ============================================================

set SCRIPT_DIR=%~dp0
set TARGET=E:\Games\steamapps\common\Windrose\R5\Binaries\Win64
set SIDECAR=%TARGET%\Quartermaster

if not exist "%SCRIPT_DIR%dxgi.dll" (
    echo [deploy] dxgi.dll not built yet - run build.bat first
    exit /b 1
)

if not exist "%TARGET%" (
    echo [deploy] Target directory not found: %TARGET%
    exit /b 1
)

rem Re-deploy is fine - we control the file. The guard below refuses to
rem overwrite a foreign dxgi.dll. Proof-of-ownership: ProductName in the
rem PE version resource embedded in the DLL (legacy dxgi.dll.qm accepted).
if not exist "%TARGET%\dxgi.dll" goto :ownership_ok
set "EXISTING_PRODUCT="
for /f "usebackq delims=" %%V in (`powershell -NoProfile -Command "(Get-Item -LiteralPath '%TARGET%\dxgi.dll').VersionInfo.ProductName"`) do set "EXISTING_PRODUCT=%%V"
if /I "%EXISTING_PRODUCT%"=="Quartermaster" goto :ownership_ok
if exist "%TARGET%\dxgi.dll.qm" goto :ownership_ok
echo [deploy] WARNING: %TARGET%\dxgi.dll exists but is not identified as Quartermaster
echo          (version resource ProductName=%EXISTING_PRODUCT%).
echo          Refusing to overwrite - could be a foreign or game-shipped dxgi.dll.
exit /b 1
:ownership_ok

echo [deploy] Copying proxy: %SCRIPT_DIR%dxgi.dll -^> %TARGET%\dxgi.dll
copy /Y "%SCRIPT_DIR%dxgi.dll" "%TARGET%\dxgi.dll" >nul
if errorlevel 1 (
    echo [deploy] Failed to copy proxy.
    exit /b 1
)

if exist "%TARGET%\dxgi.dll.qm" (
    del /q "%TARGET%\dxgi.dll.qm"
    echo [deploy] Removed legacy dxgi.dll.qm marker - ownership is embedded in the DLL now
)

if not exist "%SIDECAR%" mkdir "%SIDECAR%"

rem Sidecars used to live directly in Win64; the DLL only reads the
rem Quartermaster subfolder now, so stale root copies would silently
rem deactivate features. Migrate them once (an existing subfolder copy wins).
for %%F in ("%TARGET%\qm_*.txt" "%TARGET%\qm_*.json") do (
    if exist "%SIDECAR%\%%~nxF" (
        echo [deploy] Legacy %%~nxF superseded by sidecar copy - removing root copy
        del /q "%%~fF"
    ) else (
        echo [deploy] Migrating legacy sidecar %%~nxF -^> %SIDECAR%
        move /Y "%%~fF" "%SIDECAR%\" >nul
    )
)

rem qm_modtab_layout.json drives the settings-panel content (the DLL re-reads
rem it on change, no restart needed). Seed it once; an existing copy in the
rem sidecar folder is user-owned (live-edited) and must not be overwritten.
if exist "%SCRIPT_DIR%qm_modtab_layout.json" (
    if exist "%SIDECAR%\qm_modtab_layout.json" (
        echo [deploy] qm_modtab_layout.json present in sidecar folder - user-owned, left alone
    ) else (
        echo [deploy] Seeding default layout: %SIDECAR%\qm_modtab_layout.json
        copy /Y "%SCRIPT_DIR%qm_modtab_layout.json" "%SIDECAR%\qm_modtab_layout.json" >nul
    )
)

rem qm_items_<profile>.json files are written per-profile by the build pipeline
rem (GameDeployer.WriteItemsJson) on every profile build. We MUST NOT copy any
rem stale dev-spike stub from this source folder over the freshly-built files.
dir /b "%SIDECAR%\qm_items_*.json" 2>nul | findstr /r ".*" >nul
if errorlevel 1 (
    echo [deploy] No qm_items_*.json in sidecar folder - DLL runs idle until next profile build
) else (
    echo [deploy] qm_items_*.json files present in sidecar folder - build pipeline owns them, left alone
)

echo.
echo [deploy] Deploy complete. Files in target:
dir /b "%TARGET%\dxgi*.dll" 2>nul
echo [deploy] Sidecar folder %SIDECAR%:
dir /b "%SIDECAR%" 2>nul

echo.
echo [deploy] Test plan:
echo   1. Start Windrose normally via Steam.
echo   2. Confirm the game launches without crash.
echo   3. Check log file:
echo      %%LOCALAPPDATA%%\R5\Saved\Logs\Quartermaster_Inject.log
echo      ^(should contain a timestamped 'dxgi.dll proxy loaded' line
echo       plus a '[Passthrough] 19 exports resolved via temp-copy' line^)

endlocal
