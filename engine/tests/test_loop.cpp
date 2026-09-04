// ============================================================================
// test_loop.cpp — closed-loop feedback simulation.
//
// Every other test in this suite is OPEN loop: inject a tone, ask whether the
// detector found it. Not one of them closes the loop, so not one can answer the
// only question that has mattered for the last week - does putting a notch on a
// ring actually stop it? That is why every attempt to fix the notch bank has had
// to be validated on a stage, and why three consecutive theories about it were
// wrong.
//
// This runs the real detector and the real notch bank inside an actual feedback
// loop:
//
//     mic[n]      = source[n] + (feedback path applied to speaker history)
//     detector.push(mic)                     <- analyses pre-notch, as the engine does
//     out[n]      = notches applied to mic
//     speaker[n]  = G * out[n]
//
// which is literally H = G/(1-G*F). Sweeping G finds the Maximum Stable Gain,
// and the difference between MSG with the guard on and off is Added Stable Gain -
// the standard figure of merit for a feedback suppressor, and a number this
// project has never had.
//
//   cmake --build build --target fk-loop && ./build/fk-loop_artefacts/Release/fk-loop
// ============================================================================

#include <juce_dsp/juce_dsp.h>
#include <cstdio>
#include <cmath>
#include <random>
#include <vector>
#include <array>
#include "../Source/FeedbackDetector.h"
#include "../Source/NotchBank.h"

namespace
{
constexpr double kSR = 48000.0;

std::mt19937 rng { 20260904 };
double noise (double amp) { return amp * std::uniform_real_distribution<double> (-1.0, 1.0) (rng); }

/// The loudspeaker -> room -> microphone path.
///
/// A handful of delayed reflections rather than a measured RIR: what matters for
/// feedback is the comb the delays produce, and a sparse path gives a comb whose
/// spacing is known exactly, so the simulation can be checked against theory
/// (peaks every 1/tau Hz). The first arrival is set from the rig's own measured
/// mode spacing - a median 179 Hz between adjacent ring frequencies implies a
/// loop of about 5.6 ms, which is roughly two metres of air.
struct FeedbackPath
{
    struct Tap { int delay; double gain; };
    std::vector<Tap> taps;
    std::vector<double> history;
    size_t writePos = 0;

    void prepare (double firstMs)
    {
        const int d0 = (int) std::lround (firstMs * 0.001 * kSR);
        taps = {
            { d0,                        1.00 },
            { (int) (d0 * 1.7) + 31,    -0.55 },
            { (int) (d0 * 2.6) + 67,     0.34 },
            { (int) (d0 * 4.1) + 113,   -0.21 },
            { (int) (d0 * 6.3) + 191,    0.13 },
        };
        int longest = 0;
        for (auto& t : taps) longest = std::max (longest, t.delay);
        history.assign ((size_t) longest + 8, 0.0);
        writePos = 0;
    }

    void push (double x) noexcept
    {
        history[writePos] = x;
        writePos = (writePos + 1) % history.size();
    }

