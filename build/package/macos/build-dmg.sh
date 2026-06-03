#!/bin/bash
set -euo pipefail

VERSION="${1:-dev}"
APP_NAME="goink"
# Wails on macOS outputs a .app bundle at build/bin/goink.app
APP_BUNDLE="build/bin/${APP_NAME}.app"
RUNTIME_DIR="build/runtime"

echo "使用 Wails 生成的 .app: $APP_BUNDLE"

# 将运行时注入已有 .app bundle
mkdir -p "$APP_BUNDLE/Contents/Resources/runtime/git"
mkdir -p "$APP_BUNDLE/Contents/Frameworks"
cp "$RUNTIME_DIR/git/git" "$APP_BUNDLE/Contents/Resources/runtime/git/"
find "$RUNTIME_DIR" -maxdepth 1 -type f \( -name "libonnxruntime*" -o -name "*.pc" \) -exec cp {} "$APP_BUNDLE/Contents/Frameworks/" \;

# Info.plist
cat > "$APP_BUNDLE/Contents/Info.plist" <<EOF
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN"
  "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
    <key>CFBundleName</key>
    <string>Goink</string>
    <key>CFBundleDisplayName</key>
    <string>Goink</string>
    <key>CFBundleIdentifier</key>
    <string>com.goink.app</string>
    <key>CFBundleVersion</key>
    <string>${VERSION}</string>
    <key>CFBundleShortVersionString</key>
    <string>${VERSION}</string>
    <key>CFBundleExecutable</key>
    <string>goink</string>
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
[ -f build/package/macos/goink.icns ] && cp build/package/macos/goink.icns "$APP_BUNDLE/Contents/Resources/"

# 生成 DMG
mkdir -p build/dist
hdiutil create -volname "Goink" -srcfolder "$APP_BUNDLE" -ov -format UDZO \
    "build/dist/goink-${VERSION}-macos-universal.dmg"

echo "DMG → build/dist/goink-${VERSION}-macos-universal.dmg"
