"""
replay_gate.py — the ship gate, shown real feedback.

Runs the shipping detector and bank over every fixture cut from the flight
recordings (make_fixtures.py) and refuses the build if it does worse than the
build that made the fixtures. Until this file the gate had never once seen
actual feedback; every layer ran on signals we synthesised ourselves.

For a howl:   it must be detected inside its window, cut to at least -10 dB,
              and not bleed away while still howling.
For a voice:  a needle that was the singer's own harmonic must be left alone -
              a cut deeper than -6 dB on it is a hit.

Like preship.py this is a REGRESSION gate: bleed and voice-hit counts are
compared to a recorded baseline, not to zero, because two howls hand over to a
neighbour at the very end and two vocal harmonics were being clipped on the
day the fixtures were cut. Raise the bar when those are fixed; never lower it.

    python3 replay_gate.py            # gate
    python3 replay_gate.py --update   # re-record the baseline after a real change
"""
import json, os, sys, wave
import numpy as np

HERE = os.path.dirname(os.path.abspath(__file__)); sys.path.insert(0, HERE)
from replay import Replay, HOWL_LEVEL

FIX = os.path.join(HERE, "fixtures")
BASE = os.path.join(FIX, "gate-baseline.json")
# Both bars hang off the operator's dial, read from the rig like everything else.
#   A real howl must get at least the dial on its own frequency (within 2 dB):
#     the old merge rule gave 9896 Hz 12.5 dB and 9878 Hz 7.2, from a filter
#     beside the ring, and "> -10" was too kind to notice the first.
#   And it is LET GO if, still loud, the cut ends shallower than the dial by
#     3 dB AND 3 dB up from where it had been - a ring that only ever needed
#     the dial and reads 2 dB either side of it has not been let go of.
from fk_in_loop import rig_config
DIAL_DB   = rig_config()["max_cut_db"]
MIN_CUT   = DIAL_DB + 2.0
LET_GO_DB = DIAL_DB + 3.0


def load16(path):
    w = wave.open(path); n = w.getnframes(); sr = w.getframerate()
    x = np.frombuffer(w.readframes(n), dtype="<i2").astype(np.float64) / 32768.0
    return x, sr


def check(fx):
    x, sr = load16(os.path.join(FIX, fx["file"]))
    r = Replay(sr); r.run(x)
    hz = fx["hz"]; near = lambda q: abs(q - hz) / hz <= 0.03
    ev   = [e for e in r.events  if near(e[1]) and fx["onset"] - 0.6 <= e[0] <= fx["end"]]
    wins = [w for w in r.windows if near(w[1]) and fx["onset"] - 0.3 <= w[0] <= fx["end"] + 0.3]
    deepest = min((w[4] for w in wins), default=0.0)
    # "Bled" means the cut fell away while the howl was STILL LOUD. Sampling
    # after the needle has faded scores a clean hand-over to the next ring as
    # a bleed - five of them on the first run against two in the full replay.
    loud    = [w for w in wins if w[2] >= HOWL_LEVEL]
    last    = loud[-1][4] if loud else (wins[-1][4] if wins else 0.0)
    first   = min((e[0] for e in ev), default=None)
    # Two different things used to be one number.
    #
    # "relaxed": the cut rose more than 8 dB from its deepest while the howl was
    #   loud. That was a fair proxy for "let go" when one filter held one ring.
    #   Since the bank stopped merging a ring onto a neighbour it cannot reach, a
    #   cluster is held by several narrow filters, and their SUM moves ten
    #   decibels as they hand over - 2026-10-03: six fixtures "bled" from -50 to
    #   -39, -43 to -34, every one still carrying more than the dial.
    #
    # "let go": while the howl was still loud, the cut on it ended shallower
    #   than the dial (LET_GO_DB). That is the failure the number was for - the
    #   09-30 hold bug ended at -5 - and it is the one that is gated.
    return dict(detected=bool(ev), latency=(first - fx["onset"]) if first is not None else None,
                deepest=deepest, last=last, bled=(deepest <= -12.0 and last > deepest + 8.0),
                let_go=(deepest <= -12.0 and last > LET_GO_DB and last > deepest + 3.0),
                hit=(deepest < -6.0))


if __name__ == "__main__":
    update = "--update" in sys.argv
    if not os.path.exists(os.path.join(FIX, "manifest.json")):
        print("SKIP-FAIL  no fixtures: run make_fixtures.py on a machine with the recordings"); sys.exit(2)
    manifest = json.load(open(os.path.join(FIX, "manifest.json")))
    base = json.load(open(BASE)) if os.path.exists(BASE) and not update else None

    bad, bled, let_go, hits, lats = [], 0, 0, 0, []
    print(f"{'fixture':<44} {'kind':<5} {'detected':>10} {'deepest':>8} {'end':>7}")
    for fx in manifest:
        c = check(fx)
        if fx["kind"] == "howl":
            det = f"{c['latency']*1000:+.0f} ms" if c["detected"] else "NEVER"
            print(f"{fx['file']:<44} howl  {det:>10} {c['deepest']:>7.1f}  {c['last']:>6.1f}"
                  f"{'  LET GO' if c['let_go'] else ('  relaxed' if c['bled'] else '')}")
            if not c["detected"]:      bad.append(f"{fx['file']}: real howl not detected")
            elif c["deepest"] > MIN_CUT: bad.append(f"{fx['file']}: only {c['deepest']:.1f} dB of cut on a real howl (dial {DIAL_DB:.0f})")
            if c["detected"]: lats.append(c["latency"])
            bled += int(c["bled"]); let_go += int(c["let_go"])
        else:
            print(f"{fx['file']:<44} voice {'HIT' if c['hit'] else 'left alone':>10} {c['deepest']:>7.1f}")
            hits += int(c["hit"])

    now = dict(howls=sum(1 for m in manifest if m["kind"] == "howl"), bled=bled, let_go=let_go, voice_hits=hits,
               median_latency_ms=(1000 * sorted(lats)[len(lats) // 2]) if lats else None)
    print(f"\n{now['howls']} real howls: let go {let_go}, relaxed {bled}, voice hits {hits}, median latency {now['median_latency_ms']:+.0f} ms")

    if base:
        if let_go > base.get("let_go", 0): bad.append(f"howls let go rose {base.get('let_go', 0)} -> {let_go}")
        if hits > base["voice_hits"]: bad.append(f"voice hits rose {base['voice_hits']} -> {hits}")
    if update or base is None:
        json.dump(now, open(BASE, "w"), indent=1); print(f"baseline {'re-' if update else ''}recorded -> gate-baseline.json")
        if update: sys.exit(0)
    if bad:
        print("\nFAIL"); [print("   " + b) for b in bad]; sys.exit(1)
    print("\nPASS")
