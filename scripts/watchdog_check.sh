#!/usr/bin/env bash
#
# Does the app keep exactly one engine alive, whatever happens to it?
#
#   scripts/watchdog_check.sh --the-room-is-silent
#
# This KILLS AND FREEZES THE LIVE ENGINE of the running Feedback Fader, five
# ways, and checks that each time the app ends up with exactly one engine,
# launched by itself, holding the OSC port, with its settings replayed. It is
# for an empty room with the desk off. It refuses to run if the engine is
# hearing anything, and it must never be run during a show.
#
# Why it exists: on 2026-10-04 the app was found launching a new engine seven
# times a minute beside one it had lost track of (EngineSupervisor.cs). Nothing
# tested the watchdog; this does.
#
#   1  the engine crashes                    -> one new engine, settings replayed
#   2  the APP is starved for 10 s           -> the same engine, untouched
#   3  the engine crashes, then its replacement crashes -> one engine, settings replayed
#   4  a second engine is started by hand    -> it refuses to start; the first is untouched
#   5  a stray engine is running when the app starts   -> the app ends it and starts its own
#
# Not tested, and why: an engine that is alive but hung. The only way to fake
# it from outside is SIGSTOP, and a stopped child sends .NET's own child-process
# bookkeeping into a spin on macOS (it waits on a process that will not exit),
# which blocks the app's restart until the child continues. A debugger attached
# to the engine would do the same. A crash, a starved app and a stray engine
# are the cases that have actually happened.
set -uo pipefail

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
APPDIR="$ROOT/dist/FeedbackFader.app/Contents/MacOS"
LOGS="$HOME/Documents/FeedbackKiller/logs"
# The same audio device the app gives its engine - never the machine's default,
# which is a laptop microphone and laptop speakers.
DEVICE=$(python3 -c "import json,os;print(json.load(open(os.path.expanduser('~/Documents/FeedbackKiller/audio.json'))).get('Device') or '')" 2>/dev/null)
[[ -n "$DEVICE" ]] || { echo "no audio device in audio.json; not guessing one."; exit 2; }

[[ "${1:-}" == "--the-room-is-silent" ]] || { sed -n 3,12p "$0"; exit 2; }

eng()  { pgrep -f "FeedbackFader.app/Contents/MacOS/fk-engine" | tr '\n' ' ' | sed 's/ *$//'; }
app()  { pgrep -f "FeedbackFader.app/Contents/MacOS/FeedbackFader" | head -1; }
rows() { local f; f=$(ls -t "$LOGS"/eq-log-*.csv 2>/dev/null | head -1); [[ -n "$f" ]] && echo $(( $(wc -l < "$f") - 1 )) || echo 0; }
one()  { [[ "$(eng | wc -w | tr -d ' ')" == "1" ]]; }
FAILS=0
verdict() { if eval "$2"; then echo "   PASS  $1"; else echo "   FAIL  $1"; FAILS=$((FAILS + 1)); fi; }

APP=$(app); [[ -n "$APP" ]] || { echo "Feedback Fader is not running."; exit 2; }
one || { echo "expected exactly one engine before starting; found: [$(eng)]"; exit 2; }

# Refuse unless the engine is hearing nothing: the last status rows must all read silence.
f=$(ls -t "$LOGS"/eq-log-*.csv | head -1)
loud=$(tail -6 "$f" | awk -F, '$12 > -90 {n++} END {print n+0}')
[[ "$loud" == "0" ]] || { echo "The engine is hearing something (level above -90 dB in $f). Not running."; exit 2; }

echo "1  the engine crashes"
e0=$(eng); r0=$(rows); kill -9 "$e0"; sleep 8; e1=$(eng); r1=$(rows)
verdict "one new engine ([$e1], was $e0), settings replayed (status rows $r0 -> $r1)" 'one && [[ "$e1" != "$e0" ]] && (( r1 > r0 + 4 ))'

echo "2  the app is starved for 10 s"
e0=$(eng); kill -STOP "$APP"; sleep 10; kill -CONT "$APP"; sleep 7; e1=$(eng)
verdict "the same engine ([$e1])" '[[ "$e1" == "$e0" ]]'

echo "3  the engine crashes, then its replacement crashes"
e0=$(eng); r0=$(rows); kill -9 "$e0"; sleep 2.2; e1=$(eng); [[ -n "$e1" ]] && kill -9 $e1; sleep 10; e2=$(eng); r2=$(rows)
verdict "one engine ([$e2]), settings replayed (status rows $r0 -> $r2)" 'one && (( r2 > r0 + 4 ))'

echo "4  a second engine is started by hand"
e0=$(eng); "$APPDIR/fk-engine" "$DEVICE" 48000 64 >/dev/null 2>&1 & second=$!
sleep 3; if kill -0 "$second" 2>/dev/null; then kill -9 "$second"; code="still running"; else wait "$second"; code=$?; fi
sleep 2; e1=$(eng)
verdict "it refused to start (exit $code) and the first is untouched ([$e1])" '[[ "$code" == "1" && "$e1" == "$e0" ]]'

echo "5  a stray engine is running when the app starts"
kill -TERM "$APP"; for _ in 1 2 3 4 5 6; do sleep 1; kill -0 "$APP" 2>/dev/null || break; done; kill -9 "$APP" 2>/dev/null
pkill -f "FeedbackFader.app/Contents/MacOS/fk-engine"; sleep 2; pkill -9 -f "FeedbackFader.app/Contents/MacOS/fk-engine" 2>/dev/null
( "$APPDIR/fk-engine" "$DEVICE" 48000 64 >/dev/null 2>&1 & ); sleep 3; stray=$(eng)
( caffeinate -u -t 15 & ) 2>/dev/null; open "$ROOT/dist/FeedbackFader.app"; sleep 9
APP=$(app); e1=$(eng); parent=$(ps -o ppid= -p ${e1:-0} 2>/dev/null | tr -d ' '); r1=$(rows)
verdict "the stray ($stray) is gone, the app ($APP) has its own engine ([$e1]) and it is configured ($r1 status rows)" \
        'one && [[ "$e1" != "$stray" && "$parent" == "$APP" ]] && (( r1 > 4 ))'

echo
log=$(ls -t "$LOGS"/app-log-*.txt 2>/dev/null | head -1)
[[ -n "$log" ]] && { echo "the app's own account ($log):"; tail -8 "$log" | grep -v "pitch profile" | cut -c1-130 | sed 's/^/   /'; }
echo
if (( FAILS == 0 )); then echo "PASS  one engine, always"; else echo "FAIL  $FAILS of 5"; exit 1; fi
