# Fifty Years of Acoustic Feedback Control — reference note

**T. van Waterschoot and M. Moonen**, "Fifty Years of Acoustic Feedback Control:
State of the Art and Future Challenges," *Proceedings of the IEEE*, vol. 99,
no. 2, pp. 288–327, February 2011. DOI 10.1109/JPROC.2010.2090998.

The authors' own technical-report version (K.U.Leuven ESAT-SISTA TR 08-13) is
published by them at `ftp.esat.kuleuven.be/pub/sista/vanwaterschoot/reports/08-13.pdf`.

The paper is copyrighted and is **not** reproduced in this repository. Keep a
copy as `docs/references/08-13.pdf` if you want it to hand; PDFs in this folder
are git-ignored. What follows is a summary written for this project, in our own
words, of the parts that bear on the engine.

## What the paper is

A survey of automatic feedback control from 1958 to 2010, plus a simulated
comparison of the three families in common use. It sorts everything into four
groups:

| family | idea | best case the paper credits it with |
|---|---|---|
| phase modulation / frequency shifting | move the loop phase so no frequency stays in phase from one trip to the next | about 6 dB usable; fine for speech, poor for sustained musical tones |
| gain reduction (broadband AGC, sub-band EQ, **notch filters**) | pull the loop gain down where it is at or near unity | bounded by how peaky the room is — see below |
| spatial filtering | steer microphone or loudspeaker arrays away from each other | 6–15 dB, with geometry constraints |
| room modelling (**adaptive feedback cancellation**, inverse filtering) | estimate the loudspeaker→microphone path and subtract it | 15–20 dB reported; about 9 dB average in their own comparison |

This engine is in the third row: two-stage notch-filter howling suppression
(detect, then notch).

## The parts that matter to us

**1. The stability condition, and the unit of merit.** Howling needs loop gain
of one or more *and* loop phase a whole number of turns, at the same frequency.
The figure of merit is **maximum stable gain** (MSG) and the increase a method
buys. They recommend running 2–3 dB under it to avoid audible ringing. Our
"added stable gain" from `ringout.py` is the same quantity; we should report it
as MSG increase and park the gain with that margin after a ring-out.

**2. A ceiling on what notches alone can do.** In a reverberant room the loop
response is a forest of peaks roughly 10 dB above its own average, a few hertz
apart. A method that only flattens peaks can therefore win at most about 10 dB
before it is attenuating everything; by hand, engineers get 5–8. In the
paper's example room, 10 dB past the unaided limit puts more than twenty
frequencies over unity at once, and notching them all amounts to a broadband
cut.

*This applies to us and should temper tonight's number.* The ramp on
2026-10-03 held to 22 dB over the ring point — but with 14 to 40 filters active
and the 4–16 kHz average down 6.6 dB at the top. Part of that 22 dB is
narrow notching; part is an HF shelf built out of notches. Our rig is also not
their diffuse room: a microphone near a loudspeaker has a loop dominated by a
short delay and one strong resonance region, which is why a few teeth of a comb
stand so far above the rest. Any headline figure from the sweep must be quoted
together with the tone cost at that gain.

**3. How to tell a howl from music.** The paper catalogues six features. The
left column is theirs; the right is what this engine already does, most of it
arrived at by measurement before we read this.

| their feature | meaning | here |
|---|---|---|
| peak-to-threshold | loud enough to matter | `actionDb` |
| peak-to-average | the peak is most of the signal | the runaway path's "is the signal" test |
| peak-to-harmonic | a howl has no harmonics, a note does | harmonic guard, family test |
| peak-to-neighbour ("peakness") | stands far above nearby bins | prominence; 20 dB for the runaway path |
| inter-frame persistence | still there several frames later | `persistFrames`, sustain path |
| inter-frame slope deviation ("slopeness") | level rises as a straight line in dB | growth gate; the runaway path's line fit |

Their comparison found the slope-plus-peakness combination the most reliable,
and the only one usable on music. That is the pair the runaway path ended up
needing. Their definitions are worth borrowing directly: slope judged over
seven frames, peakness averaged over six neighbours each side skipping the
nearest, and a weighted sum of the two.

**4. Notches: width, depth, release.** Published designs use 1/10 to 1/60
octave notches, open shallow (−3 or −6 dB) and deepen on recurrence, with some
filters fixed at ring-out and the rest free. The paper notes that almost
nothing has been published on when to *release* a notch — which matches where
most of our own trouble has been (hold, bleed, let-go).

**5. Reactive versus proactive.** Most notch systems hear the howl before they
stop it. The promising direction is proactive: estimate the loop response and
notch the frequencies that are about to qualify. We have the beginnings of this
(the loop-delay probe; the delay comb measured on 2026-10-03 at about 100 Hz
spacing, i.e. a 10 ms loop). A ring-out that measures the loop and pre-places
fixed filters is the paper's "initialisation", and is what "auto mode for a new
room" should be.

**6. A broadband rescue.** Many systems keep a plain gain reduction as the
last resort: if the loop is unstable and nothing else has stopped it, turn it
down. This engine has no such thing — its output gain is fixed — and the
sweep tool's kill switch has been standing in for it. On a cold jump to 20 dB
over the ring point a ring climbs 700 dB/s; narrow filters race it and
sometimes lose by tens of decibels. A brief broadband duck is the textbook
answer and the obvious next addition.

**7. Cancellation is where the extra headroom is.** Modelling the feedback
path and subtracting it removes the loop instead of trimming it, and is the
only family the paper credits with 15–20 dB. It costs a long adaptive filter
and needs the source and loudspeaker signals decorrelated, and it is still
recommended to keep notch suppression underneath it as the safety net. If the
notch ceiling in (2) turns out to bind in practice, this is the next
architecture, not more notches.

**8. Measures worth adopting.** Besides MSG increase, the paper scores
*sound quality* (a frequency-weighted spectral distortion between source and
output) and *reliability* (fraction of time spent howling; mean time to recover
from instability). Our gate measures detection lead and cut depth; it does not
yet score time-spent-howling or recovery time, and its voice measure looks at
one frequency per fixture. Both are gaps.

## How their test compares with ours

They raise the gain past the unaided limit by 3 dB (phase modulation), 5 dB
(notches) and 10 dB (cancellation) and watch what happens, on one measured
room response, with speech and with solo violin. Our sweep climbs in half-dB
steps on the real rig and also does cold jumps. The cold jump has no
counterpart in the paper and is harsher than anything it evaluates.
