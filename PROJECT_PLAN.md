# Feedback Fader — Project Plan

Started 2026-10-04. From here this is one project: everything built so far, plus
what it takes to make it a product.

- **Statuses:** NEW · IN PROGRESS · DONE · CANCELLED.
- **Detail** (what was built, what is left, exit criteria, pass thresholds):
  [docs/TODO-AS-BUILT.md](docs/TODO-AS-BUILT.md).
- **Numbers** come from the standard tests, logged per version:
  [docs/RESULTS.md](docs/RESULTS.md).
- A feature proven only offline stays IN PROGRESS until it passes in the room.
- **Rule Zero (9 Oct 2026):** the rig is production. Nothing in this project
  changes a UAD Console or X32 setting, by any means. The app is read-only on
  the desk apart from one hold (7.8), and every Console or X32 change is
  Chris's, from [docs/RIG-CHECKLIST.md](docs/RIG-CHECKLIST.md), after a backup.

## The three numbers the product is judged on

| | Goal | Today (the build in the room, `d55f580`, simulated on the room's settings) |
|---|---|---|
| **Caught** | A real cut lands within **10 ms** of a ring becoming audible | Nothing is heard up to 6 dB past the room's limit. From 10 dB over, brief rings get through even though a cut is already on them; a reverberant room is lost by 15. |
| **Killed** | Inaudible again within **15 ms** of the catch | Not met. A ring that gets through lasts 60–700 ms at 6–10 dB over, and seconds beyond that. |
| **Sound** | Voice changed **under 3 %** when nothing rings, **under 15 %** while holding feedback | Not met, and the furthest from goal: **37 %** on the test singer and **65 %** on your recorded singing, with no feedback at all. |

"Audible" means louder than 20 dB under the singing voice. "Voice change %" is
how much of what the ear gets from the voice differs from the clean voice; a
1 dB level change is 8 %, 3 dB is 22 %.

---

## 1. Detection — IN PROGRESS

**Problem:** The guard must know a ring has started, and where, before anyone hears it.
**Goal:** A real cut lands within 10 ms of any ring becoming audible, up to 20 dB past the room's limit.

1. **Steady rings — DONE.** A narrow line that holds still and grows is called feedback. *Goal: every recorded howl caught before it is audible (22 of 22).*
2. **Fast risers — IN PROGRESS.** A ring that climbs faster than the normal test can follow is called within six frames. *Goal: caught within 10 ms of audible on a cold jump 20 dB over. Proven offline; live re-test owed.*
3. **Low rings — IN PROGRESS.** A second, longer analysis finds rings below 1 kHz. *Goal: no audible ring up to 10 dB over in a reverberant room (holds to about 6 today).*
4. **Voice or ring — IN PROGRESS.** Harmonic, vibrato and glide tests stop a sung note being called feedback. *Goal: no filter placed on a singer when nothing is ringing (31–42 filters today).*
5. **A ring hidden under a louder one — NEW.** *Goal: called before it reaches −50 dB (−32 today).*
6. **Other people's detectors — NEW.** Run the published detectors on our own recordings. *Goal: a side-by-side table, and we adopt anything that beats ours.*
7. **Wide, flat feedback — CANCELLED** *(proposed)*. Built in September; it also notched the singer and ships switched off.

## 2. Suppression — IN PROGRESS

**Problem:** Once found, a ring must be stopped fast with the smallest cut that holds it.
**Goal:** A ring that becomes audible is inaudible again within 15 ms of being caught.

1. **Notch ladder — DONE.** First cut, deeper each time the ring returns, emergency depth for runaways. *Goal: every recorded howl gets the dial's full depth on its own frequency (22 of 22).*
2. **Filters land on the ring — DONE.** A filter only takes over a ring it can actually reach, and follows one that drifts. *Goal: no ring left sitting beside its filter.*
3. **Rescue duck — IN PROGRESS.** A brief dip of the whole channel when a ring outruns the filters. *Goal: nothing louder than −45 dB on a cold jump 20 dB over, and never on a voice. Live re-test owed.*
4. **Letting go — NEW.** A filter relaxes to the shallowest depth that still holds, and leaves when its ring is gone. *Goal: filters held never exceed rings present by more than two.*
5. **One wide filter for a hump — NEW.** *Goal: the same hold with half the filters.*
6. **Rescue below 4 kHz — NEW.** *Goal: a low howl never passes −30 dB (it reaches −4 in the reverberant test today).*
7. **Knows when it is not in the loop — DONE.** Stops digging and raises an alarm when its cuts change nothing. *Goal: flagged within 2 s.*
8. **Pulsed filters — CANCELLED.** Measured much worse than steady ones.

## 3. Sound quality — IN PROGRESS

**Problem:** The voice should come through untouched when nothing is ringing, and lose only what stopping a ring needs.
**Goal:** Voice changed under 3 % with no feedback, and under 15 % while holding feedback.

1. **A number for sound quality — IN PROGRESS.** "Voice change %", measured against the clean voice. *Goal: reported for every version, simulated and live. Built today.*
2. **Leave the singer alone — NEW.** No cuts on singing when nothing is ringing. *Goal: under 3 % (37 % and 65 % today).*
3. **Did the cut work? — NEW.** Feedback gets quieter when cut and a voice does not, so a cut that changes nothing is taken back. *Goal: feature 2 met with no ring caught later than today.*
4. **Cost follows feedback — NEW.** Damage rises only as real rings appear. *Goal: under 5 % at the room's limit, 10 % at 6 dB over, 15 % at 10 dB over (48–65 % at all three today).*
5. **Cheaper cuts — NEW.** Narrower, shallower, and no "ghost note" left by a deep bass filter. *Goal: half the voice change for the same hold.*
6. **Cancellation — NEW.** Subtract the speaker-to-microphone path instead of carving the voice. *Goal: 6 dB more gain at under 5 % change, in the simulator first.*
7. **Voice budget — CANCELLED** *(proposed)*. A cap on total cut protects the voice by refusing real rings; feature 3 replaces it.

## 4. Room setup — IN PROGRESS

**Problem:** A new room should be set up by measurement, not by ear.
**Goal:** From start to safe-to-sing in under a minute with no audible ring, and the stated headroom within 1 dB of the truth.

1. **Ring-out in the app — DONE.** Push the monitors, lock what it finds. *Goal: locked filters are back in place after any restart.*
2. **Ring-out by fader sweep — DONE.** A script drives the desk, finds the ring point with and without the guard, and brackets cold jumps. *Goal: a repeatable gain-before-feedback figure.* Since 9 Oct it runs only with `--go`, which means you said go for that run.
3. **Measure the loop — IN PROGRESS.** A two-second sweep with the loop open shows what will ring and how much headroom is left. *Goal: first five rings predicted within 1 %. Proven in the simulator; the engine's probe is not built.*
4. **Pre-placed filters — IN PROGRESS.** Pin filters from the measurement before anything rings. *Goal: a cold start 9 dB over with no ring at all. Proven in the simulator; not connected to the app.*
5. **Headroom readout — NEW.** "6 dB before the first ring, at 9.5 kHz." *Goal: within 1 dB of the measured ring point.*
6. **Room memory — NEW.** Store the map per room and re-check it before trusting it. *Goal: a stale map is detected, not used.*
7. **One-button setup — NEW.** Measure, place, verify, park 3 dB under the limit. *Goal: under 60 s with no audible ring.*
8. **Desk-EQ ring-out — CANCELLED** *(proposed)*. Written in August and never connected to the app; features 3–7 replace it. The 9 Oct brief asks for it, so this one is your call; if it comes back, every cut is a click, never automatic.

## 5. Display — IN PROGRESS

**Problem:** The operator must see at a glance what is ringing and what the guard is doing about it.
**Goal:** From the Show screen alone, a user can name any ring's frequency within 2 s and see what the guard is costing the voice.

1. **Show screen — DONE.** Guard state, level, what it hears, channels, last catch, panic. *Goal: state readable at arm's length.*
2. **Where feedback is — IN PROGRESS.** Spectrum with filters marked; only the four deepest are labelled and a narrow high ring can be missing from the trace. *Goal: every active filter and the latest ring labelled with its frequency.*
3. **What it is costing — NEW.** Show the voice change % live. *Goal: within 3 points of the measured figure.*
4. **Alarms — IN PROGRESS.** Engine down and not-in-the-loop are shown; the rescue duck is not; the app now keeps its own log file. *Goal: every protective action visible within half a second.*
5. **Setup screen — DONE.** Device, channels, returns, listen band, signal-path check, ring-out, filter list. *Goal: a rig set up without editing a file.*
6. **One dial — NEW.** Hide or remove settings a normal user does not need. *Goal: guarding in three choices or fewer.*
7. **Stage switches — NEW.** One on/off and one readout per algorithm stage (epic 6). *Goal: any stage off in one tap.*
8. **Truthful picture — NEW.** The drawn EQ curve assumes the wrong filter width and the advanced panel shows stale values. *Goal: the curve within 1 dB of what the engine applies.*
9. **Desk analyser overlay — DONE.** The X32's own analyser drawn over the engine's. *Goal: a ring seen by both is marked.*

## 6. Engine architecture — NEW

**Problem:** Each algorithm should be a stage that can be switched, measured and replaced on its own.
**Goal:** Any stage can be turned off from the app or a test with no other change, and results can be reported per stage.

1. **Separate audio process — DONE.** The engine runs apart from the app and talks to it over the network. *Goal: the app can crash without touching the audio.*
2. **Stage interface — NEW.** One shape for every algorithm: process, on/off, settings, readout, cost. *Goal: a new algorithm is added without editing the others.*
3. **Fixed chain — NEW.** Room filters, canceller, detect-and-notch, rescue duck, in that order, in one pass. *Goal: no added delay.*
4. **Swappable detectors — NEW.** More than one detector can run on the same audio. *Goal: two detectors compared live on one screen.*

## 7. Signal path and reliability — IN PROGRESS

**Problem:** The guard has to be in the audio path, stay there, and fail safe.
**Goal:** One buffer of delay, a wiring fault flagged within 2 s, and a dead engine never means a dead microphone.

1. **Engine supervision — DONE.** The app starts, watches and restarts the engine. *Goal: exactly one engine, always, back within 6 s of a crash. Rebuilt 4 Oct after it was found relaunching the engine in a loop; now checked five ways. Your 6 Oct dead-man's switch (the engine quits when the app's pipe closes) and 30 s start-up grace merged 9 Oct, not yet run on the Mac rig.*
2. **Devices and channels — DONE.** Pick the interface, up to eight inputs, and where each returns. *Goal: remembered across restarts.*
3. **Signal-path check — DONE.** Asks the desk whether our audio actually arrives. *Goal: a wrong return found at soundcheck.*
4. **Build stamp — DONE.** The running engine says exactly which build it is. *Goal: no measurement filed under the wrong build again.*
5. **Dead-engine fallback — NEW.** Today a dead engine is a silent channel until someone unmutes a spare. *Goal: audio passes within 100 ms of the engine dying.* The desk bypass (8) is the by-hand version; an automatic swap would be a desk write without a click, so it is not built.
6. **Starts with the display asleep — NEW.** *Goal: starts unattended.*
7. **Windows — IN PROGRESS.** It builds with ASIO, but the build script stops on a test we keep failing on purpose. *Goal: one command produces a working Windows build.*
8. **One-click desk bypass — IN PROGRESS.** A one-second hold on Show swaps the guarded desk channels for their muted spares, and back; the only thing the app ever writes on the desk. *Goal: the vocal on the spare within a second of the hold, and never without one. Built 9 Oct and proven on a fake desk; waits for the rig checklist and your go.*
9. **Rig wiring — NEW (yours).** The Console and X32 changes in docs/RIG-CHECKLIST.md: the engine on ADAT 1/2, a bypass pair on free ADAT outputs into two spare channels. *Goal: the engine is the only path from Mic 1/2 to Ch 1/2, with a muted spare one hold away. Not before assist mode (11) has proved itself on the separate computer.*
10. **Round trip measured — NEW.** The delay through the Mac, Apollo in to X32 card in. *Goal: a measured figure in the README; the engine's own share stays one buffer (1.3 ms).*
11. **Assist mode — IN PROGRESS.** Return set to None: the detector listens and logs, and nothing is written to any output; a newly armed mic starts that way. *Goal: a rig can watch a rehearsal with the engine out of the path and the log says what would have been cut. Built 9 Oct; you run it on a separate computer first.*

