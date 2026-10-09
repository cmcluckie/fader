#!/usr/bin/env bash
#
# Package Feedback Fader into a macOS .dmg (drag-to-Applications installer).
#
#   scripts/package-feedback-fader-dmg.sh     # -> dist/FeedbackFader.dmg
#
# Builds the .app first (build-feedback-fader.sh), then wraps it in a disk image
# with a symlink to /Applications, so the user drags the app across. Uses only
# hdiutil, which ships with macOS - no extra tooling.
#
# UNSIGNED: this build is not code-signed or notarized, so Gatekeeper will warn
# on first launch. The INSTALL.md that ships in the image tells the user to
# right-click the app and choose Open once to get past it. Signing/notarization
# is a later step and needs an Apple Developer ID.
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
APP="$ROOT/dist/FeedbackFader.app"
STAGE="$ROOT/dist/dmg-stage"
DMG="$ROOT/dist/FeedbackFader.dmg"
VOLNAME="Feedback Fader"

echo "==> Building the .app"
"$ROOT/scripts/build-feedback-fader.sh"

if [ ! -d "$APP" ]; then
  echo "error: $APP was not produced by the build" >&2
  exit 1
fi

echo "==> Staging disk-image contents"
rm -rf "$STAGE" "$DMG"
mkdir -p "$STAGE"
cp -R "$APP" "$STAGE/"
ln -s /Applications "$STAGE/Applications"       # drag target

# A short, honest first-launch note, because the build is unsigned.
cat > "$STAGE/INSTALL.txt" <<'TXT'
Feedback Fader - install

1. Drag "FeedbackFader.app" onto the Applications folder shown here.
2. The first time you open it, macOS will say it is from an unidentified
   developer (this build is not signed). To allow it:
       right-click the app in Applications  ->  Open  ->  Open
   You only need to do this once.
3. It runs in the menu bar (no window). Click the menu-bar icon for its menu.

Your settings and audio selection live in ~/Documents/FeedbackKiller.
TXT

echo "==> Creating $DMG"
hdiutil create -volname "$VOLNAME" \
  -srcfolder "$STAGE" \
  -ov -format UDZO \
  "$DMG"

rm -rf "$STAGE"
echo "==> Done: $DMG"
