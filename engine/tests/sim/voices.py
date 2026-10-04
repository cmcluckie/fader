"""
voices.py — the singers the closed-loop tests use as their source.

A voice cannot be mixed on top of a simulated howl: in a real room the singer is
what the loop feeds on, so the voice has to go IN at the microphone and come
round with everything else (quality.py). This file only supplies the voice.

Two kinds:

  synth   A synthetic singer, generated here. Deterministic, nobody's property,
          and in the repository - so the standard test is the same test on any
          machine. Its score is written to contain the cases that have fooled
          the detector: a dead-straight held note, a scoop up to a loud bright
          one, a bright "ee", sibilants where the rig rings, staccato, a low
          note whose fundamental carries the level.

  local   Real singing, cut from the rig's own flight recordings by
          make_voices.py into voices/local/. Git-ignored: the repository is
          public and a recording of a person stays on his machine unless he
          says otherwise. Present on the rig, absent elsewhere; the tests use
          whatever is there and say which.

A synthetic voice is a controlled probe, not a singer. It tells you a change
moved a number; the real excerpts (and clean studio takes, when they exist) say
whether that number means anything. Both are reported, never averaged together.

Everything is returned at 48 kHz, mono, float64, with the sung parts at
SING_DB rms - where a voice sits at the guard's input on the rig (measured on
the 2026-09-27 session: sixteen sung stretches, -28.5 to -36, median -30.5).
"""
import functools, glob, hashlib, json, os
import numpy as np
from scipy import signal

HERE = os.path.dirname(os.path.abspath(__file__))
LOCAL = os.path.join(HERE, "voices", "local")
CACHE = os.path.join(HERE, "out")
FS = 48000
SING_DB = -30.0

# ----------------------------------------------------------------- the synthetic singer

#           F1    F2    F3    F4    F5       a1   a2    a3    a4    a5
VOWELS = {
    "a": ([730, 1150, 2650, 3300, 4300], [1.0, 0.63, 0.20, 0.16, 0.08]),
    "e": ([520, 1800, 2550, 3350, 4300], [1.0, 0.40, 0.25, 0.16, 0.08]),
    "i": ([300, 2200, 2950, 3500, 4400], [1.0, 0.30, 0.25, 0.18, 0.08]),
    "o": ([500,  880, 2550, 3300, 4300], [1.0, 0.55, 0.12, 0.10, 0.06]),
    "u": ([330,  800, 2400, 3300, 4300], [1.0, 0.35, 0.08, 0.08, 0.05]),
}
BW = np.array([90.0, 110.0, 170.0, 250.0, 300.0])


def hz(name):
    """'A3' -> 220.0"""
    semis = {"C": -9, "D": -7, "E": -5, "F": -4, "G": -2, "A": 0, "B": 2}
    n = semis[name[0]] + (1 if "#" in name else 0) - (1 if "b" in name[1:] else 0)
    return 440.0 * 2 ** ((n + 12 * (int(name[-1]) - 4)) / 12.0)


