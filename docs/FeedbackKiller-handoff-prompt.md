# Task: merge FeedbackKiller into the X32 tray controller

You are extending an existing **C# macOS tray application** that controls a Behringer X32 Rack
over OSC. Attached is `FeedbackKiller.zip`, a JUCE/C++ project implementing a two-channel
acoustic feedback suppressor.

The goal is a single platform that does **mixer control and feedback suppression**, structured so
further audio tools can be added to it later. Read the architectural constraint below before
planning anything. It determines the whole shape of the solution.

---

## 0. RULE ZERO: do not touch the live rig settings

The UAD Console session and the X32 scene are **locked in**. Getting them right took two years.

- **You must not change ANY UAD Console setting or ANY X32 setting.** That covers routing, Flex
  Routes, preamps, plug-ins, inserts, faders, sends, EQ, dynamics, FX, buses, user in/out patches,
  card routing, headamps, scenes and presets, by any means: computer use, OSC, file edits, scripts,
  or "just testing".
- This applies while you develop **and** to the software you ship. The app must never write to the
  X32 or to Console automatically.
- Default the app to **read-only** against the X32: meters, RTA, and reading state are fine.
  Every feature that would write to the X32 (GEQ cuts during ring-out, the bypass mute swap, etc.)
  must be:
  1. off by default,
  2. listed in the README with exactly what it changes,
  3. triggered only by an explicit click from Chris, never automatically.
- Where this design needs a Console or X32 change (the Flex Route move and the bypass channels in
  §2), **do not make it.** Write it up as a step-by-step manual checklist in the README for Chris to
  do himself, and stop.
- If you think a setting is wrong, say so in your report. Don't fix it.

---

## 0b. Remove the FaderPort code from the Fader project

The Fader repo contains two menu-bar apps: **FaderBridge** (controls the PreSonus FaderPort 8) and
**Feedback Fader**. The FaderPort now talks to Logic natively, and FaderBridge has already been
uninstalled from the Mac.

- Remove **all** FaderBridge / FaderPort-control code from the repo: the app target, its source
  files, MIDI/HUI/MCP handling for the FaderPort, its icons and assets, its build and CMake/Xcode
  config, launch agents, and any references in README/docs.
- Keep Feedback Fader and anything it actually depends on. If shared code is used only by
  FaderBridge, remove it. If both apps use it, keep it.
- Make sure the repo still builds clean afterwards.
- Do this as its own commit, separate from the FeedbackKiller work, and list what you removed.
- Do not touch the FaderPort's own settings or Logic's control surface setup.

---

## 1. Hard architectural constraint (read this first)

**Do not port the DSP to C#.** Do not rewrite `FeedbackDetector.h` or `NotchBank.h` in managed
code, and do not put a .NET audio callback in the signal path. A garbage collector pause inside a
real-time audio callback produces dropouts that are intermittent and unreproducible, and they will
happen on stage rather than at a desk. This is not a performance preference. It is a correctness
requirement.

Two architectures are acceptable. Pick one and justify the choice.

### Architecture A: separate engine process (recommended, start here)

The JUCE code becomes a **headless audio engine**: same DSP, GUI stripped out, no window. The C#
tray app supervises it and talks to it over local UDP/OSC on loopback.

- C# owns the tray UI, all X32 OSC, configuration, persistence, logging, the ring-out workflow,
  starting and stopping the engine, and showing engine state to the user.
- The engine owns Core Audio I/O, FFT analysis, detection, and the notch filters.
- They exchange small control and telemetry messages, nothing audio-rate.

This is the lowest-risk option. The audio path has no managed code anywhere near it, the engine
can be developed and debugged on its own, and if the C# app crashes the engine keeps passing
audio.

### Architecture B: native library with P/Invoke

Compile the detector and notch bank as a small dylib exposing a flat C ABI, and drive the audio
callback from native code (miniaudio or PortAudio). C# calls in only to configure and to poll
events. It must **never** call in from inside the callback, and never in a way that a callback can
block on.

This is acceptable, with tighter coupling in one process. Only choose it if you can guarantee no
managed transition occurs on the audio thread.

### Not acceptable

- The DSP rewritten in C#.
- A managed delegate used as a Core Audio render callback.
- Any allocation, lock, file I/O, or logging on the audio thread, in any language.

---

## 2. The rig this runs in (updated 2026-10-09; supersedes earlier versions)

Several design decisions depend on the signal path, so read this carefully.

