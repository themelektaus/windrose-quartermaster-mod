@echo off
REM Build the tiny CreateRemoteThread injector.
REM Output: inject.exe (x64, static CRT)

setlocal
set VS_VCVARS="C:\Program Files\Microsoft Visual Studio\2022\Community\VC\Auxiliary\Build\vcvars64.bat"
if not exist %VS_VCVARS% (
    echo [ERROR] VS 2022 Community vcvars64.bat not found at %VS_VCVARS%
    exit /b 1
)
call %VS_VCVARS% >nul

pushd "%~dp0"
cl /nologo /O2 /MT /EHsc /std:c++17 /Fe:inject.exe inject.cpp /link /SUBSYSTEM:CONSOLE Advapi32.lib
if errorlevel 1 (
    echo [ERROR] Build failed.
    popd
    exit /b 1
)
popd

echo.
echo [OK] inject.exe built.
