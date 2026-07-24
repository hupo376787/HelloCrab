#!/usr/bin/env bash
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
"$ROOT/scripts/publish-platform.sh" android Release 1.0.0
printf '\n发布结束，按回车关闭窗口。'
read -r _
