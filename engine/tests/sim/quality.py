#!/usr/bin/env python3
"""
quality.py — a singer in the loop: what the guard buys, what it costs the voice,
and how long anything rings.

Why this exists
---------------
Every other layer of the gate asks "was the ring caught". None asks what the
audience heard, and none has a voice in it that the loop can feed on. A voice
cannot be laid over a simulated howl afterwards: in a room the singer is the
thing the loop amplifies, so the voice has to go in at the microphone and come
round through the speaker, the room and the guard like everything else. That is
how the published comparisons are run (van Waterschoot & Moonen 2011; see
docs/references/), and it is what this does:

    guard input = singer + room noise + (speaker output, through the room)
    speaker     = amp( fader gain x guard(guard input) )

The fader sits AFTER the guard, as it does on the rig, so the voice arrives at
the guard at the same level whatever the gain. Levels are the rig's: a sung
phrase at -30 dBFS rms at the guard's input, the room's noise at -66.

Against that, a second pass with no loop and no guard: the same voice straight
through the same amplifier and speaker. That is the CLEAN REFERENCE - what the
audience would hear if feedback did not exist. Everything is scored against it.

The numbers (all per run)
-------------------------
  voice change %   How much of what the ear gets from the voice was altered:
                   the summed difference in loudness, band by band on the ear's
                   own frequency scale, between what came out and the clean
                   reference, as a share of the reference. 0 = untouched.
                   A level change of 1 dB everywhere is 7 %; 3 dB is 19 %.
                   Split into what was ADDED (ringing) and what was REMOVED
                   (cuts, ducks).
  distance, dB     The survey's measure: rms difference in level across the same
                   bands. For comparing with the literature.
  audible ring     A line the singer did not sing, above the AUDIBLE line
                   (-50 dBFS: 20 dB under the voice) in what the audience hears.
                   For each: how long it was audible, how long after it became
                   audible a real cut (12 dB) landed on it - CATCH, negative when
                   the cut was there first - and how long after that it took to
                   fall back under the line - KILL.
  filters, duck    What the guard was holding, and whether the rescue duck fired.

    python3 quality.py                       # the standard study: every room, every voice on this machine
    python3 quality.py --gate                # the quick subset the ship gate runs
    python3 quality.py --room rig --voice synth --case +10 -v     # one run, rings listed
    python3 quality.py --selftest            # the ruler itself: known inputs, known answers
    python3 quality.py --wav out/            # also write what came out (L) and the clean reference (R)
"""
import ctypes, json, os, sys, time
import numpy as np
from scipy import signal

HERE = os.path.dirname(os.path.abspath(__file__)); sys.path.insert(0, HERE)
from feedback_sim import FeedbackSim, StatefulSOS, peaking_eq, sos_from_biquads
from fk_in_loop import FkGuard
from loop_measure import ladder, peaking
from voices import voice, available, FS, SING_DB

AUDIBLE_DB = -50.0          # a line this loud in what the audience hears is audible: 20 dB under the voice
NOISE_DB   = -66.0          # the room at the guard's input (rig, 2026-09-27: quietest frames -65.5)
CLIP       = 3.16           # the amplifier's ceiling, +10 dBFS: 40 dB over the voice at the edge, so the PA never clips on singing
CUT_DB     = -12.0          # "a real cut": the depth that counts as caught
PREROLL_S  = 4.0            # the fader comes up over this long before the singer starts
P_HEAD     = 2048           # the room response is convolved in two parts: this much per block, the rest per chunk
UNITY_SOS  = np.array([[1.0, 0.0, 0.0, 1.0, 0.0, 0.0]])


# ============================================================================ the loop

class Loop:
    """A room and a signal path, calibrated so 0 dB is the untreated edge.

    kind "rig"   the home rig's shape: microphone half a metre from the speaker,
                 a coupling that peaks near 9.5 kHz. Rings high and fast.
         "hall"  a reverberant room, microphone three metres out. Rings low and
                 slow once its top is held.

    The simulator's rooms put their colour in the microphone, which is right for
    the loop and wrong for a singer: the rig-like one is a 1.5 kHz high-pass
    with 14 dB at 9.5 kHz, and a voice through that is not a voice. The colour
    belongs to the path from the speaker back into the microphone, so that is
    where it is put here; the loop response is identical, and the singer reaches
    the guard as sung.

    `move` = (start_s, end_s, metres): the microphone travels that far during
    the run - the survey's "the room changes".
    """
    def __init__(self, kind="rig", fs=FS, move=None):
        sim = FeedbackSim(fs=fs)
        if kind == "rig":
            b, a = peaking_eq(fs, 9500, 1.0, 14.0)
            path = np.vstack([signal.butter(2, 1500, "hp", fs=fs, output="sos"), sos_from_biquads([(b, a)]),
                              signal.butter(2, 16000, "lp", fs=fs, output="sos")])
            room = dict(dims=(6, 5, 2.8), rt60=0.4, speaker_pos=(1.0, 2.5, 1.5)); mic = np.array([1.5, 2.5, 1.5])
        elif kind == "hall":
            path = sim.mic_sos.copy()
            room = dict(dims=(9, 7, 3.2), rt60=0.7, speaker_pos=(1.0, 3.5, 1.7)); mic = np.array([4.0, 3.5, 1.6])
        else:
            raise ValueError(kind)
        mic_path = [(0.0, tuple(mic))]
        if move:
            t0, t1, metres = move
            steps = max(2, int(np.ceil(metres / 0.025)) + 1)          # a waypoint every 2.5 cm
            mic_path = [(t0 + (t1 - t0) * i / (steps - 1), tuple(mic + np.array([0.6, 0.8, 0.0]) * metres * i / (steps - 1)))
                        for i in range(steps)]
        sim.build_room(mic_path=mic_path, **room)
        sim.rirs = [signal.sosfilt(path, r) for r in sim.rirs]
        sim.mic_sos = UNITY_SOS
        sim.set_gain_margin(0.0)                                      # the untreated edge, at the starting position
        self.kind, self.fs, self.block, self.sim = kind, fs, sim.block, sim
        self.rirs = [sim.gain * r for r in sim.rirs]
        self.times = np.array(sim.rir_times)
        self.spk_sos = sim.spk_sos
        self.edge = sim.predicted_howls(top=1, min_db=-200.0, rank="margin")[0]
        assert P_HEAD % self.block == 0

    def response(self, n=1 << 18):
        """The loop's own response at 0 dB (complex), first microphone position."""
        return self.sim.loop_response(n=n)


