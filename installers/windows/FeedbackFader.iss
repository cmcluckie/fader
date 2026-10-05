; Inno Setup script for Feedback Fader (Windows).
;
; Packages the self-contained publish folder (dist\FeedbackFader-win-x64, which
; already includes fk-engine.exe) into a single per-user setup .exe. Per-user so
; it installs without an admin/UAC prompt - a tray utility does not need to live
; in Program Files, and its data already lives in Documents\FeedbackKiller.
;
; Build it with:  scripts\package-feedback-fader-win.ps1
; (that script runs the engine+app build first, then invokes ISCC on this file.)

#define MyAppName "Feedback Fader"
#define MyAppVersion "0.1.0"
#define MyAppPublisher "cmcluckie"
#define MyAppExeName "FeedbackFader.exe"
; Path to the published payload, relative to this .iss file.
#define Payload "..\..\dist\FeedbackFader-win-x64"

[Setup]
AppId={{7F3B2A10-4C9E-4E7A-9E2C-FADER0FEEDBACK}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
; Per-user install: no elevation required.
PrivilegesRequired=lowest
DefaultDirName={localappdata}\Programs\Feedback Fader
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
UninstallDisplayIcon={app}\{#MyAppExeName}
OutputDir=..\..\dist
OutputBaseFilename=FeedbackFader-Setup
Compression=lzma2
SolidCompression=yes
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
WizardStyle=modern
; Close a running copy (via Restart Manager) before replacing its files, so a
; reinstall over a running instance does not fail on locked exes.
CloseApplications=yes
RestartApplications=no

[Tasks]
Name: "startup"; Description: "Start {#MyAppName} when I sign in"; Flags: unchecked

[Files]
; The whole self-contained payload, engine included.
Source: "{#Payload}\*"; DestDir: "{app}"; Flags: recursesubdirs createallsubdirs ignoreversion

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{group}\Uninstall {#MyAppName}"; Filename: "{uninstallexe}"
; Only created if the "startup" task is ticked.
Name: "{userstartup}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: startup

[Run]
; Offer to launch straight after install. It is a tray app, so no window opens -
; look for the menu-bar / notification-area icon.
Filename: "{app}\{#MyAppExeName}"; Description: "Launch {#MyAppName} now"; Flags: nowait postinstall skipifsilent

[UninstallRun]
; The engine is a CHILD process the app spawns, so Restart Manager may not close
; it with the app. Stop both before removing files, or uninstall leaves locked
; exes behind ("some elements could not be removed"). App first, then engine.
Filename: "{sys}\taskkill.exe"; Parameters: "/f /im {#MyAppExeName}"; Flags: runhidden; RunOnceId: "KillApp"
Filename: "{sys}\taskkill.exe"; Parameters: "/f /im fk-engine.exe"; Flags: runhidden; RunOnceId: "KillEngine"
