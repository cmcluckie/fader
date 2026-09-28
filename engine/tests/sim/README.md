# Closed-loop tests against a real room model

`fk-fuzz` generates rings and grows them. It has no phase condition, so it can
never say *which* frequency will ring; no power amp, so a spike at 5 kHz cannot
suppress a mode at 77 Hz; and its modes grow independently of one another.
Those are the three mechanisms that decide what a room actually does.

`feedback_sim.py` (Chris's theory work, separate repo) has all three: an
image-source impulse response, mic and speaker directivity, a saturating amp,
and a sample-exact closed loop.

These scripts put **the shipping guard inside that loop**, via the
`fk-loopdsp` shared library (`engine/tests/fk_loopdsp.cpp`). The guard sits
where the console EQ sits - after the preamp, before the power amp - so what it
cuts changes the loop gain on the next pass. Rendering the sim's output through
the guard afterwards would prove nothing: that suppresses a *recording* of
feedback rather than preventing it.

## Running

Needs `pyroomacoustics`, `scipy`, `soundfile`, and a copy of `feedback_sim.py`
on the path.

    cmake --build build --target fk-loopdsp
    python3 fk_in_loop.py      # guard off vs guard on, in the loop
    python3 precut_test.py     # does predicting the ladder beat reacting?

## What these found

**The two models agree on which frequencies matter.** The sim's phase-aligned
ladder predicted 5371, 4817, 440, 2158 Hz; our detector - which knows nothing
about Nyquist, phase, or the room - placed filters on all four. Two independent
models, same answer.

**Our guard creates low-frequency feedback.** At +9 dB gain margin, measured on
what the microphone heard:

```
                HF 2-16k   LF 40-400   overall rms
no guard            50.4        26.8         -14.8
guard reacting      29.0        49.4         -18.9    <- +22.6 dB of LF, ours
guard + LF precut   29.2        42.9         -24.5
```

Killing the HF spike opens the power amp back up, and every candidate with
`0 < margin < margin(winner)` is released. That is the compression identity from
FEEDBACK_THEORY §4, reproduced here with our own DSP causing it.

Pre-cutting those hidden candidates recovers 6.5 dB of it and leaves the room
5.6 dB quieter overall, at the cost of seven more filters.

**A trap worth recording.** The ladder is ranked by growth rate, and growth is
dominated by the short loop delays up high - so its first dozen entries are all
HF and contain no LF candidate at all. Two earlier versions of the pre-cut arm
selected from the head of that list, armed nothing below 400 Hz, and tested
nothing. The hidden howls have to be searched for in the full ladder.

## Still open

FEEDBACK_THEORY §6 says a mode cluster wants a **wide** bell (Q 0.7-2), not a
row of narrow notches. `fk_place` currently uses the bank's default Q of 25,
which is far too narrow for that, and eight discrete notches across sixteen LF
candidates leaves gaps. That is the likely remainder of the 16 dB the pre-cut
arm still does not recover.
