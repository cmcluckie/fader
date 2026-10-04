"""
preship.py — the closed-loop gate. Nothing ships without passing this.

Why this exists as a separate gate
----------------------------------
fk-tests and fk-fuzz both feed the guard a signal it cannot influence. They can
tell you the detector fired and the bank escalated; they cannot tell you the
room got quieter, because in both of them the room is a recording. Feedback is
a loop, and a guard that is never allowed to change the loop has not been
tested against the thing it is for.

This runs the shipping DSP (fk-loopdsp) as the console EQ inside
feedback_sim.py's sample-exact acoustic loop - real impulse response, mic and
speaker directivity, a saturating power amp - so what the guard cuts changes
the loop gain on the next pass.

It is a REGRESSION gate, not a quality bar. The thresholds below are what the
build measured when they were written, minus headroom for run-to-run drift.
They say "this is no worse than it was", which is the only claim a gate can
honestly make. Raise them when the numbers improve; never lower them to make a
red build go green.

    python3 preship.py            # gate
    python3 preship.py --update   # re-record the baseline after a real change
"""
import ctypes, json, os, sys
import numpy as np

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
BASELINE = os.path.join(HERE, "preship-baseline.json")

try:
    from feedback_sim import FeedbackSim, db
except ImportError:
    print("SKIP-FAIL  feedback_sim.py is not importable.\n"
          "           This gate needs the acoustic simulator and pyroomacoustics:\n"
          "             python3 -m pip install pyroomacoustics soundfile scipy\n"
          "           and feedback_sim.py on the path (see README.md).\n"
          "           Refusing to pass a gate that did not run.")
    sys.exit(2)

from fk_in_loop import FkGuard

ROOM = dict(dims=(9, 7, 3.2), rt60=0.7, speaker_pos=(1.0, 3.5, 1.7))
MIC = (4.00, 3.50, 1.60)
SECONDS = 12.0


