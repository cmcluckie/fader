# Review of `feedback_sim.py` and the working notes

From the Feedback Fader engine side. We have a second simulator (`fk-fuzz`, a
physics-generated ring rig in C++) and about 51,500 real detections logged off a
live rig, so this is three independent sources checking each other rather than
one model checking itself.

Everything below is either a run of your own code or a measurement from the real
logs. Where we were wrong, that is said too — two of our claims died during this
review and you should not inherit them.

---

## 1. The headline: your code does not implement your §2

§2 says, correctly and importantly:

> **Not the one with the most margin. The one that *grows fastest*.**

`predicted_howls()` then sorts by margin:

```python
cands.sort(key=lambda x: -x[1])      # x[1] is db(|H|)
```

and `set_gain_margin()` calls `c[0]` "the worst-case loop frequency", which is
the largest-margin candidate, not the fastest-growing one.

Run on your own scenario 1 (9x7x3.2 m, RT60 0.7, margin +3.0), computing loop
group delay as `tau_g = -d(unwrap(phase))/d(omega)` and growth as `margin/tau_g`:

```
        margin   loop tau   growth
4066 Hz  +1.24 dB   52.7 ms   23.5 dB/s
2158 Hz  +2.02 dB   95.5 ms   21.2 dB/s
```

A candidate with **0.78 dB less margin grows faster**, which is precisely the
effect §2 describes in prose. The top winner happens to coincide in this room,
so the sim's headline result is unaffected — but the **migration order** differs,
and the migration order is the whole basis of §7. The ladder you print as "what
rings next" is currently sorted by the wrong key.

Fix is one line, and the group delay is already available from `H`.

## 2. §2's loop period is wrong by roughly 7x

> "the loop period is the acoustic delay plus the room's ring time at that frequency"

Ring time is not the right term. A resonance contributes **group delay**, not
decay time:

```
tau_resonance ~ 1/(pi*B),   B = 2.2/RT60   =>   tau ~ RT60/(2.2*pi) ~ 0.145*RT60
```

At RT60 = 0.7 s that predicts ~101 ms. Measured across the candidates in your own
sim: **52–111 ms**. The ring-time reading would give ~700 ms, i.e. growth rates
too slow by about seven.

Your field numbers support the correction, not the original. 77 Hz at +1.0 dB
reaching audibility (~30 dB) in 4 s implies tau ~ 133 ms, hence RT60 ~ 0.9 s at
77 Hz — entirely plausible. Under "delay + ring time" the same observation would
require RT60 ~ 0.13 s at 77 Hz, which no real room does.

Note also that the measured group delays (52–111 ms) dwarf the direct acoustic
delay (4 m => 12 ms). The loop period is dominated by the resonances, not the
path. That is worth stating explicitly in §2, because it is what actually makes
LF slow and HF fast, and it is a cleaner mechanism than the one in the text.

## 3. §7's ladder is internally inconsistent

```
  77 Hz  +1.0 dB   <- will ring ~4 s after 5744 dies
 183 Hz  +0.0 dB   <- will ring ~7 s after 77 dies
```

At **+0.0 dB the growth rate is exactly zero**. That candidate never reaches
audibility, on any timescale, under your own growth law. It can only ring if
killing 77 *changes its margin* — which is §4 — so the table is being read as a
forecast while being constructed as a static snapshot. Either the margins need
to be recomputed per rung, or the table needs to say it is only valid in the
quiet state.

## 4. §4 cannot be expressed by the ladder at all

`loop_response()` is documented as "linear part, no clipper", and
`predicted_howls()` is built on it. But the gain-sharing mechanism of §4 lives
**entirely in the clipper**. So every margin in the ladder is a small-signal
value, valid only when nothing is howling — which is exactly the condition you
are not in at the moment you want to read it.

This is the most valuable thing in the notes and it is currently asserted beside
the model rather than produced by it. Suggested fix: scale the loop by the
**describing-function gain** of the `tanh` at the current operating level:

```
for a sine of amplitude A into tanh(x/h)*h, the effective small-signal
gain is the first harmonic / A, which falls as A/h grows
```

Then compression tracks level, sub-unity candidates cross unity as the spike is
removed, and §4 falls out of the model instead of being narrated. That also gives
you a principled version of §7: pre-cut targets are the candidates that cross
unity *once the current howl is gone*, which is a different and smaller set than
"everything above -3 dB".

Related: `min_db=-3.0` discards exactly the migration targets §4 says matter. If
the amp opens up by 10 dB when the spike dies, a candidate at -8 dB becomes +2.

## 5. §3: the "narrow because high-Q" mechanism looks wrong

Above the Schroeder frequency the transfer function is a random field whose peak
width is set by the modal bandwidth `B = 2.2/RT60`, not by any individual
high-Q resonance. RT60 is *shorter* at HF (air absorption), so B is *larger*, so
HF peaks should be **wider in absolute Hz** than LF ones.

Our real logs agree. Median measured peak width per band, 51,517 detections:

```
   20-125 Hz     46.9 Hz        1.0-2.0 kHz    70.3 Hz
  125-250 Hz     70.3 Hz        2.0-4.0 kHz   117.2 Hz
  250-500 Hz     70.3 Hz        4.0-8.0 kHz   117.2 Hz
  500-1k  Hz     70.3 Hz        8k-20k  Hz     93.8 Hz
```

(Analysis resolution is 5.9 Hz below 1 kHz and 23.4 Hz above, so the low bands
are resolved, not floor-limited — minimum observed width below 1 kHz is 5.9 Hz.)

