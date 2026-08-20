# FaderPort 8 ↔ X32 Rack OSC Bridge

Bridge between a PreSonus FaderPort 8 (USB MIDI, Mackie Control) and a Behringer
X32 Rack (OSC over UDP 10023). Bidirectional: the surface drives the console, and
the console drives the motors, LEDs and scribble strips back.

Runs on **macOS and Windows**. The MIDI backend is selected at startup —
CoreMIDI on macOS, WinMM on Windows — so the same build works on both.

## Prerequisites

- .NET 8 SDK or newer
- FaderPort 8 in **MCU mode** (hold `NEXT` while powering on, choose MCU)
- Host machine and X32 on the same subnet

On macOS, run from **Terminal in the GUI session, not over SSH** — CoreMIDI will
not enumerate devices without a logged-in user session. Windows has no such
restriction.

## Configure

Edit `src/FaderBridge/config.json` — at minimum the console's IP, which you'll
find on the X32 under **Setup → Network**:

```json
{
  "x32IpAddress": "192.168.1.100",
  "midiPortName": "PreSonus FP8",
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

`midiPortName` is matched case-insensitively. An **exact** name wins; failing
that, a substring match must be unambiguous. If nothing matches, or a substring
matches several ports, the bridge lists what it found and exits.

Exact-match-first is not a nicety. Windows enumerates the FaderPort 8 as both
`PreSonus FP8` and `MIDIIN2 (PreSonus FP8)` — the first name is a substring of
the second, so substring matching alone rejects the most natural name as
ambiguous. Port names differ between platforms; run `MidiMonitor` with no
arguments to list what your machine actually reports, and copy a name verbatim.

## Moving to the Mac

The repo ships as a git bundle — one file, full history, no server involved:

```bash
git clone Fader.bundle Fader && cd Fader && git remote remove origin
```

Then, **before anything else**, list the MIDI ports. CoreMIDI names devices
differently from WinMM, so the `midiPortName` in `config.json` will very likely
need changing:

```bash
dotnet run --project diagnostics/MidiMonitor
```

Copy a name from that list verbatim into `config.json`. On Windows the device
reports as `PreSonus FP8`; on macOS expect something different, possibly with
separate port-1/port-2 entries.

Two macOS-only gotchas:

- **Do not run over SSH.** CoreMIDI will not enumerate devices without a
  logged-in GUI session. Use Terminal on the machine itself.
- The launch profiles in `Properties/launchSettings.json` hard-code the Windows
  port name. Use the no-argument run to list ports first.

Everything else is identical — the backend is selected at startup and the rest of
the code is platform-agnostic.

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
| Master layer | **Record (●)** flips the 8 faders to Main LR (strip 1) + mix buses 1–7 (lamp lit); press again for channels |
| Transport | ◀◀/▶▶ = previous/next track, ▶ = play/pause, ■ = pause on the Mac's music player (Apple Music / Spotify) |
| Mute | `/ch/NN/mix/on` (inverted: 0 = muted), lamp follows console |
| Solo | `/-stat/solosw/NN` |
| Select | `/-stat/selidx` |
| Bank Left/Right | shift the 8-fader window by 8 |
| Channel Left/Right | shift the window by 1 |
| Scribble strips | channel name (upper), channel number (lower) |

## Menu-bar app (macOS)

`src/FaderBridge.MenuBar` wraps the bridge in a menu-bar app. It references the
real `FaderBridge` project and hosts `BridgeHost` in-process, so it runs exactly
the same code as the console app — the surface, banking, and OSC paths are
unchanged. A menu-bar app is also a proper GUI login session, which is what
CoreMIDI needs, so the "don't run over SSH" caveat stops applying.

```bash
dotnet run --project src/FaderBridge.MenuBar   # run from source
scripts/build-app.sh                           # -> dist/FaderBridge.app
```

The menu shows whether the bridge is running, whether the X32 is replying (a
`/info` probe every 3s), and the MIDI port in use, plus Start/Stop and Quit. The
bundle sets `LSUIElement`, so it lives only in the menu bar — no Dock icon. It
reads `config.json` from `Contents/MacOS/` inside the bundle.

The app is **unsigned**: it runs when built locally, but Gatekeeper will block it
if it is zipped, moved, or downloaded without code-signing. Launch-at-login and a
theme-aware (light/dark) menu-bar icon are not yet done.

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
dotnet run --project diagnostics/MidiMonitor                      # list ports and pick one
dotnet run --project diagnostics/MidiMonitor -- "PreSonus FP8"    # open by exact name
```

Check that faders reach `0` and `16383` at the extremes, touch prints
`TOUCH DOWN`/`UP` for the right strip, and nothing prints `(unmapped)`.

