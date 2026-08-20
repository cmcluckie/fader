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
    double q        = 20.0;
    double targetDb = 0.0;     // negative
    double currentDb= 0.0;     // smoothed toward targetDb
    double lastHitS = 0.0;     // transport seconds of last (re)trigger
};

/** Fixed-size bank. Everything is preallocated; safe to drive from the audio thread. */
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
    }

    void reset() noexcept { for (auto& b : filters) b.reset(); }

    /** Deepen an existing notch near f, or claim a free slot. Returns slot index or -1. */
    int trigger (double f, double stepDb, double maxCutDb, double nowSeconds) noexcept
    {
        const int existing = findNear (f);
        if (existing >= 0)
        {
            auto& s = slots[(size_t) existing];
            s.lastHitS = nowSeconds;
            if (! s.locked)
                s.targetDb = std::max (maxCutDb, s.targetDb - stepDb);
            return existing;
        }

        const int free = findFree();
        if (free < 0) return -1;

        auto& s = slots[(size_t) free];
        s = NotchSlot{};
        s.active   = true;
        s.freq     = f;
        s.q        = defaultQ;
        s.targetDb = -stepDb;
        s.currentDb= 0.0;
        s.lastHitS = nowSeconds;
        filters[(size_t) free].reset();
        return free;
    }

    int placeManual (double f, double depthDb, double nowSeconds) noexcept
    {
        const int free = findFree();
        if (free < 0) return -1;
        auto& s = slots[(size_t) free];
        s = NotchSlot{};
        s.active   = true;
        s.locked   = true;
        s.manual   = true;
        s.freq     = f;
        s.q        = defaultQ;
        s.targetDb = depthDb;
        s.currentDb= 0.0;
        s.lastHitS = nowSeconds;
        filters[(size_t) free].reset();
        return free;
    }

    /** Walk back unlocked notches that have not retriggered recently. */
    void release (double nowSeconds, double holdSeconds, double stepDb) noexcept
    {
        for (auto& s : slots)
        {
            if (! s.active || s.locked) continue;
            if (nowSeconds - s.lastHitS < holdSeconds) continue;

            s.targetDb += stepDb;
            s.lastHitS  = nowSeconds;
            if (s.targetDb >= -0.25) { s.active = false; s.targetDb = 0.0; }
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

    /** Index of the active notch closest to f in cents, or -1 if none within tolerance. */
    int findNear (double f, double tolFraction = 0.02) const noexcept
    {
        int    best = -1;
        double bestErr = tolFraction;
        for (int i = 0; i < MaxNotches; ++i)
        {
            if (! slots[(size_t) i].active) continue;
            const double err = std::abs (slots[(size_t) i].freq - f) / f;
            if (err < bestErr) { bestErr = err; best = i; }
        }
        return best;
    }

    double defaultQ = 20.0;   // ~1/14 octave

private:
    int findFree() const noexcept
    {
        for (int i = 0; i < MaxNotches; ++i) if (! slots[(size_t) i].active) return i;
        return -1;
    }

    std::array<NotchSlot, MaxNotches> slots {};
    std::array<Biquad,    MaxNotches> filters {};
    double fs = 48000.0;
    double smoothCoeff = 0.2;
};

} // namespace fk
