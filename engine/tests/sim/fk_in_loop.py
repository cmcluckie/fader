"""
fk_in_loop.py — the shipping Feedback Fader guard, inside feedback_sim's loop.

The guard is placed where the console EQ sits: after the preamp, before the
power amp. That position is the whole point. Rendering the sim's output through
the guard afterwards would only suppress a recording of feedback; in the loop,
what the guard cuts changes the loop gain on the very next pass, so a notch can
actually prevent the howl instead of attenuating it after the fact.

What this buys that fk-fuzz cannot:
  - a real phase condition, so WHICH frequency rings is decided by physics
  - a saturating power amp, so a spike at 5 kHz can suppress a mode at 77 Hz
  - a moving mic, so the candidate set moves under the guard
"""
import ctypes, os, sys, functools
print = functools.partial(print, flush=True)
import numpy as np

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from feedback_sim import FeedbackSim, db

LIB = "/Users/cmcluckie/Code/Fader/engine/build/libfk-loopdsp.dylib"


class FkGuard:
    """Quacks like feedback_sim's StatefulSOS, but is 4000 lines of C++."""

    def __init__(self, fs, block=64, caps=None):
        self.lib = ctypes.CDLL(LIB)
        self.lib.fk_create.restype = ctypes.c_void_p
        self.lib.fk_create.argtypes = [ctypes.c_double, ctypes.c_int]
        self.lib.fk_process.argtypes = [ctypes.c_void_p,
                                        np.ctypeslib.ndpointer(np.float32, flags="C_CONTIGUOUS"),
                                        ctypes.c_int]
        self.lib.fk_event_count.argtypes = [ctypes.c_void_p]
        self.lib.fk_cut_at.argtypes = [ctypes.c_void_p, ctypes.c_float]
        self.lib.fk_cut_at.restype = ctypes.c_float
        # argtypes are NOT optional here: without them ctypes passes the 64-bit
        # handle as a 32-bit int, truncates the pointer, and the library
        # segfaults on the first call.
        FP = ctypes.POINTER(ctypes.c_float)
        self.lib.fk_notches.restype = ctypes.c_int
        self.lib.fk_notches.argtypes = [ctypes.c_void_p, FP, FP, ctypes.c_int]
        self.lib.fk_tracks.restype = ctypes.c_int
        self.lib.fk_tracks.argtypes = [ctypes.c_void_p, FP, FP, FP, FP, ctypes.c_int]
        self.lib.fk_event_count.restype = ctypes.c_int
        self.lib.fk_destroy.argtypes = [ctypes.c_void_p]
        self.h = self.lib.fk_create(float(fs), int(block))
        if not self.h:
            raise RuntimeError("fk_create failed")
        if caps:
            self.lib.fk_set_caps.argtypes = [ctypes.c_void_p, ctypes.c_float,
                                             ctypes.c_float, ctypes.c_float]
            self.lib.fk_set_caps(self.h, *[float(c) for c in caps])
        self.zi = np.zeros((1, 2))     # the sim pokes this; harmless

    def process(self, x):
        buf = np.ascontiguousarray(x, dtype=np.float32)
        self.lib.fk_process(self.h, buf, len(buf))
        return buf.astype(np.float64)

    @property
    def events(self):
        return int(self.lib.fk_event_count(self.h))

    def notches(self, cap=48):
        f = np.zeros(cap, np.float32); d = np.zeros(cap, np.float32)
        n = self.lib.fk_notches(self.h, f.ctypes.data_as(ctypes.POINTER(ctypes.c_float)),
                                d.ctypes.data_as(ctypes.POINTER(ctypes.c_float)), cap)
        return list(zip(f[:n].tolist(), d[:n].tolist()))

    def tracks(self, cap=8):
        a = [np.zeros(cap, np.float32) for _ in range(4)]
        p = [x.ctypes.data_as(ctypes.POINTER(ctypes.c_float)) for x in a]
        n = self.lib.fk_tracks(self.h, p[0], p[1], p[2], p[3], cap)
        return [(float(a[0][i]), float(a[1][i]), float(a[2][i]), float(a[3][i]))
                for i in range(n)]


def band_db(x, fs, lo, hi, W=4096):
    """Peak short-time energy in a band, dB — how loud it ever got."""
    f = np.fft.rfftfreq(W, 1 / fs); sel = (f >= lo) & (f < hi)
    win = np.hanning(W); best = -200.0
    for s in range(0, max(1, len(x) - W), W // 2):
        seg = x[s:s + W]
        if len(seg) < W: break
        m = np.abs(np.fft.rfft(seg * win)) ** 2
        best = max(best, 10 * np.log10(max(m[sel].sum(), 1e-20)))
    return best


def scenario(margin_db=3.0, seconds=12.0, guarded=True, mic_path=None, seed=1):
    sim = FeedbackSim(fs=44100)
    sim.build_room(dims=(9, 7, 3.2), rt60=0.7, speaker_pos=(1.0, 3.5, 1.7),
                   mic_path=mic_path or [(0.0, (4.0, 3.5, 1.6))])
    sim.set_gain_margin(margin_db)
    ladder = sim.predicted_howls()[:4]

    guard = None
    if guarded:
        guard = FkGuard(sim.fs, sim.block)
        sim.external_eq = guard

    out, mic, ev = sim.run(seconds=seconds, seed="click")
    return sim, guard, mic, out, ladder


if __name__ == "__main__":
    print("fk-loopdsp inside feedback_sim — the guard in the loop, not after it\n")
    for margin in (3.0, 6.0):
        row = {}
        for guarded in (False, True):
            sim, guard, mic, out, ladder = scenario(margin_db=margin, guarded=guarded)
            row[guarded] = dict(
                peak=float(db(np.max(np.abs(mic)))),
                hf=band_db(mic, sim.fs, 2000, 16000),
                lf=band_db(mic, sim.fs, 40, 400),
                guard=guard)
            if guarded:
                lad = ladder
        g, u = row[True], row[False]
        print(f"=== gain margin +{margin:.0f} dB ===")
        print("  predicted ladder (growth-ranked):",
              ", ".join(f"{c['freq']:.0f}Hz" for c in lad))
        print(f"  {'':22}{'guard off':>12}{'guard on':>12}{'delta':>9}")
        for k, lbl in (("peak", "peak mic level dB"), ("hf", "HF 2-16k dB"), ("lf", "LF 40-400 dB")):
            print(f"  {lbl:>22}{u[k]:>12.1f}{g[k]:>12.1f}{g[k]-u[k]:>+9.1f}")
        gd = g["guard"]
        print(f"  {'detections':>22}{'-':>12}{gd.events:>12}")
        nn = gd.notches()
        print(f"  {'filters placed':>22}{'-':>12}{len(nn):>12}")
        if nn:
            print("      " + ", ".join(f"{f:.0f}Hz {d:+.0f}dB" for f, d in nn[:6]))
        print()