    double read() const noexcept
    {
        double sum = 0.0;
        for (const auto& t : taps)
        {
            const size_t idx = (writePos + history.size() - (size_t) t.delay) % history.size();
            sum += t.gain * history[idx];
        }
        return sum;
    }
};

struct Run
{
    bool   blewUp   = false;    // the loop ran away
    double peakDb   = -200.0;   // loudest the microphone saw
    int    notches  = 0;        // filters the guard deployed
    double seconds  = 0.0;      // when it blew up, if it did
};

/// One pass of the loop at a fixed gain.
///
/// `guard` selects whether the detector and notch bank are in circuit. With it
/// off this measures the bare system's MSG; with it on, the improvement.
struct Tuning { double initialCut = -12.0, softCap = -18.0, hardCap = -24.0, q = 25.0; };
Tuning tuning;

Run runLoop (double gainDb, bool guard, double firstMs, double seconds = 6.0)
{
    FeedbackPath path;
    path.prepare (firstMs);

    fk::FeedbackDetector det;
    fk::FeedbackDetector::Params p;
    p.floorDb = -95.0f;
    p.minFreq = 200.0f;
    det.prepare (kSR);
    det.setParams (p);

    fk::NotchBank<48> bank;
    bank.prepare (kSR, 64);
    bank.initialCutDb   = tuning.initialCut;
    bank.fastTrackCutDb = tuning.initialCut - 3.0;
    bank.softCapDb      = tuning.softCap;
    bank.hardCapDb      = tuning.hardCap;
    bank.defaultQ       = tuning.q;
    bank.holdSeconds    = 4.0;

    const double G = std::pow (10.0, gainDb / 20.0);
    constexpr int block = 64;
    std::vector<float> buf ((size_t) block);

    Run r;
    double elapsed = 0.0;
    const int totalBlocks = (int) (seconds * kSR / block);

    for (int b = 0; b < totalBlocks; ++b)
    {
        for (int i = 0; i < block; ++i)
        {
            // A quiet, broadband excitation standing in for a talker: the loop
            // needs something to amplify, and noise excites every mode equally so
            // the simulation does not pick the ringing frequency for us.
            const double src = noise (0.0006);
            const double mic = src + path.read();
            buf[(size_t) i] = (float) mic;

            const double a = std::abs (mic);
            if (a > 1.0e-9) r.peakDb = std::max (r.peakDb, 20.0 * std::log10 (a));

            // Runaway: stop before the doubles overflow and take the run with them.
            if (! std::isfinite (mic) || a > 50.0)
            {
                r.blewUp = true;
                r.seconds = elapsed + (double) (b * block + i) / kSR;
                for (int s = 0; s < 48; ++s) if (bank.getSlot (s).active) ++r.notches;
                return r;
            }
        }

        if (guard)
        {
            det.push (buf.data(), block);           // pre-notch, as the engine does

            fk::FeedbackDetector::Event ev;
            while (det.popEvent (ev))
                bank.trigger (ev.freq, elapsed, ev.growing, ev.levelDb);

            bank.process (buf.data(), block, false);
        }

        // The amplified result goes back into the room.
        for (int i = 0; i < block; ++i) path.push (G * (double) buf[(size_t) i]);

        elapsed += (double) block / kSR;
        if (guard) bank.release (elapsed);
    }

    for (int s = 0; s < 48; ++s) if (bank.getSlot (s).active) ++r.notches;
    r.seconds = seconds;
    return r;
}

/// Maximum Stable Gain: the highest gain at which the loop does not run away.
double findMsg (bool guard, double firstMs, double lo = -40.0, double hi = 20.0)
{
    // Bisection. The loop is monotonic in gain - more gain never becomes more
    // stable - so this converges on the threshold.
    for (int i = 0; i < 9; ++i)
    {
        const double mid = 0.5 * (lo + hi);
        if (runLoop (mid, guard, firstMs).blewUp) hi = mid;
        else                                      lo = mid;
    }
    return lo;
}
}

