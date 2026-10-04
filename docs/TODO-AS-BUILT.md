# To-do and as-built

The detail behind [PROJECT_PLAN.md](../PROJECT_PLAN.md). Same numbering. Kept by
Claude, updated in the same commit as the work it describes. For every feature:
the goal, how it is measured, the stages it has to pass and the threshold for
each, what is left, and what was actually built - with the commit and the number.

Baseline for every "now" figure: the engine build in the room, `d55f580`,
measured 2026-10-04 with the settings the home rig runs (Attack "fast": first
cut -18 dB after six frames; max cut -18 dB; listen band 40 Hz - 18 kHz; floor
-95 dB; voice budget off; a quiet filter held 10 s). The simulated figures are
filed under `9d17858`, which is the same engine with the hold named in the
shared header - see 8.12 for why that mattered.

---

## How this works

### Stages

A feature that touches feedback or sound moves through five stages. Each has a
threshold. Work moves to the next stage only when the threshold is met; when a
stage fails, the failing case becomes a test at the earliest stage that can
reproduce it, and work goes back there.

| Stage | Where | What it proves | Tool |
|---|---|---|---|
| **S1 Bench** | synthetic signals, no loop | the mechanism does what it says | `fk-tests`, `fk-fuzz` |
| **S2 Recorded** | real feedback and real singing, replayed | it works on what a rig actually produced | `replay_gate.py` |
| **S3 Simulated room** | the guard inside a room that answers back, with a singer | it works as a loop, and what it costs the voice | `quality.py`, `preship.py`, `loop_measure_check.py` |
| **S4 Live, home rig** | the room, Chris present | it works in air | `ringout.py`, `coldjump.py`, a sung session |
| **S5 Second room** | the studio | it was not fitted to one room | `docs/studio-session.md` |

A feature is **DONE** when S4 passes. S5 can reopen it. A feature with no
feedback or sound in it (a screen, an installer) has exit criteria instead of
stages.

### Words

- **dB over** - gain above the point where the untreated system first rings. In
  the room that point is found by a baseline sweep each time, because it moves.
- **Audible line** - -50 dBFS at the guard, which is 20 dB under a sung phrase
  on the home rig (-30 dBFS rms, measured on the 2026-09-27 session).
- **Ring** - a line the singer did not sing, that *grows* (3 dB or more above
  where it first appeared). **Tail** - a line that only falls: a stable room
  hanging on to a note near its limit. **Ghost** - a line made by the guard's
  own filter (it is in the output and not in the input).
- **Caught** - the moment 12 dB of cut is on the ring's frequency, measured from
  the guard's input and output, so the rescue duck counts. **Catch time** is
  that moment minus the moment the ring became audible; negative means the cut
  was there first.
- **Killed** - the moment the ring is back under the audible line. **Kill
  time** runs from the catch (or from audible, if later) to that.
- **Voice change %** - band by band on the ear's own frequency scale, the
  summed difference in loudness between what came out and the clean voice, as
  a share of the clean voice. Scale: the whole voice 1 dB down is 8 %, 3 dB is
  22 %, 6 dB is 40 %; one -18 dB filter at 3 kHz is 5 %, at 9.5 kHz 0.6 %.
  Defined in `engine/tests/sim/quality.py`, which checks itself against those
  figures (`--selftest`).
- **Rooms** - *rig-like*: 6 x 5 x 2.8 m, microphone half a metre from the
  speaker, coupling that peaks at 9.5 kHz; rings high and fast, like the home
  rig. *Hall*: 9 x 7 x 3.2 m, microphone three metres out; rings low and slow.
- **Voices** - *synth*: a synthetic singer, in the repository. *rig-0927*: 37 s
  of Chris singing, cut from the 2026-09-27 recording, kept on his machine.

### The three goals

| | Goal | Chris, 2026-10-04 |
|---|---|---|
| G-CATCH | catch time 10 ms or less, on every ring | "feedback is caught under 10 ms" |
| G-KILL | kill time 15 ms or less | "killed within 15" |
| G-SOUND | voice change under 15 % while holding feedback; under 3 % when nothing rings | "sound degradation is below 15 %" (the 3 % is mine: with no feedback there is nothing to pay for) |

Two things said plainly. First, "caught" is measured from the moment a ring is
*audible*, not from the moment it physically begins: the detector's shortest
look is 32 ms, so 10 ms from the first vibration is not possible, and 10 ms
from audibility is - most rings are caught hundreds of milliseconds *before*
they are audible. Second, a ring in a reverberant room decays at the speed the
room allows; below about 1 kHz that is slower than 15 ms whatever is cut. For
those the only way to meet G-KILL is to catch them before they are audible, so
the kill time is zero.

### Baseline, singer in the loop (`quality.py`, the room's own settings)

| room | voice | gain | voice change | filters held | rings heard | worst catch | worst kill | loudest |
|---|---|---|---|---|---|---|---|---|
| rig-like | synth | no loop | **37.0 %** | 31 | 0 | - | - | - |
| rig-like | synth | 6 dB under | 41.4 % (no guard: 1.1 %) | 35 | 0 | - | - | - |
| rig-like | synth | +6 | 47.6 % | 40 | 0 | - | - | - |
| rig-like | synth | +10 | 49.7 % | 42 | 0 | - | - | - |
| rig-like | synth | +15 | 52.9 % | 45 | 0 (tails 100 ms) | - | - | - |
| rig-like | synth | +20 | 55.5 % | 47 | 29, 5.0 s | +266 ms | 757 ms | -11.3 dB |
| rig-like | rig-0927 | no loop | **64.8 %** | 42 | 0 | - | - | - |
| rig-like | rig-0927 | +6 | 63.9 % | 42 | 0 | - | - | - |
| rig-like | rig-0927 | +10 | 64.2 % | 44 | 1, 40 ms | cut in place | 57 ms | -45.5 dB |
| rig-like | rig-0927 | +15 | 62.8 % | 45 | 2, 103 ms | cut in place | 110 ms | -31.1 dB |
| rig-like | rig-0927 | +20 | 77.9 % | 47 | 50, 6.7 s | lost | lost | -12.3 dB |
| hall | synth | 6 dB under | 50.6 % (no guard: 6.9 %) | 36 | 0 (tails 152 ms) | - | - | - |
| hall | synth | +6 | 67.3 % | 43 | 1, 72 ms | cut in place | 354 ms | -44.6 dB |
| hall | synth | +10 | 69.4 % | 45 | 2, 234 ms | cut in place | 158 ms | -33.0 dB |
| hall | synth | +15 | 108.1 % | 48 | 12, 7.1 s | lost | lost | -7.0 dB |
| hall | rig-0927 | +6 | 68.7 % | 44 | 0 | - | - | - |
| hall | rig-0927 | +10 | 70.6 % | 46 | 9, 2.6 s | never cut | 695 ms | -26.9 dB |
| hall | rig-0927 | +15 | 194.7 % | 48 | 28, 24.4 s | lost | lost | -4.0 dB |