Width is roughly **constant in Hz across three decades**. The spike/plateau
impression is therefore a *relative* width effect: the same ~70–120 Hz feature is
a third of an octave at 200 Hz and a hundredth of an octave at 9 kHz. That is a
simpler and more defensible explanation than "HF resonances are high-Q", and it
also explains why the classification threshold has to be relative — which matches
the ear, since critical bandwidth scales with frequency.

**Open question neither of us has tested.** You attribute the apparent LF width
to amp saturation smearing a narrow mode. We measured it as genuinely broad. But
a third candidate exists that would explain both: a howl is a *growing* sinusoid,
and its apparent linewidth in any finite analysis window is set by the growth
rate and the window length, not by the room peak it is riding. A clean test is
cheap — synthesise an exponentially growing pure sine at 200 Hz and at 9 kHz,
push it through the analyser, and see what width comes back. If a clean growing
sine reports ~70 Hz at 200 Hz, then neither "room is broad" nor "clipping smears"
is needed. We have not run it yet.

## 6. Smaller things

- `jitter_path()` draws an independent uniform offset per waypoint. That is white
  noise, not a walk — a singer's position is strongly correlated second to second.
  An Ornstein-Uhlenbeck process (or any low-passed walk) would give realistic
  excursion statistics; white jitter both over-samples large jumps and never
  drifts. We hit exactly this in our own rig and changing it mattered.
- Crossfading RIRs gives no Doppler. Small (0.3 m/s at 5 kHz is ~4 Hz) but it is
  the one shift that is genuinely *proportional* to frequency, so it is worth
  knowing it is absent before attributing proportional drift to geometry.
- `CardioidFamily` in pyroomacoustics is frequency-independent. Real vocal mics
  are close to omni at LF and much tighter at HF. Since §3 and §6 both make claims
  about LF control, and LF omni behaviour is the reason mic aiming does not fix LF
  feedback, this gap sits directly under a conclusion.
- §10 already flags it, but to reinforce: the cluster rule cannot be a percentage.
  77 -> 183 is 138% apart and is one cluster; 9293 -> 10006 is 7.7% apart and is
  (probably) a different mechanism. The LF relation is modal, the HF relation is
  not, and one number will not serve both.

## 7. What the real rig contributes

A measured hop ladder, from `capture-log-20260927-180044.csv`, 71 catches:

```
9293 Hz -> 10006 Hz -> 10716 Hz      (+713 Hz, +710 Hz)
```

Every single one was **placed** — zero refusals — and at 10716 the existing notch
was putting only **-1.6 dB** on the new frequency. So this is not a coverage
failure or a "we stopped looking" failure. Each hop simply started the escalation
ladder again from the bottom while the ring carried over everything it had built.

Tempting reading: the two steps are equal in Hz (713, 710) but not in percent
(7.67%, 7.09%), which would mean a *stationary* candidate comb of spacing ~711 Hz
(=> a 48 cm path) rather than §3's moving singer, since geometry change predicts
constant *percentage*.

**We do not believe this, and it is offered only as a hypothesis.** Across 229
upward hops in the full log set, the relative spread of `delta Hz` is 79% of its
mean and of `delta %` is 87% — statistically indistinguishable, so neither
quantity is conserved. The sample is also contaminated (the same transition
recurs up to five times). It would need a clean per-ladder analysis to settle,
and if it did hold it would be very actionable, because the next rung would be
predictable.

## 8. The §4 test we ran, and why it failed

§4 makes a sharp prediction: kill an HF spike, and LF should rise seconds later.
Tested on a 228 s live capture — 20 HF collapses (>20 dB drop within 1 s from a
loud ring), measuring 40–400 Hz energy 5 s after each:

```
mean -1.3 dB, median -0.7 dB, scatter +-30 dB
```

Null. But we do not trust it: during a vocal session the 40–400 Hz band *is the
singer*, and nothing in that measurement separates "a room mode got its gain
back" from "they sang a low note". The confound is total.

The clean experiment is a **silent ring-out**: gain up, mic live, nobody singing,
let it howl, kill the spike, watch the LF for ten seconds. We will run it. If
your sim can produce the predicted LF rise magnitude and delay for a given
headroom setting, that is a falsifiable number to check it against, and it would
be the first genuine cross-validation between the two models.

## 9. What we are taking from you

The phase condition. Our rig has no `angle(H) = 0` anywhere — it draws ring
centres at random and grows them exponentially — which is why it can reproduce
*that* feedback happens but has never been able to say *which frequency*. We are
porting `predicted_howls()`, ranked by growth rate rather than margin, and with
compression-aware margins per §4 above.

That is also what makes §7 possible at all: pre-cutting a predicted ladder
instead of reacting to what is already audible. Our shipping engine is purely
reactive, and the measured consequence is section 7's hop ladder — three fights
started from scratch, all of them lost.

## 10. Two claims of ours that died during this review

Stated so they do not propagate:

- **"Upward phase crossings are being missed."** Void. We counted them: zero
  above -3 dB in your scenario 1. Loop delay makes the phase monotonic, so the
  downward-only detector is correct.
- **"Hops are constant in Hz, so there is a fixed candidate comb."** Built on two
  data points from one capture. Died on 229 (see §7).

We also shipped an emergency escalation this morning on the theory that the ring
was out-gunning the notch — measured afterwards at no benefit (788 -> 793
runaways across 14 seeds, HF spikes 68% runaway either way). The arithmetic says
why: a spike carrying ~8 dB excess over a 12 ms loop grows at ~650 dB/s, so it is
lost to detection *latency*, not to insufficient depth. Worth knowing before
anyone reasons about attack depth from our side.
