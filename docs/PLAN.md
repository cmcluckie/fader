# Plan

Written 2026-10-03, after the first evening of driving the rig by sweep. Two
pieces of work, in this order, on Chris's instruction; then a list of things
deliberately left alone so they are not forgotten.

**No more detector tuning until item 1 is built and measured.**

Where things stand is in `docs/wrong-machine-2026-10-03.md` and the commit log
from `11fe355` onward. The reference for both items is
`docs/references/van-waterschoot-moonen-2011.md`.

---

## 1. The rescue duck — next

**What.** A broadband gain reduction on a channel's output, applied when a ring
is out of control, held briefly, and released slowly. It lowers the loop gain at
every frequency at once and needs no frequency estimate.

**Why.** Measured on cold jumps to 20–22 dB over the ring point: a ring climbs
about 700 dB/s — floor to −21 dB in 150 ms. Narrow filters race it and, on the
builds before `294bcc8`, lost by tens of decibels; the sweep tool's kill switch
(a fader drop) ended one of three. That kill switch is a rescue duck living in
a test script. The engine needs its own. The survey lists a plain gain
reduction as the standard last resort for exactly this.

**Triggers** — either one:

| | condition | what it means |
|---|---|---|
| T1 runaway persists | an event flagged `runaway` at or above −48 dB | the first runaway call comes near −75 dB with a −45 dB filter; still running away 27 dB later, the filter is not holding it |
| T2 loud line | a line at or above 4 kHz, at or above −30 dB, 30 dB over the median of ±20 bins, for 3 frames (16 ms) | the detector has failed outright; independent of the suspect logic on purpose |

T2 reads the raw spectrum, not the peak list: the peak list is where a smeared
fast riser got refused.

**Action.** First trigger −12 dB. Each further trigger while ducked, −6 dB
more, floor −30. Attack about 2 ms. Hold 250 ms after the last trigger. Release
20 dB/s. A trigger during release deepens it again — attack first, hard, earn
the way back.

**Not when:** bypassed; or when a duck has been down a full second and the
line has not fallen (we are not in the loop — release, latch off 30 s, raise
the existing NOT IN THE LOOP alarm).

**Where.** `engine/Source/RescueDuck.h`, one small class, used by
`AudioEngine` after `bank.process` and by `fk_loopdsp`, so every gate layer
runs it. The detector exposes the T2 measurement. `/fk/rescue ch depthDb hz
levelDb reason` to the app; the app writes a row in the feedback log (gate
`rescue`).

**The risk, stated.** A duck turns a false detection from a notch into a
dropout. Hence T1 rides on the full runaway test (zero events on the six sung
fixtures) and T2 is fenced to a loud, isolated, high line. A runaway that
starts under loud singing gets no duck in this version; that is a known limit,
not an oversight.

**Status, 2026-10-03 late:** built (`RescueDuck.h`, T53, T54, both gate
layers). Items 1–3 below measured and passing; item 4, the room, is next.
One limit found on the way and not addressed: in a reverberant simulated room
the howls that take over at 15–20 dB are *low* (440, 630, 1200 Hz) and the
duck, fenced above 4 kHz, leaves them alone. The detector's low-frequency
calls are not yet trustworthy enough to hang a dropout on — see "the voice"
under Parked.

**It is built when these are measured, old against new:**

1. Unit: the state machine's timing; T2 fires within 3 frames on a −25 dB
   9 kHz line; T2 silent on a white-noise burst, on band noise at −20 dBFS
   (a sibilant), and on a harmonic stack to 8 kHz at −20 dBFS (a sung note).
2. Replay: rescue events on the six sung fixtures = **0**, gated. Count
   reported on the howl fixtures.
3. Closed loop (`preship.py`): cold start at 20 dB of margin in a loop shaped
   like the rig's — with the duck, no time above −30 dBFS, at most 0.15 s
   above −40, quiet and fully released by the end. *(First written as "peak
   15 dB lower than without". The simulator's cold start is gentler than the
   room's and never reproduced the −21 dB chirps, so there is no 15 dB to win
   there; measured instead: 0.36 s above −40 without, 0.06 s with, 47 filters
   against 36.)*
4. Live (`scripts/ringout.py`): cold steps to 20 and 22 dB over the ring point
   — loudest bin at or below −45 dB (was −21 to −23, one kill). Then two
   minutes of speech and singing with **no** rescue row in the log.

---

## 2. Measure the loop, pre-place the filters — after 1

This is "auto mode for a new room".

**What.** At setup, measure the loop's own response — magnitude and delay —
and put fixed filters on the frequencies that will ring first, before they
ring. Verify with a short ramp. Park the gain 3 dB under the measured maximum.

**Why.** Reactive notching hears the ring before it stops it; the survey names
proactive detection from a measured loop as the open direction. Our own sim
already scored it: pre-cutting the predicted ladder was worth 39 dB on the bass
when the measurement was current — and worse than nothing from 8 cm away. So
the measurement has to be cheap enough to repeat and honest about going stale.