def schedule(margin_db, preroll=PREROLL_S, start_db=-12.0):
    """The fader: up from start_db to margin_db over the pre-roll, then held."""
    lo = min(start_db, margin_db)
    return lambda t: lo + (margin_db - lo) * np.clip(t / preroll, 0.0, 1.0)


def run_loop(loop, src, margin=None, guarded=True, noise_db=NOISE_DB, seed=7, config="rig"):
    """One pass. `margin` is dB over the untreated edge: a number (via schedule),
    a function of time, or None for no loop at all (the voice alone through the guard).

    Returns the four signals the scoring needs, in the guard's units:
      xin   what the guard heard          xout  what it passed on
      yn    what the audience heard       refn  the clean reference
    (both speaker signals divided by the fader, so every run reads on one scale)."""
    fs, B = loop.fs, loop.block
    nblk = len(src) // B; N = nblk * B
    src = np.asarray(src[:N], float)
    rng = np.random.default_rng(seed)
    noise = 10 ** (noise_db / 20.0) * rng.standard_normal(N)
    closed = margin is not None
    sched = (lambda t: 0.0 * t) if not closed else (margin if callable(margin) else schedule(float(margin)))
    g = 10 ** (sched(np.arange(nblk) * B / fs) / 20.0)

    guard = FkGuard(fs, B, config=config) if guarded else None
    spk = StatefulSOS(loop.spk_sos)
    heads = [r[:P_HEAD] for r in loop.rirs]
    tails = [r[P_HEAD:] for r in loop.rirs]
    Lt = max(len(t) for t in tails)
    nt = 1 << int(np.ceil(np.log2(P_HEAD + max(Lt, 1))))
    TAILS = [np.fft.rfft(t, nt) for t in tails] if Lt else None
    acc = np.zeros(N + P_HEAD + nt + B)
    xin = np.zeros(N); xout = np.zeros(N); y = np.zeros(N)
    times = loop.times; moving = len(times) > 1
    tl_t, tl_n, tl_duck = [], [], []

    def blend(parts, t):
        if not moving: return parts[0]
        j = int(np.clip(np.searchsorted(times, t, side="right") - 1, 0, len(times) - 2))
        w = float(np.clip((t - times[j]) / max(times[j + 1] - times[j], 1e-9), 0.0, 1.0))
        return (1.0 - w) * parts[j] + w * parts[j + 1]

    head = heads[0]
    for k in range(nblk):
        i0 = k * B; i1 = i0 + B
        m = acc[i0:i1] + src[i0:i1] + noise[i0:i1]
        np.clip(m, -1.0, 1.0, out=m)                        # the converter
        xin[i0:i1] = m
        x = guard.process(m) if guard is not None else m
        xout[i0:i1] = x
        x = CLIP * np.tanh(x * g[k] / CLIP)                 # the fader, then the amplifier
        yb = spk.process(x)
        y[i0:i1] = yb
        if closed:
            if moving: head = blend(heads, i0 / fs)
            acc[i0:i0 + B + P_HEAD - 1] += np.convolve(yb, head)
            if Lt and i1 % P_HEAD == 0:                     # the tail of the room, a chunk at a time, landing exactly on time
                T = blend(TAILS, (i1 - P_HEAD) / fs)
                acc[i1:i1 + nt] += np.fft.irfft(np.fft.rfft(y[i1 - P_HEAD:i1], nt) * T, nt)
        if guard is not None and k % 8 == 0:
            tl_t.append(i1 / fs); tl_n.append(len(guard.notches())); tl_duck.append(_duck(guard)["depth_db"])

    gs = np.repeat(g, B)
    ref_in = src + noise
    refn = signal.sosfilt(loop.spk_sos, CLIP * np.tanh(ref_in * gs / CLIP)) / gs
    out = dict(fs=fs, xin=xin, xout=xout, yn=y / gs, refn=refn, refin=ref_in, src=src, g_db=20 * np.log10(g), block=B,
               t=np.array(tl_t), filters=np.array(tl_n), duck=np.array(tl_duck))
    if guard is not None:
        out.update(events=guard.events, notches=_details(guard), rescue=_duck(guard), config=dict(guard.config))
        guard.lib.fk_destroy(guard.h)
    return out


def _duck(guard):
    """The rescue duck's state (zeros on a build from before the duck)."""
    return guard.rescue()


def _details(guard):
    lib = guard.lib
    if not guard.has("fk_notch_detail"): return [dict(freq=f, q=12.0, depth_db=d) for f, d in guard.notches()]
    fn = lib.fk_notch_detail
    FP, IP = ctypes.POINTER(ctypes.c_float), ctypes.POINTER(ctypes.c_int)
    fn.restype = ctypes.c_int; fn.argtypes = [ctypes.c_void_p, ctypes.c_int, FP, FP, FP, FP, IP]
    out = []
    for i in range(48):
        f, o, q, d = (ctypes.c_float() for _ in range(4)); lk = ctypes.c_int()
        if fn(guard.h, i, f, o, q, d, lk): out.append(dict(freq=f.value, q=q.value, depth_db=d.value))
    return out


# ============================================================================ the ruler

def _erb_rate(f): return 21.4 * np.log10(4.37e-3 * np.asarray(f, float) + 1.0)


