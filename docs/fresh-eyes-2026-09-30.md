# Fresh eyes — 30 September 2026

You asked for a regroup: look at the last few days' data with no assumptions,
explain why the tests keep missing things, and say what the simulator is
actually for. Everything below was measured today. Where it contradicts
something I said earlier this week, the earlier statement is withdrawn.

## What I looked at

Six flight-recorder captures from 27–30 September. For the first time, the
shipping detector and notch bank were run *over the real recordings* — a replay
harness (`engine/tests/sim/replay.py`) that feeds the pre-notch microphone
channel through the exact code in the engine and keeps every event and every
rejection with the reason and the numbers it was decided on. Plus the
simulator, scored for the first time on the question underneath everything:
of the rings it knows it produced, how many did the detector see, and how fast?

## The findings, in the order they matter

**1. Seeing is not the problem. It never was.**

Twenty-three loud, narrow, sustained needles above 1 kHz in the recordings.
Seventeen were real feedback. **The detector caught 17 of 17**, at a median of
**405 ms before** each one would have been visible as a needle on your display,
worst case 107 ms before. The simulator agrees: 10 of 10 rings caught, median
234 ms before visible — and every one of the ten was on the theory model's
phase ladder.

You said "you should be able to see this faster than I can." It does.

**2. What you were seeing at 1.2–1.5 kHz was your own voice.**

The other six needles were the singer. The detector's rejection list shows the
harmonic stack under each one: 1113/1484/1855 Hz is 3:4:5 of 371 Hz (F#4);
748/1129/1500/3002 Hz is 2:3:4:8 of 375 Hz; 505/1015/1261 Hz is 2:4:5 of 252 Hz
(B3). Declined for vibrato — because they had vibrato, because you were singing
them.

On a spectrum display a loud vocal harmonic is a bright narrow spike standing
alone above 1 kHz. It is indistinguishable from feedback by eye. The detector
tells them apart (harmonic family, vibrato rate); the display cannot. Part of
"I can see feedback and you won't attack it" has been this.

Two of the six were hit anyway (1266 Hz at −7 dB, 3486 Hz at −20 dB, both for
under half a second). That is the real voice-damage cost, measured.

**3. Holding was the failure, and it was worse than the fix I shipped.**

Six of the 23 got a real notch — 28 to 45 dB — and then it *bled away while the
howl was still going*. That is "you attack it and it's still there."

The hold I shipped this morning was aimed at exactly this and was miscalibrated:
the 30 September howl reads −35 to −25 dB in the detector's own units and I set
the bar at −25, so it held for none of its eleven seconds. Recalibrated to −40
from the measured levels of every screaming howl on record. Result on the same
23 needles: bled-away 6 → 2, and both remaining cases are the end of a ring
handing over to the next, not a hold failing.

**4. The guard has been wired beside your rig, not in it.**

The mute test and the tone test together prove it: monitors hear the raw mic
*and* the guard's return, summed. A 45 dB notch in one of two parallel paths
does nothing to the sum. The 7235 Hz ring on 28 September grew 27 dB under a
45 dB notch, which no loop can do. The `NOT IN THE LOOP` alarm lit correctly on
its first live outing. The fix is on the X32: the raw vocal channel must stop
feeding the monitor mix.

**5. It was silent about all of this by design.**

Rejections live in a 400-entry memory queue and reach disk only when you press
the label button; your three presses on 28 September wrote three files with
zero rejections. A suspect that has been reported once and then goes steady
produces neither events nor rejections — there was no code path that could have
told us the howl was still there. The replay harness keeps everything offline.
Live, the rejections should go to the log every time.

## Why the tests didn't catch it

- Every layer — unit, fuzz, closed loop — runs on synthesised signals. Six real
  recordings of real feedback existed and **no test had ever been shown one.**
  Being fixed now: the real howls become fixtures the ship gate replays.
- The closed-loop gate always had the guard *in* the loop, so the
  not-in-the-loop code path could not execute there. It has a disconnected mode
  now, and the moment it did it failed three times on three real defects.
- The give-up tests proved the guard could *enter* the state and never that it
  could *leave* it. The bug lived on the second ring through the same slot. T48
  now tests the exit.
- My own harness scored four correct refusals as misses because its idea of a
  howl was "looks like one on a display." Same mistake as the eye. It checks for
  a harmonic comb now.

## What the simulator is for

Its unused power was ground truth: it knows which frequency rings and when, so
detector recall and latency can be measured directly instead of inferred from
outcomes. That measurement now exists (`recall.py`) and says 10/10.

Two things it has shown that nothing else could: killing the top-end winner
unmasks bass candidates (measured with our own DSP as the cause), and pre-cutting
the predicted ladder is worth 39 dB on that bass when the measurement is current
and is *worse than nothing* from 8 cm away. It is a referee, not an oracle.

## On the ideas from last night

**Rewrite detection as a scorer instead of a veto cascade** — the real data says
no. On real singing the vetoes were right every time and recall on real feedback
is 100%. The cascade critique was built on my harness calling your voice a
howl. Withdrawn.

**Attack first, earn the way back** — yes, but the attack is already fast. The
inversion belongs in what happens *after*: a filter on a confirmed howl now
holds until the tone is actually gone (done today), and the remaining family is
"let go too early" — bleeding under a howl, hopping to a fresh slot at −12,
giving up and squatting. Each of those is a second chance handed back.

## Shipped today

- Hold recalibrated (−40) and running.
- Loop verdict switchable for replays; live it is the wiring alarm.
- Replay and recall harnesses; rejections carry spread and tolerance.
- The real recordings as a fourth gate layer (`replay_gate.py`), refusing any
  build that does worse on them than the one that cut them.

## Next, in order

1. ~~Real-recording fixtures in the ship gate~~ — **done.** 23 excerpts (17
   howls, 6 voice needles) replay on every build; all four layers run in 43 s.
   Baseline: 17/17 caught, median 432 ms before visible, 4 bleed at hand-over,
   2 voice hits. The gate had never seen feedback; now it always will.
2. Log rejections live, every time.
3. The two voice hits.
4. The hop: memory can now follow a ring across a step, but the fixture set will
   say whether that is enough.

## What I got wrong this week, so it is on record

Said the guard was never connected (it was, in parallel). Said depth doesn't
matter (it does, on the clean population). Said four real rings were vetoed
(three were your voice; the fourth is 0.2 s of something too short to judge).
Shipped a give-up flag that squatted, and a hold that didn't hold. Every one was
found by measurement within hours, which is the only part of this I'd defend.
