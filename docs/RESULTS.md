# Results

Generated 2026-10-04 from `results/ledger.jsonl` by `scripts/results.py`. **Do not edit** - record a result and regenerate. What each test is: [TESTING.md](TESTING.md). Goals and thresholds: [TODO-AS-BUILT.md](TODO-AS-BUILT.md).

- **Simulated** - the guard inside a simulated room, with a singer. **Recorded** - real feedback and real singing from the rig, replayed. **Live** - the room itself.
- A **build** is named by the last commit that changed the engine's code. Past builds were re-measured with today's tests (`scripts/measure_version.py`), so every column uses one ruler.

## The latest build against the three goals

Build `d55f580` (10-04 09:19).

| | Goal | Room like the rig | Reverberant hall |
|---|---|---|---|
| **Caught / killed** | catch within 10 ms of audible, kill within 15 | +6: none heard; +10: none heard; +15: none heard; +20: 14 heard, catch -582 ms, kill 435 ms | +6: none heard; +10: 2 heard, catch -512 ms, kill 170 ms; +15: 9 heard, catch -272 ms, kill 7091 ms; +20: 7 heard, catch +19 ms, kill 4723 ms |
| **Sound** (voice change: synth / rig-0927) | under 3 % with nothing ringing, under 15 % holding feedback | nothing ringing: 32 % / 60 %; +6: 42 % / 61 %; +10: 48 % / 62 % | nothing ringing: 32 % / 60 %; +6: 64 % / 65 %; +10: 68 % / 69 % |

## Feedback, by build

Rings *heard* are lines the singer did not sing, louder than 20 dB under the voice, that grew (synthetic singer). "Held to" is how far a slow push got, in dB over the room's limit, before a ring was audible for a quarter of a second: the median of five runs that differ only in the room's noise, with the range beside it, for each voice (synth / rig-0927). One run alone moves by 3 dB or more for no reason, so only a difference larger than the ranges means anything.

| build | date | recorded howls caught | let go | median lead | fast risers: level when cut | rig-like: rings heard +10 / +15 / +20 | rig-like held to, dB | hall: rings heard +6 / +10 | hall held to, dB |
|---|---|---|---|---|---|---|---|---|---|
| `d55f580` | 10-04 09:19 | 22 of 22 | 0 | 469 ms | -78 dB | 0 / 0 / 14 | +21.2 (16.8 to 22.7) / +19.6 (17.9 to 20.4) | 0 / 2 | +11.4 (9.9 to 14.7) / +14.4 (13.6 to 15.0) |
| `4b1c88c` | 10-03 22:27 | 22 of 22 | 0 | 469 ms | -40 dB | 0 / 0 / 22 | +20.0 (19.9 to 21.1) / +20.3 (19.6 to 21.8) | 0 / 3 | +11.4 (10.7 to 14.2) / +14.0 (12.7 to 14.9) |
| `f5c5f0b` | 10-03 22:17 | 22 of 22 | 0 | 469 ms | -40 dB | 0 / 0 / 22 | +20.0 (19.9 to 21.1) / +20.3 (19.6 to 21.8) | 0 / 3 | +11.4 (10.7 to 14.2) / +14.0 (12.7 to 14.9) |
| `294bcc8` | 10-03 21:24 | 22 of 22 | 0 | 469 ms | -40 dB | 0 / 0 / 33 | +19.8 (18.9 to 20.9) / +20.4 (19.7 to 21.7) | 0 / 3 | +11.4 (10.7 to 14.2) / +14.0 (12.7 to 14.9) |
| `fce3790` | 10-03 20:14 | 21 of 22 | 0 | 475 ms | -28 dB | 0 / 1 / 11 | +19.4 (18.7 to 20.8) / +20.0 (19.2 to 20.4) | 1 / 9 | +10.0 (7.9 to 11.4) / +14.5 (13.9 to 16.2) |
| `69f6ef0` | 10-03 19:33 | 22 of 22 | 0 | 459 ms | -35 dB | 0 / 0 / 28 | +19.5 (18.8 to 19.8) / +20.4 (18.0 to 20.7) | 0 / 5 | +12.6 (10.0 to 14.1) / +13.9 (13.8 to 15.0) |
| `11fe355` | 10-03 18:12 | 22 of 22 | 0 | 459 ms | -29 dB | 0 / 3 / 35 | +20.3 (16.7 to 20.9) / +18.5 (16.2 to 19.9) | 1 / 3 | +13.4 (7.9 to 14.6) / +14.5 (13.5 to 15.0) |

