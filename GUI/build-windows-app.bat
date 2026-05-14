@echo off
setlocal

echo ============================================
echo   Quartermaster - Steam Windows App Build
echo ============================================

echo.
echo [Clean] Removing previous publish output...
if exist "App\bin\Publish\win-x64" rmdir /s /q "App\bin\Publish\win-x64"

echo.
echo [Build] Publishing Quartermaster.Web...
dotnet publish App\Quartermaster.App.csproj -c Release -r win-x64 -p:PublishProfile=win-x64 -p:RuntimeIdentifier=win-x64

if exist "App\bin\Publish\win-x64\Quartermaster.exe" (
    echo.
    echo [OK] Build successful: App\bin\Publish\win-x64\Quartermaster.exe
) else (
    echo.
    echo [Error] Build produced no binary
    exit /b 1
)
