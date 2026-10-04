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
import ctypes, json, os, sys, functools
print = functools.partial(print, flush=True)
import numpy as np

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from feedback_sim import FeedbackSim, db

LIB = os.environ.get("FK_LOOPDSP", "/Users/cmcluckie/Code/Fader/engine/build/libfk-loopdsp.dylib")
AUDIO_JSON = os.path.expanduser("~/Documents/FeedbackKiller/audio.json")

# Mirror of FeedbackController.PushAttack: the app's Attack level sets the
# detector's confirm frames and the bank's first strike together.
ATTACK = {0: (10, -6.0), 1: (6, -12.0), 2: (6, -18.0)}

# Mirror of engine/Source/EngineDefaults.h - what the engine runs until the app
# speaks. Keep the two in step; the gate is only honest while they agree.
ENGINE = dict(notch_q=12.0, first_cut_db=-6.0, max_cut_db=-18.0, persist_frames=6,
              min_hz=200.0, max_hz=16000.0, floor_db=-70.0, input_gate_db=-90.0, prominence_db=12.0)


def rig_config(path=AUDIO_JSON):
    """What the app sends the engine at startup, read from the rig's audio.json."""
    cfg = {}
    try:
        with open(path) as fh: cfg = json.load(fh)
    except (OSError, ValueError):
        pass
    frames, first = ATTACK.get(int(cfg.get("Attack", 2)), ATTACK[2])
    out = dict(ENGINE)
    out.update(first_cut_db=first, max_cut_db=float(cfg.get("MaxCutDb", -18.0)),
               persist_frames=frames, min_hz=float(cfg.get("MinHz", 40.0)), max_hz=float(cfg.get("MaxHz", 18000.0)),
               floor_db=float(cfg.get("FloorDb", -95.0)), budget=float(cfg.get("HarmBudget", 0.0)))
    return out


