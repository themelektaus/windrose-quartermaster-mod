#!/bin/bash
set -e

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
cd "$SCRIPT_DIR"

BIN="./Quartermaster.Web"
if [ ! -f "$BIN" ]; then
    BIN="./bin/Publish/linux-x64/Quartermaster.Web"
fi

if [ ! -f "$BIN" ]; then
    echo "Quartermaster.Web binary not found. Run ./build-steam-deck.sh first."
    exit 1
fi

chmod +x "$BIN" 2>/dev/null || true

echo "Starting Quartermaster on http://localhost:17777 ..."
exec "$BIN"
