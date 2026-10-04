#!/usr/bin/env python3
"""
Turn the results log into a page a person can read.

    scripts/results.py                  # results/ledger.jsonl -> docs/RESULTS.md
    scripts/results.py --backfill-live  # import the live sweeps already on this machine, once

The log (engine/tests/sim/ledger.py) is the record; this page is a view of it
and is regenerated, never edited. Where a version has been measured more than
once by the same test, the latest measurement is shown.
"""
import csv, datetime, glob, json, os, subprocess, sys
import numpy as np

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.abspath(os.path.join(HERE, ".."))
sys.path.insert(0, os.path.join(ROOT, "engine", "tests", "sim"))
import ledger

OUT = os.path.join(ROOT, "docs", "RESULTS.md")
RINGOUT = os.path.expanduser("~/Documents/FeedbackKiller/ringout")

# Which engine was running when. The start times are the feedback-log file
# names in ~/Documents/FeedbackKiller/logs (one per engine start); each matches
# the commit made minutes before it. The first is the bundle that shipped an
# engine built at 15:22 under the name of a later fix (docs/wrong-machine-2026-10-03.md).
BUILDS = [("2026-10-03T18:14:14", "3629b7f", "stale bundle: an engine built at 15:22"),
          ("2026-10-03T18:51:18", "b71ce6f", ""), ("2026-10-03T19:34:45", "69f6ef0", ""),
          ("2026-10-03T20:15:41", "fce3790", ""), ("2026-10-03T22:17:56", "f5c5f0b", ""),
          ("2026-10-04T09:21:22", "d55f580", "")]


def git(*a):
    return subprocess.run(["git", "-C", ROOT, *a], capture_output=True, text=True).stdout.strip()


def f(v, spec="{:.0f}", none="-"):
    if v is None: return none
    if isinstance(v, bool): return "yes" if v else "no"
    return spec.format(v)


# ---------------------------------------------------------------- live backfill