def band_energies(x, fs, n=2048, hop=1024, lo=100.0, hi=16000.0):
    """Energy per frame in bands one ERB wide - the ear's own frequency scale.
    Returns (E[frames, bands], band centres Hz). Units: mean square, so
    10 log10 E is the band's level in dBFS rms."""
    win = np.hanning(n); k = 1 + (len(x) - n) // hop
    idx = np.arange(n)[None, :] + hop * np.arange(k)[:, None]
    P = np.abs(np.fft.rfft(x[idx] * win, axis=1)) ** 2 * (2.0 / (n * np.sum(win ** 2)))
    f = np.fft.rfftfreq(n, 1.0 / fs); e = _erb_rate(f)
    e0, e1 = _erb_rate(lo), _erb_rate(hi); nb = int(np.floor(e1 - e0))
    b = np.floor(e - e0).astype(int); ok = (b >= 0) & (b < nb)
    W = np.zeros((nb, len(f))); W[b[ok], np.where(ok)[0]] = 1.0
    centres = (10 ** ((e0 + np.arange(nb) + 0.5) / 21.4) - 1.0) / 4.37e-3
    return P @ W.T, centres


E_FLOOR = 10 ** (-75.0 / 10.0)      # a band quieter than this is not heard under the room

# The ear does not have brick-wall bands: a loud band excites its neighbours,
# upward much more than downward, which is why a narrow hole in a spectrum is
# heard as shallower than it is. Eight decibels per band going up, twenty going
# down (the usual excitation-pattern slopes), normalised so a flat spectrum is
# unchanged. Without this a needle-thin notch scores like a wide one.
_SPREAD = np.array([10 ** (-2.0 * abs(d)) if d < 0 else 10 ** (-0.8 * d) for d in range(-3, 4)])
_SPREAD /= _SPREAD.sum()


def _loud(E):
    """Loudness per band: the excitation (energy spread to neighbouring bands),
    compressed the way the ear compresses, above the room's floor."""
    X = np.zeros_like(E)
    for i, d in enumerate(range(-3, 4)):           # energy in band j excites band j + d
        if d == 0: X += _SPREAD[i] * E
        elif d > 0: X[:, d:] += _SPREAD[i] * E[:, :-d]
        else: X[:, :d] += _SPREAD[i] * E[:, -d:]
    return (X + E_FLOOR) ** 0.3 - E_FLOOR ** 0.3


def voice_change(out, ref, fs, sung_db=SING_DB - 25.0):
    """How far `out` is from `ref`.

    change % = sum |loudness(out) - loudness(ref)| / sum loudness(ref), over
    ERB bands and over the whole run - a ring in a rest is heard as surely as a
    cut in a note - with loudness as in _loud. added / removed split the same
    sum by sign. worst % is the same thing over the worst single second.
    distance dB = the survey's frequency-weighted log-spectral distance: rms
    over bands of the level difference, averaged over the frames with a voice
    in them."""
    Eo, fc = band_energies(out, fs); Er, _ = band_energies(ref, fs)
    No, Nr = _loud(Eo), _loud(Er)
    d = No - Nr; tot = max(float(np.sum(Nr)), 1e-12)
    sung = 10 * np.log10(np.sum(Er, axis=1) + 1e-20) > sung_db
    sd = 0.0
    if np.any(sung):
        heard = Er[sung] > 10.0 * E_FLOOR
        dl = 10 * np.log10((Eo[sung] + E_FLOOR) / (Er[sung] + E_FLOOR))
        sd = float(np.mean(np.sqrt(np.sum(np.where(heard, dl ** 2, 0.0), axis=1) / np.maximum(np.sum(heard, axis=1), 1))))
    # the worst second, against an average second of the voice: a run can be fine on average and wrecked for one phrase
    w = max(1, int(fs / 1024)); per = np.sum(np.abs(d), axis=1)
    worst = float(np.max(np.convolve(per, np.ones(w), "valid"))) if len(per) >= w else float(np.sum(per))
    per_second = tot / max(1.0, np.sum(sung) / w) if np.any(sung) else tot
    return dict(change_pct=float(100 * np.sum(np.abs(d)) / tot), added_pct=float(100 * np.sum(np.maximum(d, 0)) / tot),
                removed_pct=float(100 * np.sum(np.maximum(-d, 0)) / tot), distance_db=sd,
                worst_pct=float(100 * worst / per_second))


def band_gain(xin, xout, fs, lo, hi, n=4096):
    """What the guard did to the level between lo and hi, averaged the way it is
    heard (over log frequency): long-term spectrum out over in, dB."""
    f, Pi = signal.welch(xin, fs, nperseg=n); _, Po = signal.welch(xout, fs, nperseg=n)
    sel = (f >= lo) & (f < hi); w = 1.0 / f[sel]
    return float(np.sum(w * 10 * np.log10((Po[sel] + 1e-30) / (Pi[sel] + 1e-30))) / np.sum(w))


def _lines(x, fs, n=2048, hop=256):
    """Level of the strongest line near each bin, dBFS rms of the tone: the power
    in three adjacent Hann bins, which is flat to a tenth of a dB whether or
    not the tone sits on a bin."""
    win = np.hanning(n); k = 1 + (len(x) - n) // hop
    idx = np.arange(n)[None, :] + hop * np.arange(k)[:, None]
    P = np.abs(np.fft.rfft(x[idx] * win, axis=1)) ** 2
    P3 = P.copy(); P3[:, 1:] += P[:, :-1]; P3[:, :-1] += P[:, 1:]
    return 10 * np.log10(P3 / 1.5 * (4.0 / n) ** 2 / 2.0 + 1e-30)


