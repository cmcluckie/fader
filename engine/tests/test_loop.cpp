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
/// A shoebox room, modelled the way the physics divides.
///
/// Above the Schroeder frequency the field is dense and geometric acoustics is
/// valid, so the loudspeaker-to-microphone path is built by the image-source
/// method (Allen & Berkley 1979). Below it the field is a handful of sparse,
/// high-Q axial and tangential modes, and image-source is simply the wrong model -
/// so those are added explicitly as resonators derived from the room dimensions.
///
/// Both halves come from geometry and absorption, not from taste. That is the
/// whole point: a path invented to be convenient proves nothing about a room.
struct Room
{
    const char* name = "";
    double L = 3.0, W = 3.0, H = 3.0;      // metres
    double alpha = 0.02;                    // mean absorption coefficient
    double spk[3] = { 0.6, 0.6, 1.2 };      // wedge monitor
    double mic[3] = { 1.1, 0.6, 1.4 };      // vocal mic, 0.5 m away

    double volume()  const { return L * W * H; }
    double surface() const { return 2.0 * (L * W + L * H + W * H); }
    /// Sabine. Crude, but it is the figure the Schroeder expression expects.
    double rt60()    const { return 0.161 * volume() / (surface() * alpha); }
    double schroeder() const { return 2000.0 * std::sqrt (rt60() / volume()); }
    double micDistance() const
    {
        double d = 0.0;
        for (int i = 0; i < 3; ++i) d += (mic[i] - spk[i]) * (mic[i] - spk[i]);
        return std::sqrt (d);
    }

    /// Axial, tangential and oblique modes below `upTo`, with their Q from RT60.
    /// f = (c/2)*sqrt((nx/L)^2 + (ny/W)^2 + (nz/H)^2).
    struct Mode { double hz, q, gain; };
    std::vector<Mode> modes (double upTo) const
    {
        constexpr double c = 343.0;
        std::vector<Mode> out;
        const double t60 = rt60();
        for (int nx = 0; nx <= 8; ++nx)
        for (int ny = 0; ny <= 8; ++ny)
        for (int nz = 0; nz <= 8; ++nz)
        {
            if (nx + ny + nz == 0) continue;
            const double f = 0.5 * c * std::sqrt ((nx / L) * (nx / L)
                                                + (ny / W) * (ny / W)
                                                + (nz / H) * (nz / H));
            if (f < 20.0 || f > upTo) continue;
            // Q = pi*f*T60/ln(1000): a mode decaying at the room's rate.
            const double q = juce::MathConstants<double>::pi * f * t60 / std::log (1000.0);
            // Axial modes carry the most energy, oblique the least.
            const int order = (nx > 0) + (ny > 0) + (nz > 0);
            const double gain = order == 1 ? 1.0 : order == 2 ? 0.5 : 0.25;
            out.push_back ({ f, juce::jlimit (2.0, 400.0, q), gain });
        }
        std::sort (out.begin(), out.end(), [] (auto& a, auto& b) { return a.gain > b.gain; });
        if (out.size() > 24) out.resize (24);
        return out;
    }

    /// Image-source taps: delay in samples and pressure gain, strongest first.
    std::vector<std::pair<int,double>> images (int maxTaps, double maxSeconds) const
    {
        constexpr double c = 343.0;
        const double beta = std::sqrt (1.0 - alpha);
        std::vector<std::pair<int,double>> taps;
        const int order = 12;
        for (int mx = -order; mx <= order; ++mx)
        for (int my = -order; my <= order; ++my)
        for (int mz = -order; mz <= order; ++mz)
        for (int px = 0; px < 2; ++px)
        for (int py = 0; py < 2; ++py)
        for (int pz = 0; pz < 2; ++pz)
        {
            const double sx = (1 - 2 * px) * spk[0] + 2 * mx * L;
            const double sy = (1 - 2 * py) * spk[1] + 2 * my * W;
            const double sz = (1 - 2 * pz) * spk[2] + 2 * mz * H;
            const double d = std::sqrt ((sx - mic[0]) * (sx - mic[0])
                                      + (sy - mic[1]) * (sy - mic[1])
                                      + (sz - mic[2]) * (sz - mic[2]));
            const double t = d / c;
            if (t > maxSeconds || d < 0.05) continue;
            const int refl = std::abs (2 * mx - px) + std::abs (2 * my - py) + std::abs (2 * mz - pz);
            const double g = std::pow (beta, refl) / (4.0 * juce::MathConstants<double>::pi * d);
            if (std::abs (g) < 1.0e-6) continue;
            taps.push_back ({ (int) std::lround (t * kSR), g });
        }
        std::sort (taps.begin(), taps.end(),
                   [] (auto& a, auto& b) { return std::abs (a.second) > std::abs (b.second); });
        if ((int) taps.size() > maxTaps) taps.resize ((size_t) maxTaps);
        return taps;
    }
};

