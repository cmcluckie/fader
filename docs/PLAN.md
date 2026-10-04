# Plan (superseded)

> **Superseded on 2026-10-04** by [PROJECT_PLAN.md](../PROJECT_PLAN.md) (the plan) and
> [TODO-AS-BUILT.md](TODO-AS-BUILT.md) (the detail). Kept as the record of 10-03 and 10-04;
> nothing below is maintained.

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

**Status, 2026-10-04 morning - the room, first time:**

- Left running by Chris about 20 dB over the ring point for 4.5 minutes on
  `f5c5f0b`: 300 catches at a median -74 dB, nothing louder than his voice,
  **no rescue row, including while he spoke.** Cost at that gain: 20-42
  filters, 8-14 dB off 4-16 kHz.
- Cold jumps, each bracketed by a baseline (`scripts/coldjump.py`): one at
  20 over **failed** - a 10 kHz ring to -28 dB in 160 ms (600 dB/s), duck
  fired at -37 and -28, kill switch tripped. One at 16-22 over was clean
  (-52 dB).
- Cause found and fixed in `d55f580` (the side list of fast risers held
  twelve, filled low to high; the line that mattered was tracked one frame
  in two). In replay the cut now lands with that line at -78 dB instead of
  -40. **Not yet re-run in the room** - `d55f580` is the build sitting there.

Still owed, item 4 below: the 20 and 22 dB jumps again, bracketed, on
`d55f580`.

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

## The agenda after detection — Chris, 2026-10-04

Detection has been the goal until now; more of it needs more data (the studio,
`docs/studio-session.md`). Chris's list for what comes next, in his order, with
the one addition and the one caution agreed in the same conversation. This
takes precedence over section 3 below, most of which slots into it.

0. **A number for sound quality** *(the addition)*. Every recording has the
   microphone before and after the guard. Score the difference - the survey's
   frequency-weighted spectral distance - per fixture and per session, and
   separately for stretches with no feedback present. Without it, 3 and 4 are
   impressions, and 1 cannot be judged on tone.
1. **Clean out the trial and error.** Inventory every mechanism: dead,
   disabled, proven, unmeasured. For the unmeasured, switch off, run all five
   gate layers and the twenty-seed fuzz, keep only what moves a number.
   *Caution:* the tests are a good memory and a poor crystal ball, so this can
   remove something that only matters in a case not yet seen. Delete only what
   is dead or shows no effect anywhere; switch off rather than delete anything
   uncertain until the studio data is in; one removal per commit, with its
   numbers. Known debris to start from: notch pulsing (never enabled), the
   plateau path and comb (ship disabled, T42 parked), the 10 ms chirp probe
   (nothing starts it), the harm budget (run at zero), `qMaxHigh`, and the
   offender histogram, depth memory, track layer and pitch-profile seeding
   (effect never measured in isolation).
2. **Settings: hide, and mostly remove.** Four did harm this week at values the
   app offers: Attack "fast" (four frames: 10 ms for two voice hits), the
   voice budget (refused real howls), the input gate (blinded the detector in
   a quiet room), `notchQ` (never set, leaked into the merge rule). Where the
   right value has been measured, fix it. Aim: one dial - how hard it may cut
   - and on/off. Everything else automatic or behind an advanced panel.
3. **Quality degrades only as feedback is detected.** Half true already: at low
   gain the bank holds no filters. The broken half is the voice - filters on
   singing with no feedback present (20-35 per few seconds in the fixtures;
   ten when Chris spoke on 10-04, some at 141-234 Hz). That is cost with no
   need and comes first. Then release: a filter should relax to the shallowest
   depth that still holds its ring.
4. **Reduce what notches cost.** Narrower and shallower where the ring allows;
   one wide gentle filter for a hump instead of thirty needles (the planner in
   item 2 already chooses this); and cancellation (section 3, item 4), which
   avoids notching.

While Chris travels, offline only: 0, the inventory and on/off table for 1,
dead code out. Anything that changes behaviour is staged with its numbers for
him to approve.

---

## 3. From the survey — proposed, order not yet agreed

Put to Chris on 2026-10-03; he asked for the list. Nothing here is started.
The reference is `docs/references/van-waterschoot-moonen-2011.md`. Most of the
paper is either what the engine already does or needs hardware we do not have;
these are what is left, in the order I would do them.

1. **Two measures, before anything else.** *Reliability:* the fraction of time
   spent howling and the mean time to recover, in the gate and in
   `ringout.py`. *Sound quality:* a frequency-weighted spectral distance
   between what went in and what came out. The second is the "how badly did we
   hurt the voice" number this project does not have, and the parked voice
   problem cannot be fixed until it can be measured. Cheap.
2. **Release that depends on recurrence.** Hold a filter longer on a frequency
   that keeps coming back, let go sooner of one that never returns. The only
   release idea the paper records, and release is where most of our trouble
   has been. Small; the offender memory already counts recurrences.
3. **A margin readout.** From the loop measurement (item 2 above): how many
   decibels before the first ring, and at which note. The paper's guidance is
   to sit 2-3 dB under. Small once item 2 is live.
4. **Cancellation.** Model the loudspeaker-to-microphone path with an adaptive
   filter and subtract it, rather than notching. The only family the paper
   credits with 15-20 dB, and it does not carve the voice. Weeks, not days:
   it needs the source and loudspeaker signals decorrelated (prediction-error
   prefilters are the paper's recommendation) and it is the most expensive
   thing here by far. Prototype in the simulator first; keep the notches and
   the duck underneath as the safety net, as the paper advises. Its running
   estimate of the path would also give item 2's pre-placement continuously,
   with no sweep. Decide on this with the numbers from 1 in hand.
5. **Rescue below 4 kHz.** Only after 1 gives a voice measure worth trusting.
6. **A speech-only mode with a 5 Hz frequency shift.** About 6 dB for
   talking. The paper is clear it is unsuitable for sustained musical tones,
   so never for singing. Optional, last.

Not worth doing: spatial filtering (needs microphone or loudspeaker arrays);
more detection features (the detector already ended up with the pair the
paper's comparison found best).

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
- **The ring point moves, a lot, and I do not know why.** −12 dB at 18:53 and
  −4 at 20:18 on 10-03; on 10-04, −15, then −2.5 five minutes later, then −0.5,
  nothing up to +10, −8.5, −2.5 inside three minutes. The desk has no gate,
  compressor or EQ in the path (read over OSC). Someone moving in the room at
  10 kHz (a 3 cm wavelength) explains a few decibels, not twelve. Until the
  engine's sweep probe (item 2) can measure the loop directly, every jump is
  bracketed by baselines and only counted if they agree (`coldjump.py`). This
  makes the probe the next thing to build, ahead of the rest of item 2.
- **The voice, live.** On 10-04, speaking into the microphone took the filter
  count from 31 to 42 and put filters on 141, 157 and 234 Hz within 32-75 ms.
- **The filter pool** (48): un-merged modes cost about ten runaway modes per
  six fuzz seeds, recovered at 96. The rig has shown 14 at most.
- **The app's `cut_here`** assumes a filter width; the recording's two channels
  are the truth.
- T42 (flat plateau) parked as a deliberate failure. The app cannot start with
  the display asleep. Playback to ADAT out 3/4 never arrived, unexplained. The
  1–2 kHz resolution hole.
