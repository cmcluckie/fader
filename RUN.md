# Running FaderBridge

The bridge finds the X32 by itself — on first run it searches the LAN, then
remembers the console for next time. The only thing you normally set is the
FaderPort's MIDI port name, and only because it differs between machines
(CoreMIDI on macOS, WinMM on Windows).

## On the PC

1. Open **PowerShell**.
2. `cd C:\Users\User\source\repos\cmcluckie\fader`
3. `dotnet run --project diagnostics\MidiMonitor`
   — copy the FaderPort's exact port name into `src\FaderBridge.Core\config.json` (`midiPortName`).
4. `dotnet run --project src\FaderBridge.App`
5. `Ctrl+C` to stop.

## On the Mac

1. Open **Terminal**.
2. `git clone https://github.com/cmcluckie/fader ~/Code/fader && cd ~/Code/fader`
   — or, if already cloned: `cd ~/Code/fader && git pull`
3. `dotnet run --project diagnostics/MidiMonitor`
   — copy the FaderPort's exact port name into `src/FaderBridge.Core/config.json` (`midiPortName`).
4. `dotnet run --project src/FaderBridge.App`
5. `Ctrl+C` to stop.

Run from **Terminal in the GUI session, not over SSH** — CoreMIDI won't
enumerate MIDI devices without a logged-in user session.

## Notes

- **X32 IP is optional.** Leave `x32IpAddress` blank to rely on search + memory,
  or set it to skip straight to a known console (find it on the X32 under
  **Setup → Network**). A working address is remembered either way.
- **First run with the console off** will sit at "searching for X32…" and keep
  retrying; it connects as soon as the console answers.
- **Check the pieces independently** without the full bridge:
  - `dotnet run --project diagnostics/OscPing -- <ip>` — confirm the X32 is reachable (moves channel 1's fader).
  - `dotnet run --project diagnostics/X32LocateTest` — confirm the auto-detect logic (no hardware needed).