def _loudest(path):
    import soundfile as sf
    x, sr = sf.read(path, dtype="float32", always_2d=True); pre = x[:, 0]
    N = 4096; w = np.hanning(N); fr = np.fft.rfftfreq(N, 1 / sr); lo = np.searchsorted(fr, 1000); best = (-200.0, 0.0)
    for i in range(0, len(pre) - N, N // 4):
        s = 20 * np.log10(2 * np.abs(np.fft.rfft(pre[i:i + N] * w)) / w.sum() + 1e-12); b = lo + int(np.argmax(s[lo:]))
        if s[b] > best[0]: best = (float(s[b]), float(fr[b]))
    return best


def sweep_metrics(d):
    """What one ringout.py folder says, as a flat row. None for a dry run."""
    sp = os.path.join(d, "summary.json")
    if not os.path.exists(sp): return None
    js = json.load(open(sp))
    if js.get("dry_run"): return None
    main = float(js.get("main_db", 0.0)); steps, det = [], 0
    tp = os.path.join(d, "trials.csv")
    if os.path.exists(tp):
        for r in csv.DictReader(open(tp)):
            if r["event"] == "step": steps.append(float(r["fader_db"]))
            elif r["event"] == "detector": det += 1
    m = dict(mode=js["mode"], main_db=main, killed=bool(js.get("killed")), rings=len(js.get("rings", [])),
             rescues=len(js.get("rescues", [])), detections=det, top_db=(max(steps) + main) if steps else None)
    if js.get("rings"):
        m.update(first_ring_db=float(js["rings"][0]["db"]) + main, first_ring_hz=float(js["rings"][0]["hz"]),
                 loudest_ring_db=float(max(r["peak"] for r in js["rings"])))
    wav = os.path.join(d, "sweep.wav")
    if os.path.exists(wav):
        try:
            lv, hz = _loudest(wav); m.update(loudest_db=lv, loudest_hz=hz)
        except Exception:
            pass
    return m


def backfill_live():
    have = {(r["when"], r["test"]) for r in ledger.rows()}
    n = 0
    for d in sorted(glob.glob(os.path.join(RINGOUT, "*-*"))):
        stamp = os.path.basename(d)[:15]
        try: t = datetime.datetime.strptime(stamp, "%Y%m%d-%H%M%S")
        except ValueError: continue
        m = sweep_metrics(d)
        if m is None: continue
        when = t.astimezone().isoformat(timespec="seconds")
        test = "sweep-" + m.pop("mode")
        if (when, test) in have: continue
        ver, note = "unknown", "no engine start on record before this run"
        for start, h, why in BUILDS:
            if t >= datetime.datetime.fromisoformat(start): ver, note = h, why
        ledger.record(test, "live", m, ok=None, version=ver, when=when, source="backfill", force=True,
                      note=("build named from engine start times. " + note).strip())
        n += 1
    print(f"{n} live run(s) imported")


# ---------------------------------------------------------------- the page

def build_page():
    rows = ledger.rows()
    if not rows:
        return "# Results\n\nNothing has been recorded yet. Run `scripts/preship.sh --record` on a committed tree.\n"
    last = {}
    for r in rows:
        key = (r["version"], r["test"], r["metrics"].get("room"), r["metrics"].get("voice")) if r["kind"] != "live" else None
        if key: last[key] = r
    vers = []
    for r in rows:
        if r["kind"] != "live" and r["version"] not in vers: vers.append(r["version"])
    date = {v: git("show", "-s", "--format=%cd", "--date=format:%m-%d %H:%M", v) or "?" for v in vers}
    vers.sort(key=lambda v: git("show", "-s", "--format=%ct", v) or "0", reverse=True)

    def get(v, test, room=None, voice=None):
        r = last.get((v, test, room, voice))
        return r["metrics"] if r else {}

    def case(v, room, voice, c):
        r = last.get((v, "singer-in-loop", room, voice))
        if not r: return {}
        return next((x for x in r.get("cases", []) if x["case"] == str(c) and x["guarded"]), {})

    voices = []
    for r in rows:
        v = r["metrics"].get("voice")
        if r["test"] == "singer-in-loop" and v and v not in voices: voices.append(v)
    real = next((v for v in voices if v != "synth"), None)

    L = []
    w = L.append
    w("# Results\n")
    w(f"Generated {datetime.date.today().isoformat()} from `results/ledger.jsonl` by `scripts/results.py`. **Do not edit** - "
      "record a result and regenerate. What each test is: [TESTING.md](TESTING.md). "
      "Goals and thresholds: [TODO-AS-BUILT.md](TODO-AS-BUILT.md).\n")
    w("- **Simulated** - the guard inside a simulated room, with a singer. **Recorded** - real feedback and real singing "
      "from the rig, replayed. **Live** - the room itself.")
    w("- A **build** is named by the last commit that changed the engine's code. Past builds were re-measured with "
      "today's tests (`scripts/measure_version.py`), so every column uses one ruler.\n")

    # ---- the three goals
    v0 = vers[0]
    w(f"## The latest build against the three goals\n")
    w(f"Build `{v0}` ({date[v0]}).\n")
    w("| | Goal | Room like the rig | Reverberant hall |")
    w("|---|---|---|---|")
    def ring_cell(room, voice_):
        out = []
        for c in (6, 10, 15, 20):
            x = case(v0, room, voice_, c)
            if not x: continue
            out.append(f"+{c}: " + ("none heard" if not x.get("audible") else
                       f"{x['audible']} heard, catch {f(x.get('worst_catch_ms'), '{:+.0f}')} ms, kill {f(x.get('worst_kill_ms'))} ms"))
        return "; ".join(out) or "-"
    w(f"| **Caught / killed** | catch within 10 ms of audible, kill within 15 | {ring_cell('rig', 'synth')} | {ring_cell('hall', 'synth')} |")
    def sound_cell(room):
        out = []
        for c, label in (("alone", "nothing ringing"), (6, "+6"), (10, "+10")):
            parts = [f"{case(v0, room, vv, c).get('change_pct'):.0f} %" for vv in voices if case(v0, room, vv, c)]
            if parts: out.append(f"{label}: " + " / ".join(parts))
        return "; ".join(out) or "-"
    w(f"| **Sound** (voice change: {' / '.join(voices) or '-'}) | under 3 % with nothing ringing, under 15 % holding feedback | {sound_cell('rig')} | {sound_cell('hall')} |\n")

    # ---- feedback by version
    w("## Feedback, by build\n")
    w("Rings *heard* are lines the singer did not sing, louder than 20 dB under the voice, that grew. "
      "\"Held to\" is how far a slow push got before a ring was audible for a quarter of a second.\n")
    w("| build | date | recorded howls caught | let go | median lead | fast risers: level when cut | rig-like: rings heard +10 / +15 / +20 | rig-like held to | hall: rings heard +6 / +10 | hall held to |")
    w("|---|---|---|---|---|---|---|---|---|---|")
    for v in vers:
        g = get(v, "recorded-feedback")
        def heard(room, cs):
            return " / ".join(f(case(v, room, "synth", c).get("audible")) for c in cs)
        w(f"| `{v}` | {date[v]} | {f(g.get('detected'))} of {f(g.get('howls'))} | {f(g.get('let_go'))} | {f(g.get('median_lead_ms'))} ms | "
          f"{f(g.get('fast_worst_landed_db'), '{:.0f} dB')} | {heard('rig', (10, 15, 20))} | "
          f"{f(case(v, 'rig', 'synth', 'push').get('held_to_db'), '{:+.1f} dB')} | {heard('hall', (6, 10))} | "
          f"{f(case(v, 'hall', 'synth', 'push').get('held_to_db'), '{:+.1f} dB')} |")
    w("")

    # ---- sound by version
    w("## Sound, by build\n")
    w("Voice change: how much of what the ear gets from the voice differs from the clean voice. "
      "A 1 dB level change is 8 %, 3 dB is 22 %. Goal: 3 % with nothing ringing, 15 % holding feedback.\n")
    hdr = "| build | six recorded sung phrases: taken, filters | " + " | ".join(
        f"{vv}: nothing ringing" for vv in voices) + " | " + " | ".join(f"{vv}: +6 / +10" for vv in voices) + " | filters on a singer, nothing ringing |"
    w(hdr); w("|" + "---|" * (hdr.count("|") - 1))
    for v in vers:
        g = get(v, "recorded-feedback")
        cells = [f"{case(v, 'rig', vv, 'alone').get('change_pct'):.0f} %" if case(v, 'rig', vv, 'alone') else "-" for vv in voices]
        cells2 = [" / ".join(f(case(v, 'rig', vv, c).get('change_pct'), '{:.0f} %') for c in (6, 10)) for vv in voices]
        filt = " / ".join(f(case(v, 'rig', vv, 'alone').get('filters_mean')) for vv in voices)
        w(f"| `{v}` | {f(g.get('voice_phrase_pct'), '{:.0f} %')}, {f(g.get('voice_filters'))} | " + " | ".join(cells) + " | " + " | ".join(cells2) + f" | {filt} |")
    w("")

    # ---- the other gates
    w("## The other gates, by build\n")
    w("| build | unit tests | fuzz: runaway modes | simulated room: top end quieter at 3 / 9 dB over | cold start 20 over: seconds above -40 dB | sung fixtures hit (of 6) | measured loop names the ring | started cold with pinned filters: detections |")
    w("|---|---|---|---|---|---|---|---|")
    for v in vers:
        u, z, c, g, m = get(v, "unit-tests"), get(v, "fuzz"), get(v, "closed-loop"), get(v, "recorded-feedback"), get(v, "measure-and-preplace")
        w(f"| `{v}` | {f(u.get('passing'))} | {f(z.get('runaway_modes'))} | {f(c.get('hf_quieter_3_db'))} / {f(c.get('hf_quieter_9_db'))} dB | "
          f"{f(c.get('cold20_s_above_m40'), '{:.2f}')} | {f(g.get('voice_hits'))} | {f(m.get('rig_names_the_ring'))} | {f(m.get('rig_pinned_events'))} |")
    w("\nUnit tests and fuzz are each build's own programs and are only recorded from the build that was current when they ran.\n")

    # ---- live
    live = sorted((r for r in rows if r["kind"] == "live"), key=lambda r: r["when"])
    if live:
        w("## In the room\n")
        w("A sweep's \"dB over\" is counted from the nearest baseline in the half hour before it. The room's ring point "
          "moves by ten decibels within minutes, so a figure without a baseline either side is an estimate.\n")
        w("| when | build | what | result |")
        w("|---|---|---|---|")
        base = None
        for r in live:
            m = r["metrics"]; t = datetime.datetime.fromisoformat(r["when"])
            when = t.strftime("%m-%d %H:%M")
            if r["test"] == "sweep-baseline":
                if m.get("first_ring_db") is not None:
                    base = (t, m["first_ring_db"])
                    res = f"guard off: the room rings at {m['first_ring_db']:+.1f} dB on the faders, {m.get('first_ring_hz', 0):.0f} Hz"
                else:
                    res = f"guard off: no ring up to {f(m.get('top_db'), '{:+.1f}')} dB"
                what = "baseline"
            elif r["test"] == "sweep-guard":
                over = ""
                if base and (t - base[0]).total_seconds() <= 1800 and m.get("top_db") is not None:
                    over = f" (**{m['top_db'] - base[1]:+.1f} dB over** the last baseline)"
                res = (f"guard on: reached {f(m.get('top_db'), '{:+.1f}')} dB{over}; loudest line {f(m.get('loudest_db'), '{:.1f}')} dB"
                       f" at {f(m.get('loudest_hz'))} Hz; {m.get('detections', 0)} catches, {m.get('rescues', 0)} duck actions"
                       + ("; **kill switch tripped**" if m.get("killed") else ""))
                what = "guarded sweep"
            elif r["test"] == "cold-jump":
                res = (f"{m.get('over_db', 0):.0f} dB over: loudest {f(m.get('loudest_db'), '{:.1f}')} dB at {f(m.get('loudest_hz'))} Hz, "
                       f"{m.get('detections', 0)} catches, {m.get('rescues', 0)} duck actions"
                       + ("; **kill switch tripped**" if m.get("killed") else "")
                       + ("" if m.get("valid") else " - *edge moved, not counted*"))
                what = "cold jump"
            elif r["test"] == "session-voice":
                res = (f"the guard took **{m.get('taken_pct', 0):.1f} %** of the voice ({m.get('voiced_seconds', 0):.0f} s of voice"
                       + (f", {m['stretch']}" if m.get("stretch") else "") + f"); {m.get('cut_1k_4k_db', 0):+.1f} dB over 1-4 kHz, "
                       f"{m.get('cut_4k_16k_db', 0):+.1f} dB over 4-16 kHz")
                what = "voice in a session"
            else:
                res = "  ".join(f"{k}={v}" for k, v in list(m.items())[:6]); what = r["test"]
            w(f"| {when} | `{r['version']}` | {what} | {res}" + (f" *({r['note']})*" if r.get("note") and "stale" in r["note"] else "") + " |")
        w("")

    # ---- every case for the latest build
    w(f"## A singer in the loop: every case, build `{v0}`\n")
    w("Gain is dB over the untreated room's limit. *no guard* rows show what the loop itself does to the sound. "
      "Tails are the stable room hanging on to a note; ghosts are a filter sounding its own note after the singer stops.\n")
    for room in ("rig", "hall"):
        for vv in voices:
            r = last.get((v0, "singer-in-loop", room, vv))
            if not r: continue
            w(f"**{'Room like the rig' if room == 'rig' else 'Reverberant hall'}, {vv}**\n")
            w("| gain | voice change | added / taken | filters | rings heard | heard for | worst catch | worst kill | loudest | tails | ghosts | duck |")
            w("|---|---|---|---|---|---|---|---|---|---|---|---|")
            for x in r.get("cases", []):
                name = {"alone": "no loop", "move": "+6, mic moves", "push": "slow push"}.get(x["case"], f"{int(x['case']):+d}" if x["case"].lstrip("-").isdigit() else x["case"])
                if not x["guarded"]: name += " *no guard*"
                extra = f" (held to {x['held_to_db']:+.1f} dB)" if x.get("held_to_db") is not None else ""
                w(f"| {name}{extra} | {x['change_pct']:.1f} % | {x['added_pct']:.1f} / {x['removed_pct']:.1f} | {x['filters_mean']:.0f} | {x['audible']} | "
                  f"{x['audible_ms']:.0f} ms | {f(x.get('worst_catch_ms'), '{:+.0f} ms')} | {f(x.get('worst_kill_ms'), '{:.0f} ms')} | "
                  f"{f(x.get('loudest_db'), '{:.1f} dB')} | {x.get('tail_ms', 0):.0f} ms | {x.get('artifact_ms', 0):.0f} ms | {x['duck_episodes']} |")
            w("")

    rulers = sorted({(r["version"], r["ruler"]) for r in rows if r["kind"] != "live"})
    w("## Rulers\n")
    w("Which test code measured which build: " + "; ".join(f"`{v}` by `{r}`" for v, r in rulers) + ".")
    return "\n".join(L) + "\n"


if __name__ == "__main__":
    if "--backfill-live" in sys.argv:
        backfill_live()
    page = build_page()
    open(OUT, "w").write(page)
    print(f"wrote {os.path.relpath(OUT, ROOT)} ({len(page.splitlines())} lines)")
