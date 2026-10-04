# The gate was testing a machine the room had never heard — 3 October 2026

What the series session recorded, what it meant, and what changed. Everything
here is measured; the tools that measured it are in the repo.

## What the recording said

The 118-second series recording (`audio-20261003-151246.wav`) has two channels:
the microphone before the notches and after them. The difference between them at
any frequency is the cut the guard actually applied there — not what it logged,
not what the app estimated. `timeline.py` reconstructs every howl from that.

Twenty-one howls. Medians:

| | |
|---|---|
| ring starts growing → detector fires | 0.35 s |
| → the bank logs a filter "placed" | 0.56 s |
| → **6 dB of cut actually on the ring's frequency** | **3.1 s** |
| ring peak | −27.8 dB |
| ring dies after the cut arrives | ~0.3 s |

Detection was never the problem. The cut not landing was. Every ring died the
moment a real cut reached it; the three seconds were spent cutting somewhere else.

## Why the cut landed somewhere else

Two causes, about half the howls each.

**1. The harm budget.** `region-too-dark` refusals on −26 dB howls for three
seconds at a time: the budget (30) was full, the new hold kept every filter
fresh, so "retire the stalest" found nothing. Fixed in the previous commit (a
loud ring overrides a full budget; the rig's budget is 0 now).

**2. The merge window.** A 9399 Hz ring, "placed" a hundred times in two
seconds, with 2.4 dB on it. The trigger was being merged onto a filter at 9780
Hz — 3.9% away — which slid 50 Hz, hit its leash, narrowed to Q 80 and
deepened to −45 dB where the ring was not. The app estimated 10 dB of cut there
(it assumes a width); the recording shows 2.4.

The merge window is `f / 2·defaultQ`. `AudioEngine` overwrites `defaultQ` every
block with `notchQ`, which is **12** and which nothing in the app has ever set.
So live, the window was ±4.2%, for filters that open at Q 20 (±2.5%) and narrow
to Q 80 (±0.6%).

## Why no test ever saw it

Every harness — unit tests, fuzz, closed-loop sim, real-recording replay — used
`NotchBank` and `FeedbackDetector` directly, with *their* defaults. The engine
sets different ones every block. Side by side:

| | harness | rig |
|---|---|---|
| merge window Q | 25 | **12** |
| first strike | −12 | **−18** (Attack 2) |
| soft / hard cap | −18 / −24 | **−12 / −18** |
| prominence | 10 | **12** |
| input gate | −90 | **−55** |
| confirm frames | 6 | **4** (Attack 2) |

Run with the rig's numbers, the closed-loop gate **fails the old code
outright**: 0.2 dB of HF suppression at 9 dB of margin, 250 detections, 35
filters, the ring wins. That is the room. The gate had been passing a machine
the room never ran.

## What changed

1. **Merge rule.** A trigger merges onto a filter only if the tone is inside
   that filter's bandwidth now. T52 reproduces the rig to the decibel
   (neighbour dragged to 9719 Hz, ring merged, 2.0 dB of cut) and fails on the
   old rule. Replaying today's recording: 261 of 750 triggers merged onto a
   neighbour before, 9 after; the 9342 Hz howl waits 2.26 s for a filter on the
   ring before, 0.12 s after.
2. **One set of defaults** (`EngineDefaults.h`), read by the engine, the test
   library, the fuzz, and the Python harness, which reads the rig's
   `audio.json`. The gate now runs the rig.
3. **Input gate −55 → −90.** In a quiet room with an open mic, nothing was
   analysed until the howl itself opened the gate. On the 17 real howls in the
   fixture set: median catch 213 ms before visible and one never detected at
   −55; **533 ms and 17 of 17** at −90. Voice hits unchanged.
4. **Attack "fast" keeps six confirm frames.** Four bought 10 ms and cost two
   extra hits on the singer's harmonics. The −18 first strike is the part of
   "fast" that works.
5. **Gate baselines re-cut on the rig's configuration.** Against the old ones:
   bass added at 9 dB margin 58 → 4 dB, ring-like residue 99.9% → 46%, HF
   suppression 59.6 → 60.8 dB; real recordings 17/17, bled 4 → 3, voice hits 2.

Cost, stated: on the fuzz's synthetic plateau population (~300 modes a
minute), runaway modes 155 → 169 per six seeds; ten of those return with 96
slots, so it is pool pressure from un-merged modes. The HF spikes — what this
rig actually rings with — score identically under both rules. The pool stays
at 48 until the rig shows it needs more (it has never shown more than nine).

## What was wrong in my earlier reading

"First strike −12 and the 6 dB ladder are too slow" — withdrawn. The rig's
first strike was −18, at the dial, from the first frame. The rings got loud
because the filter was on the wrong frequency or refused, not because it was
shallow.

## Next: the sweep

`scripts/ringout.py` raises one fader in half-decibel steps with a kill switch
on the microphone and everything restored on exit. Guard bypassed, it finds
where the room rings on its own and backs off before it is loud. Guard on, it
logs every ring the guard catches and climbs until the guard loses. The
difference is the added stable gain — the figure of merit this rig has never
had. It runs when someone is in the room.
