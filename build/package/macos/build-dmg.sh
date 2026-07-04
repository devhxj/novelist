#!/bin/bash
set -euo pipefail

VERSION="${1:-dev}"
APP_NAME="Novelist"
APP_EXECUTABLE="novelist"
PUBLISH_DIR="${PUBLISH_DIR:-build/bin/novelist}"
APP_BUNDLE="build/bin/${APP_NAME}.app"

if [ ! -d "$PUBLISH_DIR" ]; then
    echo "Publish directory is missing: $PUBLISH_DIR" >&2
    echo "Run bash scripts/novelist-publish.sh osx-arm64 first." >&2
    exit 1
fi

if [ ! -x "$PUBLISH_DIR/$APP_EXECUTABLE" ]; then
    echo "macOS executable is missing or not executable: $PUBLISH_DIR/$APP_EXECUTABLE" >&2
    exit 1
fi

echo "创建 Photino/.NET .app: $APP_BUNDLE"

rm -rf "$APP_BUNDLE"
mkdir -p "$APP_BUNDLE/Contents/MacOS" "$APP_BUNDLE/Contents/Resources"
cp -a "$PUBLISH_DIR"/. "$APP_BUNDLE/Contents/MacOS/"
chmod +x "$APP_BUNDLE/Contents/MacOS/$APP_EXECUTABLE"

APP_BINARY="$APP_BUNDLE/Contents/MacOS/Novelist.App"
if [ ! -x "$APP_BINARY" ]; then
    APP_BINARY="$APP_BUNDLE/Contents/MacOS/${APP_EXECUTABLE}.bin"
    mv "$APP_BUNDLE/Contents/MacOS/$APP_EXECUTABLE" "$APP_BINARY"
fi
APP_BINARY_NAME="$(basename "$APP_BINARY")"
cat > "$APP_BUNDLE/Contents/MacOS/$APP_EXECUTABLE" <<EOF
#!/bin/bash
set -euo pipefail
HERE="\$(cd "\$(dirname "\$0")" && pwd)"
exec "\$HERE/$APP_BINARY_NAME" --desktop "\$@"
EOF
chmod +x "$APP_BINARY" "$APP_BUNDLE/Contents/MacOS/$APP_EXECUTABLE"

# Info.plist
cat > "$APP_BUNDLE/Contents/Info.plist" <<EOF
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN"
  "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
    <key>CFBundleName</key>
    <string>Novelist</string>
    <key>CFBundleDisplayName</key>
    <string>Novelist</string>
    <key>CFBundleIdentifier</key>
    <string>app.novelist.desktop</string>
    <key>CFBundleVersion</key>
    <string>${VERSION}</string>
    <key>CFBundleShortVersionString</key>
    <string>${VERSION}</string>
    <key>CFBundleExecutable</key>
    <string>${APP_EXECUTABLE}</string>
    <key>CFBundlePackageType</key>
    <string>APPL</string>
    <key>LSMinimumSystemVersion</key>
    <string>11.0</string>
    <key>NSHighResolutionCapable</key>
    <true/>
</dict>
</plist>
EOF

# 图标占位
[ -f build/package/macos/novelist.icns ] && cp build/package/macos/novelist.icns "$APP_BUNDLE/Contents/Resources/"

# 生成 DMG
mkdir -p build/dist
hdiutil create -volname "Novelist" -srcfolder "$APP_BUNDLE" -ov -format UDZO \
    "build/dist/novelist-v${VERSION}-macos-arm64.dmg"

echo "DMG → build/dist/novelist-v${VERSION}-macos-arm64.dmg"