struct FeedbackPath
{
    struct Tap { int delay; double gain; };
    std::vector<Tap> taps;
    std::vector<double> history;
    size_t writePos = 0;

    // A resonance in the path, which is what decides WHERE a room rings.
    //
    // Delays alone give a flat comb: every peak is equally likely, so the loop
    // picks a frequency essentially at random and the simulation cannot be aimed.
    // Real rooms are not flat. Below the Schroeder frequency the field is a few
    // sparse high-Q axial modes; above it, the horn and driver response dominate.
    // One peaking biquad models either, and it is the knob that lets this rig's
    // own measured ring frequencies be reproduced.
    double b0 = 1, b1 = 0, b2 = 0, a1 = 0, a2 = 0;
    double x1 = 0, x2 = 0, y1 = 0, y2 = 0;

    void resonate (double hz, double gainDb, double q)
    {
        const double A = std::pow (10.0, gainDb / 40.0);
        const double w = 2.0 * juce::MathConstants<double>::pi * hz / kSR;
        const double alpha = std::sin (w) / (2.0 * q);
        const double a0 = 1.0 + alpha / A;
        b0 = (1.0 + alpha * A) / a0;
        b1 = (-2.0 * std::cos (w))   / a0;
        b2 = (1.0 - alpha * A) / a0;
        a1 = (-2.0 * std::cos (w))   / a0;
        a2 = (1.0 - alpha / A) / a0;
        x1 = x2 = y1 = y2 = 0.0;
    }

    double shape (double x) noexcept
    {
        const double y = b0 * x + b1 * x1 + b2 * x2 - a1 * y1 - a2 * y2;
        x2 = x1; x1 = x; y2 = y1; y1 = y;
        return y;
    }

    /// Aim the path so a 0-degree phase point lands on `aimHz`.
    ///
    /// Magnitude alone cannot steer a feedback loop - it rings only where the gain
    /// condition AND the phase condition are met at once. Boosting a frequency
    /// whose loop phase is -177 degrees achieves nothing, which is exactly what
    /// the first version of this did: every aim rang at 141 Hz because the comb's
    /// own peaks won regardless of where the resonance was put.
    ///
    /// For a dominant first tap the phase is -2.pi.f.d/fs, so it passes through
    /// zero when f.d/fs is a whole number. Round the delay to the nearest such
    /// value near the wanted length, and keep the later taps small so they colour
    /// the comb without moving the phase much.
    /// `extra` is any delay elsewhere in the loop - notably the processing block,
    /// which is part of the round trip and therefore part of the phase. Leaving it
    /// out was a real error: the first version aimed the acoustic path alone, and
    /// with a 64-sample block unaccounted for the phase at the target was nowhere
    /// near zero, so the loop rang wherever it liked.
    static int delayFor (double aimHz, double approxMs, int extra = 0)
    {
        const double want = approxMs * 0.001 * kSR;
        const double period = kSR / aimHz;                 // samples per cycle
        const int k = std::max (2, (int) std::lround ((want + extra) / period));
        return std::max (8, (int) std::lround (k * period) - extra);
    }

    void prepareAimed (double firstMs, double aimHz, int extra)
    {
        prepare (firstMs, 0.0);
        const int d0 = delayFor (aimHz, firstMs, extra);
        taps = {
            { d0,                        1.00 },
            { (int) (d0 * 1.7) + 31,    -0.28 },
            { (int) (d0 * 2.6) + 67,     0.17 },
            { (int) (d0 * 4.1) + 113,   -0.10 },
            { (int) (d0 * 6.3) + 191,    0.06 },
        };
        int longest = 0;
        for (auto& t : taps) longest = std::max (longest, t.delay);
        history.assign ((size_t) longest + 8, 0.0);
        writePos = 0;
        x1 = x2 = y1 = y2 = 0.0;
    }

