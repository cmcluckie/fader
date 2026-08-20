#pragma once
#include <juce_dsp/juce_dsp.h>
#include <array>
#include <atomic>
#include <cmath>
#include <algorithm>

namespace fk
{
/**
    Spectral feedback detector.

    Runs on the audio thread. Everything is preallocated in prepare(); nothing here
    allocates, locks or logs once running.

    A candidate has to survive four tests before it is called feedback:

      1. Prominence  - it stands well clear of the local spectral floor, i.e. it is a
                       narrow spike rather than a broad musical formant.
      2. Persistence - it is present in N consecutive analysis frames.
      3. Pitch lock  - its frequency barely moves. A sung note drifts and has vibrato;
                       an acoustic feedback loop sits on one frequency.
      4. No harmonics- there is little energy at 2f and 3f, and it is not itself sitting
                       at 2x or 3x a louder partial. Feedback is close to a pure sine;
                       voices and instruments arrive with a harmonic series attached.
*/
class FeedbackDetector
{
public:
    struct Params
    {
        float  prominenceDb   = 12.0f;  // how far above the local floor a spike must sit
        int    persistFrames  = 5;      // consecutive frames required
        float  pitchTolerance = 0.006f; // max fractional frequency drift across those frames
        float  harmonicDb     = 20.0f;  // if 2f or 3f is within this of f, treat as musical
        float  floorDb        = -70.0f; // ignore everything quieter than this
    };

    struct Event
    {
        float freq   = 0.0f;
        float levelDb= 0.0f;
    };

    static constexpr int fftOrder  = 11;              // 2048 -> 23.4 Hz bins at 48k
    static constexpr int fftSize   = 1 << fftOrder;
    static constexpr int numBins   = fftSize / 2;
    static constexpr int hopSize   = fftSize / 4;     // 512 -> ~10.7 ms between frames
    static constexpr int maxSuspects = 24;
    static constexpr int eventQueueSize = 16;

    FeedbackDetector() : fft (fftOrder),
                         window ((size_t) fftSize, juce::dsp::WindowingFunction<float>::hann) {}

    void prepare (double sampleRateIn)
    {
        sampleRate = sampleRateIn;
        binHz      = (float) (sampleRate / fftSize);
        ring.fill (0.0f);
        writePos = 0;
        sinceHop = 0;
        for (auto& s : suspects) s = Suspect{};
        for (auto& m : publishedMag) m.store (-120.0f, std::memory_order_relaxed);
        eventRead = eventWrite = 0;
    }

    void setParams (const Params& p) noexcept { params = p; }

    /** Feed one block. Analysis fires internally every hopSize samples. */
    void push (const float* data, int numSamples) noexcept
    {
        for (int n = 0; n < numSamples; ++n)
        {
            ring[(size_t) writePos] = data[n];
            writePos = (writePos + 1) & (ringSize - 1);

            if (++sinceHop >= hopSize)
            {
                sinceHop = 0;
                analyse();
            }
        }
    }

    bool popEvent (Event& out) noexcept
    {
        if (eventRead == eventWrite) return false;
        out = events[(size_t) eventRead];
        eventRead = (eventRead + 1) % eventQueueSize;
        return true;
    }

    /** For the GUI. Racy by design; a torn frame just means one repaint looks odd. */
    float getBinDb (int bin) const noexcept
    {
        return publishedMag[(size_t) bin].load (std::memory_order_relaxed);
    }

    float binToHz (int bin) const noexcept { return bin * binHz; }
    float getBinHz() const noexcept        { return binHz; }

private:
    struct Suspect
    {
        bool   active     = false;
        float  freq       = 0.0f;
        float  minFreq    = 0.0f;
        float  maxFreq    = 0.0f;
        float  firstLevel = 0.0f;
        float  lastLevel  = 0.0f;
        int    frames     = 0;
        int    missed     = 0;
        bool   reported   = false;
    };

    void analyse() noexcept
    {
        // newest fftSize samples, oldest first
        int r = (writePos - fftSize) & (ringSize - 1);
        for (int i = 0; i < fftSize; ++i)
        {
            scratch[(size_t) i] = ring[(size_t) r];
            r = (r + 1) & (ringSize - 1);
        }
        std::fill (scratch.begin() + fftSize, scratch.end(), 0.0f);

        window.multiplyWithWindowingTable (scratch.data(), (size_t) fftSize);
        fft.performFrequencyOnlyForwardTransform (scratch.data());

        constexpr float norm = 2.0f / fftSize;
        for (int i = 0; i < numBins; ++i)
        {
            const float m  = scratch[(size_t) i] * norm;
            const float db = juce::Decibels::gainToDecibels (m, -120.0f);
            mag[(size_t) i] = db;
            publishedMag[(size_t) i].store (db, std::memory_order_relaxed);
        }

        for (auto& s : suspects) if (s.active) ++s.missed;

        // --- test 1: prominent narrow peaks -------------------------------------
        constexpr int floorHalfWidth = 24;   // ~560 Hz either side at 48k
        const int firstBin = juce::jmax (2, (int) (80.0f / binHz));
        const int lastBin  = juce::jmin (numBins - 3, (int) (8000.0f / binHz));

        for (int i = firstBin; i <= lastBin; ++i)
        {
            const float here = mag[(size_t) i];
            if (here < params.floorDb) continue;
            if (here <= mag[(size_t) (i - 1)] || here <= mag[(size_t) (i + 1)]) continue;

            const float localFloor = medianAround (i, floorHalfWidth);
            if (here - localFloor < params.prominenceDb) continue;

            // parabolic interpolation for sub-bin frequency accuracy
            const float ym1 = mag[(size_t) (i - 1)], y0 = here, yp1 = mag[(size_t) (i + 1)];
            const float denom = (ym1 - 2.0f * y0 + yp1);
            const float delta = (std::abs (denom) > 1.0e-6f) ? 0.5f * (ym1 - yp1) / denom : 0.0f;
            const float freq  = (i + juce::jlimit (-0.5f, 0.5f, delta)) * binHz;

            // --- test 4: harmonic content -------------------------------------
            if (hasHarmonicSupport (freq, here)) continue;

            track (freq, here);
        }

        for (auto& s : suspects)
            if (s.active && s.missed > 2) s = Suspect{};
    }