def _env_db(x, fs, f0, bw):
    """Level in a band around f0 over time, dBFS rms, to about a millisecond."""
    lo, hi = max(40.0, f0 - bw / 2), min(0.49 * fs, f0 + bw / 2)
    z = signal.sosfiltfilt(signal.butter(3, [lo, hi], "bp", fs=fs, output="sos"), x)
    e = np.abs(signal.hilbert(z)) / np.sqrt(2.0)
    m = max(1, int(0.001 * fs))
    return 20 * np.log10(np.convolve(e, np.ones(m) / m, "same") + 1e-12)


def rings(r, audible_db=AUDIBLE_DB, find_db=-80.0, excess_db=15.0, min_hz=80.0):
    """Every line in what the audience heard that the singer did not sing.

    Found on the spectrogram (a line 15 dB over anything the clean reference
    has within three bins and 16 ms), then timed in a narrow band around each
    one. Returns (episodes, summary). An episode that reached the audible line
    carries audible_ms, catch_ms and kill_ms; one that did not is counted as
    held quietly, with how loud it got."""
    fs, n, hop = r["fs"], 2048, 256
    Y, R = _lines(r["yn"], fs, n, hop), _lines(r["refn"], fs, n, hop)
    from scipy.ndimage import maximum_filter
    Rm = maximum_filter(R, size=(7, 7), mode="nearest")
    f = np.fft.rfftfreq(n, 1.0 / fs)
    cand = (Y >= find_db) & (Y - Rm >= excess_db) & (f[None, :] >= min_hz)
    # a line is a local maximum across frequency
    cand[:, 1:-1] &= (Y[:, 1:-1] >= Y[:, :-2]) & (Y[:, 1:-1] >= Y[:, 2:])
    cols = np.where(cand.sum(axis=0) >= 3)[0]                       # there for at least 16 ms in all
    if len(cols) == 0:
        return [], dict(rings=0, audible=0, audible_ms=0.0, worst_audible_ms=0.0, worst_catch_ms=None,
                        worst_kill_ms=None, never_cut=0, loudest_db=None, quiet_loudest_db=None, howl_pct=0.0,
                        artifacts=0, artifact_ms=0.0, tails=0, tail_ms=0.0)
    groups, cur = [], [cols[0]]
    for c in cols[1:]:
        if c - cur[-1] <= 3: cur.append(c)
        else: groups.append(cur); cur = [c]
    groups.append(cur)

    eps = []
    dur = len(r["yn"]) / fs
    gap = int(0.15 * fs / hop)                 # the same line again after 150 ms of nothing is a new episode
    for grp in groups:
        cols_g = slice(grp[0], grp[-1] + 1)
        all_frames = np.where(cand[:, cols_g].any(axis=1))[0]
        for frames in np.split(all_frames, np.where(np.diff(all_frames) > gap)[0] + 1):
            if len(frames) < 3: continue       # there for less than 16 ms: not a line
            sub = np.where(cand[frames][:, cols_g], Y[frames][:, cols_g], -999.0)
            f0 = float(f[grp[0] + int(np.argmax(sub.max(axis=0)))]); peak_fft = float(sub.max())
            t_first, t_last = frames[0] * hop / fs, (frames[-1] * hop + n) / fs
            ep = dict(hz=f0, t0=float(t_first), t1=float(t_last), peak_db=peak_fft, audible_ms=0.0, catch_ms=None, kill_ms=None, kind="ring")
            if peak_fft >= audible_db - 6.0:
                # time it properly, in a band wide enough for the group and no wider than the ear's
                bw = max(0.03 * f0, (grp[-1] - grp[0] + 4) * fs / n)
                a = max(0, int((t_first - 0.6) * fs)); b = min(len(r["yn"]), int((t_last + 0.3) * fs))
                ey, er = _env_db(r["yn"][a:b], fs, f0, bw), _env_db(r["refn"][a:b], fs, f0, bw)
                ei, eo = _env_db(r["xin"][a:b], fs, f0, bw), _env_db(r["xout"][a:b], fs, f0, bw)
                inside = np.zeros(b - a, bool)
                inside[max(0, int((t_first - 0.02) * fs) - a):int((t_last + 0.02) * fs) - a] = True
                aud = (ey > audible_db) & (ey > er + 10.0) & inside
                ep["peak_db"] = float(np.max(np.where((ey > er + 10.0) & inside, ey, -999.0)))
                if np.any(aud):
                    on = np.where(aud)[0]
                    t_on, t_off = (a + on[0]) / fs, (a + on[-1]) / fs
                    # a cut already in place before the line was audible counts from before (as far back as we look)
                    held = np.where(((eo - ei) <= CUT_DB)[:on[-1] + 1])[0]
                    ep["audible_ms"] = float(1000.0 * np.sum(aud) / fs)
                    ep["t_on"], ep["t_off"] = float(t_on), float(t_off)
                    # Feedback comes round the loop, so it is at the guard's INPUT, over and above
                    # what the singer put there. A line that is only in the output was made by the
                    # guard itself: a deep narrow filter keeps sounding its own note for a moment
                    # after the singer stops. Both are heard; only the first is a ring.
                    eri = _env_db(r["refin"][a:b], fs, f0, bw)
                    if np.mean((ei > eri + 10.0)[aud]) < 0.5: ep["kind"] = "artifact"
                    else:
                        # Feedback GROWS. Near the edge a stable loop also hangs on to a note after
                        # the singer leaves it - a tail, loudest at its start and falling from there.
                        # That is the room being ringy (and is charged to the voice-change figure);
                        # it is not something to catch. A ring is a line that climbs: 3 dB or more
                        # above where it stood in its first 10 ms.
                        seg = ey[on[0]:on[-1] + 1]
                        if seg.max() - seg[:max(1, int(0.010 * fs))].max() < 3.0: ep["kind"] = "tail"
                    if len(held):
                        t_c = (a + held[0]) / fs
                        ep["catch_ms"] = float(1000.0 * (t_c - t_on))
                        ep["kill_ms"] = float(1000.0 * (t_off - max(t_c, t_on)))
                    ep["still_ringing"] = bool(t_off > dur - 0.05)
            eps.append(ep)

    aud = [e for e in eps if e["audible_ms"] > 0.0 and e["kind"] == "ring"]
    art = [e for e in eps if e["audible_ms"] > 0.0 and e["kind"] == "artifact"]
    tails = [e for e in eps if e["audible_ms"] > 0.0 and e["kind"] == "tail"]
    quiet = [e for e in eps if e["audible_ms"] == 0.0]
    missed = [e for e in aud if e["catch_ms"] is None]
    total = sum(e["audible_ms"] for e in aud)
    summary = dict(
        rings=len(eps), audible=len(aud), audible_ms=float(total),
        worst_audible_ms=float(max((e["audible_ms"] for e in aud), default=0.0)),
        # a ring never cut by 12 dB was never caught: its whole audible life counts against the catch
        worst_catch_ms=(float(max([e["catch_ms"] for e in aud if e["catch_ms"] is not None] +
                                  [e["audible_ms"] for e in missed])) if aud else None),
        worst_kill_ms=(float(max([e["kill_ms"] for e in aud if e["kill_ms"] is not None] +
                                 [e["audible_ms"] for e in missed])) if aud else None),
        never_cut=len(missed),
        loudest_db=(float(max(e["peak_db"] for e in aud)) if aud else None),
        quiet_loudest_db=(float(max(e["peak_db"] for e in quiet)) if quiet else None),
        howl_pct=float(100.0 * min(1.0, total / 1000.0 / dur)),
        # lines the guard made itself (a filter sounding on after the singer stops): heard, but not feedback
        artifacts=len(art), artifact_ms=float(sum(e["audible_ms"] for e in art)),
        # ...and the stable loop's own hang-over after a note: the room being ringy, not a ring
        tails=len(tails), tail_ms=float(sum(e["audible_ms"] for e in tails)))
    return eps, summary


