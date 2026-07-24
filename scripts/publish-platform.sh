#!/usr/bin/env bash
set -euo pipefail

TARGET="${1:-linux-x64}"
CONFIGURATION="${2:-Release}"
VERSION="${3:-1.0.0}"

case "$TARGET" in
  win-x64|win-arm64|linux-x64|linux-arm64|osx-x64|osx-arm64|browser|android|ios-simulator|ios) ;;
  *)
    echo "不支持的目标：$TARGET" >&2
    exit 2
    ;;
esac

if [[ ! "$VERSION" =~ ^[0-9A-Za-z][0-9A-Za-z._-]*$ ]]; then
  echo "Version 只能包含字母、数字、点、下划线和连字符。" >&2
  exit 2
fi

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
ARTIFACTS="$ROOT/artifacts"
STAGING="$ARTIFACTS/.staging"
HOST_OS="$(uname -s)"

require_command() {
  if ! command -v "$1" >/dev/null 2>&1; then
    echo "未找到命令：$1。请先安装并加入 PATH。" >&2
    exit 127
  fi
}

run() {
  printf '\n> '
  printf '%q ' "$@"
  printf '\n'
  "$@"
}

reset_dir() {
  rm -rf "$1"
  mkdir -p "$1"
}

zip_package() {
  local package_dir="$1"
  local archive="$2"
  local prefer_ditto="${3:-0}"

  rm -f "$archive"
  if [[ "$prefer_ditto" == "1" && "$HOST_OS" == "Darwin" ]] && command -v ditto >/dev/null 2>&1; then
    run ditto -c -k --sequesterRsrc --keepParent "$package_dir" "$archive"
  else
    require_command zip
    (
      cd "$(dirname "$package_dir")"
      run zip -q -r "$archive" "$(basename "$package_dir")"
    )
  fi
}

complete_package() {
  local package_name="$1"
  local source_dir="$2"
  local prefer_ditto="${3:-0}"
  local package_dir="$STAGING/$package_name"
  local archive="$ARTIFACTS/$package_name.zip"

  if [[ ! -d "$source_dir" ]]; then
    echo "发布目录不存在：$source_dir" >&2
    exit 1
  fi

  reset_dir "$package_dir"
  cp -a "$source_dir/." "$package_dir/"
  zip_package "$package_dir" "$archive" "$prefer_ditto"
  echo
  echo "打包完成：$archive"
}

collect_packages() {
  local search_root="$1"
  local package_dir="$2"
  shift 2
  local found=0

  reset_dir "$package_dir"
  if [[ -d "$search_root" ]]; then
    while IFS= read -r -d '' file; do
      cp -f "$file" "$package_dir/"
      echo "已收集：$file"
      found=1
    done < <(find "$search_root" -type f \( "$@" \) -print0)
  fi

  if [[ "$found" == "0" ]]; then
    return 1
  fi
}

configure_xcode_for_ios_26_0() {
  if [[ "$HOST_OS" != "Darwin" ]]; then
    return
  fi

  local developer_dir="${DEVELOPER_DIR:-}"
  local xcode_app="${HELLOCRAB_XCODE_PATH:-}"

  if [[ -n "$xcode_app" ]]; then
    developer_dir="$xcode_app/Contents/Developer"
  fi

  if [[ -z "$developer_dir" || ! -d "$developer_dir" ]]; then
    local candidate
    for candidate in \
      /Applications/Xcode_26.0.1.app \
      /Applications/Xcode_26.0.app \
      /Applications/Xcode_26.0*.app; do
      if [[ -d "$candidate/Contents/Developer" ]]; then
        xcode_app="$candidate"
        developer_dir="$candidate/Contents/Developer"
        break
      fi
    done
  fi

  if [[ -z "$developer_dir" || ! -d "$developer_dir" ]]; then
    echo "未找到与 net10.0-ios26.0 匹配的 Xcode 26.0。" >&2
    echo "请安装 Xcode 26.0，或通过 HELLOCRAB_XCODE_PATH 指定其 .app 路径。" >&2
    echo "当前已安装的 Xcode：" >&2
    find /Applications -maxdepth 1 -name 'Xcode*.app' -print 2>/dev/null | sort >&2 || true
    exit 1
  fi

  export DEVELOPER_DIR="$developer_dir"
  local version_line
  version_line="$(xcodebuild -version | head -n 1)"
  if [[ "$version_line" != "Xcode 26.0"* ]]; then
    echo "当前选择的是 $version_line，但 net10.0-ios26.0 需要 Xcode 26.0。" >&2
    echo "DEVELOPER_DIR=$DEVELOPER_DIR" >&2
    echo "请通过 HELLOCRAB_XCODE_PATH 指向 Xcode 26.0。" >&2
    exit 1
  fi

  echo "iOS 构建使用：$version_line"
  echo "DEVELOPER_DIR=$DEVELOPER_DIR"
}

