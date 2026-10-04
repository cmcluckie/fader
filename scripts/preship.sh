#!/usr/bin/env bash
#
# Everything that must pass before a bundle is allowed out.
#
#   scripts/preship.sh             run the gate
#   scripts/preship.sh --record    ...and keep every number in results/ledger.jsonl
#                                  under this commit (refused on uncommitted changes)
#
# Six layers, cheapest first (docs/TESTING.md says what each number means):
#
#   1. fk-tests   unit behaviour of the detector and the notch bank
#   2. fk-fuzz    statistical: many rings, scored on runaways and ear-weighted harm
#   3. preship.py CLOSED LOOP - the shipping DSP inside a real acoustic model,
#                 where what it cuts changes the loop gain on the next pass
#   4. replay_gate.py  REAL FEEDBACK - every howl cut from the flight
#                 recordings, and every needle that turned out to be the singer.
#                 Until 2026-09-30 no test had ever been shown actual feedback.
#   5. loop_measure_check.py  a measured loop must predict what rings, and
#                      pinning from it must beat reacting
#   6. quality.py --gate  A SINGER IN THE LOOP - what the audience hears: rings,
#                 and how much of the voice the guard took. Until 2026-10-04 no
#                 test had a voice the loop could feed on, or scored the voice.
#
# Layers 1 and 2 feed the guard a signal it cannot influence. They can tell you
# the detector fired; they cannot tell you the room got quieter, because in both
# of them the room is a recording. Feedback is a loop. Only layer 3 closes it.
#
# Overrides, for iterating - never for shipping:
#   SKIP_PRESHIP=1   skip the whole thing
#   SKIP_LOOP=1      skip only the closed-loop layer (missing python deps)
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
ENGINE="$ROOT/engine"
BUILD="$ENGINE/build"
LEDGER="$ENGINE/tests/sim/ledger.py"

if [[ "${1:-}" == "--record" ]]; then export FK_RECORD=1; fi

# This machine is also the rig. Everything below runs at low priority so the
# live engine and its app are never starved by a test run (2026-10-04: they were).
renice -n 10 $$ >/dev/null 2>&1 || true

if [[ "${SKIP_PRESHIP:-0}" == "1" ]]; then
  echo "==> preship SKIPPED (SKIP_PRESHIP=1) - do not ship this"
  exit 0
fi

echo "==> Building test targets"
cmake -S "$ENGINE" -B "$BUILD" >/dev/null
cmake --build "$BUILD" --target fk-tests fk-fuzz fk-loopdsp -j8 >/dev/null

echo "==> 1/6 unit tests"
if ! "$BUILD/fk-tests_artefacts/Release/fk-tests" > /tmp/fk-tests.out 2>&1; then
  # T42 is a known, deliberate failure: the plateau path ships disabled and the
  # test is kept red on purpose so the gap stays visible. Anything else is real.
  OTHER=$(grep -E "^  FAIL" /tmp/fk-tests.out | grep -v "T42" || true)
  if [[ -n "$OTHER" ]]; then
    echo "$OTHER"
    echo "FAILED: unit tests"
    exit 1
  fi
  echo "    only the known T42 plateau failure - ok"
fi
PASSING=$(grep -cE "^  PASS" /tmp/fk-tests.out || true)
echo "    $PASSING passing"
python3 "$LEDGER" add unit-tests bench passing="$PASSING" parked=1 ok=1

# Six seeds, not fourteen. This is a gate, not a study: it has to be quick
# enough that nobody reaches for SKIP_PRESHIP. Run the full sweep by hand
# when a change needs a real verdict.
echo "==> 2/6 fuzz (6 seeds)"
R=0
for s in $(seq 1 6); do
  n=$("$BUILD/fk-fuzz_artefacts/Release/fk-fuzz" 1 "$s" --profile 2>&1 \
        | grep "ran away (past" | grep -oE ': [0-9]+' | tr -d ': ')
  R=$((R + n))
done
echo "    $R runaway modes across 6 seeds"
python3 "$LEDGER" add fuzz bench runaway_modes="$R" seeds=6

if [[ "${SKIP_LOOP:-0}" == "1" ]]; then
  echo "==> 3/6 closed loop SKIPPED (SKIP_LOOP=1) - do not ship this"
  exit 0
fi

echo "==> 3/6 closed loop"
cd "$ENGINE/tests/sim"
if ! python3 preship.py; then
  echo "FAILED: the guard regressed inside a real loop"
  exit 1
fi
echo "==> 4/6 real recordings"
if ! python3 replay_gate.py; then
  echo "FAILED: worse on real feedback than the build that cut the fixtures"
  exit 1
fi
# Measure the loop, pin from the measurement, start cold (plan item 2). The
# simulator knows its own loop response, so the measured ladder is checked
# against the truth, and pinning first is checked against reacting.
echo "==> 5/6 measure and pre-place"
if ! python3 loop_measure_check.py --gate > /tmp/fk-loop-measure.out 2>&1; then
  cat /tmp/fk-loop-measure.out
  echo "FAILED: the loop measurement no longer predicts what rings"
  exit 1
fi
grep -E 'headroom|NET|reactive only|pinned first|^PASS|recorded' /tmp/fk-loop-measure.out
# A singer in the loop: rings heard, and what the guard took from the voice.
# A regression gate like the others - "no worse than it was". The goals it is
# on the way to are printed beside it and live in docs/TODO-AS-BUILT.md.
echo "==> 6/6 a singer in the loop"
if ! python3 quality.py --gate; then
  echo "FAILED: rings heard, or more of the voice taken, than the recorded build"
  exit 1
fi
if [[ "${FK_RECORD:-0}" == "1" ]]; then
  # A recorded build gets the whole study - both rooms, every voice on this
  # machine, every gain - not only the gate's five runs. About a minute more.
  echo "==> recording the full singer study"
  python3 quality.py > /tmp/fk-quality.out 2>&1 || true
  grep -E "recorded|runs in" /tmp/fk-quality.out || tail -5 /tmp/fk-quality.out
fi
echo "==> preship OK"
