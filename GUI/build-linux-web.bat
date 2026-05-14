@echo off
setlocal

echo ============================================
echo   Quartermaster - Linux Web Build
echo ============================================

echo.
echo [Clean] Removing previous publish output...
if exist "Web\bin\Publish\linux-x64" rmdir /s /q "Web\bin\Publish\linux-x64"

echo.
echo [Build] Publishing Quartermaster.Web...
dotnet publish Web\Quartermaster.Web.csproj -c Release -r linux-x64 -p:PublishProfile=linux-x64 -p:RuntimeIdentifier=linux-x64

if exist "Web\bin\Publish\linux-x64\Quartermaster.Web" (
    echo.
    echo [OK] Build successful: Web\bin\Publish\linux-x64\Quartermaster.Web
) else (
    echo.
    echo [Error] Build produced no binary
    exit /b 1
)
