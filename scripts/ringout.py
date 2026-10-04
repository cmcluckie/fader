#!/usr/bin/env python3
"""
Ring the room out, by the numbers.

Raises one X32 fader in small steps until the room sings, watches what the guard
does about it through the engine's own flight recorder, backs the fader off, and
keeps going until the guard can no longer hold the room. Run once with the guard
bypassed to find where the room rings on its own (G0), then with the guard on to
find where it loses (G1); the difference is the added stable gain, the one figure
of merit this project had never measured.

It is also the prototype of "auto mode for a new room": the same loop with the
engine's own trim in place of a desk fader.

    ringout.py --mode baseline            # guard off: find the ring point, quietly
    ringout.py --mode guard               # guard on: climb until it loses
    ringout.py --mode guard --dry-run     # everything but the fader moves

Safety, in order:
  - only ONE fader moves (--fader, default /ch/09), main is parked at --main-db
  - a hard ceiling on that fader (--max-db)
  - a kill switch on the microphone: any bin at or above --kill-db, or at or
    above --loud-db for --loud-s seconds, drops the fader to --safe-db at once,
    guard or no guard
  - Ctrl-C, or any exception, restores both faders, stops the recorder and
    takes the guard out of bypass
  - the recorder is always stopped: a left-running recorder once wrote 50 GB

Everything is logged to ~/Documents/FeedbackKiller/ringout/<stamp>/ : the sweep
recording, a trials CSV, and the fader values restored at the end.
"""
import argparse, csv, datetime, json, os, signal, socket, struct, sys, time
import numpy as np

X32_ADDR = ("192.168.9.113", 10023)
ENGINE = ("127.0.0.1", 10024)
LOGS = os.path.expanduser("~/Documents/FeedbackKiller/logs")
OUT = os.path.expanduser("~/Documents/FeedbackKiller/ringout")
SR = 48000


# ---- OSC, the minimum ------------------------------------------------------

def osc(addr, *args):
    b = addr.encode() + b"\x00"; b += b"\x00" * ((4 - len(b) % 4) % 4)
    tags = "," + "".join("f" if isinstance(a, float) else "i" if isinstance(a, int) else "s" for a in args)
    t = tags.encode() + b"\x00"; t += b"\x00" * ((4 - len(t) % 4) % 4)
    p = b""
    for a in args:
        if isinstance(a, float): p += struct.pack(">f", a)
        elif isinstance(a, int): p += struct.pack(">i", a)
        else:
            q = a.encode() + b"\x00"; q += b"\x00" * ((4 - len(q) % 4) % 4); p += q
    return b + t + p


def parse(d):
    i = d.index(b"\x00"); addr = d[:i].decode(); i = (i + 4) & ~3
    j = d.index(b"\x00", i); tags = d[i + 1:j].decode(); i = (j + 4) & ~3; out = []
    for t in tags:
        if t == "f": out.append(struct.unpack(">f", d[i:i + 4])[0]); i += 4
        elif t == "i": out.append(struct.unpack(">i", d[i:i + 4])[0]); i += 4
        elif t == "s": k = d.index(b"\x00", i); out.append(d[i:k].decode()); i = (k + 4) & ~3
    return addr, out


class X32:
    """Read and write fader positions. The X32 answers a bare address with its value."""
    def __init__(self, dry):
        self.s = socket.socket(socket.AF_INET, socket.SOCK_DGRAM); self.s.settimeout(1.5); self.dry = dry

    def get(self, addr):
        for _ in range(3):
            try:
                self.s.sendto(osc(addr), X32_ADDR); a, v = parse(self.s.recvfrom(1024)[0])
                if a == addr: return v[0]
            except socket.timeout:
                pass
        raise RuntimeError(f"X32 did not answer {addr}")

    def set(self, addr, value):
        if self.dry:
            print(f"      [dry] {addr} <- {value:.4f} ({fader_db(value):+.1f} dB)"); return
        self.s.sendto(osc(addr, float(value)), X32_ADDR)
        # read back: a fader that did not move is a fault, not a detail
        for _ in range(5):
            time.sleep(0.05)
            if abs(self.get(addr) - value) < 0.002: return
        raise RuntimeError(f"{addr} did not take {value:.4f}")