require_command dotnet
mkdir -p "$ARTIFACTS"
reset_dir "$STAGING"

case "$TARGET" in
  win-x64|win-arm64|linux-x64|linux-arm64|osx-x64|osx-arm64)
    run "$ROOT/scripts/publish-desktop.sh" "$TARGET" "$CONFIGURATION"
    complete_package \
      "HelloCrab-Desktop-$TARGET-$VERSION" \
      "$ROOT/publish/desktop/$TARGET" \
      "$([[ "$TARGET" == osx-* ]] && echo 1 || echo 0)"
    ;;

  browser)
    run "$ROOT/scripts/publish-browser.sh" "$CONFIGURATION"
    complete_package "HelloCrab-Browser-$VERSION" "$ROOT/publish/browser"
    ;;

  android)
    PROJECT="$ROOT/src/HelloCrab.Android/HelloCrab.Android.csproj"
    FRAMEWORK="net10.0-android36.0"
    rm -rf "$ROOT/src/HelloCrab.Android/bin/$CONFIGURATION/$FRAMEWORK"
    run dotnet workload restore "$PROJECT"
    # Escape the semicolon for MSBuild so both formats remain one property value.
    run dotnet publish "$PROJECT" \
      -c "$CONFIGURATION" \
      -f "$FRAMEWORK" \
      "-p:ApplicationDisplayVersion=$VERSION" \
      '-p:AndroidPackageFormats=apk%3Baab'

    PACKAGE_NAME="HelloCrab-Android-$VERSION"
    PACKAGE_DIR="$STAGING/$PACKAGE_NAME"
    SEARCH_ROOT="$ROOT/src/HelloCrab.Android/bin/$CONFIGURATION/$FRAMEWORK"
    if ! collect_packages "$SEARCH_ROOT" "$PACKAGE_DIR" -name '*.apk' -o -name '*.aab'; then
      echo "没有在 $SEARCH_ROOT 中找到 APK 或 AAB。" >&2
      exit 1
    fi
    zip_package "$PACKAGE_DIR" "$ARTIFACTS/$PACKAGE_NAME.zip"
    echo
    echo "打包完成：$ARTIFACTS/$PACKAGE_NAME.zip"
    ;;

  ios-simulator)
    if [[ "$HOST_OS" != "Darwin" ]]; then
      echo "iOS Simulator 构建必须在 macOS 上运行。" >&2
      exit 1
    fi

    PROJECT="$ROOT/src/HelloCrab.iOS/HelloCrab.iOS.csproj"
    FRAMEWORK="net10.0-ios26.0"
    RUNTIME="iossimulator-arm64"
    SEARCH_ROOT="$ROOT/src/HelloCrab.iOS/bin/$CONFIGURATION"

    configure_xcode_for_ios_26_0
    rm -rf "$ROOT/src/HelloCrab.iOS/bin/$CONFIGURATION/$FRAMEWORK"
    run dotnet workload restore "$PROJECT"
    run dotnet build "$PROJECT" \
      -c "$CONFIGURATION" \
      -f "$FRAMEWORK" \
      "-p:RuntimeIdentifier=$RUNTIME" \
      '-p:EnableCodeSigning=false' \
      "-p:ApplicationDisplayVersion=$VERSION"

    APP_PATH="$(find "$SEARCH_ROOT" -type d -name '*.app' -path "*$RUNTIME*" -print -quit 2>/dev/null || true)"
    if [[ -z "$APP_PATH" || ! -d "$APP_PATH" ]]; then
      echo "没有在 $SEARCH_ROOT 中找到 iOS Simulator .app。" >&2
      exit 1
    fi

    PACKAGE_NAME="HelloCrab-iOS-Simulator-arm64-$VERSION"
    PACKAGE_DIR="$STAGING/$PACKAGE_NAME"
    reset_dir "$PACKAGE_DIR"
    cp -a "$APP_PATH" "$PACKAGE_DIR/"
    cat > "$PACKAGE_DIR/README.txt" <<'README'