## 8. Testing and measurement — IN PROGRESS

**Problem:** Every claim about feedback or sound must be a number, from a named version, that can be re-run.
**Goal:** One command gives a version's full results in under five minutes, and every version's results are logged.

1. **Unit tests — DONE.** 58 checks of the detector and filter bank. *Goal: all pass before any build ships.*
2. **Random-feedback test — DONE.** Thousands of generated rings, scored. *Goal: no more runaways than the last version.*
3. **Simulated-room gate — DONE.** The real guard inside a simulated room that can fight back. *Goal: no build ships that is worse in a loop.*
4. **Real-recording gate — DONE.** 22 recorded howls and 6 sung phrases replayed through the guard. *Goal: no build ships that is worse on real feedback.*
5. **Loop-measurement gate — DONE.** *Goal: the measured loop names the note that rings.*
6. **Singer in the loop — DONE.** A singer fed through the simulated room and the guard, scored on catch, kill and voice change at every gain. *Goal: part of the ship gate (it is layer 6).*
7. **Results log — IN PROGRESS.** Every result kept with its version, date, and whether it was simulated, recorded or live. *Goal: any version's numbers found in one place. Seven builds and 32 live runs are in; the live tools log themselves from the next session.*
8. **Live test tools — DONE.** Fader sweep and bracketed cold jumps. *Goal: a live number is never taken without a baseline either side.*
9. **Test voices — IN PROGRESS.** A synthetic singer in the repository, and your recorded singing kept on your machine. *Goal: add clean dry singing from the studio.*
10. **Second room — NEW.** Collect a studio session. *Goal: every number re-checked in a room the tests have never seen.*
11. **Listening check — IN PROGRESS.** You listen to the same phrase at several measured levels of change. *Goal: the number means what it says to your ears. Clips sent 4 Oct; waiting on you.*
12. **One machine — IN PROGRESS.** Every test runs exactly the settings the room runs. *Goal: no setting differs between the engine and any test. Found and fixed 4 Oct: the engine holds a filter 10 s, the tests held it 2 s. A check that catches the next one is still to build.*

