"""
make_fixtures.py — cut the real howls out of the flight recordings.

Six recordings of real feedback existed and no test had ever been shown one.
This turns every real howl - and every needle that turned out to be the singer
- into a short excerpt the ship gate replays every time (replay_gate.py).

Each excerpt starts two seconds before the howl is visible on a display, so
the detector meets it the way it would live: rising out of whatever else the
room was doing. Mono, 16-bit, the engine's rate. Whatever was already sounding
when the excerpt begins is room furniture to a fresh detector, which is the
one way an excerpt differs from the live run; the howls themselves arrive
inside it.

    python3 make_fixtures.py        # -> fixtures/*.wav + fixtures/manifest.json
"""
import glob, json, os, sys, wave
import numpy as np

HERE = os.path.dirname(os.path.abspath(__file__)); sys.path.insert(0, HERE)
from replay import Replay, load_wav, segments, LOGS

OUT = os.path.join(HERE, "fixtures")
LEAD, TAIL, CAP = 2.0, 0.5, 8.0      # seconds before the needle, after it, and at most


def write16(path, x, sr):
    w = wave.open(path, "wb"); w.setnchannels(1); w.setsampwidth(2); w.setframerate(sr)
    w.writeframes((np.clip(x, -1.0, 1.0) * 32767.0).astype("<i2").tobytes()); w.close()


if __name__ == "__main__":
    os.makedirs(OUT, exist_ok=True)
    manifest = []
    for p in sorted(glob.glob(os.path.join(LOGS, "audio-*.wav"))):
        x, sr = load_wav(p)
        r = Replay(sr); r.run(x)
        stamp = os.path.basename(p)[6:21]                      # 20260927-174547
        for sg in segments(r.windows):
            t0, t1 = sg["t0"], sg["t1"]
            a = max(0.0, t0 - LEAD); b = min(len(x) / sr, t1 + TAIL, a + CAP)
            kind = "voice" if sg["voice"] else "howl"
            name = f"{kind}-{stamp}-{t0:04.0f}s-{sg['f']:.0f}Hz.wav"
            write16(os.path.join(OUT, name), x[int(a * sr):int(b * sr)], sr)
            manifest.append(dict(file=name, kind=kind, hz=round(sg["f"], 1),
                                 onset=round(t0 - a, 2), end=round(min(t1, b) - a, 2),
                                 peak=round(sg["peak"], 1), source=os.path.basename(p),
                                 source_t0=round(t0, 2)))
            print(f"  {name:<44} {kind:<5} onset {t0 - a:4.1f}s  {b - a:4.1f}s long")
    json.dump(manifest, open(os.path.join(OUT, "manifest.json"), "w"), indent=1)
    total = sum(os.path.getsize(os.path.join(OUT, m["file"])) for m in manifest)
    print(f"\n{len(manifest)} fixtures ({sum(1 for m in manifest if m['kind']=='howl')} howls, "
          f"{sum(1 for m in manifest if m['kind']=='voice')} voice), {total/1e6:.1f} MB -> {OUT}")
