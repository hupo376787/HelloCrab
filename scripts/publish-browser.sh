#!/usr/bin/env bash
set -euo pipefail
CONFIGURATION="${1:-Release}"
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
PROJECT="$ROOT/src/HelloCrab.Browser/HelloCrab.Browser.csproj"
OUTPUT="$ROOT/publish/browser"

rm -rf "$OUTPUT"
mkdir -p "$OUTPUT"

dotnet workload restore "$PROJECT"
dotnet publish "$PROJECT" -c "$CONFIGURATION" -o "$OUTPUT"
echo "Browser remote published to $OUTPUT"