def end_margin(loop, r):
    """Where the loop stands at the end, by the stability condition itself: the
    loop response, at the final gain, through the filters the guard is holding
    (the engine's own filter shape, complex - a filter moves phase too). Negative
    is stable by that many decibels. The duck is not counted: gain it holds down
    is gain not bought."""
    f, H = loop.response()
    J = np.ones_like(H)
    for nf in r.get("notches", []):
        if nf["depth_db"] < -0.1: J = J * peaking(f, loop.fs, nf["freq"], nf["q"], nf["depth_db"])
    rungs = ladder(f, H * 10 ** (r["g_db"][-1] / 20.0) * J, min_db=-200.0)
    return float(rungs[0]["margin_db"]) if rungs else -200.0


def score(loop, r, closed=True):
    """Everything about one run, as one flat row."""
    fs = r["fs"]
    row = dict(voice_change(r["yn"], r["refn"], fs))
    row.update({"guard_" + k: v for k, v in voice_change(r["xout"], r["xin"], fs).items() if k in ("change_pct", "removed_pct")})
    eps, s = rings(r); row.update(s)
    row["cut_1k_4k"] = band_gain(r["xin"], r["xout"], fs, 1000, 4000)
    row["cut_4k_16k"] = band_gain(r["xin"], r["xout"], fs, 4000, 16000)
    if len(r["t"]):
        sing = r["t"] >= PREROLL_S
        row.update(filters_mean=float(np.mean(r["filters"][sing])) if np.any(sing) else 0.0,
                   filters_max=int(np.max(r["filters"])), filters_end=int(r["filters"][-1]),
                   duck_s=float(np.sum(r["duck"] < -0.5) * (r["t"][1] - r["t"][0])) if len(r["t"]) > 1 else 0.0,
                   duck_episodes=int(r["rescue"]["episodes"]), duck_deepest_db=float(r["rescue"]["deepest_db"]),
                   events=int(r["events"]))
        if closed: row["end_margin_db"] = end_margin(loop, r)
    else:
        row.update(filters_mean=0.0, filters_max=0, filters_end=0, duck_s=0.0, duck_episodes=0, duck_deepest_db=0.0, events=0)
    return row, eps


# ============================================================================ the standard study

MARGINS = (-6, -3, 0, 3, 6, 10, 15, 20)


# The slow push is one trajectory through a room, and where it finally loses its grip is decided by
# the noise as much as by the guard: the same build, same room, same singer, with only the room-noise
# seed changed, read 16.8 to 22.7 dB (2026-10-04). One push cannot tell two builds apart. So the
# figure is the MEDIAN of five, and the range is reported beside it.
PUSH_SEEDS = (7, 1, 2, 3, 4)


def _case(args):
    """One (room, voice, case, guarded) run - a process-pool job."""
    room, vname, case, guarded, wav = args[:5]
    seed = args[5] if len(args) > 5 else 7
    x, info = voice(vname)
    src = np.concatenate([np.zeros(int(PREROLL_S * FS)), x])
    if case == "alone":
        loop = _loop(room); r = run_loop(loop, src, None, guarded, seed=seed)
    elif case == "move":
        # the survey's last phase: held 6 dB over, then the microphone travels
        t0 = PREROLL_S + 8.0; metres = 0.10 if room == "rig" else 0.30
        loop = _loop(room, (t0, t0 + 4.0, metres)); r = run_loop(loop, src, 6.0, guarded, seed=seed)
    elif case == "push":
        # a slow push to failure: half a decibel a second from 6 under, the singer on a loop
        seconds = 64.0; reps = int(np.ceil(seconds * FS / len(x)))
        src = np.tile(x, reps)[:int(seconds * FS)]
        loop = _loop(room); r = run_loop(loop, src, lambda t: -6.0 + 0.5 * t, guarded, seed=seed)
    else:
        loop = _loop(room); r = run_loop(loop, src, float(case), guarded, seed=seed)
    row, eps = score(loop, r, closed=(case != "alone"))
    row.update(room=room, voice=vname, case=str(case), guarded=bool(guarded), seconds=len(src) / FS, seed=seed)
    if case == "push":
        # how far it got: the fader setting when the first audible ring began, and when a ring was audible for a quarter second
        aud = sorted((e for e in eps if e["audible_ms"] > 0.0 and e["kind"] == "ring"), key=lambda e: e["t_on"])
        row["first_slip_db"] = float(-6.0 + 0.5 * aud[0]["t_on"]) if aud else None
        lost = [e for e in aud if e["audible_ms"] >= 250.0]
        row["held_to_db"] = float(-6.0 + 0.5 * lost[0]["t_on"]) if lost else float(-6.0 + 0.5 * seconds)
    if wav:
        import soundfile as sf
        os.makedirs(wav, exist_ok=True)
        st = np.stack([r["yn"], r["refn"]], axis=1); st = st / max(1.0, np.abs(st).max()) * 10 ** (12 / 20.0)
        sf.write(os.path.join(wav, f"quality-{room}-{vname}-{case}{'' if guarded else '-noguard'}.wav"),
                 np.clip(st, -1, 1), FS, subtype="PCM_16")
    return row, eps


