#!/usr/bin/env python3
"""
ledger.py — every test result, kept, with the version it belongs to.

results/ledger.jsonl at the top of the repository: one JSON object per line,
appended and never rewritten. scripts/results.py turns it into docs/RESULTS.md.

Why
---
For two months the numbers lived in commit messages and in my head. "Is this
the best result so far?" could only be answered by re-reading the log, and on
2026-10-03 three hours of live measurements turned out to belong to a different
binary from the one they were filed under. A result that does not carry its
version and its date is an anecdote.

A row
-----
  when      ISO time the measurement was taken
  version   the ENGINE under test: a commit hash. For a live run, the build
            stamp the running engine wrote (engine-build.txt), not the checkout.
  ruler     the commit of the test code that did the measuring. An old engine
            measured today by today's test carries today's ruler; rows are only
            comparable down a column when the ruler is the same or says so.
  kind      bench (no loop: unit tests, fuzz) | recorded (real feedback, replayed)
            | simulated (the guard inside a simulated room) | live (the room)
  test      which standard test (docs/TESTING.md)
  ok        did it pass its own gate (null where there is none)
  metrics   the headline numbers, flat
  cases     optional: one dict per case behind the headline
  source    run (measured by the tool) | backfill (reconstructed from files a
            past session left behind) - never typed in from memory

Recording is off unless asked for: FK_RECORD=1 in the environment, which
scripts/preship.sh --record and scripts/measure_version.py set. A working tree
with uncommitted engine or test changes is not a version, so its results are
printed and not recorded.

    python3 ledger.py add <test> <kind> key=value ...      # for shell scripts
    python3 ledger.py show                                   # the last row of each test
"""
import datetime, json, os, subprocess, sys

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.abspath(os.path.join(HERE, "..", "..", ".."))
LEDGER = os.environ.get("FK_LEDGER", os.path.join(ROOT, "results", "ledger.jsonl"))
BUILD_TXT = os.path.expanduser("~/Documents/FeedbackKiller/engine-build.txt")


def _git(*args):
    try:
        return subprocess.run(["git", "-C", ROOT, *args], capture_output=True, text=True, timeout=10).stdout.strip()
    except Exception:
        return ""


def head():
    return _git("rev-parse", "--short", "HEAD") or "unknown"


ENGINE_CODE = ("engine/Source", "engine/tests/fk_loopdsp.cpp", "engine/CMakeLists.txt")


def dirty():
    """Uncommitted changes to anything that decides a result."""
    return bool(_git("status", "--porcelain", "--", "engine", "scripts"))


def engine_version():
    """The engine under test. FK_VERSION when a past build's library is loaded
    (measure_version.py); otherwise the last commit that changed the engine's
    own code. A commit that only touches tests or documents is the same
    engine, and gets the same name - the test code is tracked as the ruler."""
    return os.environ.get("FK_VERSION") or _git("log", "-1", "--format=%h", "--", *ENGINE_CODE) or head()


def running_build():
    """What the engine in the room says it is: ('d55f580', 'd55f580 built 2026-10-04 09:20')."""
    try:
        line = open(BUILD_TXT).readline().strip()
        return line.split()[0], line
    except (OSError, IndexError):
        return "unknown", "unknown"


def _clean(v):
    """JSON-safe, and short: floats to three places."""
    if isinstance(v, dict): return {str(k): _clean(x) for k, x in v.items()}
    if isinstance(v, (list, tuple)): return [_clean(x) for x in v]
    if isinstance(v, bool) or v is None or isinstance(v, (int, str)): return v
    try:
        f = float(v)
        return None if f != f else round(f, 3)
    except (TypeError, ValueError):
        return str(v)


def record(test, kind, metrics, ok=None, cases=None, version=None, when=None, note=None, source="run",
           config=None, force=False):
    """Append one row. Returns the row, or None when recording is off."""
    if not (force or os.environ.get("FK_RECORD") == "1"):
        return None
    live = kind == "live"
    if not live and dirty() and not force:
        print(f"   (not recorded: {test} was measured on uncommitted changes - commit, then record)")
        return None
    row = dict(when=when or datetime.datetime.now().astimezone().isoformat(timespec="seconds"),
               version=version or (running_build()[0] if live else engine_version()),
               ruler=head(), kind=kind, test=test, ok=ok,
               metrics=_clean(metrics), source=source)
    if cases is not None: row["cases"] = _clean(cases)
    if config is not None: row["config"] = _clean(config)
    if note: row["note"] = note
    os.makedirs(os.path.dirname(LEDGER), exist_ok=True)
    with open(LEDGER, "a") as fh:
        fh.write(json.dumps(row, sort_keys=False) + "\n")
    print(f"   recorded: {test} for {row['version']}")
    return row


def rows():
    if not os.path.exists(LEDGER): return []
    out = []
    for line in open(LEDGER):
        line = line.strip()
        if line: out.append(json.loads(line))
    return out


if __name__ == "__main__":
    a = sys.argv[1:]
    if a[:1] == ["add"] and len(a) >= 3:
        m = {}
        ok = None
        for kv in a[3:]:
            k, v = kv.split("=", 1)
            if k == "ok": ok = v in ("1", "true", "True"); continue
            try: m[k] = float(v) if "." in v else int(v)
            except ValueError: m[k] = v
        record(a[1], a[2], m, ok=ok)
    elif a[:1] == ["show"]:
        last = {}
        for r in rows(): last[(r["test"], r["kind"])] = r
        for (t, k), r in sorted(last.items()):
            print(f"{r['when'][:16]}  {r['version']:<9} {k:<10} {t:<22} {'ok ' if r['ok'] else ('FAIL' if r['ok'] is False else '-  ')} "
                  + "  ".join(f"{a}={b}" for a, b in list(r["metrics"].items())[:6]))
    else:
        print(__doc__)
