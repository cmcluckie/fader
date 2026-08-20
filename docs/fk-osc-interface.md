# FeedbackKiller engine ↔ tray OSC interface

Loopback UDP, OSC 1.0, `127.0.0.1` only. All traffic is control-rate except `/fk/spectrum`,
which is a downsampled binary blob.

- **Engine listen port:** `10024` — app → engine control.
- **App telemetry port:** `10025` — engine → app. Fixed rather than reply-to-source, because
  JUCE's `OSCReceiver` does not surface the sender's address; a fixed loopback port is simplest
  for a single-user tray app. (X32 is `10023`; these sit adjacent by design.)
- **Channels** are `0 = LEAD` (X32 ch 3), `1 = BGV` (X32 ch 4). Two channels, fixed.
- **Slots** are `0..11` (12 notches per channel, per `kMaxNotches`).
- Types follow OSC: `i` int32, `f` float32, `s` string, `b` blob.

## App → engine

| Address | Args | Meaning |
|---|---|---|
| `/fk/mode` | `i` | 0 = off (analyse + display, audio untouched), 1 = assist (detect + log, no cut), 2 = auto (deploy notches). **Default assist.** |
| `/fk/notch/place` | `i f f` | channel, hz, depthDb (negative) — place a manual, locked notch |
| `/fk/notch/remove` | `i i` | channel, slot |
| `/fk/notch/lock` | `i i i` | channel, slot, locked (0/1) |
| `/fk/clear` | `i i` | channel, includeLocked (0/1) |
| `/fk/lockall` | `i` | channel — lock every currently active notch (end of a ring-out pass) |
| `/fk/param` | `s f` | set one scalar: `maxCutDb`, `notchQ`, `releaseSeconds`, `prominenceDb`, `persistFrames`, `pitchTolerance`, `harmonicDb`, `floorDb` |
| `/fk/audio` | `s i i i` | device name, sampleRate, bufferSize, (reserved) — select/confirm the Core Audio device + I/O pair |
| `/fk/subscribe` | `i` | telemetry mask: bit0 events, bit1 notches, bit2 spectrum, bit3 status. Renew < 5 s or streams stop. |
| `/fk/ping` | — | health check; engine answers `/fk/status` immediately |

## Engine → app

| Address | Args | Meaning | Rate |
|---|---|---|---|
| `/fk/event` | `i f f` | channel, hz, levelDb — a detection fired (logged in assist, notched in auto) | on detect |
| `/fk/notches` | `i b` | channel, packed slot state (see below) | ~10 Hz |
| `/fk/spectrum` | `i b` | channel, packed magnitude frame, downsampled to display bins | ~20 Hz |
| `/fk/status` | `i f` | engineOk (0/1), cpuLoad (0..1) | ~2 Hz + on ping |
| `/fk/audio/state` | `s i i i` | device, sampleRate, bufferSize, running(0/1) | on change |

## Blob layouts

**`/fk/notches` packed slot state** — 12 records, little-endian:

```
per slot (16 bytes): u8 active, u8 locked, u8 manual, u8 pad,
                     f32 freqHz, f32 currentDb, f32 targetDb
```

**`/fk/spectrum` frame** — downsampled in the engine, never ship all 1024 bins:

```
u16 binCount   (e.g. 256)
f32 hzPerBin   (display resolution, not FFT resolution)
f32[binCount]  magnitudes in dBFS
```

## Notes

- **Renewal.** `/fk/subscribe` (or any `/fk/ping`) must arrive < 5 s apart while the app wants
  telemetry, mirroring the X32's `/xremote` discipline — otherwise the engine stops publishing
  to save loopback traffic. This is deliberately the same mental model as the console.
- **Idempotency.** `/fk/mode`, `/fk/param`, `/fk/audio` are level-triggered (set-to-value), so a
  dropped UDP packet self-heals on the next send. Notch edits are edge-triggered and echoed back
  in the next `/fk/notches` frame so the app can reconcile.
- **Persistence lives in C#.** The engine holds no config across restarts. On launch the app
  replays locked notches via `/fk/notch/place` then `/fk/notch/lock`, and pushes `/fk/mode`,
  `/fk/param`, `/fk/audio`. This keeps locked filters surviving a restart even if the engine was
  the thing that died.
- **Loopback only.** The engine binds `127.0.0.1`; it never listens on the LAN, so this control
  surface is not reachable from the X32's network.
