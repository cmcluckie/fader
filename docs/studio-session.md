# The next studio session — what to collect

Agreed with Chris on 2026-10-04. The tests are now a good memory of one room
(22 real howls, 18 seconds of voice, all from the home rig) and a poor
predictor of anything new. A second room is the data they lack. The aim of the
visit is to **collect, not fix**.

## On the day, in order

1. **Is the guard in the loop?** Mute its output and talk into the microphone.
   Silence in the monitors means yes. If you can still hear yourself there is a
   raw path beside it and nothing that follows will mean anything
   (`docs/routing-2026-09-30.md`).
2. **Capture on** (Setup, Signal path) for the whole session; **off before you
   leave**. A recorder left running once wrote 50 GB.
3. **Two minutes of singing at low gain, no feedback anywhere.** Clean voice is
   the scarcest data there is, and it is the reference a voice-damage measure
   needs.
4. **The same material at working gain.**
5. **A slow push:** raise the gain gradually until the guard is visibly holding
   rings, and sit there a minute.
6. **Cold jumps:** mute, raise the gain, unmute. Four or five times, a little
   hotter each time. This is the case that has found a new failure in every
   live session so far.
7. Note anything that sounded wrong and roughly when. That is all; the
   recording has the rest.

## What should exist by then

- The engine's sweep probe and `scripts/measure_loop.py` (plan item 2): a
  two-second measurement of that room's loop, first thing, so the room itself
  can be put in the simulator.
- A voice-damage measure (plan section 3, item 1), so the session is scored on
  what it did to the singing and not only on what it caught.
- `ringout.py` and `coldjump.py` taking the desk's address as an option. They
  speak X32/M32 OSC; if the studio desk is one and is on the network, steps 5
  and 6 can be driven and measured rather than done by hand.

## Afterwards

Every howl and every sung phrase from the session becomes a fixture, each one
shown to fail on the build that produced it before it is counted.
