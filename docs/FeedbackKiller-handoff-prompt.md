# Task: merge FeedbackKiller into the X32 tray controller

You are extending an existing **C# macOS tray application** that controls a Behringer X32 Rack
over OSC. Attached is `FeedbackKiller.zip`, a JUCE/C++ project implementing a two-channel
acoustic feedback suppressor.

The goal is a single platform that does **mixer control and feedback suppression**, structured so
further audio tools can be added to it later. Read the architectural constraint below before
planning anything — it determines the whole shape of the solution.

---

## 1. Hard architectural constraint — read this first

**Do not port the DSP to C#.** Do not rewrite `FeedbackDetector.h` or `NotchBank.h` in managed
code, and do not put a .NET audio callback in the signal path. A garbage collector pause inside a
real-time audio callback produces dropouts that are intermittent, unreproducible, and will happen
on stage rather than at a desk. This is not a performance preference, it is a correctness
requirement.

Two architectures are acceptable. Pick one and justify the choice.

### Architecture A — separate engine process (recommended, start here)

The JUCE code becomes a **headless audio engine** — same DSP, GUI stripped out, no window. The C#
tray app supervises it and talks to it over local UDP/OSC on loopback.

- C# owns: tray UI, all X32 OSC, configuration, persistence, logging, the ring-out workflow,
  starting and stopping the engine, and surfacing engine state to the user.
- The engine owns: Core Audio I/O, FFT analysis, detection, and the notch filters.
- They exchange small control and telemetry messages, nothing audio-rate.

This is the lowest-risk option. The audio path has no managed code anywhere near it, the engine
can be developed and debugged independently, and if the C# app crashes the engine keeps passing
audio.

### Architecture B — native library with P/Invoke

Compile the detector and notch bank as a small dylib exposing a flat C ABI, and drive the audio
callback from native code (miniaudio or PortAudio). C# calls in only to configure and to poll
events — **never from inside the callback**, and never in a way that a callback can block on.

Acceptable, tighter coupling, one process. Only choose it if you can guarantee no managed
transition occurs on the audio thread.

### Not acceptable

- The DSP rewritten in C#.
- A managed delegate used as a Core Audio render callback.
- Any allocation, lock, file I/O, or logging on the audio thread in any language.

---

## 2. The rig this runs in

Understanding the signal path matters, because several design decisions depend on it.

```
Mic (Beta 58A) ──> X32 Rack XLR in 3  (lead vocal)
Mic (Beta 58A) ──> X32 Rack XLR in 4  (backup vocal)

X32 card out 1-16  ──ADAT──>  Apollo x6 ADAT in 1-8
Apollo: UAD chain per input (610-B -> Pultec -> 1176 -> verb), tapped POST-insert
Apollo ADAT out 1-8  ──ADAT──>  X32 card in 1-16  ──>  X32 channels 1-32

MacBook Pro M1  ──Thunderbolt──>  Apollo x6
Mac and X32 share a LAN via a GL.iNet GL-AXT1800 router
Apollo x6 is clock master; X32 slaves over BNC word clock
```

The feedback engine inserts itself into the Apollo return path: it takes the UAD-processed vocals
in over Core Audio and returns the notched signal to the Apollo's ADAT outputs, which feed X32
channels. The Console hardware direct-out for those channels must be disabled so the dry signal
does not double with the engine's return.

Relevant X32 facts: XLR outs 1/2 carry Mix Bus 1/2 (an IEM feed), Main L/R is on XLR outs 7/8,
and FX slots 5–8 are insert-only and currently hold unused 31-band GEQs.

---

## 3. What is in the ZIP

| File | Value | What to do with it |
|---|---|---|
| `Source/FeedbackDetector.h` | **High — this is the actual IP** | Port as-is into the engine. Do not reimplement. |
| `Source/NotchBank.h` | **High** | Port as-is. Real-time safe biquad bank, no allocation. |
| `Source/PluginProcessor.*` | Medium | Reference for wiring and lifecycle. Strip the plugin wrapper if going headless. |
| `Source/PluginEditor.*` | Low | Reference only. The GUI moves to C#. |
| `CMakeLists.txt` | Medium | Adapt for a headless target. |
| `README.md` | High | Explains the algorithm and the routing requirements. Read it. |

