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

## Rule Zero: the rig is locked

The UAD Console session and the X32 scene are production and took two years to
get right. Nothing in this repository changes them, and nothing in it is run
against them without Chris saying go for that run. The app is **read-only
against the X32 by default**: it subscribes to the RTA, asks the meters, and at
start-up sends `/info` to find the desk (the last address that answered, then
the saved one, then a LAN broadcast). That is all. On the Apollo side, a mic
armed with its Return set to **None** is listened to and never written anywhere:
the engine opens no output for it. Everything that can write to
the console, or make a sound in the room, is listed here. Each is off until used and fires only from an explicit
click or flag, never on its own.

| What | Where | What it does to the rig | How it fires |
|---|---|---|---|
| Check signal path | Setup | writes nothing on the X32; plays a short tone through the engine's return, which is heard in the room if the desk is up | one click |
| Bypass to desk / Back to guard | Show | **the only X32 write in the app.** Sets `/ch/NN/mix/on` on the channels named in Setup, and nothing else: to the desk it opens the bypass channels (e.g. Ch 11/12) and then mutes the guarded ones (e.g. Ch 1/2); back is the reverse, guarded open first. Each write is read back from the desk and logged to the app log; the button then shows what the desk reported, not what was sent | hold the button one second; the hint names the exact channels. Hidden until both channel lists are filled in. Never fires on its own: not on engine death, not at start-up, not at quit |
| Ring-out sweep | `scripts/ringout.py` | moves one channel fader (`--fader`, default Ch 9) and the main fader and puts both back at the end; drives the live engine's recorder, bypass and rescue duck for the run | `--go` on the command line, meaning Chris said go for this run; it refuses otherwise (`--dry-run` moves nothing) |
| Cold jump | `scripts/coldjump.py` | the same, several times over | `--go`, passed on to every sweep it starts |

Not connected: the GEQ ring-out (`RingOutSession`, `X32Geq`) can address the
X32's insert GEQs, but nothing in the app calls it. If it is ever wired it goes
in this table first, off by default.

Two tools that moved faders to prove the OSC path (`OscPing`, `AutoRingOut`)
were removed on 2026-10-09 under this rule. Where the design needs a Console or
X32 change, the change is written up as a checklist for Chris to make himself,
and the software stops there.

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

### The signal path (the rig as of October 2026)

Lead mic → Apollo x6 Mic 1 (Unison 610-B → 1176 → LA-2A → Pultec → Helios);
BGV mic → Apollo x6 Mic 2. Today a Console **Flex Route** puts Analog 1/2 on
ADAT out 1/2 → X32 card in 1/2 → X32 Ch 1 (Lead) and Ch 2 (BGV). All eight
Apollo ADAT inputs are taken (drums, guitar and bass into Logic). House music
goes Loopback → Virtual 1/2 → Flex Route Line 1/2 → X32 Aux In 1/2. The Apollo
is clock master at 48 kHz and the X32 slaves over BNC; the Mac and the X32 share
a GL.iNet router.

The engine sits between the UAD chain and the desk:

```
Apollo Mic 1/2 → UAD chain (Console inserts, REC) → Core Audio inputs (Mic 1/2)
   → fk-engine (detector + 48 notches per mic, 64-sample buffer)
   → Core Audio outputs ADAT 1/2 → X32 card in 1/2 → X32 Ch 1 Lead / Ch 2 BGV
```

**As of 9 Oct 2026 the engine is not in that path and is not going into it
yet.** The Flex Route still sends Analog 1 → ADAT out 1 and Analog 2 → ADAT out
2 directly; Ch 1 and Ch 2 take card in 1 and 2 through User In 1/2; Ch 11/12
are unused Local 11/12 inputs, not bypass channels (the bypass was never
built); the Apollo's ADAT inputs 1–8 all carry the X32's recording feeds to
Logic; ADAT outputs 3–8 are free. Chris runs the app on a separate computer
first. The first step is **assist mode**: arm Mic 1/2 with Return set to
**None**, so the detector listens and logs and nothing is written to any
output. On the rig's Apollo, Core Audio input 0 is Mic 1: it was the input that
moved when Chris sang (read from outside the engine, 9 Oct); inputs 24/25 are a
Console loopback pair carrying the mix, not a mic.

For the engine to become the only path later, the Flex Route has to come
**off** ADAT 1/2 (or the clean vocal doubles with the engine's return a few
milliseconds apart, a comb filter heard as thinness), the inserts on Mic 1/2
have to be in **REC** so the engine gets the processed vocal, and a bypass pair
has to be patched (Analog 1/2 → a free ADAT out pair → X32 card in → two spare
channels, muted). Every one of those is a Console or X32 change, so they are
steps in [docs/RIG-CHECKLIST.md](docs/RIG-CHECKLIST.md) for Chris to make, with
a backup first; the software does not touch either. The saved configuration on
the rig Mac is still the previous wiring (inputs 4/5 → returns 10/11, digital
silence onto free ADAT outputs) and is not being changed from here.

**Latency.** The engine's buffer is 64 samples at 48 kHz (1.33 ms); the detector
runs on a ring buffer beside the signal, so the engine adds the buffer and
nothing else. The round trip through the Mac (Apollo A/D → Core Audio in → engine
→ Core Audio out → ADAT → X32 card) has **not been measured** on this rig; the
loop-measurement probe (plan 4.3) will report it, and it plays a click, so it
waits for Chris. Console's Input Delay Compensation is already Medium-Long.