```
Lead mic  ──> Apollo x6 Mic 1 ─ Unison UA 610-B → 1176 Rev A → LA-2A → Pultec Legacy → Helios Legacy
BGV mic   ──> Apollo x6 Mic 2 ─ Unison UA 610-B → 1176LN → LA-2A → Pultec Legacy
              Console Flex Route: Analog 1/2 → Apollo ADAT out 1/2 → X32 card in 1/2 → Ch 1 Lead / Ch 2 BGV

X32 Local 3–10 (guitar, bass, kick, snare, OH L/R, rack tom, floor tom)
X32 Outputs 9–16 (direct outs, IN/LC tap) → card block 1 → ADAT → Apollo ADAT in 1–8 → Logic (recording)
  ADAT in 1/2 = toms, 3/4 = gtr/bass, 5/6 = kick/snare, 7/8 = OH L/R   ← ALL 8 ADAT INPUTS ARE IN USE

House music: Loopback → Apollo Virtual 1/2 → Flex Route Line 1/2 → X32 Aux In 1/2
MacBook Pro M1 ──Thunderbolt──> Apollo x6 (clock master, 48 kHz); X32 slaves over BNC word clock
Mac and X32 share a LAN via a GL.iNet GL-AXT1800 router
```

The Apollo x6 has **one** ADAT port pair, which carries 8 channels each way at 48 kHz, not 16.

**Where the engine goes.** It sits between the vocal chain and the X32:

1. It takes Apollo Mic 1/2 (after the UAD chain) in over Core Audio.
2. It returns the notched signal over Core Audio to Apollo ADAT out 1/2.
3. That signal reaches X32 Ch 1/2.

Before this works, the Console Flex Route on Analog 1/2 must be moved **off** ADAT 1/2. Otherwise
the dry signal doubles with the engine's return. **Per Rule Zero, Chris does this himself from your
README checklist. You never do it.**

**Bypass path.** It costs nothing, because Apollo ADAT **out** 3–8 are unused:

1. Flex Route Analog 1/2 to ADAT out 3/4.
2. That lands on X32 card in 3/4.
3. Patch those through User In to spare X32 channels, e.g. Ch 11/12, and leave them muted.

If the laptop or the engine dies, unmute 11/12 and mute 1/2. That is one step back to a working
vocal. **This is also a manual checklist item for Chris. Don't set it up yourself.** Ch 11/12 should copy Ch 1/2's IEM sends and FX sends, and the Bus 12 duck key should be
switchable to Ch 11.

**Latency matters.** The lead singer hears this path in his in-ears. Keep the engine's Core Audio
buffer at 32–64 samples and report the measured round trip. Console Input Delay Compensation is
already set to Medium-Long.

Relevant X32 facts:
- XLR outs 1–6 carry Mix Bus 1–6 for in-ears: 1/2 Leader (stereo, also on Aux Out 1/2), 3 Guitar, 4 Bass, 5 Drums, 6 Extra.
- Main L/R is on XLR outs 7/8.
- Bus 12 is the guitar ducking bus, with its compressor keyed from Ch 1.
- Bus 13 is reverb (FX1) and bus 15 is slap delay (FX3).
- FX slots 5–8 are insert-only and currently hold unused 31-band GEQs.

---

## 3. What is in the ZIP

| File | Value | What to do with it |
|---|---|---|
| `Source/FeedbackDetector.h` | **High: this is the actual IP** | Port as-is into the engine. Do not reimplement. |
| `Source/NotchBank.h` | **High** | Port as-is. Real-time safe biquad bank, no allocation. |
| `Source/PluginProcessor.*` | Medium | Reference for wiring and lifecycle. Strip the plugin wrapper if going headless. |
| `Source/PluginEditor.*` | Low | Reference only. The GUI moves to C#. |
| `CMakeLists.txt` | Medium | Adapt for a headless target. |
| `README.md` | High | Explains the algorithm and the routing requirements. Read it. |

**Note: this code has never been compiled.** Expect build errors on the first attempt. Fix them,
and don't take them as evidence the design is wrong.

### The detection algorithm, in brief

A spectral peak must pass all four tests before it is treated as feedback. Keep all four. Each one
exists to reject a specific false positive, and dropping any of them produces a suppressor that
carves holes in sustained vocals.

1. **Prominence:** at least 12 dB above the local spectral median. Rejects broad musical formants.
2. **Persistence:** present in 5 consecutive analysis frames (about 50 ms). Rejects transients.
3. **Pitch lock:** frequency drifts less than 0.6% across those frames. Rejects sung notes, which
   have vibrato of 1–3%. This test does most of the work.
4. **Harmonic rejection:** little energy at 2f or 3f, and the peak is not itself sitting at 2× or
   3× a louder partial. Feedback is close to a pure sine wave, while instruments arrive with a
   harmonic series.