**Note: this code has never been compiled.** Expect build errors on first attempt. Fix them; do
not take them as evidence the design is wrong.

### The detection algorithm, in brief

A spectral peak must pass all four tests before it is treated as feedback. Preserve all four —
each one exists to reject a specific false positive, and dropping any of them produces a
suppressor that carves holes in sustained vocals.

1. **Prominence** — ≥12 dB above the local spectral median. Rejects broad musical formants.
2. **Persistence** — present in 5 consecutive analysis frames (~50 ms). Rejects transients.
3. **Pitch lock** — frequency drifts <0.6% across those frames. Rejects sung notes, which have
   vibrato of 1–3%. This test does most of the work.
4. **Harmonic rejection** — little energy at 2f or 3f, and not itself sitting at 2× or 3× a louder
   partial. Feedback is near-sinusoidal; instruments arrive with a harmonic series.

FFT is 2048 points with a 512 hop, parabolic interpolation for sub-bin frequency accuracy.
Detection latency is roughly 50–90 ms; **audio latency is only the buffer size**, because analysis
runs on a ring buffer beside the signal rather than in it.

---

## 4. Responsibility split

**C# tray app**
- Menu bar presence, status, and the main window
- All X32 OSC: channel state, faders, mutes, scenes, GEQ bands, RTA meters
- Configuration and persistence, including locked notch filters
- The ring-out workflow (see §6)
- Log files and history
- Engine supervision: launch, health check, restart, surface failure to the user

**Audio engine**
- Core Audio device and channel selection
- Per-channel FFT analysis and detection
- The notch bank and its smoothing and release behaviour
- Publishing spectrum frames and detection events upstream

---

## 5. Define the interface

Specify the message set explicitly before writing either side. Something along these lines,
as OSC on loopback:

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

Spectrum frames are the only high-rate traffic — pack them as a binary blob, downsample to
display resolution in the engine rather than shipping all 1024 bins.

---

## 6. Where the two halves earn their keep together

This is the reason for merging them, so build at least the first of these:

**RTA-assisted ring-out.** The X32 exposes its own RTA over OSC and its GEQ bands are individually
addressable as FX parameters. So the app can run a ring-out on the *system* — push the mains, read
the X32's RTA, cut the offending GEQ band — while the engine handles the *microphones*. Two
different tools for two different problems, in one workflow.

**Correlate the two.** A ring detected by the engine on the vocal channel and a peak visible in
the X32's RTA at the same frequency are the same event seen from two places. Showing that
correlation is genuinely useful and no commercial product does it.

---

## 7. X32 OSC notes

- UDP, port **10023** on the X32 Rack.
- The protocol is community-reverse-engineered, not vendor-documented. **Verify every path against
  the actual console** rather than trusting any single reference.
- Meter and RTA subscriptions expire — you must renew (`/xremote` or a meter subscribe) on a timer
  well under 10 seconds or the stream silently stops.
- GEQ bands appear as indexed parameters on the FX slot holding the GEQ.
- Headamp gain lives with the physical input, not the channel, and is only reachable when a channel
  is actually sourced from that input — worth wrapping in a helper, it is a common trap.

---

## 8. Non-negotiables

- No allocation, locking, or logging on the audio thread.
- Engine failure must degrade to passing audio, or to a clearly signalled hard stop. It must never
  degrade to silence-without-warning.
- Ship an **assist mode** that detects and logs without touching audio, and default to it. Auto
  mode is opt-in.
- Locked notch filters must survive a restart.
- Document the bypass path in the README: same mics to spare ADAT channels via a Console
  direct-out, landing on muted X32 channels, so a laptop failure is one unmute away from recovery.

---

## 9. Deliverables

1. A short architecture decision record: which option from §1, and why.
2. The merged project, building clean.
3. The OSC interface documented as a table.
4. An updated README covering setup, the Console routing changes, and the ring-out workflow.
5. A list of anything you could not verify without the hardware in front of you.

Prioritise getting audio through the engine untouched and the tray app talking to it before any
detection is enabled. Prove the plumbing, then turn on the clever part.