def _be_polite():
    try: os.nice(10)
    except OSError: pass


_LOOPS = {}
def _loop(room, move=None):
    key = (room, move)
    if key not in _LOOPS: _LOOPS[key] = Loop(room, move=move)
    return _LOOPS[key]


def study(rooms=("rig", "hall"), voices=None, cases=None, wav=None, jobs=None, verbose=False):
    voices = voices or available()
    cases = cases or (["alone"] + list(MARGINS) + ["move", "push"])
    work = []
    for room in rooms:
        for v in voices:
            for c in cases:
                work.append((room, v, c, True, wav))
                if c == "push": work += [(room, v, c, True, None, sd) for sd in PUSH_SEEDS[1:]]
                if c in (-6, -3): work.append((room, v, c, False, wav))        # what the loop itself does, no guard
    # This machine is also the rig. Sixty-eight runs across every core starved the live engine's
    # app of CPU for more than six seconds, three times, on 2026-10-04 (see EngineSupervisor.cs).
    # So: leave two cores alone, and run at low priority - the engine and its app come first.
    jobs = jobs or max(1, min(len(work), (os.cpu_count() or 4) - 2))
    if jobs > 1:
        import multiprocessing as mp
        with mp.get_context("fork").Pool(jobs, initializer=_be_polite) as pool: res = pool.map(_case, work, chunksize=1)
    else:
        res = [_case(w) for w in work]
    # fold the repeated pushes into one row: the median, and the range beside it
    pushes = {}
    for row, _ in res:
        if row["case"] == "push": pushes.setdefault((row["room"], row["voice"]), []).append(row["held_to_db"])
    rows, eps = [], {}
    for row, e in res:
        if row["case"] == "push":
            if row["seed"] != PUSH_SEEDS[0]: continue
            vals = sorted(pushes[(row["room"], row["voice"])])
            row.update(held_to_db=float(np.median(vals)), held_to_min_db=vals[0], held_to_max_db=vals[-1], held_to_runs=len(vals))
        rows.append(row); eps[(row["room"], row["voice"], row["case"], row["guarded"])] = e
    return rows, eps


def fmt(v, spec="{:.1f}", none="-"):
    return none if v is None else spec.format(v)


def table(rows):
    print(f"{'room':<5} {'voice':<9} {'case':>6} {'guard':>5} | {'change':>6} {'added':>6} {'taken':>6} {'worst s':>7} {'dist':>5} |"
          f" {'rings':>5} {'heard':>5} {'audible':>8} {'catch':>6} {'kill':>6} {'loudest':>7} {'ghost':>6} {'tails':>6} | {'filt':>4} {'max':>3} {'duck':>4} {'1-4k':>5} {'4-16k':>5} {'margin':>6}")
    for r in rows:
        print(f"{r['room']:<5} {r['voice']:<9} {r['case']:>6} {'on' if r['guarded'] else 'OFF':>5} |"
              f" {r['change_pct']:5.1f}% {r['added_pct']:5.1f}% {r['removed_pct']:5.1f}% {r['worst_pct']:6.1f}% {r['distance_db']:5.1f} |"
              f" {r['rings']:5d} {r['audible']:5d} {r['audible_ms']:6.0f}ms {fmt(r['worst_catch_ms'], '{:+.0f}'):>6} {fmt(r['worst_kill_ms'], '{:.0f}'):>6}"
              f" {fmt(r['loudest_db']):>7} {r['artifact_ms']:4.0f}ms {r['tail_ms']:4.0f}ms | {r['filters_mean']:4.1f} {r['filters_max']:3d} {r['duck_episodes']:4d} {r['cut_1k_4k']:5.1f} {r['cut_4k_16k']:5.1f}"
              f" {fmt(r.get('end_margin_db')):>6}"
              + (f"   held to {fmt(r.get('held_to_db'), '{:+.1f}')} dB (median of {r.get('held_to_runs', 1)}: "
                 f"{fmt(r.get('held_to_min_db'), '{:+.1f}')} to {fmt(r.get('held_to_max_db'), '{:+.1f}')})" if r["case"] == "push" else ""))


