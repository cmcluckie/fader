"""
make_voices.py — cut real singing out of the rig's flight recordings.

Writes voices/local/<name>/*.wav for voices.py. That folder is git-ignored: the
repository is public, and a recording of a person stays on his own machine
unless he says otherwise. Run this on the machine that has the recordings.

What these are, and are not. They are the microphone as the guard heard it
(channel 0, before the filters), in stretches where somebody was singing and
nothing was howling. They are NOT a dry studio vocal: the PA was on and the
loop was closed, so each one carries a little of that room's regeneration, and
a few carry a short-lived line near 9.7-10 kHz where that room rang. The
stretches below are the ones with the least of it (no needle above 4 kHz
lasting more than two analysis frames). Clean singing recorded with the PA
muted is on the list for the studio session (docs/studio-session.md) and
replaces these when it exists.

    python3 make_voices.py            # cut the standard set
    python3 make_voices.py --list     # show what would be cut, and each stretch's needle score
"""
import os, sys, wave
import numpy as np

HERE = os.path.dirname(os.path.abspath(__file__))
LOGS = os.path.expanduser("~/Documents/FeedbackKiller/logs")
OUT = os.path.join(HERE, "voices", "local")

# name -> (recording, [(start_s, end_s), ...])
SETS = {
    "rig-0927": ("audio-20260927-174547.wav", [(58, 68), (71, 77), (78, 83), (95, 101), (212, 220)]),
}


def load_ch0(path):
    w = wave.open(path); n = w.getnframes(); sr = w.getframerate(); ch = w.getnchannels(); sw = w.getsampwidth()
    a = np.frombuffer(w.readframes(n), dtype=np.uint8).reshape(-1, sw * ch)
    b = a[:, 0:3].astype(np.int32); v = b[:, 0] | (b[:, 1] << 8) | (b[:, 2] << 16)
    return np.where(v & 0x800000, v - (1 << 24), v).astype(np.float64) / 8388608.0, sr


def needle(x, sr):
    """Longest run of frames with a narrow line above 4 kHz standing 25 dB over
    its neighbourhood: how much proto-ring a stretch carries."""
    N, hop = 2048, 1024; win = np.hanning(N); f = np.fft.rfftfreq(N, 1 / sr); sel = np.where(f >= 4000)[0]
    run = best = 0
    for i in range(0, len(x) - N, hop):
        S = 20 * np.log10(np.abs(np.fft.rfft(x[i:i + N] * win)) * 2 / N + 1e-12)
        j = sel[np.argmax(S[sel])]
        hit = S[j] - np.median(S[max(0, j - 20):j + 21]) >= 25 and S[j] > -70
        run = run + 1 if hit else 0; best = max(best, run)
    return best


if __name__ == "__main__":
    import soundfile as sf
    for name, (rec, cuts) in SETS.items():
        path = os.path.join(LOGS, rec)
        if not os.path.exists(path):
            print(f"{name}: {rec} is not on this machine - skipped"); continue
        x, sr = load_ch0(path)
        os.makedirs(os.path.join(OUT, name), exist_ok=True)
        for a, b in cuts:
            s = x[a * sr:b * sr]
            rms = 20 * np.log10(np.sqrt(np.mean(s ** 2)) + 1e-12)
            print(f"{name}  {a:4d}-{b:<4d} s  rms {rms:6.1f} dBFS  needle run {needle(s, sr)} frame(s)")
            if "--list" not in sys.argv:
                sf.write(os.path.join(OUT, name, f"{a:04d}-{b:04d}.wav"), s, sr, subtype="PCM_24")
    if "--list" not in sys.argv: print("written under", OUT)