The FFT is 2048 points with a 512 hop, with parabolic interpolation for accuracy between bins.
Detection takes roughly 50–90 ms, but **audio latency is only the buffer size**, because analysis
runs on a ring buffer beside the signal rather than in it.

---

## 4. Responsibility split

**C# tray app**
- Menu bar presence, status, and the main window
- All X32 OSC: channel state, faders, mutes, scenes, GEQ bands, RTA meters
- Configuration and persistence, including locked notch filters
- The ring-out workflow (see §6)
- Log files and history
- Engine supervision: launch, health check, restart, and telling the user when it fails
- One-click bypass: swap X32 Ch 1/2 for the bypass channels Ch 11/12 over OSC. This is the only
  X32 write allowed by default, and only when Chris clicks it (see Rule Zero).

**Audio engine**
- Core Audio device and channel selection
- Per-channel FFT analysis and detection
- The notch bank and its smoothing and release behaviour
- Publishing spectrum frames and detection events upstream

---

## 5. Define the interface

Specify the message set explicitly before writing either side. Something along these lines, as OSC
on loopback:

```
# C# -> engine
/fk/mode          i          0=off 1=assist 2=auto
/fk/notch/place   i f f      channel, hz, depthDb
/fk/notch/remove  i i        channel, slot
/fk/notch/lock    i i i      channel, slot, locked
/fk/clear         i i        channel, includeLocked
/fk/param         s f        maxCutDb | notchQ | releaseSeconds | prominenceDb | ...
/fk/ping

# engine -> C#
/fk/event         i f f      channel, hz, levelDb            (a detection fired)
/fk/notches       i b        channel, packed slot state       (~10 Hz)
/fk/spectrum      i b        channel, packed magnitude frame  (~20 Hz, for display)
/fk/status        i f        engineOk, cpuLoad
```

Spectrum frames are the only high-rate traffic. Pack them as a binary blob, and downsample to
display resolution in the engine rather than shipping all 1024 bins.

---

## 6. Where the two halves earn their keep together

This is the reason for merging them, so build at least the first of these.

**RTA-assisted ring-out.** The X32 exposes its own RTA over OSC, and its GEQ bands can each be set
as FX parameters. So the app can run a ring-out on the *system* (push the mains, read the X32's
RTA, cut the offending GEQ band) while the engine handles the *microphones*. That is two different
tools for two different problems, in one workflow.

**Correlate the two.** A ring the engine detects on the vocal channel and a peak in the X32's RTA
at the same frequency are the same event seen from two places. Showing that link is genuinely
useful, and no commercial product does it.

---

## 7. X32 OSC notes

- UDP, port **10023** on the X32 Rack.
- The protocol has been reverse-engineered by the community and is not documented by the vendor.
  **Verify every path against the actual console** rather than trusting any single reference.
- Meter and RTA subscriptions expire. Renew them (`/xremote` or a meter subscribe) on a timer well
  under 10 seconds, or the stream silently stops.
- GEQ bands appear as indexed parameters on the FX slot holding the GEQ.
- Headamp gain belongs to the physical input, not the channel, and you can only reach it when a
  channel is actually fed from that input. Wrap it in a helper, because this is a common trap.
- Ch 1/2 are fed from the card (User In codes 129/130), so they have no X32 headamp. Their gain
  lives in the Apollo.

---

## 8. Non-negotiables

- Rule Zero: no changes to Console or X32 settings, by you or by the app on its own.
- No allocation, locking, or logging on the audio thread.
- If the engine fails, it must either keep passing audio or stop with a clear signal. It must never
  go silent without warning.
- Ship an **assist mode** that detects and logs without touching audio, and make it the default.
  Auto mode is opt-in.
- Locked notch filters must survive a restart.
- Document the bypass path from §2 in the README (Analog 1/2 to ADAT out 3/4 to muted X32 Ch
  11/12), so a laptop failure is one unmute away from recovery.

---

## 9. Deliverables

1. A short architecture decision record: which option from §1, and why.
2. The merged project, building clean.
3. The OSC interface documented as a table.
4. An updated README covering setup, the Console routing changes, the bypass, and the ring-out
   workflow.
5. A list of anything you could not verify without the hardware in front of you.
6. The FaderBridge removal commit, with a list of removed files.
7. A manual checklist of every Console/X32 change the design needs, for Chris to do himself.
   Nothing on that list should have been done by you.

Get audio through the engine untouched, and the tray app talking to it, before any detection is
enabled. Prove the plumbing first, then turn on the clever part.