**Already in hand.** The chirp probe in the engine (`probeState`, `chirpAt`);
`/fk/testtone`, including mute; `scripts/ringout.py` (baseline, guard ramp,
cold step); the delay comb measured tonight (lines 100 Hz apart: a 10 ms
loop); `precut_test.py`.

**Status, 2026-10-03 late:** the arithmetic and the bank's side are built and
gated (layer 5 of `preship.sh`); the engine's sweep probe and the live tool are
not, and nothing here has touched the real room.

- `engine/tests/sim/loop_measure.py`: sweep, response estimate, ladder, planner
  (the engine's own filter shape, one filter at a time, re-ranking after each).
- `NotchBank::placePinned`: a filter placed in advance that never releases, is
  never stolen, never merged onto and never counted as coverage - if the room
  disagrees with the plan the reactive guard works as though it were not there.
- Against the simulator, where the truth is known (`loop_measure_check.py`):
  the measured ladder matches the true one rung for rung in both rooms
  (frequency exact, margin within 0.5 dB, headroom within 0.1 dB) and names
  the note that rings. Pinned from the measurement and started cold nine
  decibels over the untreated edge: **no detections, no rescue, quiet from the
  first frame** - against 19 to 56 detections and a rescue when reacting.
- And the cost, which is the point: a 15 dB raise nets **+13.6 dB** over
  1-4 kHz in the rig-like room (the cut lands on the loop's own treble peak),
  and **+2.6 dB** in the reverberant one, where twelve filters are not even
  enough. That is the survey's ceiling, measured. The tool must quote NET.

Still to build: the engine's sweep probe (pass-through muted, sample-aligned
capture), `/fk/pin` and `/fk/unpin`, `scripts/measure_loop.py`, and then the
room.

**Method.**

1. *Probe, open loop.* Mute the microphone's passthrough for the second or two
   the probe lasts and play a sweep out of the return. With the pass-through
   muted the loop is open, so this cannot ring, and what comes back at the
   microphone is the loop response itself, magnitude and phase.
2. *Rank.* Candidates are the frequencies where the phase comes round to a
   whole number of turns (the ladder; spacing = 1/delay), ranked by magnitude.
   The top of that list is the order the room will ring in as gain rises, and
   the top value is the maximum stable gain at the present settings.
3. *Pre-place.* Locked filters on the top candidates, deep enough to bring them
   down to the next rung. Show the expected gain and the tone cost **before**
   applying; the survey's ceiling (about 10 dB before notches become a shelf)
   is the thing to watch.
4. *Verify.* A guard-on ramp to 3 dB under the predicted maximum. Any ring on
   the way is a prediction error, logged with its frequency.
5. *Keep and re-check.* Store per room. Before trusting a stored map, re-probe
   quietly and compare the peaks; if the comb has moved, measure again.

**It is built when:** the first five rings of a ramp land within 1% of the
predicted frequencies; the ramp with pre-placed filters spends no time ringing
where the reactive one does; and the gain it buys is reported next to what it
costs in the 1–4 and 4–16 kHz averages.

---

## Parked — known, measured, not being worked

- **The voice, under the rig's settings.** Every build ends a 3–4 s sung
  fixture with 20–35 filters, including on 250–800 Hz. The gate's voice measure
  looks at one frequency per fixture and cannot see it. Needs its own measure
  (filter count and ear-weighted harm on the sung fixtures) before it needs a
  fix. `294bcc8` adds 0–3 filters per fixture through the ordinary ladder.
- **A second ring starting under a louder one** (6.8 kHz, 22 dB over): still
  called at −32 dB.
- **No rescue below 4 kHz.** By design for now; a low howl at −15 dBFS is as
  much an emergency as a high one.
- **The runaway test in near-silence.** Three brief HF lines in the 09-27
  session (5371, 6458, 4460 Hz, up 30 dB and gone in 0.3 s, no voice present)
  were called runaways. They no longer reach the duck (bar raised to −48 dB)
  but each still takes a −45 dB filter. Marginal rings or breath noise;
  undetermined.
- **No unit test for the fast riser.** Fixtures 24–27 cover it in the replay
  gate; a synthetic T53 would be faster feedback.
- **Notch release.** "Relaxed" (ungated) against "let go" (gated); almost no
  published guidance exists.
- **Measures to adopt from the survey:** fraction of time spent howling, mean
  time to recover.
- **The ring point moves.** −12 dB at 18:53, −4 dB at 20:18 the same evening.
  A guard measurement is only comparable to a baseline taken beside it.
- **The filter pool** (48): un-merged modes cost about ten runaway modes per
  six fuzz seeds, recovered at 96. The rig has shown 14 at most.
- **The app's `cut_here`** assumes a filter width; the recording's two channels
  are the truth.
- T42 (flat plateau) parked as a deliberate failure. The app cannot start with
  the display asleep. Playback to ADAT out 3/4 never arrived, unexplained. The
  1–2 kHz resolution hole.
