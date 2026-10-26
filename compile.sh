#!/usr/bin/env bash
# Builds a self-contained single-file poe2verlay.exe for Windows x64 into ./dist.
# Usage: ./compile.sh [version]        e.g. ./compile.sh 1.0.1
# The installer needs Inno Setup, which only runs on Windows: use compile.bat there.
set -euo pipefail
cd "$(dirname "$0")"

DOTNET="${DOTNET:-$(command -v dotnet || echo "$HOME/.dotnet/dotnet")}"
if [ ! -x "$DOTNET" ]; then
    echo "The .NET 8 SDK was not found. Install it (no sudo needed) with:" >&2
    echo "  curl -sSL https://dot.net/v1/dotnet-install.sh | bash -s -- --channel 8.0" >&2
    exit 1
fi
[ -f assets/poe2verlay.ico ] || python3 tools/make_icon.py assets/poe2verlay.ico

"$DOTNET" publish poe2verlay.csproj -c Release -o dist --nologo ${1:+"-p:Version=$1"}
echo
ls -lh dist/poe2verlay.exe