def fader_db(f):
    if f >= 0.5: return f * 40 - 30
    if f >= 0.25: return f * 80 - 50
    if f >= 0.0625: return f * 160 - 70
    return f * 480 - 90


def fader_pos(db):
    if db >= -10: return (db + 30) / 40
    if db >= -30: return (db + 50) / 80
    if db >= -60: return (db + 70) / 160
    return max(0.0, (db + 90) / 480)


class Engine:
    def __init__(self):
        self.s = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)

    def send(self, addr, *args): self.s.sendto(osc(addr, *args), ENGINE)
    def record(self, path): self.send("/fk/record", path)
    def stop_recording(self): self.send("/fk/record", "")
    def bypass(self, on): self.send("/fk/bypass", 1 if on else 0)
    def analysis(self, on): self.send("/fk/analysis", 1 if on else 0)   # keep detecting while bypassed


# ---- the meter: read the engine's own recording as it grows ----------------

class Meter:
    """Tail the two-channel 24-bit flight recording; report the pre-notch mic."""
    N = 4096

    def __init__(self, path):
        self.path = path; self.data_off = None; self.w = np.hanning(self.N)
        self.f = np.fft.rfftfreq(self.N, 1.0 / SR); self.last = None

    def _header(self):
        with open(self.path, "rb") as fh:
            d = fh.read(4096)
        i = d.find(b"data")
        if i < 0: return False
        self.data_off = i + 8; return True

    def read(self, seconds=0.1):
        """Spectrum (dB, full-scale sine = 0) and RMS of the newest `seconds` of the pre channel."""
        if self.data_off is None and not self._header(): return None
        frame = 2 * 3
        need = int(seconds * SR); need = max(need, self.N)
        size = os.path.getsize(self.path)
        avail = (size - self.data_off) // frame
        if avail < need: return None
        with open(self.path, "rb") as fh:
            fh.seek(self.data_off + (avail - need) * frame); raw = fh.read(need * frame)
        a = np.frombuffer(raw, dtype=np.uint8).reshape(-1, frame)
        pre = (a[:, 0].astype(np.int32) | (a[:, 1].astype(np.int32) << 8) | (a[:, 2].astype(np.int32) << 16))
        pre = np.where(pre >= 1 << 23, pre - (1 << 24), pre).astype(np.float32) / (1 << 23)
        rms = 20 * np.log10(np.sqrt(np.mean(pre ** 2)) + 1e-12)
        seg = pre[-self.N:] * self.w
        spec = 20 * np.log10(2 * np.abs(np.fft.rfft(seg)) / self.w.sum() + 1e-12)
        return spec, rms

    def at(self, spec, hz):
        b = int(round(hz / self.f[1])); return float(spec[max(0, b - 1):b + 2].max())

    def peak(self, spec, lo_hz=200.0):
        lo = np.searchsorted(self.f, lo_hz)
        b = lo + int(np.argmax(spec[lo:]))
        span = int(500 / self.f[1])
        hood = np.concatenate([spec[max(lo, b - span):max(lo, b - 3)], spec[b + 4:b + span]])
        prom = spec[b] - (np.median(hood) if len(hood) else -120)
        return float(self.f[b]), float(spec[b]), float(prom)


def tail_events(path, since_pos):
    """New rows of the engine's feedback log since the last read."""
    rows = []
    try:
        with open(path) as fh:
            fh.seek(since_pos); chunk = fh.read(); since_pos = fh.tell()
        for line in chunk.splitlines():
            p = line.split(",")
            if len(p) >= 4 and p[0] != "seconds":
                try: rows.append((float(p[0]), p[1], float(p[2]), float(p[3])))
                except ValueError: pass
    except OSError:
        pass
    return rows, since_pos


