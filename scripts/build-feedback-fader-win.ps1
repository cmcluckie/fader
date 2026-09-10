# Build Feedback Fader for Windows - the engine and the tray app, in one folder.
#
#   powershell -ExecutionPolicy Bypass -File scripts\build-feedback-fader-win.ps1
#   -> dist\FeedbackFader-win-x64\FeedbackFader.exe  (+ fk-engine.exe beside it)
#   -> dist\FeedbackFader-win-x64.zip
#
# Run this ON the Windows PC. The C# app cross-compiles from a Mac, but the
# engine is C++/JUCE and needs Microsoft's compiler, so the whole build lives
# here. Prerequisites:
#   - Visual Studio 2022 Build Tools, "Desktop development with C++" workload
#   - CMake 3.22+ (bundled with the Build Tools, or winget install Kitware.CMake)
#   - .NET 8 SDK or newer (winget install Microsoft.DotNet.SDK.8)
#   - Git (CMake fetches JUCE on the first configure; that takes a while)
#
# The app is self-contained: the PC it runs on needs no .NET installed.
# The engine is built with ASIO on (see engine/CMakeLists.txt) - pick
# "Focusrite USB ASIO" in Setup for a Scarlett, not the WASAPI entry.

$ErrorActionPreference = "Stop"

$Root    = Split-Path -Parent $PSScriptRoot
$Rid     = "win-x64"
$Engine  = Join-Path $Root "engine"
$Build   = Join-Path $Engine "build-win"
$Out     = Join-Path $Root "dist\FeedbackFader-$Rid"
$Zip     = "$Out.zip"
$Project = Join-Path $Root "src\FeedbackFader.App\FeedbackFader.App.csproj"

Write-Host "==> Configuring engine (Visual Studio generator)"
cmake -S $Engine -B $Build -A x64
if ($LASTEXITCODE -ne 0) { throw "cmake configure failed" }

Write-Host "==> Building engine + tests (Release)"
cmake --build $Build --config Release --target fk-engine fk-tests
if ($LASTEXITCODE -ne 0) { throw "engine build failed" }

# The detector tests are pure DSP - no device - so a Windows compiler that
# changed a result shows up here rather than in a loud room.
Write-Host "==> Running detector tests"
& (Join-Path $Build "fk-tests_artefacts\Release\fk-tests.exe")
if ($LASTEXITCODE -ne 0) { throw "detector tests failed" }

Write-Host "==> Publishing app ($Rid, self-contained)"
if (Test-Path $Out) { Remove-Item -Recurse -Force $Out }
dotnet publish $Project -c Release -r $Rid --self-contained true -o $Out --nologo
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed" }

# FindEngine looks beside the app first, so the two must travel together.
Copy-Item (Join-Path $Build "fk-engine_artefacts\Release\fk-engine.exe") $Out

Write-Host "==> Zipping"
if (Test-Path $Zip) { Remove-Item -Force $Zip }
Compress-Archive -Path "$Out\*" -DestinationPath $Zip

Write-Host "==> Done: $Out\FeedbackFader.exe"
Write-Host "    Zip:  $Zip"
Write-Host "    Data: $([Environment]::GetFolderPath('MyDocuments'))\FeedbackKiller"