## Sound, by build

Voice change: how much of what the ear gets from the voice differs from the clean voice. A 1 dB level change is 8 %, 3 dB is 22 %. Goal: 3 % with nothing ringing, 15 % holding feedback.

| build | six recorded sung phrases: taken, filters | synth: nothing ringing | rig-0927: nothing ringing | synth: +6 / +10 | rig-0927: +6 / +10 | filters on a singer, nothing ringing |
|---|---|---|---|---|---|---|
| `d55f580` | 37 %, 29 | 32 % | 60 % | 42 % / 48 % | 61 % / 62 % | 31 / 42 |
| `4b1c88c` | 37 %, 29 | 32 % | 60 % | 42 % / 46 % | 61 % / 59 % | 31 / 42 |
| `f5c5f0b` | 37 %, 29 | 32 % | 60 % | 42 % / 46 % | 61 % / 59 % | 31 / 42 |
| `294bcc8` | 37 %, 29 | 32 % | 60 % | 42 % / 46 % | 61 % / 59 % | 31 / 42 |
| `fce3790` | 36 %, 27 | 32 % | 61 % | 43 % / 47 % | 60 % / 58 % | 29 / 42 |
| `69f6ef0` | 36 %, 28 | 31 % | 60 % | 43 % / 47 % | 57 % / 60 % | 28 / 41 |
| `11fe355` | 36 %, 28 | 32 % | 60 % | 42 % / 46 % | 60 % / 60 % | 30 / 42 |

## The other gates, by build

| build | unit tests | fuzz: runaway modes | simulated room: top end quieter at 3 / 9 dB over | cold start 20 over: seconds above -40 dB | sung fixtures hit (of 6) | measured loop names the ring | started cold with pinned filters: detections |
|---|---|---|---|---|---|---|---|
| `d55f580` | 58 | 144 | 61 / 61 dB | 0.06 | 2 | yes | 0 |
| `4b1c88c` | - | - | 61 / 61 dB | 0.06 | 2 | yes | 0 |
| `f5c5f0b` | - | - | 61 / 61 dB | 0.06 | 2 | - | - |
| `294bcc8` | - | - | 61 / 61 dB | 0.36 | 2 | - | - |
| `fce3790` | - | - | 60 / 61 dB | 1.22 | 2 | - | - |
| `69f6ef0` | - | - | 60 / 61 dB | 0.74 | 2 | - | - |
| `11fe355` | - | - | 61 / 61 dB | 1.02 | 2 | - | - |

Unit tests and fuzz are each build's own programs and are only recorded from the build that was current when they ran.

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

## A singer in the loop: every case, build `d55f580`

Gain is dB over the untreated room's limit. *no guard* rows show what the loop itself does to the sound. Tails are the stable room hanging on to a note; ghosts are a filter sounding its own note after the singer stops.

**Room like the rig, synth**

| gain | voice change | added / taken | filters | rings heard | heard for | worst catch | worst kill | loudest | tails | ghosts | duck |
|---|---|---|---|---|---|---|---|---|---|---|---|
| no loop | 31.9 % | 0.0 / 31.9 | 31 | 0 | 0 ms | - | - | - | 0 ms | 0 ms | 0 |
| -6 | 35.2 % | 0.1 / 35.1 | 34 | 0 | 0 ms | - | - | - | 0 ms | 0 ms | 0 |
| -6 *no guard* | 1.1 % | 0.7 / 0.4 | 0 | 0 | 0 ms | - | - | - | 0 ms | 0 ms | 0 |
| -3 | 39.6 % | 0.1 / 39.5 | 36 | 0 | 0 ms | - | - | - | 0 ms | 32 ms | 0 |
| -3 *no guard* | 1.6 % | 1.0 / 0.6 | 0 | 0 | 0 ms | - | - | - | 0 ms | 0 ms | 0 |
| +0 | 41.9 % | 0.2 / 41.7 | 36 | 0 | 0 ms | - | - | - | 0 ms | 32 ms | 0 |
| +3 | 38.6 % | 0.3 / 38.3 | 39 | 0 | 0 ms | - | - | - | 0 ms | 31 ms | 0 |
| +6 | 42.2 % | 0.5 / 41.7 | 40 | 0 | 0 ms | - | - | - | 0 ms | 34 ms | 0 |
| +10 | 47.6 % | 0.7 / 46.9 | 43 | 0 | 0 ms | - | - | - | 0 ms | 40 ms | 0 |
| +15 | 49.5 % | 1.0 / 48.5 | 45 | 0 | 0 ms | - | - | - | 186 ms | 32 ms | 0 |
| +20 | 60.0 % | 5.2 / 54.7 | 47 | 14 | 2626 ms | -582 ms | 435 ms | -18.0 dB | 931 ms | 23 ms | 3 |
| +6, mic moves | 40.4 % | 0.5 / 39.9 | 40 | 0 | 0 ms | - | - | - | 0 ms | 34 ms | 0 |
| slow push (held to +21.2 dB, +16.8 to +22.7 over five runs) | 49.2 % | 5.5 / 43.8 | 45 | 37 | 9303 ms | +685 ms | 1447 ms | -13.5 dB | 1185 ms | 72 ms | 7 |

