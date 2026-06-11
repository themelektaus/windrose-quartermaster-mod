@echo off
setlocal
rem Removes the Quartermaster dxgi proxy. Ownership proof: ProductName in
rem the PE version resource embedded in the DLL (version.rc); a legacy
rem dxgi.dll.qm marker from older deploys is accepted too and cleaned up.
set TARGET=E:\Games\steamapps\common\Windrose\R5\Binaries\Win64

if not exist "%TARGET%\dxgi.dll" goto :marker
set "EXISTING_PRODUCT="
for /f "usebackq delims=" %%V in (`powershell -NoProfile -Command "(Get-Item -LiteralPath '%TARGET%\dxgi.dll').VersionInfo.ProductName"`) do set "EXISTING_PRODUCT=%%V"
if /I "%EXISTING_PRODUCT%"=="Quartermaster" goto :remove
if exist "%TARGET%\dxgi.dll.qm" goto :remove
echo [uninstall] %TARGET%\dxgi.dll is not identified as Quartermaster - left alone
goto :marker

:remove
del /q "%TARGET%\dxgi.dll"
echo [uninstall] removed %TARGET%\dxgi.dll

:marker
if exist "%TARGET%\dxgi.dll.qm" (
    del /q "%TARGET%\dxgi.dll.qm"
    echo [uninstall] removed legacy marker %TARGET%\dxgi.dll.qm
)

echo.
echo [uninstall] Files remaining (dxgi*):
dir /b "%TARGET%\dxgi*" 2>nul
echo [uninstall] Sidecar folder left untouched (GUI-managed):
dir /b "%TARGET%\Quartermaster" 2>nul
endlocal
