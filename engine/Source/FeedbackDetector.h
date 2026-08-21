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
        float  prominenceDb   = 10.0f;  // spike must stand this far above the local median
        int    persistFrames  = 6;      // stability window, ~64 ms at 512 hop / 48k
        float  stabilityHz    = 5.0f;   // peak may drift at most this many Hz across the window
        float  growthDb       = 3.0f;   // required rise expressed per 100 ms (loop gain > 1);
                                        // scaled to the real window so the threshold does
                                        // not silently change when the hop size does
        float  harmonicDb     = 20.0f;  // energy at 2f/3f within this of f => musical
        int    harmonicExtra  = 36;     // extra frames (~190 ms) required if harmonic-related
        float  floorDb        = -70.0f; // ignore bins quieter than this
        float  inputGateDb    = -55.0f; // skip detection when broadband input is below this
        float  minFreq        = 200.0f; // low edge of the watched band
        float  maxFreq        = 16000.0f; // high edge; still clamped to Nyquist by bin count
        float  pitchTolerance = 0.006f; // (retained for compatibility; superseded by stabilityHz)
    };

    struct Event
    {
        float freq   = 0.0f;
        float levelDb= 0.0f;
    };

    static constexpr int fftOrder  = 11;              // 2048 -> 23.4 Hz bins at 48k
    static constexpr int fftSize   = 1 << fftOrder;
    static constexpr int numBins   = fftSize / 2;
    static constexpr int hopSize   = fftSize / 8;     // 256 -> ~5.3 ms between frames
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
    static constexpr int windowSize = 48;   // >= any usable persistFrames

    struct Suspect
    {
        bool   active      = false;
        bool   harmonic    = false;   // looked harmonically-related when first seen
        float  freq        = 0.0f;
        float  lastLevel   = 0.0f;
        int    frames      = 0;
        int    missed      = 0;
        bool   reported    = false;
        float  reportLevel = 0.0f;    // level at the last event, for escalation
        int    sinceReport = 0;

        // Stability and growth are judged over a ROLLING window, not the suspect's
        // whole life. Judging them cumulatively was a real bug: a tone that wandered
        // while it built blew its +-5 Hz budget permanently, and since the envelope
        // only ever widened it could never qualify again. That is exactly the moment
        // feedback hops to a new frequency, so the fastest-moving rings were the ones
        // we went blind to.
        std::array<float, windowSize> wFreq {};
        std::array<float, windowSize> wLevel {};
        int    windowPos   = 0;
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

        // input gate: broadband level of the frame (before windowing scales it)
        double sumSq = 0.0;
        for (int i = 0; i < fftSize; ++i) sumSq += (double) scratch[(size_t) i] * scratch[(size_t) i];
        const float rmsDb = juce::Decibels::gainToDecibels ((float) std::sqrt (sumSq / fftSize), -120.0f);

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

        // §4.5 input gate: don't chase noise between songs (spectrum still published)
        if (rmsDb >= params.inputGateDb)
        {
            constexpr int floorHalfWidth = 20;   // ±20 bins for the local median (§4.1)
            const int firstBin = juce::jmax (2, (int) (params.minFreq / binHz));
            const int lastBin  = juce::jmin (numBins - 3, (int) (params.maxFreq / binHz));

            for (int i = firstBin; i <= lastBin; ++i)
            {
                const float here = mag[(size_t) i];
                if (here < params.floorDb) continue;
                if (here <= mag[(size_t) (i - 1)] || here <= mag[(size_t) (i + 1)]) continue;

                const float localFloor = medianAround (i, floorHalfWidth);
                if (here - localFloor < params.prominenceDb) continue;

                // parabolic interpolation for sub-bin frequency accuracy (§3)
                const float ym1 = mag[(size_t) (i - 1)], y0 = here, yp1 = mag[(size_t) (i + 1)];
                const float denom = (ym1 - 2.0f * y0 + yp1);
                const float delta = (std::abs (denom) > 1.0e-6f) ? 0.5f * (ym1 - yp1) / denom : 0.0f;
                const float freq  = (i + juce::jlimit (-0.5f, 0.5f, delta)) * binHz;

                // §4.4 harmonic guard: soft - flag it, track() then demands extra time
                track (freq, here, hasHarmonicSupport (freq, here));
            }
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

    void track (float freq, float levelDb, bool harmonic) noexcept
    {
        // Associate with an existing candidate (a wider window than the stability
        // test - the peak may wander a little before it locks).
        for (auto& s : suspects)
        {
            if (! s.active) continue;
            if (std::abs (s.freq - freq) > 20.0f) continue;

            s.missed    = 0;
            s.frames   += 1;
            s.lastLevel = levelDb;
            s.freq      = 0.8f * s.freq + 0.2f * freq;

            s.wFreq[(size_t) s.windowPos]  = freq;
            s.wLevel[(size_t) s.windowPos] = levelDb;
            s.windowPos = (s.windowPos + 1) % windowSize;

            // §4.2/§4.3 over the last N frames only
            const int n = juce::jlimit (1, windowSize, juce::jmin (s.frames, params.persistFrames));
            float lo = 1.0e9f, hi = -1.0e9f;
            for (int i = 1; i <= n; ++i)
            {
                const int idx = (s.windowPos - i + windowSize) % windowSize;
                lo = juce::jmin (lo, s.wFreq[(size_t) idx]);
                hi = juce::jmax (hi, s.wFreq[(size_t) idx]);
            }
            const int   oldest = (s.windowPos - n + windowSize) % windowSize;
            const float spread = hi - lo;
            const float growth = levelDb - s.wLevel[(size_t) oldest];

            // Growth is a RATE. Comparing a fixed dB figure against whatever the
            // window happens to be couples the threshold to the hop size: halving
            // the hop once made this test 3x stricter by accident, so only violently
            // building rings qualified and steady ones were ignored until they were
            // already loud. Scale the requirement to the window's real duration.
            const float windowSec  = (float) (n * hopSize) / (float) sampleRate;
            const float needGrowth = params.growthDb * (windowSec / 0.1f);
            const int   need   = params.persistFrames + (s.harmonic ? params.harmonicExtra : 0);

            if (! s.reported)
            {
                if (s.frames >= need && spread <= params.stabilityHz && growth >= needGrowth)
                {
                    s.reported    = true;
                    s.reportLevel = s.lastLevel;
                    s.sinceReport = 0;
                    pushEvent ({ s.freq, s.lastLevel });
                }
            }
            else
            {
                // §5 progressive escalation. Two ways to re-fire:
                //  - fast: the peak is still climbing (+1 dB), throttled to ~50 ms;
                //  - sustain: the tone simply survives ~250 ms after the last cut.
                // The sustain path is what handles feedback that has PLATEAUED - it
                // grows, saturates, and then never again exceeds its own maximum, so
                // a growth-only gate goes permanently silent while it rings. Merely
                // still being tracked means it out-lived the last cut: deepen again
                // (trigger() also refreshes the notch's hold, so a notch can never
                // bleed away underneath a tone that is still present).
                ++s.sinceReport;
                const bool climbing  = s.sinceReport >= 3  && s.lastLevel - s.reportLevel >= 1.0f;
                const bool surviving = s.sinceReport >= 24;                 // ~250 ms
                if (climbing || surviving)
                {
                    s.reportLevel = s.lastLevel;
                    s.sinceReport = 0;
                    pushEvent ({ s.freq, s.lastLevel });
                }
            }
            return;
        }

        for (auto& s : suspects)
        {
            if (s.active) continue;
            s = Suspect{};
            s.active     = true;
            s.harmonic   = harmonic;
            s.freq       = freq;
            s.lastLevel  = levelDb;
            s.frames     = 1;
            s.wFreq[0]   = freq;
            s.wLevel[0]  = levelDb;
            s.windowPos  = 1;
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
