"""
replay.py — the real detector over the real recordings.

Six recordings of real feedback exist and, until this file, no test had ever
been shown one. Every layer - unit, fuzz, closed-loop - runs on synthesised
signals. This runs the shipping detector and bank (fk-loopdsp, the same six
lines as the engine) over the flight recorder's pre-notch channel and asks,
for every howl a human would see on the spectrum:

    was it detected, how long did that take, what did the detector SAY when
    it declined, and did the filter on it hold or bleed?

The rejections are the point. The shipping app keeps them in memory and only
writes them to disk when the operator presses a button; three presses on
2026-09-28 wrote three files containing zero rejections. Offline, every one is
kept.

    python3 replay.py                       # every recording
    python3 replay.py path/to/audio.wav     # one

Caveats, stated so they are not forgotten:
  - the bank starts EMPTY. A recording that begins mid-howl (2026-09-30) tests
    the "already present when analysis started" path, not the live failure,
    where a filter had been placed earlier and then bled. Both are real holes.
  - detector params are the engine defaults, not the rig's audio.json floor.
"""
import ctypes, glob, os, sys, wave
from collections import Counter
import numpy as np

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from fk_in_loop import FkGuard

LOGS = os.path.expanduser("~/Documents/FeedbackKiller/logs")
REASON = {0: "none", 1: "harmonic", 2: "unstable", 3: "no-growth", 4: "vibrato", 5: "drifting"}
PATH   = {1: "growth", 2: "sustain", 3: "escalation", 4: "plateau"}

# What a human calls a howl on the display: one narrow needle carrying most of
# the top end's energy, and loud. Levels are 10*log10 of FFT power at W=8192;
# the rig's howls read +35..+45 here, the noise floor about -90.
HOWL_CONC  = 0.5
HOWL_LEVEL = 5.0     # ~ -54 dBFS: a needle a person notices on the display
WIN, HOP   = 8192, 0.25


def load_wav(path):
    w = wave.open(path); n = w.getnframes(); sr = w.getframerate(); ch = w.getnchannels(); sw = w.getsampwidth()
    raw = w.readframes(n); a = np.frombuffer(raw, dtype=np.uint8).reshape(-1, sw * ch)
    b = a[:, 0:3].astype(np.int32); v = b[:, 0] | (b[:, 1] << 8) | (b[:, 2] << 16)   # ch0 = pre-notch mic
    return np.where(v & 0x800000, v - (1 << 24), v).astype(np.float64) / 8388608.0, sr


class Replay:
    def __init__(self, sr, block=256):
        # The rig's dial: Max Cut -18, so soft -12, hard -18, emergency -45.
        self.g = FkGuard(sr, block, caps=(-12.0, -18.0, -45.0))
        lib = self.g.lib
        FP, IP, DP = (ctypes.POINTER(t) for t in (ctypes.c_float, ctypes.c_int, ctypes.c_double))
        lib.fk_pop_event.restype = ctypes.c_int;  lib.fk_pop_event.argtypes  = [ctypes.c_void_p, FP, FP, IP, IP, DP]
        lib.fk_pop_reject.restype = ctypes.c_int; lib.fk_pop_reject.argtypes = [ctypes.c_void_p, FP, FP, IP, IP, DP]
        # A recording cannot respond to a cut, so against one the not-in-the-loop
        # verdict fires by construction and sawtooths every filter between the
        # dial and emergency depth. Off here, so holding can be measured.
        lib.fk_set_loop_verdict.argtypes = [ctypes.c_void_p, ctypes.c_int]
        lib.fk_set_loop_verdict(self.g.h, 0)
        self.block, self.sr = block, sr
        self.events, self.rejects, self.windows = [], [], []

    def _drain(self):
        lib, h = self.g.lib, self.g.h
        hz, lv = ctypes.c_float(), ctypes.c_float(); a, b = ctypes.c_int(), ctypes.c_int(); t = ctypes.c_double()
        while lib.fk_pop_event(h, hz, lv, a, b, t):
            self.events.append((t.value, hz.value, lv.value, a.value, b.value))
        while lib.fk_pop_reject(h, hz, lv, a, b, t):
            self.rejects.append((t.value, hz.value, lv.value, a.value, b.value))

    def run(self, x):
        sr, B = self.sr, self.block
        win = np.hanning(WIN); f = np.fft.rfftfreq(WIN, 1 / sr); top = f >= 1000.0
        next_win = WIN / sr
        for s in range(0, len(x) - B, B):
            self.g.process(x[s:s + B])
            self._drain()
            t = (s + B) / sr
            if t >= next_win:
                next_win += HOP
                P = np.abs(np.fft.rfft(x[s + B - WIN:s + B] * win)) ** 2
                band = P[top]
                if band.sum() > 0:
                    i = int(np.argmax(band)); fpk = float(f[top][i])
                    lvl = 10 * np.log10(band[i] + 1e-20)
                    conc = float(np.sort(band)[::-1][:5].sum() / band.sum())
                    cut = float(self.g.lib.fk_cut_at(self.g.h, fpk))
                    self.windows.append((t, fpk, lvl, conc, cut))


