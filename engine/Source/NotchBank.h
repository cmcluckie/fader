#pragma once
#include <cmath>
#include <array>
#include <algorithm>

namespace fk
{
constexpr double kPi = 3.14159265358979323846;

/** Direct-form-II transposed biquad. No allocation, no branching in process(). */
struct Biquad
{
    double b0 = 1.0, b1 = 0.0, b2 = 0.0, a1 = 0.0, a2 = 0.0;
    double z1 = 0.0, z2 = 0.0;

    inline float process (float x) noexcept
    {
        const double y = b0 * x + z1;
        z1 = b1 * x - a1 * y + z2;
        z2 = b2 * x - a2 * y;
        return static_cast<float> (y);
    }

    void reset() noexcept { z1 = z2 = 0.0; }

    void setBypass() noexcept { b0 = 1.0; b1 = b2 = a1 = a2 = 0.0; }

    /** RBJ peaking EQ. gainDb is negative for a cut. */
    void setPeaking (double fs, double f, double q, double gainDb) noexcept
    {
        if (f <= 20.0 || f >= fs * 0.48 || q <= 0.01) { setBypass(); return; }

        const double A     = std::pow (10.0, gainDb / 40.0);
        const double w0    = 2.0 * kPi * f / fs;
        const double cw    = std::cos (w0);
        const double sw    = std::sin (w0);
        const double alpha = sw / (2.0 * q);
        const double a0    = 1.0 + alpha / A;

        b0 = (1.0 + alpha * A) / a0;
        b1 = (-2.0 * cw)       / a0;
        b2 = (1.0 - alpha * A) / a0;
        a1 = (-2.0 * cw)       / a0;
        a2 = (1.0 - alpha / A) / a0;
    }
};

/** One deployed filter. */
struct NotchSlot
{
    bool   active   = false;
    bool   locked   = false;   // never auto-released or auto-deepened
    bool   manual   = false;   // placed by hand rather than detected
    double freq     = 1000.0;
    double q        = 40.0;
    double targetDb = 0.0;     // negative
    double currentDb= 0.0;     // smoothed toward targetDb
    double lastHitS = 0.0;     // transport seconds of last (re)trigger
    double lastRelS = 0.0;     // transport seconds of last release step
};

/**
    Fixed-size notch pool with the adaptive-suppression policy (spec §5-8).

    Depth   : peaking EQ at Q ~40; new notches open at -6 dB (or -12 for a known
              repeat offender) and deepen -6 dB per re-trigger to a -18 dB soft
              cap, then to a -24 dB hard cap while the tone keeps coming back.
    Release : a notch that has not re-triggered for `holdSeconds` bleeds back
              toward 0 at `bleedDbPerSec`, no faster than one step per
              `minReleaseGap`, and retires once it is shallower than `retireDb`.
    Pool    : triggers within 1/12 octave coalesce onto one slot; when every slot
              is busy the least-recently-hit unlocked one is stolen.
    Memory  : a decaying 1/24-octave histogram counts how often each frequency has
              offended; three strikes and new notches there open deep (fast-track).

    Everything is preallocated; safe to drive from the audio thread.
*/
template <int MaxNotches>
class NotchBank
{
public:
    static constexpr int maxNotches = MaxNotches;

    void prepare (double sampleRate, int blockSize) noexcept
    {
        fs = sampleRate;
        // one-pole smoothing coefficient, ~30 ms time constant, applied per block
        const double blockSeconds = (double) blockSize / sampleRate;
        smoothCoeff = 1.0 - std::exp (-blockSeconds / 0.030);
        for (auto& b : filters) b.reset();
        // the histogram's decay clock rides the transport, which restarts here
        for (auto& h : histogram) h = Bucket{};
    }

    void reset() noexcept { for (auto& b : filters) b.reset(); }