**Room like the rig, rig-0927**

| gain | voice change | added / taken | filters | rings heard | heard for | worst catch | worst kill | loudest | tails | ghosts | duck |
|---|---|---|---|---|---|---|---|---|---|---|---|
| no loop | 60.5 % | 0.0 / 60.5 | 42 | 0 | 0 ms | - | - | - | 0 ms | 0 ms | 0 |
| -6 | 57.5 % | 0.0 / 57.5 | 41 | 0 | 0 ms | - | - | - | 0 ms | 0 ms | 0 |
| -6 *no guard* | 0.9 % | 0.6 / 0.3 | 0 | 0 | 0 ms | - | - | - | 0 ms | 0 ms | 0 |
| -3 | 58.9 % | 0.0 / 58.9 | 41 | 0 | 0 ms | - | - | - | 0 ms | 0 ms | 0 |
| -3 *no guard* | 1.4 % | 1.0 / 0.4 | 0 | 0 | 0 ms | - | - | - | 0 ms | 0 ms | 0 |
| +0 | 56.9 % | 0.0 / 56.9 | 41 | 0 | 0 ms | - | - | - | 0 ms | 0 ms | 0 |
| +3 | 60.0 % | 0.0 / 59.9 | 42 | 0 | 0 ms | - | - | - | 0 ms | 0 ms | 0 |
| +6 | 61.3 % | 0.1 / 61.2 | 43 | 0 | 0 ms | - | - | - | 1 ms | 0 ms | 0 |
| +10 | 62.2 % | 0.3 / 61.9 | 44 | 0 | 0 ms | - | - | - | 0 ms | 0 ms | 0 |
| +15 | 62.1 % | 0.6 / 61.5 | 45 | 0 | 0 ms | - | - | - | 74 ms | 0 ms | 0 |
| +20 | 71.1 % | 5.2 / 65.9 | 47 | 39 | 5717 ms | +245 ms | 548 ms | -13.5 dB | 1199 ms | 0 ms | 12 |
| +6, mic moves | 56.3 % | 0.1 / 56.2 | 43 | 0 | 0 ms | - | - | - | 0 ms | 0 ms | 0 |
| slow push (held to +19.6 dB, +17.9 to +20.4 over five runs) | 63.6 % | 2.0 / 61.6 | 46 | 37 | 4093 ms | +159 ms | 1046 ms | -12.6 dB | 993 ms | 0 ms | 7 |

**Reverberant hall, synth**