# The score. Each line: (kind, ...).
#   note  pitch, seconds, vowel, vibrato (0 none .. 1 full), level start, level end (dB re forte), consonant before
#   glide from, to, seconds, vowel, level
#   rest  seconds, breath (True: an audible intake)
SCORE = [
    ("rest", 0.5, False),
    # 1. an ordinary legato phrase with vibrato on the long note
    ("note", "A3", 0.8, "a", 0.3, -4, -3, None),
    ("note", "C4", 0.6, "a", 0.3, -3, -2, None),
    ("note", "E4", 1.2, "a", 1.0, -2, -5, None),
    ("rest", 0.5, True),
    # 2. a dead-straight held note: no vibrato, a slow swell. To a detector this
    #    is the nearest thing a voice does to feedback.
    ("note", "D4", 2.5, "o", 0.0, -8, -1, None),
    ("rest", 0.4, False),
    # 3. sibilants, landing where the rig rings (5-9 kHz)
    ("note", "F4", 0.7, "i", 0.4, -3, -3, "s"),
    ("note", "E4", 0.6, "a", 0.4, -3, -3, "s"),
    ("note", "D4", 0.9, "o", 1.0, -3, -6, "sh"),
    ("rest", 0.5, True),
    # 4. the scoop: slide up an octave into a loud bright note. A scoop from
    #    4723 to 4768 Hz is what the runaway path once mistook for a ring.
    ("glide", "A3", "A4", 0.6, "a", -5),
    ("note", "A4", 1.5, "a", 1.0, -1, 0, None),
    ("rest", 0.6, True),
    # 5. staccato
    ("note", "G4", 0.18, "e", 0.0, -3, -3, "t"), ("rest", 0.12, False),
    ("note", "E4", 0.18, "e", 0.0, -3, -3, "t"), ("rest", 0.12, False),
    ("note", "C4", 0.18, "e", 0.0, -3, -3, "t"), ("rest", 0.12, False),
    ("note", "E4", 0.18, "e", 0.0, -3, -3, "t"), ("rest", 0.12, False),
    ("note", "G4", 0.30, "e", 0.0, -3, -4, "t"),
    ("rest", 0.5, True),
    # 6. a long bright "ee": strong partials at 2-3.5 kHz, straight first,
    #    vibrato arriving late, fading to nothing
    ("note", "F4", 3.0, "i", 0.7, -2, -14, None),
    ("rest", 0.5, True),
    # 7. low: the fundamental carries the level (T41: a bass voice's fundamental was once notched)
    ("note", "G3", 1.0, "u", 0.3, -4, -3, None),
    ("note", "A3", 0.8, "u", 0.3, -3, -3, None),
    ("note", "E3", 1.5, "o", 0.0, -3, -8, None),
    ("rest", 0.6, False),
]


def _envelope(f, F, A, effort):
    """Spectral envelope at frequencies f (..., K) for formants F, A (..., 5).

    Parallel formants (each a Lorentzian), a source term that keeps the lowest
    partials strong, and a roll-off above 4.5 kHz. `effort` (0..1) brightens:
    a loud voice is not just a louder quiet one."""
    f = f[..., None]
    L = 1.0 / np.sqrt(1.0 + ((f - F[..., None, :]) / (BW / 2.0)) ** 2)
    tilt = np.array([0.0, 0.0, 6.0, 6.0, 4.0]) * effort[..., None, None]
    env = np.sum(A[..., None, :] * 10 ** (tilt / 20.0) * L, axis=-1)
    f = f[..., 0]
    return env / np.sqrt(1.0 + (f / 4500.0) ** 2)


