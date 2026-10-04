# Results

Generated 2026-10-04 from `results/ledger.jsonl` by `scripts/results.py`. **Do not edit** - record a result and regenerate. What each test is: [TESTING.md](TESTING.md). Goals and thresholds: [TODO-AS-BUILT.md](TODO-AS-BUILT.md).

- **Simulated** - the guard inside a simulated room, with a singer. **Recorded** - real feedback and real singing from the rig, replayed. **Live** - the room itself.
- A **build** is named by the last commit that changed the engine's code. Past builds were re-measured with today's tests (`scripts/measure_version.py`), so every column uses one ruler.

## The latest build against the three goals

Build `9d17858` (10-04 14:51).

| | Goal | Room like the rig | Reverberant hall |
|---|---|---|---|
| **Caught / killed** | catch within 10 ms of audible, kill within 15 | +6: none heard; +10: 1 heard for 40 ms (rig-0927), a cut already on it, kill 57 ms; +15: 2 heard for 103 ms (rig-0927), a cut already on it, kill 110 ms; +20: lost (50 rings, 7 s, rig-0927) | +6: 1 heard for 72 ms (synth), a cut already on it, kill 354 ms; +10: 9 heard for 2559 ms (rig-0927), never cut, kill 695 ms; +15: lost (28 rings, 24 s, rig-0927); +20: lost (15 rings, 18 s, rig-0927) |
| **Sound** (voice change: synth / rig-0927) | under 3 % with nothing ringing, under 15 % holding feedback | nothing ringing: 37 % / 65 %; +6: 48 % / 64 %; +10: 50 % / 64 % | nothing ringing: 37 % / 65 %; +6: 67 % / 69 %; +10: 69 % / 71 % |

## Feedback, by build

Rings *heard* are lines the singer did not sing, louder than 20 dB under the voice, that grew (synthetic singer). "Held to" is how far a slow push got, in dB over the room's limit, before a ring was audible for a quarter of a second: the median of five runs that differ only in the room's noise, with the range beside it, for each voice (synth / rig-0927). One run alone moves by 3 dB or more for no reason, so only a difference larger than the ranges means anything.

| build | date | recorded howls caught | let go | median lead | fast risers: level when cut | rig-like: rings heard +10 / +15 / +20 | rig-like held to, dB | hall: rings heard +6 / +10 | hall held to, dB |
|---|---|---|---|---|---|---|---|---|---|
| `9d17858` | 10-04 14:51 | 22 of 22 | 0 | 469 ms | -78 dB | 0 / 0 / 29 | +19.5 (17.3 to 22.6) / +19.1 (17.4 to 20.5) | 1 / 2 | +11.4 (9.4 to 15.1) / +14.1 (12.6 to 15.5) |
| `d55f580` | 10-04 09:19 | 22 of 22 | 0 | 469 ms | -78 dB | 0 / 0 / 29 | +19.5 (17.3 to 22.6) / +19.1 (17.4 to 20.5) | 1 / 2 | +11.4 (9.4 to 15.1) / +14.1 (12.6 to 15.5) |
| `4b1c88c` | 10-03 22:27 | 22 of 22 | 0 | 469 ms | -40 dB | 0 / 0 / 16 | +19.4 (17.9 to 22.3) / +19.8 (15.9 to 23.4) | 1 / 2 | +12.5 (10.7 to 14.1) / +13.8 (10.9 to 15.0) |
| `f5c5f0b` | 10-03 22:17 | 22 of 22 | 0 | 469 ms | -40 dB | 0 / 0 / 16 | +19.4 (17.9 to 22.3) / +19.8 (15.9 to 23.4) | 1 / 2 | +12.5 (10.7 to 14.1) / +13.8 (10.9 to 15.0) |
| `294bcc8` | 10-03 21:24 | 22 of 22 | 0 | 469 ms | -40 dB | 0 / 0 / 20 | +19.4 (18.8 to 20.9) / +20.5 (16.6 to 21.8) | 1 / 2 | +12.5 (10.7 to 14.1) / +13.8 (10.9 to 15.0) |
| `fce3790` | 10-03 20:14 | 21 of 22 | 0 | 475 ms | -28 dB | 0 / 1 / 7 | +18.8 (17.3 to 20.3) / +19.1 (17.8 to 21.0) | 0 / 5 | +12.9 (9.9 to 14.3) / +14.9 (12.4 to 15.8) |
| `69f6ef0` | 10-03 19:33 | 22 of 22 | 0 | 459 ms | -35 dB | 0 / 0 / 24 | +19.4 (18.8 to 20.8) / +19.8 (18.0 to 20.4) | 0 / 5 | +14.2 (7.9 to 14.9) / +14.0 (12.0 to 15.0) |
| `11fe355` | 10-03 18:12 | 22 of 22 | 0 | 459 ms | -29 dB | 0 / 0 / 17 | +20.2 (17.3 to 20.9) / +19.9 (19.1 to 21.1) | 0 / 3 | +13.4 (10.0 to 14.7) / +13.8 (11.7 to 15.2) |

