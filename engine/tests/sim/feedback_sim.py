"""
feedback_sim.py — physically-based microphone feedback simulator.

Closed loop, exact block processing:

    mic = seed + noise + (RIR * speaker_out)        # acoustic path (room + mic pattern)
    x   = mic_response(mic)                         # SM58-ish capsule
    x   = preamp_gain * x
    x   = channel_eq(x)   (your attack notches live here)
    x   = power_amp(x)    (soft clip / saturation — this is what makes it sound real)
    speaker_out = speaker_response(x)               # driver bandpass + resonance
    -> convolved with the room impulse response back to the mic

Block size is kept below the shortest acoustic path delay so every block's feedback
depends only on already-emitted audio.  That makes the loop sample-exact, not an approximation.

Usage (see bottom / demo.py):

    sim = FeedbackSim(fs=44100)
    sim.build_room(...)                 # geometry, mic + speaker positions, mic path
    sim.set_gain_margin(+4.0)           # preamp set so worst frequency is +4 dB over unity
    y, mic = sim.run(seconds=8, seed="click", attacks=[Attack(t=4.0, freq=None, q=30, depth_db=-18)])
    report = sim.analyze(mic, attacks=[...])
"""

from __future__ import annotations
import numpy as np
import pyroomacoustics as pra
from pyroomacoustics.directivities import CardioidFamily, DirectionVector, HyperCardioid, Cardioid
from scipy import signal
from dataclasses import dataclass, field
from typing import Optional, List, Dict, Tuple
import soundfile as sf


# ----------------------------------------------------------------------------- helpers

def db(x):
    return 20 * np.log10(np.maximum(np.abs(x), 1e-12))


def peaking_eq(fs, f0, q, gain_db):
    """RBJ peaking EQ biquad (b, a)."""
    A = 10 ** (gain_db / 40)
    w0 = 2 * np.pi * f0 / fs
    alpha = np.sin(w0) / (2 * q)
    b = [1 + alpha * A, -2 * np.cos(w0), 1 - alpha * A]
    a = [1 + alpha / A, -2 * np.cos(w0), 1 - alpha / A]
    return np.array(b) / a[0], np.array(a) / a[0]


def low_shelf(fs, f0, gain_db, slope=1.0):
    """RBJ low shelf biquad (b, a)."""
    A = 10 ** (gain_db / 40)
    w0 = 2 * np.pi * f0 / fs
    alpha = np.sin(w0) / 2 * np.sqrt((A + 1 / A) * (1 / slope - 1) + 2)
    c = np.cos(w0)
    b = [A * ((A + 1) - (A - 1) * c + 2 * np.sqrt(A) * alpha), 2 * A * ((A - 1) - (A + 1) * c),
         A * ((A + 1) - (A - 1) * c - 2 * np.sqrt(A) * alpha)]
    a = [(A + 1) + (A - 1) * c + 2 * np.sqrt(A) * alpha, -2 * ((A - 1) + (A + 1) * c),
         (A + 1) + (A - 1) * c - 2 * np.sqrt(A) * alpha]
    return np.array(b) / a[0], np.array(a) / a[0]


def jitter_path(start, end, seconds, step=0.4, cm=6.0, seed=3, tau_s=2.0):
    """
    Waypoints for a singer drifting from start to end with sway modelled as an
    Ornstein-Uhlenbeck process (low-passed random walk, stationary std = cm).
    Position is correlated second to second; excursions are realistic, jumps are not.
    """
    rng = np.random.default_rng(seed)
    ts = np.arange(0, seconds + step, step)
    s0, s1 = np.array(start, float), np.array(end, float)
    sig = cm / 100.0
    x = np.zeros(3)
    path = []
    a = np.exp(-step / tau_s)
    for t in ts:
        x = a * x + sig * np.sqrt(1 - a * a) * rng.standard_normal(3)
        w = min(t / seconds, 1.0)
        path.append((float(t), tuple(s0 + (s1 - s0) * w + x)))
    return path


