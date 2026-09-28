"""
precut_test.py — does predicting the ladder beat reacting to it?

FEEDBACK_THEORY §7 claims the right move is to pre-cut the predicted candidates
and then notch what rings, rather than chasing what is already audible. That is
a strategy claim, and until now neither side could test it: the theory sim had
no real detector, and our rig had no phase condition to predict FROM.

Three arms, everything else identical:

    none     no guard at all                       (the floor)
    react    the shipping guard, reacting          (what ships today)
    precut   the same guard, plus the top-N phase-aligned candidates armed
             shallow at t=0                        (what section 7 argues for)

Scored on what the microphone actually heard, because that is what the room
sounded like.
"""
import ctypes, os, sys, functools
print = functools.partial(print, flush=True)
import numpy as np

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from feedback_sim import FeedbackSim, db
from fk_in_loop import FkGuard, band_db

ROOM = dict(dims=(9, 7, 3.2), rt60=0.7, speaker_pos=(1.0, 3.5, 1.7))


def run(arm, margin_db, seconds, precut_n=4, precut_db=-8.0, mic_path=None):
    sim = FeedbackSim(fs=44100)
    sim.build_room(mic_path=mic_path or [(0.0, (4.0, 3.5, 1.6))], **ROOM)
    sim.set_gain_margin(margin_db)
    ladder = sim.predicted_howls(top=12)

    guard = None
    if arm != "none":
        guard = FkGuard(sim.fs, sim.block)
        if arm == "hidden":
            # Section 7's ACTUAL advice, and the compression identity's target:
            # the candidates that are being held down right now by the winner's
            # saturation, i.e. 0 < m(f) < m(winner). Those are exactly the ones
            # that get unmasked the moment we kill the spike.
            # The ladder is ranked by GROWTH, and growth is dominated by the
            # short loop delays up top - so its first dozen entries are all HF
            # and contain no LF candidate at all. The hidden howls have to be
            # searched for in the full list, not the head of it. Getting this
            # wrong made two earlier arms test nothing.
            full = sim.predicted_howls(top=400)
            win = max(c["margin_db"] for c in full)
            hidden = [c for c in full if 0.0 < c["margin_db"] < win and c["freq"] < 400.0]
            hidden.sort(key=lambda c: -c["margin_db"])
            for c in hidden[:8]:
                guard.lib.fk_place.argtypes = [ctypes.c_void_p, ctypes.c_float, ctypes.c_float]
                guard.lib.fk_place.restype = ctypes.c_int
                guard.lib.fk_place(guard.h, float(c["freq"]), float(precut_db))
        if arm == "precut":
            for c in ladder[:precut_n]:
                guard.lib.fk_place.argtypes = [ctypes.c_void_p, ctypes.c_float, ctypes.c_float]
                guard.lib.fk_place.restype = ctypes.c_int
                guard.lib.fk_place(guard.h, float(c["freq"]), float(precut_db))
        sim.external_eq = guard

    out, mic, ev = sim.run(seconds=seconds, seed="click")
    return dict(arm=arm, mic=mic, fs=sim.fs, ladder=ladder, guard=guard)


def score(r):
    mic, fs = r["mic"], r["fs"]
    return dict(
        hf=band_db(mic, fs, 2000, 16000),
        lf=band_db(mic, fs, 40, 400),
        peak=float(db(np.max(np.abs(mic)))),
        rms=float(db(np.sqrt(np.mean(mic ** 2)))),
        filters=len(r["guard"].notches()) if r["guard"] else 0,
        events=r["guard"].events if r["guard"] else 0,
    )


if __name__ == "__main__":
    for margin in (9.0,):
        print(f"\n=== gain margin +{margin:.0f} dB, 12 s, click seed ===")
        rows = {}
        for arm in ("none", "react", "precut", "hidden"):
            rows[arm] = score(run(arm, margin, 12.0))
        print(f"  {'':10}{'HF 2-16k':>10}{'LF 40-400':>11}{'peak':>8}{'rms':>8}{'filters':>9}{'events':>8}")
        for arm in ("none", "react", "precut", "hidden"):
            s = rows[arm]
            print(f"  {arm:<10}{s['hf']:>10.1f}{s['lf']:>11.1f}{s['peak']:>8.1f}"
                  f"{s['rms']:>8.1f}{s['filters']:>9}{s['events']:>8}")
        d = rows["precut"]["hf"] - rows["react"]["hf"]
        print(f"  -> precut vs react, HF: {d:+.1f} dB   rms: "
              f"{rows['precut']['rms'] - rows['react']['rms']:+.1f} dB")
