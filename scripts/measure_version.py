#!/usr/bin/env python3
"""
Measure a past build with today's tests, and log it.

    scripts/measure_version.py 11fe355 69f6ef0      # these commits
    scripts/measure_version.py --since 11fe355      # every commit since that one which touched the engine
    scripts/measure_version.py --no-record d55f580  # print, do not log

For each commit: build ITS engine's test library (fk-loopdsp) in a throwaway
worktree, then run TODAY'S standard offline tests against that library and
record the results under that commit (FK_VERSION), with today's test code as
the ruler. That is the only honest way to put two builds in one table: the
same ruler laid against both.

What can be measured this way: everything that loads the library - the
recorded-feedback gate, the simulated-room gate, the loop-measurement gate (on
builds that have pinned filters) and the singer in the loop. The unit tests and
the fuzz are each build's own programs and are not re-run.

Reaches back as far as 11fe355 (2026-10-03), the first build whose library can
be given the rig's settings; before that the gate was testing a machine the
room had never heard (docs/wrong-machine-2026-10-03.md).

Libraries are kept in engine/build/versions/ (git-ignored), so a second run is
quick. No network: JUCE comes from the copy already fetched in engine/build.
"""
import ctypes, os, subprocess, sys

ROOT = os.path.abspath(os.path.join(os.path.dirname(os.path.abspath(__file__)), ".."))
SIM = os.path.join(ROOT, "engine", "tests", "sim")
VERS = os.path.join(ROOT, "engine", "build", "versions")
JUCE = os.path.join(ROOT, "engine", "build", "_deps", "juce-src")
FIRST = "11fe355"


def git(*a):
    return subprocess.run(["git", "-C", ROOT, *a], capture_output=True, text=True).stdout.strip()


def source_id(h):
    """What the library is made of at that commit: the engine sources and the shim."""
    return git("rev-parse", f"{h}:engine/Source") + git("rev-parse", f"{h}:engine/tests/fk_loopdsp.cpp")


def md5(path):
    import hashlib
    return hashlib.md5(open(path, "rb").read()).hexdigest()


def build(h):
    """The test library of commit h. Returns its path, or None.

    The library is recompiled from nothing every time, and the result is
    checked against every other library here: two commits with different
    engine sources must not produce the same file. On 2026-10-04 they did -
    make compares timestamps to the second, each checkout landed in the same
    second as the previous object file, and every second "build" was skipped.
    Three of seven libraries were copies of their neighbour, labelled as
    something else. A measurement filed under the wrong build is worse than no
    measurement (docs/wrong-machine-2026-10-03.md was the same mistake, live).
    """
    lib = os.path.join(VERS, f"libfk-loopdsp.{h}.dylib")
    tag = lib + ".src"
    if os.path.exists(lib) and os.path.exists(tag) and open(tag).read().split()[0] == source_id(h):
        return lib
    os.makedirs(VERS, exist_ok=True)
    wt, bd = os.path.join(VERS, "wt"), os.path.join(VERS, "wt-build")
    if not os.path.isdir(wt):
        subprocess.run(["git", "-C", ROOT, "worktree", "add", "--detach", wt, h], check=True, capture_output=True)
    subprocess.run(["git", "-C", wt, "checkout", "-q", "--detach", h], check=True)
    cfg = subprocess.run(["cmake", "-S", os.path.join(wt, "engine"), "-B", bd, "-DCMAKE_BUILD_TYPE=Release",
                          f"-DFETCHCONTENT_SOURCE_DIR_JUCE={JUCE}", "-DFETCHCONTENT_FULLY_DISCONNECTED=ON"],
                         capture_output=True, text=True)
    if cfg.returncode: print(cfg.stdout[-800:], cfg.stderr[-800:]); return None
    # from nothing: the shim's object and the library itself (JUCE's objects do not depend on our sources)
    for root, _, files in os.walk(bd):
        for f in files:
            if f == "fk_loopdsp.cpp.o" or f == "libfk-loopdsp.dylib": os.remove(os.path.join(root, f))
    b = subprocess.run(["cmake", "--build", bd, "--target", "fk-loopdsp", "-j8"], capture_output=True, text=True)
    if b.returncode: print(b.stdout[-800:], b.stderr[-800:]); return None
    built = os.path.join(bd, "libfk-loopdsp.dylib")
    sid, m = source_id(h), md5(built)
    for other in os.listdir(VERS):
        if other.endswith(".src") and other != os.path.basename(tag):
            osid, om = open(os.path.join(VERS, other)).read().split()
            if om == m and osid != sid:
                print(f"   REFUSED: this library is byte-identical to {other[:-4]}, built from different sources - the build was skipped")
                return None
    subprocess.run(["cp", built, lib], check=True)
    open(tag, "w").write(f"{sid} {m}\n")
    return lib


def has(lib, symbol):
    try: getattr(ctypes.CDLL(lib), symbol); return True
    except AttributeError: return False


def measure(h, lib, record):
    env = dict(os.environ, FK_LOOPDSP=lib, FK_VERSION=h)
    if record: env["FK_RECORD"] = "1"
    else: env.pop("FK_RECORD", None)
    tests = [("recorded feedback", ["replay_gate.py"]), ("simulated room", ["preship.py"]), ("singer in the loop", ["quality.py"])]
    if has(lib, "fk_pin"): tests.insert(2, ("measure and pre-place", ["loop_measure_check.py", "--gate"]))
    for name, cmd in tests:
        p = subprocess.run([sys.executable] + cmd, cwd=SIM, env=env, capture_output=True, text=True)
        tail = [l for l in p.stdout.splitlines() if l.strip()][-1:] or [""]
        logged = sum(1 for l in p.stdout.splitlines() if l.strip().startswith("recorded:"))
        refused = any("not recorded:" in l for l in p.stdout.splitlines())
        print(f"   {name:<22} {'ok  ' if p.returncode == 0 else 'FAIL'} {logged} row(s) logged"
              f"{'  (REFUSED: uncommitted changes)' if refused else ''}   {tail[0][:90]}")
        if p.returncode not in (0, 1): print(p.stderr[-600:])


if __name__ == "__main__":
    a = sys.argv[1:]
    record = "--no-record" not in a
    a = [x for x in a if x != "--no-record"]
    if a[:1] == ["--since"]:
        base = a[1] if len(a) > 1 else FIRST
        hashes = [base] + git("log", "--reverse", "--format=%h", f"{base}..HEAD", "--", "engine/Source", "engine/tests/fk_loopdsp.cpp").split()
    else:
        hashes = a
    if not hashes:
        print(__doc__); sys.exit(0)
    for h in hashes:
        h = git("rev-parse", "--short", h) or h
        print(f"{h}  {git('show', '-s', '--format=%cd  %s', '--date=format:%m-%d %H:%M', h)}")
        lib = build(h)
        if lib is None: print("   could not build"); continue
        if not has(lib, "fk_configure"):
            print("   this build's library cannot be given the rig's settings (before 11fe355) - not measured"); continue
        measure(h, lib, record)
    wt = os.path.join(VERS, "wt")
    if os.path.isdir(wt):
        subprocess.run(["git", "-C", ROOT, "worktree", "remove", "--force", wt], capture_output=True)
