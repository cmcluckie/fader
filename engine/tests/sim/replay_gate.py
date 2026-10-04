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
try:
    from quality import voice_change          # the whole-phrase voice measure; needs the simulator's dependencies
except ImportError:
    voice_change = None
DIAL_DB   = rig_config()["max_cut_db"]
MIN_CUT   = DIAL_DB + 2.0
LET_GO_DB = DIAL_DB + 3.0


def load16(path):
    w = wave.open(path); n = w.getnframes(); sr = w.getframerate()
    x = np.frombuffer(w.readframes(n), dtype="<i2").astype(np.float64) / 32768.0
    return x, sr


def landed(x, sr, hz, cuts, need_db=-12.0):
    """How loud the line was when the bank first had `need_db` of cut on its own
    frequency. Read from the recording itself, in the detector's units."""
    hit = next((t for t, c in cuts if c <= need_db), None)
    if hit is None: return None
    N = 2048; i = max(0, int(hit * sr) - N)
    seg = x[i:i + N]
    if len(seg) < N: return None
    S = 20 * np.log10(np.abs(np.fft.rfft(seg * np.hanning(N))) * 2 / N + 1e-12)
    f = np.fft.rfftfreq(N, 1 / sr); sel = (f >= hz * 0.985) & (f <= hz * 1.015)
    return float(S[sel].max())


# A fast riser - a ring that goes from the floor to howl level inside a fifth of
# a second - must have a real cut on its own frequency before it is this loud.
# The cold jumps of 2026-10-03/04 reached -21 to -29 dB in the room on builds
# that named the line late; called on time they are stopped near -70.
FAST_LANDED_DB = -50.0


def check(fx):
    x, sr = load16(os.path.join(FIX, fx["file"]))
    r = Replay(sr); r.watch_hz = fx["hz"]; r.keep = fx["kind"] == "voice"; r.run(x)
    # A sung fixture used to be judged at one frequency - the needle it was cut
    # for. Judged as a phrase on 2026-10-04, the same fixtures that read "left
    # alone" were carrying twenty filters. Both are reported now.
    phrase = filters = None
    if r.keep and voice_change is not None:
        y = np.concatenate(r.out)
        phrase = voice_change(y, x[:len(y)], sr)["change_pct"]; filters = len(r.g.notches())
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
    rescue = r.g.rescue()
    return dict(rescues=rescue["episodes"], rescue_why=rescue["reason"], phrase_pct=phrase, filters=filters,
                landed=landed(x, sr, fx["hz"], r.cuts),
                detected=bool(ev), latency=(first - fx["onset"]) if first is not None else None,
                deepest=deepest, last=last, bled=(deepest <= -12.0 and last > deepest + 8.0),
                let_go=(deepest <= -12.0 and last > LET_GO_DB and last > deepest + 3.0),
                hit=(deepest < -6.0))