    void prepare (double firstMs, double aimHz = 0.0)
    {
        const int d0 = aimHz > 0.0 ? delayFor (aimHz, firstMs)
                                   : (int) std::lround (firstMs * 0.001 * kSR);
        taps = {
            { d0,                        1.00 },
            { (int) (d0 * 1.7) + 31,    -0.28 },
            { (int) (d0 * 2.6) + 67,     0.17 },
            { (int) (d0 * 4.1) + 113,   -0.10 },
            { (int) (d0 * 6.3) + 191,    0.06 },
        };
        int longest = 0;
        for (auto& t : taps) longest = std::max (longest, t.delay);
        history.assign ((size_t) longest + 8, 0.0);
        writePos = 0;
        x1 = x2 = y1 = y2 = 0.0;
    }

    void push (double x) noexcept
    {
        history[writePos] = x;
        writePos = (writePos + 1) % history.size();
    }

    double read() noexcept
    {
        double sum = 0.0;
        for (const auto& t : taps)
        {
            const size_t idx = (writePos + history.size() - (size_t) t.delay) % history.size();
            sum += t.gain * history[idx];
        }
        return shape (sum);
    }
};


/// A feedback path built from a room rather than invented.
struct RoomPath
{
    std::vector<std::pair<int,double>> taps;
    std::vector<double> history;
    size_t writePos = 0;

    // One biquad resonator per room mode, in parallel with the image field.
    struct Res { double b0,b1,b2,a1,a2, x1,x2,y1,y2, gain; };
    std::vector<Res> res;

    /// The transducers, which are part of the loop and were missing.
    ///
    /// Image-source gains are all positive - beta^n / 4.pi.d, no sign inversion -
    /// so several hundred taps sum to an enormous gain at DC, and the simulated
    /// loop went unstable at 23 Hz in every room before any acoustic mode got
    /// started. Real rigs do not do this because a wedge does not reproduce DC and
    /// neither does a vocal microphone. Modelling the room and forgetting the
    /// transducers is modelling half the loop.
    ///
    /// Second-order high pass at 70 Hz (wedge) and low pass at 16 kHz (driver
    /// plus mic), which is a fair caricature of the pair in series.
    struct Biquad
    {
        double b0=1,b1=0,b2=0,a1=0,a2=0,x1=0,x2=0,y1=0,y2=0;
        double run (double x) noexcept
        {
            const double y = b0*x + b1*x1 + b2*x2 - a1*y1 - a2*y2;
            x2=x1; x1=x; y2=y1; y1=y; return y;
        }
        void highpass (double hz, double q)
        {
            const double w = 2.0*juce::MathConstants<double>::pi*hz/kSR;
            const double al = std::sin(w)/(2.0*q), c = std::cos(w), a0 = 1.0+al;
            b0=(1.0+c)/2.0/a0; b1=-(1.0+c)/a0; b2=(1.0+c)/2.0/a0;
            a1=(-2.0*c)/a0; a2=(1.0-al)/a0; x1=x2=y1=y2=0.0;
        }
        void lowpass (double hz, double q)
        {
            const double w = 2.0*juce::MathConstants<double>::pi*hz/kSR;
            const double al = std::sin(w)/(2.0*q), c = std::cos(w), a0 = 1.0+al;
            b0=(1.0-c)/2.0/a0; b1=(1.0-c)/a0; b2=(1.0-c)/2.0/a0;
            a1=(-2.0*c)/a0; a2=(1.0-al)/a0; x1=x2=y1=y2=0.0;
        }
    };
    Biquad hp1, hp2, lp;

    void build (const Room& room, int maxTaps = 700)
    {
        taps = room.images (maxTaps, 0.35);
        int longest = 8;
        for (auto& t : taps) longest = std::max (longest, t.first);
        history.assign ((size_t) longest + 8, 0.0);
        writePos = 0;

        // Normalise the direct field so gain sweeps mean the same thing in every
        // room - otherwise a big room just looks stable because it is quiet.
        double peak = 0.0;
        for (auto& t : taps) peak = std::max (peak, std::abs (t.second));
        if (peak > 0.0) for (auto& t : taps) t.second /= peak;

        hp1.highpass (70.0, 0.707);
        hp2.highpass (70.0, 0.707);      // 4th order overall: a wedge really does roll off
        lp.lowpass  (16000.0, 0.707);

        res.clear();
        for (const auto& m : room.modes (room.schroeder()))
        {
            // Constant-Q bandpass at the mode frequency.
            const double w = 2.0 * juce::MathConstants<double>::pi * m.hz / kSR;
            const double alpha = std::sin (w) / (2.0 * m.q);
            const double a0 = 1.0 + alpha;
            Res r {};
            r.b0 = alpha / a0; r.b1 = 0.0; r.b2 = -alpha / a0;
            r.a1 = (-2.0 * std::cos (w)) / a0;
            r.a2 = (1.0 - alpha) / a0;
            r.gain = m.gain;
            res.push_back (r);
        }
    }

