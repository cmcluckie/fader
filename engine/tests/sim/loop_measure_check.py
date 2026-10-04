#!/usr/bin/env python3
"""
Does a measured loop predict what rings?

The simulator knows its own loop response exactly, so the measurement in
loop_measure.py can be checked against the truth rather than against hope:

  1. play the sweep through the simulated loop, open, with room noise on the
     microphone - the same thing the engine's probe does in a real room;
  2. estimate the response from those two signals only;
  3. compare the measured ladder with the simulator's own, rung by rung;
  4. close the loop unguarded, a little over the edge, and see which note
     actually rings.

Two rooms: the reverberant one the rest of the gate uses, and the one shaped
like the rig (microphone half a metre from the speaker, response peaking near
9.5 kHz).

    python3 loop_measure_check.py            # prints the comparison
    python3 loop_measure_check.py --gate     # exit 1 if the prediction is off
"""
import sys, os
import numpy as np
from scipy import signal

HERE = os.path.dirname(os.path.abspath(__file__)); sys.path.insert(0, HERE)
from feedback_sim import FeedbackSim, peaking_eq, sos_from_biquads
from loop_measure import ess, estimate_response, ladder, loop_delay_ms, plan
from fk_in_loop import FkGuard


def room(kind):
    sim = FeedbackSim(fs=44100); fs = sim.fs
    if kind == "rig":
        b, a = peaking_eq(fs, 9500, 1.0, 14.0)
        sim.mic_sos = np.vstack([signal.butter(2, 1500, "hp", fs=fs, output="sos"),
                                 sos_from_biquads([(b, a)]),
                                 signal.butter(2, 16000, "lp", fs=fs, output="sos")])
        sim.build_room(dims=(6, 5, 2.8), rt60=0.4, speaker_pos=(1.0, 2.5, 1.5), mic_path=[(0.0, (1.5, 2.5, 1.5))])
    else:
        sim.build_room(dims=(9, 7, 3.2), rt60=0.7, speaker_pos=(1.0, 3.5, 1.7), mic_path=[(0.0, (4.0, 3.5, 1.6))])
    return sim


