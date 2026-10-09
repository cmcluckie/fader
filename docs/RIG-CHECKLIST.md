# Rig checklist: the changes only Chris makes

Rule Zero (README): the UAD Console session and the X32 scene are production
and locked. The software never writes to either. Everything the design needs
from them is here, as steps for Chris, in the order to do them. Where I am
going by the protocol tables or the Console manual rather than by something
seen on this rig, the step says so.

Nothing in this list is done yet (2026-10-09). The app's saved configuration on
the rig Mac is still the previous wiring (inputs 4/5, returns 10/11, no console
address), and the app itself has been stopped since 2026-10-07 20:54 while its
engine runs on alone.

## 0. Back up first

1. Console: **Session → Save As** a new name (keep the current one untouched).
2. X32: save the current scene to a **new slot** and export scenes to USB, so
   the old one is one recall away.
3. Mac: copy `~/Documents/FeedbackKiller/` somewhere else (notches, the audio
   config, the logs).
4. Write down, or photograph, Console's Flex Routing view and the X32 routing
   pages (Routing → Inputs, Card Out, User In).

## 1. Console: give ADAT 1/2 to the engine

Today ADAT out 1/2 carries Analog 1/2 by Flex Route. The engine's return is the
Mac's playback to ADAT 1/2. Both on the same outputs means the clean vocal and
the guarded vocal arrive at the desk a few milliseconds apart.

1. Flex Routing view: set **ADAT 1** and **ADAT 2** back to their default
   source (the Mac's playback; in Console that is removing the Flex Route on
   those two outputs). Going by the Console manual: the Flex Route source
   selector is per output.
2. Flex Routing view: set **ADAT 3 ← Analog 1** and **ADAT 4 ← Analog 2**. That
   is the bypass pair: the same mics, same UAD chain, straight to the desk.
3. On the Mic 1 and Mic 2 channels: **Insert Effects = REC** (not MON), so the
   Mac receives the vocal after the 610-B, 1176, LA-2A, Pultec and Helios.
   If it is MON today, say so in the report: the engine has been hearing the
   raw mic.
4. Leave Input Delay Compensation where it is (Medium-Long).
5. Nothing else in Console changes: not the Unison, not the plug-ins, not the
   Line 1/2 Flex Route for house music, not the ADAT inputs to Logic.
6. Save the session under the new name.

## 2. X32: the bypass channels

Ch 1 and Ch 2 are fed from the card (User In 129/130 = card in 1/2). Ch 11 and
Ch 12 are the spares.

1. Routing → User In: **card in 3 → the source for Ch 11**, **card in 4 → Ch 12**,
   the same way Ch 1/2 take card in 1/2. Going by the X32 User In numbering
   (card 1–32 = User In 129–160): card 3 = 131, card 4 = 132. Check on the desk.
2. Ch 11 / Ch 12 config: no headamp (the vocal is already processed), same
   fader as Ch 1/2, same EQ and dynamics only if Ch 1/2 have any you want on the
   bypass too.
3. Sends: copy Ch 1's sends to **Bus 1–6 (IEMs)**, **Bus 13 (reverb, FX1)** and
   **Bus 15 (slap, FX3)** onto Ch 11; Ch 2's onto Ch 12. Check the pre/post
   setting of each send matches.
4. **Mute Ch 11 and Ch 12.** They stay muted until the app swaps to them, or you
   do.
5. Bus 12 (guitar duck) is keyed from Ch 1. While on bypass nothing keys it.
   Decide: either re-key Bus 12 to Ch 11 by hand when you swap, or accept no
   ducking while bypassed. The app does not touch the key source either way.
6. Save the scene to the new slot again.

## 3. The app: Setup

Only after 1 and 2. If the room is live, do it with the mains down.

1. Start the app. The old one has been stopped since 10-07 and its engine is
   still running on its own; the app's supervisor ends any stray engine from
   the same binary before it launches its own (checked five ways on 10-04), so
   there is nothing to quit by hand.
2. Audio device: **Universal Audio Thunderbolt**.
3. Arm the two inputs the device lists as the Apollo's mic inputs 1 and 2.
   Name them Lead and BGV.
4. Return for each: the outputs the device lists as **ADAT 1** and **ADAT 2**.
5. Console (X32): `192.168.9.113`. Desk ch: `1 2`. Bypass ch: `11 12`.
6. Leave the detector settings as they were (40 Hz–18 kHz, floor −95, Attack
   fast, −18 dB) unless the plan says otherwise by then.

## 4. Prove it, with the mains down

1. Speak into the Lead mic. X32 Ch 1 meter moves; Ch 11 (muted) also meters.
   Ch 1 meters only while the engine is up: quit the app and Ch 1 goes silent,
   Ch 11 still meters. That is the whole point of the spare.
2. Setup → **Check signal path**: it plays a short tone on the returns and asks
   the desk's meters whether it arrived. It is heard if the desk is up.
3. Show → hold **Bypass to desk**: the ribbon should read "desk: BYPASSED. Ch
   11/12 open, Ch 1/2 muted". Look at the desk: Ch 11/12 unmuted, Ch 1/2 muted.
   Hold **Back to guard**: the reverse. Then look at the app log
   (`~/Documents/FeedbackKiller/logs/app-log-*.txt`): one line per write, each
   "read back as sent". If any line says DID NOT TAKE, stop and tell me.
4. Mains up, sing, and watch the Show screen with the guard on.

## 5. Going back

Recall the old Console session and the old X32 scene; put `audio.json` back
from the copy. The app can run with nothing armed and changes nothing.
