#!/usr/bin/env python3
"""
Per-howl timelines from a flight recording.

The recording has two channels: pre-notch and post-notch. The difference between
them at a frequency is exactly the cut the guard applied there, instant by instant,
so a howl's story can be told without trusting anything the engine said about
itself: when did the ring start growing, when did the detector fire, when did a cut
actually arrive at that frequency, how deep, and what did the ring do about it.

    timeline.py <recording.wav> [--capture capture-log.csv] [--events feedback-log.csv]
                [--offset SECONDS] [--min-hz 200] [--csv out.csv]

The engine's logs carry app-uptime seconds; the recording starts at some uptime
the file name only gives to the second. The offset is refined by matching each
logged detection's level against the recording's own level at that frequency.
"""
import argparse, csv, math, sys
import numpy as np
import soundfile as sf

N = 2048; HOP = 480


def stft_db(x, sr):
    w = np.hanning(N)
    frames = 1 + (len(x) - N) // HOP
    idx = np.arange(N)[None, :] + HOP * np.arange(frames)[:, None]
    X = np.fft.rfft(x[idx] * w, axis=1)
    mag = 2.0 * np.abs(X) / w.sum()                     # a full-scale sine reads 0 dB
    return 20.0 * np.log10(mag + 1e-12), np.arange(frames) * HOP / sr, np.fft.rfftfreq(N, 1.0 / sr)


def ridges(S, t, f, min_hz, level_db=-60.0, prom_db=12.0):
    """Peaks per frame that stand out from their neighbourhood, linked into tracks."""
    lo = np.searchsorted(f, min_hz)
    tracks, live = [], []
    for k in range(S.shape[0]):
        row = S[k]
        peaks = []
        for b in range(lo + 3, len(f) - 3):
            v = row[b]
            if v < level_db or v < row[b - 1] or v < row[b + 1]: continue
            span = max(8, int(400.0 / (f[1] - f[0])))
            hood = np.concatenate([row[max(lo, b - span):b - 3], row[b + 4:b + span]])
            if v - np.median(hood) < prom_db: continue
            # parabolic refinement of the bin
            a, c = row[b - 1], row[b + 1]
            d = 0.5 * (a - c) / (a - 2 * v + c) if (a - 2 * v + c) != 0 else 0.0
            peaks.append(((b + d) * (f[1] - f[0]), v, b))
        used = set(); nxt = []
        for tr in live:
            fh, _, _ = tr['pts'][-1]
            best = None
            for i, p in enumerate(peaks):
                if i in used: continue
                if abs(p[0] - fh) / fh <= 0.03 and (best is None or p[1] > peaks[best][1]): best = i
            if best is not None:
                used.add(best); tr['pts'].append(peaks[best]); tr['last'] = k; nxt.append(tr)
            elif k - tr['last'] <= 8:                       # survive 80 ms gaps
                nxt.append(tr)
            else:
                tracks.append(tr)
        for i, p in enumerate(peaks):
            if i not in used: nxt.append({'start': k, 'last': k, 'pts': [p]})
        live = nxt
    tracks += live
    out = []
    for tr in tracks:
        pts = tr['pts']
        if len(pts) < 20: continue                           # < 0.2 s is not a howl
        lv = np.array([p[1] for p in pts]); fr = np.array([p[0] for p in pts]); bins = np.array([p[2] for p in pts])
        ks = np.arange(tr['start'], tr['start'] + len(pts))   # approximate; gaps are rare and short
        out.append({'k0': tr['start'], 'k1': tr['last'], 'f': fr, 'lv': lv, 'bins': bins, 'ks': ks})
    return out


def load_events(path, capture):
    ev = []
    with open(path) as fh:
        for r in csv.DictReader(fh):
            try:
                e = {'t': float(r['seconds']), 'hz': float(r['frequency_hz']), 'lv': float(r['level_db'])}
            except (KeyError, ValueError):
                continue
            if capture:
                e.update(path=r.get('gate', ''), cut=float(r.get('cut_here_db', 'nan') or 'nan'),
                         near=r.get('nearest_notch_hz', ''), filt=r.get('filter', ''), guard=r.get('guard_on', ''))
            else:
                e.update(path='', cut=float('nan'), near='', filt='applied' if r.get('applied') == '1' else 'no', guard='')
            ev.append(e)
    return ev


