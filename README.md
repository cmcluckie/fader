# Fader — two products for a live rig

One repo, two independent macOS menu-bar apps. They share a repo, an OSC codec
and a drawn-control toolkit; they share no runtime state and neither needs the
other installed.

- **FaderBridge** (the first half of this document) — maps a PreSonus FaderPort 8
  (USB MIDI, Mackie Control) to a Behringer X32 Rack (OSC over UDP 10023).
  Bidirectional: the surface drives the console, and the console drives the
  motors, LEDs and scribble strips back. Runs on **macOS and Windows** (CoreMIDI
  or WinMM, selected at startup).
- **Feedback Fader** ([Feedback suppression](#feedback-suppression)) — a
  headless JUCE audio process the tray app supervises, notching microphone
  feedback, plus an X32 RTA-assisted ring-out for the room. macOS, and Windows
  with an ASIO interface ([Windows](#windows)).

They began as one binary. The split cost almost nothing because the seam was
already there: the feedback code never referenced MIDI, and the feedback views
never referenced the bridge. The single thing they had in common — which console
to talk to — is now each product's own setting.

The two-process shape of Feedback Fader (why a separate audio process, not
managed DSP) is recorded in
[docs/adr/0001](docs/adr/0001-feedback-engine-architecture.md).

## Prerequisites

- .NET 8 SDK or newer
- FaderPort 8 in **MCU mode** (hold `NEXT` while powering on, choose MCU)
- Host machine and X32 on the same subnet

On macOS, run from **Terminal in the GUI session, not over SSH** — CoreMIDI will
not enumerate devices without a logged-in user session. Windows has no such
restriction.

## Configure

Edit `src/FaderBridge.Core/config.json` — at minimum the console's IP, which
you'll find on the X32 under **Setup → Network**:

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

Everything builds as a unit from `Fader.slnx` — both products, both core
libraries, the shared library, and the diagnostics:

```bash
dotnet build Fader.slnx
```

Five projects under `src/`: `Fader.Shared` (the OSC codec and the drawn-control
toolkit), then a Core library and an App per product. Only the two Apps produce
a bundle.

## Run

```bash
dotnet run --project src/FaderBridge.Core
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

`src/FaderBridge.App` wraps the bridge in a menu-bar app. It references the real
`FaderBridge.Core` project and hosts `BridgeHost` in-process, so it runs exactly
the same code as the console app — the surface, banking, and OSC paths are
unchanged. A menu-bar app is also a proper GUI login session, which is what
CoreMIDI needs, so the "don't run over SSH" caveat stops applying.

```bash
dotnet run --project src/FaderBridge.App   # run from source
scripts/build-fader-bridge.sh              # -> dist/FaderBridge.app
```

The menu shows whether the bridge is running, whether the X32 is replying (a
`/info` probe every 5s), and the MIDI port in use, plus Start/Stop and Quit.
There is no window: the bridge has nothing to show that the console and the
surface do not show better. It also **reconnects**: if the
FaderPort is unplugged it waits and retries every 5s, reattaching when it
returns. The bundle sets `LSUIElement`, so it lives only in the menu bar — no
Dock icon. It reads `config.json` from `Contents/MacOS/` inside the bundle.

The app is **unsigned**: it runs when built locally, but Gatekeeper will block it
if it is zipped, moved, or downloaded without code-signing. Launch-at-login is not
yet done.

## Icons

Each product has its own mark, and the two are one family: a line that describes
signal, with a fader cap on it. FaderBridge is two fader tracks tied by a span —
a surface and a console, linked, each end carrying the other's move. Feedback
Fader is a flat response bitten by one narrow notch, the cap sitting in the
trough it just pulled down. Colour comes from `Tokens`: teal for the bridge,
magenta for the catch, so the icons say the same thing the UI does.

The menu-bar icon is a **template image** — black plus alpha, flagged with
`MacOSProperties.SetIsTemplateIcon` — so macOS inverts it for a dark menu bar and
tints it while the menu is open, rather than the app guessing at the theme. The
Windows notification area has no such notion, and a black-on-transparent icon
would disappear into a dark taskbar, so each app ships a coloured twin
(`tray-color.png`) and picks between them at startup.

`assets/` holds the SVG masters, the two `.icns` bundle icons and the two `.ico`
files the Windows executables embed. Everything there is generated from geometry drawn on a
44-unit grid — a 22 pt menu-bar icon at 2× — by `assets/make-icons.py`
(`pip install cairosvg pillow`); edit the geometry in that script rather than the
rasters, and re-run it to rebuild every size. The `.icns` is written directly, so
the script does not need `iconutil` and runs off a Mac too.

## Feedback suppression

A two-channel acoustic feedback suppressor for the lead and backup vocal mics,
plus a system ring-out that drives the X32's own GEQ. The DSP (a spectral
detector and a notch bank, ported verbatim from the FeedbackKiller plugin) runs
in a **separate headless process**, `fk-engine`; the menu-bar app supervises it
and talks to it over OSC on loopback. See
[docs/adr/0001](docs/adr/0001-feedback-engine-architecture.md) for why, and
[docs/fk-osc-interface.md](docs/fk-osc-interface.md) for the wire contract.

The audio path has **no managed code anywhere near it** — a GC pause in an audio
callback produces the kind of dropout that only ever happens on stage. That
constraint is the whole reason for the split.

### Building the engine

The engine needs CMake and a C++ toolchain; the first build fetches JUCE and
takes a while.

```bash
brew install cmake                                   # if not already present
cd engine
cmake -B build -G "Unix Makefiles"                   # or -G Xcode with full Xcode
cmake --build build --config Release
# -> engine/build/fk-engine_artefacts/Release/fk-engine
```

`scripts/build-feedback-fader.sh` copies that binary into `FeedbackFader.app` so
the tray app finds it; otherwise the app resolves it via `$FK_ENGINE_PATH` or the
dev build path, and the tray reads "Engine not built" if it can't.

```bash
dotnet run --project src/FeedbackFader.App   # run from source
scripts/build-feedback-fader.sh              # -> dist/FeedbackFader.app
```

### Windows

Feedback Fader runs on Windows with any interface that has an ASIO driver — a
Focusrite Scarlett 2i2 is the reference. Build it **on the Windows PC**: the
C# app would cross-compile from a Mac, but the engine is C++ and needs
Microsoft's compiler, so the script builds both in one place.

Prerequisites: Visual Studio 2022 Build Tools with the *Desktop development
with C++* workload, CMake 3.22+, the .NET 8 SDK, and Git. Then:

```powershell
powershell -ExecutionPolicy Bypass -File scripts\build-feedback-fader-win.ps1
# -> dist\FeedbackFader-win-x64\FeedbackFader.exe, fk-engine.exe beside it
# -> dist\FeedbackFader-win-x64.zip
```

The script configures and builds the engine, runs the detector tests (pure DSP,
so a compiler that changed a result fails the build rather than a gig), publishes
the app self-contained — the PC it runs on needs no .NET — and puts
`fk-engine.exe` beside `FeedbackFader.exe`, which is where the app looks first.
Settings, notches and logs live in `Documents\FeedbackKiller`, as on the Mac.

In Setup, pick the interface's **ASIO** entry — *Focusrite USB ASIO* for a
Scarlett — not one of the WASAPI endpoints Windows also lists. ASIO is what
honours the 64-sample buffer; WASAPI in shared mode adds tens of milliseconds to
a vocal that is being monitored live. On a 2i2 the insert is simply: mic into
input 1, the engine's return on output 1, and output 1 to the console channel or
the wedge feed, so there is no Console-style routing change to make.

Three things differ from the Mac build:

- **ASIO licence.** The engine is compiled with JUCE's bundled Steinberg ASIO
  headers, dual-licensed GPLv3 or Steinberg's proprietary licence. Building and
  running it yourself is unaffected; read
  `engine/build-win/_deps/juce-src/modules/juce_audio_devices/native/asio/LICENSE.txt`
  before giving the binary to anyone else.
- **Stopping the engine.** Windows has no SIGTERM, so the app sends `/fk/quit`
  and the engine closes the audio device before exiting; a hard kill is only
  the fallback for an engine that no longer answers.
- **Unsigned.** SmartScreen warns on first launch: *More info → Run anyway*.

### The signal path and the Console routing change

The engine inserts into the Apollo return path, after the UAD chain:

```
X32 XLR 3/4 (mics) → card out → ADAT → Apollo → UAD chain (post-insert)
   → fk-engine (Core Audio, ADAT 3/4 in) → notch → ADAT 3/4 out
   → X32 card in → X32 channels 3/4
```

For this to work you must **disable the Console hardware direct-out** on ADAT 3
and 4 (set their output destination to *none*). If you leave the direct-out
running you will hear the un-notched vocal doubling with the engine's return.
Confirm ADAT 3/4 are still **post-insert** so the engine receives the vocal after
the 610-B and 1176. In the engine's audio settings choose the Apollo and enable
**ADAT 3 and 4 for input and output**, buffer **32 or 64 samples** — anything
larger is wasted latency (analysis runs on a ring buffer beside the signal, so
audio latency is only the buffer).

### Keep a bypass path

The Mac is now in your audio path, so before you trust it live, wire a fallback:
send the same mics to a **spare pair of ADAT channels** with a Console
direct-out, land them on **two muted X32 channels**, and keep those in reach. If
the laptop sleeps or the engine dies, unmute and carry on with the clean vocal —
one unmute from recovery. The app also never fails to silence *quietly*: if the
engine stops, the menu reads **"engine down — audio bypassed"**.

### Modes and the workflow

`OFF` analyses and displays but passes audio untouched. `ASSIST` detects and
logs but never cuts — **this is the default, and where you start.** `AUTO`
deploys notches. Pick the mode from the tray's **Feedback mode** submenu.

1. Run **ASSIST** for a full rehearsal. Every detection is logged to
   `~/Documents/FeedbackKiller/logs/feedback-log-*.csv`
   (`seconds,channel,frequency_hz,level_db,applied`).
2. Read the log against what actually rang. If the frequencies match, the
   detector is tuned for your room; if it flagged you *singing*, raise
   `prominenceDb` or `persistFrames` (via `/fk/param`).
3. Ring-out: **AUTO**, push the mains until things ring, let it find them, then
   **Lock all feedback filters** (tray). Locked filters are persisted to
   `~/Documents/FeedbackKiller/notches.json` and **replayed on every restart** —
   they survive the engine, or the app, dying.
4. For the show, leave it in **AUTO** with the locked filters in place; anything
   new it finds is a live filter that releases on its own.

### System ring-out (the X32 side)

The engine handles the *microphones*; the X32's own RTA + GEQ handle the
*system*. The 31-band GEQs on insert FX slots 5–8 are individually addressable,
and the console's 100-band RTA is readable over OSC, so the app can push the
mains, read the RTA, and cut the offending GEQ band — a second tool for a
different problem. Set the RTA source to the main mix and insert a GEQ on the
main LR for this. The paths are verified against the console; **cutting during a
real ring needs the PA up**, so that step is done at the rig.

Verify the RTA/GEQ path (read-only, no console changes) with:

```bash
dotnet run --project diagnostics/RingOut -- <x32-ip> 5
```

## Diagnostics

Six, all runnable independently.

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

**Feedback engine smoke test** — spawns `fk-engine` via the supervisor and proves
the whole C# ↔ engine path: telemetry flows, a placed notch round-trips, and a
locked notch survives an engine restart. No mics needed (it opens the default
audio device):

```bash
dotnet run --project diagnostics/FkPing
```

**X32 ring-out path check** — read-only: subscribes to the RTA, reports the peak
band mapped to a GEQ band, and reads GEQ bands to confirm they're flat. Changes
nothing on the console:

```bash
dotnet run --project diagnostics/RingOut -- <x32-ip> 5
```

**Spectrum scope** — the engine's spectrum and the console RTA on one shared
log-frequency axis, with engine↔RTA correlations marked (a ring seen on a mic
*and* in the RTA at the same frequency — §6). Terminal form of the display:

```bash
dotnet run --project diagnostics/SpectrumScope -- <x32-ip>
```

## What's verified, and what isn't

**Verified (89/89 self-test assertions, on real UDP):** OSC encoding is
byte-for-byte spec-correct; fader scaling round-trips exactly with no creep;
MCU scribble SysEx layout and offsets; fader-touch gating suppresses motor
updates and resyncs on release; echo suppression stops the console's echo of our
own move re-driving the motor; the inverted mute sense in both directions; bank
windowing, including clamping at both ends and ignoring off-bank channels;
recovery from a silent scene recall, without stomping a held fader; the
master/bus layer routing and the Record-lamp toggle; transport→media commands;
the whole-row scribble marquee and its display-override gate; feedback
locked-notch persistence and the CSV log format; and the RTA blob decode, GEQ
par↔dB maths, nearest-band lookup, and the headamp source mapping.

The suite was mutation-tested — injecting a non-inverted mute and a disabled
touch gate produced exactly the expected failures, so the assertions have teeth.

**Verified against the live X32 Rack (firmware 2.07).** OSC read *and* write
(`/info`, `/ch/NN/mix/fader`, `/main/st/mix/fader`); the CoreMIDI port name
(`PreSonus FP8 Port 1`); the console driving the surface (mute lamps, banking);
the RTA stream (`/batchsubscribe … /meters/15`, 100×int16, `dB = v/256`) — note
the batchsubscribe alias must start with `/` or the reply is rejected; the GEQ on
insert slots 5–7 reading flat (`/fx/N/par/NN`, 0.5 = 0 dB); and the headamp trap
(`/ch/03/config/source` = 3 → `/headamp/002/gain`).

**Verified end-to-end (C# ↔ engine, default audio device).** `fk-engine` builds
with JUCE, passes audio through the detector untouched, and the supervisor round-
trips control and telemetry, persists locked notches, and replays them across a
restart. Proven by `FkPing`.

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
| Surface → console direction on this Mac | Low | move fader 1, watch channel 1 move — not yet eyes-verified here |
| **Feedback: Apollo/ADAT audio path & channel mapping** | High | no signal reaches the engine, or the wrong ADAT pair is used |
| **Feedback: detection tuning for your room** | Medium | ASSIST log flags you *singing*, or misses real rings |
| **Feedback: RTA band-centre frequencies** | Low-med | a ring-out cut lands on a neighbouring 1/3-octave band |
| **Feedback: GEQ ±15 dB endpoints** | Low | a cut lands at a slightly wrong depth |
| **Feedback: cutting GEQ / notching during a real ring** | — | needs the PA up; confirm at soundcheck |

Touch reporting matters more than it looks: fader-touch gating is what stops
incoming OSC fighting your hand. If those notes are wrong, the gating silently
never engages.

The feedback rows are the deliverable-5 list: everything the engine does was
proven on the bench against the default audio device and the live console, but
nothing was run against the Apollo, real mics, or a PA. The `RingOut` and
`FkPing` diagnostics are the tools to check each at the rig.

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
src/Fader.Shared/               what both products use and neither owns
  Osc/OscMessage.cs             hand-rolled OSC 1.0 codec, incl. bundles + blobs
  Ui/Tokens.cs                  colours, type, spacing
  Ui/Controls.cs                ArmSwitch, Led, and the panel-building helpers
  Ui/HoldButton.cs              TapButton + hold-to-confirm
  Ui/LevelMeter.cs              metering with ballistics

src/FaderBridge.Core/           the bridge
  Program.cs                    startup, config load, graceful shutdown
  BridgeConfig.cs               config.json
  Bridge/BridgeHost.cs          both directions, touch gating, banking, layers
  Bridge/FaderScaling.cs        MCU 14-bit <-> X32 float  (tune the taper here)
  Midi/McuProtocol.cs           note map, motor/LED/scribble/marquee construction
  Midi/FaderPortDevice.cs       MIDI in/out
  Osc/X32Client.cs              UDP + /xremote keepalive
  Osc/X32Address.cs             channel/bus/main address construction
  Osc/X32Headamp.cs             the headamp/source trap (§7)
src/FaderBridge.App/            FaderBridge.app (Avalonia tray, no window)
  BridgeController.cs           host the bridge in-process, report status
  NowPlaying.cs                 the scribble-strip marquee
  MediaKeys.cs                  transport -> Apple Music / Spotify

src/FeedbackFader.Core/         Feedback Fader's host side
  X32Rta.cs                     100-band RTA subscribe + decode
  X32Geq.cs                     31-band GEQ addressing + par<->dB
  RingOutSession.cs             RTA peak -> GEQ cut
  X32ChannelMeters.cs           per-channel metering for the signal-path check
  Feedback/FkEngineClient.cs    loopback OSC to fk-engine
  Feedback/EngineSupervisor.cs  spawn / health-check / restart
  Feedback/FeedbackController.cs replay, persistence, logging
  Feedback/FkNotchStore.cs      locked notches (JSON)
  Feedback/FkAudioStore.cs      device, channels, and the console address
  Feedback/FkEventLog.cs        detections, EQ, and captures (CSV)
  Feedback/NotchResponse.cs     the engine's filter maths, for display
  Feedback/Correlator.cs        engine detection <-> RTA peak
src/FeedbackFader.App/          FeedbackFader.app (Avalonia tray + window)
  ShowView.cs                   the screen you leave open during the set
  SetupView.cs                  the screen you open at soundcheck
  GuardSpectrum.cs              what the guard is hearing, right now
  SpectrumWindow.cs             the engine's spectra over the X32 RTA
  Program.cs                    entry point + the headless render harnesses

engine/                         the headless C++ audio engine (JUCE)
  Source/FeedbackDetector.h     spectral detector (ported verbatim)
  Source/NotchBank.h            biquad notch bank (ported verbatim)
  Source/AudioEngine.h          processBlock as a Core Audio callback
  Source/EngineMain.cpp         device + OSC wiring, headless
  tests/test_detector.cpp       the detector against synthetic rooms
  tests/test_loop.cpp           the closed-loop room simulator

diagnostics/
  bridge:   BridgeSelfTest/  MidiMonitor/  OscPing/
  feedback: FeedbackSelfTest/  FkPing/  RingOut/  SpectrumScope/
docs/
  adr/0001-...                  architecture decision record
  fk-osc-interface.md           the engine <-> app OSC contract
```

The C# diagnostics reference each product's real source, so a clean diagnostic
run means that product's own code is what passed. The engine's DSP headers are
byte-for-byte copies of the FeedbackKiller originals (same SHA).

## Tuning

`FaderScaling` is currently a linear position map. X32 fader floats are not
linear in dB (`0.0` = −inf, `0.75` ≈ 0 dB, `1.0` = +10 dB). If the FaderPort's
markings don't line up with the console's readout, shape the curve there — and
invert the same shaping in `X32ToMcu`, or values will creep on every round trip.
The self-test asserts that inverse holds.