def glob_mtime(pattern):
    import glob
    return [(os.path.getmtime(f), f) for f in glob.glob(pattern)]


def newest(prefix):
    fs = sorted((os.path.getmtime(os.path.join(LOGS, f)), f) for f in os.listdir(LOGS) if f.startswith(prefix))
    return os.path.join(LOGS, fs[-1][1]) if fs else None


# ---- the sweep -------------------------------------------------------------

def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--mode", choices=["baseline", "guard"], required=True)
    ap.add_argument("--fader", default="/ch/09/mix/fader")
    ap.add_argument("--main", default="/main/st/mix/fader")
    ap.add_argument("--main-db", type=float, default=0.0, help="park the main fader here for the sweep")
    ap.add_argument("--start-db", type=float, default=-20.0)
    ap.add_argument("--max-db", type=float, default=10.0, help="hard ceiling on the swept fader")
    ap.add_argument("--step-db", type=float, default=0.5)
    ap.add_argument("--hold-s", type=float, default=2.5, help="dwell per step")
    ap.add_argument("--safe-db", type=float, default=-30.0, help="where the fader goes on a kill")
    ap.add_argument("--kill-db", type=float, default=-30.0, help="any bin this loud: kill at once")
    ap.add_argument("--loud-db", type=float, default=-40.0, help="a bin this loud for --loud-s: kill")
    ap.add_argument("--loud-s", type=float, default=1.5)
    ap.add_argument("--onset-db", type=float, default=-70.0, help="a detector event this loud starts a ring candidate")
    ap.add_argument("--rings", type=int, default=12, help="guard mode: stop after this many rings")
    ap.add_argument("--settle-s", type=float, default=3.0)
    ap.add_argument("--dry-run", action="store_true")
    ap.add_argument("--use-capture", action="store_true",
                    help="the app's Capture is on: meter from its recording and leave its recorder alone")
    a = ap.parse_args()

    stamp = datetime.datetime.now().strftime("%Y%m%d-%H%M%S")
    out = os.path.join(OUT, f"{stamp}-{a.mode}"); os.makedirs(out, exist_ok=True)
    x32 = X32(a.dry_run); eng = Engine()
    try:
        print("   engine: " + open(os.path.expanduser("~/Documents/FeedbackKiller/engine-build.txt")).read().replace("\n", "; ").strip("; "))
    except OSError:
        print("   engine: build unknown (no engine-build.txt - an engine older than the stamp)")

    # where everything was, written down before anything moves
    was = {a.fader: x32.get(a.fader), a.main: x32.get(a.main)}
    json.dump(was, open(os.path.join(out, "faders-before.json"), "w"), indent=1)
    print(f"{a.mode} sweep -> {out}")
    print(f"   {a.fader} {was[a.fader]:.4f} ({fader_db(was[a.fader]):+.1f} dB), {a.main} {was[a.main]:.4f} ({fader_db(was[a.main]):+.1f} dB)")
    if a.dry_run: print("   DRY RUN: no fader will move")

    rec = os.path.join(out, "sweep.wav")
    if a.use_capture:
        # The app's Capture mode records to logs/audio-<stamp>.wav and writes the
        # capture log (refusal reason, nearest filter, cut here) per detection -
        # the one record of WHY the bank did what it did. Meter from that file.
        cands = sorted(glob_mtime(os.path.join(LOGS, "audio-*.wav")))
        if not cands: sys.exit("no capture recording in logs - turn Capture on in Setup first")
        rec = cands[-1][1]; size0 = os.path.getsize(rec); time.sleep(1.0)
        if os.path.getsize(rec) == size0: sys.exit(f"{os.path.basename(rec)} is not growing - is Capture on?")
        print(f"   metering the app's capture recording {os.path.basename(rec)}")
    trials = open(os.path.join(out, "trials.csv"), "w", newline="")
    tw = csv.writer(trials); tw.writerow(["t", "fader_db", "event", "hz", "level_db", "prom_db", "rms_db", "note"])
    t0 = time.time()
    ev_path = newest("feedback-log-"); ev_pos = os.path.getsize(ev_path) if ev_path else 0

    def log(event, hz=0.0, lvl=0.0, prom=0.0, rms=0.0, note="", db=None):
        tw.writerow([f"{time.time()-t0:.2f}", f"{db if db is not None else cur_db:.1f}", event, f"{hz:.0f}", f"{lvl:.1f}", f"{prom:.1f}", f"{rms:.1f}", note]); trials.flush()

    cur_db = a.start_db
    killed = False
    first = True

    def restore():
        if not a.use_capture: eng.stop_recording(); eng.analysis(False)
        eng.bypass(False)
        for addr, v in was.items():
            try: x32.set(addr, v)
            except Exception as e: print(f"   RESTORE FAILED {addr}: {e} - set it by hand to {v:.4f}")
        print(f"   restored: {a.fader} {fader_db(was[a.fader]):+.1f} dB, {a.main} {fader_db(was[a.main]):+.1f} dB; recorder stopped; guard on")

    def on_signal(*_): raise KeyboardInterrupt
    signal.signal(signal.SIGINT, on_signal); signal.signal(signal.SIGTERM, on_signal)

    try:
        if not a.use_capture: eng.record(rec); time.sleep(0.5)
        if a.mode == "baseline" and not a.dry_run:
            if not a.use_capture: eng.analysis(True)
            eng.bypass(True); print("   guard BYPASSED (detector still watching)")
        elif a.mode == "baseline": print("   [dry] guard would be bypassed")
        else: eng.bypass(False)
        x32.set(a.fader, fader_pos(a.start_db)); x32.set(a.main, fader_pos(a.main_db))
        meter = Meter(rec); time.sleep(1.0)
        rings = []; loud_since = None; prev_peaks = []
        print(f"\n{'t':>6} {'fader':>6} {'peak Hz':>8} {'dB':>6} {'prom':>5} {'rms':>6}  note")
        while True:
            # one step up
            if first: first = False
            else: cur_db = min(a.max_db, cur_db + a.step_db)
            x32.set(a.fader, fader_pos(cur_db)); log("step", db=cur_db)
            step_end = time.time() + a.hold_s; ring_here = None; candidate = None
            while time.time() < step_end:
                time.sleep(0.1)
                m = meter.read(0.1)
                if m is None: continue
                spec, rms = m; hz, lvl, prom = meter.peak(spec)
                # --- kill switch, before anything else
                if lvl >= a.kill_db or rms >= a.kill_db - 3:
                    log("KILL", hz, lvl, prom, rms, "instant"); killed = True
                if lvl >= a.loud_db:
                    loud_since = loud_since or time.time()
                    if time.time() - loud_since >= a.loud_s: log("KILL", hz, lvl, prom, rms, f"{a.loud_s}s loud"); killed = True
                else: loud_since = None
                if killed:
                    x32.set(a.fader, fader_pos(a.safe_db)); cur_db = a.safe_db
                    print(f"{time.time()-t0:6.1f} {cur_db:+6.1f} {hz:8.0f} {lvl:6.1f} {prom:5.1f} {rms:6.1f}  KILL -> fader to {a.safe_db:+.0f} dB")
                    break
                # --- the ring signal is the DETECTOR's. The meter alone called a
                # voice harmonic at 305 Hz a ring on the first live run; the
                # detector knows a voice from a ring, so it starts the candidate
                # and the meter only confirms that the tone is still rising.
                if ev_path:
                    rows, ev_pos = tail_events(ev_path, ev_pos)
                    for (ts, ch, hz_, lv_) in rows:
                        log("detector", hz_, lv_, 0, rms, ch)
                        if candidate is None and ring_here is None and lv_ >= a.onset_db:
                            candidate = (hz_, meter.at(spec, hz_), time.time())
                            print(f"{time.time()-t0:6.1f} {cur_db:+6.1f} {hz_:8.0f} {lv_:6.1f} {'':>5} {rms:6.1f}  detector: candidate")
                if candidate is not None:
                    chz, c0, ct = candidate; now_lvl = meter.at(spec, chz)
                    if (now_lvl >= c0 + 2.0 and now_lvl >= -65.0) or now_lvl >= -50.0:
                        ring_here = (chz, now_lvl, time.time()); candidate = None
                        log("ring", chz, now_lvl, prom, rms, "confirmed"); rings.append(dict(db=cur_db, hz=chz, onset=now_lvl, peak=now_lvl, t=time.time()))
                        print(f"{time.time()-t0:6.1f} {cur_db:+6.1f} {chz:8.0f} {now_lvl:6.1f} {prom:5.1f} {rms:6.1f}  RING #{len(rings)} confirmed")
                        if a.mode == "baseline":
                            # that is the number; get out before it is loud
                            x32.set(a.fader, fader_pos(a.safe_db)); log("backoff", db=a.safe_db); cur_db = a.safe_db
                            break
                    elif time.time() - ct > 0.6:
                        log("unconfirmed", chz, now_lvl, prom, rms, f"was {c0:.1f}"); candidate = None
                if ring_here is not None:
                    now_lvl = meter.at(spec, ring_here[0])
                    rings[-1]["peak"] = max(rings[-1]["peak"], now_lvl)
                    if now_lvl <= rings[-1]["peak"] - 10 and "fell10" not in rings[-1]:
                        rings[-1]["fell10"] = time.time() - ring_here[2]
                        print(f"{time.time()-t0:6.1f} {cur_db:+6.1f} {ring_here[0]:8.0f} {now_lvl:6.1f} {prom:5.1f} {rms:6.1f}  fell 10 dB from {rings[-1]['peak']:.1f} in {rings[-1]['fell10']:.2f} s")
            # detector's view, appended to the log
            if ev_path:
                rows, ev_pos = tail_events(ev_path, ev_pos)
                for (ts, ch, hz_, lv_) in rows: log("detector", hz_, lv_, 0, 0, ch)
            if killed:
                print(f"\n   the guard LOST at {rings[-1]['db'] if rings else cur_db:+.1f} dB on the fader" if a.mode == "guard" else "\n   killed")
                break
            if a.mode == "baseline" and rings:
                print(f"\n   the room rings on its own at {rings[-1]['db']:+.1f} dB on the fader ({rings[-1]['hz']:.0f} Hz)")
                break
            if a.mode == "guard" and ring_here is not None:
                # let the guard finish, then carry on up
                time.sleep(a.settle_s)
                if len(rings) >= a.rings: print(f"\n   {a.rings} rings mapped; stopping"); break
            if cur_db >= a.max_db and ring_here is None:
                print(f"\n   ceiling {a.max_db:+.1f} dB reached with no ring"); break
        summary = dict(mode=a.mode, dry_run=a.dry_run, rings=rings, killed=killed, fader=a.fader, main_db=a.main_db,
                       last_db=cur_db, stamp=stamp)
        json.dump(summary, open(os.path.join(out, "summary.json"), "w"), indent=1, default=float)
        if rings:
            print(f"\n{'#':>3} {'fader':>6} {'Hz':>7} {'onset':>6} {'peak':>6} {'fell10 s':>9}")
            for i, r in enumerate(rings, 1):
                print(f"{i:3d} {r['db']:+6.1f} {r['hz']:7.0f} {r['onset']:6.1f} {r['peak']:6.1f} {r.get('fell10', float('nan')):9.2f}")
    except KeyboardInterrupt:
        print("\n   interrupted")
    finally:
        restore(); trials.close()


if __name__ == "__main__":
    main()
