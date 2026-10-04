#!/usr/bin/env python3
"""
listen.py — put a sound to the number.

"Voice change 60 %" is arithmetic until someone has heard it. This renders the
same sung phrase several ways, each file named with its measured voice change,
so the scale in quality.py can be checked against a pair of ears
(docs/TODO-AS-BUILT.md, 8.11).

    python3 listen.py                 # every voice on this machine -> out/listen/
    python3 listen.py --voice synth

For each voice:
    1-clean                the voice as it went in
    2-today                through this build's guard, the rig's settings, NO loop - nothing ringing
    3-today-gentle         the same with Attack "gentle" (ten frames, first cut -6 dB)
    4-one-filter           the clean voice with a single -18 dB filter at 3 kHz: what about 5 % is
    5-level-2db            the clean voice 2 dB quieter: what about 15 % is, if it were only level

and for a voice cut from a flight recording, where the recording has it:
    6-live-<date>          what the guard of THAT day actually sent to the speakers

Everything is written at one fixed gain, so the files can be compared by ear
without reaching for the volume. Real voices are written beside the synthetic
one in out/listen/, which is git-ignored.
"""
import os, sys
import numpy as np
from scipy import signal

HERE = os.path.dirname(os.path.abspath(__file__)); sys.path.insert(0, HERE)
import quality as Q
from voices import voice, available, FS
from feedback_sim import peaking_eq

OUT = os.path.join(HERE, "out", "listen")
GAIN = 10 ** (14.0 / 20.0)            # singing at -30 dBFS is quiet in a file; one gain for every file


def through_guard(x, override=""):
    old = os.environ.get("FK_OVERRIDE")
    os.environ["FK_OVERRIDE"] = override
    try:
        loop = Q._loop("rig")
        src = np.concatenate([np.zeros(int(Q.PREROLL_S * FS)), x])
        r = Q.run_loop(loop, src, None, True)
    finally:
        if old is None: os.environ.pop("FK_OVERRIDE", None)
        else: os.environ["FK_OVERRIDE"] = old
    n = int(Q.PREROLL_S * FS)
    return r["xout"][n:], r["xin"][n:]


def live_output(name):
    """What that day's guard sent out, for a voice cut from a recording: the same stretches, channel 1."""
    import make_voices as mv
    if name not in mv.SETS: return None
    rec, cuts = mv.SETS[name]
    path = os.path.join(mv.LOGS, rec)
    if not os.path.exists(path): return None
    import wave
    w = wave.open(path); n = w.getnframes(); sr = w.getframerate(); ch = w.getnchannels(); sw = w.getsampwidth()
    a = np.frombuffer(w.readframes(n), dtype=np.uint8).reshape(-1, sw * ch)
    def chan(i):
        b = a[:, i * 3:(i + 1) * 3].astype(np.int32); v = b[:, 0] | (b[:, 1] << 8) | (b[:, 2] << 16)
        return np.where(v & 0x800000, v - (1 << 24), v).astype(np.float64) / 8388608.0
    x0, x1 = chan(0), chan(1)
    pre, post = [], []
    m = int(0.02 * sr); fade = 0.5 - 0.5 * np.cos(np.pi * np.arange(m) / m)
    for lo, hi in cuts:
        for src, dst in ((x0, pre), (x1, post)):
            s = src[lo * sr:hi * sr].copy(); s[:m] *= fade; s[-m:] *= fade[::-1]
            dst += [s, np.zeros(int(0.4 * sr))]
    return np.concatenate(pre), np.concatenate(post), rec[6:14]


def render(name):
    import soundfile as sf
    os.makedirs(OUT, exist_ok=True)
    x, info = voice(name)
    made = []
    def put(tag, y, ref):
        pct = Q.voice_change(y, ref[:len(y)], FS)["change_pct"]
        fn = os.path.join(OUT, f"{name}-{tag}-{pct:.0f}pct.wav")
        sf.write(fn, np.clip(y * GAIN, -1, 1), FS, subtype="PCM_16"); made.append((fn, pct)); return pct
    put("1-clean", x, x)
    y, xin = through_guard(x);                 put("2-today", y, xin)
    y, xin = through_guard(x, "persist_frames=10,first_cut_db=-6"); put("3-today-gentle", y, xin)
    b, a = peaking_eq(FS, 3000.0, 12.0, -18.0); put("4-one-filter", signal.lfilter(b, a, x), x)
    put("5-level-2db", x * 10 ** (-2.0 / 20.0), x)
    live = live_output(name)
    if live is not None:
        pre, post, day = live
        k = 10 ** (Q.SING_DB / 20.0) / np.sqrt(np.mean(pre[np.abs(pre) > 1e-4] ** 2)) * 0.9
        pct = Q.voice_change(post, pre, FS)["change_pct"]
        fn = os.path.join(OUT, f"{name}-6-live-{day}-{pct:.0f}pct.wav")
        sf.write(fn, np.clip(post * k * GAIN, -1, 1), FS, subtype="PCM_16"); made.append((fn, pct))
    return made


if __name__ == "__main__":
    a = sys.argv[1:]
    names = [a[a.index("--voice") + 1]] if "--voice" in a else available()
    for n in names:
        for fn, pct in render(n):
            print(f"{pct:5.1f} %   {os.path.relpath(fn, HERE)}")