def refine_offset(events, S, t, f, offset):
    """Match each logged level to the recording level at that frequency: the lag that
    makes them agree is the true offset. Reports the median residual too, which is
    the detector's unit calibration against this STFT."""
    diffs = []
    for e in events:
        b = int(round(e['hz'] / (f[1] - f[0])))
        if b < 2 or b >= len(f) - 2: continue
        col = S[:, b - 1:b + 2].max(axis=1)
        k = int(round((e['t'] - offset) / (HOP / 48000.0)))
        if k < 50 or k >= len(t) - 50: continue
        win = col[k - 50:k + 50]                              # +-0.5 s
        # first time within the window the level reaches the logged value
        hits = np.where(win >= e['lv'])[0]
        if len(hits) == 0: continue
        diffs.append((hits[0] - 50) * HOP / 48000.0)
    if not diffs: return offset, 0
    return offset + float(np.median(diffs)), len(diffs)


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('wav'); ap.add_argument('--capture'); ap.add_argument('--events')
    ap.add_argument('--offset', type=float, help='uptime seconds at recording start')
    ap.add_argument('--min-hz', type=float, default=200.0)
    ap.add_argument('--howl-db', type=float, default=-45.0, help='peak level that counts as a howl')
    ap.add_argument('--csv')
    a = ap.parse_args()

    x, sr = sf.read(a.wav, dtype='float32', always_2d=True)
    pre, post = x[:, 0], x[:, 1]
    S, t, f = stft_db(pre, sr); P, _, _ = stft_db(post, sr)

    events = []
    if a.capture: events = load_events(a.capture, True)
    elif a.events: events = load_events(a.events, False)
    offset = a.offset or 0.0
    if events and a.offset is not None:
        offset, n = refine_offset(events, S, t, f, a.offset)
        print(f"offset {a.offset:.1f} -> {offset:.3f} s (from {n} matched detections)")

    tracks = ridges(S, t, f, a.min_hz)
    rows = []
    for tr in tracks:
        lv, fr, ks, bins = tr['lv'], tr['f'], tr['ks'], tr['bins']
        if lv.max() < a.howl_db: continue
        kp = int(np.argmax(lv)); k_on = ks[0]; t_on = t[k_on]; t_pk = t[ks[kp]]
        # growth: fit from onset to 3 dB below peak
        up = np.where(lv[:kp + 1] <= lv[kp] - 3)[0]
        seg = slice(0, (up[-1] + 1) if len(up) else kp + 1)
        growth = float(np.polyfit(t[ks[seg]], lv[seg], 1)[0]) if (seg.stop - seg.start) >= 3 else float('nan')
        # fall: time from peak to -10 dB and -20 dB
        def fall(db):
            w = np.where(lv[kp:] <= lv[kp] - db)[0]
            return t[ks[kp + w[0]]] - t_pk if len(w) else float('nan')
        # cut at the ridge frequency, frame by frame, from the two channels
        cut = np.array([S[k, b] - P[k, b] for k, b in zip(ks, bins)])
        arrived = np.where(cut >= 6.0)[0]
        t_cut = t[ks[arrived[0]]] - t_on if len(arrived) else float('nan')
        cut_pk = float(cut[kp]); cut_max = float(cut.max())
        # the detector's view
        f0 = float(fr[kp])
        mine = [e for e in events if abs(e['hz'] - f0) / f0 <= 0.06 and (t_on - 0.5) <= (e['t'] - offset) <= t[ks[-1]] + 0.5]
        first = min(mine, key=lambda e: e['t']) if mine else None
        placed = [e for e in mine if e['filt'] == 'placed' or e['filt'] == 'applied']
        fp = min(placed, key=lambda e: e['t']) if placed else None
        hop = float(fr[-1] / fr[0] - 1.0) * 100.0
        rows.append(dict(hz=f0, t_on=t_on, peak=float(lv[kp]), climb=t_pk - t_on, growth=growth,
                         det=(first['t'] - offset - t_on) if first else float('nan'),
                         det_lv=first['lv'] if first else float('nan'), det_path=first['path'] if first else '',
                         det_cut=first['cut'] if first else float('nan'),
                         placed=(fp['t'] - offset - t_on) if fp else float('nan'),
                         cut_arr=t_cut, cut_pk=cut_pk, cut_max=cut_max, fall10=fall(10), fall20=fall(20),
                         n_ev=len(mine), n_placed=len(placed), dur=t[ks[-1]] - t_on, hop=hop,
                         refusals=','.join(sorted({e['filt'] for e in mine if e['filt'] not in ('placed', 'applied', '')}))))
    rows.sort(key=lambda r: r['t_on'])

    hdr = f"{'t_on':>7} {'Hz':>6} {'peak':>6} {'climb':>5} {'dB/s':>5} | {'det':>6} {'@dB':>6} {'path':<10} {'cut@':>5} {'plcd':>6} | {'cut>6':>6} {'cut@pk':>6} {'cutmax':>6} | {'-10dB':>5} {'-20dB':>5} {'dur':>5} {'ev':>3} {'hop%':>5}  refusals"
    print(hdr); print('-' * len(hdr))
    for r in rows:
        print(f"{r['t_on']:7.2f} {r['hz']:6.0f} {r['peak']:6.1f} {r['climb']:5.2f} {r['growth']:5.0f} | "
              f"{r['det']:6.2f} {r['det_lv']:6.1f} {r['det_path']:<10} {r['det_cut']:5.1f} {r['placed']:6.2f} | "
              f"{r['cut_arr']:6.2f} {r['cut_pk']:6.1f} {r['cut_max']:6.1f} | "
              f"{r['fall10']:5.2f} {r['fall20']:5.2f} {r['dur']:5.1f} {r['n_ev']:3d} {r['hop']:5.1f}  {r['refusals']}")
    if rows:
        def med(key):
            v = np.array([r[key] for r in rows], dtype=float); v = v[np.isfinite(v)]
            return float(np.median(v)) if len(v) else float('nan')
        print(f"\n{len(rows)} howls (peak >= {a.howl_db:.0f} dB). medians: peak {med('peak'):.1f} dB, climb {med('climb'):.2f} s, "
              f"growth {med('growth'):.0f} dB/s, detect {med('det'):.2f} s after onset at {med('det_lv'):.1f} dB, "
              f"placed {med('placed'):.2f} s, cut>=6 dB at {med('cut_arr'):.2f} s, cut at peak {med('cut_pk'):.1f} dB, "
              f"max cut {med('cut_max'):.1f} dB, fall 10 dB {med('fall10'):.2f} s")
    if a.csv:
        with open(a.csv, 'w', newline='') as fh:
            w = csv.DictWriter(fh, fieldnames=list(rows[0].keys())); w.writeheader(); w.writerows(rows)


if __name__ == '__main__':
    main()