/// Does the simulation obey the physics? Growth rate in dB/s should equal excess
/// loop gain divided by loop delay (van Waterschoot & Moonen; RaneNote 158). If
/// this does not hold, nothing else the simulator says is worth reading.
void validate (double firstMs)
{
    FeedbackPath path;
    path.prepare (firstMs);

    // Just past the threshold, guard off, and watch the envelope climb.
    const double msg = findMsg (false, firstMs);
    std::printf ("  loop delay %.1f ms, MSG %.2f dB\n", firstMs, msg);
    std::printf ("  %10s %14s %14s %9s\n", "excess", "predicted", "measured", "error");

    for (double excess : { 0.25, 0.5, 1.0, 2.0 })
    {
        path.prepare (firstMs);
        const double G = std::pow (10.0, (msg + excess) / 20.0);
        constexpr int block = 64;
        std::vector<float> buf ((size_t) block);

        // Envelope, sampled every block, in dB.
        std::vector<double> env;
        for (int b = 0; b < (int) (4.0 * kSR / block); ++b)
        {
            double pk = 0.0;
            for (int i = 0; i < block; ++i)
            {
                const double mic = noise (0.0006) + path.read();
                buf[(size_t) i] = (float) mic;
                pk = std::max (pk, std::abs (mic));
                if (! std::isfinite (mic) || std::abs (mic) > 50.0) { pk = 0.0; break; }
            }
            if (pk <= 0.0) break;
            env.push_back (20.0 * std::log10 (pk));
            for (int i = 0; i < block; ++i) path.push (G * (double) buf[(size_t) i]);
        }

        // Fit over a clean window of the climb, in dB rather than in time: from
        // -55 dB up to -15 dB. Fitting "the second half of whatever was collected"
        // was wrong at high excess gain, where the run reaches the runaway clamp
        // and the last samples flatten against it - which showed up as the error
        // growing with excess (22% to 50%) instead of staying constant.
        auto firstAbove = [&env] (double db) -> long
        {
            for (size_t i = 0; i < env.size(); ++i) if (env[i] >= db) return (long) i;
            return -1;
        };
        const long a = firstAbove (-55.0), b2 = firstAbove (-15.0);
        if (a < 0 || b2 <= a) { std::printf ("  %9.2f dB   (no clean climb to fit)\n", excess); continue; }
        const double dt = (double) (b2 - a) * block / kSR;
        const double measured = (env[(size_t) b2] - env[(size_t) a]) / dt;
        const double predicted = excess / (firstMs * 0.001);
        std::printf ("  %9.2f dB %11.0f dB/s %11.0f dB/s %8.0f%%\n",
                     excess, predicted, measured,
                     100.0 * std::abs (measured - predicted) / predicted);
    }
}

int main()
{
    std::printf ("\nClosed-loop feedback simulation\n");
    std::printf ("===============================\n\n");
    std::printf ("Validating against theory: growth rate = excess gain / loop delay\n");
    validate (5.6);
    std::printf ("\n");
    std::printf ("Real detector and real notch bank inside an actual feedback loop.\n"
                 "Feedback path: sparse reflections, first arrival as given.\n\n");

    std::printf ("%10s %14s %14s %10s %10s\n",
                 "loop delay", "MSG guard off", "MSG guard on", "ASG", "filters");

    double bestAsg = -100.0;
    for (double firstMs : { 3.0, 5.6, 10.0, 20.0 })
    {
        const double off = findMsg (false, firstMs);
        const double on  = findMsg (true,  firstMs);
        const auto   at  = runLoop (on, true, firstMs);
        std::printf ("%8.1f ms %11.1f dB %11.1f dB %7.1f dB %8d\n",
                     firstMs, off, on, on - off, at.notches);
        bestAsg = std::max (bestAsg, on - off);
    }

    // The physics says the excess loop gain is a fraction of a decibel, so the
    // 24 dB cuts this ships with are two orders of magnitude past what is needed.
    // That has never been testable before - a stage cannot be swept.
    std::printf ("\n%-28s %9s %9s\n", "notch depth / Q", "ASG", "filters");
    for (auto t : { Tuning{ -12, -18, -24, 25 },
                    Tuning{  -8, -12, -16, 25 },
                    Tuning{  -6,  -9, -12, 25 },
                    Tuning{  -3,  -6,  -9, 25 },
                    Tuning{  -6,  -9, -12, 12 },
                    Tuning{  -3,  -6,  -9, 12 },
                    Tuning{  -2,  -4,  -6,  8 } })
    {
        tuning = t;
        const double off = findMsg (false, 5.6);
        const double on  = findMsg (true,  5.6);
        const auto   at  = runLoop (on, true, 5.6);
        char label[64];
        std::snprintf (label, sizeof label, "open %.0f, cap %.0f, Q %.0f",
                       t.initialCut, t.hardCap, t.q);
        std::printf ("%-28s %6.1f dB %9d\n", label, on - off, at.notches);
    }
    tuning = Tuning{};

    std::printf ("\nFor reference: Sabine claim 6-9 dB typical; frequency shifting buys\n"
                 "2-6 dB; hearing-aid adaptive cancellation exceeds 10 dB. Under 3 dB and\n"
                 "the honest answer is acoustics and gain structure, not software.\n");

    std::printf ("\n%s\n\n", bestAsg >= 3.0 ? "ASG is worth having." : "ASG IS TOO SMALL TO MATTER.");
    return 0;
}