| gain | voice change | added / taken | filters | rings heard | heard for | worst catch | worst kill | loudest | tails | ghosts | duck |
|---|---|---|---|---|---|---|---|---|---|---|---|
| no loop | 31.9 % | 0.0 / 31.9 | 31 | 0 | 0 ms | - | - | - | 0 ms | 0 ms | 0 |
| -6 | 44.5 % | 0.8 / 43.7 | 37 | 0 | 0 ms | - | - | - | 160 ms | 0 ms | 0 |
| -6 *no guard* | 6.9 % | 4.7 / 2.3 | 0 | 0 | 0 ms | - | - | - | 315 ms | 0 ms | 0 |
| -3 | 53.8 % | 0.8 / 52.9 | 39 | 0 | 0 ms | - | - | - | 160 ms | 0 ms | 0 |
| -3 *no guard* | 10.6 % | 7.5 / 3.1 | 0 | 0 | 0 ms | - | - | - | 879 ms | 0 ms | 0 |
| +0 | 57.6 % | 1.8 / 55.8 | 40 | 0 | 0 ms | - | - | - | 497 ms | 0 ms | 0 |
| +3 | 62.0 % | 1.2 / 60.8 | 42 | 0 | 0 ms | - | - | - | 182 ms | 0 ms | 0 |
| +6 | 64.0 % | 1.7 / 62.4 | 43 | 0 | 0 ms | - | - | - | 828 ms | 0 ms | 0 |
| +10 | 68.5 % | 5.9 / 62.5 | 45 | 2 | 154 ms | -512 ms | 170 ms | -18.6 dB | 1669 ms | 0 ms | 0 |
| +15 | 124.1 % | 73.3 / 50.8 | 48 | 9 | 11009 ms | -272 ms | 7091 ms | -7.0 dB | 137 ms | 0 ms | 1 |
| +20 | 187.3 % | 152.4 / 34.9 | 48 | 7 | 13400 ms | +19 ms | 4723 ms | -9.2 dB | 0 ms | 0 ms | 8 |
| +6, mic moves | 60.1 % | 1.7 / 58.4 | 43 | 0 | 0 ms | - | - | - | 798 ms | 0 ms | 0 |
| slow push (held to +11.4 dB, +9.9 to +14.7 over five runs) | 151.0 % | 111.2 / 39.8 | 43 | 3 | 22973 ms | -584 ms | 22478 ms | -6.2 dB | 911 ms | 0 ms | 0 |

**Reverberant hall, rig-0927**

| gain | voice change | added / taken | filters | rings heard | heard for | worst catch | worst kill | loudest | tails | ghosts | duck |
|---|---|---|---|---|---|---|---|---|---|---|---|
| no loop | 60.5 % | 0.0 / 60.5 | 42 | 0 | 0 ms | - | - | - | 0 ms | 0 ms | 0 |
| -6 | 58.2 % | 0.1 / 58.1 | 41 | 0 | 0 ms | - | - | - | 0 ms | 0 ms | 0 |
| -6 *no guard* | 5.0 % | 3.4 / 1.6 | 0 | 0 | 0 ms | - | - | - | 0 ms | 0 ms | 0 |
| -3 | 62.4 % | 0.2 / 62.2 | 42 | 0 | 0 ms | - | - | - | 0 ms | 0 ms | 0 |
| -3 *no guard* | 7.7 % | 5.8 / 2.0 | 0 | 0 | 0 ms | - | - | - | 1 ms | 0 ms | 0 |
| +0 | 60.2 % | 0.5 / 59.7 | 43 | 0 | 0 ms | - | - | - | 0 ms | 0 ms | 0 |
| +3 | 61.7 % | 0.8 / 60.9 | 43 | 0 | 0 ms | - | - | - | 247 ms | 0 ms | 0 |
| +6 | 65.2 % | 1.3 / 63.9 | 45 | 0 | 0 ms | - | - | - | 54 ms | 0 ms | 0 |
| +10 | 68.5 % | 2.4 / 66.1 | 46 | 7 | 2409 ms | +805 ms | 805 ms | -29.0 dB | 2176 ms | 0 ms | 0 |
| +15 | 94.5 % | 41.9 / 52.5 | 48 | 33 | 12845 ms | -252 ms | 3705 ms | -6.0 dB | 406 ms | 0 ms | 4 |
| +20 | 214.7 % | 169.5 / 45.2 | 48 | 9 | 24753 ms | -591 ms | 16927 ms | -4.3 dB | 0 ms | 0 ms | 7 |
| +6, mic moves | 61.8 % | 1.0 / 60.9 | 45 | 0 | 0 ms | - | - | - | 120 ms | 0 ms | 0 |
| slow push (held to +14.4 dB, +13.6 to +15.0 over five runs) | 96.4 % | 39.8 / 56.6 | 47 | 8 | 11564 ms | -536 ms | 4383 ms | -8.9 dB | 92 ms | 0 ms | 7 |

## Rulers

Which test code measured which build: `11fe355` by `1416d5b`; `11fe355` by `f81455d`; `294bcc8` by `1416d5b`; `294bcc8` by `f81455d`; `4b1c88c` by `1416d5b`; `4b1c88c` by `f81455d`; `69f6ef0` by `1416d5b`; `69f6ef0` by `f81455d`; `b71ce6f` by `1416d5b`; `d55f580` by `1416d5b`; `d55f580` by `f81455d`; `f5c5f0b` by `1416d5b`; `f5c5f0b` by `f81455d`; `fce3790` by `1416d5b`; `fce3790` by `f81455d`.
