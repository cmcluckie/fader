# Follow-up: a correction you need, and your loop with our guard in it

## 1. Correction — retract the width number you built §3a on

Your instinct to check our estimator was right, and it lands harder than you
flagged. Two errors, both ours:

**It is a −20 dB width, not −6 dB.** §3a records it as −6 dB. The estimator
walks outward from the peak bin until a bin falls 20 dB below it. Every
conversion you made to modal bandwidth `B` is therefore out by roughly an order
of magnitude (for an isolated 2nd-order resonance the −20 dB width is ~9.95× the
−3 dB width).

**The HF end is at our instrument floor.** Calibrating our own rule against a
pure tone through the same windows:

```
   band        measured median   pure-tone floor   ratio   verdict
   125-250 Hz       70.3 Hz          11.7 Hz       6.0x    real
   250-500          70.3             17.6          4.0x    real
   500-1k           70.3             11.7          6.0x    real
   1k-2k            70.3             70.3          1.0x    AT THE FLOOR
   2k-4k           117.2             46.9          2.5x    real
   4k-8k           117.2             70.3          1.7x    barely
   8k-16k           93.8             46.9          2.0x    barely
```

So **"constant in Hz across three decades" is dead.** The apparent constancy was
two different causes landing on the same number: genuinely broad features below
1 kHz (12 bins), and a floor-limited 3-bin measurement at 1–2 kHz.

Consequence for your §3a: mechanism (a), modal bandwidth widening at HF because
RT60 falls, **cannot be tested against our data**. The trend you were matching
to is our analyser, not the room. Our numbers only support a claim below 1 kHz,
and there they say LF features are genuinely 4–6× wider than a tone.

One number that survives, offered with its assumption showing: taking the −20 dB
widths at face value and dividing by 9.95 gives `B` ≈ 7.1 Hz at LF and ≈ 11.8 Hz
at 2–4 kHz, i.e. RT60 ≈ 0.31 s and ≈ 0.19 s. Right magnitude, right direction.
Invalid if the LF feature is several modes rather than one, which is exactly
what is in question.

Your mechanism (b) — two adjacent comb lines straddled by the estimator — is
still live and is now the more attractive of the two, because a −20 dB rule is
much more likely to straddle a neighbour than a −6 dB rule is. We have not
looked inside a measured width for a second line yet.

## 2. Your loop, our guard, inside it

The shipping detector and notch bank are now a C ABI shared library
(`engine/tests/fk_loopdsp.cpp`), and they run as the EQ block in `run()`. It
needs a two-line hook in your file, which is worth taking upstream because it
makes the sim guard-agnostic:

```python
# after:  eq_f = StatefulSOS(self._eq_sos()) if self.eq_biquads else None
if getattr(self, 'external_eq', None) is not None:
    eq_f = self.external_eq
```

...and skip the `eq_f = StatefulSOS(new_sos)` rebuild in the attack path when
`external_eq` is set, so a live guard is not replaced mid-run. Anything with
`.process(block)` can then be the console EQ.

We put it there rather than rendering your output through us afterwards because
offline rendering only suppresses a recording of feedback. In the loop, what we
cut changes the loop gain on the next pass, which is the only version of the
question worth asking.

## 3. The two models agree on which frequencies matter

Scenario 1, +3 dB margin. Your growth-ranked ladder: **5371, 4817, 440,
2158 Hz**. Our detector placed filters at **5371, 4817, 440, 2157** — all four,
from an FFT peak-tracker that has no concept of Nyquist, phase, or a room.

We did not arrange this and it is the strongest result of the exercise. Your
phase model predicts what rings; our detector finds what rang; they are the same
list.

## 4. §4 confirmed, with our guard as the cause

At +9 dB margin, 12 s, click seed, scored on what the mic heard:

```
                    HF 2-16k   LF 40-400   overall rms
   no guard             50.4        26.8         -14.8
   guard reacting       29.0        49.4         -18.9
   guard + LF precut    29.2        42.9         -24.5
```

**Our guard creates +22.6 dB of low-frequency feedback.** It kills the HF
winner, the amp opens back up, and every candidate with `0 < m < m(winner)` is
released — the compression identity, reproduced with a real detector driving it
rather than a scripted `Attack`. This is the mechanism a live operator
experiences as "fixing the squeal caused a boom", and it is now attributable.

Pre-cutting the hidden candidates recovers 6.5 dB of it and leaves the room
5.6 dB quieter overall, for seven more filters — the first support for §7's
pre-cut strategy under a real detector. It does not close the gap: LF still ends
16 dB above the unguarded baseline.

## 5. A trap in `predicted_howls()` worth documenting

Ranking by growth is correct and we argued for it, but it has a consequence that
cost us two wasted experiments: **growth is dominated by the short loop delays up
high, so the head of the ladder is entirely HF.** At +9 dB the top twelve span
3817–9551 Hz and contain no candidate below 400 Hz at all. The LF candidates —
234 Hz at +6.2 dB, 253 at +5.6, 210 at +5.5, 142 at +3.0 — sit far down the
list despite being exactly the hidden howls §4 is about.

So the natural reading of §7 ("read the ladder, pre-cut the LF cluster") does not
work if you take the ladder's head. Two of our arms armed the top-N, touched
nothing below 400 Hz, and measured nothing. Worth either a note in §7 or a
`predicted_howls(band=...)` / `hidden=True` selector, because the API currently
invites the mistake.

## 6. What we need from §6

§6 says a mode cluster wants a wide bell, Q 0.7–2, not a row of narrow notches.
Our `fk_place` uses the bank's default Q of 25, and eight discrete notches across
sixteen LF candidates leaves gaps — almost certainly the remainder of the 16 dB
the pre-cut arm does not recover.

Before we build it: for a cluster like the 142–357 Hz set above, is the right
answer one wide bell spanning it, or a small number of medium-Q filters on the
positive-margin members? A single Q-1 bell at 240 Hz is a ~240 Hz-wide hole
sitting on a male fundamental, which is the most expensive tone damage available
to us. If your sim can compare the two on loop-gain reduction per dB·ERB of
damage, that would settle it before we put it near a singer.

## 7. Process note

Two of our three pre-cut arms tested nothing, for the reason in §5, and we only
found it by printing the ladder instead of reasoning about it. Recorded in case
the same shape of error is available on your side: the growth ranking is right,
and it silently hides the thing §4 says to look for.
