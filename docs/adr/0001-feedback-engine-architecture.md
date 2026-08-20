# ADR 0001 — Feedback engine architecture

**Status:** accepted
**Date:** 2026-08-20
**Context:** merging the JUCE/C++ FeedbackKiller suppressor into the C# X32 tray controller.

## Decision

**Architecture A — a separate headless audio-engine process, supervised by the C# tray
app, talking over OSC on loopback UDP.**

The JUCE DSP (`FeedbackDetector.h`, `NotchBank.h`) is ported *as-is* into a headless engine
executable that owns Core Audio and the signal path. The C# app owns everything else — the
FaderPort/X32 world it already runs, configuration, persistence, logging, the ring-out
workflow, and supervising the engine (launch, health-check, restart, surface failure).

## Why A and not B (native dylib + P/Invoke)

The hard constraint is that **no managed code, allocation, lock, or logging touches the audio
thread**. Both A and B can satisfy that on paper. A satisfies it *structurally* — there is no
managed runtime in the engine process at all, so it cannot be violated by accident later. B
keeps the door open: a single stray `[UnmanagedCallersOnly]` render callback, or a P/Invoke
that momentarily blocks behind a GC, reintroduces exactly the stage-only dropout we are trying
to make impossible. On this project the correctness margin matters more than the one-process
tidiness of B.

Concrete reasons, weighed against this rig:

- **Fault isolation.** If the C# app crashes, hangs, or is restarted to pick up a config
  change, the engine keeps passing audio. In B a managed crash takes the audio path with it.
  §8 requires that engine failure "degrade to passing audio, or to a clearly signalled hard
  stop" — separate processes make the *good* degradation the default one.
- **The DSP ports unchanged.** The detector and notch bank already assume a JUCE audio
  callback and `juce::dsp::FFT`. A headless `juce::AudioProcessorPlayer` / `AudioDeviceManager`
  host runs them verbatim. B would mean re-hosting them under miniaudio/PortAudio and
  re-implementing the FFT dependency — more porting, more risk, for tighter coupling we do not
  want.
- **Independent development and debugging.** The engine can be run, profiled, and left in
  ASSIST for a whole rehearsal from a terminal, with no C# in the loop. The plumbing (§9
  priority) can be proven with `oscsend`/`nc` before the tray app exists.
- **The rig already has two clock/hardware domains.** Core Audio (Apollo, Thunderbolt) and the
  X32 (OSC/UDP over the LAN) are genuinely different worlds with different failure modes and
  different real-time requirements. A process boundary that mirrors that split is easier to
  reason about than one address space straddling both.

The cost of A — a second process, a supervisor, and a small IPC surface — is real but low, and
the IPC is control-rate plus one downsampled spectrum stream (§5), never audio-rate.

## Consequences

- A new `engine/` C++ target (headless, no GUI) is added alongside the existing .NET solution.
  It links `juce_audio_devices`, `juce_dsp`, and `juce_osc`; it does **not** link any GUI
  module.
- The C# app gains an **engine supervisor** (spawn, `/fk/ping` health-check on a timer,
  restart-with-backoff, surface state) and an **FK OSC client** on loopback. The existing
  `Fader.Bridge.Osc.OscMessage` codec is reused for it — no new OSC implementation.
- The OSC control/telemetry contract is frozen up front (see
  [`fk-osc-interface.md`](../fk-osc-interface.md)) so both halves can be built against it
  independently.
- Locked notches persist on the **C# side** (it owns config/persistence) and are replayed to
  the engine on launch via `/fk/notch/place` + `/fk/notch/lock`. The engine stays stateless
  across restarts except for what the app pushes — this is what makes "locked filters survive a
  restart" (§8) robust even if the crash was the engine's.
- Audio bypass/failover is a hardware path (spare ADAT + muted X32 channels), documented in the
  README, independent of either process.

## What this does not decide

- Core Audio device/channel *selection UX* (which Apollo, which ADAT pairs) — engine config,
  pushed from C#.
- Whether the engine also exposes a tiny local status page for headless debugging — nice to
  have, not now.
- Buffer size policy (32–64 samples per README) — engine config with a safe default.
