#!/usr/bin/env bash
set -euo pipefail

TARGET="${1:-all}"
CONFIGURATION="${CONFIGURATION:-Release}"
VERSION="${VERSION:-1.0.0}"
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
ARTIFACTS="$ROOT/artifacts"
PUBLISH_PLATFORM="$ROOT/scripts/publish-platform.sh"

publish_target() {
  local target="$1"
  echo
  echo "=== $target ==="
  bash "$PUBLISH_PLATFORM" "$target" "$CONFIGURATION" "$VERSION"
}

publish_desktop() {
  for rid in win-x64 win-arm64 linux-x64 linux-arm64 osx-x64 osx-arm64; do
    publish_target "$rid"
  done
}

case "$TARGET" in
  desktop) publish_desktop ;;
  browser) publish_target browser ;;
  android) publish_target android ;;
  all)
    publish_desktop
    publish_target android
    publish_target browser
    ;;
  *)
    echo "Usage: $0 [all|desktop|browser|android]"
    exit 2
    ;;
esac

echo
echo "Publish finished. Artifacts: $ARTIFACTS"