    void push (double x) noexcept
    {
        history[writePos] = x;
        writePos = (writePos + 1) % history.size();
    }

    double read() noexcept
    {
        double direct = 0.0;
        for (const auto& t : taps)
        {
            const size_t idx = (writePos + history.size() - (size_t) t.first) % history.size();
            direct += t.second * history[idx];
        }
        // Modal field, driven by the same signal. Below the Schroeder frequency
        // this is the room; the image sum up there is not to be believed.
        const size_t newest = (writePos + history.size() - 1) % history.size();
        const double drive = history[newest];
        double modal = 0.0;
        for (auto& r : res)
        {
            const double y = r.b0 * drive + r.b1 * r.x1 + r.b2 * r.x2 - r.a1 * r.y1 - r.a2 * r.y2;
            r.x2 = r.x1; r.x1 = drive; r.y2 = r.y1; r.y1 = y;
            modal += r.gain * y;
        }
        return lp.run (hp2.run (hp1.run (direct + 0.5 * modal)));
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

/// MSG for a path with a resonance in it.
double findMsgFor (FeedbackPath&, double firstMs, double aimHz, double resDb, double q)
{
    constexpr int block = 64;
    double lo = -60.0, hi = 20.0;
    for (int it = 0; it < 10; ++it)
    {
        const double mid = 0.5 * (lo + hi);
        FeedbackPath p2;
        p2.prepareAimed (firstMs, aimHz, block);
        p2.resonate (aimHz, resDb, q);
        const double G = std::pow (10.0, mid / 20.0);
        std::vector<double> out ((size_t) block, 0.0);
        std::vector<double> in  ((size_t) block, 0.0);
        bool blew = false;
        for (int b = 0; b < (int) (4.0 * kSR / block) && ! blew; ++b)
        {
            for (int i = 0; i < block; ++i)
            {
                const double mic = noise (0.0006) + p2.read();
                if (! std::isfinite (mic) || std::abs (mic) > 50.0) { blew = true; break; }
                in[(size_t) i] = mic;
                // Sample-accurate: what goes back into the room is last block's
                // processed output, pushed one sample at a time. Pushing a whole
                // block after reading it made every read in the block see stale
                // history, which quantised the path to block boundaries and
                // destroyed the phase the aim depends on.
                p2.push (G * out[(size_t) i]);
            }
            out = in;                       // no guard here: this is bare MSG
        }
        if (blew) hi = mid; else lo = mid;
    }
    return lo;
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

/// Where did it actually ring, and how fast did it build?
///
/// The point of calibration: aim the path's resonance at a frequency this rig has
/// really produced and check the loop rings THERE, at a rate its logs really
/// recorded. A simulator that rings wherever it likes cannot be used to test
/// anything, and one that only rings where it is aimed is proof of nothing until
/// the aim is set from measurements rather than convenience.
struct Ring { double hz = 0.0; double rateDbPerSec = 0.0; };

Ring findRing (double firstMs, double aimHz, double resonanceDb, double q, double excessDb)
{
    FeedbackPath path;
    path.prepareAimed (firstMs, aimHz, 64);
    path.resonate (aimHz, resonanceDb, q);

    // Threshold for THIS path, then push just past it.
    const double msg = findMsgFor (path, firstMs, aimHz, resonanceDb, q);
    const double G = std::pow (10.0, (msg + excessDb) / 20.0);

    path.prepareAimed (firstMs, aimHz, 64);
    path.resonate (aimHz, resonanceDb, q);

    constexpr int block = 64;
    constexpr int fftOrder = 13, fftSize = 1 << fftOrder;
    std::vector<float> buf ((size_t) block);
    std::vector<float> tail ((size_t) fftSize * 2, 0.0f);
    size_t tailPos = 0;
    std::vector<double> env;

    std::vector<double> outBlock ((size_t) block, 0.0);
    for (int b = 0; b < (int) (5.0 * kSR / block); ++b)
    {
        double pk = 0.0;
        bool dead = false;
        for (int i = 0; i < block; ++i)
        {
            const double mic = noise (0.0006) + path.read();
            if (! std::isfinite (mic) || std::abs (mic) > 50.0) { dead = true; break; }
            buf[(size_t) i] = (float) mic;
            pk = std::max (pk, std::abs (mic));
            tail[tailPos] = (float) mic;
            tailPos = (tailPos + 1) % (size_t) fftSize;
            path.push (G * outBlock[(size_t) i]);      // sample-accurate, one block late
        }
        if (dead) break;
        if (pk > 0.0) env.push_back (20.0 * std::log10 (pk));
        for (int i = 0; i < block; ++i) outBlock[(size_t) i] = (double) buf[(size_t) i];
    }

    // Which frequency dominates the last window?
    juce::dsp::FFT fft (fftOrder);
    std::vector<float> fd ((size_t) fftSize * 2, 0.0f);
    for (int i = 0; i < fftSize; ++i)
        fd[(size_t) i] = tail[(tailPos + (size_t) i) % (size_t) fftSize]
                         * (0.5f - 0.5f * std::cos (2.0f * (float) M_PI * (float) i / (float) fftSize));
    fft.performFrequencyOnlyForwardTransform (fd.data());

    Ring r;
    float best = 0.0f;
    for (int i = 2; i < fftSize / 2; ++i)
        if (fd[(size_t) i] > best) { best = fd[(size_t) i]; r.hz = i * kSR / fftSize; }

    // ...and how fast it climbed, between fixed dB marks.
    auto firstAbove = [&env] (double db) -> long
    {
        for (size_t i = 0; i < env.size(); ++i) if (env[i] >= db) return (long) i;
        return -1;
    };
    const long a = firstAbove (-55.0), b2 = firstAbove (-15.0);
    if (a >= 0 && b2 > a)
        r.rateDbPerSec = (env[(size_t) b2] - env[(size_t) a])
                         / ((double) (b2 - a) * block / kSR);
    return r;
}


/// Run the loop in a real room. Returns MSG in dB.
int lastFilters = 0, lastEvents = 0; double lastRingHz = 0.0;

double roomMsg (const Room& room, bool guard, double lo = -60.0, double hi = 30.0)
{
    constexpr int block = 64;
    for (int it = 0; it < 10; ++it)
    {
        const double mid = 0.5 * (lo + hi);
        RoomPath path; path.build (room);

        fk::FeedbackDetector det;
        fk::FeedbackDetector::Params p;
        p.floorDb = -95.0f; p.minFreq = 60.0f;
        det.prepare (kSR); det.setParams (p);

        fk::NotchBank<48> bank;
        bank.prepare (kSR, block);
        bank.initialCutDb   = tuning.initialCut;
        bank.fastTrackCutDb = tuning.initialCut - 3.0;
        bank.softCapDb      = tuning.softCap;
        bank.hardCapDb      = tuning.hardCap;
        bank.defaultQ       = tuning.q;
        bank.holdSeconds    = 4.0;

        const double G = std::pow (10.0, mid / 20.0);
        std::vector<float> buf ((size_t) block);
        std::vector<double> out ((size_t) block, 0.0);
        double elapsed = 0.0;
        bool blew = false;

        for (int b = 0; b < (int) (5.0 * kSR / block) && ! blew; ++b)
        {
            for (int i = 0; i < block; ++i)
            {
                const double mic = noise (0.0006) + path.read();
                if (! std::isfinite (mic) || std::abs (mic) > 50.0) { blew = true; break; }
                buf[(size_t) i] = (float) mic;
                path.push (G * out[(size_t) i]);
            }
            if (blew) break;
            if (guard)
            {
                det.push (buf.data(), block);
                fk::FeedbackDetector::Event ev;
                while (det.popEvent (ev))
                {
                    ++lastEvents; lastRingHz = ev.freq;
                    bank.trigger (ev.freq, elapsed, ev.growing, ev.levelDb);
                }
                bank.process (buf.data(), block, false);
            }
            for (int i = 0; i < block; ++i) out[(size_t) i] = (double) buf[(size_t) i];
            elapsed += (double) block / kSR;
            if (guard) bank.release (elapsed);
        }
        if (blew) hi = mid; else lo = mid;
        if (guard)
        {
            lastFilters = 0;
            for (int i = 0; i < 48; ++i) if (bank.getSlot (i).active) ++lastFilters;
        }
    }
    return lo;
}


/// Run one room at a fixed gain and report what the detector actually saw.
void probeRoom (const Room& room, double gainDb, bool guard)
{
    constexpr int block = 64;
    RoomPath path; path.build (room);

    fk::FeedbackDetector det;
    fk::FeedbackDetector::Params p;
    p.floorDb = -95.0f; p.minFreq = 60.0f;
    det.prepare (kSR); det.setParams (p);

    fk::NotchBank<48> bank;
    bank.prepare (kSR, block);
    bank.initialCutDb = -12; bank.softCapDb = -18; bank.hardCapDb = -24;
    bank.defaultQ = 25; bank.holdSeconds = 4.0;

    const double G = std::pow (10.0, gainDb / 20.0);
    std::vector<float> buf ((size_t) block);
    std::vector<double> out ((size_t) block, 0.0);
    double elapsed = 0.0, peak = -200.0;
    int events = 0; double firstHz = 0.0, firstAtDb = 0.0;
    bool blew = false;

    for (int b = 0; b < (int) (6.0 * kSR / block) && ! blew; ++b)
    {
        for (int i = 0; i < block; ++i)
        {
            const double mic = noise (0.0006) + path.read();
            if (! std::isfinite (mic) || std::abs (mic) > 50.0) { blew = true; break; }
            buf[(size_t) i] = (float) mic;
            if (std::abs (mic) > 1e-9) peak = std::max (peak, 20.0 * std::log10 (std::abs (mic)));
            path.push (G * out[(size_t) i]);
        }
        if (blew) break;
        if (guard)
        {
            det.push (buf.data(), block);
            fk::FeedbackDetector::Event ev;
            while (det.popEvent (ev))
            {
                if (events++ == 0) { firstHz = ev.freq; firstAtDb = ev.levelDb; }
                bank.trigger (ev.freq, elapsed, ev.growing, ev.levelDb);
            }
            bank.process (buf.data(), block, false);
        }
        for (int i = 0; i < block; ++i) out[(size_t) i] = (double) buf[(size_t) i];
        elapsed += (double) block / kSR;
        if (guard) bank.release (elapsed);
    }
    int filters = 0;
    for (int i = 0; i < 48; ++i) if (bank.getSlot (i).active) ++filters;
    if (guard)
    {
        // What did the detector actually have in front of it?
        float best = -200.0f; int bestBin = 0;
        for (int i = 1; i < fk::FeedbackDetector::numBins; ++i)
            if (det.getBinDb (i) > best) { best = det.getBinDb (i); bestBin = i; }
        std::printf ("      [ran %.0f ms, loudest bin %.0f Hz at %.1f dB]\n",
                     elapsed * 1000.0,
                     bestBin * kSR / fk::FeedbackDetector::fftSize, best);
    }
    std::printf ("  %-18s %+6.1f dB %-6s peak %6.1f dB  %s  events %4d  filters %2d",
                 room.name, gainDb, guard ? "guard" : "bare", peak,
                 blew ? "RANAWAY" : "stable ", events, filters);
    if (events) std::printf ("  first %.0f Hz at %.1f dB", firstHz, firstAtDb);
    std::printf ("\n");
}


/// Does the guard actually put a filter ON what it catches?
///
/// The rig's standing complaint, measured: a median 3.1 dB of cut on the frequency
/// just caught, the nearest filter 300 to 500 Hz away, rings climbing to -13 dB
/// while detected 29 times. Detection was never the problem; placement was. This
/// asks the same question of a real room, where it can be answered in seconds
/// instead of by asking someone to sing.
void probePlacement (const Room& room, double gainDb)
{
    constexpr int block = 64;
    RoomPath path; path.build (room);

    fk::FeedbackDetector det;
    fk::FeedbackDetector::Params p;
    p.floorDb = -95.0f; p.minFreq = 60.0f;
    det.prepare (kSR); det.setParams (p);

    fk::NotchBank<48> bank;
    bank.prepare (kSR, block);
    bank.initialCutDb = -6; bank.softCapDb = -12; bank.hardCapDb = -18;
    bank.defaultQ = 12; bank.holdSeconds = 4.0;

    const double G = std::pow (10.0, gainDb / 20.0);
    std::vector<float> buf ((size_t) block);
    std::vector<double> out ((size_t) block, 0.0);
    double elapsed = 0.0;

    int events = 0, refusedCovered = 0, refusedDark = 0, refusedLocked = 0, placedOk = 0;
    double sumCut = 0.0, sumGap = 0.0;
    int measured = 0;

    for (int b = 0; b < (int) (6.0 * kSR / block); ++b)
    {
        bool dead = false;
        for (int i = 0; i < block; ++i)
        {
            const double mic = noise (0.0006) + path.read();
            if (! std::isfinite (mic) || std::abs (mic) > 50.0) { dead = true; break; }
            buf[(size_t) i] = (float) mic;
            path.push (G * out[(size_t) i]);
        }
        if (dead) break;

        det.push (buf.data(), block);
        fk::FeedbackDetector::Event ev;
        while (det.popEvent (ev))
        {
            ++events;
            // What is already on this frequency, before we act?
            const double before = bank.cutAtDb (ev.freq, -1);
            double gap = 1.0e9;
            for (int s = 0; s < 48; ++s)
                if (bank.getSlot (s).active)
                    gap = std::min (gap, std::abs (bank.getSlot (s).freq - (double) ev.freq));
            if (gap < 1.0e8) { sumCut += before; sumGap += gap; ++measured; }

            const int slot = bank.trigger (ev.freq, elapsed, ev.growing, ev.levelDb);
            if (slot >= 0) ++placedOk;
            else switch (bank.lastRefusal)
            {
                case fk::NotchBank<48>::Refusal::AlreadyCovered: ++refusedCovered; break;
                case fk::NotchBank<48>::Refusal::RegionTooDark:  ++refusedDark;    break;
                case fk::NotchBank<48>::Refusal::AllLocked:      ++refusedLocked;  break;
                default: break;
            }
        }
        bank.process (buf.data(), block, false);
        for (int i = 0; i < block; ++i) out[(size_t) i] = (double) buf[(size_t) i];
        elapsed += (double) block / kSR;
        bank.release (elapsed);
    }

    // What the program is being put through, summed across every live filter.
    // This is the number the complaint was actually about.
    double sumEq = 0.0; int n = 0; int live = 0;
    for (int i = 0; i < 48; ++i) if (bank.getSlot (i).active) ++live;
    for (double f = 200.0; f <= 16000.0; f *= 1.03) { sumEq += bank.cutAtDb (f, -1); ++n; }

    std::printf ("  %-18s %5d ev | placed %4d dark %4d | %2d filters, EQ %5.1f dB",
                 room.name, events, placedOk, refusedDark, live, sumEq / n);
    if (measured) std::printf (" | cut on catch %5.1f dB, gap %4.0f Hz", sumCut / measured, sumGap / measured);
    std::printf ("\n");
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

    // ---- calibration against the rig -------------------------------------
    //
    // Every frequency below was produced by the real system and is in its logs.
    // The high ones are from 2026-09-04 (the rings that were escaping); the low
    // ones from the studio on 08-26, the night the low end was the whole problem.
    // Loop delay 5.6 ms is the rig's own, inferred from a median 179 Hz spacing
    // between adjacent ring frequencies.
    //
    // Aiming the path's resonance at each and checking the loop rings THERE is
    // what makes this simulator worth trusting for anything else.
    std::printf ("\nCalibration against the rig's own measured rings\n");
    std::printf ("%9s %10s %11s %12s %10s\n", "aimed at", "rang at", "error", "growth", "band");

    struct Case { double hz; const char* band; };
    for (auto c : { Case{  281.0, "low"  }, Case{  469.0, "low"  }, Case{  656.0, "low"  },
                    Case{ 1125.0, "low"  }, Case{ 2438.0, "mid"  }, Case{ 4715.0, "mid"  },
                    Case{ 7108.0, "high" }, Case{ 9897.0, "high" }, Case{ 14565.0, "high" } })
    {
        // A room mode below the Schroeder frequency is sparse and high-Q; a horn
        // or driver peak up high is broader. That is the physical difference
        // between the two bands, so the simulation should reflect it.
        const bool low = c.hz < 1500.0;
        const auto r = findRing (5.6, c.hz, low ? 14.0 : 10.0, low ? 20.0 : 8.0, 0.5);
        const double err = 100.0 * std::abs (r.hz - c.hz) / c.hz;
        std::printf ("%7.0f Hz %8.0f Hz %9.1f%% %9.0f dB/s %10s\n",
                     c.hz, r.hz, err, r.rateDbPerSec, c.band);
    }
    std::printf ("\n");
    std::printf ("Real detector and real notch bank inside an actual feedback loop.\n"
                 "Feedback path: sparse reflections, first arrival as given.\n\n");

    std::printf ("%10s %14s %14s %10s %10s\n",
                 "loop delay", "MSG guard off", "MSG guard on", "ASG", "filters");

    double bestAsg = -100.0, ladderWorst = 1000.0;
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

    // ---- the room ladder --------------------------------------------------
    std::printf ("\nThe room ladder - geometry, not invented paths\n");
    std::printf ("%-22s %7s %7s %8s %9s %8s %8s\n",
                 "room", "V m3", "RT60", "f_c", "MSG off", "ASG", "filters");

    const Room rungs[] = {
        { "1 concrete cube",  3.0,  3.0,  3.0, 0.02, { 0.6,0.6,1.2 }, { 1.1,0.6,1.4 } },
        { "2 gymnasium",     30.0, 18.0,  9.0, 0.03, { 3.0,4.0,1.6 }, { 4.0,4.0,1.5 } },
        { "3 church nave",   40.0, 14.0, 12.0, 0.05, { 8.0,7.0,4.0 }, { 3.0,7.0,1.5 } },
        { "4 hotel ballroom",26.0, 16.0,  3.6, 0.12, { 3.0,5.0,1.5 }, { 4.0,5.0,1.5 } },
        { "5 treated club",  14.0, 10.0,  4.5, 0.28, { 2.0,3.0,1.4 }, { 2.8,3.0,1.5 } },
        { "6 large studio",  25.0, 18.0,  8.0, 0.35, { 4.0,5.0,1.6 }, { 4.8,5.0,1.5 } },
    };

    for (const auto& room : rungs)
    {
        const double off = roomMsg (room, false);
        lastEvents = 0; lastFilters = 0; lastRingHz = 0.0;
        const double on  = roomMsg (room, true);
        char det[32];
        std::snprintf (det, sizeof det, "%d/%d", lastFilters, lastEvents);
        ladderWorst = std::min (ladderWorst, on - off);
        std::printf ("%-22s %7.0f %6.1fs %7.0f %8.1f %8.1f %8s  ring %.0f Hz\n",
                     room.name, room.volume(), room.rt60(), room.schroeder(),
                     off, on - off, det, lastRingHz);
    }

    // ---- Rule 2, tested on geometry ---------------------------------------
    // The physics says the excess loop gain is a fraction of a dB. If that is
    // right, a 24 dB cut is answering a 0.3 dB problem and the depth is pure
    // damage. Asked on invented comb paths it came out flat; asked on rooms it
    // either holds or it does not.
    std::printf ("\nNotch depth against ASG, per room\n");
    std::printf ("%-18s %-22s %7s %8s\n", "room", "tuning", "ASG", "filters");
    for (const auto& room : { rungs[0], rungs[1], rungs[3], rungs[5] })
    {
        tuning = Tuning{};
        const double off = roomMsg (room, false);
        for (auto t : { Tuning{ -12, -18, -24, 25 },
                        Tuning{  -6, -12, -18, 25 },
                        Tuning{  -3,  -6, -12, 25 },
                        Tuning{  -6, -12, -18, 12 },
                        Tuning{  -3,  -6,  -9, 12 } })
        {
            tuning = t;
            lastFilters = 0;
            const double on = roomMsg (room, true);
            char label[48];
            std::snprintf (label, sizeof label, "open %.0f cap %.0f Q%.0f",
                           t.initialCut, t.hardCap, t.q);
            std::printf ("%-18s %-22s %6.1f dB %8d\n", room.name, label, on - off, lastFilters);
        }
        tuning = Tuning{};
    }

    std::printf ("\nPlacement: does a filter land ON what was caught?\n");
    for (const auto& room : { rungs[0], rungs[1], rungs[3], rungs[5] })
        probePlacement (room, roomMsg (room, false) + 3.0);


    std::printf ("\nAbove threshold, explicitly - is the detector even seeing it?\n");
    for (const auto& room : { rungs[0], rungs[1], rungs[5] })
    {
        const double off = roomMsg (room, false);
        probeRoom (room, off + 3.0, false);
        probeRoom (room, off + 3.0, true);
    }

    std::printf ("\nFor reference: Sabine claim 6-9 dB typical; frequency shifting buys\n"
                 "2-6 dB; hearing-aid adaptive cancellation exceeds 10 dB. Under 3 dB and\n"
                 "the honest answer is acoustics and gain structure, not software.\n");

    std::printf ("\n%s\n\n", ladderWorst >= 3.0
                 ? "ASG clears the floor in every room on the ladder."
                 : "ASG IS TOO SMALL TO MATTER IN AT LEAST ONE ROOM.");
    return 0;
}