This package is an unsigned Apple Silicon iOS Simulator build.
It is intended for testing with Xcode Simulator and cannot be installed on a physical iPhone or iPad.
Configure the GitHub iOS signing secrets to also produce a signed IPA for physical devices.
README
    zip_package "$PACKAGE_DIR" "$ARTIFACTS/$PACKAGE_NAME.zip" 1
    echo
    echo "打包完成：$ARTIFACTS/$PACKAGE_NAME.zip"
    ;;

  ios)
    PROJECT="$ROOT/src/HelloCrab.iOS/HelloCrab.iOS.csproj"
    FRAMEWORK="net10.0-ios26.0"

    if [[ "$HOST_OS" != "Darwin" && -z "${HELLOCRAB_IOS_SERVER_ADDRESS:-}" ]]; then
      cat >&2 <<'MSG'
iOS 发布需要 macOS + Xcode。
若从 Windows/Linux 远程构建，请设置 HELLOCRAB_IOS_SERVER_ADDRESS、
HELLOCRAB_IOS_SERVER_USER，以及可选的 HELLOCRAB_IOS_SERVER_PASSWORD、
HELLOCRAB_IOS_REMOTE_DOTNET_ROOT。
MSG
      exit 1
    fi

    if [[ "$HOST_OS" == "Darwin" ]]; then
      configure_xcode_for_ios_26_0
    fi
    rm -rf "$ROOT/src/HelloCrab.iOS/bin/$CONFIGURATION/$FRAMEWORK"
    run dotnet workload restore "$PROJECT"
    IOS_ARGS=(
      publish "$PROJECT"
      -c "$CONFIGURATION"
      -f "$FRAMEWORK"
      '-p:RuntimeIdentifier=ios-arm64'
      '-p:ArchiveOnBuild=true'
      "-p:ApplicationDisplayVersion=$VERSION"
    )

    [[ -n "${HELLOCRAB_IOS_CODESIGN_KEY:-}" ]] && IOS_ARGS+=("-p:CodesignKey=$HELLOCRAB_IOS_CODESIGN_KEY")
    [[ -n "${HELLOCRAB_IOS_PROVISION:-}" ]] && IOS_ARGS+=("-p:CodesignProvision=$HELLOCRAB_IOS_PROVISION")
    [[ -n "${HELLOCRAB_IOS_ENTITLEMENTS:-}" ]] && IOS_ARGS+=("-p:CodesignEntitlements=$HELLOCRAB_IOS_ENTITLEMENTS")

    if [[ "$HOST_OS" != "Darwin" ]]; then
      IOS_ARGS+=("-p:ServerAddress=$HELLOCRAB_IOS_SERVER_ADDRESS")
      [[ -n "${HELLOCRAB_IOS_SERVER_USER:-}" ]] && IOS_ARGS+=("-p:ServerUser=$HELLOCRAB_IOS_SERVER_USER")
      [[ -n "${HELLOCRAB_IOS_SERVER_PASSWORD:-}" ]] && IOS_ARGS+=("-p:ServerPassword=$HELLOCRAB_IOS_SERVER_PASSWORD")
      [[ -n "${HELLOCRAB_IOS_REMOTE_DOTNET_ROOT:-}" ]] && IOS_ARGS+=("-p:_DotNetRootRemoteDirectory=$HELLOCRAB_IOS_REMOTE_DOTNET_ROOT")
      IOS_ARGS+=('-p:TcpPort=58181')
    fi

    run dotnet "${IOS_ARGS[@]}"

    PACKAGE_NAME="HelloCrab-iOS-$VERSION"
    PACKAGE_DIR="$STAGING/$PACKAGE_NAME"
    SEARCH_ROOT="$ROOT/src/HelloCrab.iOS/bin/$CONFIGURATION/$FRAMEWORK"
    if ! collect_packages "$SEARCH_ROOT" "$PACKAGE_DIR" -name '*.ipa'; then
      echo "没有在 $SEARCH_ROOT 中找到 IPA。请检查 Apple 证书和 Provisioning Profile。" >&2
      exit 1
    fi
    zip_package "$PACKAGE_DIR" "$ARTIFACTS/$PACKAGE_NAME.zip" 1
    echo
    echo "打包完成：$ARTIFACTS/$PACKAGE_NAME.zip"
    ;;
esac
