# The suppression toolbox

What can be done about feedback, what each tool is good against, and what it
costs the singer. Kept here because the choice of weapon matters more than the
tuning of any one of them, and because half this table is still prediction.

**Read the Evidence column first.** Four rows are measured on our own rings with
`fk-fuzz`; the rest are from the literature and the loop equations, and are
marked so. A predicted row is a hypothesis with a citation, not a result.

## The two shapes

Feedback arrives in one of two shapes, and they are the same mechanism either
side of the Schroeder frequency, `f_c = 2000 sqrt(RT60 / V)`:

| | Below f_c | Above f_c |
|---|---|---|
| Modal overlap | < 1, modes stand alone | > 1, modes crowd |
| Shape | **spike**, narrow, triangular | **plateau**, wide, flat |
| Excess gain | one mode takes it all, 0.3-6 dB | shared out, 0.05-0.6 dB each |
| Speed | fast: `20 log10|GF| / tau` | slow, for the same reason |

That is why the wide ones are the slow ones: energy shared between many modes
is energy that makes each of them rise slowly. It also says a plateau is the
*cheaper* target - every one of its modes is barely over unity - which is the
opposite of how we have been treating it.

## The tools

| Tool | Fast spike | Slow spike | Plateau | Cost to audio | Status | Evidence |
|---|---|---|---|---|---|---|
| Narrow notch | Excellent | Good once seen | Poor - needs dozens | High at LF, low at HF | Shipping | **Measured**: 93% killed, harm 50 dB-ERB |
| Comb of notches | Wasteful | Wasteful | Good in principle | Moderate | Built, plateau-only | **Measured**: cost room 4 six filters for nothing |
| Pulsed notches | Good | Good | Good | ~4x cheaper | Measured, not in engine | **Measured**: harm 45.6 -> 11.4 for +4 pts runaway |
| Harm budget | Allocates | Allocates | Allocates | Sets the ceiling | Built | **Measured**: budget 60 is the knee |
| Frequency shift (~5 Hz) | Modest | Modest | Excellent | Slight pitch artefact | Not built | *Predicted*: 5-6 dB, standard result since Schroeder 1964 |
| Phase/delay modulation | Modest | Modest | Excellent | Mild chorusing | Not built | *Predicted*: decorrelation, 3-6 dB |
| Adaptive cancellation | Very good | Very good | Very good | None if it converges | Not built | *Predicted*: 10 dB+; we do have the reference signal |
| Broadband trim (-1 dB) | Useless | Useless | Excellent | Almost nothing | Not built | *Predicted from our own generator*: plateau modes sit 0.05-0.6 dB over unity |
| Limiting | Protection only | Protection only | Protection only | Pumping | N/A | Rule 3: limiters are protection, not suppression |
| Acoustics / mic pattern | Good | Good | Best available | None | The operator's | Always true, rarely convenient |

## The rule

Let as much of the singer through as possible. Every dB removed must justify
itself, and the justification has to be a measurement.

Only a **gain** control can promise zero feedback - feedback is loop gain over
unity, so turning the monitor down far enough silences it by definition.
Everything else buys *headroom*, measured in dB of extra gain before it rings.
That gives every tool one currency: **dB of headroom bought, harm paid**.

Harm is not dB of cut. The ear charges for spectral *area*, weighted by where
it hears best:

    harm = depth x (notch width / ERB at that frequency) x importance(f)
    ERB(f) = 24.7 (0.00437 f + 1)

A 100 Hz notch spans 1.4 ear-bandwidths at 300 Hz and 0.12 at 8 kHz. The same
filter is expensive in one place and nearly free in the other, which is why a
constant Q was never the right control.

## The uncomfortable number

At maximum damage - no budget, continuous, everything the engine has - about
half of all rings still get away (`fk-fuzz`, four seeds). The harm is not what
is protecting the room. That is the strongest argument for the rule above: most
of it can be given back for very little.
