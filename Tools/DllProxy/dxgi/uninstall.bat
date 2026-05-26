@echo off
setlocal
rem Removes the Quartermaster dxgi proxy + marker.
set TARGET=E:\Games\steamapps\common\Windrose\R5\Binaries\Win64

if exist "%TARGET%\dxgi.dll" (
    del /q "%TARGET%\dxgi.dll"
    echo [uninstall] removed %TARGET%\dxgi.dll
)
if exist "%TARGET%\dxgi.dll.qm" (
    del /q "%TARGET%\dxgi.dll.qm"
    echo [uninstall] removed %TARGET%\dxgi.dll.qm
)

echo.
echo [uninstall] Files remaining (dxgi*):
dir /b "%TARGET%\dxgi*" 2>nul
endlocal
