#!/usr/bin/env python3
"""
Did the bank put a filter ON the ring, or next to it?

Replays a recording's pre-notch channel through the shipping detector and bank
(fk-loopdsp) and, for every detection, asks the bank what it actually did: which
filter is nearest now, how deep, and how much cut lands at the ring's own
frequency. A trigger that returns "placed" and leaves the ring with 3 dB of cut is
the failure this is for - the engine reports success while deepening a neighbour.

    merge_audit.py <recording.wav> --howls timeline.csv [--budget 0] [--offset-rec 0]

`--howls` is the CSV timeline.py writes; each howl's centre frequency is sampled
every 10 ms for the cut the bank is applying there, so the time from onset to a
6 dB cut comes from the bank itself, not from the post-notch channel.
"""
import argparse, csv, ctypes, sys, os
import numpy as np
import soundfile as sf

sys.path.insert(0, os.path.dirname(__file__))
from fk_in_loop import FkGuard

CHUNK = 480   # 10 ms at 48 k


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('wav'); ap.add_argument('--howls', required=True)
    ap.add_argument('--budget', type=float, default=0.0)
    ap.add_argument('--from', dest='t_from', type=float, default=0.0)
    ap.add_argument('--until', type=float, default=1e9)
    ap.add_argument('--detail', type=float, help='print every event near this frequency (Hz)')
    ap.add_argument('--window', type=float, default=0.06, help='fractional window for --detail')
    ap.add_argument('--prepend', action='append', default=[], help='recording to play first, state carried over')
    ap.add_argument('--gap', type=float, default=0.0, help='seconds of silence between recordings')
    a = ap.parse_args()

    x, sr = sf.read(a.wav, dtype='float32', always_2d=True)
    pre = np.ascontiguousarray(x[:, 0])
    howls = list(csv.DictReader(open(a.howls)))
    centres = np.array([float(h['hz']) for h in howls], np.float32)

    g = FkGuard(sr, block=CHUNK, disconnected=True)
    g.lib.fk_set_loop_verdict.argtypes = [ctypes.c_void_p, ctypes.c_int]
    g.lib.fk_set_loop_verdict(g.h, 0)
    g.lib.fk_set_budget.argtypes = [ctypes.c_void_p, ctypes.c_float]
    g.lib.fk_set_budget(g.h, float(a.budget))
    g.lib.fk_pop_event.argtypes = [ctypes.c_void_p, ctypes.POINTER(ctypes.c_float), ctypes.POINTER(ctypes.c_float),
                                   ctypes.POINTER(ctypes.c_int), ctypes.POINTER(ctypes.c_int), ctypes.POINTER(ctypes.c_double)]
    g.lib.fk_pop_event.restype = ctypes.c_int

    hz = ctypes.c_float(); lv = ctypes.c_float(); gr = ctypes.c_int(); pa = ctypes.c_int(); tt = ctypes.c_double()
    FP = ctypes.POINTER(ctypes.c_float)
    g.lib.fk_notch_detail.argtypes = [ctypes.c_void_p, ctypes.c_int, FP, FP, FP, FP, ctypes.POINTER(ctypes.c_int)]
    g.lib.fk_notch_detail.restype = ctypes.c_int
    g.lib.fk_find_near.argtypes = [ctypes.c_void_p, ctypes.c_float]
    g.lib.fk_find_near.restype = ctypes.c_int

    def detail(i):
        f = ctypes.c_float(); o = ctypes.c_float(); q = ctypes.c_float(); d = ctypes.c_float(); lk = ctypes.c_int()
        if not g.lib.fk_notch_detail(g.h, i, f, o, q, d, lk): return None
        return (float(f.value), float(o.value), float(q.value), float(d.value), int(lk.value))

    # History first: the bank's state at the start of the recording under audit is
    # whatever the earlier recordings left behind, which is where the neighbour that
    # swallows a ring comes from.
    for path in a.prepend:
        y, sr2 = sf.read(path, dtype='float32', always_2d=True)
        yy = np.ascontiguousarray(y[:, 0])
        for k in range(len(yy) // CHUNK): g.process(yy[k * CHUNK:(k + 1) * CHUNK])
        silence = np.zeros(CHUNK, np.float32)
        for k in range(int(a.gap * sr2 / CHUNK)): g.process(silence)
        while g.lib.fk_pop_event(g.h, hz, lv, gr, pa, tt): pass
    if a.prepend:
        print("filters carried in from history:")
        for i in range(48):
            d = detail(i)
            if d: print(f"   slot {i:2d}  {d[0]:8.1f} Hz  anchor {d[1]:8.1f}  Q {d[2]:5.1f}  {d[3]:6.1f} dB{'  locked' if d[4] else ''}")

    hz = ctypes.c_float(); lv = ctypes.c_float(); gr = ctypes.c_int(); pa = ctypes.c_int(); tt = ctypes.c_double()
    events = []                      # (t, hz, lvl, growing, path, nearest_f, nearest_depth, cut_here)
    cuts = []                        # per chunk: cut at each howl centre
    times = []
    n = len(pre) // CHUNK
    for k in range(n):
        t = k * CHUNK / sr
        if t < a.t_from or t > a.until:
            if t > a.until: break
            g.process(pre[k * CHUNK:(k + 1) * CHUNK]); continue
        g.process(pre[k * CHUNK:(k + 1) * CHUNK])
        while g.lib.fk_pop_event(g.h, hz, lv, gr, pa, tt):
            f = float(hz.value)
            notches = g.notches()
            if notches:
                nf, nd = min(notches, key=lambda p: abs(p[0] - f))
            else:
                nf, nd = 0.0, 0.0
            cut = float(g.lib.fk_cut_at(g.h, ctypes.c_float(f)))
            near = g.lib.fk_find_near(g.h, ctypes.c_float(f))
            nd_ = detail(near) if near >= 0 else None
            events.append((t, f, float(lv.value), int(gr.value), int(pa.value), nf, nd, cut, nd_))
        cuts.append([float(g.lib.fk_cut_at(g.h, ctypes.c_float(c))) for c in centres]); times.append(t)
    cuts = np.array(cuts); times = np.array(times)

    print(f"{len(events)} detections, budget {a.budget:g}")
    hdr = f"{'t_on':>7} {'Hz':>6} {'peak':>6} | {'1st ev':>6} {'@dB':>6} | {'1st on-ring':>11} {'cut>=6':>7} {'cut>=12':>7} {'maxcut':>6} | {'ev':>3} {'merged':>6} {'far%':>5}"
    print(hdr); print('-' * len(hdr))
    agg = {'det': [], 'onring': [], 'c6': [], 'c12': [], 'merged': 0, 'total': 0}
    for i, h in enumerate(howls):
        f0 = float(h['hz']); t_on = float(h['t_on']); t_end = t_on + float(h['dur'])
        mine = [e for e in events if abs(e[1] - f0) / f0 <= 0.04 and t_on - 0.6 <= e[0] <= t_end + 0.5]
        first = mine[0] if mine else None
        # "on the ring": after this trigger the nearest filter is within 1% of the ring
        onring = [e for e in mine if e[5] > 0 and abs(e[5] - e[1]) / e[1] <= 0.01]
        merged = [e for e in mine if e[5] > 0 and abs(e[5] - e[1]) / e[1] > 0.01]
        far = (100.0 * np.median([abs(e[5] - e[1]) / e[1] for e in merged])) if merged else float('nan')
        sel = (times >= t_on - 0.6) & (times <= t_end + 0.5)
        c = cuts[sel, i]; ts = times[sel]
        def first_at(th):
            w = np.where(c <= -th)[0]
            return (ts[w[0]] - t_on) if len(w) else float('nan')
        c6, c12 = first_at(6.0), first_at(12.0)
        print(f"{t_on:7.2f} {f0:6.0f} {float(h['peak']):6.1f} | {(first[0]-t_on) if first else float('nan'):6.2f} {first[2] if first else float('nan'):6.1f} | "
              f"{(onring[0][0]-t_on) if onring else float('nan'):11.2f} {c6:7.2f} {c12:7.2f} {(-c.min()) if len(c) else 0:6.1f} | "
              f"{len(mine):3d} {len(merged):6d} {far:5.1f}")
        if first: agg['det'].append(first[0] - t_on)
        if onring: agg['onring'].append(onring[0][0] - t_on)
        agg['c6'].append(c6); agg['c12'].append(c12); agg['merged'] += len(merged); agg['total'] += len(mine)
    def med(v):
        v = np.array(v, float); v = v[np.isfinite(v)]; return float(np.median(v)) if len(v) else float('nan')
    nan6 = int(np.sum(~np.isfinite(np.array(agg['c6'], float))))
    print(f"\nmedians: first detection {med(agg['det']):.2f} s after onset; first filter ON the ring {med(agg['onring']):.2f} s; "
          f"6 dB at the ring {med(agg['c6']):.2f} s; 12 dB {med(agg['c12']):.2f} s; never reached 6 dB: {nan6}/{len(howls)}; "
          f"triggers merged onto a neighbour: {agg['merged']}/{agg['total']}")

    if a.detail:
        f0 = a.detail
        print(f"\nevents within {a.window*100:.0f}% of {f0:.0f} Hz:")
        print(f"{'t':>7} {'Hz':>7} {'lvl':>6} {'gr':>2} {'path':>4} {'nearest':>8} {'depth':>6} {'cut@f':>6} | merge target now: freq anchor Q depth")
        for e in events:
            if abs(e[1] - f0) / f0 <= a.window:
                m = e[8]
                ms = f"{m[0]:8.1f} {m[1]:8.1f} {m[2]:5.1f} {m[3]:6.1f}{' locked' if m[4] else ''}" if m else "   (would open a new filter)"
                print(f"{e[0]:7.2f} {e[1]:7.1f} {e[2]:6.1f} {e[3]:2d} {e[4]:4d} {e[5]:8.1f} {e[6]:6.1f} {e[7]:6.1f} | {ms}")


if __name__ == '__main__':
    main()