def describing_gain(A, h):
    """
    Effective small-signal gain of y = h*tanh(x/h) for a sine of amplitude A riding through it:
    first-harmonic amplitude / A.  -> 1 for A << h, falls as A/h grows.  This is the
    "gain sharing" mechanism: a loud howl in the amp lowers the loop gain for everything else.
    """
    if A <= 0:
        return 1.0
    th = np.linspace(0, 2 * np.pi, 4096, endpoint=False)
    y = h * np.tanh(A * np.sin(th) / h)
    b1 = 2 * np.mean(y * np.sin(th))
    return b1 / A


def sos_from_biquads(biquads):
    return np.array([np.concatenate([b, a]) for b, a in biquads])


class StatefulSOS:
    """IIR filter that remembers its state between blocks."""

    def __init__(self, sos):
        self.sos = np.asarray(sos, dtype=np.float64)
        self.zi = np.zeros((self.sos.shape[0], 2))

    def process(self, x):
        y, self.zi = signal.sosfilt(self.sos, x, zi=self.zi)
        return y

    def freqz(self, fs, n=8192):
        w, h = signal.sosfreqz(self.sos, worN=n, fs=fs)
        return w, h


@dataclass
class Attack:
    """A notch you drop on the feedback at time t.  freq=None means 'auto: hit whatever is ringing'."""
    t: float
    freq: Optional[float] = None
    q: float = 30.0
    depth_db: float = -18.0
    label: str = ""
    kind: str = "notch"      # "notch" (narrow bell), "cone" (wide bell over a plateau), "auto" (classify then pick)


# ----------------------------------------------------------------------------- the sim