def headline(rows, room="rig", vname="synth"):
    """The handful of numbers a results page shows for one room and voice."""
    pick = {r["case"]: r for r in rows if r["room"] == room and r["voice"] == vname and r["guarded"]}
    out = {}
    if "alone" in pick:
        out.update(alone_change_pct=pick["alone"]["change_pct"], alone_filters_max=pick["alone"]["filters_max"])
    for c in ("-6", "6", "10", "15", "20"):
        if c in pick:
            k = c.replace("-", "m")
            out.update({f"at{k}_change_pct": pick[c]["change_pct"], f"at{k}_audible_ms": pick[c]["audible_ms"],
                        f"at{k}_catch_ms": pick[c]["worst_catch_ms"], f"at{k}_kill_ms": pick[c]["worst_kill_ms"],
                        f"at{k}_filters": pick[c]["filters_mean"]})
    if "push" in pick:
        out.update(held_to_db=pick["push"].get("held_to_db"), held_to_min_db=pick["push"].get("held_to_min_db"),
                   held_to_max_db=pick["push"].get("held_to_max_db"))
    if "move" in pick:
        out.update(move_audible_ms=pick["move"]["audible_ms"], move_change_pct=pick["move"]["change_pct"])
    return out


# ============================================================================ the ruler, checked

def selftest_quiet():
    """The self-check, saying nothing unless it fails."""
    import contextlib, io
    buf = io.StringIO()
    with contextlib.redirect_stdout(buf): bad = selftest()
    if bad: print(buf.getvalue())
    return ["the ruler failed its own check: " + b for b in bad]


def selftest():
    """Known inputs, known answers. A ruler that has not been checked against
    something of known length measures nothing."""
    bad = []
    x, _ = voice("synth"); fs = FS
    def near(name, got, want, tol):
        ok = abs(got - want) <= tol
        print(f"   {'ok  ' if ok else 'FAIL'} {name}: {got:.2f} (expect {want:.2f} +/- {tol})")
        if not ok: bad.append(name)
    print("the voice-change measure")
    near("identical signals", voice_change(x, x, fs)["change_pct"], 0.0, 0.01)
    # The scale, so a percentage means something: the whole voice turned down by
    # 1, 3 and 6 dB. (A little more than the bare power law gives - 7, 19, 34 -
    # because the quiet bands sit near the room's floor and lose more of
    # themselves per decibel.)
    for dbv, want in ((-1.0, 8.0), (-3.0, 22.5), (-6.0, 40.5)):
        v = voice_change(x * 10 ** (dbv / 20.0), x, fs)
        near(f"{dbv:+.0f} dB everywhere, change %", v["change_pct"], want, 1.5)
        near(f"{dbv:+.0f} dB everywhere, distance dB", v["distance_db"], abs(dbv), 0.25)
        near(f"{dbv:+.0f} dB everywhere is all 'removed'", v["added_pct"], 0.0, 0.05)
    for f0, lo, hi in ((3000.0, 1.0, 5.0), (9500.0, 0.0, 1.0)):
        b, a = peaking_eq(fs, f0, 12.0, -18.0)
        one = voice_change(signal.lfilter(b, a, x), x, fs)["change_pct"]
        near(f"one -18 dB filter at {f0:.0f} Hz, Q 12", one, (lo + hi) / 2, (hi - lo) / 2)
    print("the ring finder")
    t = np.arange(len(x)) / fs
    # a ring as a ring behaves: up from the floor at half a decibel a millisecond, level at -40 dBFS, gone at 200 ms.
    # It crosses the audible line (-50) 40 ms in, so it is audible for 160 ms.
    u = t - 5.0
    amp = np.where((u >= 0) & (u < 0.2), 10 ** (np.minimum(-70.0 + 500.0 * u, -40.0) / 20.0), 0.0)
    tone = amp * np.sqrt(2) * np.sin(2 * np.pi * 9000.3 * t)
    r = dict(fs=fs, yn=x + tone, refn=x, refin=x, xin=x + tone, xout=x + tone)
    eps, s = rings(r)
    near("a ring that grows to -40 dBFS and stops: found once", s["audible"], 1, 0)
    if s["audible"]:
        e = max((e for e in eps if e["kind"] == "ring"), key=lambda e: e["audible_ms"])
        near("  its frequency", e["hz"], 9000.3, 25.0)
        near("  its level", e["peak_db"], -40.0, 1.0)
        near("  how long it was audible, ms", e["audible_ms"], 160.0, 8.0)
        near("  never cut: catch is None", 0.0 if e["catch_ms"] is None else 1.0, 0.0, 0.0)
    eps, s = rings(dict(fs=fs, yn=x, refn=x, refin=x, xin=x, xout=x))
    near("the singer alone: no rings", s["rings"], 0, 0)
    quiet = 10 ** (-62.0 / 20.0) * np.sqrt(2) * np.sin(2 * np.pi * 6000.0 * t) * ((t >= 8.0) & (t < 8.3))
    eps, s = rings(dict(fs=fs, yn=x + quiet, refn=x, refin=x, xin=x + quiet, xout=x + quiet))
    near("a -62 dBFS tone: seen, not audible", s["rings"] - s["audible"] - s["tails"] - s["artifacts"], 1, 0)
    # caught: the guard's output carries the ring 20 dB down from 30 ms after it became audible
    gate = np.where(t >= 5.07, 10 ** (-20.0 / 20.0), 1.0)
    r = dict(fs=fs, yn=x + tone * gate, refn=x, refin=x, xin=x + tone, xout=x + tone * gate)
    eps, s = rings(r)
    if s["audible"]:
        near("a ring cut 20 dB, 30 ms after it became audible: catch, ms", s["worst_catch_ms"], 30.0, 4.0)
        near("  and audible for about as long", s["worst_audible_ms"], 30.0, 6.0)
    else: bad.append("cut ring not found")
    r = dict(fs=fs, yn=x + tone, refn=x, refin=x, xin=x, xout=x + tone)
    eps, s = rings(r)
    near("a line only in the output is the guard's own: no ring", s["audible"], 0, 0)
    near("  ...counted as a ghost instead", s["artifacts"], 1, 0)
    # a tail: there at once at -40 dBFS and falling 0.3 dB a millisecond - a stable room letting go of a note
    tail = np.where((u >= 0) & (u < 0.2), 10 ** ((-40.0 - 300.0 * u) / 20.0), 0.0) * np.sqrt(2) * np.sin(2 * np.pi * 7300.0 * t)
    eps, s = rings(dict(fs=fs, yn=x + tail, refn=x, refin=x, xin=x + tail, xout=x + tail))
    near("a line that only falls is a tail, not a ring", s["audible"], 0, 0)
    near("  ...counted as a tail", s["tails"], 1, 0)
    return bad


