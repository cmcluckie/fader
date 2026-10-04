#!/usr/bin/env python3
"""
A cold jump that knows how far over the edge it was.

The ring point of this room is not a constant: on 2026-10-04 it read -15 dB and,
five minutes later, -2.5; the night before -9.5, -12 and -4. At 10 kHz a
wavelength is three centimetres and a person moving shifts the strongest mode by
several decibels. So "a cold jump to 20 dB over" means nothing unless the edge
was measured seconds before and seconds after:

    baseline (guard off, drops at the first ring)   -> G0
    cold jump to G0 + N dB, guard on, held 8 s
    baseline again                                  -> G0'

The jump counts if G0 and G0' agree within --agree dB, and its margin is quoted
against both. Uses scripts/ringout.py for every move, so the kill switch, the
fader ceiling and the restore are the same as everywhere else.

    coldjump.py 20 22        # one cycle at each margin
"""
import argparse, glob, json, os, subprocess, sys, time
import numpy as np
import soundfile as sf

HERE = os.path.dirname(os.path.abspath(__file__))
OUT = os.path.expanduser("~/Documents/FeedbackKiller/ringout")


def run(args):
    before = set(glob.glob(OUT + "/*"))
    p = subprocess.run([sys.executable, os.path.join(HERE, "ringout.py")] + args, capture_output=True, text=True)
    new = sorted(set(glob.glob(OUT + "/*")) - before)
    d = new[-1] if new else None
    js = json.load(open(os.path.join(d, "summary.json"))) if d and os.path.exists(os.path.join(d, "summary.json")) else None
    return d, js, p.stdout


def baseline(guess):
    start = max(-30.0, guess - 8.0)
    d, js, out = run(["--mode", "baseline", "--start-db", f"{start}", "--max-db", "10", "--step-db", "1", "--hold-s", "1.5"])
    if not js or not js.get("rings"): return None
    return float(js["rings"][0]["db"])


def loudest(d):
    x, sr = sf.read(os.path.join(d, "sweep.wav"), dtype="float32", always_2d=True); pre = x[:, 0]
    N = 4096; w = np.hanning(N); fr = np.fft.rfftfreq(N, 1 / sr); lo = np.searchsorted(fr, 1000); best = (-200.0, 0.0, 0.0)
    for i in range(0, len(pre) - N, N // 4):
        s = 20 * np.log10(2 * np.abs(np.fft.rfft(pre[i:i + N] * w)) / w.sum() + 1e-12); b = lo + int(np.argmax(s[lo:]))
        if s[b] > best[0]: best = (float(s[b]), float(fr[b]), i / sr)
    return best


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("over", nargs="+", type=float, help="decibels over the ring point to jump to")
    ap.add_argument("--guess", type=float, default=-6.0, help="where the ring point probably is (channel fader dB, main at 0)")
    ap.add_argument("--agree", type=float, default=3.0)
    ap.add_argument("--no-rescue", action="store_true")
    a = ap.parse_args()
    guess = a.guess
    print(f"{'over':>5} | {'edge before':>11} {'edge after':>10} | {'ch9':>5} {'main':>5} | {'loudest bin':>22} | {'detections':>10} {'rescues':>7} {'killed':>6} | verdict")
    for over in a.over:
        g0 = baseline(guess)
        if g0 is None: print(f"{over:5.0f} | no ring found in the baseline - nothing to jump over"); continue
        total = g0 + over
        ch, mn = (total, 0.0) if total <= 10.0 else (10.0, total - 10.0)
        if mn > 10.0: print(f"{over:5.0f} | edge {g0:+.1f}: {total:+.1f} dB needs more than both faders have"); continue
        time.sleep(12)                                   # let the baseline's ring and the bank settle
        d, js, out = run(["--mode", "guard", "--main-db", f"{mn}", "--start-db", f"{ch}", "--max-db", f"{ch}",
                          "--step-db", "0.5", "--hold-s", "8"] + (["--no-rescue"] if a.no_rescue else []))
        lb = loudest(d)
        rows = [l.split(",") for l in open(os.path.join(d, "trials.csv")).read().splitlines()[1:]]
        ndet = sum(1 for r in rows if r[2] == "detector"); nres = sum(1 for r in rows if r[2] == "rescue")
        time.sleep(12)
        g1 = baseline(g0)
        ok = g1 is not None and abs(g1 - g0) <= a.agree
        g1s = f"{g1:+10.1f}" if g1 is not None else f"{'none':>10}"
        real = f"really {total - g0:.0f}-{total - g1:.0f} over" if g1 is not None else "edge lost"
        print(f"{over:5.0f} | {g0:+11.1f} {g1s} | {ch:+5.1f} {mn:+5.1f} | {lb[0]:6.1f} dB @ {lb[1]:5.0f} Hz {lb[2]:3.1f}s | {ndet:10d} {nres:7d} {str(bool(js and js.get('killed'))):>6} | "
              f"{'VALID' if ok else 'edge moved'} ({real})")
        try:
            sys.path.insert(0, HERE)
            import results
            killed = bool(js and js.get("killed"))
            results.ledger.record("cold-jump", "live",
                                  dict(over_db=over, edge_before_db=g0, edge_after_db=g1, valid=ok, fader_db=ch, main_db=mn,
                                       loudest_db=lb[0], loudest_hz=lb[1], loudest_at_s=lb[2], detections=ndet, rescues=nres, killed=killed),
                                  ok=(lb[0] <= -45.0 and not killed) if ok else None, force=True)
        except Exception as e:
            print(f"   (result not logged: {e})")
        guess = g1 if g1 is not None else g0
        time.sleep(12)


if __name__ == "__main__":
    main()
