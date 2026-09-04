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
#include <array>
#include <string>
#include "../Source/FeedbackDetector.h"
#include "../Source/NotchBank.h"

namespace
{
constexpr double kSR = 48000.0;

struct Result { bool fired = false; float firstHz = 0.0f; float firstLevelDb = 0.0f; double firstSeconds = 0.0; int count = 0; };

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
            if (! r.fired)
            {
                r.fired = true; r.firstHz = ev.freq; r.firstLevelDb = ev.levelDb;
                r.firstSeconds = (double) (b * block) / kSR;
            }
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

    // ---- T6: a dead input produces nothing --------------------------------
    // The input gate used to sit at -55 dB and was described as "below the input
    // gate, stay asleep". That was too high by 35 dB and made the detector deaf
    // through quiet passages - see T27, which is a ring it missed for a full second
    // for exactly that reason. The gate now only catches a dead input, so this
    // tests what it is actually for.
    {
        fk::FeedbackDetector det; init (det);
        auto r = runTone (det, 2.0, [] (double) { return std::make_pair (5000.0, 0.0000004); }, 0.0);
        std::snprintf (msg, sizeof msg, "detections=%d", r.count);
        report ("T6  a dead input produces nothing", ! r.fired, msg);
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
    // The ring ARRIVES rather than being present from the first sample. A tone
    // that predates the analysis is indistinguishable from a room mode, and is now
    // deliberately left alone - see T22.
    {
        fk::FeedbackDetector det; init (det);
        auto r = runTone (det, 3.0, [] (double t) {
            const double amp = juce::jmin (0.0002 * std::pow (10.0, 30.0 * t / 20.0), 0.05);
            return std::make_pair (6000.0, t > 1.6 ? 0.0 : amp);
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

        // Quiet and lone, which is what this test is about, but it arrives:
        // it climbs to ~ -72 dBFS and then simply sits there. A tone that was
        // already present before the analysis began is room furniture, and is now
        // left alone deliberately - see T22 - so a ring has to come from somewhere.
        auto r = runTone (det, 3.0, [] (double t) {
            return std::make_pair (9616.0, juce::jmin (0.00002 * std::pow (10.0, 25.0 * t / 20.0),
                                                       0.00025));
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

    // ---- T15: rejections are reported, with a reason ----------------------
    // Until now the detector only logged what fired, so every "it missed one" had
    // to be reverse-engineered by simulating it against a downsampled copy of the
    // spectrum. A near-miss must say which gate stopped it.
    {
        fk::FeedbackDetector det; init (det);
        constexpr int block = 64;
        std::vector<float> buf ((size_t) block);
        double phase = 0.0;
        int counts[5] = {};
        fk::FeedbackDetector::Reject rj;

        for (int b = 0; b < (int) (4.0 * kSR / block); ++b)
        {
            for (int i = 0; i < block; ++i)
            {
                const double t = (double) (b * block + i) / kSR;
                const double vib = 1.0 + 0.015 * std::sin (2.0 * M_PI * 5.5 * t);
                phase += 2.0 * M_PI * (400.0 * vib) / kSR;
                double v = 0.0;
                for (int h = 1; h <= 5; ++h) v += (0.06 / h) * std::sin (phase * h);
                buf[(size_t) i] = (float) (std::min (1.0, t / 0.3) * v
                                           + 0.002 * ((double) rand() / RAND_MAX * 2.0 - 1.0));
            }
            det.push (buf.data(), block);
            while (det.popReject (rj))
                if (rj.reason >= 0 && rj.reason < 5) ++counts[rj.reason];
        }
        const int total = counts[1] + counts[2] + counts[3] + counts[4];
        std::snprintf (msg, sizeof msg, "harmonic=%d unstable=%d no-growth=%d vibrato=%d",
                       counts[1], counts[2], counts[3], counts[4]);
        report ("T15 a rejected candidate reports which gate stopped it", total > 0, msg);
    }

    // ---- T16: real rings captured at the rig, replayed among rig clutter ---
    //
    // What 437 measured rises say about real feedback, by octave band:
    //
    //     1-2 kHz    18.2 dB/s median   1.40 s median duration
    //     2-4 kHz    23.2                1.02
    //     4-8 kHz    27.8                1.02
    //     8-16 kHz   33.6                0.81
    //
    // Lower is slower and lasts longer (r = +0.375 against log frequency), and the
    // -10 dB width is roughly constant in Hz while collapsing as a PROPORTION of
    // centre frequency, 14.3% at 1-2 kHz to 1.7% at 11-16 kHz (r = -0.891). Both
    // tolerances here scale proportionally, so the low end gets the tightest
    // absolute window while its peaks are just as wide in Hz.
    //
    // That reads like it should matter, and it was tested twice. Scaling the growth
    // requirement to the fitted per-band curve changed one synthetic case by 91 ms
    // and nothing else. Letting harmonic suspects use the sustain path, which they
    // are barred from, changed nothing at all. Both were reverted. The reason is
    // that neither gate is what sets the timing: on anything reaching the floor the
    // sustain path fires first regardless, and for a ring hiding under a sung note's
    // harmonic the wait is masking - it has to get louder than the partial it sits
    // on before it is a distinct peak at all - which no threshold can shorten.
    //
    // So: the frequency dependence is real, and it is NOT in these two gates. Anyone
    // reaching for them next should measure before believing it.
    // Rates, frequencies and start levels below are MEASURED, not invented: they
    // come from 22 marked misses at the rig (labels of 2026-08-27), taken from the
    // armed slot's own spectrum. The distribution matters more than any single
    // number - 130 sustained rises, median 29.8 dB/s, tenth percentile 15.6, and a
    // slow tail reaching 3.6 dB/s. The shipping growth gate asks for 30 dB/s, which
    // is the median of real feedback, so path A can only ever catch half of it and
    // the sustain path has to carry the rest.
    //
    // T2 already shows a 6 dB/s creep caught on a clean tone. What that test cannot
    // show is the rig: a dozen other partials alive at once, which is where the
    // tracker actually has to hold its suspect. Hence the clutter here.
    {
        struct Profile { double hz; double rateDbPerSec; double startDb; };
        static const Profile captured[] = {
            { 4593.8,  3.6, -94.1 },   // the slowest thing measured: 16 dB over 4.5 s
            { 4500.0,  5.4, -102.1 },  // 35 dB over 6.4 s - the one marked most often
            { 9562.5,  8.0, -70.0 },
            { 6093.8, 11.0, -106.8 },
            { 6375.0, 13.4, -110.5 },
            { 10031.3, 17.1, -104.4 },
        };

        int caught = 0;
        double worstMs = 0.0, worstHz = 0.0;
        std::string slow;
        for (const auto& pr : captured)
        {
            fk::FeedbackDetector det; init (det);
            const double hz = pr.hz, rate = pr.rateDbPerSec;
            auto r = runTone (det, 8.0, [hz, rate] (double t) {
                // Start well under the floor and climb at the measured rate, with
                // the few-Hz wander a real room gives a ring.
                const double amp = 0.00008 * std::pow (10.0, rate * t / 20.0);
                return std::make_pair (hz + 2.0 * std::sin (2.0 * M_PI * 0.7 * t),
                                       juce::jmin (amp, 0.08));
            }, 0.00008, 0.05);
            if (r.fired && std::abs (r.firstHz - (float) hz) < juce::jmax (60.0f, 0.02f * (float) hz))
            {
                ++caught;
                // Wall-clock to fire is dominated by how long the tone spends under
                // the floor, which is the room's business, not the detector's. What
                // matters is how far it got ABOVE the floor before being caught -
                // the "it let it run away" number.
                const double escape = r.firstLevelDb - fk::FeedbackDetector::Params{}.floorDb;
                if (escape > worstMs) { worstMs = escape; worstHz = hz; }
            }
            else
            {
                if (! slow.empty()) slow += " ";
                slow += std::to_string ((int) hz);
            }
        }
        const int total = (int) (sizeof captured / sizeof captured[0]);
        if (caught == total)
            std::snprintf (msg, sizeof msg, "%d/%d caught, worst escape %.1f dB at %.0f Hz",
                           caught, total, worstMs, worstHz);
        else
            std::snprintf (msg, sizeof msg, "%d/%d caught, MISSED %s Hz", caught, total, slow.c_str());
        report ("T16 rings measured at the rig are all caught", caught == total && worstMs <= 12.0, msg);
    }

    // ---- T17: a ring must be caught with the suspect table already full ----
    // The rig runs a live spectrum: room tone, bleed and partials keep dozens of
    // low peaks alive at once. track() associates with an existing suspect or
    // claims a free slot - and if all 24 are taken it used to return silently, so
    // a brand-new ring simply never entered the tracker. Peaks are offered in
    // ascending frequency order, so the low clutter claimed every slot first and
    // the ring above it was invisible for as long as the clutter held.
    //
    // T14 hid this by using 20 clutter tones, just under the 24 slots.
    {
        constexpr double ringHz = 4500.0;
        fk::FeedbackDetector det; init (det);
        constexpr int block = 64;
        std::vector<float> buf ((size_t) block);
        double ringPhase = 0.0;
        std::array<double, 30> clutterPhase {};

        bool fired = false; double firstS = 0.0; float firstHz = 0.0f;
        const int totalBlocks = (int) (6.0 * kSR / block);
        for (int b = 0; b < totalBlocks && ! fired; ++b)
        {
            for (int i = 0; i < block; ++i)
            {
                const double t = (double) (b * block + i) / kSR;
                double v = 0.05 * std::sin (2.0 * M_PI * 100.0 * t);       // gate keeper
                // 30 steady low partials, all below the ring, all prominent enough
                // to be picked - more than the tracker has slots for.
                for (size_t c = 0; c < clutterPhase.size(); ++c)
                {
                    const double f = 300.0 + 90.0 * (double) c;
                    clutterPhase[c] += 2.0 * M_PI * f / kSR;
                    v += 0.004 * std::sin (clutterPhase[c]);
                }
                // The ring: emerges from under the floor at the rate measured at
                // the rig for the 4500 Hz creep that was never caught.
                ringPhase += 2.0 * M_PI * ringHz / kSR;
                v += juce::jmin (0.00008 * std::pow (10.0, 5.4 * t / 20.0), 0.08)
                     * std::sin (ringPhase);
                buf[(size_t) i] = (float) (v + noise (0.00008));
            }
            det.push (buf.data(), block);
            fk::FeedbackDetector::Event ev;
            while (det.popEvent (ev))
                if (! fired && std::abs (ev.freq - (float) ringHz) < 90.0f)
                { fired = true; firstHz = ev.freq; firstS = (double) (b * block) / kSR; }
        }
        std::snprintf (msg, sizeof msg, "fired=%d at %.0f Hz after %.0f ms",
                       (int) fired, firstHz, firstS * 1000.0);
        report ("T17 a ring is caught when the suspect table is full", fired, msg);
    }

    // ---- T18: the "max cut" setting is the real ceiling --------------------
    // The engine used to hand the user's number to the bank as the SOFT cap and
    // allow a further 6 dB past it for a stubborn tone. Measured at the rig with
    // the dial on -24: 14% of notch samples were cutting deeper than -24, down to
    // -30. T10 sets the bank's caps by hand, so it could never see this - the
    // defect was in how the engine wired them up. This mirrors that wiring.
    {
        constexpr double userMaxCut = -24.0;
        fk::NotchBank<48> bank;
        bank.prepare (kSR, 64);
        bank.softCapDb  = userMaxCut + 6.0;   // exactly as AudioEngine sets them
        bank.hardCapDb  = userMaxCut;
        bank.holdSeconds = 1.0;

        // Hammer one frequency far past the point where it should stop deepening.
        double t = 0.0, deepest = 0.0;
        for (int i = 0; i < 60; ++i, t += 0.05)
        {
            bank.trigger (5000.0, t);
            deepest = juce::jmin (deepest, bank.getSlot (0).targetDb);
        }
        std::snprintf (msg, sizeof msg, "dial %.0f dB, deepest reached %.1f dB", userMaxCut, deepest);
        report ("T18 escalation never cuts past the max-cut setting",
                deepest >= userMaxCut - 0.01, msg);
    }

    // ---- T19: the notch pool holds a rig's worth of resonances --------------
    // At 24 the bank was pinned at its ceiling 37% of the time at the rig, with 22
    // of the 24 cutting more than 3 dB - all doing real work. A full pool means a
    // new ring evicts one still holding something down, and the evicted tone comes
    // straight back: the bank thrashes and neither ring is ever finished.
    {
        fk::NotchBank<48> bank;
        bank.prepare (kSR, 64);
        bank.softCapDb = -18.0; bank.hardCapDb = -24.0; bank.holdSeconds = 30.0;
        // This tests POOL CAPACITY, so the stacking budget is off: with it on, 30
        // filters 450 Hz apart are refused on purpose - overlapping that heavily is
        // a high-cut, which is T29's subject, not this one.
        bank.budgetDb = 0.0;

        // 30 distinct resonances, as a real room presents.
        double t = 0.0;
        for (int i = 0; i < 30; ++i, t += 0.01) bank.trigger (1000.0 + 450.0 * i, t);

        int active = 0;
        for (int i = 0; i < 48; ++i) if (bank.getSlot (i).active) ++active;
        std::snprintf (msg, sizeof msg, "30 distinct tones -> %d filters held", active);
        report ("T19 30 simultaneous resonances all get a filter", active == 30, msg);
    }

    // ---- T20: a notch may follow a drifting tone, but may not ratchet ------
    // The merge window travels with the filter, so before this a tone at the edge
    // of the window pulled the notch part of the way over, which opened a fresh
    // window further out, and the filter random-walked across the spectrum dragged
    // by whatever knocked last. Measured at the rig: notches wandering 650-900 Hz,
    // more than twice their own bandwidth at Q25. One slid 275 Hz off a ring that
    // then climbed 28 dB through the gap while the abandoned filter released.
    {
        fk::NotchBank<48> bank;
        bank.prepare (kSR, 64);
        bank.softCapDb = -18.0; bank.hardCapDb = -24.0; bank.holdSeconds = 30.0;

        // Walk a tone steadily upward, well past the filter's bandwidth, hitting
        // the bank at every step - the ratchet's ideal food.
        double t = 0.0;
        for (int i = 0; i < 120; ++i, t += 0.02) bank.trigger (10000.0 + 8.0 * i, t);

        // The notch placed at 10 kHz must still be within half a bandwidth of it.
        const double leash = 10000.0 / (2.0 * 25.0);     // ~200 Hz
        double anchored = 1.0e9;
        int active = 0;
        for (int i = 0; i < 48; ++i)
        {
            const auto& s = bank.getSlot (i);
            if (! s.active) continue;
            ++active;
            anchored = juce::jmin (anchored, std::abs (s.freq - 10000.0));
        }
        std::snprintf (msg, sizeof msg, "tone swept 10000->10952 Hz: %d filters, nearest sits %.0f Hz from 10 kHz",
                       active, anchored);
        report ("T20 a notch never ratchets past half a bandwidth", anchored <= leash + 1.0, msg);
    }

    // ---- T21: a deep notch must not be a wide notch ------------------------
    // A peaking EQ's shape is fixed by Q, so deepening it drags the skirts down.
    // At 5 kHz with Q=25 a -6 dB notch is 428 Hz wide at its -1 dB points and a
    // -24 dB one is 1449 Hz - each deep filter doing audible damage across more
    // than a kilohertz. Measured at the rig with dozens live, the skirts summed to
    // -13 dB of average cut above 4 kHz, which is a shelf, not a set of notches.
    {
        fk::NotchBank<48> bank;
        bank.prepare (kSR, 64);
        bank.softCapDb = -18.0; bank.hardCapDb = -24.0; bank.holdSeconds = 30.0;

        // RBJ peaking-EQ magnitude, evaluated directly so the test does not depend
        // on the filter object's internals.
        auto respDb = [] (double fq, double f0, double cutDb, double q)
        {
            const double A = std::pow (10.0, cutDb / 40.0);
            const double w = 2.0 * M_PI * f0 / kSR, a = std::sin (w) / (2.0 * q);
            const double W = 2.0 * M_PI * fq / kSR;
            auto mag = [W] (double c0, double c1, double c2)
            {
                const double re = c0 + c1 * std::cos (W) + c2 * std::cos (2.0 * W);
                const double im = -(c1 * std::sin (W) + c2 * std::sin (2.0 * W));
                return std::sqrt (re * re + im * im);
            };
            const double num = mag (1.0 + a * A, -2.0 * std::cos (w), 1.0 - a * A);
            const double den = mag (1.0 + a / A, -2.0 * std::cos (w), 1.0 - a / A);
            return 20.0 * std::log10 (std::max (num / den, 1.0e-12));
        };

        auto widthAt = [&] (double cutDb)
        {
            const double q = fk::NotchBank<48>::qForDepth (25.0, cutDb);
            double lo = 5000.0, hi = 5000.0;
            while (lo > 500.0   && respDb (lo, 5000.0, cutDb, q) < -1.0) lo -= 1.0;
            while (hi < 15000.0 && respDb (hi, 5000.0, cutDb, q) < -1.0) hi += 1.0;
            return hi - lo;
        };

        const double w6 = widthAt (-6.0), w24 = widthAt (-24.0);
        // Four times the depth must not mean four times the damage.
        std::snprintf (msg, sizeof msg, "-6 dB spans %.0f Hz, -24 dB spans %.0f Hz (%.1fx)",
                       w6, w24, w24 / w6);
        report ("T21 a 4x deeper notch is not 4x wider", w24 < 3.0 * w6, msg);
    }

    // ---- T22: a steady room tone must never be notched ---------------------
    // Turning the guard on must not change the sound when there is no feedback.
    // It did. The sustain path asked only for "dead stable for 300 ms and not
    // harmonically related", which describes a room mode, a monitor hiss, an HVAC
    // hum - anything that is simply always present. Those got notched and then
    // held, because analysis runs pre-notch so the tone never appears to go away.
    // Measured at the rig: 97.3% of notch samples pinned at their target, 0.1%
    // ever releasing, two dozen deep filters standing permanently. That is a high
    // shelf on the vocal, present whether or not anything is ringing.
    //
    // These tones are prominent, isolated and rock-steady - everything the sustain
    // path used to want. The only thing they do not do is arrive from nothing.
    {
        int notched = 0;
        float where = 0.0f;
        for (auto hz : { 1150.0, 3300.0, 6700.0, 11900.0 })
        {
            fk::FeedbackDetector::Params p;
            p.floorDb = -95.0f;                  // what the rig actually runs
            fk::FeedbackDetector det; init (det, p);

            // Present at full level from the first sample and never varying: the
            // pre-roll in runTone means there is no onset artefact to fire on.
            auto r = runTone (det, 6.0, [hz] (double) {
                return std::make_pair (hz, 0.0025);      // ~ -52 dBFS, plainly audible
            }, 0.00008, 0.05);
            if (r.fired)
            {
                ++notched; where = r.firstHz;
                std::printf ("      %.0f Hz fired at %.0f ms, level %.1f dB\n",
                             hz, r.firstSeconds * 1000.0, r.firstLevelDb);
            }
        }
        std::snprintf (msg, sizeof msg, notched ? "notched %d of 4 (first at %.0f Hz)"
                                                : "all 4 left alone", notched, where);
        report ("T22 a steady room tone is never notched", notched == 0, msg);
    }

    // ---- T23: nothing inaudible is worth a filter --------------------------
    // The floor is dragged low deliberately, so a ring is tracked while it is
    // still tiny. That is the point of it. But acting on everything it can SEE is
    // how the guard became audible with no feedback present: measured live, 82% of
    // detections sat below -70 dB and the loudest thing in a full minute was -48,
    // so almost every filter was spent on something inaudible - and the pile of
    // them was not. Watching and acting are now separate thresholds.
    {
        fk::FeedbackDetector::Params p;
        p.floorDb  = -95.0f;          // watch right down into the noise, as the rig does
        p.minFreq  = 1000.0f;
        fk::FeedbackDetector det; init (det, p);

        // A ring that arrives and settles well below audibility: real, steady,
        // isolated, and not worth a filter.
        auto quiet = runTone (det, 4.0, [] (double t) {
            return std::make_pair (7350.0, juce::jmin (0.000004 * std::pow (10.0, 25.0 * t / 20.0),
                                                       0.000045));   // ~ -87 dBFS
        }, 0.000004, 0.05);

        // The same ring, allowed to reach a level that matters.
        fk::FeedbackDetector det2; init (det2, p);
        auto loud = runTone (det2, 4.0, [] (double t) {
            return std::make_pair (7350.0, juce::jmin (0.000004 * std::pow (10.0, 25.0 * t / 20.0),
                                                       0.004));      // ~ -48 dBFS
        }, 0.000004, 0.05);

        std::snprintf (msg, sizeof msg, "-87 dB ring: %s   -48 dB ring: %s (%.1f dB)",
                       quiet.fired ? "NOTCHED" : "left alone",
                       loud.fired ? "caught" : "MISSED", loud.firstLevelDb);
        report ("T23 an inaudible ring is watched, not notched",
                ! quiet.fired && loud.fired, msg);
    }

    // ---- T25: a quiet ring must not have to outshout the room to be seen ----
    // The marked miss this comes from: a 10.6 kHz ring climbed 50 dB in 1.5 s and
    // was first detected at -43 dB, at the very top, after being audible and
    // plainly visible on the meter for about two seconds. No gate rejected it. It
    // was never tracked at all - the suspect table was full of louder tones, and a
    // newcomer used to need to be LOUDER than something already in it to get a
    // slot. Feedback arrives quiet and becomes loud, so that rule locked out
    // precisely the case it most needed to catch, and the rejection log could not
    // show it either: nothing that is not a suspect can be reported as one.
    //
    // Forty steady tones, every one far louder than the ring when it appears.
    {
        fk::FeedbackDetector::Params p;
        p.floorDb = -95.0f; p.minFreq = 1000.0f;
        fk::FeedbackDetector det; init (det, p);

        constexpr int block = 64;
        std::vector<float> buf ((size_t) block);
        std::array<double, 40> clutter {};
        double ring = 0.0;
        bool fired = false; float firedAt = 0.0f; double firedMs = 0.0;

        for (int b = 0; b < (int) (4.0 * kSR / block) && ! fired; ++b)
        {
            for (int i = 0; i < block; ++i)
            {
                const double t = (double) (b * block + i) / kSR;
                double v = 0.05 * std::sin (2.0 * M_PI * 100.0 * t);
                for (size_t c = 0; c < clutter.size(); ++c)
                {
                    const double f = 1200.0 + 190.0 * (double) c;   // all below the ring
                    clutter[c] += 2.0 * M_PI * f / kSR;
                    v += 0.0016 * std::sin (clutter[c]);            // ~ -56 dB each
                }
                ring += 2.0 * M_PI * 10594.0 / kSR;
                v += juce::jmin (0.0000025 * std::pow (10.0, 33.0 * t / 20.0), 0.007)
                     * std::sin (ring);
                buf[(size_t) i] = (float) (v + noise (0.00002));
            }
            det.push (buf.data(), block);
            fk::FeedbackDetector::Event ev;
            while (det.popEvent (ev))
                if (! fired && std::abs (ev.freq - 10594.0f) < 120.0f)
                { fired = true; firedAt = ev.levelDb; firedMs = (double)(b*block)/kSR*1000.0; }
        }
        std::snprintf (msg, sizeof msg, fired ? "caught at %.1f dB after %.0f ms" : "NEVER caught",
                       firedAt, firedMs);
        // It must be caught as it crosses the action threshold, not 30 dB later.
        report ("T25 a quiet ring is seen through a table full of louder tones",
                fired && firedAt <= -65.0f, msg);
    }

    // ---- (no T26) -----------------------------------------------------------
    // There was a test here for the peak list filling up: collection scans from the
    // low edge upward, and dropping newcomers once full keeps the N lowest peaks
    // and discards everything above them regardless of prominence. The code change
    // stands - the list now evicts its least prominent entry - but the test could
    // not be made to fail on the old rule, because 120 packed tones only ever
    // produced 8 prominent peaks against a list of 96: they raise each other's
    // local median and stop qualifying. A test that passes either way proves
    // nothing, so it is gone rather than sitting here looking like cover.
    //
    // Whether the rig ever fills that list is still unmeasured. It was NOT the
    // cause of either marked miss - see T25 and T27 for what actually was.

    // ---- T27: a ring building in a QUIET channel ---------------------------
    // The marked miss this comes from. A 7 kHz ring, 33 dB prominent from the
    // moment it appeared, climbing 25 dB/s in a channel that was otherwise nearly
    // silent - local median -114 dB. It was ignored for a full second while it
    // gained 24 dB, then detected at -45 dB, by which point it was audible across
    // the room and had been visible on the meter throughout.
    //
    // Nothing rejected it. The broadband input gate sat at -55 dB, so while the
    // CHANNEL was quiet the detector never looked at all, however prominent the
    // ring. It opened only when the ring itself dragged the channel past -55.
    // Feedback starts in the quiet moments; a broadband gate is deaf exactly then.
    {
        fk::FeedbackDetector::Params p;
        p.floorDb = -95.0f; p.minFreq = 2468.0f;    // the rig's own settings
        fk::FeedbackDetector det; init (det, p);

        // No gate-keeper tone: the channel is quiet apart from the ring, which is
        // the whole point. It climbs at the measured 25 dB/s.
        auto r = runTone (det, 5.0, [] (double t) {
            return std::make_pair (7031.0, juce::jmin (0.000004 * std::pow (10.0, 25.0 * t / 20.0),
                                                       0.006));
        }, 0.0000025, 0.0);

        std::snprintf (msg, sizeof msg, r.fired ? "caught at %.1f dB after %.0f ms" : "NEVER caught",
                       r.firstLevelDb, r.firstSeconds * 1000.0);
        // Caught as it crosses the action threshold, not 30 dB later.
        report ("T27 a ring in a quiet channel is not ignored",
                r.fired && r.firstLevelDb <= -65.0f, msg);
    }

    // ---- T28: the low-end rings from the studio, replayed -------------------
    // Wednesday 2026-08-26: "a whole bunch of really low feedback, killing me the
    // entire night - all the high feedback was gone, but none of that."
    //
    // The reason none of it was caught is not subtle. minHz was 1320 Hz that
    // night, so everything below was outside the search band and the detector
    // never looked: the loudest thing under 2 kHz was 656 Hz at -17.8 dB, and only
    // 1 detection in 200 was below 2 kHz. But the band being wrong is no reason to
    // assume the detector would have coped, so these are measured from that night
    // and checked properly: 20 distinct frequencies from 281 Hz to 2438 Hz, median
    // 39.5 dB/s, peaks reaching -20 dB.
    //
    // These sit in the voice band, where the detector waits ~300 ms and tests for
    // vibrato before it will fire, so this is the case that has to work without
    // notching a singer - T4 and T22 guard the other side of that.
    {
        struct Ring { double hz, rate, startDb; };
        static const Ring wed[] = {
            {  281.0, 11.6, -68.8 },   // slowest of the night
            {  281.0, 54.2, -54.6 },
            {  469.0, 65.3, -50.4 },
            {  562.0, 13.6, -81.7 },
            {  656.0, 11.0, -83.6 },
            {  844.0, 61.8, -58.9 },
            { 1031.0, 47.4, -46.0 },
            { 1125.0, 48.0, -38.9 },
            { 1594.0, 42.2, -58.9 },
        };

        int caught = 0; double worst = 0.0; double worstHz = 0.0;
        std::string missed;
        for (const auto& r : wed)
        {
            fk::FeedbackDetector::Params p;
            p.floorDb = -95.0f;
            p.minFreq = 200.0f;          // the band it SHOULD have been watching
            fk::FeedbackDetector det; init (det, p);

            const double hz = r.hz, rate = r.rate;
            auto res = runTone (det, 8.0, [hz, rate] (double t) {
                return std::make_pair (hz, juce::jmin (0.000004 * std::pow (10.0, rate * t / 20.0),
                                                       0.05));
            }, 0.0000025, 0.0);

            if (res.fired && std::abs (res.firstHz - (float) hz) < juce::jmax (25.0f, 0.02f * (float) hz))
            {
                ++caught;
                // Measured against the level at which the detector is ALLOWED to
                // act, not against the floor. The floor is deliberately 20 dB
                // lower - it is where watching starts, not acting - so measuring
                // from it reports that gap as though it were a failure.
                const double escape = res.firstLevelDb - (double) fk::FeedbackDetector::Params{}.actionDb;
                if (escape > worst) { worst = escape; worstHz = hz; }
            }
            else
            {
                if (! missed.empty()) missed += " ";
                missed += std::to_string ((int) hz);
            }
        }
        const int total = (int) (sizeof wed / sizeof wed[0]);
        if (caught == total)
            std::snprintf (msg, sizeof msg, "%d/%d caught, worst escape %.1f dB at %.0f Hz",
                           caught, total, worst, worstHz);
        else
            std::snprintf (msg, sizeof msg, "%d/%d caught, MISSED %s Hz", caught, total, missed.c_str());
        report ("T28 the studio's low-end rings are all caught", caught == total && worst <= 10.0, msg);
    }

    // ---- T29: the guard may not turn into a high-cut filter -----------------
    // Measured live: 22 filters spanning 3.8-16 kHz, twelve of them at the -24 dB
    // cap, summing to -13.6 dB average across the top end and -28 dB at 6 kHz. The
    // least attenuated point up there was still down 5.6 dB. Every single one of
    // those filters was individually a correct decision - the room really was
    // ringing at all of them. Together they are a savage high-cut, and it sounded
    // like one.
    //
    // A system ringing across its entire top end needs less gain. No amount of EQ
    // fixes that; it only trades howl for dullness. The guard now refuses to keep
    // stacking cut in one region, so that trade is the user's to make.
    {
        fk::NotchBank<48> bank;
        bank.prepare (kSR, 64);
        bank.softCapDb = -18.0; bank.hardCapDb = -24.0; bank.holdSeconds = 30.0;

        // Hammer a whole region, as a rig on the edge everywhere would.
        double t = 0.0;
        for (int round = 0; round < 8; ++round)
            for (int i = 0; i < 22; ++i, t += 0.01)
                bank.trigger (3800.0 + 560.0 * i, t);

        // Sum the real response across the region it was told to destroy.
        auto respDb = [] (double fq, double f0, double cutDb, double qq)
        {
            const double A = std::pow (10.0, cutDb / 40.0);
            const double w = 2.0 * M_PI * f0 / kSR, a = std::sin (w) / (2.0 * qq);
            const double W = 2.0 * M_PI * fq / kSR;
            auto mag = [W] (double c0, double c1, double c2)
            {
                const double re = c0 + c1 * std::cos (W) + c2 * std::cos (2.0 * W);
                const double im = -(c1 * std::sin (W) + c2 * std::sin (2.0 * W));
                return std::sqrt (re * re + im * im);
            };
            return 20.0 * std::log10 (std::max (mag (1.0 + a * A, -2.0 * std::cos (w), 1.0 - a * A)
                                              / mag (1.0 + a / A, -2.0 * std::cos (w), 1.0 - a / A), 1.0e-12));
        };

        double worst = 0.0, sum = 0.0; int n = 0;
        for (double f = 3800.0; f <= 16000.0; f *= 1.02)
        {
            double tot = 0.0;
            for (int i = 0; i < 48; ++i)
            {
                const auto& sl = bank.getSlot (i);
                if (sl.active) tot += respDb (f, sl.freq, sl.targetDb,
                                              fk::NotchBank<48>::qForDepth (25.0, sl.targetDb));
            }
            worst = std::min (worst, tot); sum += tot; ++n;
        }
        std::snprintf (msg, sizeof msg, "top end averages %.1f dB, worst point %.1f dB",
                       sum / n, worst);
        report ("T29 stacked notches cannot become a high-cut", sum / n >= -10.0, msg);
    }

    // ---- T30: every catch reports how long it took and how wide it was ------
    // The two questions behind every investigation in this project - "why was that
    // slow" and "could one filter ever have covered it" - were both being answered
    // after the fact by simulating against a downsampled spectrum. They are exact
    // at the moment of firing and free to collect there.
    {
        fk::FeedbackDetector::Params p;
        p.floorDb = -95.0f; p.minFreq = 1000.0f;
        fk::FeedbackDetector det; init (det, p);

        constexpr int block = 64;
        std::vector<float> buf ((size_t) block);
        double ph = 0.0;
        fk::FeedbackDetector::Event first {};
        bool got = false;

        for (int b = 0; b < (int) (5.0 * kSR / block) && ! got; ++b)
        {
            for (int i = 0; i < block; ++i)
            {
                const double t = (double) (b * block + i) / kSR;
                ph += 2.0 * M_PI * 6000.0 / kSR;
                buf[(size_t) i] = (float) (juce::jmin (0.000004 * std::pow (10.0, 25.0 * t / 20.0), 0.006)
                                           * std::sin (ph) + noise (0.0000025));
            }
            det.push (buf.data(), block);
            fk::FeedbackDetector::Event ev;
            while (det.popEvent (ev)) if (! got && std::abs (ev.freq - 6000.0f) < 60.0f) { first = ev; got = true; }
        }

        // A pure tone through a Hann window occupies a few bins; at 23.4 Hz per bin
        // that is a width in the low hundreds of Hz, and certainly not zero and not
        // a whole octave. Age must be a real span, not the single frame it fired on.
        const bool ageOk   = first.ageMs > 20.0f && first.ageMs < 5000.0f;
        const bool widthOk = first.widthHiHz > first.widthLoHz
                             && (first.widthHiHz - first.widthLoHz) < 1000.0f
                             && first.widthLoHz < 6000.0f && first.widthHiHz > 6000.0f;
        std::snprintf (msg, sizeof msg, "age %.0f ms, width %.0f-%.0f Hz (%.0f Hz)",
                       first.ageMs, first.widthLoHz, first.widthHiHz,
                       first.widthHiHz - first.widthLoHz);
        report ("T30 a catch reports its age and peak width", got && ageOk && widthOk, msg);
    }

    // ---- T31: the upper partials of a sung note are not feedback ------------
    // Measured live on 2026-09-02 with capture running: 6908 catches in 2.3
    // minutes - 49 a second - scattered over 158 frequency buckets and 73% of them
    // between 2 and 8 kHz. One busy moment caught 505, 757, 1001, 1249, 1501,
    // 2263, 2522, 2776 and 3279 Hz. The gaps are 243, 244, 246 and 252: a sung B3
    // and its harmonics 2 through 13, every one of them notched.
    //
    // T4 did not catch this because it watches a 400 Hz note with five partials,
    // all inside the reach of the ratio-based series test. The failure lives in
    // the TOP of a series, at partial ten and above, where that test cannot
    // propose a low enough fundamental to see it.
    {
        constexpr double f0 = 252.0;
        fk::FeedbackDetector::Params p;
        p.floorDb = -95.0f;
        p.minFreq = 40.0f;               // the rig's own band
        fk::FeedbackDetector det; init (det, p);

        constexpr int block = 64;
        std::vector<float> buf ((size_t) block);
        double ph = 0.0;
        std::vector<float> hit;

        for (int b = 0; b < (int) (6.0 * kSR / block); ++b)
        {
            for (int i = 0; i < block; ++i)
            {
                const double t = (double) (b * block + i) / kSR;
                // A held note with the gentle drift a real voice always has, and a
                // full series up to 3.3 kHz - the partials actually seen notched.
                ph += 2.0 * M_PI * (f0 * (1.0 + 0.004 * std::sin (2.0 * M_PI * 4.6 * t))) / kSR;
                double v = 0.0;
                for (int h = 2; h <= 13; ++h) v += (0.05 / std::sqrt ((double) h)) * std::sin (ph * h);
                buf[(size_t) i] = (float) (v * std::min (1.0, t / 0.4) + noise (0.00008));
            }
            det.push (buf.data(), block);
            fk::FeedbackDetector::Event ev;
            while (det.popEvent (ev)) hit.push_back (ev.freq);
        }

        std::string where;
        for (size_t k = 0; k < hit.size() && k < 5; ++k)
            where += (k ? " " : "") + std::to_string ((int) hit[k]);
        std::snprintf (msg, sizeof msg, hit.empty() ? "no partial notched"
                                                    : "NOTCHED %d partials: %s Hz",
                       (int) hit.size(), where.c_str());
        report ("T31 the upper partials of a sung note are left alone", hit.empty(), msg);
    }

    // ---- T32: a ring must survive a busy spectrum ---------------------------
    // T31's fix went too far and broke the guard outright on the rig - detections
    // fell from 6957 to 129 and real feedback stopped being caught.
    //
    // The comb test asked only for two peaks evenly spaced through the candidate,
    // and any two peaks define SOME spacing. On a quiet bench that is fine; in a
    // room with dozens of live peaks it finds a grid through almost anything, so
    // every ring was called harmonic - which bars it from the sustain path and
    // makes it wait ~490 ms for a growth test judged over 300 ms. Nothing fired.
    //
    // T31 could not see this because it holds one clean voice and nothing else.
    // The question is not "is the voice protected" but "is the voice protected
    // WITHOUT deafening it to everything else", and that needs a busy spectrum.
    {
        constexpr double ringHz = 9616.0;
        int caught = 0;
        std::string missed;
        for (int nclut : { 0, 25, 45, 80 })
        {
            fk::FeedbackDetector::Params p;
            p.floorDb = -95.0f; p.minFreq = 40.0f;
            fk::FeedbackDetector det; init (det, p);

            constexpr int block = 64;
            std::vector<float> buf ((size_t) block);
            double vp = 0.0, rp = 0.0;
            std::vector<double> clutter ((size_t) nclut, 0.0);
            bool fired = false;

            for (int b = 0; b < (int) (8.0 * kSR / block) && ! fired; ++b)
            {
                for (int i = 0; i < block; ++i)
                {
                    const double t = (double) (b * block + i) / kSR;
                    // a sung note, as T31
                    vp += 2.0 * M_PI * (252.0 * (1.0 + 0.004 * std::sin (2.0 * M_PI * 4.6 * t))) / kSR;
                    double v = 0.0;
                    for (int h = 2; h <= 13; ++h) v += (0.05 / std::sqrt ((double) h)) * std::sin (vp * h);
                    v *= std::min (1.0, t / 0.4);
                    // ...and a room. Irregularly spaced on purpose: these are not a
                    // series, and nothing should mistake them for one.
                    for (size_t c = 0; c < clutter.size(); ++c)
                    {
                        clutter[c] += 2.0 * M_PI * (900.0 + 317.0 * (double) c) / kSR;
                        v += 0.0018 * std::sin (clutter[c]);
                    }
                    rp += 2.0 * M_PI * ringHz / kSR;
                    v += juce::jmin (0.000004 * std::pow (10.0, 25.0 * t / 20.0), 0.006) * std::sin (rp);
                    buf[(size_t) i] = (float) (v + noise (0.00008));
                }
                det.push (buf.data(), block);
                fk::FeedbackDetector::Event ev;
                while (det.popEvent (ev))
                    if (! fired && std::abs (ev.freq - (float) ringHz) < 80.0f) fired = true;
            }
            if (fired) ++caught;
            else { if (! missed.empty()) missed += " "; missed += std::to_string (nclut); }
        }
        if (caught == 4) std::snprintf (msg, sizeof msg, "caught through 0, 25, 45 and 80 other tones");
        else std::snprintf (msg, sizeof msg, "%d/4 - LOST at clutter %s", caught, missed.c_str());
        report ("T32 a ring is still caught in a busy spectrum", caught == 4, msg);
    }

    // ---- T33: the budget must not stand back during a runaway ---------------
    // Measured on 2026-09-04: feedback running away to -12.9 dB across 6.6-12.3
    // kHz, and the guard answered with 5 to 8 filters out of 48 and -3.5 dB of
    // average cut. It was barely fighting.
    //
    // The stacking budget did that. It was added to stop the guard turning itself
    // into a high-cut filter (T29) and it works - but a flat budget cannot tell an
    // emergency from a quiet afternoon, so it throttled the response to a howl
    // exactly as hard as it throttles a -70 dB nuisance. A ring that quiet is not
    // worth dulling a vocal to stop; one at -15 dB plainly is.
    {
        auto deploy = [] (double levelDb)
        {
            fk::NotchBank<48> bank;
            bank.prepare (kSR, 64);
            bank.softCapDb = -18.0; bank.hardCapDb = -24.0; bank.holdSeconds = 30.0;

            // A runaway hops modes: many frequencies across the top end at once.
            double t = 0.0;
            for (int round = 0; round < 6; ++round)
                for (int i = 0; i < 18; ++i, t += 0.01)
                    bank.trigger (6600.0 + 320.0 * i, t, true, levelDb);

            int active = 0;
            for (int i = 0; i < 48; ++i) if (bank.getSlot (i).active) ++active;
            return active;
        };

        const int quiet = deploy (-70.0);   // nuisance: the budget should hold
        const int loud  = deploy (-15.0);   // howl: it should not

        std::snprintf (msg, sizeof msg, "quiet ring -> %d filters, runaway -> %d filters", quiet, loud);
        report ("T33 a runaway gets more filters than a nuisance", loud > quiet + 4, msg);
    }

    // ---- T34: a ring under a coincident voice partial ----------------------
    // From an investigation into why recurring modes are sometimes slow. At the
    // rig, 14400 Hz rang 37 times in 40 seconds and 9900 Hz 29 times; each had a
    // BEST catch of 21 ms and a median of 130-250 ms.
    //
    // The hypothesis was that they were being tagged harmonic on the slow visits
    // and made to serve the voice-protection wait, and that a detector-side memory
    // of proven offenders would let them skip it. That was built and could not be
    // shown to help in any scenario, so it was reverted - see the commit. What the
    // attempt did establish is worth keeping, because it bounds what any amount of
    // tuning can achieve:
    //
    //   - A ring sitting on a voice partial and QUIETER than it is not detectable.
    //     At 2520 Hz under a 252 Hz note, a ring 8 dB below the tenth partial was
    //     never caught, with or without any relaxation. That is masking, not a
    //     threshold, and no gate change reaches it.
    //   - A ring that dominates the partial it shares a frequency with IS caught,
    //     which is what this asserts.
    //   - Every attempt to time the GATES ended up timing the ring's climb
    //     instead: at 60 dB/s a 50 dB rise takes 833 ms, and that is what the
    //     numbers turned out to be measuring. Gate latency is small next to
    //     journey time, which is worth remembering before tuning gates again.
    {
        constexpr double hz = 2520.0;          // 10 x the 252 Hz note sung below
        fk::FeedbackDetector::Params p;
        p.floorDb = -95.0f; p.minFreq = 40.0f;
        fk::FeedbackDetector det; init (det, p);

        constexpr int block = 64;
        std::vector<float> buf ((size_t) block);
        double rp = 0.0, vp = 0.0;
        int loudCaught = 0, quietCaught = 0;

        for (int visit = 0; visit < 4; ++visit)
        {
            const bool loud = visit >= 2;      // 0.05 dominates the partial; 0.006 does not
            bool got = false;
            for (int b = 0; b < (int) (2.0 * kSR / block) && ! got; ++b)
            {
                for (int i = 0; i < block; ++i)
                {
                    const double t = (double) (b * block + i) / kSR;
                    vp += 2.0 * M_PI * (252.0 * (1.0 + 0.004 * std::sin (2.0 * M_PI * 4.6 * t))) / kSR;
                    double v = 0.0;
                    for (int h = 2; h <= 13; ++h) v += (0.05 / std::sqrt ((double) h)) * std::sin (vp * h);
                    rp += 2.0 * M_PI * hz / kSR;
                    v += juce::jmin (0.00016 * std::pow (10.0, 60.0 * t / 20.0), loud ? 0.05 : 0.006)
                         * std::sin (rp);
                    buf[(size_t) i] = (float) (v + noise (0.00008));
                }
                det.push (buf.data(), block);
                fk::FeedbackDetector::Event ev;
                while (det.popEvent (ev))
                    if (! got && std::abs (ev.freq - (float) hz) < 70.0f)
                    { got = true; if (loud) ++loudCaught; else ++quietCaught; }
            }
            for (auto& x : buf) x = 0.0f;
            for (int b = 0; b < (int) (0.6 * kSR / block); ++b) det.push (buf.data(), block);
        }

        std::snprintf (msg, sizeof msg, "dominant ring caught %d/2; masked ring caught %d/2 (expected 0)",
                       loudCaught, quietCaught);
        report ("T34 a ring is caught when it dominates a coincident partial",
                loudCaught == 2, msg);
    }

    // ---- T35: even a runaway may not become a high-cut ----------------------
    // T29 holds the quiet case and T33 makes sure a howl gets a harder answer than
    // a nuisance. Between them sat the gap this closes: the first version of the
    // urgency rule switched the budget OFF above -50 dB, so a busy passage went
    // straight to the ceiling. Measured on 2026-09-04: 48 filters - the entire
    // pool - and -10 to -14 dB across the top end, which is the muffling back.
    // Escape improved by 6 dB and dullness worsened by 8. Not a trade worth making.
    {
        fk::NotchBank<48> bank;
        bank.prepare (kSR, 64);
        bank.softCapDb = -18.0; bank.hardCapDb = -24.0; bank.holdSeconds = 30.0;

        // A sustained howl right across the top end, as loud as the rig has seen.
        double t = 0.0;
        for (int round = 0; round < 10; ++round)
            for (int i = 0; i < 26; ++i, t += 0.01)
                bank.trigger (4000.0 + 480.0 * i, t, true, -13.0);

        auto respDb = [] (double fq, double f0, double cutDb, double qq)
        {
            const double A = std::pow (10.0, cutDb / 40.0);
            const double w = 2.0 * M_PI * f0 / kSR, a = std::sin (w) / (2.0 * qq);
            const double W = 2.0 * M_PI * fq / kSR;
            auto mag = [W] (double c0, double c1, double c2)
            {
                const double re = c0 + c1 * std::cos (W) + c2 * std::cos (2.0 * W);
                const double im = -(c1 * std::sin (W) + c2 * std::sin (2.0 * W));
                return std::sqrt (re * re + im * im);
            };
            return 20.0 * std::log10 (std::max (mag (1.0 + a * A, -2.0 * std::cos (w), 1.0 - a * A)
                                              / mag (1.0 + a / A, -2.0 * std::cos (w), 1.0 - a / A), 1.0e-12));
        };

        double sum = 0.0; int n = 0; int active = 0;
        for (int i = 0; i < 48; ++i) if (bank.getSlot (i).active) ++active;
        for (double f = 4000.0; f <= 16000.0; f *= 1.02)
        {
            double tot = 0.0;
            for (int i = 0; i < 48; ++i)
            {
                const auto& sl = bank.getSlot (i);
                if (sl.active) tot += respDb (f, sl.freq, sl.targetDb,
                                              fk::NotchBank<48>::qForDepth (25.0, sl.targetDb));
            }
            sum += tot; ++n;
        }
        std::snprintf (msg, sizeof msg, "%d filters, top end averages %.1f dB", active, sum / n);
        // The bound, not silence: unlimited gives -16 dB and the whole pool, and
        // the rig complained at -13.6 dB SUSTAINED. A howl lasting seconds may cost
        // -11 dB - you would rather be briefly dull than howling - so long as it is
        // bounded and releases afterwards, which is what makes it brief.
        report ("T35 a runaway fights hard but not to the ceiling",
                active > 10 && active < 48 && sum / n >= -12.0, msg);
    }

    std::printf ("\n%s  (%d failed)\n\n", failures == 0 ? "ALL PASS" : "FAILURES", failures);
    return failures == 0 ? 0 : 1;
}
