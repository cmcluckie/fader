#!/usr/bin/env bash
#
# Build FaderBridge.app - a macOS menu-bar bundle around the bridge.
#
#   scripts/build-app.sh            # -> dist/FaderBridge.app
#
# Framework-dependent: the .app relies on the installed .NET runtime (the
# apphost locates it via hostfxr; `dotnet` need not be on PATH). RollForward in
# the .csproj lets a net8.0 build run on a newer runtime.
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
PROJECT="$ROOT/src/FaderBridge.MenuBar/FaderBridge.MenuBar.csproj"
RID="osx-arm64"
EXE="FaderBridgeMenuBar"

PUB="$ROOT/dist/publish"
APP="$ROOT/dist/FaderBridge.app"

echo "==> Publishing ($RID, framework-dependent)"
rm -rf "$PUB" "$APP"
dotnet publish "$PROJECT" -c Release -r "$RID" --self-contained false -o "$PUB" --nologo

echo "==> Assembling bundle"
mkdir -p "$APP/Contents/MacOS" "$APP/Contents/Resources"
cp -R "$PUB"/* "$APP/Contents/MacOS/"
chmod +x "$APP/Contents/MacOS/$EXE"

cat > "$APP/Contents/Info.plist" <<PLIST
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN"
  "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
  <key>CFBundleName</key>            <string>FaderBridge</string>
  <key>CFBundleDisplayName</key>     <string>FaderBridge</string>
  <key>CFBundleIdentifier</key>      <string>com.faderbridge.menubar</string>
  <key>CFBundleExecutable</key>      <string>$EXE</string>
  <key>CFBundlePackageType</key>     <string>APPL</string>
  <key>CFBundleShortVersionString</key> <string>1.0</string>
  <key>CFBundleVersion</key>         <string>1</string>
  <key>LSMinimumSystemVersion</key>  <string>11.0</string>
  <key>NSHighResolutionCapable</key> <true/>
  <!-- Menu-bar agent: no Dock icon, no app-switcher entry. -->
  <key>LSUIElement</key>             <true/>
</dict>
</plist>
PLIST

echo "==> Done: $APP"
echo "    Launch:  open \"$APP\""
echo "    Config:  $APP/Contents/MacOS/config.json"
