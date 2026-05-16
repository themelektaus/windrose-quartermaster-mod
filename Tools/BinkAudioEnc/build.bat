@echo off
setlocal enabledelayedexpansion

set "VSCOMM=C:\Program Files\Microsoft Visual Studio\2022\Community"
set "VCVARS=%VSCOMM%\VC\Auxiliary\Build\vcvars64.bat"

if not exist "%VCVARS%" (
    echo [ERR] vcvars64.bat not found at "%VCVARS%"
    exit /b 1
)

set "UE_BINKA=E:\Windrose\Mods\Quartermaster\References\UE_5.7\Engine\Source\Runtime\BinkAudioDecoder\SDK\BinkAudio"
set "BINKA_INCLUDE=%UE_BINKA%\Include"
set "BINKA_LIB=%UE_BINKA%\Lib\binka_ue_encode_win64_static.lib"

if not exist "%BINKA_LIB%" (
    echo [ERR] Encoder lib not found: "%BINKA_LIB%"
    exit /b 1
)

set "OUT_EXE=E:\Windrose\Mods\Quartermaster\Tools\binkaudioenc.exe"
set "BUILD_DIR=%~dp0obj"
if not exist "%BUILD_DIR%" mkdir "%BUILD_DIR%"

echo === Activating MSVC environment ===
call "%VCVARS%" >nul
if errorlevel 1 exit /b 1

echo === Compiling and linking binkaudioenc.exe ===
cl.exe /nologo /EHsc /MD /O2 /std:c++17 /W3 ^
    /I "%BINKA_INCLUDE%" ^
    /Fo"%BUILD_DIR%\\" ^
    "%~dp0binkaudioenc.cpp" ^
    /link ^
    "%BINKA_LIB%" ^
    /OUT:"%OUT_EXE%"

if errorlevel 1 (
    echo [ERR] Build failed.
    exit /b 1
)

echo.
echo === Build OK: %OUT_EXE% ===