def band_peak(x, fs, lo, hi, W=4096):
    f = np.fft.rfftfreq(W, 1 / fs); sel = (f >= lo) & (f < hi)
    win = np.hanning(W); best = -200.0
    for s in range(0, max(1, len(x) - W), W // 2):
        seg = x[s:s + W]
        if len(seg) < W: break
        m = np.abs(np.fft.rfft(seg * win)) ** 2
        best = max(best, 10 * np.log10(max(m[sel].sum(), 1e-20)))
    return best


def steady_band(x, fs, lo, hi, W=8192, tail=3.0):
    """Sustained level over the last `tail` seconds, and how ring-like it is.

    Peak short-time energy is the wrong measure here and it cost a night: it is
    dominated by the seed transient, which made a pre-cut arm look 6.5 dB better
    when the sustained howl behind it was 39 dB better.
    """
    seg = x[-int(tail * fs):]
    acc = np.zeros(W // 2 + 1); n = 0
    for i in range(0, max(1, len(seg) - W), W // 2):
        if i + W > len(seg): break
        acc += np.abs(np.fft.rfft(seg[i:i + W] * np.hanning(W))) ** 2; n += 1
    if n == 0: return -200.0, 0.0
    acc /= n
    f = np.fft.rfftfreq(W, 1 / fs); sel = (f >= lo) & (f < hi)
    band = acc[sel]
    if band.sum() <= 0: return -200.0, 0.0
    top = np.sort(band)[::-1][:5].sum() / band.sum()   # 1.0 = a pure ring
    return 10 * np.log10(band.sum() + 1e-20), float(top)


def run(margin_db, guarded, disconnected=False):
    sim = FeedbackSim(fs=44100)
    sim.build_room(mic_path=[(0.0, MIC)], **ROOM)
    sim.set_gain_margin(margin_db)
    guard = None
    if guarded:
        guard = FkGuard(sim.fs, sim.block, disconnected=disconnected)
        sim.external_eq = guard
    _, mic, _ = sim.run(seconds=SECONDS, seed="click")
    hf_s, hf_c = steady_band(mic, sim.fs, 2000, 16000)
    lf_s, lf_c = steady_band(mic, sim.fs, 40, 400)
    return dict(hf_peak=band_peak(mic, sim.fs, 2000, 16000),
                hf_steady=hf_s, lf_steady=lf_s, lf_ringlike=lf_c,
                events=guard.events if guard else 0,
                filters=len(guard.notches()) if guard else 0,
                not_in_loop=guard.not_in_loop() if guard else 0,
                deepest=guard.deepest_db() if guard else 0.0,
                duck_end=guard.rescue()["depth_db"] if guard else 0.0,
                duck_futile=guard.rescue()["futile"] if guard else 0)


def disconnected_check():
    """The guard wired out of its own loop - the fault that cost a session.

    It sees the microphone perfectly and none of its output reaches the
    speakers. Two things must happen and both were broken at once when this
    scenario did not exist:

      1. it must NOTICE, because the symptom is otherwise indistinguishable
         from the software simply failing - the room howls while the guard
         reports itself busy and effective;
      2. it must STOP DIGGING, because past the operator's dial every decibel
         is tone damage in a fight it is not part of.

    And afterwards it must still work. The first version of the give-up check
    latched forever: once flagged, a filter stopped deepening and never retired,
    so it squatted on its frequency and blocked anything else from trying. That
    shipped, because the tests only ever proved the guard could ENTER the state.
    """
    g = run(9.0, True, disconnected=True)
    problems = []
    if g["not_in_loop"] < 1:
        problems.append("disconnected: the guard never noticed it was not in the loop")
    if g["deepest"] < -30.0:
        problems.append(f"disconnected: parked a filter at {g['deepest']:.1f} dB, "
                        f"far past the dial, for no effect")
    # The rescue duck has the same duty. Out of the loop it cannot quieten
    # anything; if it is still down at the end it is simply muting the room.
    if g["duck_end"] < -0.5:
        problems.append(f"disconnected: the rescue duck is still down {g['duck_end']:.1f} dB "
                        f"at the end of a loop it is not in")
    return g, problems


def rig_like(margin_db, duck):
    """A loop shaped like the rig's, started cold.

    The reverberant room above rings low once its top end is held - 440, 630,
    1200 Hz - and the rescue duck is fenced above 4 kHz so that it can never
    act where the voice lives; it cannot be judged there. Every ring on the rig
    has been between 4.4 and 14.6 kHz with the microphone near the speaker, so:
    half a metre apart, a short room, and a microphone response that peaks near
    9.5 kHz. Unguarded this howls at 9 kHz at -10 dBFS for as long as it runs.
    """
    from scipy import signal
    from feedback_sim import peaking_eq, sos_from_biquads
    sim = FeedbackSim(fs=44100); fs = sim.fs
    b, a = peaking_eq(fs, 9500, 1.0, 14.0)
    sim.mic_sos = np.vstack([signal.butter(2, 1500, "hp", fs=fs, output="sos"),
                             sos_from_biquads([(b, a)]),
                             signal.butter(2, 16000, "lp", fs=fs, output="sos")])
    sim.build_room(dims=(6, 5, 2.8), rt60=0.4, speaker_pos=(1.0, 2.5, 1.5), mic_path=[(0.0, (1.5, 2.5, 1.5))])
    sim.set_gain_margin(margin_db)
    guard = FkGuard(sim.fs, sim.block); guard.set_rescue(duck); sim.external_eq = guard
    _, mic, _ = sim.run(seconds=6.0, seed="click")
    mic = np.asarray(mic); n = int(0.02 * fs); k = len(mic) // n
    env = 20 * np.log10(np.sqrt(np.mean(mic[:k * n].reshape(k, n) ** 2, axis=1)) + 1e-12)
    r = guard.rescue()
    return dict(above_30=float(np.sum(env > -30) * 0.02), above_40=float(np.sum(env > -40) * 0.02),
                last=float(20 * np.log10(np.sqrt(np.mean(mic[-2 * fs:] ** 2)) + 1e-12)),
                filters=len(guard.notches()), events=guard.events,
                episodes=r["episodes"], deepest=r["deepest_db"], depth_end=r["depth_db"])


def cold_start_check():
    """A cold start 20 dB over, with the rescue duck and without it.

    This is the case the duck exists for (RescueDuck.h). It has to engage, it
    has to keep the room out of howl territory, and it has to be GONE by the
    end with the room quiet - a duck that stays down has not rescued anything,
    it has turned the system off.
    """
    off, on = rig_like(20.0, False), rig_like(20.0, True)
    problems = []
    if on["episodes"] < 1:
        problems.append("cold start: the rescue duck never engaged - this scenario is not testing it")
    if on["above_30"] > 0.0:
        problems.append(f"cold start: {on['above_30']:.2f} s above -30 dBFS with the duck on")
    if on["above_40"] > 0.15:
        problems.append(f"cold start: {on['above_40']:.2f} s above -40 dBFS with the duck on (limit 0.15)")
    if on["last"] > -60.0:
        problems.append(f"cold start: still at {on['last']:.1f} dBFS at the end with the duck on")
    if on["depth_end"] < -0.5:
        problems.append(f"cold start: the duck is still down {on['depth_end']:.1f} dB at the end")
    return off, on, problems


def measure():
    out = {}
    # +3 dB is the ordinary case and +9 dB is where the bass bug lives.
    # Two scenarios, because a slow gate is a gate that gets skipped.
    for m in (3.0, 9.0):
        off, on = run(m, False), run(m, True)
        out[f"margin_{int(m)}"] = dict(
            hf_suppression=off["hf_steady"] - on["hf_steady"],
            lf_added=on["lf_steady"] - off["lf_steady"],
            lf_ringlike=on["lf_ringlike"],
            events=on["events"], filters=on["filters"])
    return out


# What the gate insists on. Headroom below the recorded values, because the
# simulator is stochastic in its seed noise and a gate that fails on drift gets
# switched off, which is worse than no gate.
CHECKS = [
    ("hf_suppression", "min", 10.0,
     "the guard must make the top end quieter, in a loop that can fight back"),
    ("events", "min", 5,
     "it must actually be detecting - a silent guard passes an HF check by luck"),
]


def gate(now, base):
    bad = []
    print(f"{'scenario':<12}{'HF suppressed':>15}{'LF added':>11}{'ring?':>8}{'events':>8}{'filters':>9}")
    for k in sorted(now):
        n = now[k]
        print(f"{k:<12}{n['hf_suppression']:>14.1f}dB{n['lf_added']:>10.1f}dB"
              f"{100*n['lf_ringlike']:>7.0f}%{n['events']:>8}{n['filters']:>9}")
        for field, kind, limit, why in CHECKS:
            v = n[field]
            if kind == "min" and v < limit:
                bad.append(f"{k}: {field}={v:.1f} below {limit} - {why}")
        if base and k in base:
            drop = base[k]["hf_suppression"] - n["hf_suppression"]
            if drop > 4.0:
                bad.append(f"{k}: HF suppression fell {drop:.1f} dB against the baseline")
            # LF is REPORTED, not gated. Measured 76.2 dB one run and 58.2 dB the
            # next on identical code - which mode wins the unmasking race is
            # decided by the seed noise, so the spread is ~18 dB. A gate on a
            # quantity that noisy fails at random, and a gate that fails at
            # random gets switched off. Watch the column; do not automate it.
            worse = n["lf_added"] - base[k]["lf_added"]
            if worse > 25.0:
                bad.append(f"{k}: bass added is {worse:.1f} dB worse than baseline - "
                           f"beyond even this metric's noise, look at it")
    return bad


if __name__ == "__main__":
    update = "--update" in sys.argv
    base = None
    if os.path.exists(BASELINE) and not update:
        base = json.load(open(BASELINE))

    print("closed-loop gate: the shipping guard inside a real acoustic loop\n")
    now = measure()
    bad = gate(now, base)

    dis, dis_bad = disconnected_check()
    print(f"\n{'disconnected':<12}{'flagged ' + str(dis['not_in_loop']):>15}"
          f"{'deepest ' + format(dis['deepest'], '.1f') + 'dB':>20}"
          f"{dis['events']:>8}{dis['filters']:>9}")
    bad += dis_bad

    cs_off, cs_on, cs_bad = cold_start_check()
    print(f"\ncold start, rig-like loop, 20 dB over   s>-30   s>-40   last 2 s  filters  duck")
    print(f"{'  without the rescue duck':<38}{cs_off['above_30']:>6.2f}{cs_off['above_40']:>8.2f}{cs_off['last']:>10.1f}{cs_off['filters']:>9}")
    print(f"{'  with it':<38}{cs_on['above_30']:>6.2f}{cs_on['above_40']:>8.2f}{cs_on['last']:>10.1f}{cs_on['filters']:>9}"
          f"  {cs_on['episodes']} episode(s), deepest {cs_on['deepest']:.0f} dB, {cs_on['depth_end']:.0f} dB at the end")
    bad += cs_bad

    if update:
        json.dump(now, open(BASELINE, "w"), indent=2, sort_keys=True)
        print(f"\nbaseline re-recorded -> {os.path.basename(BASELINE)}")
        sys.exit(0)

    if base is None:
        json.dump(now, open(BASELINE, "w"), indent=2, sort_keys=True)
        print(f"\nno baseline existed; recorded one -> {os.path.basename(BASELINE)}")

    try:
        import ledger
        ledger.record("closed-loop", "simulated",
                      dict(hf_quieter_3_db=now["margin_3"]["hf_suppression"], hf_quieter_9_db=now["margin_9"]["hf_suppression"],
                           events_3=now["margin_3"]["events"], events_9=now["margin_9"]["events"],
                           filters_3=now["margin_3"]["filters"], filters_9=now["margin_9"]["filters"],
                           disconnected_flagged=dis["not_in_loop"], disconnected_deepest_db=dis["deepest"],
                           cold20_s_above_m30=cs_on["above_30"], cold20_s_above_m40=cs_on["above_40"],
                           cold20_noduck_s_above_m40=cs_off["above_40"], cold20_duck_episodes=cs_on["episodes"],
                           cold20_duck_end_db=cs_on["depth_end"]),
                      ok=not bad)
    except ImportError:
        pass
    if bad:
        print("\nFAIL")
        for b in bad: print(f"   {b}")
        sys.exit(1)
    print("\nPASS")
