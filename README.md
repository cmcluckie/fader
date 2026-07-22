# FaderPort 8 ↔ X32 Rack OSC Bridge

macOS bridge between a PreSonus FaderPort 8 (USB MIDI, Mackie Control) and a
Behringer X32 Rack (OSC over UDP 10023). Bidirectional: the surface drives the
console, and the console drives the motors, LEDs and scribble strips back.

## Prerequisites

- .NET 8 SDK or newer
- FaderPort 8 in **MCU mode** (hold `NEXT` while powering on, choose MCU)
- Mac and X32 on the same subnet

## Configure

Edit `src/FaderBridge/config.json` — at minimum the console's IP, which you'll
find on the X32 under **Setup → Network**:

```json
{
  "x32IpAddress": "192.168.1.100",
  "midiPortName": "FaderPort",
  "stripCount": 8,
  "x32ChannelCount": 32,
  "keepaliveSeconds": 9.0,
  "echoSuppressionMs": 150,
  "resyncSeconds": 5.0
}
```

`resyncSeconds` re-pulls the whole bank periodically. The X32 does not reliably
broadcast every parameter after a scene recall, so a push-only bridge goes
quietly stale; this is what catches it. Set to `0` to disable.

`midiPortName` is a case-insensitive substring; it must match exactly one port.
If it matches none or several, the bridge lists what it found and exits.

## Build

All four projects build as a unit from `Fader.sln`:

```bash
dotnet build Fader.sln
```

## Run

```bash
dotnet run --project src/FaderBridge
```

Run from **Terminal in the GUI session, not over SSH** — CoreMIDI will not
enumerate devices without a logged-in user session.

| Control | Does |
|---|---|
| Faders | `/ch/NN/mix/fader`, motorised both ways |
| Mute | `/ch/NN/mix/on` (inverted: 0 = muted), lamp follows console |
| Solo | `/-stat/solosw/NN` |
| Select | `/-stat/selidx` |
| Bank Left/Right | shift the 8-fader window by 8 |
| Channel Left/Right | shift the window by 1 |
| Scribble strips | channel name (upper), channel number (lower) |

## Diagnostics

Three, all runnable independently.

**Bridge self-test** — no hardware needed. Runs the real bridge against a mock
console over a real UDP socket:

```bash
dotnet run --project diagnostics/BridgeSelfTest
```

**FaderPort MIDI monitor** — prints every incoming message decoded, using the
bridge's own parser:

```bash
dotnet run --project diagnostics/MidiMonitor -- FaderPort
```

Check that faders reach `0` and `16383` at the extremes, touch prints
`TOUCH DOWN`/`UP` for the right strip, and nothing prints `(unmapped)`.

**X32 OSC path check** — four steps: `/info` round-trip, read fader, set fader
(channel 1 should physically move), read back:

```bash
dotnet run --project diagnostics/OscPing -- <x32-ip>
```

## What's verified, and what isn't

**Verified (49/49 self-test assertions, on real UDP):** OSC encoding is
byte-for-byte spec-correct; fader scaling round-trips exactly with no creep;
MCU scribble SysEx layout and offsets; fader-touch gating suppresses motor
updates and resyncs on release; echo suppression stops the console's echo of our
own move re-driving the motor; the inverted mute sense in both directions; bank
windowing, including clamping at both ends and ignoring off-bank channels;
recovery from a silent scene recall, without stomping a held fader.

The suite was mutation-tested — injecting a non-inverted mute and a disabled
touch gate produced exactly the expected failures, so the assertions have teeth.

**Not verified — needs your hardware.** All of the above ran against a *mock*
console and a *fake* surface. What that proves is that the logic is right given
the protocol assumptions. What it cannot prove is that the assumptions match your
two devices. Specifically, still unconfirmed:

| Assumption | Risk | How you'll know |
|---|---|---|
| MCU note numbers (mute 16–23, touch 104–111, etc.) | Medium | `MidiMonitor` — mislabelled or `(unmapped)` output |
| FaderPort needs no MCU host handshake to enable motors/LEDs | Medium | `MidiMonitor` — a device-query SysEx appearing at startup |
| FaderPort honours standard MCU display SysEx at device ID `0x14` | Medium | scribble strips stay blank |
| `/-stat/solosw/NN` and `/-stat/selidx` on Rack firmware | Low-medium | solo/select do nothing; `OscPing` can probe them |
| `/ch/NN/...` fader, mix/on, config/name | Low | `OscPing` already exercises fader |

## Open decisions

- **Transport buttons are deliberately unmapped.** An X32 Rack has no transport
  of its own — the only candidate is the USB recorder, whose OSC paths vary
  between firmware versions. Rather than guess, the bridge logs the press. Tell
  me what you want Play/Stop/Record to do and it's a few lines each.
- **Scribble-strip colour** is not implemented (it was flagged as a stretch
  goal). The X32 exposes `/ch/NN/config/color`; the FaderPort's colour protocol
  isn't documented by PreSonus.

## Library choice

`managed-midi` (`Commons.Music.Midi`), binding `CoreMidiApi.CoreMidiAccess`
**explicitly** rather than via `MidiAccessManager.Default`.

RtMidi.Core was rejected: version 1.0.53 bundles a single `librtmidi.dylib` that
is x86_64-only (Mach-O cputype `0x01000007`, no arm64 slice), so on Apple
Silicon it fails to load unless the whole app is forced under Rosetta.
managed-midi ships **no** native binaries — it P/Invokes CoreMIDI directly, so
it runs native on Apple Silicon and Intel alike.

`MidiAccessManager.Default` is avoided because it can resolve to the RtMidi
backend on macOS and reintroduce that dependency. `MidiBackend` falls back to
RtMidi only if CoreMIDI fails outright, in which case: `brew install rtmidi`.

## Layout

```
src/FaderBridge/
  Program.cs              startup, config load, graceful shutdown
  BridgeConfig.cs         JSON config + validation
  Bridge/BridgeHost.cs    both directions, touch gating, banking
  Bridge/FaderScaling.cs  MCU 14-bit <-> X32 float  (tune the taper here)
  Midi/McuProtocol.cs     note map, motor/LED/scribble message construction
  Midi/MidiStreamParser.cs
  Midi/FaderPortDevice.cs MIDI in/out
  Midi/IControlSurface.cs so the bridge is testable without hardware
  Osc/OscMessage.cs       hand-rolled OSC 1.0 codec, incl. bundles
  Osc/X32Client.cs        UDP + /xremote keepalive
  Osc/X32Address.cs       address construction and parsing
diagnostics/
  BridgeSelfTest/  MidiMonitor/  OscPing/
```

The diagnostics link the bridge's real source files rather than copying them, so
a clean diagnostic run means the bridge's own code is what passed.

## Tuning

`FaderScaling` is currently a linear position map. X32 fader floats are not
linear in dB (`0.0` = −inf, `0.75` ≈ 0 dB, `1.0` = +10 dB). If the FaderPort's
markings don't line up with the console's readout, shape the curve there — and
invert the same shaping in `X32ToMcu`, or values will creep on every round trip.
The self-test asserts that inverse holds.
