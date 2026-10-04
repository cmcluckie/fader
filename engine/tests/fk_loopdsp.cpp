/*
    fk_loopdsp — the shipping guard, as a C ABI, so a closed-loop room simulator
    can put it INSIDE the loop.

    Why this exists
    ---------------
    fk-fuzz generates rings and lets them grow. It has no phase condition, so it
    can never say WHICH frequency will ring; it has no amp, so a howl at 5 kHz
    cannot compress a mode at 77 Hz; and every mode grows independently of every
    other. Those are not details. They are the mechanisms that decide what the
    room actually does.

    feedback_sim.py has all three - a real image-source impulse response, mic and
    speaker directivity, a saturating power amp, and a sample-exact closed loop.
    What it does not have is our detector.

    Rendering its output through our guard offline would prove nothing: the loop
    has to be able to react to what we cut, or we are only suppressing a
    recording of feedback rather than preventing it. So the guard goes in the
    loop instead, as the EQ block the sim already has a slot for.

    This is deliberately the SAME six lines as the render harness - push, drain
    events into the bank, process, release. No reimplementation, no second
    policy to keep in sync. If it passes here it is the shipping code that
    passed.

    Build:  cmake --build build --target fk-loopdsp
    Use:    ctypes.CDLL(".../libfk-loopdsp.dylib")
*/

#include <juce_dsp/juce_dsp.h>
#include "../Source/FeedbackDetector.h"
#include "../Source/NotchBank.h"
#include "../Source/EngineDefaults.h"
#include "../Source/RescueDuck.h"

namespace
{
constexpr int kMaxNotches = 48;

struct EventRec  { float hz, levelDb; int growing, path; double t; int runaway; float depthDb; };
struct RejectRec { float hz, levelDb; int reason, frames; double t; float spreadHz, tolHz; };

struct Guard
{
    fk::FeedbackDetector        det;
    fk::NotchBank<kMaxNotches>  bank;
    fk::RescueDuck              duck;
    int                         duckSeen = 0;
    double sampleRate = 48000.0;
    double t          = 0.0;      // transport seconds, advanced by process()
    int    events     = 0;        // detections seen, cumulative

    // Everything the detector SAID, kept for the caller. A replay of a real
    // recording is worthless without the rejections: the question "why did it
    // not fire on that howl" has an answer inside the detector and nowhere
    // else, and the shipping app only writes those answers to disk when the
    // operator presses a button. Offline, the caller drains these.
    std::vector<EventRec>  evq;  size_t evRead = 0;
    std::vector<RejectRec> rjq;  size_t rjRead = 0;
};
}