class FkGuard:
    """Quacks like feedback_sim's StatefulSOS, but is 4000 lines of C++."""

    def __init__(self, fs, block=64, caps=None, disconnected=False, config="rig"):
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
        # The rig's settings, not the library's defaults. The gate ran the bank
        # with its own defaults for a week - merge window Q 25, first strike -12,
        # caps -18/-24 - while the rig ran Q 12, -18, -12/-18. It passed on a
        # machine the room never saw. "rig" reads ~/Documents/FeedbackKiller/
        # audio.json (falling back to the app's Attack-2 numbers); "engine" is
        # the engine before the app has spoken; None leaves the library alone.
        config = os.environ.get("FK_CONFIG", config)      # rig | engine | none
        if config not in (None, "none"):
            cfg = rig_config() if config == "rig" else dict(ENGINE)
            for kv in filter(None, os.environ.get("FK_OVERRIDE", "").split(",")):   # experiments
                k, v = kv.split("="); cfg[k] = float(v) if k != "persist_frames" else int(v)
            self.configure(**cfg)
        if caps:
            self.lib.fk_set_caps.argtypes = [ctypes.c_void_p, ctypes.c_float,
                                             ctypes.c_float, ctypes.c_float]
            self.lib.fk_set_caps(self.h, *[float(c) for c in caps])
        self.disconnected = disconnected
        self.zi = np.zeros((1, 2))     # the sim pokes this; harmless

    def configure(self, notch_q=12.0, first_cut_db=-6.0, max_cut_db=-18.0, persist_frames=6,
                  min_hz=200.0, max_hz=16000.0, floor_db=-70.0, budget=None,
                  input_gate_db=-90.0, prominence_db=12.0):
        self.lib.fk_configure.argtypes = [ctypes.c_void_p, ctypes.c_float, ctypes.c_float, ctypes.c_float,
                                          ctypes.c_int, ctypes.c_float, ctypes.c_float, ctypes.c_float,
                                          ctypes.c_float, ctypes.c_float]
        self.lib.fk_configure(self.h, float(notch_q), float(first_cut_db), float(max_cut_db),
                              int(persist_frames), float(min_hz), float(max_hz), float(floor_db),
                              float(input_gate_db), float(prominence_db))
        if budget is not None:
            self.lib.fk_set_budget.argtypes = [ctypes.c_void_p, ctypes.c_float]
            self.lib.fk_set_budget(self.h, float(budget))
        self.config = dict(notch_q=notch_q, first_cut_db=first_cut_db, max_cut_db=max_cut_db,
                           persist_frames=persist_frames, min_hz=min_hz, max_hz=max_hz, floor_db=floor_db, budget=budget,
                           input_gate_db=input_gate_db, prominence_db=prominence_db)

    def process(self, x):
        buf = np.ascontiguousarray(x, dtype=np.float32)
        self.lib.fk_process(self.h, buf, len(buf))
        if self.disconnected:
            # The guard sees the microphone and does all its work, but the
            # console is feeding the wedge from upstream of our return, so none
            # of it reaches the loop. This is not a hypothetical: it happened at
            # the rig on 2026-09-28 and cost a session. Every cut the guard makes
            # is real and completely without effect, which is the one condition
            # under which "cut harder" is exactly the wrong answer.
            return np.asarray(x, dtype=np.float64)
        return buf.astype(np.float64)

    @property
    def events(self):
        return int(self.lib.fk_event_count(self.h))

    def notches(self, cap=48):
        f = np.zeros(cap, np.float32); d = np.zeros(cap, np.float32)
        n = self.lib.fk_notches(self.h, f.ctypes.data_as(ctypes.POINTER(ctypes.c_float)),
                                d.ctypes.data_as(ctypes.POINTER(ctypes.c_float)), cap)
        return list(zip(f[:n].tolist(), d[:n].tolist()))

    def pin(self, hz, depth_db, q):
        """Place a filter in advance from a loop measurement (NotchBank::placePinned)."""
        self.lib.fk_pin.argtypes = [ctypes.c_void_p, ctypes.c_float, ctypes.c_float, ctypes.c_float]
        self.lib.fk_pin.restype = ctypes.c_int
        return int(self.lib.fk_pin(self.h, float(hz), float(depth_db), float(q)))

    def unpin(self):
        self.lib.fk_unpin.argtypes = [ctypes.c_void_p]
        self.lib.fk_unpin(self.h)

    def has(self, symbol):
        """Is this function in the loaded library? A past build's library
        (measure_version.py) lacks whatever was added after it."""
        try: getattr(self.lib, symbol); return True
        except AttributeError: return False

    def set_rescue(self, on):
        """The broadband rescue duck (RescueDuck.h): on by default, as in the engine."""
        if not self.has("fk_set_rescue"): return          # a build from before the duck
        self.lib.fk_set_rescue.argtypes = [ctypes.c_void_p, ctypes.c_int]
        self.lib.fk_set_rescue(self.h, 1 if on else 0)

    def rescue(self):
        """What the duck has done: triggers, episodes, deepest and current depth, last cause."""
        if not self.has("fk_rescue"):
            return dict(triggers=0, episodes=0, deepest_db=0.0, depth_db=0.0, hz=0.0, level_db=0.0, reason="", futile=0)
        out = (ctypes.c_float * 8)()
        self.lib.fk_rescue.argtypes = [ctypes.c_void_p, ctypes.POINTER(ctypes.c_float)]
        self.lib.fk_rescue(self.h, out)
        return dict(triggers=int(out[0]), episodes=int(out[1]), deepest_db=float(out[2]), depth_db=float(out[3]),
                    hz=float(out[4]), level_db=float(out[5]), reason={1: "runaway", 2: "loud line"}.get(int(out[6]), ""),
                    futile=int(out[7]))

    def not_in_loop(self):
        self.lib.fk_not_in_loop.restype = ctypes.c_int
        self.lib.fk_not_in_loop.argtypes = [ctypes.c_void_p]
        return int(self.lib.fk_not_in_loop(self.h))

    def deepest_db(self):
        return min([d for _, d in self.notches()], default=0.0)

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