## Sound, by build

Voice change: how much of what the ear gets from the voice differs from the clean voice. A 1 dB level change is 8 %, 3 dB is 22 %. Goal: 3 % with nothing ringing, 15 % holding feedback.

| build | six recorded sung phrases: taken, filters | synth: nothing ringing | rig-0927: nothing ringing | synth: +6 / +10 | rig-0927: +6 / +10 | filters on a singer, nothing ringing |
|---|---|---|---|---|---|---|
| `9d17858` | 37 %, 29 | 37 % | 65 % | 48 % / 50 % | 64 % / 64 % | 31 / 42 |
| `d55f580` | 37 %, 29 | 37 % | 65 % | 48 % / 50 % | 64 % / 64 % | 31 / 42 |
| `4b1c88c` | 37 %, 29 | 37 % | 65 % | 48 % / 50 % | 63 % / 63 % | 31 / 42 |
| `f5c5f0b` | 37 %, 29 | 37 % | 65 % | 48 % / 50 % | 63 % / 63 % | 31 / 42 |
| `294bcc8` | 37 %, 29 | 37 % | 65 % | 48 % / 50 % | 63 % / 63 % | 31 / 42 |
| `fce3790` | 36 %, 27 | 38 % | 66 % | 49 % / 51 % | 63 % / 64 % | 30 / 42 |
| `69f6ef0` | 36 %, 28 | 36 % | 65 % | 49 % / 53 % | 64 % / 65 % | 28 / 42 |
| `11fe355` | 36 %, 28 | 38 % | 65 % | 49 % / 50 % | 65 % / 68 % | 30 / 42 |

## The other gates, by build

| build | unit tests | fuzz: runaway modes | simulated room: top end quieter at 3 / 9 dB over | cold start 20 over: seconds above -40 dB | sung fixtures hit (of 6) | measured loop names the ring | started cold with pinned filters: detections |
|---|---|---|---|---|---|---|---|
| `9d17858` | 58 | 111 | 62 / 62 dB | 0.06 | 2 | yes | 0 |
| `d55f580` | 58 | 144 | 62 / 62 dB | 0.06 | 2 | yes | 0 |
| `4b1c88c` | - | - | 62 / 62 dB | 0.06 | 2 | yes | 0 |
| `f5c5f0b` | - | - | 62 / 62 dB | 0.06 | 2 | - | - |
| `294bcc8` | - | - | 62 / 62 dB | 0.36 | 2 | - | - |
| `fce3790` | - | - | 61 / 62 dB | 1.22 | 2 | - | - |
| `69f6ef0` | - | - | 61 / 62 dB | 0.74 | 2 | - | - |
| `11fe355` | - | - | 62 / 62 dB | 1.02 | 2 | - | - |