# ============================================================================ the gate

# The quick subset the ship gate runs: the rig-like room, the synthetic singer.
# These are REGRESSION thresholds - what d55f580 measured on 2026-10-04, with a
# little room - and say only "no worse than it was". The goals they are on the
# way to (3 % with nothing ringing, 15 % while holding feedback, caught in
# 10 ms, killed in 15) are in docs/TODO-AS-BUILT.md, and this build is far from
# the first two. Tighten a line when the number improves. Never loosen one to
# make a red build green.
#
# The two voice-change lines were 35 and 51 for a few hours. They moved to 40
# and 53 on the same day, with the engine untouched, because the RULER was
# wrong: the test library held a quiet filter 2 s where the engine in the room
# holds it 10 s (EngineDefaults.h). Measured on the room's own machine the same
# build takes 37.0 % and 49.7 %. That is a correction, not a loosening.
GATE_CASES = ["alone", -6, 6, 10]
GATE = [  # case, field, limit, why
    ("alone", "change_pct", 40.0, "what the guard takes from a singer with nothing ringing (goal 3 %)"),
    ("alone", "duck_episodes", 0, "the rescue duck must never fire on a voice alone"),
    ("-6", "audible", 0, "a ring heard six decibels UNDER the room's limit is the guard's own doing"),
    ("6", "audible", 0, "no ring may be heard 6 dB over"),
    ("10", "audible", 0, "no ring may be heard 10 dB over"),
    ("10", "change_pct", 53.0, "what holding 10 dB over costs the voice (goal 15 %)"),
]
GOALS = dict(alone=3.0, m6=3.0, at6=10.0, at10=15.0)


def gate(rows):
    bad = []
    pick = {r["case"]: r for r in rows if r["guarded"]}
    for case, field, limit, why in GATE:
        if case not in pick: bad.append(f"{case}: did not run"); continue
        v = pick[case][field]
        if v > limit: bad.append(f"{case}: {field} = {v:.1f}, limit {limit} - {why}")
    return bad


def _record(rows, ok, gate_run):
    """One row in the results log per room and voice (ledger.py)."""
    try:
        import ledger
    except ImportError:
        return
    for room in sorted({r["room"] for r in rows}):
        for v in sorted({r["voice"] for r in rows}):
            sub = [r for r in rows if r["room"] == room and r["voice"] == v]
            if not sub: continue
            _, info = voice(v)
            # the quick gate subset under its own name, so it never stands in for the full study on the results page
            ledger.record("singer-gate" if gate_run else "singer-in-loop", "simulated",
                          dict(room=room, voice=v, voice_id=info["id"], **headline(rows, room, v)),
                          ok=ok if gate_run else None, cases=sub)


if __name__ == "__main__":
    args = sys.argv[1:]
    def opt(name, default=None):
        if name in args:
            i = args.index(name); v = args[i + 1]; del args[i:i + 2]; return v
        return default
    wav = opt("--wav"); room = opt("--room"); vname = opt("--voice"); case = opt("--case"); out_json = opt("--json")
    jobs = opt("--jobs"); jobs = int(jobs) if jobs else None
    if "--selftest" in args:
        bad = selftest()
        print("\nFAIL" if bad else "\nPASS  the ruler reads true on known inputs")
        sys.exit(1 if bad else 0)
    t0 = time.time()
    rooms = (room,) if room else ("rig", "hall")
    vs = [vname] if vname else None
    is_gate = "--gate" in args
    if is_gate:
        rooms, vs, cases = ("rig",), ["synth"], GATE_CASES
    else:
        cases = None
        if case: cases = [case if case in ("alone", "move", "push") else int(case)]
    rows, eps = study(rooms, vs, cases, wav=wav, jobs=jobs)
    table(rows)
    if "-v" in args:
        for key, el in eps.items():
            for e in sorted(el, key=lambda e: e["t0"]):
                if e["audible_ms"] > 0:
                    print(f"   {key[0]} {key[1]} {key[2]}: {dict(ring='AUDIBLE', artifact='ghost  ', tail='tail   ')[e['kind']]} {e['hz']:7.0f} Hz at {e['t_on']:6.2f} s for {e['audible_ms']:5.0f} ms, peak {e['peak_db']:6.1f} dB, "
                          f"catch {fmt(e['catch_ms'], '{:+.0f}')} ms, kill {fmt(e['kill_ms'], '{:.0f}')} ms")
                else:
                    print(f"   {key[0]} {key[1]} {key[2]}: quiet   {e['hz']:7.0f} Hz at {e['t0']:6.2f} s, peak {e['peak_db']:6.1f} dB")
    print(f"\n{len(rows)} runs in {time.time() - t0:.0f} s")
    if out_json:
        json.dump(rows, open(out_json, "w"), indent=1)
    bad = []
    if is_gate:
        bad = selftest_quiet() + gate(rows)
        h = headline(rows, "rig", "synth")
        print(f"\nagainst the goals:  nothing ringing {h['alone_change_pct']:.0f} % (goal {GOALS['alone']:.0f})   "
              f"6 dB over {h['at6_change_pct']:.0f} % (goal {GOALS['at6']:.0f})   10 dB over {h['at10_change_pct']:.0f} % (goal {GOALS['at10']:.0f})")
    if cases is None or is_gate:
        _record(rows, not bad, is_gate)
    if is_gate:
        if bad:
            print("\nFAIL"); [print("   " + b) for b in bad]; sys.exit(1)
        print("\nPASS  no worse than the recorded build (the goals are another matter)")
