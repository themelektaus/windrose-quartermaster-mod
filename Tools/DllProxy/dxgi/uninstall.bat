@echo off
setlocal
rem Removes the Quartermaster dxgi proxy + the renamed system dxgi.
set TARGET=E:\Games\steamapps\common\Windrose\R5\Binaries\Win64

if exist "%TARGET%\dxgi.dll" (
    del /q "%TARGET%\dxgi.dll"
    echo [uninstall] removed %TARGET%\dxgi.dll
)
if exist "%TARGET%\dxgi_org.dll" (
    del /q "%TARGET%\dxgi_org.dll"
    echo [uninstall] removed %TARGET%\dxgi_org.dll
)

echo.
echo [uninstall] Files remaining (dxgi*):
dir /b "%TARGET%\dxgi*" 2>nul
endlocal