Add `--test-output` to drive the surface *outward* first — it writes the scribble
strips, chases the Select LEDs and sweeps fader 1, restoring everything after:

```bash
dotnet run --project diagnostics/MidiMonitor -- "PreSonus FP8" --test-output
```

This is the check that the surface is really in MCU mode and accepts display
SysEx; passive monitoring cannot tell you either. Only the FaderPort is written
to — there is no audio path.

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

**Verified against a real FaderPort 8 (Windows / WinMM).** The entire outbound
path works — everything the bridge sends *to* the surface:

- port enumeration and exact-name matching; the device reports as `PreSonus FP8`
  (inputs also list `MIDIIN2 (PreSonus FP8)`, outputs `MIDIOUT2 (PreSonus FP8)`)
- **motorised faders** move to commanded positions — so pitch-bend encoding is
  right, and **no MCU host handshake is required** before the surface responds
- **scribble strips** render text — standard MCU display SysEx at device ID
  `0x14` is accepted, offsets and 7-character cells are correct
- **button LEDs** light via note-on echo

**Not verified — still needs hardware.** The self-test ran against a *mock*
console and a *fake* surface, which proves the logic is right given the protocol
assumptions but cannot prove the assumptions match the devices. Outstanding:

| Assumption | Risk | How you'll know |
|---|---|---|
| MCU note numbers *inbound* (mute 16–23, touch 104–111, …) | Medium | `MidiMonitor` — mislabelled or `(unmapped)` output |
| Fader touch reports on notes 104–111 | Medium | `MidiMonitor` — no `TOUCH DOWN` when gripping a fader |
| `/-stat/solosw/NN` and `/-stat/selidx` on Rack firmware | Low-medium | solo/select do nothing; `OscPing` can probe them |
| `/ch/NN/...` fader, mix/on, config/name | Low | `OscPing` already exercises fader |

Touch reporting matters more than it looks: fader-touch gating is what stops
incoming OSC fighting your hand. If those notes are wrong, the gating silently
never engages.

## Open decisions

- **Transport buttons are deliberately unmapped.** An X32 Rack has no transport
  of its own — the only candidate is the USB recorder, whose OSC paths vary
  between firmware versions. Rather than guess, the bridge logs the press. Tell
  me what you want Play/Stop/Record to do and it's a few lines each.
- **Scribble-strip colour** is not implemented (it was flagged as a stretch
  goal). The X32 exposes `/ch/NN/config/color`; the FaderPort's colour protocol
  isn't documented by PreSonus.
## Master / bus layer

The console's master and mix-bus levels live on a second **layer** of the eight
motor faders, instead of the FaderPort's master encoder. `MidiMonitor` showed
that encoder is not an absolute fader — it rests at `0` and only rises while
turning, with no direction — so routing it to `/main` drove the master to zero
at rest. Real motor faders are a far better fit.

The FaderPort's own Master button sends no MIDI (nor does the Session Navigator
encoder's push), so the layer is toggled by the **Record (●)** button instead —
a real button gives an instant, lamp-lit switch. Press it to put Main LR on
strip 1 and mix buses 1–7 on strips 2–8 (Record lamp lit); press again for
channels. Mute/Solo/Select stay channel-only. Buses 8–16 are not surfaced yet.

The transport buttons, which an X32 has no use for, drive the Mac's music player
instead (Apple Music, or Spotify if it is running): ◀◀/▶▶ previous/next track,
▶ play/pause, ■ pause. This uses AppleScript, so macOS asks for Automation
permission the first time.

## Library choice

`managed-midi` (`Commons.Music.Midi`). On macOS it binds
`CoreMidiApi.CoreMidiAccess` **explicitly**; on Windows it uses
`MidiAccessManager.Default`, which resolves to `WinMMMidiAccess`.

RtMidi.Core was rejected: version 1.0.53 bundles a single `librtmidi.dylib` that
is x86_64-only (Mach-O cputype `0x01000007`, no arm64 slice), so on Apple
Silicon it fails to load unless the whole app is forced under Rosetta.
managed-midi ships **no** native binaries — it P/Invokes the platform's own MIDI
framework, so it runs native on Apple Silicon, Intel, and Windows alike.

`MidiAccessManager.Default` is avoided *on macOS* because it can resolve to the
RtMidi backend there and reintroduce that dependency. `MidiBackend` falls back to
RtMidi only if CoreMIDI fails outright, in which case: `brew install rtmidi`.

Scribble strips need SysEx output. The WinMM backend exposes `midiOutLongMsg`
and `midiOutPrepareHeader`, so this works on Windows.

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
