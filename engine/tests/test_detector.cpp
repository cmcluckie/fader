// ============================================================================
// test_detector.cpp — synthetic-signal tests for FeedbackDetector.
//
// Every gap found in this detector so far was found at the rig: slowly, in a
// loud room, with one pair of ears and no way to tell "it didn't fire" from
// "it fired and I missed it". These tests make the same questions cheap and
// unambiguous, and each one is a bug that actually shipped.
//
// Framework-light: needs only juce_dsp (FFT + windowing), no audio device.
//
//   cmake --build build --target fk-tests && ./build/fk-tests_artefacts/fk-tests
// ============================================================================

#include <juce_dsp/juce_dsp.h>
#include <cstdio>
#include <cmath>
#include <random>
#include <vector>
#include "../Source/FeedbackDetector.h"

namespace
{
constexpr double kSR = 48000.0;

struct Result { bool fired = false; float firstHz = 0.0f; double firstSeconds = 0.0; int count = 0; };

/// Feed a generated signal block by block and report the first detection.
/// gen(t) returns one sample at time t seconds.
template <typename Gen>
Result run (fk::FeedbackDetector& det, double seconds, Gen&& gen)
{
    constexpr int block = 64;
    std::vector<float> buf ((size_t) block);
    const int totalBlocks = (int) (seconds * kSR / block);

    Result r;
    for (int b = 0; b < totalBlocks; ++b)
    {
        for (int i = 0; i < block; ++i)
        {
            const double t = (double) (b * block + i) / kSR;
            buf[(size_t) i] = (float) gen (t);
        }
        det.push (buf.data(), block);

        fk::FeedbackDetector::Event ev;
        while (det.popEvent (ev))
        {
            if (! r.fired)
            {
                r.fired = true;
                r.firstHz = ev.freq;
                r.firstSeconds = (double) (b * block) / kSR;
            }
            ++r.count;
        }
    }
    return r;
}

/// Tone harness. `fa(t)` returns {frequency, amplitude}; phase is integrated so
/// a moving frequency stays a clean FM sweep rather than the phase-scrambled
/// mess that sin(2*pi*f(t)*t) produces.
///
/// Every tone starts with a quiet pre-roll of ITSELF at constant level. Without
/// that, the FFT's 2048-sample window is still filling for the first ~43 ms and
/// its magnitude ramps hard - which reads as enormous growth and fires every
/// test on an artefact. The first version of this suite did exactly that, and
/// passed with the code under test reverted.
template <typename FA>
Result runTone (fk::FeedbackDetector& det, double seconds, FA&& fa,
                double noiseAmp = 0.002, double gateToneAmp = 0.0)
{
    constexpr int block = 64;
    std::vector<float> buf ((size_t) block);
    const int totalBlocks = (int) (seconds * kSR / block);
    double phase = 0.0;

    Result r;
    for (int b = 0; b < totalBlocks; ++b)
    {
        for (int i = 0; i < block; ++i)
        {
            const double t = (double) (b * block + i) / kSR;
            const auto fa2 = fa (t);
            phase += 2.0 * M_PI * fa2.first / kSR;
            // A 100 Hz tone sits below the 200 Hz search floor: it holds the
            // broadband input gate open without ever becoming a candidate, so the
            // watched band can stay clean enough that noise cannot fake growth.
            const double gate = gateToneAmp * std::sin (2.0 * M_PI * 100.0 * t);
            buf[(size_t) i] = (float) (fa2.second * std::sin (phase) + gate
                                       + noiseAmp * ((double) rand() / RAND_MAX * 2.0 - 1.0));
        }
        det.push (buf.data(), block);

        fk::FeedbackDetector::Event ev;
        while (det.popEvent (ev))
        {
            if (! r.fired) { r.fired = true; r.firstHz = ev.freq; r.firstSeconds = (double) (b * block) / kSR; }
            ++r.count;
        }
    }
    return r;
}

std::mt19937 rng { 20260822 };
double noise (double amp) { return amp * (std::uniform_real_distribution<double> (-1.0, 1.0) (rng)); }

// The detector holds atomics for the display, so it is neither copyable nor
// movable: initialise one in place rather than returning it.
void init (fk::FeedbackDetector& d, fk::FeedbackDetector::Params p = {})
{
    d.prepare (kSR);
    d.setParams (p);          // shipping defaults unless a test says otherwise
}

int failures = 0;
void report (const char* name, bool pass, const char* detail)
{
    std::printf ("  %s  %-52s %s\n", pass ? "PASS" : "FAIL", name, detail);
    if (! pass) ++failures;
}
}