def segments(windows):
    """Group howl-like windows into rings: same frequency within 3%, gaps under 0.75 s."""
    segs, cur = [], None
    for t, fpk, lvl, conc, cut in windows:
        howl = conc >= HOWL_CONC and lvl >= HOWL_LEVEL
        if howl and cur and abs(fpk - cur["f"]) / cur["f"] <= 0.03 and t - cur["t1"] <= 0.75:
            cur["t1"] = t; cur["peak"] = max(cur["peak"], lvl); cur["cuts"].append((t, cut)); cur["n"] += 1
            cur["f"] = (cur["f"] * (cur["n"] - 1) + fpk) / cur["n"]
        elif howl:
            if cur: segs.append(cur)
            cur = dict(t0=t, t1=t, f=fpk, peak=lvl, cuts=[(t, cut)], n=1)
        elif cur and t - cur["t1"] > 0.75:
            segs.append(cur); cur = None
    if cur: segs.append(cur)
    return [s for s in segs if s["t1"] - s["t0"] >= 0.25]


def report(path, until=None):
    x, sr = load_wav(path)
    if until: x = x[: int(until * sr)]
    r = Replay(sr); r.run(x)
    segs = segments(r.windows)
    name = os.path.basename(path)
    print(f"\n{name}   {len(x)/sr:.1f} s   detector said: {len(r.events)} events, {len(r.rejects)} rejections")
    if not segs:
        print("   no howl-like segments (nothing narrow, loud and sustained above 1 kHz)")
    rows = []
    for sg in segs:
        f0, t0, t1 = sg["f"], sg["t0"], sg["t1"]
        near = lambda hz, tol: abs(hz - f0) / f0 <= tol
        ev = [e for e in r.events if near(e[1], 0.03) and t0 - 0.5 <= e[0] <= t1]
        rj = [q for q in r.rejects if near(q[1], 0.05) and t0 - 0.5 <= q[0] <= t1]
        first = min((e[0] for e in ev), default=None)
        lat = (first - t0) if first is not None else None
        deepest = min(c for _, c in sg["cuts"]); last = sg["cuts"][-1][1]
        why = Counter(REASON.get(q[3], str(q[3])) for q in rj)
        det = f"{lat*1000:+.0f} ms" if lat is not None else "NEVER"
        if ev:
            lv = sorted(e[2] for e in ev); grow = sum(1 for e in ev if e[3])
            print(f"      detector-reported level on it: min {lv[0]:.1f}  median {lv[len(lv)//2]:.1f}  max {lv[-1]:.1f} dB"
                  f"   ({grow} of {len(ev)} flagged growing; paths {dict(Counter(PATH.get(e[4], e[4]) for e in ev))})")
            tl = sg["cuts"][:: max(1, len(sg["cuts"]) // 8)]
            print("      cut over time: " + "  ".join(f"{t:.1f}s:{c:.0f}" for t, c in tl))
        print(f"   howl {f0:7.0f} Hz  {t0:6.1f}-{t1:6.1f} s  peak {sg['peak']:+5.1f}   "
              f"detected {det:>9}  events {len(ev):3d}   cut deepest {deepest:6.1f} → end {last:6.1f}   "
              f"declined: {dict(why) if why else '-'}")
        rows.append((name, f0, t1 - t0, lat, deepest, last, len(ev), sum(why.values())))
    if r.rejects and not segs:
        print("   rejections overall:", dict(Counter(REASON.get(q[3], str(q[3])) for q in r.rejects)))
    return rows


if __name__ == "__main__":
    args = sys.argv[1:]
    until = None
    if "--until" in args:
        i = args.index("--until"); until = float(args[i + 1]); del args[i:i + 2]
    paths = args or sorted(glob.glob(os.path.join(LOGS, "audio-*.wav")))
    allrows = []
    for p in paths:
        allrows += report(p, until)
    if allrows:
        n = len(allrows); seen = [r for r in allrows if r[3] is not None]
        lats = sorted(r[3] for r in seen)
        never_cut = sum(1 for r in allrows if r[4] > -6.0)
        bled = sum(1 for r in allrows if r[4] <= -12.0 and r[5] > r[4] + 8.0)
        print(f"\n{'='*78}")
        print(f"{n} howls across {len(paths)} recordings")
        print(f"   detected at all : {len(seen)}/{n}")
        if lats: print(f"   latency         : median {1000*lats[len(lats)//2]:+.0f} ms, worst {1000*lats[-1]:+.0f} ms")
        print(f"   never cut > 6 dB: {never_cut}")
        print(f"   cut then bled away (lost > 8 dB of a >= 12 dB notch while still howling): {bled}")
