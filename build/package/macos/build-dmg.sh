#!/bin/bash
set -euo pipefail

VERSION="${1:-dev}"
APP_NAME="goink"
APP_BUNDLE="build/${APP_NAME}.app"
RUNTIME_DIR="build/runtime"

rm -rf "$APP_BUNDLE"
mkdir -p "$APP_BUNDLE/Contents/MacOS"
mkdir -p "$APP_BUNDLE/Contents/Resources/runtime/git"
mkdir -p "$APP_BUNDLE/Contents/Frameworks"

# 二进制
cp "build/bin/$APP_NAME" "$APP_BUNDLE/Contents/MacOS/"

# 运行时
cp "$RUNTIME_DIR/git/git" "$APP_BUNDLE/Contents/Resources/runtime/git/"
cp "$RUNTIME_DIR"/libonnxruntime* "$APP_BUNDLE/Contents/Frameworks/"

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