    /**
        A detector hit at f. Deepen the coalesced notch if one already covers f,
        otherwise open a fresh one (deeper if f is a known repeat offender).
        Returns the slot index, or -1 only if every slot is locked. `nowSeconds`
        is the transport clock.
    */
    int trigger (double f, double nowSeconds) noexcept
    {
        const int existing = findNear (f);
        if (existing >= 0)
        {
            auto& s = slots[(size_t) existing];
            s.lastHitS = nowSeconds;
            if (! s.locked)
            {
                // Follow the tone. Feedback wanders as the loop builds - a single
                // mode was measured drifting 460 Hz - and a filter this narrow
                // slides off a peak that moves even a little. Without this, a notch
                // can sit at its deepest cut while the ring grows beside it.
                s.freq += (f - s.freq) * freqTrack;

                // deepen a step; allow the hard cap only once we are already at the
                // soft cap and the tone is still knocking (spec §5).
                const double floorDb = (s.targetDb <= softCapDb + 0.25) ? hardCapDb : softCapDb;
                s.targetDb = std::max (floorDb, s.targetDb + stepDb);
            }
            return existing;
        }

        int slot = findFree();
        if (slot < 0) slot = stealLru();          // pool full: evict the coldest notch
        if (slot < 0) return -1;                   // everything locked

        const double openDb = (offenderCount (f, nowSeconds) >= fastTrackStrikes)
                                  ? fastTrackCutDb : initialCutDb;

        auto& s = slots[(size_t) slot];
        s = NotchSlot{};
        s.active   = true;
        s.freq     = f;
        s.q        = defaultQ;
        s.targetDb = openDb;
        s.currentDb= 0.0;
        s.lastHitS = nowSeconds;
        s.lastRelS = nowSeconds;
        filters[(size_t) slot].reset();

        noteOffender (f, nowSeconds);              // one strike per fresh occurrence
        return slot;
    }

    int placeManual (double f, double depthDb, double nowSeconds) noexcept
    {
        int slot = findFree();
        if (slot < 0) slot = stealLru();
        if (slot < 0) return -1;
        auto& s = slots[(size_t) slot];
        s = NotchSlot{};
        s.active   = true;
        s.locked   = true;
        s.manual   = true;
        s.freq     = f;
        s.q        = defaultQ;
        s.targetDb = depthDb;
        s.currentDb= 0.0;
        s.lastHitS = nowSeconds;
        s.lastRelS = nowSeconds;
        filters[(size_t) slot].reset();
        return slot;
    }

    /**
        Bleed unlocked notches back out. Call periodically with the transport
        clock; the per-slot timers enforce the 2 s hold and the 0.5 s minimum gap
        between steps regardless of how often this is called (spec §6).
    */
    void release (double nowSeconds) noexcept
    {
        for (auto& s : slots)
        {
            if (! s.active || s.locked) continue;
            if (nowSeconds - s.lastHitS < holdSeconds)               // still holding:
            {
                s.lastRelS = nowSeconds;                             // bleed clock only
                continue;                                            // runs after hold
            }
            const double dt = nowSeconds - s.lastRelS;
            if (dt < minReleaseGap) continue;                        // not yet

            s.targetDb += bleedDbPerSec * dt;                        // toward 0
            s.lastRelS  = nowSeconds;
            if (s.targetDb >= retireDb) { s.active = false; s.targetDb = 0.0; }
        }
    }

    void clear (bool includeLocked) noexcept
    {
        for (size_t i = 0; i < slots.size(); ++i)
        {
            if (slots[i].locked && ! includeLocked) continue;
            slots[i] = NotchSlot{};
            filters[i].reset();
        }
    }

    void removeAt (int index) noexcept
    {
        if (index < 0 || index >= MaxNotches) return;
        slots[(size_t) index] = NotchSlot{};
        filters[(size_t) index].reset();
    }

    /** Lock every currently active notch — used at the end of a ring-out pass. */
    void lockAll() noexcept
    {
        for (auto& s : slots) if (s.active) s.locked = true;
    }

    /** Smooth gains, refresh coefficients, filter in place. */
    void process (float* data, int numSamples, bool bypassAudio) noexcept
    {
        for (size_t i = 0; i < slots.size(); ++i)
        {
            auto& s = slots[i];
            if (! s.active && std::abs (s.currentDb) < 0.01) { filters[i].setBypass(); continue; }

            const double target = s.active ? s.targetDb : 0.0;
            s.currentDb += (target - s.currentDb) * smoothCoeff;

            if (std::abs (s.currentDb) < 0.01) filters[i].setBypass();
            else                               filters[i].setPeaking (fs, s.freq, s.q, s.currentDb);
        }

        if (bypassAudio) return;

        for (int n = 0; n < numSamples; ++n)
        {
            float x = data[n];
            for (size_t i = 0; i < filters.size(); ++i)
                if (slots[i].active || std::abs (slots[i].currentDb) >= 0.01)
                    x = filters[i].process (x);
            data[n] = x;
        }
    }

    NotchSlot  getSlot (int i) const noexcept { return slots[(size_t) i]; }
    NotchSlot& slotRef  (int i)       noexcept { return slots[(size_t) i]; }
    int  activeCount() const noexcept
    {
        int c = 0; for (auto& s : slots) if (s.active) ++c; return c;
    }

