#!/usr/bin/env python3
"""Sweep the guard's internal knobs and map the frontier.

    python3 scripts/fuzz-sweep.py [minutes] [seeds...]

Two axes, and every configuration is a point between them:

    top     lossless audio, no suppression at all
    bottom  full feedback, everything gets away

Nothing here is a judgement about which point to ship. It reports what each
setting costs and what it buys, marks the points that nothing else beats on
BOTH axes (the Pareto frontier), and leaves the choice where it belongs.

  harm     ear-weighted dB-ERB removed from the singer, time-averaged.
           Depth x width-in-ear-bandwidths x how much that frequency matters.
  runaway  percentage of rings that passed -3 dBFS anyway.
  singer   detections that landed on a harmonic of a sung note. Any number
           above zero is a guard notching the voice it is supposed to protect.
"""
import subprocess, sys, re, statistics, os

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
FUZZ = os.path.join(ROOT, "engine/build/fk-fuzz_artefacts/Release/fk-fuzz")

MINUTES = sys.argv[1] if len(sys.argv) > 1 else "10"
SEEDS = sys.argv[2:] or ["1", "2", "3"]

# The knobs we can actually turn today. "budget 0" means no harm ceiling, which
# is what ships; duty 100 means continuous, which is also what ships.
GRID = []
for budget in (0, 120, 60, 30, 15):
    for duty in (100, 50, 20):
        GRID.append({"budget": budget, "duty": duty})
GRID.append({"budget": 60, "duty": 50, "plateau": True})
GRID.append({"budget": 0, "duty": 100, "plateau": True})


def run(cfg, seed):
    args = [FUZZ, MINUTES, seed]
    if cfg["budget"]:
        args += ["--budget", str(cfg["budget"])]
    if cfg["duty"] != 100:
        args += ["--duty", str(cfg["duty"]), "--period", "12"]
    if cfg.get("plateau"):
        args += ["--plateau"]
    out = subprocess.run(args, capture_output=True, text=True).stdout

    def grab(pattern, where=out, cast=float):
        m = re.search(pattern, where)
        return cast(m.group(1)) if m else float("nan")

    spikes = re.search(r"^  spikes.*$", out, re.M).group(0)
    plates = re.search(r"^  plateaus.*$", out, re.M).group(0)
    return {
        "harm": grab(r"HARM \(dB-ERB, ear-weighted\)\s*:\s*([\d.]+)"),
        "runaway": (grab(r"ran away\s+(\d+)%", spikes) + grab(r"ran away\s+(\d+)%", plates)) / 2,
        "killed": (grab(r"killed\s+(\d+)%", spikes) + grab(r"killed\s+(\d+)%", plates)) / 2,
        "singer": grab(r"catches on the singer\s*:\s*(\d+)"),
    }


rows = []
for cfg in GRID:
    runs = [run(cfg, s) for s in SEEDS]
    name = f"budget {cfg['budget'] or '-':>3}  duty {cfg['duty']:>3}%" + ("  +plateau" if cfg.get("plateau") else "")
    rows.append({
        "name": name,
        "harm": statistics.mean(r["harm"] for r in runs),
        "runaway": statistics.mean(r["runaway"] for r in runs),
        "killed": statistics.mean(r["killed"] for r in runs),
        "singer": statistics.mean(r["singer"] for r in runs),
    })
    print(f"  ran {name}", flush=True)

# Pareto: nothing else is better on BOTH axes at once.
for r in rows:
    r["front"] = not any(o is not r and o["harm"] <= r["harm"] and o["runaway"] <= r["runaway"]
                         and (o["harm"] < r["harm"] or o["runaway"] < r["runaway"]) for o in rows)

print(f"\n{MINUTES} minutes x {len(SEEDS)} seeds, averaged over spikes and plateaus\n")
print(f"  {'configuration':<30} {'harm':>7} {'runaway':>8} {'killed':>7} {'singer':>7}   frontier")
for r in sorted(rows, key=lambda r: r["harm"]):
    print(f"  {r['name']:<30} {r['harm']:>7.1f} {r['runaway']:>7.0f}% {r['killed']:>6.0f}% "
          f"{r['singer']:>7.1f}   {'<-- best available' if r['front'] else ''}")

print("\n  harm 0 = lossless; runaway 100% = full feedback. The frontier is the"
      "\n  set of settings where buying less feedback costs more voice.")
