# Feedback Fader

A macOS menu-bar app that keeps microphone feedback out of a live mix, with a
headless audio engine doing the listening and the cutting. The engine is C++
(JUCE); the app is C# (Avalonia); they talk over OSC on loopback. Why the audio
lives in a separate process is recorded in
[docs/adr/0001](docs/adr/0001-feedback-engine-architecture.md).

Where the project stands: [PROJECT_PLAN.md](PROJECT_PLAN.md) (epics, features,
goals), [docs/TODO-AS-BUILT.md](docs/TODO-AS-BUILT.md) (what was built, what is
left, pass thresholds), [docs/TESTING.md](docs/TESTING.md) (the standard tests)
and [docs/RESULTS.md](docs/RESULTS.md) (every build's numbers).

Until October 2026 this repository also held **FaderBridge**, a FaderPort 8 to
X32 Rack bridge. The FaderPort talks to Logic natively now; the bridge was
removed on 2026-10-09 and lives on in the git history.

## Prerequisites

- .NET 8 SDK or newer
- CMake and a C++ toolchain, for the engine
- The host machine and the X32 on the same subnet, for the console's meters and RTA

## Build

```bash
dotnet build Fader.slnx
```

Three projects under `src/`: `Fader.Shared` (the OSC codec and the drawn-control
toolkit), `FeedbackFader.Core` (the host side: engine supervision, persistence,
logging, the X32 readers) and `FeedbackFader.App` (the menu-bar app and its
window). Only the App produces a bundle.

## Icons

The mark is a flat response bitten by one narrow notch, the cap sitting in the
trough it just pulled down. Colour comes from `Tokens`: magenta for the catch,
so the icon says the same thing the UI does.

The menu-bar icon is a **template image** - black plus alpha, flagged with
`MacOSProperties.SetIsTemplateIcon` - so macOS inverts it for a dark menu bar and
tints it while the menu is open, rather than the app guessing at the theme. The
Windows notification area has no such notion, and a black-on-transparent icon
would disappear into a dark taskbar, so the app ships a coloured twin
(`tray-color.png`) and picks between them at startup.

`assets/` holds the SVG masters, the `.icns` bundle icon and the `.ico` the
Windows executable embeds. Everything there is generated from geometry drawn on a
44-unit grid - a 22 pt menu-bar icon at 2x - by `assets/make-icons.py`
(`pip install cairosvg pillow`); edit the geometry in that script rather than the
rasters, and re-run it to rebuild every size. The `.icns` is written directly, so
the script does not need `iconutil` and runs off a Mac too.

## Feedback suppression

An acoustic feedback suppressor for up to eight microphones. The DSP (a
spectral detector, a bank of 48 notch filters per channel and a rescue duck)
runs in a **separate headless process**, `fk-engine`; the menu-bar app
supervises it and talks to it over OSC on loopback. See
[docs/adr/0001](docs/adr/0001-feedback-engine-architecture.md) for why, and
[docs/fk-osc-interface.md](docs/fk-osc-interface.md) for the wire contract.

Where the project stands and where it is going:
[PROJECT_PLAN.md](PROJECT_PLAN.md) (epics, features, goals),
[docs/TODO-AS-BUILT.md](docs/TODO-AS-BUILT.md) (what was built, what is left,
pass thresholds), [docs/TESTING.md](docs/TESTING.md) (the standard tests) and
[docs/RESULTS.md](docs/RESULTS.md) (every build's numbers).

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
**ADAT 3 and 4 for input and output**. The buffer is fixed at **64 samples**
(analysis runs on a ring buffer beside the signal, so audio latency is only the
buffer).

### Keep a bypass path

The Mac is now in your audio path, so before you trust it live, wire a fallback:
send the same mics to a **spare pair of ADAT channels** with a Console
direct-out, land them on **two muted X32 channels**, and keep those in reach. If
the laptop sleeps or the engine dies, unmute and carry on with the clean vocal —
one unmute from recovery. If the engine stops, the menu reads **"engine down —
audio bypassed"** — but the engine *is* the pass-through, so a dead engine is a
silent channel until you unmute the spare: that label is a warning, not a
bypass.

### Controls and the workflow

There are no modes. (OFF / ASSIST / AUTO were retired in August.) What there is:

- **Feedback engine** (tray): the audio process on or off.
- **Arm** (Setup, per channel): this input is guarded. Nothing is cut until a
  channel is armed.
- **Guard on / off** (Show): off passes audio untouched and lets the filters
  fade out.
- **Capture** (Show or Setup): logs every catch and records the microphone
  before and after the filters. With the guard off it still detects and logs,
  and places nothing — the way to watch without acting.
- **Panic** (Show, hold one second): clears every filter, locked ones included.

Every detection is logged to
`~/Documents/FeedbackKiller/logs/feedback-log-*.csv`
(`seconds,channel,frequency_hz,level_db,applied,gate,filter,cut_here_db,nearest_notch_hz,age_ms`),
every declined candidate to `reject-log-*.csv`, and what the filters were doing
each second to `eq-log-*.csv`.

1. Arm the microphones, guard off, Capture on, and run a rehearsal. Read the
   log against what actually rang.
2. Ring-out (Setup): **Start ring-out**, push the monitors until things ring,
   let it find them, then **Lock found filters**. Locked filters are saved to
   `~/Documents/FeedbackKiller/notches.json` and **replaced on every restart** —
   they survive the engine, or the app, dying.
3. For the show, guard on with the locked filters in place; anything new it
   finds is a live filter that releases on its own.

Know before you rely on it: measured on 2026-10-04, the current build with the
"fast" attack setting places filters on a singing voice even when nothing is
ringing ([docs/RESULTS.md](docs/RESULTS.md)). That is the top item in the plan.

### The X32 side

The app reads the console in two places: the **X32 RTA overlay** (tray) draws
the console's own analyser over the engine's spectrum and marks rings both can
see, and **Check signal path** (Setup) plays a brief tone and asks the
console's meters whether it arrived.

A ring-out that cuts the X32's own GEQ was written in August
(`RingOutSession`, `X32Geq`) and its console paths were verified, but **it is
not connected to the app**: nothing in the app writes the GEQ. The read-only
check still runs:

```bash
dotnet run --project diagnostics/RingOut -- <x32-ip> 5
```


## Diagnostics

Four, all runnable independently.

**X32 OSC path check** - four steps: `/info` round-trip, read fader, set fader
(channel 1 should physically move), read back:

```bash
dotnet run --project diagnostics/OscPing -- <x32-ip>
```

**Feedback engine smoke test** - spawns `fk-engine` via the supervisor and proves
the whole C# <-> engine path: telemetry flows, a placed notch round-trips, and a
locked notch survives an engine restart. No mics needed (it opens the default
audio device):

```bash
dotnet run --project diagnostics/FkPing
```

**X32 ring-out path check** - read-only: subscribes to the RTA, reports the peak
band mapped to a GEQ band, and reads GEQ bands to confirm they're flat. Changes
nothing on the console:

```bash
dotnet run --project diagnostics/RingOut -- <x32-ip> 5
```

**Spectrum scope** - the engine's spectrum and the console RTA on one shared
log-frequency axis, with engine<->RTA correlations marked (a ring seen on a mic
*and* in the RTA at the same frequency). Terminal form of the display:

```bash
dotnet run --project diagnostics/SpectrumScope -- <x32-ip>
```

## What's verified, and what isn't

**Verified by the self-test (`diagnostics/FeedbackSelfTest`, on real UDP):** OSC
encoding is byte-for-byte spec-correct; locked-notch persistence and the CSV log
format; the RTA blob decode, GEQ par<->dB maths, nearest-band lookup, and the
headamp source mapping.

**Verified against the live X32 Rack (firmware 2.07).** OSC read and write
(`/info`, `/ch/NN/mix/fader`, `/main/st/mix/fader`); the RTA stream
(`/batchsubscribe ... /meters/15`, 100xint16, `dB = v/256`) - note the
batchsubscribe alias must start with `/` or the reply is rejected; the GEQ on
insert slots 5-7 reading flat (`/fx/N/par/NN`, 0.5 = 0 dB); and the headamp trap
(`/ch/03/config/source` = 3 -> `/headamp/002/gain`).

**Verified end-to-end (C# ↔ engine, default audio device).** `fk-engine` builds
with JUCE, passes audio through the detector untouched, and the supervisor round-
trips control and telemetry, persists locked notches, and replays them across a
restart. Proven by `FkPing`.

**Measured in a real room (2026-09-22, a studio: vocal mic, monitors in the room,
X32 main fader as the gain control).** The first rig session that produced
numbers rather than impressions. `diagnostics/AutoRingOut` drove the console's
main fader in 1 dB steps and watched the console's own RTA, alternating bypassed
and guarded runs.

- **The detector is not firing on the voice.** Singing with the monitors live
  produced 1,022 catches/min; singing with the loop opened (monitors muted,
  everything else identical) produced **5 catches in 80 s — 4/min, median
  −67 dB**. 99.6% of catches need the loop to exist, so they are feedback, not
  false alarms on the voice. That suspicion had stood for weeks; it is dead.
- **ASG is real, and it depends on what you call a ring.** By ear: bypassed
  sustained at −9 dB, guarded at −4 dB → **≈5 dB**. By the RTA criterion (a peak
  ≥25 dB above its neighbours held 1.2 s): bypassed rang at −21/−21/−20 dB,
  guarded at −14/−14/−14 → **7 dB**, three runs each, ±1 dB.
- **−18 dB beats −24 dB on the rig**, confirming Rule 2 off the simulator:
  identical loudest escape (−18.0 vs −17.2 dB) and identical detection latency
  (37 ms median), for 1.8 dB less pull across 1–4 kHz.
- **Rings are 70–141 Hz wide wherever they sit** (20,190 catches). One fixed Q is
  therefore wrong at both ends — at Q12 a filter is 13 Hz wide at 150 Hz and
  370 Hz wide at 4 kHz. This is what `qForWidth` now fixes.
- **A high ring walks.** One 10.6 kHz ring was caught at 9250, 9600, 9950, 10150,
  10300, 10450, 10550, 10750 and 10850 Hz within seconds, so up there a filter
  must cover where the tone is going, not only where it is. A 296 Hz filter
  (Q36) let it out in one guarded run of three; 533 Hz (Q20) is the setting the
  room ladder then agreed with.

**Not verified, from that same session.** The RTA ring criterion **missed
feedback the operator could hear**: with a like-for-like level gate the guarded
runs never rang up to −2 dB, implying ASG ≥ 19 dB, while the room was audibly
ringing. Treat that bound as wrong until the console's RTA source is confirmed
to be metering the signal in the loop. The studio's dimensions were never
measured, so the simulator has not been run against it; RT60 per octave is still
unmeasured anywhere; and the music false-alarm test (Rule 5) was not run.

Feedback Fader has since been run on the Apollo, real microphones and a PA;
what is proven and what is not, with the threshold each feature has to pass, is
kept in [docs/TODO-AS-BUILT.md](docs/TODO-AS-BUILT.md), and every build's
numbers in [docs/RESULTS.md](docs/RESULTS.md). The findings above are the
record of August and September and are not maintained.

## Layout

```
src/Fader.Shared/               the OSC codec and the drawn-control toolkit
  Osc/OscMessage.cs             hand-rolled OSC 1.0 codec, incl. bundles + blobs
  Ui/Tokens.cs                  colours, type, spacing
  Ui/Controls.cs                ArmSwitch, Led, and the panel-building helpers
  Ui/HoldButton.cs              TapButton + hold-to-confirm
  Ui/LevelMeter.cs              metering with ballistics

src/FeedbackFader.Core/         the host side
  X32Rta.cs                     100-band RTA subscribe + decode
  X32Geq.cs                     31-band GEQ addressing + par<->dB
  RingOutSession.cs             RTA peak -> GEQ cut (not connected to the app)
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
  Source/FeedbackDetector.h     spectral detector
  Source/NotchBank.h            biquad notch bank
  Source/RescueDuck.h           the broadband rescue
  Source/AudioEngine.h          processBlock as a Core Audio callback
  Source/EngineMain.cpp         device + OSC wiring, headless
  tests/                        unit tests, fuzz, the C ABI for the simulator
  tests/sim/                    the closed-loop room simulator and the gate

diagnostics/                    FeedbackSelfTest/  FkPing/  OscPing/  RingOut/  SpectrumScope/
scripts/                        the ship gate, the bundle build, the live tools
results/ledger.jsonl            every test result, by build
docs/
  adr/0001-...                  architecture decision record
  fk-osc-interface.md           the engine <-> app OSC contract
  TESTING.md, RESULTS.md        the standard tests and every build's numbers
  TODO-AS-BUILT.md              what was built, what is left, pass thresholds
```

The C# diagnostics reference the real source, so a clean diagnostic run means
the shipped code is what passed.