Unit tests and fuzz are each build's own programs and are only recorded from the build that was current when they ran. The fuzz's own settings were corrected at `9d17858` (six confirm frames and the engine's 10 s hold, where it had run four and 2 s), so its counts before and after are not comparable.

## In the room

A sweep's "dB over" is counted from the nearest baseline in the half hour before it. The room's ring point moves by ten decibels within minutes, so a figure without a baseline either side is an estimate.

| when | build | what | result |
|---|---|---|---|
| 09-27 17:45 | `e2a52c2` | voice in a session | the guard took **5.9 %** of the voice (9 s of voice, 58-68 s); -0.3 dB over 1-4 kHz, -5.0 dB over 4-16 kHz |
| 09-27 17:45 | `e2a52c2` | voice in a session | the guard took **7.6 %** of the voice (6 s of voice, 71-77 s); -1.2 dB over 1-4 kHz, -4.5 dB over 4-16 kHz |
| 09-27 17:45 | `e2a52c2` | voice in a session | the guard took **7.3 %** of the voice (5 s of voice, 78-83 s); -1.2 dB over 1-4 kHz, -4.9 dB over 4-16 kHz |
| 09-27 17:45 | `e2a52c2` | voice in a session | the guard took **6.6 %** of the voice (6 s of voice, 95-101 s); -1.2 dB over 1-4 kHz, -4.8 dB over 4-16 kHz |
| 09-27 17:45 | `e2a52c2` | voice in a session | the guard took **13.2 %** of the voice (8 s of voice, 212-220 s); -4.0 dB over 1-4 kHz, -4.0 dB over 4-16 kHz |
| 10-03 18:23 | `3629b7f` † | baseline | guard off: the room rings at -9.5 dB on the faders, 305 Hz |
| 10-03 18:27 | `3629b7f` † | baseline | guard off: the room rings at -9.5 dB on the faders, 9559 Hz |
| 10-03 18:30 | `3629b7f` † | guarded sweep | guard on: reached +3.0 dB (**+12.5 dB over** the last baseline); loudest line -47.9 dB at 9562 Hz; 16 catches, 0 duck actions |
| 10-03 18:32 | `3629b7f` † | guarded sweep | guard on: reached +6.5 dB (**+16.0 dB over** the last baseline); loudest line -25.5 dB at 9891 Hz; 72 catches, 0 duck actions; **kill switch tripped** |
| 10-03 18:37 | `3629b7f` † | guarded sweep | guard on: reached +6.0 dB (**+15.5 dB over** the last baseline); 46 catches, 0 duck actions; **kill switch tripped** |
| 10-03 18:53 | `b71ce6f` | baseline | guard off: the room rings at -12.0 dB on the faders, 9870 Hz |
| 10-03 18:55 | `b71ce6f` | guarded sweep | guard on: reached +8.0 dB (**+20.0 dB over** the last baseline); loudest line -72.3 dB at 9480 Hz; 44 catches, 0 duck actions |
| 10-03 19:07 | `b71ce6f` | guarded sweep | guard on: reached +7.0 dB (**+19.0 dB over** the last baseline); loudest line -34.7 dB at 4652 Hz; 20 catches, 0 duck actions; **kill switch tripped** |
| 10-03 19:08 | `b71ce6f` | guarded sweep | guard on: reached +6.0 dB (**+18.0 dB over** the last baseline); loudest line -21.4 dB at 9879 Hz; 9 catches, 0 duck actions; **kill switch tripped** |
| 10-03 19:36 | `69f6ef0` | guarded sweep | guard on: reached +10.0 dB; loudest line -50.5 dB at 8367 Hz; 51 catches, 0 duck actions |
| 10-03 19:40 | `69f6ef0` | guarded sweep | guard on: reached +6.0 dB; loudest line -64.6 dB at 9867 Hz; 27 catches, 0 duck actions |
| 10-03 19:58 | `69f6ef0` | guarded sweep | guard on: reached +8.0 dB; loudest line -45.4 dB at 9973 Hz; 24 catches, 0 duck actions |
| 10-03 19:58 | `69f6ef0` | guarded sweep | guard on: reached +10.0 dB; loudest line -31.1 dB at 14637 Hz; 41 catches, 0 duck actions |
| 10-03 20:17 | `fce3790` | guarded sweep | guard on: reached +8.0 dB; loudest line -65.8 dB at 9984 Hz; 22 catches, 0 duck actions |
| 10-03 20:17 | `fce3790` | guarded sweep | guard on: reached +10.0 dB; loudest line -65.4 dB at 9984 Hz; 36 catches, 0 duck actions |
| 10-03 20:18 | `fce3790` | guarded sweep | guard on: reached +10.0 dB; loudest line -66.0 dB at 9984 Hz; 27 catches, 0 duck actions |
| 10-03 20:19 | `fce3790` | baseline | guard off: the room rings at -4.0 dB on the faders, 9979 Hz |
| 10-03 20:20 | `fce3790` | guarded sweep | guard on: reached +16.0 dB (**+20.0 dB over** the last baseline); loudest line -21.1 dB at 9984 Hz; 81 catches, 0 duck actions |
| 10-03 20:21 | `fce3790` | guarded sweep | guard on: reached +18.0 dB (**+22.0 dB over** the last baseline); loudest line -23.5 dB at 9973 Hz; 102 catches, 0 duck actions |
| 10-03 20:22 | `fce3790` | guarded sweep | guard on: reached +18.0 dB (**+22.0 dB over** the last baseline); loudest line -23.3 dB at 6832 Hz; 82 catches, 0 duck actions; **kill switch tripped** |
| 10-04 08:42 | `f5c5f0b` | voice in a session | the guard took **69.7 %** of the voice (19 s of voice); -5.4 dB over 1-4 kHz, -7.5 dB over 4-16 kHz |
| 10-04 08:45 | `f5c5f0b` | baseline | guard off: the room rings at -15.0 dB on the faders, 10515 Hz |
| 10-04 08:46 | `f5c5f0b` | guarded sweep | guard on: reached -1.0 dB (**+14.0 dB over** the last baseline); loudest line -62.8 dB at 6773 Hz; 2 catches, 0 duck actions |
| 10-04 08:46 | `f5c5f0b` | guarded sweep | guard on: reached +3.0 dB (**+18.0 dB over** the last baseline); loudest line -71.7 dB at 6832 Hz; 9 catches, 0 duck actions |
| 10-04 08:47 | `f5c5f0b` | guarded sweep | guard on: reached +5.0 dB (**+20.0 dB over** the last baseline); loudest line -71.1 dB at 6070 Hz; 11 catches, 0 duck actions |
| 10-04 08:48 | `f5c5f0b` | guarded sweep | guard on: reached +7.0 dB (**+22.0 dB over** the last baseline); loudest line -71.1 dB at 6070 Hz; 15 catches, 0 duck actions |
| 10-04 08:49 | `f5c5f0b` | baseline | guard off: the room rings at -2.5 dB on the faders, 9971 Hz |
| 10-04 08:53 | `f5c5f0b` | baseline | guard off: the room rings at -0.5 dB on the faders, 9870 Hz |
| 10-04 08:54 | `f5c5f0b` | guarded sweep | guard on: reached +19.5 dB (**+20.0 dB over** the last baseline); loudest line -29.5 dB at 10043 Hz; 37 catches, 2 duck actions; **kill switch tripped** |
| 10-04 08:54 | `f5c5f0b` | baseline | guard off: no ring up to -2.5 dB |
| 10-04 08:54 | `f5c5f0b` | baseline | guard off: the room rings at -8.5 dB on the faders, 122 Hz |
| 10-04 08:54 | `f5c5f0b` | guarded sweep | guard on: reached +13.5 dB (**+22.0 dB over** the last baseline); loudest line -52.0 dB at 1570 Hz; 50 catches, 0 duck actions |
| 10-04 08:55 | `f5c5f0b` | baseline | guard off: the room rings at -2.5 dB on the faders, 9432 Hz |

† The bundle running at that time carried an engine built at 15:22, older than the fix it was shipped under (`docs/wrong-machine-2026-10-03.md`); it is filed under the commit that build came from.

Live builds are named from the engine's start times (the feedback-log file names); from `d55f580` on the engine writes its own build stamp and the tools read it.

Builds with live results and no row in the tables above: `3629b7f` is from before `11fe355` and cannot be re-measured with the rig's settings; `b71ce6f` has the same detector and filter code as `11fe355`; `e2a52c2` is from before `11fe355` and cannot be re-measured with the rig's settings.

## A singer in the loop: every case, build `9d17858`

Gain is dB over the untreated room's limit. *no guard* rows show what the loop itself does to the sound. Tails are the stable room hanging on to a note; ghosts are a filter sounding its own note after the singer stops.

**Room like the rig, synth**

| gain | voice change | added / taken | filters | rings heard | heard for | worst catch | worst kill | loudest | tails | ghosts | duck |
|---|---|---|---|---|---|---|---|---|---|---|---|
| no loop | 37.0 % | 0.0 / 37.0 | 31 | 0 | 0 ms | - | - | - | 0 ms | 0 ms | 0 |
| -6 | 41.4 % | 0.1 / 41.3 | 35 | 0 | 0 ms | - | - | - | 0 ms | 0 ms | 0 |
| -6 *no guard* | 1.1 % | 0.7 / 0.4 | 0 | 0 | 0 ms | - | - | - | 0 ms | 0 ms | 0 |
| -3 | 44.2 % | 0.1 / 44.1 | 37 | 0 | 0 ms | - | - | - | 0 ms | 29 ms | 0 |
| -3 *no guard* | 1.6 % | 1.0 / 0.6 | 0 | 0 | 0 ms | - | - | - | 0 ms | 0 ms | 0 |
| +0 | 48.6 % | 0.1 / 48.5 | 38 | 0 | 0 ms | - | - | - | 0 ms | 29 ms | 0 |
| +3 | 49.2 % | 0.2 / 49.0 | 39 | 0 | 0 ms | - | - | - | 0 ms | 28 ms | 0 |
| +6 | 47.6 % | 0.4 / 47.1 | 40 | 0 | 0 ms | - | - | - | 0 ms | 32 ms | 0 |
| +10 | 49.7 % | 0.8 / 49.0 | 42 | 0 | 0 ms | - | - | - | 0 ms | 29 ms | 0 |
| +15 | 52.9 % | 1.3 / 51.5 | 45 | 0 | 0 ms | - | - | - | 100 ms | 0 ms | 0 |
| +20 | 55.5 % | 8.5 / 47.0 | 47 | 29 | 4954 ms | +266 ms | 757 ms | -11.3 dB | 538 ms | 0 ms | 2 |
| +6, mic moves | 45.5 % | 0.4 / 45.1 | 40 | 0 | 0 ms | - | - | - | 0 ms | 32 ms | 0 |
| slow push (held to +19.5 dB, +17.3 to +22.6 over five runs) | 52.7 % | 3.4 / 49.3 | 45 | 33 | 4805 ms | -143 ms | 541 ms | -12.8 dB | 757 ms | 10 ms | 6 |

**Room like the rig, rig-0927**

| gain | voice change | added / taken | filters | rings heard | heard for | worst catch | worst kill | loudest | tails | ghosts | duck |
|---|---|---|---|---|---|---|---|---|---|---|---|
| no loop | 64.8 % | 0.0 / 64.7 | 42 | 0 | 0 ms | - | - | - | 0 ms | 0 ms | 0 |
| -6 | 62.9 % | 0.0 / 62.9 | 42 | 0 | 0 ms | - | - | - | 0 ms | 0 ms | 0 |
| -6 *no guard* | 0.9 % | 0.6 / 0.3 | 0 | 0 | 0 ms | - | - | - | 0 ms | 0 ms | 0 |
| -3 | 63.9 % | 0.0 / 63.9 | 41 | 0 | 0 ms | - | - | - | 0 ms | 0 ms | 0 |
| -3 *no guard* | 1.4 % | 1.0 / 0.4 | 0 | 0 | 0 ms | - | - | - | 0 ms | 0 ms | 0 |
| +0 | 62.9 % | 0.0 / 62.9 | 41 | 0 | 0 ms | - | - | - | 0 ms | 0 ms | 0 |
| +3 | 65.7 % | 0.0 / 65.6 | 41 | 0 | 0 ms | - | - | - | 0 ms | 0 ms | 0 |
| +6 | 63.9 % | 0.1 / 63.8 | 42 | 0 | 0 ms | - | - | - | 0 ms | 0 ms | 0 |
| +10 | 64.2 % | 0.2 / 64.0 | 44 | 1 | 40 ms | -597 ms | 57 ms | -45.5 dB | 0 ms | 0 ms | 0 |
| +15 | 62.8 % | 0.8 / 62.0 | 45 | 2 | 103 ms | -643 ms | 110 ms | -31.1 dB | 103 ms | 0 ms | 1 |
| +20 | 77.9 % | 5.3 / 72.6 | 47 | 50 | 6658 ms | +192 ms | 539 ms | -12.3 dB | 1097 ms | 0 ms | 12 |
| +6, mic moves | 61.7 % | 0.1 / 61.6 | 42 | 0 | 0 ms | - | - | - | 0 ms | 0 ms | 0 |
| slow push (held to +19.1 dB, +17.4 to +20.5 over five runs) | 68.4 % | 2.8 / 65.6 | 46 | 47 | 6415 ms | +267 ms | 504 ms | -13.8 dB | 1241 ms | 65 ms | 8 |

**Reverberant hall, synth**

| gain | voice change | added / taken | filters | rings heard | heard for | worst catch | worst kill | loudest | tails | ghosts | duck |
|---|---|---|---|---|---|---|---|---|---|---|---|
| no loop | 37.0 % | 0.0 / 37.0 | 31 | 0 | 0 ms | - | - | - | 0 ms | 0 ms | 0 |
| -6 | 50.6 % | 0.7 / 49.9 | 36 | 0 | 0 ms | - | - | - | 152 ms | 0 ms | 0 |
| -6 *no guard* | 6.9 % | 4.7 / 2.3 | 0 | 0 | 0 ms | - | - | - | 315 ms | 0 ms | 0 |
| -3 | 58.2 % | 0.8 / 57.4 | 38 | 0 | 0 ms | - | - | - | 155 ms | 0 ms | 0 |
| -3 *no guard* | 10.6 % | 7.5 / 3.1 | 0 | 0 | 0 ms | - | - | - | 879 ms | 0 ms | 0 |
| +0 | 59.8 % | 1.8 / 58.0 | 40 | 0 | 0 ms | - | - | - | 482 ms | 0 ms | 0 |
| +3 | 61.9 % | 1.1 / 60.8 | 42 | 0 | 0 ms | - | - | - | 137 ms | 0 ms | 0 |
| +6 | 67.3 % | 1.4 / 65.8 | 43 | 1 | 72 ms | -590 ms | 354 ms | -44.6 dB | 796 ms | 0 ms | 0 |
| +10 | 69.4 % | 7.7 / 61.7 | 45 | 2 | 234 ms | -510 ms | 158 ms | -33.0 dB | 1984 ms | 0 ms | 0 |
| +15 | 108.1 % | 50.7 / 57.4 | 48 | 12 | 7071 ms | -272 ms | 4915 ms | -7.0 dB | 613 ms | 0 ms | 4 |
| +20 | 167.7 % | 126.7 / 41.0 | 48 | 6 | 13949 ms | +19 ms | 7903 ms | -8.3 dB | 0 ms | 0 ms | 6 |
| +6, mic moves | 67.1 % | 1.4 / 65.7 | 43 | 0 | 0 ms | - | - | - | 766 ms | 0 ms | 0 |
| slow push (held to +11.4 dB, +9.4 to +15.1 over five runs) | 121.3 % | 74.3 / 47.0 | 47 | 3 | 18226 ms | -575 ms | 14793 ms | -7.1 dB | 1438 ms | 0 ms | 2 |

**Reverberant hall, rig-0927**

| gain | voice change | added / taken | filters | rings heard | heard for | worst catch | worst kill | loudest | tails | ghosts | duck |
|---|---|---|---|---|---|---|---|---|---|---|---|
| no loop | 64.8 % | 0.0 / 64.7 | 42 | 0 | 0 ms | - | - | - | 0 ms | 0 ms | 0 |
| -6 | 61.7 % | 0.1 / 61.6 | 41 | 0 | 0 ms | - | - | - | 0 ms | 0 ms | 0 |
| -6 *no guard* | 5.0 % | 3.4 / 1.6 | 0 | 0 | 0 ms | - | - | - | 0 ms | 0 ms | 0 |
| -3 | 68.4 % | 0.2 / 68.2 | 42 | 0 | 0 ms | - | - | - | 0 ms | 0 ms | 0 |
| -3 *no guard* | 7.7 % | 5.8 / 2.0 | 0 | 0 | 0 ms | - | - | - | 1 ms | 0 ms | 0 |
| +0 | 65.2 % | 0.4 / 64.9 | 43 | 0 | 0 ms | - | - | - | 0 ms | 0 ms | 0 |
| +3 | 66.8 % | 0.7 / 66.1 | 43 | 0 | 0 ms | - | - | - | 199 ms | 0 ms | 0 |
| +6 | 68.7 % | 1.2 / 67.5 | 44 | 0 | 0 ms | - | - | - | 2 ms | 0 ms | 0 |
| +10 | 70.6 % | 3.6 / 67.1 | 46 | 9 | 2559 ms | +461 ms | 695 ms | -26.9 dB | 2694 ms | 0 ms | 0 |
| +15 | 194.7 % | 142.1 / 52.6 | 48 | 28 | 24384 ms | -272 ms | 19208 ms | -4.0 dB | 224 ms | 0 ms | 4 |
| +20 | 167.5 % | 115.9 / 51.5 | 48 | 15 | 17988 ms | -578 ms | 3855 ms | -8.8 dB | 0 ms | 0 ms | 15 |
| +6, mic moves | 69.4 % | 1.0 / 68.4 | 44 | 0 | 0 ms | - | - | - | 3 ms | 0 ms | 0 |
| slow push (held to +14.1 dB, +12.6 to +15.5 over five runs) | 92.2 % | 30.5 / 61.7 | 46 | 13 | 10454 ms | -578 ms | 2896 ms | -9.1 dB | 91 ms | 0 ms | 10 |

## Rulers

Which test code measured which build: `11fe355` by `1416d5b`; `11fe355` by `9d17858`; `11fe355` by `f81455d`; `294bcc8` by `1416d5b`; `294bcc8` by `9d17858`; `294bcc8` by `f81455d`; `4b1c88c` by `1416d5b`; `4b1c88c` by `9d17858`; `4b1c88c` by `f81455d`; `69f6ef0` by `1416d5b`; `69f6ef0` by `9d17858`; `69f6ef0` by `f81455d`; `9d17858` by `9d17858`; `b71ce6f` by `1416d5b`; `b71ce6f` by `9d17858`; `d55f580` by `1416d5b`; `d55f580` by `9d17858`; `d55f580` by `f81455d`; `f5c5f0b` by `1416d5b`; `f5c5f0b` by `9d17858`; `f5c5f0b` by `f81455d`; `fce3790` by `1416d5b`; `fce3790` by `9d17858`; `fce3790` by `f81455d`.