    /** Index of the active notch within `tolOctaves` of f, closest first, or -1. */
    int findNear (double f) const noexcept
    {
        if (f <= 0.0) return -1;
        // The merge window is the filter's own half-bandwidth (f / 2Q), not a fixed
        // musical interval. A tone further away than that is barely attenuated by
        // this notch, so merging onto it would deepen a filter that misses the tone
        // while the ring grows beside it. Anything outside gets its own slot.
        const double window = std::max (10.0, f / (2.0 * defaultQ));
        int    best = -1;
        double bestErr = window;
        for (int i = 0; i < MaxNotches; ++i)
        {
            if (! slots[(size_t) i].active) continue;
            const double err = std::abs (slots[(size_t) i].freq - f);
            if (err < bestErr) { bestErr = err; best = i; }
        }
        return best;
    }

    // ---- depth policy (spec §5, §10) ----------------------------------------
    // Q is wider than the spec's 30-60 because the rig disagreed with the spec:
    // the room's worst mode wanders ~500 Hz, which a Q40 notch (240 Hz wide) cannot
    // hold. Q25 spans ~390 Hz, so one filter keeps its grip instead of the tone
    // sliding out and spawning a fresh shallow notch each time.
    double defaultQ       = 25.0;
    double initialCutDb   = -12.0;   // first strike
    double fastTrackCutDb = -18.0;   // first strike on a known repeat offender
    double stepDb         = -6.0;    // deepen per re-trigger (negative)
    double softCapDb      = -18.0;   // normal ceiling
    double hardCapDb      = -24.0;   // absolute ceiling for a stubborn tone

    // ---- release policy (spec §6) -------------------------------------------
    double holdSeconds    = 2.0;     // quiet time before a notch starts leaving
    double bleedDbPerSec  = 1.5;     // walk-out rate
    double retireDb       = -1.0;    // shallower than this -> drop the notch
    double minReleaseGap  = 0.5;     // min seconds between release steps

    double freqTrack      = 0.30;    // how fast a notch follows a drifting tone

private:
    int findFree() const noexcept
    {
        for (int i = 0; i < MaxNotches; ++i) if (! slots[(size_t) i].active) return i;
        return -1;
    }

    /** Evict the least-recently-hit unlocked notch. -1 if all are locked. */
    int stealLru() noexcept
    {
        int    victim = -1;
        double oldest = 1.0e18;
        for (int i = 0; i < MaxNotches; ++i)
        {
            const auto& s = slots[(size_t) i];
            if (! s.active || s.locked) continue;
            if (s.lastHitS < oldest) { oldest = s.lastHitS; victim = i; }
        }
        if (victim >= 0) { slots[(size_t) victim] = NotchSlot{}; filters[(size_t) victim].reset(); }
        return victim;
    }

    // ---- offender histogram (spec §8) ---------------------------------------
    // 1/24-octave buckets across the watched band; each bucket's count decays by
    // half every `histHalfLife` seconds (lazily, on touch).
    struct Bucket { double count = 0.0; double stamp = 0.0; };

    static constexpr double histBaseHz    = 200.0;    // band low edge
    static constexpr double histTopHz     = 16000.0;  // band high edge
    static constexpr int    histBuckets   = 160;      // 24/oct * log2(80) ~= 152
    static constexpr double histHalfLife  = 300.0;    // halve every 5 min
    static constexpr int    fastTrackStrikes = 3;

    int bucketOf (double f) const noexcept
    {
        if (f <= 0.0) return -1;
        const int b = (int) std::lround (24.0 * std::log2 (f / histBaseHz));
        if (b < 0 || b >= histBuckets) return -1;
        return b;
    }

    double decayed (Bucket& h, double nowSeconds) const noexcept
    {
        const double dt = nowSeconds - h.stamp;
        if (dt > 0.0) { h.count *= std::exp2 (-dt / histHalfLife); h.stamp = nowSeconds; }
        return h.count;
    }

    double offenderCount (double f, double nowSeconds) noexcept
    {
        const int b = bucketOf (f);
        return b < 0 ? 0.0 : decayed (histogram[(size_t) b], nowSeconds);
    }

    void noteOffender (double f, double nowSeconds) noexcept
    {
        const int b = bucketOf (f);
        if (b < 0) return;
        auto& h = histogram[(size_t) b];
        decayed (h, nowSeconds);
        h.count += 1.0;
    }

    std::array<NotchSlot, MaxNotches> slots {};
    std::array<Biquad,    MaxNotches> filters {};
    std::array<Bucket, (size_t) histBuckets> histogram {};
    double fs = 48000.0;
    double smoothCoeff = 0.2;
};

} // namespace fk
