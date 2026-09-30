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

namespace
{
constexpr int kMaxNotches = 48;

struct EventRec  { float hz, levelDb; int growing, path; double t; };
struct RejectRec { float hz, levelDb; int reason, frames; double t; };

struct Guard
{
    fk::FeedbackDetector        det;
    fk::NotchBank<kMaxNotches>  bank;
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

/** Create a guard. Returns an opaque handle, or null. */
void* fk_create (double sampleRate, int maxBlock)
{
    auto* g = new (std::nothrow) Guard();
    if (g == nullptr) return nullptr;

    g->sampleRate = sampleRate;
    g->det.prepare (sampleRate);
    g->bank.prepare (sampleRate, maxBlock);
    return g;
}

void fk_destroy (void* h) { delete static_cast<Guard*> (h); }

/** Reset to a clean guard without reallocating - between sim scenarios. */
void fk_reset (void* h)
{
    auto* g = static_cast<Guard*> (h);
    if (g == nullptr) return;
    g->bank.clear (true);
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

    fk::FeedbackDetector::Reject rj;
    while (g->det.popReject (rj))
        g->rjq.push_back ({ rj.freq, rj.levelDb, rj.reason, rj.frames, g->t });

    fk::FeedbackDetector::Event ev;
    while (g->det.popEvent (ev))
    {
        g->bank.trigger (ev.freq, g->t, ev.growing, ev.levelDb,
                         (double) (ev.widthHiHz - ev.widthLoHz), ev.path == 4);
        g->evq.push_back ({ ev.freq, ev.levelDb, ev.growing ? 1 : 0, ev.path, g->t });
        ++g->events;
    }

    g->bank.process (buf, n, false);
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

/** Drain one rejection: a suspect that waited long enough and was declined. */
int fk_pop_reject (void* h, float* hz, float* levelDb, int* reason, int* frames, double* t)
{
    auto* g = static_cast<Guard*> (h);
    if (g == nullptr || g->rjRead >= g->rjq.size()) { if (g) { g->rjq.clear(); g->rjRead = 0; } return 0; }
    const auto& r = g->rjq[g->rjRead++];
    if (hz) *hz = r.hz; if (levelDb) *levelDb = r.levelDb; if (reason) *reason = r.reason;
    if (frames) *frames = r.frames; if (t) *t = r.t;
    return 1;
}

/** Replays only: a recording cannot respond to a cut, so the not-in-the-loop
    verdict fires by construction and sawtooths every filter. Turn it off to
    measure holding. Never off live. */
void fk_set_loop_verdict (void* h, int on)
{
    auto* g = static_cast<Guard*> (h);
    if (g) g->bank.loopVerdict = on != 0;
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