Slow push (half a decibel a second from 6 under, singer on a loop; the median of
five runs that differ only in the room's noise, range in brackets): rig-like
room held to **+19.5** (17.3 to 22.6, synth) and **+19.1** (17.4 to 20.5, rig-0927); hall to
**+11.4** (9.4 to 15.1, synth) and **+14.1** (12.6 to 15.5, rig-0927). One run alone moves
3 dB or more for no reason.

Read: nothing is heard up to 6 dB over in either room, bar one 72 ms ring in
the hall. From 10 dB over, brief rings get through in a room like the rig
(40-110 ms, each with a cut already on it), and it loses its grip between 15
and 20. The hall is lost between 10 and 15. The voice is where it fails
outright, at every gain including none, and the filter pool (48) is full of
filters placed on the singer before any ring arrives. Every build's figures
are in [RESULTS.md](RESULTS.md).

### Baseline, the room itself

| when | build | what | result |
|---|---|---|---|
| 2026-09-27 | that day's | Chris singing, loop closed, five stretches | guard took **5.8 - 13.2 %** of the voice (output channel against input channel of the flight recording) |
| 2026-10-03 | fixed engine (`fce3790`) | slow ramp | held to 22 dB over the ring point, loudest ring -51.6 dB, 14-40 filters |
| 2026-10-04 | `f5c5f0b` | 4.5 min about 20 dB over, speaking part of the time | 300 catches, median -74 dB, no rescue row; guard took **69.9 %** of the speaking voice |
| 2026-10-04 | `f5c5f0b` | cold jump, 20 dB over, bracketed | **failed**: 10 kHz ring to -28 dB in 160 ms, duck fired twice, kill switch tripped |
| - | `d55f580` | anything live | **not yet run** |

The same 37 seconds of singing, two builds: the 09-27 build took 6-13 % in
the room; today's takes 65 % replayed with no loop at all.

### What the settings do (same tests, same build)

| setting | synth, no loop | rig-0927, no loop | synth, +10 | rig-0927, +10 |
|---|---|---|---|---|
| the rig as it is (attack fast, budget off) | 37.0 %, 31 filters | 64.8 %, 42 | 49.7 %, no ring | 64.2 %, one ring 40 ms |
| voice budget 30 (the 09-30 setting) | 28.1 %, 13 | 54.1 %, 29 | 33.2 %, rings heard 1.4 s | 57.7 %, 3.3 s |
| voice budget 15 | 26.7 %, 10 | 52.8 %, 28 | 32.5 %, 1.3 s | 52.7 %, 2.1 s |
| attack normal (6 frames, first cut -12) | 34.1 %, 31 | 61.1 %, 42 | 43.4 %, no ring | 63.2 %, no ring |
| attack gentle (10 frames, first cut -6) | **11.2 %**, 14 | **35.5 %**, 22 | 35.9 %, no ring | 42.1 %, no ring |
| floor -70, low edge 200 Hz | 35.0 %, 26 | 61.6 %, 41 | 48.9 %, no ring | 61.5 %, no ring |

The budget buys voice by letting rings through. **Gentle attack** is the
largest lever by far, and it was measured across everything (10-04):

- Recorded feedback: 22 of 22 howls still caught, none let go, median lead 449 ms
  (469 on fast), fast risers still cut at -74 to -79 dB. The six sung phrases
  lose **13.8 %** and carry 12 filters, against 36.8 % and 29.
- Room like the rig: about half the voice damage at every gain; no ring heard
  up to +10 with either voice; slow push the same (about +20). At +15 it is
  worse with the recorded voice (rings heard 0.8 s against 0.1 s).
- Hall: fine to +6, then much worse - at +10 it howls for most of the run
  where "fast" lets through 0.2 to 2.6 s.

So on a rig like Chris's, at working gains, gentle is better on the voice and
no worse on feedback; in a reverberant room pushed past 6 dB it is not. No
setting reaches the goal: the fault is in what gets called feedback, not in a
dial. (Decision for Chris: try gentle on the rig - it is a menu in Setup.)

---

# 1. Detection - IN PROGRESS

**Goal:** a real cut lands within 10 ms of any ring becoming audible, up to
20 dB past the room's limit.

### 1.1 Steady rings - DONE

- **Goal.** Every recorded howl caught before it is audible.
- **Measured by.** `replay_gate.py` (lead, depth on own frequency, let-go);
  `quality.py` rings heard at +6 and +10.

| Stage | Threshold to move on | Now | |
|---|---|---|---|
| S1 | T1-T3, T11, T12, T14, T16, T17, T25, T27, T28, T32, T34 pass | 58 of 58 passing (T42 parked) | pass |
| S2 | 22 of 22 detected; each cut to within 2 dB of the dial on its own frequency; none let go | 22 of 22; let go 0; median lead 469 ms before a person would see it; worst lead 20 ms | pass |
| S3 | no ring heard at +6 and +10, rig-like room, both voices | 0, except one ring at +10 with the recorded voice: 40 ms at -45.5 dB, a cut already on it. That is a ring not held down far enough, which is 2.x's business, not a ring unseen | pass, with that note |
| S4 | slow ramp holds 15 dB over the ring point with nothing above -45 dB | 22 dB over, loudest -51.6 dB (10-03); 300 catches at a median -74 dB over 4.5 min (10-04) | pass |
| S5 | the same in the studio | not run | open |

- **To do.** S5 at the studio.
- **As built.**
  - 08-20 `c4834d6` first detector: spectral peaks, prominence, persistence. `9abeca3` band widened from 8 to 12 kHz.
  - 08-21 `ee50ee5` stability judged over a rolling window, analysis twice as often. `c8a3196` growth as a rate. `e078067` keep cutting a tone that survives the cut (escalation path).
  - 08-22 `95a6e3d` slow-creep (sustain) path; tolerances scale with frequency. `d31e7d7` analysis window halved. `6257a0b` first test suite.
  - 08-27 `1bc76e9` pitch by phase. `de6a0e2` a quiet newcomer no longer has to outshout the room. `9a177dd` input gate was 35 dB too high.
  - 09-22 `14b5f2a` slow rings were the ones being missed.
  - Today: FFT 2048 at 48 kHz (23.4 Hz bins), a frame every 5.3 ms; paths 1 growth, 2 sustain, 3 escalation; vetoes unstable, vibrato, harmonic, no-growth, drifting; width gate 200 Hz.

### 1.2 Fast risers - IN PROGRESS

- **Goal.** Caught within 10 ms of audible on a cold jump 20 dB over.
- **Measured by.** `replay_gate.py` "landed" (how loud the line was when 12 dB
  of cut arrived); `preship.py` cold start; `quality.py` at +15 and +20;
  `coldjump.py` in the room.

| Stage | Threshold to move on | Now | |
|---|---|---|---|
| S1 | a synthetic 600 dB/s riser is called within 8 frames | no such test | **open** |
| S2 | all five fast fixtures have 12 dB of cut before the line reaches -50 dBFS | landed at -77.7 to -79.9 dB | pass |
| S3a | cold start 20 dB over, rig-like: 0 s above -30 dBFS, at most 0.15 s above -40 | 0.00 s, 0.06 s | pass |
| S3b | singer in the loop at +15: no ring heard | synthetic 0; recorded voice 2, 103 ms in all, loudest -31 dB, a cut already on each | **fail** |
| S3c | singer in the loop at +20: catch 10 ms or less, kill 15 ms or less | 29-50 rings heard, 5 to 7 s in all, loudest -11 dB | **fail** |
| S4 | bracketed cold jumps at 20 and 22 dB over: 3 of 3 with nothing above -45 dBFS | `f5c5f0b`: 1 of 2 (-29.5 dB, kill switch). `d55f580`: not run | **open** |

- **Exit.** S1, S3b, S3c and S4 pass.
- **To do.**
  - [ ] S1: a synthetic fast-riser unit test (fixtures 24-28 cover it only in replay).
  - [ ] S3b and S3c are failing with the filter pool full of voice filters (45-47 of 48), and at +15 each ring heard already had a cut on it. Re-measure after 3.2 before touching the detector.
  - [ ] S4: `coldjump.py` at 20 and 22 on `d55f580`, when Chris is home and says go.
  - [ ] The runaway test still calls three brief near-silent HF lines in the 09-27 session; each takes a -45 dB filter.
- **As built.**
  - 10-03 `69f6ef0` a ring that starts as a cluster of modes is tracked as one. `fce3790` a doublet is a cluster too.
  - 10-03 `294bcc8` the runaway path (path 5): above 4 kHz, six frames, a straight-line rise of 4 dB or more, 20 dB prominent, not gliding, not the voice. Fast-rising lines too wide for the main list go to a side list.
  - 10-04 `d55f580` the side list held 12 and filled low to high, so the line that mattered was tracked one frame in two; now 32, quietest replaced, louder of two kept. The 10-04 failure replays with the cut landing at -78 dB instead of -40.
  - Measured live: old engine lost at 16-18 dB over with rings at -27 dB (10-03); cold jump at 20 over reached -29.5 dB on `f5c5f0b` (10-04).

### 1.3 Low rings - IN PROGRESS

- **Goal.** No ring heard up to 10 dB over in a reverberant room.
- **Measured by.** `quality.py`, hall room.

| Stage | Threshold to move on | Now | |
|---|---|---|---|
| S1 | T12 (332 Hz ring), T28 (studio low rings), T41 (bass voice left alone) pass | pass | pass |
| S2 | recorded low howls caught before audible | **no recording of a low howl exists** - all 22 are 7.2-14.6 kHz | blocked on data |
| S3 | hall, +6: no ring heard | recorded voice 0; synthetic 1 ring, 72 ms at -45 dB | pass by a hair |
| S3 | hall, +10: no ring heard | 2-9 rings, 0.2 to 2.6 s in all, between 0.2 and 3.5 kHz; loudest -27 dB | **fail** |
| S4/S5 | a room that rings low | needs the studio | open |

- **Exit.** S3 at +10, then a real room that rings low.
- **To do.**
  - [ ] The hall's failures at +10 are in the voice's own range (0.2-1.3 kHz), where the voice tests hold detection back and the rescue duck is fenced out (2.6). Depends on 3.3.
  - [ ] Record low feedback at the studio (S2 has no data).
- **As built.** 08-27 `6f7dca9` stability gate respects what the FFT can resolve at the low end. 08-28 `e01cd02` low end was outside the listen band. 09-22 `dc1560f` T41 written (the build notched a bass voice's fundamental). 09-27 `25bd482` second, longer analyser below 1 kHz; T41 passes.

### 1.4 Voice or ring - IN PROGRESS

- **Goal.** No filter placed on a singer when nothing is ringing.
- **Measured by.** `quality.py` "no loop" case (filters held, voice change);
  `replay_gate.py` voice hits.

| Stage | Threshold to move on | Now | |
|---|---|---|---|
| S1 | T4, T31, T39, T41, T54 pass | pass | pass |
| S2 | none of the six sung fixtures cut more than 6 dB at its marked frequency | 2 of 6 hit (-25.6 dB at 1266 Hz, -26.1 dB at 3486 Hz) | **fail** (accepted as baseline since 09-30) |
| S3 | singer, no loop: 5 filters or fewer on both voices (goal: 0) | 31 (synth), 42 (rig-0927), cuts to -45 dB on fundamentals at 209-298 Hz; 14 and 22 on the gentle attack setting | **fail** |
| S4 | two minutes of singing at low gain: guard takes under 3 % | not run on this build | open |

- **Exit.** S2 at 0 of 6, S3 at 0 filters, S4.
- **To do.** This is the same work as 3.2 and 3.3; tracked there.
- **As built.**
  - 08-21 `34403cb` listen band, harmonic gate, vibrato veto. 08-22 `00f3c90` harmonic guard judges between peaks. 08-27 `5309e9f` it was flagging almost everything.
  - 09-02 `197744b` it was notching harmonics 2-13 of a sung B3. 09-06 `665da89` a sung note whose low harmonics are missing (T39).
  - 09-27 `47201c9` voice protection applies to the voice, not to what sits near it. `e2a52c2` scene held steady. 09-30 `5a9638a` the voice hit was the 10th harmonic of F4. 10-01 `66d7f6d` the hold is for howls, not the singer.
  - 10-03 `294bcc8` the runaway path fired on a scoop from 4723 to 4768 Hz; glide test added.
  - **Found 10-04:** every one of these was judged one frequency at a time. Measured across the whole voice for the first time, the guard holds 31-42 filters on a singer with nothing ringing.

### 1.5 A ring hidden under a louder one - NEW

- **Goal.** Called before it reaches -50 dB.
- **Now.** A 6.8 kHz ring under a louder one was called at -32 dB, 22 dB over (10-03, live).
- **Stages.** S2: cut a fixture from the 10-03 recording, threshold landed at -50 dB or lower. S3: two rungs over at once in the rig-like room.
- **To do.** [ ] Cut the fixture and show it failing first.

### 1.6 Other people's detectors - NEW

- **Goal.** A side-by-side table on our recordings; adopt anything that beats ours.
- **Exit.** Each candidate run over the 22 howls and the sung material through the same gate, scored on lead, hits on the voice, and voice change.
- **Candidates** (notes in `docs/references/`):
  - The 2025 sparsity-measure detector (NINOS2-T), public code and test set. Its own results: music is much harder than speech.
  - The survey's six criteria and its 2010 companion comparison. The engine already ended up with the pair that comparison found best (slope and peakness).
  - A temporal detector (2022) and the magnitude-slope-deviation tests (2016): read the papers first.
- **Needs** epic 6.4 (swappable detectors) to be done properly; a first pass can run offline on recordings.

### 1.7 Wide, flat feedback - CANCELLED (proposed)

- 09-22 `c294ea7` built and parked; `b7b6e2a` it works and it also notches the singer. Ships disabled (`plateauRiseDb 999`); T42 is kept failing on purpose so the gap stays visible.
- **If cancelled:** remove the plateau path and T42 in 9.3, which also unblocks the Windows build (7.7).

---

# 2. Suppression - IN PROGRESS

**Goal:** a ring that becomes audible is inaudible again within 15 ms of being caught.

### 2.1 Notch ladder - DONE

- **Goal.** Every recorded howl gets the dial's full depth on its own frequency.
- **Stages.** S1: T9, T10, T18, T19, T21, T35 pass. S2: 22 of 22 at or beyond the dial (-18 dB) within 2 dB - deepest cuts -20.0 to -75.9 dB. S3: closed-loop gate, top end 61 dB quieter with the guard at 3 and 9 dB over. S4: as 1.1. All pass.
- **As built.** 08-20 `ef382bf` adaptive depth, release, pool, offender memory. 08-21 `e194639`, `b6689ca` wider and harder first strike, faster attack. 08-27 `26d1abd` 48 filters; "max cut" means max. `16f0d99` a deeper cut is a narrower one. 09-04 `b47a6a8` Q 12 and -18 dB, measured on rooms. 09-22 `e9f54bf` each filter sized to its ring. 10-03 `11fe355` one `configure` call shared by engine and tests. Ladder: first strike, soft cap, hard cap (the dial), emergency -45 dB with Q 80.

### 2.2 Filters land on the ring - DONE

- **Goal.** No ring left sitting beside its filter.
- **Stages.** S1: T20, T37, T38, T52 pass. S2: `merge_audit.py` clean on all fixtures. S4: as 1.1.
- **As built.** 08-21 `2cc20d4` follow a drifting tone. 08-27 `6a6af52` tracking anchored (it was a random walk: filters wandered 650-900 Hz). 09-04 `e639e90` filters were landing beside the rings; `0d6c3d4` the bank says why it refused. 09-27 `a2534da` the ring was walking away. 10-03 `11fe355` merge only within a filter's own half-bandwidth (a 9400 Hz ring had been "placed" on a filter 380 Hz away).

### 2.3 Rescue duck - IN PROGRESS

- **Goal.** Nothing louder than -45 dB on a cold jump 20 dB over, and never on a voice.

| Stage | Threshold to move on | Now | |
|---|---|---|---|
| S1 | T53 (timing), T54 (silent on noise, sibilants, a bright sung note) | pass | pass |
| S2 | fires on 0 of 6 sung fixtures | 0 of 6; fires on 18 of 22 howls | pass |
| S3 | cold start 20 over: with the duck, 0 s above -30 and it is released by the end; disconnected: released | 0.00 s; -24 dB deepest, 0 dB at the end; released | pass |
| S3 | singer in the loop, rig-like room: 0 duck episodes up to +10 | 0 to +10; one at +15 with the recorded voice (a ring at -31 dB); 2-12 at +20, where it is losing | pass |
| S4 | cold jumps 20 and 22 over: nothing above -45 dBFS; two minutes of voice with no duck | voice: 4.5 min, none (10-04). Jumps: -29.5 dB on `f5c5f0b`; not re-run | **open** |

- **To do.** [ ] S4 jumps on `d55f580`. [ ] An indicator in the app (5.4). [ ] It is fenced above 4 kHz (2.6).
- **As built.** 10-03 `f5c5f0b` `RescueDuck.h`: first trigger -12 dB, -6 dB more per trigger to -30, 2 ms attack, 250 ms hold, 20 dB/s release; gives up and latches off 30 s if a second of ducking changes nothing. Two triggers: a runaway still running at -48 dB, or a loud isolated line above 4 kHz for three frames. Simulated cold start: 0.36 s above -40 without it, 0.06 s with.

### 2.4 Letting go - NEW

- **Goal.** Filters held never exceed rings present by more than two.
- **Now.** Hold 2 s, then 1.5 dB/s, retired at -1 dB. In the singer test the count only climbs: 39-48 held at the end of 22 s with nothing ringing. In replay 4 of 22 howls "relax" more than 8 dB while still loud; none is let go past the dial.
- **Stages.** S2: let go 0 (holds today). S3: after the gain drops back under the limit, filters fall to 2 or fewer within 10 s and no ring is heard on the way. S4: the same on the rig.
- **As built.** 08-27 `4714e82` filters release at all. 09-30 `c8613ec` it let go of a howl still screaming; `a4ea4d6` hold calibrated on the recordings. 10-01 `66d7f6d`. 10-03 `fce3790` "bled" split into relaxed and let go. The survey notes almost nothing is published on release.

### 2.5 One wide filter for a hump - NEW

- **Goal.** The same hold with half the filters.
- **Now.** The planner in `loop_measure.py` already chooses one wide filter when three or more rungs within a third of an octave are over the line; the reactive bank does not.
- **Stages.** S3: rig-like room at +10 - half the filters held, no ring heard, voice change no worse.

### 2.6 Rescue below 4 kHz - NEW

- **Goal.** A low howl never passes -30 dB.
- **Now.** Hall at +15: a howl at -4 to -7 dBFS for 7-24 s, with four duck episodes that do not stop it. The duck's triggers are fenced above 4 kHz on purpose, because the detector's low calls are not yet trustworthy enough to hang a dropout on.
- **Depends on** 3.3. **Stages.** S3: hall at +15, loudest -30 dB or lower, 0 ducks on either voice with no loop.

### 2.7 Knows when it is not in the loop - DONE

- **Goal.** Flagged within 2 s; no filter dug past the dial.
- **Stages.** S3: `preship.py` disconnected check - flagged, deepest -18 dB, duck released. S4: found the real fault on the rig 09-30 (the guard was beside the loop).
- **As built.** 09-28 `bfa5bb9`, `45d1b10`. 09-30 `837947b`. Six hits where a ring got louder under 20 dB of cut; verdict latched 30 s; alarm on the Show screen.
- **Found 10-04, not fixed.** Singing alone raises it. With no loop at all the alarm comes up 5.6 s into the synthetic singer and 25.5 s into Chris's recorded singing: a held note that swells under a deep cut is, to this test, a ring that will not die. The evidence is right and the conclusion is wrong - it means "this is not feedback", which is 3.3. Until then the alarm can be false while someone is singing.

### 2.8 Pulsed filters - CANCELLED

- 09-22 `3feff89`, 09-23 `90a0323`: implemented properly, measured much worse. The code is still in the bank (`dutyCycle 1.0` = off); removal is 9.3.

---

# 3. Sound quality - IN PROGRESS

**Goal:** voice change under 3 % with no feedback, under 15 % while holding feedback.

### 3.1 A number for sound quality - IN PROGRESS

- **Goal.** Reported for every version, simulated and live.
- **Exit.**
  - [x] The measure exists and checks itself against known inputs (`quality.py --selftest`: 25 checks).
  - [x] The loop it runs in matches the algebra (closed-loop colouration within 0.02 dB rms of 1/(1-H)).
  - [x] It reads the live flight recordings (output channel against input channel).
  - [x] In the ship gate (8.6).
  - [x] Logged per version (8.7).
  - [ ] Chris has listened to examples and agrees with the scale (8.11).
- **As built.** 10-04 `quality.py`, `voices.py`, `make_voices.py`. Singer fed in at the microphone; fader after the guard, as on the rig; clean reference is the same voice through the same amplifier and speaker with no loop and no guard.

### 3.2 Leave the singer alone - NEW

- **Goal.** Under 3 % with nothing ringing.

| Stage | Threshold to move on | Now | |
|---|---|---|---|
| S2 | six sung fixtures, whole phrase: under 5 % each | 29.7-40.7 %, average 36.8 %; 22-35 filters each | **fail** |
| S3 | singer, no loop, both voices: under 10 % to move on; **goal 3 %** | 37.0 % (synth), 64.8 % (rig-0927) | **fail** |
| S3 | no more rings heard than today: none to +6 in either room, none to +10 rig-like with the synthetic singer | (the bar it must not lower) | - |
| S4 | two minutes of singing at low gain: guard takes under 3 % | 09-27 build took 6-13 % at working gain | open |

- **To do.**
  - [x] Whole-phrase measure in `replay_gate.py` (S2), 10-04. Reported; gate it once it is under 10 %.
  - [ ] Build 3.3; then re-measure everything above.
  - [ ] Decide the rig's Attack setting. The numbers are in "What the settings do": gentle takes 11 % and 36 % against 37 % and 65 %, with no ring heard up to +10 in a room like the rig; it is worse at +15 and in a hall past +6. It is a menu in Setup - Chris can try it on the rig, and `session_score.py` will say what it took.

### 3.3 Did the cut work? - NEW

- **Goal.** 3.2 met with no ring caught later than today.
- **Idea.** The guard is in the loop, so it can do what a listener cannot: cut, and watch. A real ring is the loop's own output coming round, so cutting it changes what arrives at the guard's *input*: it falls, or it goes on climbing more slowly. A sung note is not coming round the loop and does not care. A line that is cut by 12 dB or more, keeps being re-detected, and is neither falling nor climbing has not answered the cut - so it is not feedback.
- **Where the evidence already is.** `NotchBank::trigger` already compares the level at each re-trigger with the last one (`lastLevelDb`), and counts the case "louder under 20 dB of cut" (`futileHits`). It uses that count for one thing only: the NOT IN THE LOOP alarm. The case next to it - "the same, under a deep cut" - is the singer, and today it is answered by cutting deeper: first cut past -30 dB within 0.8-1.4 s of singing, 18 of 48 filters deeper than -30 dB on Chris's recorded voice.
- **Design, to be built behind a switch that ships off.**
  1. *Not answering:* a filter with 12 dB or more applied, re-triggered three times over at least 100 ms, input level within 2 dB of where it stood when the cut landed.
  2. *Then:* release that filter quickly (100 ms, not the 2 s hold and slow bleed) and ignore that line for as long as it stays, plus a second.
  3. *If that was wrong* - it was a ring sitting just over the edge - letting go is itself the test: a ring jumps the moment it is released, the growth path catches it within a few frames, and the filter goes back marked proven, not to be challenged again for ten seconds. Cost of a wrong release: one blip of about 2 dB.
  4. *Not in the loop* stays as it is: louder under a deep cut, six times.
- **Why first.** It is most of the 60 %, and most of the full pool that then gets in the way of real rings (1.2 S3c, 1.3).

| Stage | Threshold to move on | Now |
|---|---|---|
| S1 | (a) a steady voice-like tone that ignores cuts is released within 300 ms and not re-cut while it lasts; (b) a ring in a closed loop that dies under its cut is not released early; (c) a ring 20 dB over, still climbing under its first cut, is never released; (d) a ring left just over the edge by its cut: released once, re-caught within 100 ms, then held | none of these tests exist |
| S2 | cannot be tested on recordings: a recorded howl does not answer a cut either. The rule is switched off in replay, like the loop verdict. 22 of 22 howls unchanged with it off. | - |
| S3 | singer, no loop: under 10 % on both voices (goal 3 %), 5 filters or fewer; no more rings heard than today at any gain; slow push not lower than today's range; NOT IN THE LOOP not raised by singing | 37 % / 65 %; 31 / 42 filters; alarm raised at 5.6 s / 25.5 s |
| S4 | two minutes of singing at low gain: guard takes under 3 %; bracketed cold jumps no worse | not run |

- **Risk.** (d) above is the one that can hurt: if the re-catch is slow, a marginal ring pumps. S1(d) has to pass before anything else is built, and S3's slow push is the check that it holds in a room.

### 3.4 Cost follows feedback - NEW

- **Goal.** Damage rises only as real rings appear.

| Gain | Threshold (goal) | Now, rig-like (synth / rig-0927) | Now, hall |
|---|---|---|---|
| 6 dB under | 3 % | 41 % / 63 % | 51 % / 62 % |
| at the limit | 5 % | 49 % / 63 % | 60 % / 65 % |
| +6 | 10 % | 48 % / 64 % | 67 % / 69 % |
| +10 | 15 % | 50 % / 64 % | 69 % / 71 % |

- **First milestone before the goal:** back to what the 09-27 build did in the room - 13 % or less at working gain.
- **Depends on** 3.2, 3.3, 2.4.

### 3.5 Cheaper cuts - NEW

- **Goal.** Half the voice change for the same hold.
- **Found 10-04.** A deep narrow filter near 300 Hz goes on sounding its own note for 23-34 ms at -43 dB after the singer stops (a "ghost"): heard at every gain from 3 dB under upward on the synthetic singer.
- **To do.** [ ] Depth and width per ring from the measured margin, not from a ladder. [ ] No filter narrower than its own ringing allows below 500 Hz. [ ] 2.5.

### 3.6 Cancellation - NEW

- **Goal.** 6 dB more gain at under 5 % voice change, in the simulator first.
- **What.** Model the path from speaker to microphone and subtract it, instead of notching. The survey credits this family with the most headroom and it does not carve the voice. Reference implementation to study: MuTap (open source, MIT, C++), which reports about 9-10 dB.
- **Stages.** S3 only until it earns more: a prototype stage in `quality.py`, both rooms, both voices.
- **Needs** epic 6.

### 3.7 Voice budget - CANCELLED (proposed)

- 08-28 `6e206de` a cap on total cut. 09-04 `75bdb9a`, `2e1d6df` it stood back during a howl. 09-22 `02d1766` in ear-weighted units. 09-26 `0c2adf1` reached the app. 09-27 `79d32b2` a full budget reallocates. 10-03 `3629b7f` a full budget yields to a loud ring.
- On the rig it was 30 on 09-30 and is off now. Measured 10-04 (table above): at 30 it lowers voice change by a third and lets a ring be heard for over a second at +10.
- **If cancelled:** remove the setting from the app (5.6) and the code (9.3) once 3.3 is in.

---

# 4. Room setup - IN PROGRESS

**Goal:** start to safe-to-sing in under a minute with no audible ring; stated headroom within 1 dB of the truth.

### 4.1 Ring-out in the app - DONE

- **Exit.** [x] Start ring-out clears the unlocked filters. [x] "Lock found" holds them. [x] Locked filters are saved and replaced on every engine start.
- **As built.** 08-20 `badca8e` locked filters persisted to `notches.json`. 08-21 `a271714` ring-out card and per-filter list (Hold, Remove).

### 4.2 Ring-out by fader sweep - DONE

- **Exit.** [x] Baseline and guarded sweeps. [x] Kill switch live before the first move. [x] Prints the running build. [x] Cold jump bracketed by baselines and rejected if they disagree by more than 3 dB.
- **As built.** 10-03 `4b293bf` `scripts/ringout.py`. 10-04 `scripts/coldjump.py`. Results under `~/Documents/FeedbackKiller/ringout/`.
- **Open note.** The room's ring point moved 12 dB within minutes on 10-03 and 10-04, unexplained; the loop probe (4.3) is the instrument for it.

### 4.3 Measure the loop - IN PROGRESS

- **Goal.** First five rings predicted within 1 %.

| Stage | Threshold | Now | |
|---|---|---|---|
| S3 | measured ladder against the simulator's own: first five rungs within 1 %, margin within 1.5 dB, headroom within 1 dB, the note that rings among the first three | exact, 0.5 dB, 0.1 dB, yes - both rooms | pass |
| S4 | first five rings of a live ramp within 1 % of the prediction | engine probe not built | open |

- **To do.** [ ] Engine sweep probe: pass-through muted, sample-aligned capture. [ ] `/fk/probe`. [ ] `scripts/measure_loop.py`. [ ] Remove the old 10 ms chirp probe that nothing starts.
- **As built.** 10-03 `4b1c88c` `loop_measure.py` (sweep, response estimate, ladder, planner), `loop_measure_check.py` (gate layer 5).

### 4.4 Pre-placed filters - IN PROGRESS

- **Goal.** A cold start 9 dB over with no ring at all.
- **Now (S3).** Pinned from the measurement and started cold 9 dB over: no detections, no duck, quiet from the first frame, against 19-56 detections reacting. A 15 dB raise nets +13.6 dB in the rig-like room and +2.6 dB in the hall.
- **To do.** [ ] `/fk/pin`, `/fk/unpin`. [ ] App: show the plan and its cost before applying. [ ] S4.
- **As built.** 10-03 `4b1c88c` `NotchBank::placePinned`: never released, never stolen, never merged onto, never counted as coverage.

### 4.5 Headroom readout - NEW
- **Goal.** Within 1 dB of the measured ring point. **Needs** 4.3 live.

### 4.6 Room memory - NEW
- **Goal.** A stale map is detected, not used. **Now.** `pitch-profile.csv` remembers where a room has rung and is meant to seed those regions at start. Found 10-04 by reading the source: the seed is erased when the audio device is configured, a moment after it is loaded, so it has no effect in the engine (it does in the fuzz, which seeds later). Confirm with a test, then wire it properly or remove it.

### 4.7 One-button setup - NEW
- **Goal.** Under 60 s, no audible ring. **Needs** 4.3-4.6.

### 4.8 Desk-EQ ring-out - CANCELLED (proposed)

- 08-20 `81ce119` RTA-assisted ring-out on the X32's GEQ, paths verified against the console. Never connected to the app: `RingOutSession.cs` is referenced nowhere. The README still describes it.

---

# 5. Display - IN PROGRESS

**Goal:** from the Show screen alone, name any ring's frequency within 2 s and see what the guard is costing the voice.

### 5.1 Show screen - DONE
- **As built.** 08-21 `618f96a` Show and Setup. `8129fd2` one vocabulary for guard state. 09-27 `24fe05a` what it hears: level, note, voice or music. Guard pill, HEARING / NOTE / ENGINE / LOAD, a tile per channel with meter and filter count, LAST CATCH, GUARD on/off, PANIC (hold 1 s), CAPTURE, THAT WAS FEEDBACK (`ShowView.cs`).
- **Known.** PANIC also erases the locked filters saved for the room.

### 5.2 Where feedback is - IN PROGRESS
- **Goal.** Every active filter and the latest ring labelled with its frequency.
- **Exit.** [x] Latest catch printed with frequency. [ ] Every filter labelled, not the four deepest. [ ] A detection marked with its frequency, not an unlabelled ring that fades in 2.2 s. [ ] The trace cannot miss a narrow line: above 1.3 kHz it samples one bin per band and skips the rest.
- **As built.** 09-01 `d6c7b0a` the EQ being applied is drawn. 09-04 `dd8c04d` shows what leaves, not what arrives.

### 5.3 What it is costing - NEW
- **Goal.** Voice change % on screen, within 3 points of the measured figure.
- **Now.** One line: "guard is removing x dB of 4-16k".

### 5.4 Alarms - IN PROGRESS
- **Exit.** [x] Engine down. [x] NOT IN THE LOOP. [ ] Rescue duck: the engine reports it and the app only logs it. [ ] The app's own log lines are shown nowhere.

### 5.5 Setup screen - DONE
- **As built.** Device, console address, listen band with draggable edges and floor, low-edge presets, Attack, Max cut, Voice budget, signal-path check, capture, ring-out, channel strips (arm, name, input, return, level, filters), filters in place (`SetupView.cs`).
- **Known.** Sample rate and buffer are fixed text (48 kHz, 64). The Auto-floor button exists and is not on the screen, so dragging the floor switches auto off with no way back. Capture's label does not follow the Show screen's.

### 5.6 One dial - NEW
- **Goal.** Guarding in three choices or fewer.
- **Evidence for which settings go.** Attack: 32 % against 7 % of the voice (table above) - a setting that large should not be a user's guess. Voice budget: 3.7. Floor and band edges: 1-2 points. `notchQ`: never sent by the app, and leaked into the merge rule.
- **Exit.** [ ] Each setting fixed at a measured value, made automatic, or moved behind an advanced panel. [ ] One dial: how hard it may cut.

### 5.7 Stage switches - NEW
- **Goal.** Any stage off in one tap. **Needs** epic 6.

### 5.8 Truthful picture - NEW
- **Exit.** [ ] The LOAD readout shows a real figure (the engine never computes it; it is always 0). [ ] The drawn curve uses the engine's filter width (it assumes Q 25; the engine uses 12 and per-ring widths, and does not report them). [ ] The "Detector - advanced" cards show live values (they say prominence 10 dB, Q 25, band 200 Hz-16 kHz; the engine runs 12, 12, and the user's band). [ ] A disarmed channel's trace and filters are cleared.

### 5.9 Desk analyser overlay - DONE
- **As built.** 08-20 `6f52c68`, `dbd7a35`. Engine spectrum, X32 RTA, filters, detections coloured by whether the desk's analyser agrees. Display only.
- **Known.** With more than two channels the extra ones overwrite one trace.

---

# 6. Engine architecture - NEW

**Goal:** any stage can be turned off from the app or a test with no other change; results can be reported per stage.

### 6.1 Separate audio process - DONE
- **As built.** 08-20 `95dcee5`, `7b12e76`, `124a974`; `docs/adr/0001`. Headless JUCE engine; C# app; OSC on loopback (10024 commands, 10025 telemetry).

### 6.2 Stage interface - NEW
- **Goal.** A new algorithm is added without editing the others.
- **Exit.** [ ] One interface: prepare, process a block, on/off, parameters by name, a status report, a cost report. [ ] The detector-and-bank, the pinned filters and the rescue duck each implement it. [ ] `/fk/stage <name> <0|1>` and a per-stage telemetry message. [ ] `fk_loopdsp` exposes the same switches to the tests. [ ] No change in any gate number from the refactor alone.
- **Already this shape.** `RescueDuck` (own class, own switch `/fk/param rescue`), pinned filters (own lifetime, separate from the reactive ones).

### 6.3 Fixed chain - NEW
- **Goal.** No added delay. Order: pinned room filters, canceller (when it exists), detect-and-notch, rescue duck. One pass, one buffer.

### 6.4 Swappable detectors - NEW
- **Goal.** Two detectors compared on the same audio. Several may watch; one bank acts. Serves 1.6.

---

# 7. Signal path and reliability - IN PROGRESS

**Goal:** one buffer of delay, a wiring fault flagged within 2 s, a dead engine never a dead microphone.

### 7.1 Engine supervision - DONE
- **As built.** 08-20 `124a974` supervisor: starts the engine, restarts it if status stops for 6 s, re-sends every setting and locked filter. 08-21 `dae52ba` a busy telemetry port no longer takes the app down. 08-20 `5b61c15` on/off remembered.

### 7.2 Devices and channels - DONE
- **As built.** 08-20 `906b705`, `e562920`, `0a3677a`, `125677e`: device picker, up to eight inputs. 08-21 `6b90290` a return per input. 48 kHz, 64-sample buffer, fixed.

### 7.3 Signal-path check - DONE
- **As built.** 08-22 `0f718d3` plays a brief tone and asks the X32's meters whether it arrived. 08-21 `84143d0` `scripts/find-return.sh`.
- **Known.** Runs against a blank console address without complaint.

### 7.4 Build stamp - DONE
- **As built.** 10-03 `b71ce6f` after the bundle shipped a three-hour-old engine: the engine writes its build to `engine-build.txt`, the build script rebuilds it every time, the sweep prints it.

### 7.5 Dead-engine fallback - NEW
- **Goal.** Audio passes within 100 ms of the engine dying.
- **Now.** The engine *is* the pass-through; if it dies the channel is silent. The tray says "engine down - audio bypassed", which is not true of anything the app does. The README's answer is a spare pair of muted channels on the desk.

### 7.6 Starts with the display asleep - NEW
- **Now.** It cannot (found 10-03).

### 7.7 Windows - IN PROGRESS
- **Exit.** [x] Engine and app build with ASIO (`43e1adf`). [ ] The build script finishes: it runs the unit tests and stops on any failure, and T42 fails on purpose. [ ] The ship gate runs on Windows. [ ] Run on a real interface.

**Open, not yet a feature:** playback to ADAT out 3/4 never arrived (unexplained); the 1-2 kHz hole between the two analysers.

---

# 8. Testing and measurement - IN PROGRESS

**Goal:** one command gives a version's full results in under five minutes; every version's results are logged.

### 8.1 Unit tests - DONE
- `engine/tests/test_detector.cpp`: 58 passing, T42 parked. First suite 08-22 `6257a0b`; from the rig's own feedback 08-27 `c049566`.

### 8.2 Random-feedback test - DONE
- `engine/tests/test_fuzz.cpp` (09-22 `fd27b30`, `feb9d27`): modes drawn from the loop equations, scored on runaways and ear-weighted harm. Gate runs 6 seeds: 144 runaway modes. Six-seed differences are noise; a verdict needs 20 seeds (`scripts/fuzz-sweep.py`).

### 8.3 Simulated-room gate - DONE
- `preship.py` (09-28 `8db4ccd`): top end 61 dB quieter with the guard at 3 and 9 dB over; disconnected check; cold start.

### 8.4 Real-recording gate - DONE
- `replay_gate.py` (09-30 `a4ea4d6`, `c637469`): 28 fixtures - 22 howls (5 fast), 6 sung. Let go 0, relaxed 4, voice hits 2, median lead 469 ms, rescue on 18 howls and 0 voices.
- **Known limit.** The voice measure looks at one frequency per fixture; 3.2 adds the whole phrase.

### 8.5 Loop-measurement gate - DONE
- `loop_measure_check.py` (10-03 `4b1c88c`). See 4.3, 4.4.

### 8.6 Singer in the loop - DONE
- **Goal.** Part of the ship gate.
- **Exit.** [x] Singer as the source inside the loop. [x] Two rooms, two voices, nine gains, a moving microphone, a slow push (median of five): 68 runs in about 100 s. [x] Self-check of the ruler (25 checks). [x] Rings, tails and ghosts told apart. [x] A quick subset in `preship.sh` as layer 6, with regression thresholds: 5 runs, 4 s. [x] Logged (8.7). [x] Works against past builds' libraries: seven builds back to `11fe355`, each rebuilt from nothing.
- **Regression thresholds in the gate** (rig-like room, synthetic singer; "no worse than `d55f580`", not the goals): voice change with nothing ringing 40 % or less; no duck on a voice alone; no ring heard at 6 under, +6 or +10; voice change at +10 53 % or less. (35 and 51 for a few hours, until the hold was corrected - 8.12.)
- **Method.** As the published comparisons do it (`docs/references/`): the voice is the loop's source, the clean reference is the voice with no loop, scores are gain held, distance from the clean voice, time ringing, and time to recover.

### 8.7 Results log - IN PROGRESS
- **Goal.** Any version's numbers found in one place.
- **Exit.** [x] `ledger.py`: one line per result with build, ruler, date, kind (bench, recorded, simulated, live), which test, pass or fail. [x] Every gate layer writes to it (`preship.sh --record`). [x] `docs/RESULTS.md` generated from it (`scripts/results.py`). [x] Backfill: seven builds from `11fe355` rebuilt and re-measured with today's tests; 32 live sweeps of 10-03 and 10-04 imported from their own folders; the voice scored in two live recordings (`session_score.py`). [ ] `ringout.py` and `coldjump.py` log themselves under the running engine's build - written, never yet run: the first live session proves it.
- **Two mistakes made and caught on the day, both now impossible.** The first backfill was refused by the log because a new file had appeared in the tree half way through (rule 8 in TESTING.md). And the past builds it would have recorded were wrong: three of seven libraries were copies of their neighbour, because the compile had been skipped (rule 7). Had the log not refused, three builds would have been filed with another build's numbers.

### 8.8 Live test tools - DONE
- See 4.2. Rule since 10-04: a live number counts only between two baselines that agree.

### 8.9 Test voices - IN PROGRESS
- **Exit.** [x] Synthetic singer, 22 s, deterministic, in the repository: legato, a dead-straight held note, sibilants, a scoop to a loud bright note, staccato, a long "ee", a low phrase. [x] Chris's singing cut from the 09-27 recording, five stretches, 37 s, kept on his machine (the repository is public). [ ] Clean dry singing with the PA muted, from the studio. [ ] Chris decides whether any real voice goes in the public repository.
- **Limit.** The 09-27 stretches were recorded with the loop closed; they carry a little of that room. The synthetic singer is a probe, not a singer.

### 8.10 Second room - NEW
- `docs/studio-session.md`: what to collect, in order.

### 8.11 Listening check - NEW
- **Exit.** [x] The same phrase rendered several ways, each file named with its measured change (`listen.py`, 10-04): clean; today's build with nothing ringing (32 % synthetic, 60 % Chris); the same with gentle attack (7 %, 29 %); one filter (5-7 %); 2 dB quieter (16 %); and what the 09-27 build actually sent to the speakers (8 %). [x] Three of Chris's own sent to him 10-04. [ ] Chris listens. [ ] The scale, the audible line (-50 dBFS) and the 3 % / 15 % goals are confirmed or moved.

### 8.12 One machine - IN PROGRESS
- **Goal.** No setting differs between the engine in the room and any test.
- **Exit.** [x] The detector's and the bank's settings come from one header and one `configure` call, and the tests read the rig's own settings file (`11fe355`, after a week of gating a machine the room had never heard). [x] The hold time: 10 s in the engine, 2 s in the test library and the fuzz - found and fixed 10-04 (`9d17858`); past builds are re-measured with the same correction. [x] The fuzz confirms in six frames like the app (it was four). [ ] The fuzz has no rescue duck. [ ] A check that fails the gate when the two drift: the engine and the test library each print their full effective settings, and the gate compares them.
- **What the 10 s hold changed, same build:** voice taken with nothing ringing 31.9 -> 37.0 % (synthetic) and 60.5 -> 64.8 % (recorded); in the rig-like room with the recorded voice one ring heard at +10 (40 ms) and two at +15 (103 ms) where there had been none; fuzz 144 -> 111 runaway modes. The gate had been flattering the build on both counts.
- **Why it keeps happening.** A setting can be given in four places - the app, the engine's own start-up values, the shared header, and a class's built-in default - and a test that forgets one silently runs the last.

---

# 9. Code health - IN PROGRESS

**Goal:** every mechanism has a measured effect on record, or is gone.

### 9.1 Inventory - DONE
- **Exit.** [x] App (10-04): [inventory/app-2026-10-04.md](inventory/app-2026-10-04.md). [x] Engine (10-04): [inventory/engine-2026-10-04.md](inventory/engine-2026-10-04.md) - all 43 detector parameters, every bank knob, every mechanism, each marked live, off by its value, unreachable or dead, with file and line.
- **What the engine inventory found.**
  - **The tests still do not run the room's machine.** The engine holds a quiet filter for **10 s** before letting it go (`AudioEngine.h:519`); the test library and the fuzz hold it for **2 s** (the bank's own default). Every gate layer has been measuring a guard that lets go five times sooner than the one in the room. The fuzz also confirms a ring in 4 frames where the app sends 6, and has no rescue duck. Tracked as 8.12.
  - **Room memory does nothing.** The remembered ring frequencies (`pitch-profile.csv`) are loaded once at start and erased when the audio device is configured a moment later (read in the source; not yet run). 4.6.
  - **Off by its value, and unreachable while it is:** the plateau path and its comb filters; the voice budget and everything under it; pulsing.
  - **Dead:** the 10 ms chirp probe (nothing can start it); `qMaxHigh`, `qWidenAboveHz` and the function that reads them; `histTopHz`; the CPU figure (never written, so the app's LOAD readout is always 0); two unused accessors.
  - **Read but never decisive at the shipping values:** `reopenMarginDb`, `minReleaseGap`, `plateauMinHz`.
  - **Live, with nothing testing it:** the runaway path has no unit test (1.2 S1); the "drifting" veto, cluster matching and the rising-line list are reached only through the simulator; nothing fills the pool of 48 to test stealing.
  - **Telemetry only, no effect on audio:** the track layer.
  - **Debug printing left on** in the engine, five lines a second.
  - Comments in four places describe a machine that no longer exists (paths 1-3 only, Q 40, "ASSIST").
- **App findings not filed elsewhere.** Unused: `FkMode`, `RingOutSession`, `CorrelationMonitor`, several controller properties. "Release all" sends 48 messages per channel into a queue of 128. A second window survives the engine being switched off.

### 9.2 On/off table - NEW
- **Exit.** [ ] Each live mechanism whose effect has never been measured on its own switched off in turn - all six gate layers, 20 fuzz seeds, the full singer study - one row each. Candidates, from the inventory: offender memory and fast-track; depth memory; filter tracking; the coverage test; the harmonic family test; the low-band analyser; the sustain hold; cluster matching; the rising-line list; each veto.
- **Needs** a switch per mechanism in the test library, which is what epic 6 builds; until then each needs a hand-made build.
- **Caution agreed 10-04.** The tests are a good memory and a poor crystal ball: switch off rather than delete anything uncertain until the studio data is in; one removal per commit, with its numbers.

### 9.3 Remove the dead - NEW
- **Safe now (dead by the inventory, no behaviour change):** the chirp probe; `qMaxAt`, `qMaxHigh`, `qWidenAboveHz`, `histTopHz`; the two unused accessors; the engine's debug prints; in the app `FkMode`, `CorrelationMonitor` and the unused controller properties. One per commit, gate numbers unchanged before and after.
- **Waiting on a decision:** pulsing (2.8, cancelled); the plateau path, the comb and T42 (1.7); the voice budget (3.7); `RingOutSession` and `X32Geq` (4.8).
- **Either wire it or remove it:** the CPU figure behind the LOAD readout; the pitch-profile seed.
- **Old test tools, checked 10-04.** Removed: `precut_test.py` (its question is gate layer 5 now) and `recall.py` (broken since 09-30, when the replay code it borrowed from changed; nobody had noticed, which is the argument for 9.5). Kept, and they run: `timeline.py` (a howl's story from a flight recording), `merge_audit.py` (2.2's check), `make_fixtures.py`, `scripts/fuzz-sweep.py`.

### 9.4 Documents match the code - IN PROGRESS
- **Exit.** [x] README, feedback section (10-04): eight channels not two; the OFF / ASSIST / AUTO modes replaced by what exists (engine, arm, guard, capture, panic); the desk-EQ ring-out stated as not connected; the log's ten columns; buffer fixed at 64; "audio bypassed" explained as a warning, not a bypass; the stale "nothing was run on a PA" table rows removed. [x] `docs/fk-osc-interface.md` rewritten against `EngineMain.cpp` (10-04): `/fk/mode` gone, twelve addresses added, the event's eight arguments, 48 filter slots, which parameters the app actually sends. [ ] Stale comments in six source files (list in 9.1's app findings). [ ] `diagnostics/FeedbackSelfTest` still asserts the old five-column log header - not run on 10-04 because the diagnostics talk to the live engine's ports. [ ] The app's "Detector - advanced" cards (5.8).

### 9.5 Test tools in one place - NEW
- **Goal.** One command, and no file nobody runs.
- **Now.** 22 Python files (two leftovers removed 10-04): the ship gate (7), the singer test and its voices (4), the results log (4), the live tools (2), tools for studying a recording or sweeping settings (4), and the icon maker. Eight were added on 10-04. None of it ships in the product.
- **Exit.** [ ] One entry point (`fk test ...`, `fk results`, `fk live ...`) over one package. [ ] The leftovers in 9.3 removed. [ ] `docs/TESTING.md` lists every file and nothing else exists.

---

# 10. Release - NEW

**Goal:** a sound engineer installs it and is guarding in ten minutes without us.

### 10.1 Mac app - DONE
- `scripts/build-feedback-fader.sh`: runs the ship gate, builds the engine with its stamp, assembles `dist/FeedbackFader.app`. Apple silicon only; needs .NET 8 installed.

### 10.2 Installer - NEW
- **Exit.** [ ] Self-contained (no .NET to install). [ ] Intel or universal build. [ ] A disk image or package, not a bare folder.

### 10.3 Signing - NEW
- **Now.** None on either platform; Windows warns on first launch.

### 10.4 Versions - NEW
- **Now.** "1.0 / 1" hard-coded on the Mac; the engine's 0.1.0 is shown nowhere; no About screen. The engine build stamp exists (7.4) and is not shown in the app.

### 10.5 Licences - NEW
- **To settle.** JUCE's licence for a distributed product. Steinberg's ASIO terms (the Windows engine is built with their headers). The room simulator's dependencies are test-only.

### 10.6 User guide - NEW

---

## Log

One entry per working day: what was asked, what was built, what was measured,
what was found, what went wrong. Newest first.

### 2026-10-04

**Asked.** (1) Standardise the test results - simulated, live, which version,
when; log every version's tests; make sound quality one of them; use sample
singing. (2) The singing cannot be laid on top; it has to be in the loop.
(3) Look at how the research does it, and at other algorithms. (4) Turn this
into a real project: a plan of epics and features with four statuses and a
goal on each, and a detailed to-do and as-built list with exit criteria and
thresholds. (5) Should each algorithm be its own engine, switched from the UI?

**Built.**
- `PROJECT_PLAN.md` (10 epics, 73 features) and this file.
- `quality.py`: a singer inside a simulated loop, scored against the same
  voice with no loop. Two rooms, two voices, nine gains, a moving microphone, a
  slow push (median of five). 68 runs in about 100 s. Self-check: 25 known
  answers. The loop matches 1/(1-H) within 0.02 dB.
- `voices.py` (a synthetic singer, in the repository), `make_voices.py` (real
  singing cut from the recordings, kept local), `listen.py` (the same phrase
  rendered several ways, named with its measured change).
- `ledger.py`, `scripts/results.py`, `scripts/measure_version.py`,
  `scripts/session_score.py`; every gate layer and both live tools write to
  the log; `preship.sh --record`.
- Gate layer 6 (singer in the loop, 4 s). The recorded-feedback gate scores
  each sung fixture as a whole phrase.
- `docs/TESTING.md`, `docs/RESULTS.md` (generated),
  `docs/references/other-work-2026-10-04.md`; `docs/fk-osc-interface.md` and
  the README's feedback section rewritten against the code.

**Measured, for the first time.**
- Voice change with nothing ringing: 37 % (synthetic), 65 % (Chris, recorded),
  31-42 filters - the same on every build back to `11fe355`. (First measured as
  32 % and 60 %, with the test library's 2 s hold.) In the room on 09-27 the
  build of the day took 6-13 % of the same singing; on 10-04 at about 20 dB
  over the guard took 70 % of his speaking voice.
- The six sung fixtures, whole phrase: 30-41 % taken, 22-35 filters each. Four
  of them have read "left alone" since 09-30.
- Rings heard: none up to +6 in either room; brief ones from +10 in the
  rig-like room (40-110 ms, each already cut); the hall loses between +10 and
  +15, low (0.2-3.5 kHz).
- Slow push holds to about +19 (rig-like) and +11 to +14 (hall), median of five.
- Attack "gentle" against "fast", across everything: half the voice damage, the
  same hold up to +10 in a room like the rig, worse beyond; much worse in the
  hall past +6.
- The fast-riser fixture across builds: the cut lands at -29 dB (`11fe355`),
  -40 (`294bcc8` to `4b1c88c`), -78 (`d55f580`); `fce3790` missed one of the 22
  howls outright.

**Found.**
- Singing alone raises the NOT IN THE LOOP alarm (5.6 s and 25.5 s in).
- A voice does not answer a cut, so the ladder climbs to emergency depth on
  it: 18 of 48 filters deeper than -30 dB on Chris's singing.
- A deep narrow filter near 300 Hz leaves a 23-34 ms ghost of the note.
- The engine holds a quiet filter 10 s; every test held it 2 s. Fixed the
  same day; every figure above is from the corrected tests.
- The remembered ring frequencies are loaded and then erased; the LOAD readout
  shows a number the engine never computes; the chirp probe cannot be started.
- The voice budget buys voice by letting rings through for one to three
  seconds.
- The slow push moves 3-6 dB between runs that differ only in noise.

**Went wrong, and what changed because of it.**
- The plan Chris asked for came an hour late, behind tool work he had not
  asked for first, and he had to ask twice. The plan and this file now move in
  the same commit as the work, and a direct request is delivered before
  anything else continues.
- Three of seven rebuilt past libraries were copies of their neighbour (the
  compile was skipped). Caught by checksum before anything was logged. The
  tool now rebuilds from nothing, stamps each library with its sources and
  refuses a duplicate.
- The first backfill was refused by the log because a file was created in the
  tree while it ran. Rule 9 in TESTING.md.
- A 3 dB "regression" in the newest build turned out to be noise. Rule 8.

**Commits.** `72c9ef3` the plan, this file, the singer test. `81f896a` the results
log and gate layer 6. `f81455d` past builds must prove what they are; `listen.py`.
`1416d5b` first rows; the slow push as a median. `769083c` seven builds on one
ruler; the inventories; the documents made true. `9d17858` the tests hold a
filter as long as the room does. Then the corrected re-measurement of every
build and this entry.

**Where the plan stands at the end of the day.** 73 features: 23 done, 15 in
progress, 31 not started, 4 cancelled (three of them proposals).

**Not done.** The engine's behaviour was not changed (one start-up value moved
into the shared header, same value). Nothing was run in the room: the
engine there is `d55f580`, untouched. The diagnostics were not run (they talk
to the live engine's ports).

## Waiting on Chris

1. **The room, when you are back:** cold jumps at 20 and 22 on `d55f580` (1.2, 2.3); two minutes of singing at low gain for a live voice-change figure (3.2); and the same two minutes with Attack set to Gentle in Setup - the tests say it halves what is taken from the voice at working gains on a rig like yours.
2. **Three proposed cancellations:** 1.7 wide flat feedback, 3.7 voice budget, 4.8 desk-EQ ring-out.
3. **The goals themselves:** 10 ms, 15 ms and 15 % are yours; 3 % with no feedback, the -50 dBFS audible line and "up to 20 dB over" are mine. Change any of them and the thresholds above move with it.
4. **Your recorded singing:** it is on your machine only. Say if any of it may go in the public repository.
5. **A listen** to three rendered examples, so the percentage is tied to your ears (8.11).