def probe(sim, sweep, noise_db=-75.0, seed=7):
    """The sweep through the simulated loop, open: gain, microphone, speaker, room,
    plus room noise at the microphone. Linear part only - the sweep is 20 dB
    under the amplifier's clip level."""
    n = 1
    while n < len(sweep) + len(sim.rirs[0]) + sim.fs: n *= 2
    f, H = sim.loop_response(n=n)
    y = np.fft.irfft(np.fft.rfft(sweep, n) * H, n)[:len(sweep) + sim.fs // 2]
    rng = np.random.default_rng(seed)
    return y + (10 ** (noise_db / 20.0)) * rng.standard_normal(len(y))


def what_rings(sim, seconds=5.0):
    """Close the loop with no guard and report the note it settles on."""
    out, mic, ev = sim.run(seconds=seconds, seed="click")
    seg = np.asarray(mic)[-sim.fs:]
    S = np.abs(np.fft.rfft(seg * np.hanning(len(seg)))); f = np.fft.rfftfreq(len(seg), 1.0 / sim.fs)
    return float(f[int(np.argmax(S))])


def check(kind, verbose=True):
    sim = room(kind)
    sim.set_gain_margin(-6.0)                       # stable: six decibels under the edge
    truth = sim.predicted_howls(top=8, min_db=-40.0, rank="margin")
    sweep = ess(sim.fs, seconds=1.5, level_db=-20.0)
    mic = probe(sim, sweep)
    f, H, h = estimate_response(sweep, mic, sim.fs)
    got = ladder(f, H)

    rows, worst_hz, worst_db, found = [], 0.0, 0.0, 0
    for t in truth[:5]:
        near = min(got[:12], key=lambda c: abs(c["freq"] - t["freq"])) if got else None
        ok = near is not None and abs(near["freq"] - t["freq"]) / t["freq"] <= 0.01
        if ok:
            found += 1
            worst_hz = max(worst_hz, abs(near["freq"] - t["freq"]) / t["freq"] * 100)
            worst_db = max(worst_db, abs(near["margin_db"] - t["margin_db"]))
        rows.append((t, near if ok else None))

    # ...and the proof that matters: push it over the edge and listen.
    sim.set_gain_margin(3.0)
    rang = what_rings(sim)
    first = got[0]["freq"] if got else 0.0
    top3 = [c["freq"] for c in got[:3]]
    hit = any(abs(rang - c) / c <= 0.01 for c in top3)

    if verbose:
        print(f"\n{kind}: round trip {loop_delay_ms(h, sim.fs):.1f} ms, measured headroom {-got[0]['margin_db']:.1f} dB "
              f"(true {-truth[0]['margin_db']:.1f})")
        print(f"   {'true rung':>10} {'margin':>7} | {'measured':>9} {'margin':>7}")
        for t, g in rows:
            gs = f"{g['freq']:9.0f} {g['margin_db']:7.1f}" if g else f"{'missed':>9} {'':>7}"
            print(f"   {t['freq']:10.0f} {t['margin_db']:7.1f} | {gs}")
        print(f"   unguarded, 3 dB over: rings at {rang:.0f} Hz; predicted first {first:.0f} Hz, top three "
              f"{', '.join(f'{c:.0f}' for c in top3)}  -> {'HIT' if hit else 'MISS'}")
    return dict(found=found, worst_hz_pct=worst_hz, worst_db=worst_db, hit=hit,
                headroom_err=abs(got[0]["margin_db"] - truth[0]["margin_db"]) if got else 99.0)


def cold_start(kind, margin_db, pins=None, seconds=6.0):
    """The guard in the loop, started cold at `margin_db` over the untreated edge,
    with or without the planned filters pinned in advance."""
    sim = room(kind); sim.set_gain_margin(margin_db)
    g = FkGuard(sim.fs, sim.block); sim.external_eq = g
    for p in (pins or []): g.pin(p["freq"], p["depth_db"], p["q"])
    out, mic, ev = sim.run(seconds=seconds, seed="click"); mic = np.asarray(mic)
    n = int(0.02 * sim.fs); k = len(mic) // n
    env = 20 * np.log10(np.sqrt(np.mean(mic[:k * n].reshape(k, n) ** 2, axis=1)) + 1e-12)
    r = g.rescue()
    return dict(above_40=float(np.sum(env > -40) * 0.02), above_50=float(np.sum(env > -50) * 0.02),
                last=float(20 * np.log10(np.sqrt(np.mean(mic[-2 * sim.fs:] ** 2)) + 1e-12)),
                events=g.events, filters=len(g.notches()), rescues=r["episodes"])


def preplace_check(kind, raise_db=15.0, verbose=True):
    """Measure six decibels under the edge, plan for `raise_db` more (nine over,
    untreated), pin, and start cold there - against the same start with nothing
    pinned."""
    sim = room(kind); sim.set_gain_margin(-6.0)
    sweep = ess(sim.fs, seconds=1.5, level_db=-20.0)
    f, H, h = estimate_response(sweep, probe(sim, sweep), sim.fs)
    p = plan(f, H, sim.fs, raise_db)
    reactive = cold_start(kind, -6.0 + raise_db)
    pinned = cold_start(kind, -6.0 + raise_db, pins=p["filters"])
    if verbose:
        print(f"\n{kind}: raising {raise_db:.0f} dB from 6 under the edge")
        print(f"   plan: {len(p['filters'])} filter(s), predicted headroom afterwards {p['headroom_after_db']:.1f} dB"
              f"{'' if p['done'] else '  (NOT enough filters)'}")
        print(f"   cost {p['cost_1k_4k']:+.1f} dB over 1-4 kHz, {p['cost_4k_16k']:+.1f} dB over 4-16 kHz"
              f"  ->  NET {p['net_1k_4k']:+.1f} dB and {p['net_4k_16k']:+.1f} dB for a {raise_db:.0f} dB raise")
        for x in p["filters"]: print(f"        {x['freq']:7.0f} Hz  Q {x['q']:4.1f}  {x['depth_db']:6.1f} dB")
        print(f"   {'cold start':<22}{'s > -40':>8}{'s > -50':>8}{'last 2 s':>10}{'events':>8}{'filters':>9}{'rescues':>9}")
        for label, r in (("reactive only", reactive), ("pinned first", pinned)):
            print(f"   {label:<22}{r['above_40']:8.2f}{r['above_50']:8.2f}{r['last']:10.1f}{r['events']:8d}{r['filters']:9d}{r['rescues']:9d}")
    return p, reactive, pinned


if __name__ == "__main__":
    gate = "--gate" in sys.argv
    bad = []
    for kind in ("reverberant", "rig"):
        r = check(kind)
        if r["found"] < 4: bad.append(f"{kind}: only {r['found']} of the first 5 rungs found within 1%")
        if r["worst_db"] > 1.5: bad.append(f"{kind}: a rung's margin is off by {r['worst_db']:.1f} dB")
        if r["headroom_err"] > 1.0: bad.append(f"{kind}: headroom off by {r['headroom_err']:.1f} dB")
        if not r["hit"]: bad.append(f"{kind}: the note that rang was not among the first three predicted")
    for kind in ("reverberant", "rig"):
        p, reactive, pinned = preplace_check(kind)
        if pinned["events"] > reactive["events"]:
            bad.append(f"{kind}: pinning first made the guard work harder ({pinned['events']} events against {reactive['events']})")
        if pinned["above_50"] > reactive["above_50"] + 0.05:
            bad.append(f"{kind}: pinning first left the room louder for longer")
    print()
    if bad:
        print("FAIL"); [print("   " + b) for b in bad]
        sys.exit(1 if gate else 0)
    print("PASS  the measured ladder matches the true one and names the note that rings")
