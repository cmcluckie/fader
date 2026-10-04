#!/usr/bin/env python3
"""
What did the guard take from the voice in the room?

Every flight recording (Capture, or a sweep) has the microphone before the
filters on the left and after them on the right. This scores the difference,
with the same ruler the simulated tests use (engine/tests/sim/quality.py), and
logs it as a LIVE result.

    scripts/session_score.py ~/Documents/FeedbackKiller/logs/audio-20261004-084207.wav
    scripts/session_score.py rec.wav --from 58 --to 68        # one stretch
    scripts/session_score.py rec.wav --version f5c5f0b        # the build that made it, if the stamp is not current
    scripts/session_score.py rec.wav --no-record

It measures what the guard removed. It cannot measure what the loop added -
live there is no clean voice to compare with - so it is a lower bound on the
damage, and exactly the figure for "nothing ringing, guard on".
"""
import argparse, datetime, os, re, sys, wave
import numpy as np

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, os.path.join(HERE, "..", "engine", "tests", "sim"))
import quality as Q
import ledger


def load2(path):
    w = wave.open(path); n = w.getnframes(); sr = w.getframerate(); ch = w.getnchannels(); sw = w.getsampwidth()
    raw = np.frombuffer(w.readframes(n), dtype=np.uint8).reshape(-1, sw * ch)
    out = []
    for i in range(2):
        if sw == 3:
            b = raw[:, i * 3:(i + 1) * 3].astype(np.int32); v = b[:, 0] | (b[:, 1] << 8) | (b[:, 2] << 16)
            out.append(np.where(v & 0x800000, v - (1 << 24), v).astype(np.float64) / 8388608.0)
        else:
            out.append(np.frombuffer(raw[:, i * 2:(i + 1) * 2].tobytes(), dtype="<i2").astype(np.float64) / 32768.0)
    return out[0], out[1], sr


def score(path, t0=None, t1=None):
    x0, x1, sr = load2(path)
    if t0 is not None: x0, x1 = x0[int(t0 * sr):int(t1 * sr)], x1[int(t0 * sr):int(t1 * sr)]
    v = Q.voice_change(x1, x0, sr)
    L = int(0.02 * sr); k = len(x0) // L
    fr = 10 * np.log10(np.mean(x0[:k * L].reshape(k, L) ** 2, axis=1) + 1e-20)
    voiced = fr > Q.SING_DB - 25.0
    return dict(taken_pct=v["removed_pct"], added_pct=v["added_pct"], worst_second_pct=v["worst_pct"], distance_db=v["distance_db"],
                cut_1k_4k_db=Q.band_gain(x0, x1, sr, 1000, 4000), cut_4k_16k_db=Q.band_gain(x0, x1, sr, 4000, 16000),
                seconds=len(x0) / sr, voiced_seconds=float(np.sum(voiced) * 0.02),
                voice_rms_db=float(10 * np.log10(np.mean(10 ** (fr[voiced] / 10.0)))) if np.any(voiced) else None)


if __name__ == "__main__":
    ap = argparse.ArgumentParser()
    ap.add_argument("wav"); ap.add_argument("--from", dest="t0", type=float); ap.add_argument("--to", dest="t1", type=float)
    ap.add_argument("--version"); ap.add_argument("--note"); ap.add_argument("--no-record", action="store_true")
    a = ap.parse_args()
    m = score(a.wav, a.t0, a.t1)
    name = os.path.basename(a.wav)
    print(f"{name}{'' if a.t0 is None else f'  {a.t0:.0f}-{a.t1:.0f} s'}: the guard took {m['taken_pct']:.1f} % of the voice "
          f"(worst second {m['worst_second_pct']:.1f} %), {m['cut_1k_4k_db']:+.1f} dB over 1-4 kHz, {m['cut_4k_16k_db']:+.1f} dB over 4-16 kHz; "
          f"{m['voiced_seconds']:.0f} s with a voice in it")
    if not a.no_record:
        when = None
        t = re.search(r"(\d{8})-(\d{6})", name)
        if t: when = datetime.datetime.strptime(t.group(1) + t.group(2), "%Y%m%d%H%M%S").astimezone().isoformat(timespec="seconds")
        m.update(recording=name, stretch=None if a.t0 is None else f"{a.t0:.0f}-{a.t1:.0f} s")
        ledger.record("session-voice", "live", m, version=a.version, when=when, note=a.note,
                      source="run" if a.version is None else "backfill", force=True)