class FeedbackSim:
    def __init__(self, fs=44100, block=64):
        self.fs = fs
        self.block = block
        self.rirs: List[np.ndarray] = []
        self.rir_times: List[float] = []
        self.gain = 1.0
        self.amp_headroom = 0.35   # power-amp clip level (full scale = 1.0)
        self.eq_biquads: List[Tuple] = []
        self._build_fixed_chain()

    # ---------------- transducers (fixed part of the loop)

    def _build_fixed_chain(self):
        fs = self.fs
        # Dynamic vocal mic (SM58-ish): 2nd-order HP ~100 Hz, presence bump ~+5 dB @ 5 kHz,
        # rolloff above 12 kHz.  Proximity effect approximated by a low shelf tied to distance later.
        hp = signal.butter(2, 100, "hp", fs=fs, output="sos")
        pres_b, pres_a = peaking_eq(fs, 5000, 1.2, 5.0)
        lp = signal.butter(2, 13000, "lp", fs=fs, output="sos")
        self.mic_sos = np.vstack([hp, sos_from_biquads([(pres_b, pres_a)]), lp])

        # PA speaker: 2-way box.  Port-tuned HP ~55 Hz (4th order), cone breakup bump ~2.2 kHz,
        # crossover dip ~1.8 kHz, HF driver rolloff ~16 kHz.
        sp_hp = signal.butter(4, 55, "hp", fs=fs, output="sos")
        br_b, br_a = peaking_eq(fs, 2200, 4.0, 3.0)
        xo_b, xo_a = peaking_eq(fs, 1800, 3.0, -2.5)
        sp_lp = signal.butter(2, 16000, "lp", fs=fs, output="sos")
        self.spk_sos = np.vstack([sp_hp, sos_from_biquads([(br_b, br_a), (xo_b, xo_a)]), sp_lp])

    # ---------------- room / acoustic path

    def build_room(self,
                   dims=(8.0, 6.0, 3.2),
                   rt60=0.6,
                   speaker_pos=(1.0, 3.0, 1.6),
                   speaker_aim=(1.0, 0.0, 0.0),
                   mic_path=None,
                   mic_aim=None,
                   mic_pattern="cardioid",
                   max_order=None,
                   proximity_db=0.0):
        """
        mic_path: list of (t_seconds, (x,y,z)) waypoints.  The mic slides between them
                  (RIRs are crossfaded per block).  One waypoint = stationary mic.
        mic_aim:  direction the mic's front points.  Default: straight away from the speaker
                  at the first waypoint ... then deliberately drifts toward it, like a singer turning.
        """
        fs = self.fs
        if mic_path is None:
            mic_path = [(0.0, (3.5, 3.0, 1.6))]
        self.mic_path = mic_path
        e_abs, order = pra.inverse_sabine(rt60, dims)
        if max_order is None:
            max_order = min(order, 24)
        self.rirs, self.rir_times = [], []
        Pat = {"cardioid": Cardioid, "hypercardioid": HyperCardioid}[mic_pattern]
        for i, (t, mpos) in enumerate(mic_path):
            spk = np.array(speaker_pos, float)
            mp = np.array(mpos, float)
            if mic_aim is None:
                # mic front points away from speaker (rear null at speaker) — the correct setup
                away = mp - spk
                az = np.degrees(np.arctan2(away[1], away[0]))
            else:
                az = np.degrees(np.arctan2(mic_aim[1], mic_aim[0]))
            mic_dir = Pat(orientation=DirectionVector(azimuth=az, colatitude=90, degrees=True))
            saz = np.degrees(np.arctan2(speaker_aim[1], speaker_aim[0]))
            spk_dir = Cardioid(orientation=DirectionVector(azimuth=saz, colatitude=90, degrees=True))
            room = pra.ShoeBox(dims, fs=fs, materials=pra.Material(e_abs),
                               max_order=max_order, air_absorption=True)
            room.add_source(spk, directivity=spk_dir)
            room.add_microphone(mp, directivity=mic_dir)
            room.compute_rir()
            rir = np.asarray(room.rir[0][0], dtype=np.float64)
            # trim tail below -60 dB
            env = np.abs(rir)
            keep = np.where(env > env.max() * 1e-3)[0]
            rir = rir[: keep[-1] + 1]
            self.rirs.append(rir)
            self.rir_times.append(t)
        # min acoustic delay -> block size cap
        d_min = min(np.linalg.norm(np.array(p, float) - np.array(speaker_pos, float)) for _, p in mic_path)
        n_min = int(d_min / 343.0 * fs)
        self.min_delay = n_min
        if self.block > n_min:
            self.block = max(8, int(2 ** np.floor(np.log2(n_min))))
        self.speaker_pos = speaker_pos
        self.dims = dims
        if proximity_db:
            # singer eating the mic: proximity effect = low shelf boost
            b, a = low_shelf(fs, 250.0, proximity_db)
            self.mic_sos = np.vstack([self.mic_sos, sos_from_biquads([(b, a)])])
        return self

    # ---------------- loop gain analysis (frequency domain)

    def loop_response(self, n=16384, rir_index=0, include_eq=True):
        """Open-loop transfer function H(f) = G(f)·F(f) (linear part, no clipper)."""
        fs = self.fs
        f = np.fft.rfftfreq(n, 1 / fs)
        F = np.fft.rfft(self.rirs[rir_index], n)
        _, Hm = signal.sosfreqz(self.mic_sos, worN=f, fs=fs)
        _, Hs = signal.sosfreqz(self.spk_sos, worN=f, fs=fs)
        He = np.ones_like(Hm)
        if include_eq and self.eq_biquads:
            _, He = signal.sosfreqz(sos_from_biquads(self.eq_biquads), worN=f, fs=fs)
        H = self.gain * Hm * He * Hs * F
        return f, H

    def set_gain_margin(self, margin_db=3.0, rir_index=0):
        """Set preamp gain so the worst-case loop frequency sits margin_db above unity."""
        self.gain = 1.0
        c = self.predicted_howls(rir_index=rir_index, top=1, min_db=-200, rank="margin")
        peak_db = c[0]["margin_db"]
        self.gain = 10 ** ((margin_db - peak_db) / 20)
        return self.gain

    def predicted_howls(self, rir_index=0, top=8, include_eq=True, min_db=-12.0,
                        amp_level=0.0, rir_weights=None, rank="growth"):
        """
        Howl candidates: frequencies where loop phase == 0 (mod 2pi).
        For each: small-signal margin (dB), loop group delay tau_g (s), growth rate (dB/s),
        and the compression-aware margin given a sine of amplitude `amp_level` currently
        driving the tanh amp (describing-function gain).
        rank: "growth" (default, what actually wins) or "margin".
        rir_weights: dict {index: weight} to evaluate at a crossfaded mic position.
        Returns list of dicts sorted by rank key.
        """
        n = 1 << 18
        if rir_weights:
            F = sum(w * np.fft.rfft(self.rirs[i], n) for i, w in rir_weights.items())
            f = np.fft.rfftfreq(n, 1 / self.fs)
            _, Hm = signal.sosfreqz(self.mic_sos, worN=f, fs=self.fs)
            _, Hs = signal.sosfreqz(self.spk_sos, worN=f, fs=self.fs)
            He = np.ones_like(Hm)
            if include_eq and self.eq_biquads:
                _, He = signal.sosfreqz(sos_from_biquads(self.eq_biquads), worN=f, fs=self.fs)
            H = self.gain * Hm * He * Hs * F
        else:
            f, H = self.loop_response(n=n, rir_index=rir_index, include_eq=include_eq)
        mag = np.abs(H)
        ph = np.angle(H)
        # group delay from unwrapped phase: tau_g = -dphi/domega
        phu = np.unwrap(ph)
        dw = 2 * np.pi * (f[1] - f[0])
        tau = -np.gradient(phu, dw)
        comp_db = db(describing_gain(amp_level, self.amp_headroom)) if amp_level > 0 else 0.0
        s = np.sign(ph)
        cross = np.where((s[:-1] > 0) & (s[1:] <= 0) & (np.abs(ph[1:] - ph[:-1]) < np.pi))[0]
        cands = []
        for i in cross:
            a, b = ph[i], ph[i + 1]
            w = a / (a - b) if a != b else 0.0
            fc = f[i] + w * (f[i + 1] - f[i])
            mc = db(mag[i] + w * (mag[i + 1] - mag[i]))
            if mc < min_db:
                continue
            # loop period = acoustic delay + resonance group delay.  The derivative of the unwrapped
            # phase is noisy at sharp modes, so estimate the resonance part from the -3 dB bandwidth
            # of |H| around the crossing (tau_res ~ 1/(pi*B)) and take the larger of the two estimates.
            pk = mag[i]; j = i; k = i
            while j > 0 and mag[j] > pk * 0.707 and i - j < 4000: j -= 1
            while k < len(mag) - 1 and mag[k] > pk * 0.707 and k - i < 4000: k += 1
            Bw = max(f[k] - f[j], f[1] - f[0])
            tg_bw = self.min_delay / self.fs + 1 / (np.pi * Bw)
            tg_ph = float(np.median(tau[max(0, i - 20): i + 21]))
            tg = max(tg_bw, tg_ph, self.min_delay / self.fs)
            cands.append(dict(freq=float(fc), margin_db=float(mc), tau_ms=1000 * tg,
                              growth_db_s=float(mc / tg),
                              margin_now_db=float(mc + comp_db)))
        key = (lambda c: -c["growth_db_s"]) if rank == "growth" else (lambda c: -c["margin_db"])
        cands.sort(key=key)
        return cands[:top]

    def ladder(self, **kw):
        """Human-readable predicted_howls."""
        return [f"{c['freq']:.0f} Hz  {c['margin_db']:+.1f} dB  tau {c['tau_ms']:.0f} ms  "
                f"{c['growth_db_s']:+.1f} dB/s" + (f"  (now {c['margin_now_db']:+.1f} dB)" if kw.get('amp_level') else "")
                for c in self.predicted_howls(**kw)]

    # ---------------- seeds (the thing that starts it)

    def make_seed(self, kind, seconds):
        fs, n = self.fs, int(seconds * self.fs)
        t = np.arange(n) / fs
        s = np.zeros(n)
        rng = np.random.default_rng(1)
        if kind == "click":
            s[int(0.2 * fs)] = 0.8
        elif kind == "pop":  # plosive: LF thump into the capsule
            i = int(0.2 * fs); L = int(0.03 * fs)
            s[i:i + L] = 0.9 * np.sin(2 * np.pi * 70 * t[:L]) * np.hanning(L)
        elif kind == "boom":
            i = int(0.2 * fs); L = int(0.4 * fs)
            s[i:i + L] = 0.9 * np.sin(2 * np.pi * 55 * t[:L]) * np.exp(-t[:L] * 8)
        elif kind == "snare":
            i = int(0.2 * fs); L = int(0.15 * fs)
            s[i:i + L] = 0.7 * rng.standard_normal(L) * np.exp(-t[:L] * 30)
        elif kind == "voice":  # buzzy vowel-ish burst at 140 Hz with formants
            i = int(0.2 * fs); L = int(0.6 * fs)
            saw = signal.sawtooth(2 * np.pi * 140 * t[:L])
            b1, a1 = peaking_eq(fs, 700, 6, 14); b2, a2 = peaking_eq(fs, 1200, 6, 10)
            v = signal.lfilter(b2, a2, signal.lfilter(b1, a1, saw))
            s[i:i + L] = 0.4 * v / np.max(np.abs(v)) * np.hanning(L)
        elif kind == "noise":
            s = 0.02 * rng.standard_normal(n)
        elif kind == "none":
            pass
        else:
            raise ValueError(kind)
        return s

    # ---------------- the loop

    def _eq_sos(self):
        if not self.eq_biquads:
            return None
        return sos_from_biquads(self.eq_biquads)

    def run(self, seconds=8.0, seed="click", attacks: Optional[List[Attack]] = None,
            noise_floor_db=-75.0, movement_wobble=True, verbose=True):
        """
        Returns (speaker_out, mic_signal, events) — both float arrays at self.fs.
        attacks: notches inserted into channel EQ at time t.  freq=None -> auto-detect the ringing peak at t.
        """
        fs, B = self.fs, self.block
        N = int(seconds * fs)
        nblk = N // B
        N = nblk * B
        seed_sig = self.make_seed(seed, seconds)[:N]
        rng = np.random.default_rng(7)
        noise = 10 ** (noise_floor_db / 20) * rng.standard_normal(N)

        mic_f = StatefulSOS(self.mic_sos)
        spk_f = StatefulSOS(self.spk_sos)
        eq_f = StatefulSOS(self._eq_sos()) if self.eq_biquads else None
        # Hook: anything with .process(block) can BE the console EQ, which lets an
        # external guard sit inside the loop and react to what it cut. Scripted
        # Attacks still work; they just stack behind it.
        if getattr(self, 'external_eq', None) is not None:
            eq_f = self.external_eq

        # feedback accumulator: future acoustic arrivals at the mic
        Lr = max(len(r) for r in self.rirs)
        acc = np.zeros(N + Lr + B)
        # FFT partition setup: conv of one block with each RIR
        nfft = int(2 ** np.ceil(np.log2(B + Lr - 1)))
        RIR_F = [np.fft.rfft(r, nfft) for r in self.rirs]
        times = np.array(self.rir_times)

        out = np.zeros(N)
        mic = np.zeros(N)
        amp_in = np.zeros(N)
        attacks = sorted(attacks or [], key=lambda a: a.t)
        pending = list(attacks)
        events = []
        clip = self.amp_headroom

        # slow random-walk on gain to emulate the singer's hand / head moving a few cm (±0.4 dB)
        wob = np.ones(nblk)
        if movement_wobble:
            w = np.cumsum(rng.standard_normal(nblk)) * 0.002
            w = signal.lfilter([1], [1, -0.999], w) * 0.001
            wob = 10 ** (np.clip(w, -0.4, 0.4) / 20)

        for k in range(nblk):
            i0, i1 = k * B, (k + 1) * B
            t_now = i0 / fs

            # -------- drop attack notches into the EQ when their time comes
            while pending and pending[0].t <= t_now:
                a = pending.pop(0)
                info = self.classify_ring(out[max(0, i0 - int(1.0 * fs)):i0])
                if info["kind"] == "silent" and a.freq is None:
                    events.append((t_now, f"attack {a.label}: nothing ringing, no filter added",
                                   self.predicted_howls(rir_weights=self._weights_at(t_now))))
                    if verbose: print(f"[{t_now:6.2f}s] {events[-1][1]}")
                    continue
                if a.kind == "auto":
                    a.kind = "cone" if info["kind"] == "plateau" else "notch"
                    # cluster rule: a mode we already notched nearby means this is a plateau of
                    # neighbouring modes taking over one at a time -> widen instead of whack-a-mole
                    near = [p for p in getattr(self, "attack_log", []) if abs(p.freq - info["freq"]) / info["freq"] < 0.25]
                    if near and a.kind == "notch":
                        a.kind = "cone"
                        fs_all = [p.freq for p in near] + [info["freq"]]
                        info["lo"], info["hi"] = min(fs_all), max(fs_all)
                        info["kind"] = f"cluster of {len(fs_all)} modes"
                if a.freq is None:
                    a.freq = info["freq"]
                if a.kind == "cone":
                    # wide bell covering the whole wander range with margin, centred on it
                    span = max(info["hi"] - info["lo"], 0.15 * a.freq) * 2.0
                    a.freq = 0.5 * (info["lo"] + info["hi"]) if info["hi"] > info["lo"] else a.freq
                    a.q = min(a.q, a.freq / span)
                    a.q = max(a.q, 0.5)
                b, aa = peaking_eq(fs, a.freq, a.q, a.depth_db)
                self.eq_biquads.append((b, aa))
                self.attack_log = getattr(self, "attack_log", []) + [a]
                new_sos = self._eq_sos()
                if getattr(self, 'external_eq', None) is None:
                    old_zi = eq_f.zi if eq_f else None
                    eq_f = StatefulSOS(new_sos)
                    if old_zi is not None:
                        eq_f.zi[: old_zi.shape[0]] = old_zi
                # amp drive level just before the attack (peak of amp input over last 50 ms)
                amp_A = float(np.max(np.abs(amp_in[max(0, i0 - int(0.05 * fs)):i0]))) if i0 > 0 else 0.0
                events.append((t_now, f"{a.kind} {a.freq:.1f} Hz  Q={a.q:.1f}  {a.depth_db:+.0f} dB  {a.label}  "
                                      f"[seen: {info['kind']}, drift {info['drift_hz']:.0f} Hz, width {info['width_hz']:.0f} Hz]",
                               self.predicted_howls(rir_weights=self._weights_at(t_now), amp_level=amp_A)))
                if verbose:
                    print(f"[{t_now:6.2f}s] ATTACK -> {events[-1][1]}")

            # -------- acoustic path in (already computed arrivals) + seed + self-noise
            m = acc[i0:i1] + seed_sig[i0:i1] + noise[i0:i1]
            mic[i0:i1] = m

            # -------- electronics
            x = mic_f.process(m)
            x = x * (self.gain * wob[k])
            if eq_f is not None:
                x = eq_f.process(x)
            amp_in[i0:i1] = x
            # power amp: soft knee into hard-ish clip.  tanh gives the odd harmonics you hear
            x = clip * np.tanh(x / clip)
            y = spk_f.process(x)
            out[i0:i1] = y

            # -------- radiate: convolve this block with the room and add to future arrivals
            Y = np.fft.rfft(y, nfft)
            if len(self.rirs) == 1:
                contrib = np.fft.irfft(Y * RIR_F[0], nfft)[: B + Lr - 1]
            else:
                # crossfade between waypoint RIRs by current time (linear in position)
                j = np.searchsorted(times, t_now, side="right") - 1
                j = int(np.clip(j, 0, len(times) - 2))
                w1 = (t_now - times[j]) / max(times[j + 1] - times[j], 1e-9)
                w1 = float(np.clip(w1, 0, 1))
                if t_now >= times[-1]:
                    j, w1 = len(times) - 2, 1.0
                contrib = np.fft.irfft(Y * ((1 - w1) * RIR_F[j] + w1 * RIR_F[j + 1]), nfft)[: B + Lr - 1]
            acc[i0:i0 + B + Lr - 1] += contrib

        return out, mic, events

    def _weights_at(self, t_now):
        times = np.array(self.rir_times)
        if len(times) == 1:
            return {0: 1.0}
        j = int(np.clip(np.searchsorted(times, t_now, side="right") - 1, 0, len(times) - 2))
        w1 = float(np.clip((t_now - times[j]) / max(times[j + 1] - times[j], 1e-9), 0, 1))
        if t_now >= times[-1]:
            j, w1 = len(times) - 2, 1.0
        return {j: 1 - w1, j + 1: w1}

    # ---------------- analysis

    def _dominant_freq(self, x):
        if len(x) < 256 or np.max(np.abs(x)) < 1e-6:
            return 1000.0
        n = 1 << int(np.ceil(np.log2(len(x) * 4)))
        X = np.abs(np.fft.rfft(x * np.hanning(len(x)), n))
        f = np.fft.rfftfreq(n, 1 / self.fs)
        i = np.argmax(X[5:]) + 5
        # parabolic interpolation
        if 1 <= i < len(X) - 1:
            a, b, c = np.log(X[i - 1] + 1e-12), np.log(X[i] + 1e-12), np.log(X[i + 1] + 1e-12)
            p = 0.5 * (a - c) / (a - 2 * b + c + 1e-12)
            return f[i] + p * (f[1] - f[0])
        return f[i]

    def classify_ring(self, x):
        """
        Look at the last ~1 s of signal.  Returns dict(freq, drift_hz, width_hz, kind)
        kind = "spike" (narrow, stationary) or "plateau" (wide and/or wandering).
        """
        fs = self.fs
        L = int(0.05 * fs)
        n = len(x) // L
        if n < 4:
            return dict(freq=1000.0, drift_hz=0, width_hz=0, kind="spike", lo=1000, hi=1000)
        lv = np.array([db(np.sqrt(np.mean(x[i * L:(i + 1) * L] ** 2))) for i in range(n)])
        if lv[-4:].max() < -55:
            return dict(freq=None, drift_hz=0, width_hz=0, kind="silent", lo=0, hi=0)
        doms = np.array([self._dominant_freq(x[i * L:(i + 1) * L]) for i in range(n)])
        doms = doms[lv > lv.max() - 20]   # only frames where something is actually ringing
        f0 = float(np.median(doms[-8:]))
        lo, hi = np.percentile(doms, 10), np.percentile(doms, 90)
        drift = float(hi - lo)
        # -3 dB width of the peak in the last 0.4 s
        seg = x[-int(0.4 * fs):]
        nf = 1 << 16
        X = db(np.fft.rfft(seg * np.hanning(len(seg)), nf))
        fr = np.fft.rfftfreq(nf, 1 / fs)
        i = np.argmin(abs(fr - f0)); pk = X[i]
        j = i
        while j > 0 and X[j] > pk - 6: j -= 1
        k = i
        while k < len(X) - 1 and X[k] > pk - 6: k += 1
        width = float(fr[k] - fr[j])
        res = fs / nf * 8   # ignore width below a few bins
        plateau = (drift / f0 > 0.03) or (width > max(0.05 * f0, res))
        return dict(freq=f0, drift_hz=drift, width_hz=width, lo=float(lo), hi=float(hi),
                    kind="plateau" if plateau else "spike")

    def track(self, x, frame=0.05):
        """Per-frame RMS (dB) and dominant frequency."""
        fs = self.fs
        L = int(frame * fs)
        n = len(x) // L
        t = np.arange(n) * frame
        lvl = np.array([db(np.sqrt(np.mean(x[i * L:(i + 1) * L] ** 2))) for i in range(n)])
        dom = np.array([self._dominant_freq(x[i * L:(i + 1) * L]) for i in range(n)])
        return t, lvl, dom

    def analyze(self, out, attacks: List[Attack], events, ring_thresh_db=-30.0, verdict_window=1.5):
        """
        For each attack: did the spike die?  did it move?  what does the loop-gain math say?
        Verdict per attack: KILLED / MIGRATED / SURVIVED (+ predicted next howl if any).
        """
        t, lvl, dom = self.track(out)
        report = []
        for (ta, txt, preds), a in zip(events, attacks):
            f0 = a.freq
            if f0 is None:
                report.append({"t": ta, "attack": txt, "verdict": "NO-OP (nothing ringing)"})
                continue
            pre = (t >= ta - 0.5) & (t < ta)
            post = (t >= ta + 0.3) & (t < ta + verdict_window)
            late = (t >= ta + verdict_window) & (t < ta + verdict_window + 1.0)
            pre_lvl = np.median(lvl[pre]) if pre.any() else -120
            pre_f = np.median(dom[pre]) if pre.any() else 0
            post_lvl = np.median(lvl[post]) if post.any() else -120
            post_f = np.median(dom[post]) if post.any() else 0
            late_lvl = np.median(lvl[late]) if late.any() else post_lvl
            late_f = np.median(dom[late]) if late.any() else post_f

            same_f = abs(late_f - f0) / max(f0, 1) < (0.15 if a.kind == "cone" else 0.02)
            floor = -60.0
            if late_lvl < floor:
                verdict = "KILLED"
            elif same_f:
                verdict = f"SURVIVED in the same region, {pre_lvl - late_lvl:.1f} dB quieter (notch off-centre / too shallow / too narrow)"
            elif late_lvl >= ring_thresh_db:
                verdict = f"MIGRATED -> now ringing at {late_f:.0f} Hz"
            else:
                verdict = f"DYING ({late_f:.0f} Hz at {late_lvl:.0f} dBFS, still decaying at end of window)"
            if late_lvl > post_lvl + 6 and late_lvl < ring_thresh_db:
                verdict = f"MIGRATING -> {late_f:.0f} Hz building ({post_lvl:.0f} -> {late_lvl:.0f} dBFS)"
            report.append({
                "t": ta, "attack": txt,
                "ringing_before": f"{pre_f:.0f} Hz @ {pre_lvl:.1f} dBFS",
                "after": f"{late_f:.0f} Hz @ {late_lvl:.1f} dBFS",
                "verdict": verdict,
                "ladder_after (growth-ranked, small-signal)": [f"{c['freq']:.0f} Hz: {c['margin_db']:+.1f} dB, {c['growth_db_s']:+.1f} dB/s" for c in preds[:4]],
            })
        return report


def save_wav(path, x, fs, peak_db=-1.0):
    x = np.asarray(x, float)
    m = np.max(np.abs(x)) or 1.0
    sf.write(path, x / m * 10 ** (peak_db / 20), fs)
