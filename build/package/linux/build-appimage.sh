#!/bin/bash
set -euo pipefail

VERSION="${1:-dev}"
APP_NAME="novelist"
APPDIR="build/${APP_NAME}.AppDir"
PUBLISH_DIR="${PUBLISH_DIR:-build/bin/novelist}"

if [ ! -d "$PUBLISH_DIR" ]; then
  echo "Publish directory is missing: $PUBLISH_DIR" >&2
  echo "Run bash scripts/novelist-publish.sh linux-x64 first." >&2
  exit 1
fi

if [ ! -x "$PUBLISH_DIR/novelist" ]; then
  echo "Linux executable is missing or not executable: $PUBLISH_DIR/novelist" >&2
  exit 1
fi

rm -rf "$APPDIR"
mkdir -p "$APPDIR"

# .NET publish output
cp -a "$PUBLISH_DIR"/. "$APPDIR/"

# 桌面入口
cat > "$APPDIR/${APP_NAME}.desktop" <<EOF
[Desktop Entry]
Name=Novelist
Comment=智能写作助手
Exec=novelist --desktop
Icon=novelist
Type=Application
Categories=Office;WordProcessor;
Terminal=false
EOF

# 图标（优先专用图标，回退到项目 appicon.png）
if [ -f build/package/linux/novelist.png ]; then
  cp build/package/linux/novelist.png "$APPDIR/"
elif [ -f appicon.png ]; then
  cp appicon.png "$APPDIR/novelist.png"
elif [ -f build/appicon.png ]; then
  cp build/appicon.png "$APPDIR/novelist.png"
else
  echo "Warning: no icon found, AppImage will have no icon" >&2
  touch "$APPDIR/novelist.png"
fi

# AppRun
cat > "$APPDIR/AppRun" <<'APPRUN'
#!/bin/bash
HERE="$(dirname "$(readlink -f "${0}")")"
exec "${HERE}/novelist" --desktop "$@"
APPRUN
chmod +x "$APPDIR/AppRun" "$APPDIR/novelist"

# 下载 appimagetool
if [ ! -f /tmp/appimagetool ]; then
    curl -fsSL -o /tmp/appimagetool \
        "https://github.com/AppImage/AppImageKit/releases/download/continuous/appimagetool-$(uname -m).AppImage"
    chmod +x /tmp/appimagetool
fi

# 生成 AppImage（--appimage-extract-and-run 避免 FUSE 依赖）
ARCH=$(uname -m)
mkdir -p build/dist
/tmp/appimagetool --appimage-extract-and-run "$APPDIR" "build/dist/novelist-v${VERSION}-linux-${ARCH}.AppImage"

echo "AppImage → build/dist/novelist-v${VERSION}-linux-${ARCH}.AppImage"