if __name__ == "__main__":
    update = "--update" in sys.argv
    if not os.path.exists(os.path.join(FIX, "manifest.json")):
        print("SKIP-FAIL  no fixtures: run make_fixtures.py on a machine with the recordings"); sys.exit(2)
    manifest = json.load(open(os.path.join(FIX, "manifest.json")))
    base = json.load(open(BASE)) if os.path.exists(BASE) and not update else None

    bad, bled, let_go, hits, lats, ducked = [], 0, 0, 0, [], 0
    cases, phrases, vfilters, fast_landed = [], [], [], []
    print(f"{'fixture':<44} {'kind':<5} {'detected':>10} {'deepest':>8} {'end':>7} {'landed':>7}")
    for fx in manifest:
        c = check(fx)
        cases.append(dict(file=fx["file"], kind=fx["kind"], hz=fx["hz"], fast=bool(fx.get("fast")),
                          lead_ms=(-1000 * c["latency"]) if c["latency"] is not None else None, deepest_db=c["deepest"],
                          end_db=c["last"], landed_db=c["landed"], let_go=c["let_go"], relaxed=c["bled"], hit=c["hit"],
                          rescues=c["rescues"], phrase_pct=c["phrase_pct"], filters=c["filters"]))
        if fx["kind"] == "howl" and fx.get("fast") and c["landed"] is not None: fast_landed.append(c["landed"])
        if fx["kind"] == "voice" and c["phrase_pct"] is not None: phrases.append(c["phrase_pct"]); vfilters.append(c["filters"])
        if fx["kind"] == "howl":
            det = f"{c['latency']*1000:+.0f} ms" if c["detected"] else "NEVER"
            ld = f"{c['landed']:6.1f}" if c["landed"] is not None else "   n/a"
            print(f"{fx['file']:<44} howl  {det:>10} {c['deepest']:>7.1f}  {c['last']:>6.1f}  {ld}"
                  f"{'  fast' if fx.get('fast') else ''}"
                  f"{'  LET GO' if c['let_go'] else ('  relaxed' if c['bled'] else '')}")
            if fx.get("fast"):
                if c["landed"] is None:
                    bad.append(f"{fx['file']}: a fast riser never got 12 dB on its own frequency")
                elif c["landed"] > FAST_LANDED_DB:
                    bad.append(f"{fx['file']}: fast riser was already at {c['landed']:.1f} dB when the cut landed (limit {FAST_LANDED_DB:.0f})")
            if not c["detected"]:      bad.append(f"{fx['file']}: real howl not detected")
            elif c["deepest"] > MIN_CUT: bad.append(f"{fx['file']}: only {c['deepest']:.1f} dB of cut on a real howl (dial {DIAL_DB:.0f})")
            if c["detected"]: lats.append(c["latency"])
            bled += int(c["bled"]); let_go += int(c["let_go"]); ducked += int(c["rescues"] > 0)
        else:
            print(f"{fx['file']:<44} voice {'HIT' if c['hit'] else 'left alone':>10} {c['deepest']:>7.1f}"
                  + (f"   whole phrase: {c['phrase_pct']:4.1f} % taken, {c['filters']} filters" if c["phrase_pct"] is not None else "")
                  + f"{'  RESCUE DUCK' if c['rescues'] else ''}")
            hits += int(c["hit"])
            # Not relative to any baseline: a duck on a voice is a dropout, and
            # the number that is acceptable is none.
            if c["rescues"]:
                bad.append(f"{fx['file']}: the rescue duck fired on a voice ({c['rescue_why']})")

    now = dict(howls=sum(1 for m in manifest if m["kind"] == "howl"), bled=bled, let_go=let_go, voice_hits=hits,
               median_latency_ms=(1000 * sorted(lats)[len(lats) // 2]) if lats else None)
    print(f"\n{now['howls']} real howls: let go {let_go}, relaxed {bled}, voice hits {hits}, median latency {now['median_latency_ms']:+.0f} ms"
          f"; rescue duck on {ducked} howls, 0 voices" if not any('rescue duck fired' in b for b in bad) else "")

    if phrases:
        print(f"the six sung phrases, whole: {np.mean(phrases):.1f} % of the voice taken on average (worst {max(phrases):.1f} %), "
              f"{np.mean(vfilters):.0f} filters each - with nothing ringing")
    if base:
        if let_go > base.get("let_go", 0): bad.append(f"howls let go rose {base.get('let_go', 0)} -> {let_go}")
        if hits > base["voice_hits"]: bad.append(f"voice hits rose {base['voice_hits']} -> {hits}")
    if update or base is None:
        json.dump(now, open(BASE, "w"), indent=1); print(f"baseline {'re-' if update else ''}recorded -> gate-baseline.json")
        if update: sys.exit(0)
    try:
        import ledger
        ledger.record("recorded-feedback", "recorded",
                      dict(howls=now["howls"], detected=sum(1 for c in cases if c["kind"] == "howl" and c["lead_ms"] is not None),
                           let_go=let_go, relaxed=bled, voice_hits=hits, voices=sum(1 for c in cases if c["kind"] == "voice"),
                           median_lead_ms=-now["median_latency_ms"] if now["median_latency_ms"] is not None else None,
                           worst_lead_ms=min((c["lead_ms"] for c in cases if c["kind"] == "howl" and c["lead_ms"] is not None), default=None),
                           fast_worst_landed_db=max(fast_landed) if fast_landed else None, rescue_on_howls=ducked,
                           voice_phrase_pct=float(np.mean(phrases)) if phrases else None,
                           voice_phrase_worst_pct=float(max(phrases)) if phrases else None,
                           voice_filters=float(np.mean(vfilters)) if vfilters else None),
                      ok=not bad, cases=cases)
    except ImportError:
        pass
    if bad:
        print("\nFAIL"); [print("   " + b) for b in bad]; sys.exit(1)
    print("\nPASS")
