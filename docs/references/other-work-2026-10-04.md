# How others test, and what else exists — notes, 2026-10-04

Written after Chris asked how a singing voice should be put into a feedback
test, and what other algorithms exist. Our own summaries; no paper is
reproduced here. The survey itself is in
[van-waterschoot-moonen-2011.md](van-waterschoot-moonen-2011.md).

## How the voice gets into the test

Everyone who publishes does it the same way, and it is the way Chris said it
had to be: **the source goes inside a simulated loop.** Speech or music is the
microphone's input; the loop is built from a room's measured or simulated
impulse response; the gain is set relative to the point where that loop goes
unstable; the output is compared with the clean source. Nobody mixes the
source over a finished howl.

| Who | Source | Loop | Gain | Scored on |
|---|---|---|---|---|
| The 2011 survey | speech, solo violin | one measured room response | starts 3 dB under the limit, ramps over, holds, then the room changes | gain bought, frequency-weighted distance from the clean source, time howling, time to recover |
| Sparsity-measure detector (2025) | 30 speech files, 28 music (jazz, opera, piano) | 8 measured rooms | ramps from 6 dB under the limit to the limit | precision and recall of the detector, frame by frame |
| Neural suppressors (2023) | speech | 10,000 simulated rooms | 0 to 10 dB of amplifier gain | speech-quality scores against the clean voice, with the output fed back round the loop at test time |

`engine/tests/sim/quality.py` follows the survey's recipe: source in the loop,
clean reference, the four scores, a held gain and a moving microphone. Two
things are ours: the fader sits after the guard (as on the rig), and the
quality score is given as a percentage of the voice's loudness rather than
only in decibels.

## Other algorithms

**Other detectors for a notch system** (the family we are in)

- *Sparsity over time (NINOS2-T), 2025.* Looks at how constant each frequency
  has been over the last several frames; feedback is a steady line. Reported as
  the most reliable of the features compared, and earlier. Code and a test set
  are public. Its own figures show music is far harder than speech, and its
  test has no singing.
  <https://link.springer.com/article/10.1186/s13636-025-00399-1>
- *The survey's six features, compared (2010).* The pair found best - slope of
  the level over time, and how far a peak stands above its neighbours - is the
  pair this engine's fast path ended up needing.
  <https://www.researchgate.net/publication/279481999>
- *A temporal detector (2022)* and *the magnitude-slope tests on rock music
  (2016)*: not yet read in full. The 2016 result, as summarised, is the one
  that matters to us: the slope feature is accurate on speech and classical
  music and poor on rock.

**Cancellation instead of notching**

Model the path from speaker to microphone with an adaptive filter and subtract
it. Nothing is carved out of the voice. The survey credits this family with
the most headroom. Its difficulty is that the speaker's signal and the
singer's are the same signal a moment apart, which biases the model; the
standard cure is a prediction-error prefilter.

- *MuTap* - open source (MIT), C++20, header-only: a frequency-domain
  prediction-error canceller with its own closed-loop simulator, reporting
  about 9-10 dB of added stable gain. One author, early. A reference to study
  and to test in our simulator, not a part to drop in.
  <https://github.com/tap/MuTap>

**Neural suppression**

- *Deep AHS* and *Hybrid AHS* (2023): a network trained to separate the voice
  from its own feedback, alone or behind a Kalman filter. Speech only, 16 kHz,
  no code released. Not usable for live singing as published.
  <https://arxiv.org/abs/2305.02583>

**Frequency shifting** - a few hertz of shift buys about 6 dB on speech and is
audible on any sustained note. Never for singing.

## What we take from it

1. The test method (done: `quality.py`).
2. Run the 2025 detector's feature over our own recordings, beside ours
   (plan 1.6).
3. Try a canceller as a stage in the simulator before deciding anything
   (plan 3.6), which needs the engine to be a chain of switchable stages
   (plan epic 6).
4. Their public test sets could give us singing-free but varied material. They
   have not been downloaded; that needs Chris's say-so.