## 9. Code health — IN PROGRESS

**Problem:** Only code that measurably earns its place should stay.
**Goal:** Every mechanism has a measured effect on record, or is gone.

1. **Inventory — DONE.** Every mechanism and setting in the app and the engine listed as live, switched off, unreachable or dead. *Goal: nothing unlisted.*
2. **On/off table — NEW.** Switch each unmeasured mechanism off and run the full tests. *Goal: a number beside each one.*
3. **Remove the dead — IN PROGRESS.** Gone on 4 Oct, with every test number unchanged: the unused probe, unused modes, debug prints, a filter rule that was measured out long ago. Still to do: a readout that always shows zero, room memory that is loaded and then erased, and whatever you cancel. *Goal: none left.*
4. **Documents match the code — IN PROGRESS.** The README and the interface document were stale in about fifteen places; both are corrected, stale comments in the source are not yet. *Goal: no claim the code contradicts.*
5. **Test tools in one place — NEW.** The 22 Python test tools folded under one command. *Goal: one entry point, and no file nobody runs. Two leftovers removed 4 Oct, one of which had been broken for days unnoticed.*
6. **FaderBridge removed — DONE.** Gone 9 Oct with its diagnostics, build script, assets and launch configs; the solution builds clean. *Goal: nothing of the bridge left but the git history.*
7. **Nothing moves a fader without you — DONE.** The two diagnostics that moved desk faders are gone; the sweep scripts refuse without `--go`; the README lists every write the repository can make. *Goal: no path from this repository to the desk that does not start with your go.*

## 10. Release — NEW

**Problem:** It has to install and run on someone else's machine.
**Goal:** A sound engineer installs it and is guarding in ten minutes without us.

1. **Mac app — DONE.** A working app for Apple-silicon Macs. *Goal: opens and guards on the home rig.*
2. **Installer — IN PROGRESS.** Your `.dmg` script and Windows setup `.exe` (13 Sep) are in since 9 Oct; still needs .NET installed and Intel Macs are not built. *Goal: one download, nothing else to install.*
3. **Signing — NEW.** *Goal: opens without a security warning on Mac and Windows.*
4. **Versions — NEW.** A visible version number and an About screen. *Goal: a user can tell us which build they have.*
5. **Licences — NEW.** Settle the terms of the audio framework and the ASIO driver code. *Goal: cleared before anyone else gets a copy.*
6. **User guide — NEW.** *Goal: setup without us on the phone.*

---

**Not in this plan:** FaderBridge (the FaderPort-to-X32 bridge). It was removed from the repository on 9 Oct 2026; the FaderPort talks to Logic natively now.

**Three items are marked CANCELLED as proposals** (1.7, 3.7, 4.8). Say the word and any of them goes back to NEW.
