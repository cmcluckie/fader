// fk-fuzz - a random feedback generator, and a score.
//
//   ./fk-fuzz [minutes] [seed]
//
// Everything the detector has been tested against so far was drawn by hand from
// something we had already seen: a ring at the frequency the rig rang at, a
// plateau shaped like the one in a screenshot. That tests memory, not physics.
//
// This generates rings from the loop equations instead, and never draws a
// "shape" at all. It draws MODES:
//
//   growth (dB/s) = 20 log10|GF| / tau          gain over round-trip delay
//   modal bandwidth B = 2.2 / RT60              reverberation sets width
//   modal density dN/df = 4 pi V f^2 / c^3      how many modes live there
//   overlap M = B dN/df,  Schroeder f_c = 2000 sqrt(RT60/V)   where M ~ 3
//
// Below f_c the draw lands one isolated mode: a spike, taking all the excess
// gain, fast. Above it the same draw lands several modes inside one bandwidth,
// each with a share of the gain: wide, flat, slow. The two shapes the operator
// described are not two cases in this code - they are the same case either side
// of Schroeder.
//
// The loop is closed through the bank: a mode's growth rate is
// (excess + whatever the notch bank is cutting at that frequency) / tau, so a
// filter that lands makes the ring decay, one that misses does not, and one
// that lands 300 Hz away does nothing at all. That is the whole product, scored.
//
// Voice is injected too - held notes with harmonics and vibrato - because a
// guard that scores well by notching the singer has not scored well.

#include <juce_dsp/juce_dsp.h>
#include <cstdio>
#include <random>
#include <string>
#include <vector>
#include <algorithm>
#include "../Source/FeedbackDetector.h"
#include "../Source/NotchBank.h"

namespace
{
constexpr double kSR    = 48000.0;
constexpr int    kBlock = 64;
constexpr double kC     = 343.0;

/// One room mode that is trying to run away.
struct Mode
{
    double freq     = 0.0;
    double excessDb = 0.0;    // loop gain over unity, dB per round trip
    double tau      = 0.012;  // round-trip delay, seconds
    double levelDb  = -70.0;  // current level
    double phase    = 0.0;
    bool   live     = false;
    double bornAt   = 0.0;
    double peakDb   = -120.0;
    bool   caught   = false;
    double caughtLevel = -120.0;   // how loud it already was when first detected
    double killedAt = 0.0;         // when it fell 10 dB back off its own peak
    bool   killed   = false;
    bool   ranAway  = false;
    bool   audible  = false;
    double audibleAt= 0.0;
    double caughtAt = 0.0;
    int    ringId   = 0;
};

/// A ring: one mode below Schroeder, a cluster above it.
struct Ring
{
    int    id        = 0;
    double centre    = 0.0;
    int    modes     = 1;
    double bandwidth = 0.0;
    double startAt   = 0.0;
    double endAt     = 0.0;
    bool   plateau   = false;
    bool   scored    = false;
};

double dbToGain (double db) { return std::pow (10.0, db / 20.0); }
}

