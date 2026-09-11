#!/usr/bin/env bash
set -euo pipefail
CONFIGURATION="${1:-Release}"
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
PROJECT="$ROOT/src/HelloCrab.Browser/HelloCrab.Browser.csproj"
OUTPUT="$ROOT/publish/browser"
RAW_OUTPUT="$ROOT/publish/browser-raw"

rm -rf "$OUTPUT" "$RAW_OUTPUT"
mkdir -p "$OUTPUT" "$RAW_OUTPUT"

dotnet workload restore "$PROJECT"
dotnet publish "$PROJECT" -c "$CONFIGURATION" -o "$RAW_OUTPUT"

if [[ -f "$RAW_OUTPUT/wwwroot/index.html" ]]; then
  STATIC_ROOT="$RAW_OUTPUT/wwwroot"
elif [[ -f "$RAW_OUTPUT/index.html" ]]; then
  STATIC_ROOT="$RAW_OUTPUT"
else
  echo "Browser publish output does not contain index.html: $RAW_OUTPUT" >&2
  exit 1
fi

cp -a "$STATIC_ROOT/." "$OUTPUT/"
rm -rf "$RAW_OUTPUT"

echo "Browser static site published to $OUTPUT"
