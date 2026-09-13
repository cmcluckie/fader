# Package Feedback Fader for Windows into a double-click installer.
#
#   powershell -ExecutionPolicy Bypass -File scripts\package-feedback-fader-win.ps1
#   -> dist\FeedbackFader-Setup.exe
#
# Runs the full build first (engine + app, via build-feedback-fader-win.ps1),
# then compiles installers\windows\FeedbackFader.iss with Inno Setup's ISCC.
#
# Prerequisite beyond the build's own: Inno Setup 6.
#   winget install JRSoftware.InnoSetup
# ISCC is found on PATH or at its default install location automatically.

$ErrorActionPreference = "Stop"

$Root  = Split-Path -Parent $PSScriptRoot
$Iss   = Join-Path $Root "installers\windows\FeedbackFader.iss"
$Build = Join-Path $Root "scripts\build-feedback-fader-win.ps1"
$Out   = Join-Path $Root "dist\FeedbackFader-Setup.exe"

Write-Host "==> Building engine + app"
& powershell -ExecutionPolicy Bypass -File $Build
if ($LASTEXITCODE -ne 0) { throw "build failed" }

# Locate ISCC: PATH first, then the usual install spots.
$iscc = (Get-Command iscc -ErrorAction SilentlyContinue).Source
if (-not $iscc) {
    $candidates = @(
        "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe",   # winget per-user install
        "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
        "${env:ProgramFiles}\Inno Setup 6\ISCC.exe"
    )
    $iscc = $candidates | Where-Object { Test-Path $_ } | Select-Object -First 1
}
if (-not $iscc) {
    throw "Inno Setup (ISCC.exe) not found. Install it with: winget install JRSoftware.InnoSetup"
}

Write-Host "==> Compiling installer with $iscc"
& $iscc $Iss
if ($LASTEXITCODE -ne 0) { throw "ISCC failed" }

Write-Host "==> Done: $Out"