int main (int argc, char* argv[])
{
    const double minutes = argc > 1 ? std::atof (argv[1]) : 1.0;
    const unsigned seed  = argc > 2 ? (unsigned) std::atoi (argv[2]) : 1u;
    // Diagnostic: silence the rings and leave only the singer, to separate "the
    // voice guard is weak" from "the voice guard is weak WHEN RINGS ARE PRESENT".
    bool noRings = false, plateauPath = false;
    for (int i = 1; i < argc; ++i)
    {
        if (std::string (argv[i]) == "--no-rings") noRings = true;
        // The plateau path ships disabled. This turns it on so its worth can be
        // scored against the same rings rather than argued from one fixture.
        if (std::string (argv[i]) == "--plateau") plateauPath = true;
    }
    std::mt19937 rng (seed);

    auto uni = [&rng] (double a, double b) {
        return std::uniform_real_distribution<double> (a, b) (rng);
    };

    fk::FeedbackDetector::Params p;
    p.floorDb = -95.0f;
    p.minFreq = 40.0f;
    if (plateauPath) p.plateauRiseDb = 10.0f;
    fk::FeedbackDetector det;
    det.prepare (kSR);
    det.setParams (p);

    fk::NotchBank<48> bank;
    bank.prepare (kSR, kBlock);
    bank.defaultQ    = 12.0;
    bank.softCapDb   = -18.0;
    bank.hardCapDb   = -24.0;
    bank.initialCutDb = -6.0;

    std::vector<Ring> rings;
    std::vector<Mode> modes;
    rings.reserve (512);
    modes.reserve (4096);

    // ---- the generator -----------------------------------------------------
    // A room per ring, because a rig moves: volume and RT60 decide Schroeder,
    // Schroeder decides whether this draw is a spike or a plateau. Nothing here
    // chooses a shape.
    const double totalSec = minutes * 60.0;
    double t = 0.0;
    int nextId = 1;
    while (t < totalSec)
    {
        t += uni (1.5, 6.0);
        if (t >= totalSec) break;

        // Rooms across the range a working rig actually meets. Small and live
        // puts Schroeder high, which is where spikes come from; big and dead puts
        // it low, which is where plateaus come from. A draw biased to one end
        // would quietly test only half the problem - the first run gave 14
        // plateaus to 2 spikes.
        const double volume = (uni (0.0, 1.0) < 0.5) ? std::pow (10.0, uni (1.2, 2.3))    // 16 - 200 m3
                                                     : std::pow (10.0, uni (2.3, 3.8));   // 200 - 6300 m3
        const double rt60   = uni (0.3, 3.0);
        const double fc     = 2000.0 * std::sqrt (rt60 / volume);    // Schroeder
        const double centre = std::pow (10.0, uni (std::log10 (70.0), std::log10 (12000.0)));

        const double B       = 2.2 / rt60;                           // modal bandwidth
        const double density = 4.0 * juce::MathConstants<double>::pi * volume
                             * centre * centre / (kC * kC * kC);     // modes per Hz
        const double overlap = juce::jlimit (0.2, 40.0, B * density);

        Ring r;
        r.id        = nextId++;
        r.centre    = centre;
        r.plateau   = centre > fc;
        r.modes     = r.plateau ? (int) juce::jlimit (3.0, 24.0, overlap) : 1;
        r.bandwidth = r.plateau ? juce::jmax (120.0, B * (double) r.modes) : B;
        r.startAt   = t;
        r.endAt     = t + uni (4.0, 14.0);
        rings.push_back (r);

        // Excess gain per mode. A plateau shares it out - which is exactly why
        // plateaus are slow - and a lone mode keeps the lot.
        const double share = r.plateau ? uni (0.05, 0.6) : uni (0.3, 6.0);
        const double tau   = uni (0.004, 0.030);                     // 1.4 - 10 m of path

        for (int m = 0; m < r.modes; ++m)
        {
            Mode mo;
            mo.ringId   = r.id;
            mo.freq     = r.modes == 1 ? centre
                                       : centre - r.bandwidth / 2.0
                                         + r.bandwidth * ((double) m + 0.5) / r.modes;
            mo.excessDb = juce::jmax (0.02, share * uni (0.7, 1.3));
            mo.tau      = tau;
            mo.levelDb  = uni (-78.0, -66.0);
            mo.phase    = uni (0.0, 6.28);
            mo.bornAt   = r.startAt;
            modes.push_back (mo);
        }
    }

    // Voice: held notes with harmonics and vibrato, so a guard that scores by
    // notching the singer is caught doing it.
    struct Note { double startAt, endAt, f0, vibHz, vibDepth; };
    std::vector<Note> notes;
    for (double u = 3.0; u < totalSec; u += uni (6.0, 15.0))
        notes.push_back ({ u, u + uni (1.5, 4.0), std::pow (10.0, uni (std::log10 (95.0), std::log10 (520.0))),
                           uni (4.2, 6.4), uni (0.003, 0.02) });

    std::printf ("fk-fuzz - %.1f minutes, seed %u%s\n", minutes, seed, noRings ? "  [RINGS SILENCED]" : plateauPath ? "  [PLATEAU PATH ON]" : "");
    std::printf ("  %zu rings (%zu spikes, %zu plateaus), %zu modes, %zu sung notes\n\n",
                 rings.size(),
                 (size_t) std::count_if (rings.begin(), rings.end(), [] (const Ring& r) { return ! r.plateau; }),
                 (size_t) std::count_if (rings.begin(), rings.end(), [] (const Ring& r) { return r.plateau; }),
                 modes.size(), notes.size());

    // ---- run ---------------------------------------------------------------
    std::vector<float> buf ((size_t) kBlock);
    const long totalBlocks = (long) (totalSec * kSR / kBlock);
    double now = 0.0;
    int falseAlarms = 0, voiceEvents = 0, runaways = 0, deadEvents = 0;
    struct VoiceHit { float freq, f0, levelDb; int path; float widthHz; };
    std::vector<VoiceHit> voiceLog;
    double damageSum = 0.0; int damageN = 0;

    for (long b = 0; b < totalBlocks; ++b)
    {
        now = (double) (b * kBlock) / kSR;

        // synthesise
        std::fill (buf.begin(), buf.end(), 0.0f);
        for (auto& mo : modes)
        {
            const Ring& r = rings[(size_t) (mo.ringId - 1)];
            mo.live = ! noRings && now >= r.startAt && now <= r.endAt;
            if (! mo.live) continue;

            const double amp = dbToGain (mo.levelDb);
            const double step = 2.0 * juce::MathConstants<double>::pi * mo.freq / kSR;
            for (int i = 0; i < kBlock; ++i)
            {
                buf[(size_t) i] += (float) (amp * std::sin (mo.phase));
                mo.phase += step;
            }
            mo.peakDb = juce::jmax (mo.peakDb, mo.levelDb);
            if (mo.levelDb > -3.0 && ! mo.ranAway) { mo.ranAway = true; ++runaways; }
        }

        for (const auto& n : notes)
        {
            if (now < n.startAt || now > n.endAt) continue;
            for (int i = 0; i < kBlock; ++i)
            {
                const double tt = now + (double) i / kSR;
                const double f  = n.f0 * (1.0 + n.vibDepth * std::sin (2.0 * juce::MathConstants<double>::pi * n.vibHz * tt));
                double v = 0.0;
                for (int h = 1; h <= 10; ++h)
                    v += (0.05 / std::sqrt ((double) h)) * std::sin (2.0 * juce::MathConstants<double>::pi * f * h * tt);

                // A voice does not start instantly. Without this the note's onset
                // is a transient, and the first version scored 78 catches on the
                // singer for something no singer does.
                const double env = juce::jmin (1.0, juce::jmin ((tt - n.startAt) / 0.20,
                                                                (n.endAt - tt) / 0.20));
                buf[(size_t) i] += (float) (v * juce::jmax (0.0, env));
            }
        }

        for (int i = 0; i < kBlock; ++i)
            buf[(size_t) i] += (float) (0.00008 * (uni (-1.0, 1.0)));

        // the engine's order: analyse pre-notch, then filter
        det.push (buf.data(), kBlock);

        fk::FeedbackDetector::Event ev;
        while (det.popEvent (ev))
        {
            bank.trigger (ev.freq, now, ev.growing, ev.levelDb,
                          (double) (ev.widthHiHz - ev.widthLoHz), ev.path == 4);

            // Was that one of ours, or the singer?
            bool mine = false;
            for (auto& mo : modes)
            {
                if (! mo.live) continue;
                if (std::abs (mo.freq - ev.freq) > juce::jmax (25.0, 0.03 * mo.freq)) continue;
                mine = true;
                if (! mo.caught) { mo.caught = true; mo.caughtAt = now; mo.caughtLevel = mo.levelDb; }
            }
            if (! mine)
            {
                // Not a live mode. Either the singer, or a ring that has already
                // finished (its filter is still being refreshed), or nothing at
                // all - which are three different things and were one number.
                // A catch counts against the singer only if it lands ON the note -
                // one of the ten harmonics actually synthesised. The first version
                // blamed the voice for ANY unmatched catch while a note sounded,
                // and reported 80 hits at partials 7 to 49 of a 10-harmonic note,
                // which is a bug in the scoring, not in the guard.
                bool singing = false;
                for (const auto& n : notes)
                {
                    if (now < n.startAt || now > n.endAt) continue;
                    for (int h = 1; h <= 10; ++h)
                    {
                        const double want = n.f0 * h;
                        if (std::abs (ev.freq - want) <= juce::jmax (20.0, 0.04 * want)) singing = true;
                    }
                }

                bool dead = false;
                for (const auto& mo : modes)
                    if (! mo.live && std::abs (mo.freq - ev.freq) <= juce::jmax (25.0, 0.03 * mo.freq)) dead = true;

                if (singing && voiceLog.size() < 12)
                {
                    double f0 = 0.0;
                    for (const auto& n : notes)
                        if (now >= n.startAt && now <= n.endAt) f0 = n.f0;
                    voiceLog.push_back ({ ev.freq, (float) f0, ev.levelDb, ev.path,
                                          ev.widthHiHz - ev.widthLoHz });
                }
                if (singing)   ++voiceEvents;
                else if (dead) ++deadEvents;
                else           ++falseAlarms;
            }
        }

        bank.process (buf.data(), kBlock, false);
        bank.release (now);

        // close the loop: the notch subtracts from the excess gain
        const double dt = (double) kBlock / kSR;
        for (auto& mo : modes)
        {
            if (! mo.live) continue;
            const double cut = bank.cutAtDb (mo.freq, -1);          // negative where a filter sits
            const double rate = (mo.excessDb + cut) / mo.tau;       // dB per second
            mo.levelDb = juce::jlimit (-100.0, 0.0, mo.levelDb + rate * dt);
            if (! mo.audible && mo.levelDb > -60.0) { mo.audible = true; mo.audibleAt = now; }
            // KILLED: driven 10 dB back down off its own peak, which only happens
            // if a filter actually landed on it. "Caught" merely means something
            // was reported; this means the loop was beaten.
            if (! mo.killed && mo.peakDb > -70.0 && mo.levelDb < mo.peakDb - 10.0)
            { mo.killed = true; mo.killedAt = now; }
        }

        if ((b % 750) == 0)                                          // ~ once a second
        {
            double sum = 0.0; int n = 0;
            for (double f = 200.0; f <= 16000.0; f *= 1.05) { sum += bank.cutAtDb (f, -1); ++n; }
            damageSum += sum / n; ++damageN;
        }
    }

    // ---- score -------------------------------------------------------------
    // "caught"  = a detection landed within 3% of the mode, ever.
    // "killed"   = its level was driven 10 dB back off its own peak.
    // "latency"  = from crossing -60 dB (audible) to that first detection.
    // "at catch" = how loud it already was when first detected.
    struct Tally
    {
        int n = 0, caught = 0, killed = 0, ranAway = 0;
        std::vector<double> lat;
        double peak = 0.0, atCatch = 0.0;
    };
    Tally spike, plate;
    for (const auto& mo : modes)
    {
        Ring& r = rings[(size_t) (mo.ringId - 1)];
        if (r.scored) continue;                                     // one row per ring
        r.scored = true;
        Tally& tl = r.plateau ? plate : spike;
        ++tl.n;
        tl.peak += mo.peakDb;
        if (mo.ranAway) ++tl.ranAway;
        if (mo.killed)  ++tl.killed;
        if (mo.caught)
        {
            ++tl.caught;
            tl.atCatch += mo.caughtLevel;
            const double from = mo.audible ? mo.audibleAt : mo.bornAt;
            tl.lat.push_back (juce::jmax (0.0, (mo.caughtAt - from)) * 1000.0);
        }
    }

    auto row = [] (const char* name, Tally& tl) {
        std::sort (tl.lat.begin(), tl.lat.end());
        const double med = tl.lat.empty() ? 0.0 : tl.lat[tl.lat.size() / 2];
        const double p90 = tl.lat.empty() ? 0.0 : tl.lat[(size_t) (tl.lat.size() * 0.9)];
        std::printf ("  %-8s %4d  caught %3.0f%%  killed %3.0f%%  ran away %3.0f%%  "
                     "latency med %5.0f / p90 %6.0f ms  at catch %6.1f  peak %6.1f dB\n",
                     name, tl.n,
                     tl.n ? 100.0 * tl.caught / tl.n : 0.0,
                     tl.n ? 100.0 * tl.killed / tl.n : 0.0,
                     tl.n ? 100.0 * tl.ranAway / tl.n : 0.0,
                     med, p90,
                     tl.caught ? tl.atCatch / tl.caught : 0.0,
                     tl.n ? tl.peak / tl.n : 0.0);
    };
    std::printf ("SCORE   caught = detected at all; killed = driven 10 dB off its peak\n");
    std::printf ("        latency = from crossing -60 dB to that first detection\n\n");
    row ("spikes", spike);
    row ("plateaus", plate);
    std::printf ("\n  modes that ran away (past -3 dBFS)  : %d of %zu\n", runaways, modes.size());
    std::printf ("  catches on the singer               : %d\n", voiceEvents);
    std::printf ("  catches on a ring already finished  : %d\n", deadEvents);
    std::printf ("  catches on nothing at all           : %d\n", falseAlarms);
    std::printf ("  average cut, 200 Hz-16 kHz          : %.2f dB\n", damageN ? damageSum / damageN : 0.0);

    if (! voiceLog.empty())
    {
        std::printf ("\n  what it caught while the singer was singing:\n");
        std::printf ("    %9s %9s %9s %8s %7s %s\n", "caught Hz", "note f0", "partial", "level", "width", "path");
        for (const auto& v : voiceLog)
            std::printf ("    %9.0f %9.0f %9.2f %7.1f %7.0f %s\n", v.freq, v.f0,
                         v.f0 > 0 ? v.freq / v.f0 : 0.0, v.levelDb, v.widthHz,
                         v.path == 1 ? "growth" : v.path == 2 ? "sustain" : v.path == 4 ? "plateau" : "escal");
    }

    const bool ok = spike.n > 0 && plate.n > 0;
    return ok ? 0 : 1;
}
