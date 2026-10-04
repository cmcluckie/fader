# The standard tests

What is measured, how, and where the numbers go. The numbers themselves are in
[RESULTS.md](RESULTS.md); the goals they are judged against are in
[TODO-AS-BUILT.md](TODO-AS-BUILT.md).

## One command

```bash
scripts/preship.sh            # the gate: about 90 s, fails the build if anything got worse
```

```bash
scripts/preship.sh --record   # the same, and every number is kept under this build
```

Recording is refused on uncommitted changes: a working tree is not a version.
After recording, `scripts/results.py` regenerates `docs/RESULTS.md`.

## Three kinds of result

| Kind | What it is | Trust it for |
|---|---|---|
| **Simulated** | the real guard inside a simulated room, with a singer | comparing builds; what a change does to the voice; cases too dangerous or too slow to do live |
| **Recorded** | real feedback and real singing from the rig, replayed through the guard | "is it still right about what the rig actually did" |
| **Live** | the room, the desk, the speakers | the only proof that it works. Everything else is a rehearsal. |

A recording cannot answer back when it is cut and a simulation is only as good
as its room, so a feature is not done until it has passed live.

## The tests

| # | Test | Kind | Tool | What it says |
|---|---|---|---|---|
| 1 | Unit tests | bench | `fk-tests` | 58 checks that each mechanism does what it claims. One (T42) is kept failing on purpose. |
| 2 | Random feedback | bench | `fk-fuzz` | thousands of generated rings; how many ran away. Six seeds in the gate is a smoke test - differences smaller than a few percent are noise; use 20 seeds for a verdict (`scripts/fuzz-sweep.py`). |
| 3 | Simulated room | simulated | `preship.py` | top end with the guard against without, at 3 and 9 dB over; wired out of its own loop; a cold start 20 dB over. |
| 4 | Recorded feedback | recorded | `replay_gate.py` | 22 real howls: caught, how early, cut deep enough, not let go. 6 sung phrases: the marked frequency left alone, and (since 10-04) the whole phrase scored. |
| 5 | Measure and pre-place | simulated | `loop_measure_check.py` | a measured loop names the note that will ring; filters pinned from it beat reacting. |
| 6 | **A singer in the loop** | simulated | `quality.py` | the standard quality and timing test - below. |
| 7 | Ring-out sweep | live | `scripts/ringout.py` | where the room rings with the guard off, and how far the guard holds it. |
| 8 | Cold jump | live | `scripts/coldjump.py` | a jump to N dB over, between two baselines; counted only if they agree within 3 dB. |
| 9 | Voice in a session | live | `scripts/session_score.py` | what the guard took from the voice in a real recording. |

Layers 1-6 are the ship gate. 7-9 need Chris in the room.

## A singer in the loop

A voice cannot be mixed on top of simulated feedback afterwards. In a room the
singer is what the loop feeds on, so the voice goes in at the microphone and
comes round through the speaker, the room and the guard. This is how the
published comparisons are run (`docs/references/`).

```
guard input = singer + room noise + (speaker output, back through the room)
speaker     = amplifier( fader x guard(guard input) )
```

The fader is after the guard, as on the rig. Levels are the rig's: singing at
-30 dBFS at the guard, the room at -66. The same voice through the same
amplifier and speaker with **no loop and no guard** is the clean reference;
everything is scored against it.

- **Rooms.** One shaped like the home rig (microphone half a metre from the
  speaker, rings at 8-13 kHz). One reverberant hall (three metres, rings at
  0.2-2 kHz).
- **Voices.** A synthetic singer that is in the repository, so the test is the
  same on any machine. Real singing cut from the rig's recordings
  (`make_voices.py`), kept out of the repository because it is public.
- **Gains.** No loop at all; 6 and 3 dB under the room's limit; at the limit;
  3, 6, 10, 15, 20 dB over. Also: 6 over with the microphone moving, and a
  slow push from 6 under at half a decibel a second until it loses.
- **Scores.** Voice change %, split into added and taken. Rings heard, how
  long, catch and kill time, loudest. Tails and ghosts, separately. Filters
  held. Duck actions. Whether the loop is stable at the end by the stability
  condition itself.

```bash
cd engine/tests/sim
python3 quality.py                    # everything: 52 runs, about 70 s
python3 quality.py --gate             # the quick subset the ship gate runs
python3 quality.py --selftest         # the ruler against known inputs
python3 quality.py --room rig --voice synth --case 10 -v --wav out/    # one case, rings listed, audio written
```

`--wav` writes what came out on the left and the clean reference on the right,
so a number can be listened to.

## Measuring a past build

```bash
scripts/measure_version.py --since 11fe355
```

Builds each past engine's test library and runs today's tests 3-6 against it,
so two builds are compared with one ruler. Reaches back to `11fe355`, the first
build whose library could be given the rig's settings.

## The results log

`results/ledger.jsonl`: one line per result, appended, never rewritten. Each
line has the time, the **build** (the last commit that changed engine code; for
a live run, the build the running engine reports), the **ruler** (the commit of
the test code that measured it), the kind, the test, pass or fail, and the
numbers.

## Rules learned the hard way

1. **A test must fail on the old code first.** A test that has only ever passed
   has not been shown to measure anything.
2. **Know which build is running.** Three hours of live measurements once
   belonged to a different binary. The sweep prints the engine's own stamp.
3. **A live number needs a baseline on each side.** The room's ring point moved
   12 dB in five minutes.
4. **The tests run the rig's settings**, read from its own file - for a week
   they ran a machine the room had never heard.
5. **A replay must copy what it feeds the guard.** Processing in place leaks
   the cuts into the "recording" and flatters the result.
6. **Judge the voice as a voice.** Six sung fixtures passed for a week judged
   at one frequency each while carrying about thirty filters apiece.
7. **A rebuilt past build must prove what it is.** Rebuilding seven old builds
   in a row produced three copies of their neighbours: the compile was
   skipped and the file was labelled as if it had not been. Each library is
   now rebuilt from nothing, stamped with its sources, and refused if it is
   byte-identical to one built from different sources.
8. **Do not touch the tree while a recording run is in progress.** The log
   refuses results from uncommitted work, including a new file created half
   way through.
