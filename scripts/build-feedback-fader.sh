#!/usr/bin/env bash
#
# Build FeedbackFader.app - the macOS menu-bar bundle for Feedback Fader.
#
#   scripts/build-feedback-fader.sh          # -> dist/FeedbackFader.app
#
# The DSP engine is a separate process (engine/, C++/JUCE). It is copied in
# beside the app binary when it has been built, because App.FindEngine looks
# for it at AppContext.BaseDirectory/fk-engine first.
#
# Framework-dependent: the .app relies on the installed .NET runtime (the
# apphost locates it via hostfxr; `dotnet` need not be on PATH). RollForward in
# the .csproj lets a net8.0 build run on a newer runtime.
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
PROJECT="$ROOT/src/FeedbackFader.App/FeedbackFader.App.csproj"
RID="osx-arm64"
EXE="FeedbackFader"

PUB="$ROOT/dist/publish-feedback-fader"
APP="$ROOT/dist/FeedbackFader.app"

echo "==> Publishing ($RID, framework-dependent)"
rm -rf "$PUB" "$APP"
dotnet publish "$PROJECT" -c Release -r "$RID" --self-contained false -o "$PUB" --nologo

echo "==> Assembling bundle"
mkdir -p "$APP/Contents/MacOS" "$APP/Contents/Resources"
cp -R "$PUB"/* "$APP/Contents/MacOS/"
chmod +x "$APP/Contents/MacOS/$EXE"

ENGINE="$ROOT/engine/build/fk-engine_artefacts/Release/fk-engine"
if [ -x "$ENGINE" ]; then
  cp "$ENGINE" "$APP/Contents/MacOS/fk-engine"
  echo "==> Bundled fk-engine"
else
  echo "==> fk-engine not built; the tray will say 'engine not built' (build engine/ to include it)"
fi

cat > "$APP/Contents/Info.plist" <<PLIST
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN"
  "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
  <key>CFBundleName</key>            <string>Feedback Fader</string>
  <key>CFBundleDisplayName</key>     <string>Feedback Fader</string>
  <key>CFBundleIdentifier</key>      <string>com.feedbackfader.app</string>
  <key>CFBundleExecutable</key>      <string>$EXE</string>
  <key>CFBundlePackageType</key>     <string>APPL</string>
  <key>CFBundleShortVersionString</key> <string>1.0</string>
  <key>CFBundleVersion</key>         <string>1</string>
  <key>LSMinimumSystemVersion</key>  <string>11.0</string>
  <key>NSHighResolutionCapable</key> <true/>
  <!-- The engine opens the audio interface; the request comes from this bundle. -->
  <key>NSMicrophoneUsageDescription</key>
  <string>Feedback Fader listens to your microphone inputs to find and notch feedback.</string>
  <!-- Menu-bar agent: no Dock icon, no app-switcher entry. -->
  <key>LSUIElement</key>             <true/>
</dict>
</plist>
PLIST

echo "==> Done: $APP"
echo "    Launch:  open \"$APP\""
echo "    Logs:    ~/Documents/FeedbackKiller/logs"
