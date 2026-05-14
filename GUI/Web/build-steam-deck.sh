#!/bin/bash
set -e

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
cd "$SCRIPT_DIR"

echo "====================================="
echo "  Quartermaster - Steam Deck Build"
echo "====================================="
echo ""

if ! command -v dotnet &> /dev/null; then
    echo "FAIL dotnet not found. Install .NET 10 SDK:"
    echo "  https://dotnet.microsoft.com/download/dotnet/10.0"
    exit 1
fi

echo "[dotnet] Version: $(dotnet --version)"
echo ""

echo "[Clean] Removing previous publish output..."
rm -rf bin/Publish/linux-x64 2>/dev/null || true

echo ""
echo "[Build] Publishing Quartermaster.Web (linux-x64, single-file, self-contained)..."
dotnet publish Quartermaster.Web.csproj \
    -c Release \
    -r linux-x64 \
    -p:PublishProfile=linux-x64 \
    -p:RuntimeIdentifier=linux-x64

OUT="bin/Publish/linux-x64/Quartermaster.Web"
if [ -f "$OUT" ]; then
    chmod +x "$OUT"
    echo ""
    echo "OK Build successful: $OUT"
    echo ""
    echo "To run:"
    echo "  cd bin/Publish/linux-x64 && ./start.sh"
    echo ""
    echo "WARN repak/retoc are Windows .exe binaries. To use them on the Deck"
    echo "     you need Wine (or Steam's bundled Proton) - place the .exe files"
    echo "     under  <DataRoot>/Tools/repak/  and  <DataRoot>/Tools/retoc/"
    echo "     and the SetupRunner will invoke them through wine."
else
    echo ""
    echo "FAIL Build produced no binary at $OUT"
    exit 1
fi