def synth_singer(fs=FS, seed=11):
    """The standard synthetic singer: SCORE, rendered. About 21 s."""
    rng = np.random.default_rng(seed)
    cr = 200                                   # control rate, Hz
    hop = fs // cr
    # ---- build control tracks: f0, voiced gain, vowel formants, effort
    f0, gain, Fm, Am, eff = [], [], [], [], []
    bursts = []                                # (start_s, kind)
    breaths = []
    t = 0.0
    prevF = np.array(VOWELS["a"][0], float); prevA = np.array(VOWELS["a"][1], float)
    for ev in SCORE:
        if ev[0] == "rest":
            n = int(round(ev[1] * cr))
            f0 += [0.0] * n; gain += [0.0] * n; eff += [0.0] * n
            Fm += [prevF] * n; Am += [prevA] * n
            if ev[2] and ev[1] >= 0.3: breaths.append((t + ev[1] - 0.28, 0.22))
            t += ev[1]
        elif ev[0] in ("note", "glide"):
            if ev[0] == "note":
                _, p, dur, v, vib, l0, l1, cons = ev
                fa = fb = hz(p)
            else:
                _, pa, pb, dur, v, l0 = ev
                fa, fb, vib, l1, cons = hz(pa), hz(pb), 0.0, l0 + 3.0, None
            if cons:
                clen = {"s": 0.11, "sh": 0.12, "t": 0.03}[cons]
                bursts.append((t, cons))
                n = int(round(clen * cr))
                f0 += [0.0] * n; gain += [0.0] * n; eff += [0.0] * n
                Fm += [prevF] * n; Am += [prevA] * n
                t += clen
            n = int(round(dur * cr)); u = np.arange(n) / cr
            # pitch: a slide in log frequency, a little scoop into each note, vibrato arriving late
            base = fa * (fb / fa) ** (0.5 - 0.5 * np.cos(np.pi * np.clip(u / dur, 0, 1))) if fa != fb else np.full(n, fa)
            scoop = -0.25 * np.exp(-u / 0.035)                         # semitones, gone in ~100 ms
            vdepth = 0.35 * vib * np.clip((u - 0.25) / 0.30, 0, 1)      # semitones peak
            vrate = 5.6 + 0.3 * np.sin(2 * np.pi * 0.31 * (t + u))
            vph = 2 * np.pi * np.cumsum(vrate) / cr
            drift = 0.03 * np.cumsum(rng.standard_normal(n)) / np.sqrt(cr)   # a slow wander of a few cents
            drift -= np.linspace(0, drift[-1], n) * 0.5
            semis = scoop + vdepth * np.sin(vph) + drift
            f0 += list(base * 2 ** (semis / 12.0))
            # level: attack 30 ms, release 60 ms, linear-in-dB swell between
            lev = np.linspace(l0, l1, n)
            env = np.minimum(1.0, u / 0.03) * np.minimum(1.0, (dur - u) / 0.06)
            gain += list(10 ** (lev / 20.0) * np.clip(env, 0, 1))
            eff += list(np.clip(1.0 + lev / 14.0, 0.0, 1.0))
            F, A = np.array(VOWELS[v][0], float), np.array(VOWELS[v][1], float)
            k = np.clip(u / 0.06, 0, 1)[:, None]                         # 60 ms vowel transition
            Fm += list(prevF + (F - prevF) * k); Am += list(prevA + (A - prevA) * k)
            prevF, prevA = F, A
            t += dur
    f0 = np.array(f0); gain = np.array(gain); eff = np.array(eff)
    Fm = np.array(Fm); Am = np.array(Am)
    nc = len(f0); N = nc * hop
    tc = np.arange(nc) * hop; ts = np.arange(N)

    # keep the pitch track continuous through rests so the phase does not jump
    f0c = f0.copy()
    last = f0c[f0c > 0][0]
    for i in range(nc):
        if f0c[i] > 0: last = f0c[i]
        else: f0c[i] = last
    f0s = np.interp(ts, tc, f0c)
    f0s *= 1.0 + 0.0025 * signal.lfilter([1], [1, -0.995], rng.standard_normal(N)) * np.sqrt(1 - 0.995 ** 2)  # jitter
    phase = 2 * np.pi * np.cumsum(f0s) / fs
    gs = np.interp(ts, tc, gain)
    shimmer = 1.0 + 0.03 * signal.lfilter([1], [1, -0.99], rng.standard_normal(N)) * np.sqrt(1 - 0.99 ** 2)

    # ---- harmonics: amplitude tracks at the control rate, interpolated
    x = np.zeros(N)
    K = int(12000.0 / f0c.min())
    for k in range(1, K + 1):
        fk = k * f0c
        if fk.min() > 12000.0: break
        a = _envelope(fk[:, None], Fm, Am, eff)[:, 0] + 0.5 / k ** 1.6       # formants + the source's own low partials
        a = a / np.sqrt(1.0 + (fk / 7000.0) ** 4)                             # the top of a voice is breath, not pitch
        a[fk > 0.45 * fs] = 0.0
        x += np.interp(ts, tc, a) * np.sin(k * phase + rng.uniform(0, 2 * np.pi))
    x *= gs * shimmer

    # ---- breath in the tone: noise shaped like the vowel's top end, riding the voiced gain
    asp = rng.standard_normal(N)
    asp = signal.sosfilt(signal.butter(2, [2500, 11000], "bp", fs=fs, output="sos"), asp)
    x += 0.012 * asp * gs

    # ---- consonants and breaths
    def noise_burst(start, length, band, level, attack=0.01, release=0.02):
        i0 = int(start * fs); n = int(length * fs)
        if i0 + n > N: return
        b = signal.sosfilt(signal.butter(4, band, "bp", fs=fs, output="sos"), rng.standard_normal(n + 2048))[2048:]
        u = np.arange(n) / fs
        env = np.minimum(1.0, u / attack) * np.minimum(1.0, (length - u) / release)
        x[i0:i0 + n] += level * b / (np.sqrt(np.mean(b ** 2)) + 1e-12) * np.clip(env, 0, 1)

    ref = np.sqrt(np.mean(x[gs > 0.5] ** 2))                 # the voice's own level, to hang consonants on
    for start, kind in bursts:
        if kind == "s":   noise_burst(start, 0.11, [4500, 9500], 0.30 * ref)
        elif kind == "sh": noise_burst(start, 0.12, [2200, 6500], 0.35 * ref)
        elif kind == "t":  noise_burst(start, 0.03, [2500, 9000], 0.45 * ref, attack=0.002, release=0.02)
    for start, length in breaths:
        noise_burst(start, length, [700, 4500], 0.05 * ref, attack=0.08, release=0.08)

    sung = gs > 0.3
    x *= 10 ** (SING_DB / 20.0) / np.sqrt(np.mean(x[sung] ** 2))
    return x