    /** True if this looks like part of a harmonic series rather than a lone tone. */
    bool hasHarmonicSupport (float f, float levelDb) const noexcept
    {
        auto levelAt = [this] (float hz) -> float
        {
            const int b = (int) std::round (hz / binHz);
            if (b < 1 || b >= numBins - 1) return -120.0f;
            return juce::jmax (mag[(size_t) (b - 1)], juce::jmax (mag[(size_t) b], mag[(size_t) (b + 1)]));
        };

        // upward: is there a partial at 2f or 3f close behind it?
        if (levelAt (f * 2.0f) > levelDb - params.harmonicDb) return true;
        if (levelAt (f * 3.0f) > levelDb - params.harmonicDb) return true;

        // downward: are we ourselves the 2nd or 3rd partial of something louder?
        if (levelAt (f * 0.5f)        > levelDb - params.harmonicDb) return true;
        if (levelAt (f * (1.0f/3.0f)) > levelDb - params.harmonicDb) return true;

        return false;
    }

    void track (float freq, float levelDb) noexcept
    {
        // --- test 2 and 3: persistence and pitch lock ---------------------------
        for (auto& s : suspects)
        {
            if (! s.active) continue;
            if (std::abs (s.freq - freq) / freq > 0.03f) continue;

            s.missed   = 0;
            s.frames  += 1;
            s.lastLevel= levelDb;
            s.freq     = 0.7f * s.freq + 0.3f * freq;
            s.minFreq  = juce::jmin (s.minFreq, freq);
            s.maxFreq  = juce::jmax (s.maxFreq, freq);

            const float spread = (s.maxFreq - s.minFreq) / s.freq;

            if (! s.reported
                && s.frames >= params.persistFrames
                && spread   <= params.pitchTolerance
                && s.lastLevel >= s.firstLevel - 1.5f)   // steady or growing, not decaying
            {
                s.reported = true;
                pushEvent ({ s.freq, s.lastLevel });
            }
            return;
        }

        for (auto& s : suspects)
        {
            if (s.active) continue;
            s = Suspect{};
            s.active     = true;
            s.freq       = freq;
            s.minFreq    = freq;
            s.maxFreq    = freq;
            s.firstLevel = levelDb;
            s.lastLevel  = levelDb;
            s.frames     = 1;
            return;
        }
    }

    float medianAround (int centre, int halfWidth) noexcept
    {
        const int lo = juce::jmax (0, centre - halfWidth);
        const int hi = juce::jmin (numBins - 1, centre + halfWidth);
        int count = 0;
        for (int i = lo; i <= hi; ++i)
            if (std::abs (i - centre) > 2) medianScratch[(size_t) count++] = mag[(size_t) i];

        if (count == 0) return -120.0f;
        const int mid = count / 2;
        std::nth_element (medianScratch.begin(), medianScratch.begin() + mid,
                          medianScratch.begin() + count);
        return medianScratch[(size_t) mid];
    }

    void pushEvent (Event e) noexcept
    {
        const int next = (eventWrite + 1) % eventQueueSize;
        if (next == eventRead) return;         // full: drop, we will see it again next frame
        events[(size_t) eventWrite] = e;
        eventWrite = next;
    }

    static constexpr int ringSize = fftSize * 2;   // power of two

    juce::dsp::FFT fft;
    juce::dsp::WindowingFunction<float> window;

    std::array<float, (size_t) ringSize>   ring {};
    std::array<float, (size_t) fftSize * 2> scratch {};
    std::array<float, (size_t) numBins>    mag {};
    std::array<float, (size_t) 64 * 2 + 4> medianScratch {};
    std::array<std::atomic<float>, (size_t) numBins> publishedMag;

    std::array<Suspect, maxSuspects> suspects {};
    std::array<Event, eventQueueSize> events {};
    int eventRead = 0, eventWrite = 0;

    Params params;
    double sampleRate = 48000.0;
    float  binHz      = 23.4375f;
    int    writePos   = 0;
    int    sinceHop   = 0;
};

} // namespace fk
