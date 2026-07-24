#!/usr/bin/env bash
set -euo pipefail

RUNTIME="${1:-linux-x64}"
CONFIGURATION="${2:-Release}"

case "$RUNTIME" in
  win-x64|win-arm64|linux-x64|linux-arm64|osx-x64|osx-arm64) ;;
  *)
    echo "Unsupported runtime: $RUNTIME" >&2
    exit 2
    ;;
esac

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
PROJECT="$ROOT/src/HelloCrab.Desktop/HelloCrab.Desktop.csproj"
OUTPUT_ROOT="$ROOT/publish/desktop/$RUNTIME"
RAW_OUTPUT="$OUTPUT_ROOT"

rm -rf "$OUTPUT_ROOT"
mkdir -p "$OUTPUT_ROOT"

if [[ "$RUNTIME" == osx-* ]]; then
  RAW_OUTPUT="$OUTPUT_ROOT/raw"
fi

PUBLISH_ARGS=(
  "$PROJECT"
  --configuration "$CONFIGURATION"
  --runtime "$RUNTIME"
  --self-contained true
  --output "$RAW_OUTPUT"
  -p:UseAppHost=true
  -p:PublishSingleFile=false
)


dotnet publish "${PUBLISH_ARGS[@]}"

if [[ "$RUNTIME" == osx-* ]]; then
  APP="$OUTPUT_ROOT/HelloCrab.app"
  mkdir -p "$APP/Contents/MacOS" "$APP/Contents/Resources"
  cp -a "$RAW_OUTPUT/." "$APP/Contents/MacOS/"
  cp "$ROOT/packaging/macos/Info.plist" "$APP/Contents/Info.plist"
  cp "$ROOT/src/HelloCrab.Desktop/Assets/app-icon.icns" \
     "$APP/Contents/Resources/app-icon.icns"

  chmod +x "$APP/Contents/MacOS/HelloCrab"
  if [[ -d "$APP/Contents/MacOS/.playwright" ]]; then
    find "$APP/Contents/MacOS/.playwright" -type f -exec chmod u+x {} +
  fi
  rm -rf "$RAW_OUTPUT"
  echo "macOS app bundle: $APP"
elif [[ "$RUNTIME" == linux-* ]]; then
  chmod +x "$OUTPUT_ROOT/HelloCrab"
  mkdir -p "$OUTPUT_ROOT/Assets"
  cp "$ROOT/src/HelloCrab.Desktop/Assets/app-icon.png" \
     "$OUTPUT_ROOT/Assets/app-icon.png"
  cp "$ROOT/packaging/linux/HelloCrab.desktop" \
     "$OUTPUT_ROOT/HelloCrab.desktop.template"
  cat > "$OUTPUT_ROOT/install-desktop-entry.sh" <<'INSTALL'
#!/usr/bin/env bash
set -euo pipefail
HERE="$(cd "$(dirname "$0")" && pwd)"
TARGET="$HOME/.local/share/applications/social-media-crawler.desktop"
mkdir -p "$(dirname "$TARGET")"
sed \
  -e "s|__EXECUTABLE__|$HERE/HelloCrab|g" \
  -e "s|__ICON__|$HERE/Assets/app-icon.png|g" \
  "$HERE/HelloCrab.desktop.template" > "$TARGET"
chmod +x "$HERE/HelloCrab"
echo "Installed desktop entry: $TARGET"
INSTALL
  chmod +x "$OUTPUT_ROOT/install-desktop-entry.sh"
  echo "Linux executable: $OUTPUT_ROOT/HelloCrab"
else
  echo "Windows executable: $OUTPUT_ROOT/HelloCrab.exe"
fi

echo "Published to $OUTPUT_ROOT"
echo "Chromium is installed from the app's '安装 Chromium' button on the target machine."