# ----------------------------------------------------------------- loading


def _active_rms_db(x, fs):
    L = int(0.02 * fs); k = len(x) // L
    fr = 10 * np.log10(np.mean(x[:k * L].reshape(k, L) ** 2, axis=1) + 1e-20)
    act = fr[fr > fr.max() - 30.0]
    return float(10 * np.log10(np.mean(10 ** (act / 10.0))))


@functools.lru_cache(maxsize=None)
def voice(name="synth"):
    """A named voice at 48 kHz, sung parts at SING_DB. Returns (samples, info)."""
    if name == "synth":
        os.makedirs(CACHE, exist_ok=True)
        key = hashlib.sha1((json.dumps(SCORE) + json.dumps(VOWELS) + "v1").encode()).hexdigest()[:10]
        path = os.path.join(CACHE, f"synth-{key}.npy")
        if os.path.exists(path): x = np.load(path)
        else:
            x = synth_singer(); np.save(path, x)
        return x, dict(name="synth", kind="synthetic", seconds=len(x) / FS, id=key)
    files = sorted(glob.glob(os.path.join(LOCAL, name, "*.wav")))
    if not files:
        raise FileNotFoundError(f"no voice '{name}' (looked in {os.path.join(LOCAL, name)}; run make_voices.py on the rig)")
    import soundfile as sf
    parts, h = [], hashlib.sha1()
    for p in files:
        s, sr = sf.read(p, dtype="float64", always_2d=True)
        s = s[:, 0]
        if sr != FS: s = signal.resample_poly(s, FS, sr)
        m = int(0.02 * FS); w = 0.5 - 0.5 * np.cos(np.pi * np.arange(m) / m)
        s[:m] *= w; s[-m:] *= w[::-1]
        parts += [s, np.zeros(int(0.4 * FS))]
        h.update(open(p, "rb").read())
    x = np.concatenate([np.zeros(int(0.5 * FS))] + parts)
    x *= 10 ** ((SING_DB - _active_rms_db(x, FS)) / 20.0)
    return x, dict(name=name, kind="recorded", seconds=len(x) / FS, id=h.hexdigest()[:10])


def available():
    """Voices present on this machine, the synthetic one first."""
    names = ["synth"]
    if os.path.isdir(LOCAL):
        names += sorted(d for d in os.listdir(LOCAL) if glob.glob(os.path.join(LOCAL, d, "*.wav")))
    return names


if __name__ == "__main__":
    import sys, soundfile as sf
    for n in available():
        x, info = voice(n)
        pk = 20 * np.log10(np.abs(x).max())
        print(f"{n:<14} {info['kind']:<10} {info['seconds']:5.1f} s   sung rms {_active_rms_db(x, FS):6.1f} dBFS   peak {pk:6.1f}   id {info['id']}")
    if "--write" in sys.argv:
        os.makedirs(CACHE, exist_ok=True)
        x, _ = voice("synth"); sf.write(os.path.join(CACHE, "synth-singer.wav"), x, FS, subtype="PCM_24")
        print("wrote", os.path.join(CACHE, "synth-singer.wav"))
