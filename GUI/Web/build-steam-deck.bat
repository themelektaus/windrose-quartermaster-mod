@echo off
setlocal

echo ============================================
echo   Quartermaster - Steam Deck Build
echo ============================================

echo.
echo [Clean] Removing previous publish output...
if exist "bin\Publish\linux-x64" rmdir /s /q "bin\Publish\linux-x64"

echo.
echo [Build] Publishing Quartermaster.Web...
dotnet publish Quartermaster.Web.csproj -c Release -r linux-x64 -p:PublishProfile=linux-x64 -p:RuntimeIdentifier=linux-x64

if exist "bin\Publish\linux-x64\Quartermaster.Web" (
    echo.
    echo [OK] Build successful: bin\Publish\linux-x64\Quartermaster.Web
) else (
    echo.
    echo [Error] Build produced no binary
    exit /b 1
)
