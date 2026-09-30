"""
recall.py — the detector scored against rings the simulator KNOWS it produced.

feedback_sim decides which frequency rings from the loop's own physics. That is
ground truth no recording can give: a recording tells you what a person would
have seen; the simulator tells you what actually rang, and when. Until this
file the sim was scored only on OUTCOMES - runaways, harm, HF suppressed -
never on the question underneath them: of the rings it produced, how many did
the detector see, how fast, and what did it say when it declined?

The guard runs DISCONNECTED (its cuts never reach the loop) with the loop
verdict off, so every ring persists and detection is scored on its own rather
than on whether suppression happened to end the ring before the detector spoke.

    python3 recall.py
"""
import ctypes, os, sys
from collections import Counter
import numpy as np

HERE = os.path.dirname(os.path.abspath(__file__)); sys.path.insert(0, HERE)
from feedback_sim import FeedbackSim
from fk_in_loop import FkGuard
from replay import segments, REASON, PATH, WIN, HOP

ROOM      = dict(dims=(9, 7, 3.2), rt60=0.7, speaker_pos=(1.0, 3.5, 1.7))
POSITIONS = [(4.00, 3.50, 1.60), (3.92, 3.52, 1.48), (4.06, 3.47, 1.66)]
MARGINS   = [3.0, 6.0, 9.0]
SECONDS   = 12.0


class Listening(FkGuard):
    """An FkGuard that keeps everything the detector said."""

    def __init__(self, fs, block):
        super().__init__(fs, block, caps=(-12.0, -18.0, -45.0), disconnected=True)
        lib = self.lib
        FP, IP, DP = (ctypes.POINTER(t) for t in (ctypes.c_float, ctypes.c_int, ctypes.c_double))
        lib.fk_pop_event.restype = ctypes.c_int;  lib.fk_pop_event.argtypes  = [ctypes.c_void_p, FP, FP, IP, IP, DP]
        lib.fk_pop_reject.restype = ctypes.c_int; lib.fk_pop_reject.argtypes = [ctypes.c_void_p, FP, FP, IP, IP, DP, FP, FP]
        lib.fk_set_loop_verdict.argtypes = [ctypes.c_void_p, ctypes.c_int]
        lib.fk_set_loop_verdict(self.h, 0)
        self.evs, self.rjs = [], []

    def process(self, x):
        y = super().process(x)
        hz, lv, sp, tl = (ctypes.c_float() for _ in range(4)); a, b = ctypes.c_int(), ctypes.c_int(); t = ctypes.c_double()
        while self.lib.fk_pop_event(self.h, hz, lv, a, b, t):
            self.evs.append((t.value, hz.value, lv.value, a.value, b.value))
        while self.lib.fk_pop_reject(self.h, hz, lv, a, b, t, sp, tl):
            self.rjs.append((t.value, hz.value, lv.value, a.value, b.value, sp.value, tl.value))
        return y


def windows_of(x, sr):
    """The same 'what a person sees' windows replay.py uses, over a whole array."""
    win = np.hanning(WIN); f = np.fft.rfftfreq(WIN, 1 / sr); top = f >= 1000.0
    out = []
    for s in range(WIN, len(x), int(HOP * sr)):
        P = np.abs(np.fft.rfft(x[s - WIN:s] * win)) ** 2; band = P[top]
        if band.sum() <= 0: continue
        i = int(np.argmax(band))
        out.append((s / sr, float(f[top][i]), 10 * np.log10(band[i] + 1e-20),
                    float(np.sort(band)[::-1][:5].sum() / band.sum()), 0.0))
    return out


def run(pos, margin):
    sim = FeedbackSim(fs=44100)
    sim.build_room(mic_path=[(0.0, pos)], **ROOM)
    sim.set_gain_margin(margin)
    g = Listening(sim.fs, sim.block); sim.external_eq = g
    _, mic, _ = sim.run(seconds=SECONDS, seed="click")
    ladder = [c["freq"] for c in sim.predicted_howls(top=12)]
    rows = []
    for sg in segments(windows_of(mic, sim.fs)):
        f0, t0, t1 = sg["f"], sg["t0"], sg["t1"]
        ev = [e for e in g.evs if abs(e[1] - f0) / f0 <= 0.03 and t0 - 0.5 <= e[0] <= t1]
        rj = [q for q in g.rjs if abs(q[1] - f0) / f0 <= 0.05 and t0 - 0.5 <= q[0] <= t1]
        first = min((e[0] for e in ev), default=None)
        rows.append(dict(pos=pos, margin=margin, f=f0, t0=t0, dur=t1 - t0, peak=sg["peak"],
                         lat=(first - t0) if first is not None else None, n=len(ev),
                         why=Counter(REASON.get(q[3], str(q[3])) for q in rj),
                         wob=[(q[5], q[6]) for q in rj],
                         predicted=any(abs(c - f0) / f0 <= 0.03 for c in ladder)))
    return rows


if __name__ == "__main__":
    print("detector recall against the simulator's own rings (guard disconnected, verdict off)\n")
    print(f"{'pos':>4} {'margin':>6} {'ring Hz':>8} {'onset':>6} {'dur':>5} {'peak':>6} {'detected':>10} {'events':>7} {'ladder?':>8}  declined")
    allrows = []
    for pi, pos in enumerate(POSITIONS):
        for m in MARGINS:
            for r in run(pos, m):
                allrows.append(r)
                det = f"{r['lat']*1000:+.0f} ms" if r["lat"] is not None else "NEVER"
                wob = ""
                if r["wob"]:
                    sp = sorted(s for s, _ in r["wob"]); tl = sorted(t for _, t in r["wob"])
                    wob = f" (spread {sp[len(sp)//2]:.0f} vs tol {tl[len(tl)//2]:.0f} Hz)"
                print(f"{pi:>4} {m:>+6.0f} {r['f']:>8.0f} {r['t0']:>6.1f} {r['dur']:>5.1f} {r['peak']:>+6.1f} {det:>10} {r['n']:>7} "
                      f"{'yes' if r['predicted'] else 'no':>8}  {dict(r['why']) if r['why'] else '-'}{wob}")
    n = len(allrows); seen = [r for r in allrows if r["lat"] is not None]
    lats = sorted(r["lat"] for r in seen)
    print(f"\n{'='*78}\n{n} rings the simulator produced")
    print(f"   detected : {len(seen)}/{n}")
    if lats: print(f"   latency  : median {1000*lats[len(lats)//2]:+.0f} ms, worst {1000*lats[-1]:+.0f} ms  (negative = before it was visible)")
    miss = [r for r in allrows if r["lat"] is None]
    if miss:
        why = Counter(); [why.update(r["why"]) for r in miss]
        print(f"   missed   : {len(miss)}, declined as {dict(why) if why else 'nothing said at all'}")
        lo = [r for r in miss if r["f"] < 2000]; print(f"   missed below 2 kHz: {len(lo)} of {sum(1 for r in allrows if r['f'] < 2000)} rings there")
    print(f"   predicted by the phase ladder: {sum(1 for r in allrows if r['predicted'])}/{n}")