extern "C"
{

/** The engine's own configuration, applied here the way AudioEngine applies it
    every block. Before this the library ran the detector's and the bank's
    built-in defaults, which are not what the engine runs, and the gate was
    passing a machine the rig had never seen. */
static void applyEngineDefaults (Guard& g)
{
    fk::FeedbackDetector::Params p;
    p.prominenceDb  = fk::defaults::prominenceDb;
    p.persistFrames = fk::defaults::persistFrames;
    p.floorDb       = fk::defaults::floorDb;
    p.minFreq       = fk::defaults::minFreq;
    p.maxFreq       = fk::defaults::maxFreq;
    p.stabilityHz   = fk::defaults::stabilityHz;
    p.growthDb      = fk::defaults::growthDb;
    p.inputGateDb   = fk::defaults::inputGateDb;
    g.det.setParams (p);
    g.bank.configure (fk::defaults::notchQ, fk::defaults::initialCutDb, fk::defaults::maxCutDb);
    // ...and the hold, which AudioEngine sets every block and this library never did.
    g.bank.holdSeconds = (double) fk::defaults::releaseSeconds;
}

/** Create a guard. Returns an opaque handle, or null. */
void* fk_create (double sampleRate, int maxBlock)
{
    auto* g = new (std::nothrow) Guard();
    if (g == nullptr) return nullptr;

    g->sampleRate = sampleRate;
    g->det.prepare (sampleRate);
    g->bank.prepare (sampleRate, maxBlock);
    g->duck.prepare (sampleRate);
    applyEngineDefaults (*g);
    return g;
}

/** What the app sends on top of the defaults: its Attack level (confirm frames
    and first strike), the max-cut dial, the frequency range and the floor. The
    harness reads these from the rig's audio.json so the gate runs the rig. */
void fk_configure (void* h, float notchQ, float initialCutDb, float maxCutDb, int persistFrames,
                   float minHz, float maxHz, float floorDb, float inputGateDb, float prominenceDb)
{
    auto* g = static_cast<Guard*> (h);
    if (g == nullptr) return;
    fk::FeedbackDetector::Params p;
    p.prominenceDb  = prominenceDb;
    p.persistFrames = persistFrames;
    p.floorDb       = floorDb;
    p.minFreq       = minHz;
    p.maxFreq       = maxHz;
    p.stabilityHz   = fk::defaults::stabilityHz;
    p.growthDb      = fk::defaults::growthDb;
    p.inputGateDb   = inputGateDb;
    g->det.setParams (p);
    g->bank.configure (notchQ, initialCutDb, maxCutDb);
    g->bank.holdSeconds = (double) fk::defaults::releaseSeconds;   // the app never sends releaseSeconds; the engine's own value stands
}

void fk_destroy (void* h) { delete static_cast<Guard*> (h); }

/** Reset to a clean guard without reallocating - between sim scenarios. */
void fk_reset (void* h)
{
    auto* g = static_cast<Guard*> (h);
    if (g == nullptr) return;
    g->bank.clear (true);
    g->duck.reset(); g->duck.triggers = g->duck.episodes = g->duck.futileEver = 0; g->duck.deepestDb = 0.0;
    g->duckSeen = 0;
    g->t = 0.0;
    g->events = 0;
}

/**
    One block, in place. This is the signal path from test_render.cpp verbatim:
    the detector sees the audio, every detection it reports becomes a trigger on
    the bank, the bank filters the block, and the release clock runs.

    The caller owns the loop. Call this where the console EQ sits - after the
    preamp, before the power amp - so what we cut actually changes the loop gain
    on the next pass.
*/
void fk_process (void* h, float* buf, int n)
{
    auto* g = static_cast<Guard*> (h);
    if (g == nullptr || buf == nullptr || n <= 0) return;

    g->det.push (buf, n);
    fk::rescueOnFrame (g->duck, g->det, g->duckSeen, g->t);

    fk::FeedbackDetector::Reject rj;
    while (g->det.popReject (rj))
        g->rjq.push_back ({ rj.freq, rj.levelDb, rj.reason, rj.frames, g->t, rj.spreadHz, rj.tolHz });

    fk::FeedbackDetector::Event ev;
    while (g->det.popEvent (ev))
    {
        const int slot = g->bank.trigger (ev.freq, g->t, ev.growing, ev.levelDb,
                         (double) (ev.widthHiHz - ev.widthLoHz), ev.path == 4, ev.runaway);
        const float depth = slot >= 0 ? (float) g->bank.getSlot (slot).targetDb : 0.0f;
        g->evq.push_back ({ ev.freq, ev.levelDb, ev.growing ? 1 : 0, ev.path, g->t, ev.runaway ? 1 : 0, depth });
        ++g->events;
        fk::rescueOnEvent (g->duck, ev, g->t);
    }

    g->bank.process (buf, n, false);
    g->duck.process (buf, n, g->t);
    g->bank.release (g->t);
    g->t += (double) n / g->sampleRate;
}

/**
    Place a filter by hand, before anything has rung.

    This is what makes the pre-cut strategy testable: the room simulator can
    compute its phase-aligned candidate ladder, hand us the frequencies, and we
    arm them at t=0. The guard then still runs reactively on top, so the two
    strategies can be compared with everything else held identical.
*/
int fk_place (void* h, float hz, float depthDb)
{
    auto* g = static_cast<Guard*> (h);
    if (g == nullptr) return -1;
    return g->bank.placeManual ((double) hz, (double) depthDb, g->t);
}

/** Pin a filter from a loop measurement: frequency, depth, width. Returns the
    slot, or -1. fk_unpin removes every pinned filter and nothing else. */
int fk_pin (void* h, float hz, float depthDb, float q)
{
    auto* g = static_cast<Guard*> (h);
    return g ? g->bank.placePinned ((double) hz, (double) depthDb, (double) q, g->t) : -1;
}

void fk_unpin (void* h)
{
    auto* g = static_cast<Guard*> (h);
    if (g) g->bank.clearPinned();
}

/** Drain one detection. Returns 1 if one was written, 0 when empty. */
int fk_pop_event (void* h, float* hz, float* levelDb, int* growing, int* path, double* t)
{
    auto* g = static_cast<Guard*> (h);
    if (g == nullptr || g->evRead >= g->evq.size()) { if (g) { g->evq.clear(); g->evRead = 0; } return 0; }
    const auto& e = g->evq[g->evRead++];
    if (hz) *hz = e.hz; if (levelDb) *levelDb = e.levelDb; if (growing) *growing = e.growing;
    if (path) *path = e.path; if (t) *t = e.t;
    return 1;
}

/** As fk_pop_event, plus what the bank did with it: whether the detector called
    it a runaway, and the depth of the filter it landed on (0 if refused). */
int fk_pop_event_ex (void* h, float* hz, float* levelDb, int* growing, int* path, double* t,
                     int* runaway, float* depthDb)
{
    auto* g = static_cast<Guard*> (h);
    if (g == nullptr || g->evRead >= g->evq.size()) { if (g) { g->evq.clear(); g->evRead = 0; } return 0; }
    const auto& e = g->evq[g->evRead++];
    if (hz) *hz = e.hz; if (levelDb) *levelDb = e.levelDb; if (growing) *growing = e.growing;
    if (path) *path = e.path; if (t) *t = e.t; if (runaway) *runaway = e.runaway; if (depthDb) *depthDb = e.depthDb;
    return 1;
}

/** Drain one rejection: a suspect that waited long enough and was declined. */
int fk_pop_reject (void* h, float* hz, float* levelDb, int* reason, int* frames, double* t,
                   float* spreadHz, float* tolHz)
{
    auto* g = static_cast<Guard*> (h);
    if (g == nullptr || g->rjRead >= g->rjq.size()) { if (g) { g->rjq.clear(); g->rjRead = 0; } return 0; }
    const auto& r = g->rjq[g->rjRead++];
    if (hz) *hz = r.hz; if (levelDb) *levelDb = r.levelDb; if (reason) *reason = r.reason;
    if (frames) *frames = r.frames; if (t) *t = r.t;
    if (spreadHz) *spreadHz = r.spreadHz; if (tolHz) *tolHz = r.tolHz;
    return 1;
}

/** Replays only: a recording cannot respond to a cut, so the not-in-the-loop
    verdict fires by construction and sawtooths every filter. Turn it off to
    measure holding. Never off live. */
void fk_set_loop_verdict (void* h, int on)
{
    auto* g = static_cast<Guard*> (h);
    if (g)
    {
        g->bank.loopVerdict = on != 0;
        // The rescue duck has the same verdict and the same problem: a recording
        // does not get quieter when ducked, so it concludes it is not in the loop
        // and latches itself off for half a minute - which under-counts what it
        // would have done to everything after its first episode.
        g->duck.futileS = on != 0 ? 1.0 : 1.0e9;
    }
}

/** The ear-weighted harm budget, as the app's "Voice budget" sets it. 0 = off. */
void fk_set_budget (void* h, float dbErb)
{
    auto* g = static_cast<Guard*> (h);
    if (g) g->bank.harmBudget = dbErb;
}

/** Filters that have concluded they are not in the loop. See NotchBank. */
int fk_not_in_loop (void* h)
{
    auto* g = static_cast<Guard*> (h);
    return g ? (g->bank.ineffectiveEver > 0 ? std::max (1, g->bank.ineffectiveNow()) : 0) : 0;
}

// ---- what the guard did, so the sim can score it ------------------------

int fk_event_count (void* h)
{
    auto* g = static_cast<Guard*> (h);
    return g ? g->events : 0;
}

/** Active filters, newest state. Returns how many were written (<= cap). */
int fk_notches (void* h, float* freqHz, float* depthDb, int cap)
{
    auto* g = static_cast<Guard*> (h);
    if (g == nullptr) return 0;

    int out = 0;
    for (int i = 0; i < kMaxNotches && out < cap; ++i)
    {
        const auto& s = g->bank.getSlot (i);
        if (! s.active) continue;
        if (freqHz  != nullptr) freqHz[out]  = (float) s.freq;
        if (depthDb != nullptr) depthDb[out] = (float) s.targetDb;
        ++out;
    }
    return out;
}

/** Everything about one active filter the merge rule can see: where it is, where
    it was anchored, how wide, how deep. Returns 1 if slot i is active. The audit
    that found a 9400 Hz ring "placed" onto a filter 380 Hz away needed exactly
    these four numbers and nothing exported them. */
int fk_notch_detail (void* h, int i, float* freqHz, float* originHz, float* q, float* depthDb, int* locked)
{
    auto* g = static_cast<Guard*> (h);
    if (g == nullptr || i < 0 || i >= kMaxNotches) return 0;
    const auto& s = g->bank.getSlot (i);
    if (! s.active) return 0;
    if (freqHz)   *freqHz   = (float) s.freq;
    if (originHz) *originHz = (float) s.originHz;
    if (q)        *q        = (float) s.q;
    if (depthDb)  *depthDb  = (float) s.targetDb;
    if (locked)   *locked   = s.locked ? 1 : 0;
    return 1;
}

/** The slot the bank would merge a trigger at f onto, or -1: the merge rule itself. */
int fk_find_near (void* h, float f)
{
    auto* g = static_cast<Guard*> (h);
    return g ? g->bank.findNear ((double) f) : -1;
}

/** The detector's suspects in a band, for "why has it not fired yet". Each row is
    freq, level, frames, flags (1 reported, 2 harmonic, 4 family, 8 missed-this-frame). */
int fk_suspects (void* h, float loHz, float hiHz, float* freq, float* level, int* frames, int* flags, int cap)
{
    auto* g = static_cast<Guard*> (h);
    if (g == nullptr) return 0;
    fk::FeedbackDetector::SuspectView v[16];
    const int n = g->det.debugSuspects (loHz, hiHz, v, std::min (cap, 16));
    for (int i = 0; i < n; ++i)
    {
        freq[i] = v[i].freq; level[i] = v[i].level; frames[i] = v[i].frames;
        flags[i] = (v[i].reported ? 1 : 0) | (v[i].harmonic ? 2 : 0) | (v[i].hasFamily ? 4 : 0) | (v[i].missed > 0 ? 8 : 0);
    }
    return n;
}

/** The rescue duck, for the gate: switch it, and read what it has done.
    out[0] triggers, [1] episodes, [2] deepest dB, [3] depth now dB, [4] last Hz,
    [5] last level dB, [6] last reason (1 runaway, 2 loud line), [7] futile verdicts. */
void fk_set_rescue (void* h, int on)
{
    auto* g = static_cast<Guard*> (h);
    if (g) { g->duck.enabled = on != 0; if (! on) g->duck.reset(); }
}

void fk_rescue (void* h, float* out8)
{
    auto* g = static_cast<Guard*> (h);
    if (g == nullptr || out8 == nullptr) return;
    out8[0] = (float) g->duck.triggers;   out8[1] = (float) g->duck.episodes;
    out8[2] = (float) g->duck.deepestDb;  out8[3] = (float) g->duck.depthDb();
    out8[4] = g->duck.lastHz;             out8[5] = g->duck.lastLevelDb;
    out8[6] = (float) (int) g->duck.lastReason; out8[7] = (float) g->duck.futileEver;
}

/** Total cut the guard is applying at f, dB (negative). The sim can subtract
    this from its own loop gain to confirm the two agree about the EQ. */
float fk_cut_at (void* h, float f)
{
    auto* g = static_cast<Guard*> (h);
    return g ? (float) g->bank.cutAtDb ((double) f, -1) : 0.0f;
}

/** The tracker: rings followed across their hops. heat 0 = sitting still. */
int fk_tracks (void* h, float* freqHz, float* loHz, float* hiHz, float* heat, int cap)
{
    auto* g = static_cast<Guard*> (h);
    if (g == nullptr) return 0;

    int out = 0;
    for (int i = 0; i < fk::FeedbackDetector::maxTracks && out < cap; ++i)
    {
        const auto& tr = g->det.trackAt (i);
        if (! tr.active || tr.freq <= 0.0f) continue;
        if (freqHz != nullptr) freqHz[out] = tr.freq;
        if (loHz   != nullptr) loHz[out]   = tr.loHz;
        if (hiHz   != nullptr) hiHz[out]   = tr.hiHz;
        if (heat   != nullptr) heat[out]   = tr.heat();
        ++out;
    }
    return out;
}

/** Dials, so a sweep can be driven from the sim without a rebuild. */
void fk_set_caps (void* h, float softDb, float hardDb, float emergencyDb)
{
    auto* g = static_cast<Guard*> (h);
    if (g == nullptr) return;
    g->bank.softCapDb      = softDb;
    g->bank.hardCapDb      = hardDb;
    g->bank.emergencyCapDb = emergencyDb;
}

} // extern "C"