### Keep a bypass path

The Mac is now in your audio path, so before you trust it live, wire a fallback:
send the same mics to a **spare pair of ADAT channels** with a Console
direct-out, land them on **two muted X32 channels**, and keep those in reach. If
the laptop sleeps or the engine dies, unmute and carry on with the clean vocal —
one unmute from recovery. If the engine stops, the menu reads **"engine down —
audio bypassed"** — but the engine *is* the pass-through, so a dead engine is a
silent channel until you unmute the spare: that label is a warning, not a
bypass.

The app can do that unmute for you, from one hold. In Setup, beside the console
address, enter the **desk channels** the guarded mics land on (`1 2`) and the
**bypass channels** carrying the same mics straight from the Console (`11 12`),
in the same order. Show then gains a **Bypass to desk** hold button whose hint
names exactly what it will open and mute; after the swap it reads the mutes back
and reports what the desk says, and the guard pill reads **ON THE DESK** until
**Back to guard** is held. It is the only thing the app ever writes on the
console (see Rule Zero above), it never fires on its own, and it has not yet
been tried against the real desk: the swap is proven on a fake X32 on loopback
by `diagnostics/FeedbackSelfTest`, and the first real use is Chris's, on his go,
after a backup.

### Controls and the workflow

There are no mode switches. (OFF / ASSIST / AUTO were retired in August.) What
there is:

- **Feedback engine** (tray): the audio process on or off.
- **Arm** (Setup, per channel): this input is guarded. Nothing is cut until a
  channel is armed. Setup has three tabs since 9 Oct: **Audio device** (the
  interface, the console, the desk bypass, the path check), **Levels** (every
  input the device has, each with a live meter and an Arm button: sing, and the
  input that moves is your microphone) and **Inputs** (only the armed channels,
  with their Returns, then the guard settings, ring-out, capture and the filters
  in place). A status strip above the tabs reads the same on all three: engine,
  build, ASSIST or GUARD, how many are armed and how many are in the audio path.
- **Return** (Setup, per channel): the output the cut signal goes back out on,
  or **None**: the detector listens and every catch is logged and shown, and
  nothing is written to any output. That is assist mode, and it is what a newly
  armed mic starts as; the Show pill reads **ASSIST** while every armed mic is
  on None. Only a Return chosen on purpose puts the engine in the audio path.
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

It finds the console itself at start-up: the last address that answered (kept
in `~/.config/Fader/x32.json`, or `%APPDATA%\Fader` on Windows), then the
address saved in Setup, then a broadcast of `/info` across the LAN, the way
X32-Edit finds a desk. Whatever answers is saved as the console address, so the
field in Setup only needs typing if the search finds nothing.

A ring-out that cuts the X32's own GEQ was written in August
(`RingOutSession`, `X32Geq`) and its console paths were verified, but **it is
not connected to the app**: nothing in the app writes the GEQ. The read-only
check still runs:

```bash
dotnet run --project diagnostics/RingOut -- <x32-ip> 5
```


## Diagnostics

Four, all runnable independently. None of them writes to the console.

**X32 locate test** - exercises the start-up search for the console: the
remembered address, then a given one, then an `/info` broadcast; reports what
answered. Read-only (`/info` is a query):

```bash
dotnet run --project diagnostics/X32LocateTest
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

**Verified against the live X32 Rack (firmware 2.07), in September, before
Rule Zero.** OSC read and write (`/info`, `/ch/NN/mix/fader`,
`/main/st/mix/fader`; the tool that wrote them is gone); the RTA stream
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
numbers rather than impressions. A sweep tool (since removed; `scripts/ringout.py`
is its successor and refuses to move a fader without `--go`) drove the console's
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

**Cannot be verified without the hardware, or without Chris (Rule Zero).**

- The Console changes (Flex Route off ADAT 1/2, Analog 1/2 onto ADAT 3/4,
  inserts in REC) and the X32 changes (Ch 11/12 from card 3/4, their sends,
  the Bus 12 key): Chris makes them from the checklist; the software never will.
- That the engine receives Apollo Mic 1/2 after the UAD chain and lands on X32
  Ch 1/2 with the new wiring. The saved configuration on the rig is still the
  old one, and the engine is not going into the path until Chris has run
  assist mode on a separate computer.
- Return = None on a real interface: the engine opens the device with inputs
  only. Built 9 Oct, unit and self-tests pass, not yet run against an Apollo.
- The desk bypass against the real X32. It is proven on a fake desk on loopback;
  the address and the 0/1 meaning come from the X32 protocol, not from a test
  here.
- The electrical round trip through the Mac (above).
- The RTA overlay and the signal-path check against the new channels: no console
  address has ever been set on the rig Mac.
- The app on the rig stopped on 2026-10-07 at 20:54 with no crash report, and
  its engine ran on alone until 2026-10-09 14:57, when the app was relaunched
  on Chris's go with build ec99884. The new supervisor ended the orphan and
  started its own engine; the console search found the desk on the first try.
  The dead-man's switch and the 30 s start-up grace are running on the rig now
  but have not been put through `scripts/watchdog_check.sh` there.
- Not run on the Mac at all: the `.dmg` and Windows installer scripts.

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

diagnostics/                    FeedbackSelfTest/  FkPing/  RingOut/  SpectrumScope/
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
