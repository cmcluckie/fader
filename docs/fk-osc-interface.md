# Feedback Fader: engine ↔ app OSC interface

Loopback UDP, OSC 1.0, `127.0.0.1` only. Checked against
`engine/Source/EngineMain.cpp` on 2026-10-04; if this page and the code
disagree, the code is right and this page is a bug.

- **Engine listens on `10024`** - app → engine control.
- **App listens on `10025`** - engine → app telemetry. Fixed rather than
  reply-to-source, because JUCE's `OSCReceiver` does not surface the sender's
  address. (The X32 is `10023`; these sit next to it by design.)
- **Channels** are slots `0..7`: the i-th armed input, in ascending order of
  physical input. Up to eight.
- **Filters** are slots `0..47` per channel (`kMaxNotches`).
- Types follow OSC: `i` int32, `f` float32, `s` string, `b` blob.

## App → engine

| Address | Args | Meaning |
|---|---|---|
| `/fk/audio` | `s i i` | device name, sample rate, buffer size. The app always sends 48000 and 64. |
| `/fk/inputs` | `i ...` | the physical input channels to guard (0-based), up to eight. Sorted by the engine; slot i is the i-th of them. |
| `/fk/outputs` | `i ...` | where each slot returns, parallel to the sorted inputs. Empty: each slot returns on its own input's index. |
| `/fk/listdevices` | - | send the device and channel lists again |
| `/fk/bypass` | `i` | 1: audio passes untouched, analysis stops, filters fade out |
| `/fk/analysis` | `i` | 1: keep detecting while bypassed (what Capture uses) - detects and logs, places nothing |
| `/fk/param` | `s f` | set one value by name - see below |
| `/fk/notch/place` | `i f f` | channel, Hz, depth dB (negative): a manual, locked filter at the default width |
| `/fk/notch/remove` | `i i` | channel, slot |
| `/fk/notch/lock` | `i i i` | channel, slot, locked (0/1) |
| `/fk/lockall` | `i` | channel: lock every active filter (the end of a ring-out) |
| `/fk/clear` | `i i` | channel, include locked (0/1) |
| `/fk/record` | `s` | a path starts the flight recorder (24-bit stereo: before the filters, after them; slot 0); an empty string stops it |
| `/fk/testtone` | `f` | above 0: a sine at that many Hz on every active return, replacing the audio. Below 0: silence on every active return. 0: off. Soundcheck only. |
| `/fk/subscribe` | `i` | telemetry mask: bit 0 rejections, bit 1 filters, bit 2 spectrum, bit 3 periodic status. Stored until changed - it does not expire. |
| `/fk/ping` | - | answered with `/fk/status` at once |
| `/fk/quit` | - | close the audio device and exit. The only clean stop on Windows. |

**`/fk/param` names**

| Name | What it sets | The app sends it? |
|---|---|---|
| `maxCutDb` | the dial: the deepest ordinary cut | yes (Max cut) |
| `initialCut` | the first cut | yes (Attack) |
| `persistFrames` | frames a line must persist before it is called | yes (Attack) |
| `minFreq`, `maxFreq` | the listen band | yes |
| `floorDb` | quieter than this is ignored | yes |
| `harmBudget` | ceiling on the total ear-weighted cut; 0 = none | yes (Voice budget) |
| `notchQ` | default filter width | no - engine default 12 |
| `prominenceDb` | how far a line must stand above its neighbours | no - 12 |
| `stabilityHz` | how far a line may wander | no - 5 |
| `growthDb` | required growth per 100 ms | no - 3 |
| `inputGate` | below this the detector does not look | no - -90 |
| `releaseSeconds` | filter release | no |
| `rescue` | above 0.5: the rescue duck is on | no - on |

Everything else in the detector and the filter bank is fixed at build time.

## Engine → app

| Address | Args | Meaning | When |
|---|---|---|---|
| `/fk/status` | `i f` | audio device running (0/1), CPU load 0..1 | about 2 Hz with bit 3, and on ping |
| `/fk/audio/state` | `s i i i` | device, sample rate, buffer size, running | on change |
| `/fk/device` | `s` | one input-capable device name | after `/fk/listdevices` |
| `/fk/channel` | `i s` | input index, name | with the device list |
| `/fk/outchannel` | `i s` | output index, name | with the device list |
| `/fk/event` | `i f f f f f i i` | channel, Hz, level dB, age ms (how long it was watched), width low Hz, width high Hz, path, refused | on every detection, whatever the mask |
| `/fk/reject` | `i f f i i` | channel, Hz, level dB, reason, frames | when a candidate is declined, with bit 0 |
| `/fk/notches` | `i b` | channel, packed filter state | about 10 Hz with bit 1 |
| `/fk/spectrum` | `i b` | channel, packed magnitudes, before the filters | about 20 Hz with bit 2 |
| `/fk/context` | `f f i f` | level dB, fundamental Hz, harmonic families, flatness - slot 0 only | about 5 Hz, always |
| `/fk/track` | `i i f f f i f` | channel, index, Hz, low Hz, high Hz, hops, heat (0 still, 1 walking) - slots 0 and 1 only; every index is sent, empty ones as zeros | about 5 Hz, always |
| `/fk/notinloop` | `i` | filters that have concluded they are not in the loop; 0 = fine. Latched 30 s. | about 2 Hz, always |
| `/fk/rescue` | `i f f f i` | channel, duck depth dB, Hz, level dB, why (1 runaway, 2 loud line) | when the rescue duck goes down or deeper |

**`path`** in `/fk/event`: 1 growth, 2 sustain, 3 escalation, 4 plateau (ships switched off), 5 runaway.
**`refused`**: 0 the bank acted; 1 already covered by a filter; 2 refused by the voice budget; 3 every filter is locked.
**`reason`** in `/fk/reject`: 1 harmonic, 2 unstable, 3 no growth, 4 vibrato, 5 drifting.

## Blob layouts

**`/fk/notches`** - 48 records, little-endian, 16 bytes each:

```
u8 active, u8 locked, u8 manual, u8 pad, f32 freqHz, f32 currentDb, f32 targetDb
```

The filter's width is not sent, so the app cannot draw the exact response (plan 5.8).

**`/fk/spectrum`**

```
u16 binCount      256
f32 hzPerBin      display resolution: 1024 analysis bins max-pooled to 256
f32[binCount]     dBFS
```

## Notes

- **Level-triggered.** `/fk/param`, `/fk/audio`, `/fk/inputs`, `/fk/outputs` and `/fk/bypass` set a
  value; the app sends all of them again on every engine start, so a lost packet heals.
  Filter edits are edge-triggered and show up in the next `/fk/notches`.
- **Nothing persists in the engine.** The app keeps the settings (`audio.json`) and the locked
  filters (`notches.json`) and replays them with `/fk/notch/place`, which places each one locked.
- **Loopback only.** The engine binds `127.0.0.1` and is not reachable from the console's network.
- **There are no modes.** OFF / ASSIST / AUTO were retired in August. What exists: the engine on or
  off, a channel armed or not, the guard bypassed or not, and Capture (detect and log while bypassed).
- **Not wired yet:** the loop probe and pinned filters (`/fk/probe`, `/fk/pin`, `/fk/unpin` - plan 4.3,
  4.4) and per-stage switches (plan 6.2).
