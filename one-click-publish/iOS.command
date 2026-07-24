#!/usr/bin/env bash
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd)"

# 可在运行前设置：
# export HELLOCRAB_IOS_CODESIGN_KEY='Apple Distribution: Company (TEAMID)'
# export HELLOCRAB_IOS_PROVISION='ProvisioningProfileName'

"$ROOT/scripts/publish-platform.sh" ios Release 1.0.0
printf '\n发布结束，按回车关闭窗口。'
read -r _
