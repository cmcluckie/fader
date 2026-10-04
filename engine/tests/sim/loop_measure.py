#!/usr/bin/env python3
"""
Measure the loop, rank what will ring.

Plan item 2 (docs/PLAN.md). Everything else in this project waits for the room
to sing and then goes looking for the note. This asks the room first: play a
sweep out of the return with the microphone's pass-through muted - the loop is
open, so it cannot ring - and what comes back at the microphone, divided by what
went out, is the loop's own response. Howling needs that response to be at
least one in magnitude and a whole number of turns in phase at the same
frequency (Nyquist), so:

    candidates  = the frequencies where the phase comes round to zero
    margin (dB) = 20 log10 |H| there     (negative: that far below ringing)

The largest margin is the frequency the room will ring at first, and how many
decibels of gain are left before it does. The list below it is the order the
rest will follow in.

This module is the arithmetic, shared by the live tool (scripts/measure_loop.py)
and by the check against the simulator (loop_measure_check.py), where the true
response is known.
"""
import numpy as np


def ess(fs, seconds=1.5, f0=100.0, f1=18000.0, level_db=-20.0, fade=0.02):
    """An exponential sine sweep: equal time per octave, so the low end - where
    room noise lives - gets as long as the top. Faded at both ends so it does
    not click."""
    n = int(seconds * fs)
    t = np.arange(n) / fs
    k = np.log(f1 / f0)
    x = np.sin(2 * np.pi * f0 * seconds / k * (np.exp(t / seconds * k) - 1.0))
    m = int(fade * fs)
    x[:m] *= 0.5 - 0.5 * np.cos(np.pi * np.arange(m) / m)
    x[-m:] *= 0.5 + 0.5 * np.cos(np.pi * np.arange(m) / m)
    return (10 ** (level_db / 20.0)) * x


def estimate_response(sweep, mic, fs, gate_s=0.35, band=(100.0, 18000.0)):
    """H(f) from what went out and what came back.

    Regularised spectral division, then the impulse response is cut off after
    `gate_s`: everything later than the room's decay is noise, and leaving it
    in is what makes a measured phase jitter enough to invent crossings.
    Returns (f, H, h) - the response on a dense grid and the impulse response.
    """
    n = 1
    while n < len(sweep) + len(mic): n *= 2
    S = np.fft.rfft(sweep, n); M = np.fft.rfft(mic, n)
    f = np.fft.rfftfreq(n, 1.0 / fs)
    # Regularise where the sweep has no energy (outside its band) instead of
    # dividing noise by nothing.
    p = np.abs(S) ** 2
    inband = (f >= band[0]) & (f <= band[1])
    eps = 1e-3 * np.median(p[inband])
    H = M * np.conj(S) / (p + eps)
    h = np.fft.irfft(H, n)
    g = int(gate_s * fs)
    win = np.ones(g); tail = int(0.05 * fs)
    win[-tail:] = 0.5 + 0.5 * np.cos(np.pi * np.arange(tail) / tail)
    h = h[:g] * win
    nd = 1 << 18
    Hd = np.fft.rfft(h, nd); fd = np.fft.rfftfreq(nd, 1.0 / fs)
    return fd, Hd, h


def ladder(f, H, band=(150.0, 17000.0), min_db=-40.0):
    """The frequencies where the loop phase crosses zero going down, with the
    loop gain there in dB. Sorted loudest first: the order the room rings in."""
    mag = np.abs(H); ph = np.angle(H)
    s = np.sign(ph)
    cross = np.where((s[:-1] > 0) & (s[1:] <= 0) & (np.abs(ph[1:] - ph[:-1]) < np.pi))[0]
    out = []
    for i in cross:
        a, b = ph[i], ph[i + 1]
        w = a / (a - b) if a != b else 0.0
        fc = f[i] + w * (f[i + 1] - f[i])
        if not (band[0] <= fc <= band[1]): continue
        m = 20 * np.log10(max(mag[i] + w * (mag[i + 1] - mag[i]), 1e-12))
        if m >= min_db: out.append(dict(freq=float(fc), margin_db=float(m)))
    out.sort(key=lambda c: -c["margin_db"])
    return out