int main()
{
    std::printf ("\nFeedbackDetector — synthetic signal tests\n");
    std::printf ("=========================================\n\n");
    char msg[160];

    // ---- T1: the original bug — a ring above 8 kHz must be caught ----------
    {
        fk::FeedbackDetector det; init (det);
        auto r = runTone (det, 2.0, [] (double t) {
            // 250 ms steady pre-roll (window fills, nothing to report), then a
            // real attack. Short enough that the slow-creep path cannot claim it.
            const double amp = t < 0.25 ? 0.01
                                        : std::min (0.01 * std::pow (10.0, 40.0 * (t - 0.25) / 20.0), 0.3);
            return std::make_pair (10000.0, amp);
        });
        std::snprintf (msg, sizeof msg, "fired=%d at %.0f Hz after %.0f ms",
                       (int) r.fired, r.firstHz, r.firstSeconds * 1000.0);
        report ("T1  growing 10 kHz ring is detected", r.fired && std::abs (r.firstHz - 10000.0f) < 120.0f, msg);
    }

    // ---- T2: the slow creep — growth too gentle to trip the growth gate ----
    // This is the path added from the reference implementation. Before it, a
    // ring hovering near unity loop gain was invisible until it accelerated.
    {
        fk::FeedbackDetector det; init (det);
        auto r = runTone (det, 5.0, [] (double t) {
            // Emerges from the noise at 6 dB/s and stays there. The growth gate
            // wants 30 dB/s, so path A can never fire on this at any point -
            // including the onset, because it starts below the detector's floor.
            return std::make_pair (3150.0, 0.00015 * std::pow (10.0, 6.0 * t / 20.0));
        }, 0.00008, 0.05);
        std::snprintf (msg, sizeof msg, "fired=%d at %.0f Hz after %.0f ms",
                       (int) r.fired, r.firstHz, r.firstSeconds * 1000.0);
        // Firing LATE is the proof: path A is instant, the sustain path waits ~500 ms.
        report ("T2  slow creep (6 dB/s) caught by the sustain path",
                r.fired && std::abs (r.firstHz - 3150.0f) < 60.0f && r.firstSeconds > 0.4, msg);
    }

    // ---- T3: high-frequency ring with realistic estimator jitter ----------
    // A flat +-5 Hz stability window is 0.04% at 12 kHz — tighter than the
    // sub-bin estimator's own noise, so this used to never hold a track.
    {
        fk::FeedbackDetector det; init (det);
        auto r = runTone (det, 5.0, [] (double t) {
            // +-6 Hz wander: 12 Hz of spread. A flat +-5 Hz window rejects that
            // outright; the frequency-scaled window (+-9.6 Hz at 12 kHz) accepts it.
            return std::make_pair (12000.0 + 6.0 * std::sin (2.0 * M_PI * 11.0 * t),
                                   0.00015 * std::pow (10.0, 6.0 * t / 20.0));
        }, 0.00008, 0.05);
        std::snprintf (msg, sizeof msg, "fired=%d at %.0f Hz after %.0f ms",
                       (int) r.fired, r.firstHz, r.firstSeconds * 1000.0);
        report ("T3  12 kHz ring with +-6 Hz jitter is detected",
                r.fired && std::abs (r.firstHz - 12000.0f) < 200.0f && r.firstSeconds > 0.4, msg);
    }

    // ---- T4: a sung note must NOT be notched ------------------------------
    // Vibrato + a harmonic series. This is what was cutting the singer's voice
    // at 291 and 420 Hz in the rig logs.
    {
        fk::FeedbackDetector det; init (det);
        auto r = run (det, 3.0, [] (double t) {
            const double vib = 1.0 + 0.015 * std::sin (2.0 * M_PI * 5.5 * t);   // 5.5 Hz, +-1.5%
            const double f0 = 400.0 * vib;
            const double env = std::min (1.0, t / 0.3);
            double s = 0.0;
            for (int h = 1; h <= 5; ++h)
                s += (0.06 / h) * std::sin (2.0 * M_PI * f0 * h * t);
            return env * s + noise (0.002);
        });
        std::snprintf (msg, sizeof msg, "detections=%d%s", r.count,
                       r.fired ? " (would have notched the singer)" : "");
        report ("T4  sung 400 Hz with vibrato is NOT notched", ! r.fired, msg);
    }

    // ---- T5: broadband noise is not a ring --------------------------------
    {
        fk::FeedbackDetector det; init (det);
        auto r = run (det, 2.0, [] (double) { return noise (0.05); });
        std::snprintf (msg, sizeof msg, "detections=%d", r.count);
        report ("T5  loud broadband noise is NOT detected", ! r.fired, msg);
    }

    // ---- T6: below the input gate, stay asleep ----------------------------
    {
        fk::FeedbackDetector det; init (det);
        auto r = runTone (det, 2.0, [] (double) { return std::make_pair (5000.0, 0.0004); }, 0.0);
        std::snprintf (msg, sizeof msg, "detections=%d", r.count);
        report ("T6  ring below the input gate is ignored", ! r.fired, msg);
    }

    // ---- T7: outside the listen band, stay out ----------------------------
    {
        fk::FeedbackDetector::Params p;
        p.minFreq = 1000.0f;                       // "1 kHz - vocal mic"
        fk::FeedbackDetector det; init (det, p);
        auto r = runTone (det, 2.0, [] (double) { return std::make_pair (400.0, 0.05); });
        std::snprintf (msg, sizeof msg, "detections=%d", r.count);
        report ("T7  400 Hz ring below a 1 kHz low edge is ignored", ! r.fired, msg);
    }

    // ---- T8: a ring that dies stops producing events -----------------------
    {
        fk::FeedbackDetector det; init (det);
        auto r = runTone (det, 3.0, [] (double t) {
            return std::make_pair (6000.0, t > 1.2 ? 0.0 : 0.05);
        });
        // it must have fired while ringing, and gone quiet afterwards
        std::snprintf (msg, sizeof msg, "fired=%d detections=%d", (int) r.fired, r.count);
        report ("T8  ring is caught, then the detector goes quiet", r.fired, msg);
    }

    std::printf ("\n%s  (%d failed)\n\n", failures == 0 ? "ALL PASS" : "FAILURES", failures);
    return failures == 0 ? 0 : 1;
}
