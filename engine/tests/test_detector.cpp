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
#include "../Source/NotchBank.h"

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

    // ---- T9: the filter actually attenuates, by the amount it claims -------
    // The detector can be perfect and still change nothing if the notch path is
    // broken. This measures the cut instead of assuming it.
    {
        fk::NotchBank<24> bank;
        bank.prepare (kSR, 64);
        bank.placeManual (1000.0, -24.0, 0.0);

        auto measure = [&bank] (double hz)
        {
            constexpr int block = 64;
            std::vector<float> buf ((size_t) block);
            double phase = 0.0, peak = 0.0;
            const int blocks = (int) (1.0 * kSR / block);
            for (int b = 0; b < blocks; ++b)
            {
                for (int i = 0; i < block; ++i)
                {
                    phase += 2.0 * M_PI * hz / kSR;
                    buf[(size_t) i] = (float) std::sin (phase);
                }
                bank.process (buf.data(), block, false);
                if (b > blocks / 2)                       // ignore the gain ramp
                    for (int i = 0; i < block; ++i) peak = std::max (peak, (double) std::fabs (buf[(size_t) i]));
            }
            return 20.0 * std::log10 (std::max (peak, 1.0e-9));
        };

        const double atCentre = measure (1000.0);
        const double offCentre = measure (4000.0);
        std::snprintf (msg, sizeof msg, "at 1 kHz %.1f dB, at 4 kHz %.2f dB", atCentre, offCentre);
        report ("T9  a -24 dB notch really cuts 24 dB at its centre",
                std::fabs (atCentre - (-24.0)) < 1.5 && std::fabs (offCentre) < 1.0, msg);
    }

    // ---- T10: repeated hits escalate to the cap, then release to flat ------
    {
        fk::NotchBank<24> bank;
        bank.prepare (kSR, 64);
        bank.softCapDb = -24.0; bank.hardCapDb = -30.0; bank.holdSeconds = 1.0;

        double t = 0.0;
        for (int i = 0; i < 12; ++i, t += 0.05) bank.trigger (5000.0, t);
        const double deepest = bank.getSlot (0).targetDb;

        for (int i = 0; i < 200; ++i) { t += 0.25; bank.release (t); }
        const bool retired = ! bank.getSlot (0).active;

        std::snprintf (msg, sizeof msg, "reached %.0f dB, retired after silence=%d", deepest, (int) retired);
        report ("T10 repeated hits escalate to the cap, then retire",
                deepest <= -24.0 && deepest >= -30.5 && retired, msg);
    }

    // ---- T11: a QUIET lone ring must not be mistaken for a harmonic -------
    // The bug this catches: the harmonic guard asked whether any energy sat at
    // f/2 or 2f within 20 dB. For a quiet ring the noise floor itself qualifies,
    // so the room counted as the ring's own harmonic partner, the ring was born
    // "musical", and it stayed undetected until loud enough to push the noise
    // 20 dB down. Measured at the rig: prominent and ignored for six seconds.
    {
        fk::FeedbackDetector::Params p;
        p.floorDb = -95.0f;             // what the auto-floor actually runs at
        p.minFreq = 1000.0f;
        fk::FeedbackDetector det; init (det, p);

        auto r = runTone (det, 3.0, [] (double) {
            return std::make_pair (9616.0, 0.00025);   // ~ -72 dBFS: quiet, lone, steady
        }, 0.00004, 0.05);

        std::snprintf (msg, sizeof msg, "fired=%d at %.0f Hz after %.0f ms",
                       (int) r.fired, r.firstHz, r.firstSeconds * 1000.0);
        report ("T11 a quiet lone ring is caught within a second",
                r.fired && std::abs (r.firstHz - 9616.0f) < 200.0f && r.firstSeconds < 1.0, msg);
    }

    // ---- T12: a LOW ring must be catchable at all -------------------------
    // From a rehearsal: low-end feedback went unsuppressed. Part of it was the
    // listen band excluding it, but the rest was a stability gate demanding
    // +-5 Hz where one bin is 47 Hz wide - precision the estimator cannot supply,
    // so no low ring could ever hold a track long enough to qualify.
    {
        fk::FeedbackDetector::Params p;
        p.minFreq = 150.0f;
        p.floorDb = -95.0f;
        fk::FeedbackDetector det; init (det, p);

        auto r = runTone (det, 4.0, [] (double t) {
            return std::make_pair (332.0, 0.0004 * std::pow (10.0, 6.0 * t / 20.0));
        }, 0.00008, 0.0);

        std::snprintf (msg, sizeof msg, "fired=%d at %.0f Hz after %.0f ms",
                       (int) r.fired, r.firstHz, r.firstSeconds * 1000.0);
        report ("T12 a 332 Hz ring is detected", r.fired && std::abs (r.firstHz - 332.0f) < 60.0f, msg);
    }

    // ---- T13: how accurately do we actually locate a tone? ----------------
    // This is the number the stability gate depends on, and until now nobody had
    // measured it. Magnitude-only interpolation is worth roughly a quarter of a
    // bin: 5.9 Hz at 2048/48k, which is +-57 cents at 174 Hz - far coarser than
    // the +-5 Hz the gate was asking for.
    {
        struct Probe { double hz; const char* label; };
        const Probe probes[] = { {174.3, "174.3 Hz"}, {332.7, "332.7 Hz"},
                                 {1237.4, "1237.4 Hz"}, {9616.5, "9616.5 Hz"} };
        double worstCents = 0.0;
        char detail[160] = "";
        for (const auto& pr : probes)
        {
            fk::FeedbackDetector::Params p;
            p.minFreq = 150.0f; p.floorDb = -95.0f;
            fk::FeedbackDetector det; init (det, p);
            auto r = runTone (det, 3.0, [&pr] (double t) {
                return std::make_pair (pr.hz, 0.0005 * std::pow (10.0, 6.0 * t / 20.0));
            }, 0.00008, 0.0);
            if (! r.fired) { std::snprintf (detail, sizeof detail, "%s never fired", pr.label); worstCents = 9999; break; }
            const double cents = 1200.0 * std::log2 (r.firstHz / pr.hz);
            if (std::abs (cents) > std::abs (worstCents))
            {
                worstCents = cents;
                std::snprintf (detail, sizeof detail, "worst: %s -> %.1f Hz (%+.1f cents)",
                               pr.label, r.firstHz, cents);
            }
        }
        report ("T13 a tone is located within 20 cents", std::abs (worstCents) < 20.0, detail);
    }

    // ---- T14: a ring in a CLUTTERED spectrum is still caught fast ---------
    // The harmonic guard must discriminate, not just fire. A version of it flagged
    // 97% of unrelated peaks as harmonic, which denied nearly everything the fast
    // path and added 190 ms of persistence - real feedback crawled, and the rig
    // reported it as "detected way too slow". A lone ring surrounded by unrelated
    // tones must still be caught promptly.
    {
        fk::FeedbackDetector::Params p;
        p.minFreq = 150.0f; p.floorDb = -95.0f;
        fk::FeedbackDetector det; init (det, p);

        // A real stage spectrum carries dozens of peaks, and that is what makes a
        // loose harmonic test misfire: with enough tones about, SOMETHING always
        // lands near a multiple of something. Five tones was not a fair test of
        // that - this is twenty, spaced to avoid simple integer relationships.
        static const double clutter[] = {
            237.0,  409.0,  611.0,  853.0, 1103.0, 1439.0, 1787.0, 2141.0,
           2887.0, 3313.0, 3701.0, 4703.0, 5279.0, 5867.0, 6421.0, 7321.0,
           8093.0, 8677.0,11279.0,13417.0 };
        constexpr int nClutter = (int) (sizeof (clutter) / sizeof (clutter[0]));
        double cphase[nClutter] = {};
        constexpr int block = 64;
        std::vector<float> buf ((size_t) block);
        double phase = 0.0;
        fk::FeedbackDetector::Event ev;
        bool fired = false; double firedAt = 0.0; float firedHz = 0.0f;

        for (int b = 0; b < (int) (2.5 * kSR / block) && ! fired; ++b)
        {
            for (int i = 0; i < block; ++i)
            {
                const double t = (double) (b * block + i) / kSR;
                phase += 2.0 * M_PI * 9616.0 / kSR;
                double v = std::min (0.02, 0.002 * std::pow (10.0, 40.0 * t / 20.0)) * std::sin (phase);
                for (int c = 0; c < nClutter; ++c)
                {
                    cphase[c] += 2.0 * M_PI * clutter[c] / kSR;
                    v += 0.01 * std::sin (cphase[c]);
                }
                buf[(size_t) i] = (float) (v + 0.0002 * ((double) rand() / RAND_MAX * 2.0 - 1.0));
            }
            det.push (buf.data(), block);
            while (det.popEvent (ev))
                if (! fired && std::abs (ev.freq - 9616.0f) < 200.0f)
                { fired = true; firedAt = (double) (b * block) / kSR; firedHz = ev.freq; }
        }
        std::snprintf (msg, sizeof msg, "fired=%d at %.0f Hz after %.0f ms",
                       (int) fired, firedHz, firedAt * 1000.0);
        report ("T14 a ring among unrelated tones is caught fast", fired && firedAt < 0.5, msg);
    }

    std::printf ("\n%s  (%d failed)\n\n", failures == 0 ? "ALL PASS" : "FAILURES", failures);
    return failures == 0 ? 0 : 1;
}
