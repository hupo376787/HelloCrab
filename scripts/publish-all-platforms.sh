#!/usr/bin/env bash
set -uo pipefail

CONFIGURATION="${1:-Release}"
VERSION="${2:-1.0.0}"
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
SCRIPT="$ROOT/scripts/publish-platform.sh"
HOST_OS="$(uname -s)"
TARGETS=(
  win-x64 win-arm64
  linux-x64 linux-arm64
  osx-x64 osx-arm64
  browser android
)

if [[ "$HOST_OS" == "Darwin" || "${INCLUDE_IOS:-0}" == "1" ]]; then
  TARGETS+=(ios)
fi

FAILURES=()
for target in "${TARGETS[@]}"; do
  echo
  echo '============================================================'
  echo "开始发布：$target"
  echo '============================================================'
  if ! "$SCRIPT" "$target" "$CONFIGURATION" "$VERSION"; then
    FAILURES+=("$target")
    echo "发布失败：$target" >&2
  fi
done

echo
echo "发布产物目录：$ROOT/artifacts"

if (( ${#FAILURES[@]} > 0 )); then
  echo "以下目标发布失败：${FAILURES[*]}" >&2
  exit 1
fi

echo '全部目标发布完成。'
