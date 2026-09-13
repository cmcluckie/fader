# Installers — Feedback Fader

Two double-click installers, one per platform. Each is built on its own OS: the
C++/JUCE engine needs the native toolchain, so there is no cross-building.

## Windows → `dist\FeedbackFader-Setup.exe`

A per-user installer (no admin prompt) built with **Inno Setup 6**.

```powershell
winget install JRSoftware.InnoSetup          # one time
powershell -ExecutionPolicy Bypass -File scripts\package-feedback-fader-win.ps1
```

That script builds the engine + app (via `build-feedback-fader-win.ps1`) and
compiles `installers\windows\FeedbackFader.iss` into `dist\FeedbackFader-Setup.exe`.
Prerequisites for the build itself: Visual Studio 2022 C++ build tools, CMake,
.NET 8+ SDK (see the build script's header).

The installer offers an optional "start when I sign in" task and a Start-menu
shortcut. It installs to `%LOCALAPPDATA%\Programs\Feedback Fader`; user data
stays in `Documents\FeedbackKiller`.

## macOS → `dist/FeedbackFader.dmg`

A drag-to-Applications disk image built with `hdiutil` (ships with macOS).

```bash
scripts/package-feedback-fader-dmg.sh
```

That builds `FeedbackFader.app` (via `build-feedback-fader.sh`, which also copies
the built `fk-engine` in beside it) and wraps it in the image with an
`/Applications` symlink and a short INSTALL note.

**This build is unsigned.** Gatekeeper warns on first launch; the user
right-clicks the app → **Open** → **Open** once to allow it. Opening cleanly
without that step needs code signing + notarization, which needs an Apple
Developer ID — a later step, not wired in here.