def loop_delay_ms(h, fs):
    """Where the impulse response starts: the round trip. Rungs are 1/delay apart."""
    k = int(np.argmax(np.abs(h)))
    return 1000.0 * k / fs


def peaking(f, fs, f0, q, gain_db):
    """The engine's own filter, as a complex response: RBJ peaking EQ, with the
    bank's rule that a deeper cut is a proportionally narrower one
    (NotchBank::qForDepth). Planning with any other shape plans a different room."""
    qe = q * max(1.0, abs(gain_db) / 18.0)
    A = 10 ** (gain_db / 40.0); w0 = 2 * np.pi * f0 / fs
    al = np.sin(w0) / (2 * qe); c = np.cos(w0)
    z = np.exp(-1j * 2 * np.pi * np.asarray(f) / fs)
    return ((1 + al * A) - 2 * c * z + (1 - al * A) * z * z) / ((1 + al / A) - 2 * c * z + (1 - al / A) * z * z)


def plan(f, H, fs, raise_db, target_db=-6.0, max_filters=12, q_narrow=30.0, q_wide_min=1.5,
         max_depth_db=-30.0):
    """Which filters to pin before raising the gain by `raise_db`.

    One at a time, on the response as it would be with the raise and with the
    filters already chosen: take the rung nearest to ringing; if it is one of a
    crowd - three or more rungs over the line within a third of an octave - the
    loop has a hump there and gets one wide filter across it, otherwise the
    rung gets a narrow one; apply it; look again. Overlap and the phase the
    filters themselves add are in the arithmetic rather than hoped away.

    Returns the filters, the ladder afterwards, and what it costs: the average
    change in level over 1-4 kHz and 4-16 kHz. Past about ten decibels of raise
    a reverberant room wants so many that the cost column is the point.
    """
    g = 10 ** (raise_db / 20.0)
    Hc = H * g
    filters = []
    for _ in range(max_filters):
        rungs = ladder(f, Hc)
        if not rungs or rungs[0]["margin_db"] <= target_db: break
        top = rungs[0]
        crowd = [r for r in rungs if r["margin_db"] > target_db and abs(np.log2(r["freq"] / top["freq"])) < 1.0 / 3.0]
        if len(crowd) >= 3:
            lo, hi = min(r["freq"] for r in crowd), max(r["freq"] for r in crowd)
            centre = float(np.sqrt(lo * hi))
            q = float(np.clip(centre / max(hi - lo, 1.0), q_wide_min, q_narrow))
        else:
            centre, q = top["freq"], q_narrow
        depth = float(max(max_depth_db, -(top["margin_db"] - target_db + 1.0)))
        filters.append(dict(freq=centre, q=q, depth_db=depth))
        Hc = Hc * peaking(f, fs, centre, q, depth)
    after = ladder(f, Hc)
    def band_cost(lo, hi):
        sel = (f >= lo) & (f < hi)
        # averaged over log frequency, which is how it is heard
        w = 1.0 / np.maximum(f[sel], 1.0)
        return float(np.sum(w * 20 * np.log10(np.abs(Hc[sel]) / np.maximum(np.abs(H[sel] * g), 1e-12))) / np.sum(w))
    c1, c4 = band_cost(1000, 4000), band_cost(4000, 16000)
    return dict(filters=filters, after=after,
                headroom_after_db=-(after[0]["margin_db"]) if after else 99.0,
                done=bool(after) and after[0]["margin_db"] <= target_db,
                cost_1k_4k=c1, cost_4k_16k=c4,
                # What the raise is actually worth once the filters are in: the
                # number to quote. Fifteen decibels of fader that costs twelve of
                # equaliser is three decibels, and saying fifteen is a lie.
                net_1k_4k=raise_db + c1, net_4k_16k=raise_db + c4)
