#pragma once
#include <juce_dsp/juce_dsp.h>
#include <array>
#include <atomic>
#include <cmath>
#include <algorithm>

#ifndef FLAT_TOP_MIN_HZ
#define FLAT_TOP_MIN_HZ 4000.0f
#endif

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
        // Above the voice band the same bar costs latency for nothing. Measured on
        // 4,478 rig catches, real rings grow at a median 20-22 dB/s, so a 30 dB/s
        // bar sends more than half of them down the 340 ms sustain path. At 20 dB/s
        // a clean 3 kHz ring fires in 69 ms instead of 341. It is NOT safe to use
        // this in the voice band: at 2 dB/100 ms everywhere, T39 notched the fifth
        // partial of a sung note at 1446 Hz.
        float  growthDbFast   = 2.0f;   // per 100 ms, above voiceBandHz only
                                        // scaled to the real window so the threshold does
                                        // not silently change when the hop size does
        int    harmonicExtra  = 36;     // extra frames (~190 ms) required if harmonic-related
        float  floorDb        = -70.0f; // ignore bins quieter than this
        // Three or more contiguous bins within this of a peak's top cannot be
        // one windowed sinusoid: it is a cluster of modes, and its frequency is
        // the cluster's centroid - see refineFrequency.
        float  flatTopDb      = 5.5f;
        int    flatTopMaxBins = 5;
        float  flatTopMinHz   = FLAT_TOP_MIN_HZ;
        // Watching and ACTING are different questions, and conflating them is what
        // made the guard audible with no feedback in the room. The floor is dragged
        // low on purpose, so a ring is tracked while it is still tiny - that is the
        // whole point of it. But placing a filter on a -85 dB tone buys nothing:
        // it is 40 dB below anything that has ever got away here, and it costs a
        // notch that then stands there. Measured at the rig, 82% of detections were
        // below -70 dB and the loudest thing in a minute was -48 dB - so almost
        // every filter was spent on something inaudible, and the pile of them was
        // not. Track from the floor; only act once it is worth acting on.
        float  actionDb       = -75.0f;
        // Feedback is a pure tone. Through this window a real ring measures about
        // 94 Hz between its -20 dB skirts, and the rig's own good sessions bear
        // that out: 63% of real catches under 50 Hz wide, 96% under 250.
        //
        // Broad energy is not a ring, however prominent its local maxima. A voice
        // formant or a sibilant carries several, and each was becoming a separate
        // suspect and then a separate filter: measured while singing, 121 distinct
        // frequencies in 16 seconds with only 2.5% of them narrow, adjacent buckets
        // at 10000-10400 and 6800-6900 that are plainly one feature carved up. That
        // is what built 29 filters and -7.3 dB over a fifteen-second phrase.
        //
        // At 200 Hz this keeps 94% of the frequencies caught in a session that
        // sounded right and rejects 64% of those from one that did not.
        float  maxWidthHz     = 200.0f;
        // ...unless it is FLAT. Nothing natural is both that wide and that even:
        // a vowel's formant is rippled by the harmonics underneath it, a cymbal
        // decays, a room's broad mode cluster just sits there. A plateau this
        // smooth is a loop, and it was invisible before - too wide to become a
        // suspect, so it produced no catch AND no rejection.
        // Below this, peaks are found in the 8192-point analysis instead of the
        // 2048-point one. 1 kHz: a voice's partials are 4.7 bins apart at 110 Hz
        // in the short window and 19 in the long one.
        float  crossoverHz    = 1000.0f;
        // How close to a multiple of the CURRENT sung fundamental a peak must sit
        // before the voice protections apply to it. Measured at the rig: rings at
        // 252, 287 and 357 Hz sat 2-3% from sung notes at 262, 294 and 349 and
        // were shielded as if they were the voice, uncut, for up to 1.9 s.
        float  voiceTolerance = 0.015f;
        float  plateauMinHz   = 150.0f;  // a plateau must be at least this wide
        float  plateauRipple  = 6.0f;    // peak-to-median across it, dB
        // OFF by default (999 = never). The path works - it catches a synthetic
        // plateau - but it also fires on a singer, and until it stops doing that
        // it has no business on a stage. Set to ~10 to enable it; see T42.
        float  plateauRiseDb  = 999.0f;  // above the band's own slow baseline
        int    plateauFrames  = 40;      // ~210 ms of continuous elevation
        int    plateauBands   = 3;       // consecutive raised bands to count as wide
        // Raised and flat is not enough: a vowel and a smooth hump are both raised
        // and flat (T4 fired 53 times, T36 carved a hump 46 times). Feedback also
        // CLIMBS - so the run must gain this much while it is being watched.
        // Not how FAST it rises - a plateau in a big room can creep - but how
        // STRAIGHT the rise is. Feedback is exponential, so in dB it is a straight
        // line whatever its slope; music arrives in lumps. Fit a line over the
        // window and judge the residual.
        float  plateauGrowDb  = 3.0f;    // minimum total rise across the window
        // A plateau is several modes at once, and modes beat: its level wobbles
        // several dB while rising perfectly straight. 1.5 dB was a bar only a pure
        // tone could clear - measured 8.2 dB on a textbook synthetic plateau.
        float  plateauStraight= 6.0f;    // RMS residual from that line, dB
        // ...and it must not MOVE. A plateau is a fixed cluster of room modes; a
        // singer's vibrato and vowel changes slide energy between bands every few
        // frames. Requiring the run's own edges to hold still is what separates
        // the two, and it is the term the first version was missing.
        // Only true silence, not "quiet". This was -55 dB, meant to stop the
        // detector chasing noise between songs, and it is the reason rings took so
        // long to find: a marked miss at 7 kHz was 33 dB prominent and climbing
        // steadily for a full second - 24 dB of it - with the detector not even
        // looking, because the CHANNEL was quiet. It only opened when the ring
        // itself dragged the channel past -55, by which time it was -45 dB and
        // audible across the room.
        //
        // Feedback starts in the quiet moments. A broadband gate is deaf exactly
        // then, and it was never needed: noise is not prominent, so the prominence
        // test already rejects it, and actionDb already refuses to spend a filter
        // on anything inaudible. Both are per-bin, which is the right shape for
        // this question. This now only stops a dead input.
        float  inputGateDb    = -90.0f;
        float  minFreq        = 200.0f; // low edge of the watched band
        float  maxFreq        = 16000.0f; // high edge; still clamped to Nyquist by bin count

        // A sung harmonic is prominent, steady and growing - it passes every test
        // feedback does. These three separate them.
        float  sustainRiseDb  = 1.5f;   // the sustain path also needs to have GROWN this
                                        // much: steady-forever is furniture, not feedback
        // A tone this loud is a howl, not furniture, and its filter must not be
        // allowed to bleed away underneath it however steady it has gone.
        //
        // Calibrated by replaying the real recordings through this detector
        // (tests/sim/replay.py), not from a percentile. The 2026-09-30 howl -
        // +41 dB on the spectrum, screaming - reports here as -35.5..-25.3 dB,
        // median -30, so the first bar of -25 held it for exactly none of its
        // eleven seconds: 10 events during the climb, then silence while the
        // notch bled 43 -> 28 dB. The 18-second howl of 2026-09-28 reports
        // -69..-22, median -33, and dipped from -30 to -8 dB of cut in the
        // middle for the same reason. -40 is below every screaming howl on
        // record and above the median of all 52,053 detections (-52).
        float  sustainHoldDb  = -40.0f;
        float  sustainHoldMinHz = 2000.0f;   // the hold is for howls; speech lives below this
        float  sustainSeconds = 0.3f;   // a dead-stable, harmonically isolated peak this
                                        // old is feedback even with NO growth
        float  harmonicPromDb = 6.0f;   // harmonic-related peaks need this much EXTRA prominence
        float  voiceBandHz    = 2000.0f;// below here, look longer and test for vibrato
        int    voiceFrames    = 56;     // ~300 ms: long enough to SEE a wobble
        float  vibratoDepth   = 0.004f; // peak-to-peak pitch swing, relative (0.4%)
        float  vibratoMinHz   = 3.5f;   // a singer's wobble lives in this band
        float  vibratoMaxHz   = 9.0f;
    };

    struct Event
    {
        float freq   = 0.0f;
        float levelDb= 0.0f;
        bool  growing= false;   // still climbing, as opposed to merely still present

        // Measurements for the capture log. Free to collect - the numbers are all
        // in hand at the moment of firing - and impossible to reconstruct after the
        // fact, which is what every "why was that slow" investigation has needed.
        // Which gate let it through. Every "why was that slow" question so far has
        // been answered by guessing at this and then failing to reproduce it: the
        // detector knows, and it costs a byte to say.
        //   1 = growth (fast path)   2 = sustain (steady and risen)   3 = escalation
        int   path   = 0;
        float ageMs  = 0.0f;    // tracked for this long before it was called feedback
        float widthLoHz = 0.0f; // -6 dB skirts of the peak as measured in the frame
        float widthHiHz = 0.0f; // that fired it
    };

    /// Why a candidate that looked like a peak did NOT become a detection.
    /// Logging only what fired means every "it missed one" has to be
    /// reverse-engineered by simulation; this says which gate stopped it.
    enum class Reason { None = 0, Harmonic = 1, Unstable = 2, NoGrowth = 3, Vibrato = 4, Drifting = 5 };

    /// Test hook: the last wide run the plateau path looked at, and every number
    /// it was judged on. Guessing which term blocked a plateau cost three rounds
    /// of rebuilding; the detector knows, so it says.
    struct PlateauProbe
    {
        float loHz = 0.0f, hiHz = 0.0f;
        float grew = 0.0f, slope = 0.0f, residual = 0.0f, ripple = 0.0f;
        int   bands = 0, still = 0;
        bool  fired = false;
    };
    const PlateauProbe& lastPlateau() const noexcept { return probe; }

    /// Test hook: what the frame saw at the bottom of the spectrum. Guessing why
    /// a fundamental was not recognised has cost two rounds already.
    struct VoiceProbe
    {
        int   peaks = 0;          // peaks in the frame
        float lowestHz = 0.0f;    // the lowest of them
        bool  family = false;     // ...and whether it carried partials above it
        float partial2 = 0.0f;    // the nearest peak to 2x, if any
        float partial3 = 0.0f;
        bool  lowUsed = false;    // was the long window in play?
    };
    const VoiceProbe& lastVoice() const noexcept { return vprobe; }

    /**
        What is going on in front of the microphone, as opposed to what the guard
        is doing about it.

        The operator cannot narrate their own performance - they are singing - so
        the engine says it instead: how loud, what note, and whether that is one
        voice, several things at once (a backing track, a band), or an empty room.

        Families are counted, not pitches. A voice is one fundamental carrying
        partials; polyphony is several at once. Room noise carries none.
    */
    struct Context
    {
        float levelDb   = -120.0f;   // frame RMS
        float f0Hz      = 0.0f;      // the lowest fundamental found, 0 if none
        int   families  = 0;         // independent harmonic families in the frame
        float flatness  = 0.0f;      // 0 = tonal, 1 = noise-like
    };
    Context context() const noexcept { return ctx; }

    struct Reject
    {
        float freq    = 0.0f;
        float levelDb = 0.0f;
        int   reason  = 0;
        int   frames  = 0;
        // The numbers the verdict was made on. "Unstable" without the spread
        // and the tolerance it failed is an opinion; with them it is a fact
        // that can be argued with. Four real howls at 1245-1500 Hz were
        // declined as unstable/vibrato on 2026-09-27 and nothing recorded by
        // how much.
        float spreadHz = 0.0f;
        float tolHz    = 0.0f;
    };

    bool popReject (Reject& out) noexcept
    {
        if (rejRead == rejWrite) return false;
        out = rejects[(size_t) rejRead];
        rejRead = (rejRead + 1) % rejQueueSize;
        return true;
    }

    // 2048 -> 23.4 Hz bins, 43 ms window.
    //
    // 1024 was quicker (21 ms) and fine for the 9-15 kHz rings this room is full
    // of, but a rehearsal turned up low-end feedback and 47 Hz bins cannot serve
    // it: the stability gate has to be loosened to about +-12 Hz just to let a
    // 330 Hz ring qualify, and at that width a singer's vibrato qualifies too.
    // Measured, not argued - T4 fails outright at 1024 with a tolerance wide
    // enough for T12 to pass. 2048 is the width where both hold.
    static constexpr int fftOrder  = 11;
    static constexpr int fftSize   = 1 << fftOrder;
    static constexpr int numBins   = fftSize / 2;
    static constexpr int hopSize   = fftSize / 8;     // 256 -> ~5.3 ms between frames
    // One slot per peak the frame can offer. It was 24 against maxPeaks of 48, so
    // a busy rig kept the table permanently full - and a full table is not a
    // graceful degradation, it is blindness: see track() for what a newcomer had to
    // do to get in. Each slot is a frequency/level history, ~1 KB.
    static constexpr int maxSuspects = 64;
    // Peaks kept per analysis frame. A live stage in a wide listen band offers
    // more than this, so what happens when the list fills is not a detail - see
    // the collection loop.
    static constexpr int maxPeaks    = 96;
    static constexpr int eventQueueSize = 16;

    // ---- the low-band analyser ----------------------------------------------
    // 2048 points gives 23.4 Hz bins everywhere, and down low that is blindness:
    // at 110 Hz a voice's partials sit 4.7 bins apart and a Hann window smears
    // them into each other, so nothing can tell a bass fundamental from a lone
    // ring (T41). Everything below crossoverHz is analysed again at 8192 points -
    // 5.9 Hz bins - where those partials resolve cleanly.
    //
    // It costs four times the FFT work in the low band. The engine was using 0.3%
    // of one core, so that is not a consideration; being unable to see is.
    static constexpr int loFftOrder = 13;
    static constexpr int loFftSize  = 1 << loFftOrder;
    static constexpr int loNumBins  = loFftSize / 2;

    FeedbackDetector() : fft (fftOrder), loFft (loFftOrder),
                         window ((size_t) fftSize, juce::dsp::WindowingFunction<float>::hann),
                         loWindow ((size_t) loFftSize, juce::dsp::WindowingFunction<float>::hann) {}

    void prepare (double sampleRateIn)
    {
        sampleRate = sampleRateIn;
        binHz      = (float) (sampleRate / fftSize);
        loBinHz    = (float) (sampleRate / loFftSize);
        magLo.fill (-120.0f);
        loValid = false;
        ring.fill (0.0f);
        writePos = 0;
        sinceHop = 0;
        for (auto& s : suspects) s = Suspect{};
        for (auto& m : publishedMag) m.store (-120.0f, std::memory_order_relaxed);
        eventRead = eventWrite = 0;
        rejRead = rejWrite = 0;
        hasPrevFrame = false;
        samplesSeen  = 0;
    }

    void setParams (const Params& p) noexcept { params = p; }

    /** Feed one block. Analysis fires internally every hopSize samples. */
    /// Discard the analysis window and wait for it to refill with real audio.
    /// Used when the signal has been away - after a bypass, say - so the join is
    /// not read as every tone in the room exploding out of silence at once.
    void resetAnalysis() noexcept
    {
        samplesSeen  = 0;
        hasPrevFrame = false;
        for (auto& s : suspects) s = Suspect{};
    }

    void push (const float* data, int numSamples) noexcept
    {
        for (int n = 0; n < numSamples; ++n)
        {
            ring[(size_t) writePos] = data[n];
            // Count up to the LONGEST window, not the shortest: the low analyser
            // needs loFftSize samples before it can run, and capping at fftSize
            // meant it never ran at all.
            if (samplesSeen < loFftSize) ++samplesSeen;
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

    float getBinHz() const noexcept        { return binHz; }

private:
    static constexpr int windowSize = 128;  // >= sustainSeconds and voiceFrames

public:
    /// One ring, followed across its hops. See noteTrack().
    struct Track
    {
        bool   active   = false;
        float  freq     = 0.0f;   // where it is now
        float  loHz     = 0.0f;   // the whole stretch it has walked
        float  hiHz     = 0.0f;
        int    hops     = 0;      // jumps clear of the suspect matcher
        int    hits     = 0;
        double lastSeen = 0.0;

        /// How far it has wandered, as a fraction of where it sits. One measured
        /// hop is 7.7%, so that is what counts as fully hot.
        float heat() const noexcept
        {
            if (freq <= 0.0f) return 0.0f;
            return juce::jlimit (0.0f, 1.0f, (hiHz - loHz) / (freq * 0.08f));
        }
    };

    static constexpr int maxTracks = 8;
    const Track& trackAt (int i) const noexcept { return tracks[(size_t) i]; }

    /// Heat of the hottest live track covering f, 0 if none does.
    float heatAt (float f) const noexcept
    {
        float best = 0.0f;
        for (const auto& t : tracks)
        {
            if (! t.active || t.freq <= 0.0f) continue;
            const float gap = f < t.loHz ? (t.loHz - f) : f > t.hiHz ? (f - t.hiHz) : 0.0f;
            if (gap <= trackNearFrac * f) best = std::max (best, t.heat());
        }
        return best;
    }

private:
    // Loose on purpose: the tight matcher is what the hop defeats.
    static constexpr float  trackNearFrac    = 0.15f;  // twice a measured hop
    static constexpr double trackHoldSeconds = 20.0;
    std::array<Track, maxTracks> tracks {};
    int64_t framesAnalysed = 0;

    struct Suspect
    {
        bool   active      = false;
        bool   harmonic    = false;   // looked harmonically-related when first seen
        bool   hasFamily   = false;   // carries partials above it: a fundamental
        int    voiceFrames = 0;       // frames in which it looked like part of a voice
        float  freq        = 0.0f;
        float  lastLevel   = 0.0f;
        int    frames      = 0;
        int    missed      = 0;
        bool   reported    = false;
        float  reportLevel = 0.0f;    // level at the last event, for escalation
        int    sinceReport = 0;
        int    sinceReject = 0;

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
        ++framesAnalysed;

        // Wait until the analysis window holds nothing but real audio.
        //
        // The ring starts at zero, so for the first fftSize samples every tone
        // already present in the room appears to climb out of silence as the
        // window fills - a textbook ring, and detected as one. Arming the guard
        // was therefore an event that notched every steady tone in the room at
        // once: the room mode, the monitor hiss, the hum. They were then held,
        // because analysis runs pre-notch and a suppressed tone never looks gone.
        //
        // That is why switching the guard on made the vocal sound muffled with no
        // feedback anywhere near it. The test harness has always compensated for
        // this artefact with a pre-roll, which is exactly why the suite never
        // caught it: the workaround lived in the tests instead of the engine.
        if (samplesSeen < fftSize) return;

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

        // Complex, not magnitude-only. The phase advance of a bin between frames
        // locates a tone far more precisely than fitting three magnitudes ever
        // can - the difference between +-57 cents and a couple of cents at 174 Hz -
        // and the stability gate is only as good as the frequency estimate feeding
        // it. Discarding phase was why low rings could not be tracked.
        std::swap (curRe, prevRe);
        std::swap (curIm, prevIm);
        fft.performRealOnlyForwardTransform (scratch.data(), true);

        constexpr float norm = 2.0f / fftSize;
        for (int i = 0; i < numBins; ++i)
        {
            const float re = scratch[(size_t) (2 * i)];
            const float im = scratch[(size_t) (2 * i + 1)];
            (*curRe)[(size_t) i] = re;
            (*curIm)[(size_t) i] = im;
            const float m  = std::sqrt (re * re + im * im) * norm;
            const float db = juce::Decibels::gainToDecibels (m, -120.0f);
            mag[(size_t) i] = db;
            publishedMag[(size_t) i].store (db, std::memory_order_relaxed);
        }

        computeLowSpectrum();
        collectLowPeaks();

        for (auto& s : suspects) if (s.active) ++s.missed;
        peakCount = 0;

        // §4.5 input gate: don't chase noise between songs (spectrum still published)
        if (rmsDb >= params.inputGateDb)
        {
            const int floorHalfWidth = floorHalfWidthOverride > 0 ? floorHalfWidthOverride : 20;

            // TRACKING stays on the short window. The long one is 171 ms, longer
            // than a vibrato cycle at 5.4 Hz, so it averages the wobble away: a
            // singer's partial stops looking like it is moving and starts looking
            // like a rock-steady tone. Better frequency resolution, worse time
            // resolution - and stability is a question about time.
            //
            // The long window answers the question it is actually good at, which
            // is whether a harmonic family is present. See collectLowPeaks.
            const int firstBin = juce::jmax (2, (int) (params.minFreq / binHz));
            const int lastBin  = juce::jmin (numBins - 3, (int) (params.maxFreq / binHz));

            for (int i = firstBin; i <= lastBin; ++i)
            {
                const float here = mag[(size_t) i];
                if (here < params.floorDb) continue;
                if (here <= mag[(size_t) (i - 1)] || here <= mag[(size_t) (i + 1)]) continue;

                const float localFloor = medianAround (i, floorHalfWidth);
                if (here - localFloor < params.prominenceDb) continue;

                const float freq = refineFrequency (i, here);

                const float prom = here - localFloor;

                // Too broad to be a ring. Checked here rather than at firing so a
                // formant never becomes a suspect at all - it is the suspects that
                // turn into filters, and a wide feature spawns many of them.
                if (params.maxWidthHz > 0.0f)
                {
                    float wlo = 0.0f, whi = 0.0f;
                    peakWidth (freq, wlo, whi);
                    const float span = whi - wlo;
                    if (span > params.maxWidthHz)
                    {
                        // Wide is not automatically a formant. Let a FLAT one
                        // through; a rippled one is a voice and is dropped as before.
                        const bool plateau = span >= params.plateauMinHz
                                          && rippleDb (wlo, whi) <= params.plateauRipple;
                        if (! plateau) continue;
                    }
                }

                if (peakCount < maxPeaks)
                {
                    peakFreq[(size_t) peakCount]  = freq;
                    peakLevel[(size_t) peakCount] = here;
                    peakProm[(size_t) peakCount]  = prom;
                    ++peakCount;
                }
                else
                {
                    // The list is full. This scan runs from firstBin UPWARD, so
                    // simply dropping the newcomer keeps the 48 lowest peaks and
                    // silently discards everything above them - a frequency
                    // guillotine, not a prominence one. Measured at the rig: a
                    // 7 kHz ring in a band starting at 2.5 kHz, climbing 36 dB and
                    // plainly audible, never entered the peak list at all, so it
                    // could not become a suspect, so no gate ever saw it and no
                    // rejection could be logged for it. Raising the suspect table
                    // did nothing, because the peak never got that far.
                    //
                    // Keep the most prominent instead. A ring is by definition one
                    // of the most prominent things in the spectrum; the rank it
                    // must win is that one, not an accident of frequency order.
                    int weakest = 0;
                    for (int j = 1; j < maxPeaks; ++j)
                        if (peakProm[(size_t) j] < peakProm[(size_t) weakest]) weakest = j;

                    if (prom > peakProm[(size_t) weakest])
                    {
                        peakFreq[(size_t) weakest]  = freq;
                        peakLevel[(size_t) weakest] = here;
                        peakProm[(size_t) weakest]  = prom;
                    }
                }
            }
        }

        if (peakCount > maxSeenPeaks) maxSeenPeaks = peakCount;

        updateContext (rmsDb);

        // What did the bottom of the spectrum look like this frame?
        {
            vprobe = VoiceProbe{};
            vprobe.peaks   = peakCount;
            vprobe.lowUsed = loValid && params.minFreq < params.crossoverHz;
            int lowest = -1;
            for (int i = 0; i < peakCount; ++i)
                if (lowest < 0 || peakFreq[(size_t) i] < peakFreq[(size_t) lowest]) lowest = i;
            if (lowest >= 0)
            {
                vprobe.lowestHz = peakFreq[(size_t) lowest];
                vprobe.family   = hasPartialsAbove (lowest);
                for (int mult = 2; mult <= 3; ++mult)
                {
                    const float want = vprobe.lowestHz * (float) mult;
                    float best = 0.0f, err = 1.0e9f;
                    for (int k = 0; k < peakCount; ++k)
                    {
                        const float d = std::abs (peakFreq[(size_t) k] - want);
                        if (d < err) { err = d; best = peakFreq[(size_t) k]; }
                    }
                    (mult == 2 ? vprobe.partial2 : vprobe.partial3) = best;
                }
            }
        }

        // §4.4 harmonic guard, judged between PEAKS.
        //
        // The old test asked whether any energy sat at f/2 or 2f within 20 dB.
        // When a ring is quiet the noise floor itself is within 20 dB, so the room
        // counted as its own harmonic partner: every ring was born "musical",
        // denied the fast path, and only broke free once it was loud enough to put
        // the noise more than 20 dB down. Measured at the rig, one ring sat
        // prominent and undetected for six seconds for exactly this reason - which
        // is why a pronounced ring was always found instantly and a quiet one
        // never was.
        //
        // Noise is not a peak. Requiring two harmonically-related PEAKS means a
        // voice (which really does arrive with a harmonic series) still trips the
        // guard, and a lone ring in a quiet room no longer does.
        for (int i = 0; i < peakCount; ++i)
        {
            // Does this peak belong to a harmonic SERIES?
            //
            // Pairwise ratios of 2, 3 or 4 miss the obvious case: the 5th harmonic
            // of a sung note has no peer at 2x, 3x or 4x of ITSELF, so it looked
            // like a lone tone and got notched - measured, at 2037 Hz on a 400 Hz
            // note. Instead, propose that this peak is the n-th harmonic of some
            // fundamental and count how many other peaks land on that series.
            // Measured against random peaks, the first version of this test flagged
            // 97% of unrelated tones as harmonic - which denied nearly everything the
            // fast path, added 190 ms of required persistence and 6 dB of prominence,
            // and made real feedback crawl. Loose here is not cautious; it is blind.
            //
            // Four things make it discriminate, at 4% false positives while still
            // catching every partial of a simulated voice:
            //   - members must land within 1% of an exact multiple, not 4%
            //   - the series must be shallow (n <= 5, k <= 6), not stretch to 12
            //   - THREE other members, not two
            //   - one of them must be a low partial (k <= 3): a real series has
            //     energy near its fundamental, coincidences do not.
            bool harmonic = false;
            for (int n = 1; n <= 5 && ! harmonic; ++n)
            {
                const float f0 = peakFreq[(size_t) i] / (float) n;
                if (f0 < 40.0f) break;

                int members = 0;
                bool lowPartial = false;
                for (int j = 0; j < peakCount; ++j)
                {
                    if (j == i) continue;
                    const float k = peakFreq[(size_t) j] / f0;
                    const float nearest = std::round (k);
                    if (nearest >= 1.0f && nearest <= 6.0f && std::abs (k - nearest) < 0.008f)
                    {
                        ++members;
                        if (nearest <= 3.0f) lowPartial = true;
                    }
                }
                harmonic = members >= 3 && lowPartial;
            }

            // The series test above proposes f0 = peak/n for n up to 5 and asks
            // which peaks land on exact multiples. That can only ever see the
            // bottom of a series, and it is the TOP that gets notched: a voice at
            // 250 Hz puts its 2-8 kHz energy on harmonics 10 to 40, and no
            // candidate f0 is ever 250 Hz. Worse, the test is a ratio test, so an
            // error in f0 multiplies with k - at 0.8% tolerance the fifth partial
            // is already marginal.
            //
            // Measured live on 2026-09-02, singing into it with capture on: 6908
            // catches in 2.3 minutes, 49 a second, scattered across 158 different
            // frequency buckets and concentrated 73% in 2-8 kHz. One busy moment
            // caught 505, 757, 1001, 1249, 1501, 2263, 2522, 2776 and 3279 Hz -
            // gaps of 243, 244, 246, 252. That is a sung B3 and its harmonics 2
            // through 13, every one of them notched.
            //
            // Spacing is the robust question. Adjacent partials of a series are
            // separated by f0 wherever you are in it, so a comb can be recognised
            // from differences alone, with no f0 to estimate and no error to
            // multiply. A ring is a lone peak: it has no evenly-spaced companions.
            if (! harmonic)
                harmonic = sitsOnAComb (i);

            // A harmonic-series member must be markedly more prominent to be believed.
            if (harmonic && peakProm[(size_t) i] < params.prominenceDb + params.harmonicPromDb)
                continue;

            track (peakFreq[(size_t) i], peakLevel[(size_t) i], harmonic, hasPartialsAbove (i));
        }

        for (auto& s : suspects)
            if (s.active && s.missed > 2) s = Suspect{};

        // Runs whether or not the input gate is open: the baseline must keep
        // tracking a quiet room, or the first loud moment after silence reads as a
        // plateau everywhere.
        scanBands (rmsDb >= params.inputGateDb);

        hasPrevFrame = true;
    }

    /**
        The second way of seeing: band energy against that band's own history.

        Peak-picking cannot see a flat shelf - it has no local maximum, and the
        median window that measures prominence sits inside the shelf, so there is
        nothing to stand above. Measured: a 200-400 Hz plateau was invisible even
        with the prominence bar dropped to 2 dB (T42).

        So a plateau is found the other way round: each band is compared with a
        slow baseline of itself, and a run of adjacent bands that rises together,
        stays flat across the run, and holds for ~64 ms is reported as one wide
        event. Its width then buys it a comb of narrow notches rather than one
        wide cut.
    */
    void scanBands (bool gateOpen) noexcept
    {
        const float lo = juce::jmax (20.0f, params.minFreq);
        const float hi = juce::jmin ((float) (numBins - 2) * binHz, params.maxFreq);
        if (hi <= lo * 1.05f) return;

        const float ratio = std::pow (hi / lo, 1.0f / (float) numBands);

        for (int b = 0; b < numBands; ++b)
        {
            const float f0 = lo * std::pow (ratio, (float) b);
            const float f1 = f0 * ratio;
            const int   i0 = juce::jlimit (1, numBins - 2, (int) (f0 / binHz));
            const int   i1 = juce::jlimit (1, numBins - 2, (int) (f1 / binHz));

            float peak = -140.0f;
            for (int i = i0; i <= i1; ++i) peak = juce::jmax (peak, mag[(size_t) i]);
            bandLevel[(size_t) b] = peak;

            if (! bandsPrimed) { bandBaseline[(size_t) b] = peak; continue; }

            // Creep up, drop fast: a classic floor tracker. Upward at 0.02 dB per
            // frame (~4 dB/s) so a ring cannot quietly become its own baseline
            // before it is reported, downward fast so the floor follows a room
            // that goes quiet.
            float& base = bandBaseline[(size_t) b];
            base = peak > base ? base + 0.02f : juce::jmax (peak, base - 0.5f);
        }

        for (int b = 0; b < numBands; ++b) bandHist[(size_t) b][(size_t) histPos] = bandLevel[(size_t) b];
        histPos = (histPos + 1) % histLen;
        histCount = juce::jmin (histCount + 1, histLen);

        if (! bandsPrimed) { bandsPrimed = true; return; }
        if (! gateOpen) { for (auto& f : bandHot) f = 0; return; }

        for (int b = 0; b < numBands; ++b)
        {
            // Hysteresis: a band goes up at the threshold but does not come back
            // down until it is clearly below it. Without this the run's edges
            // flicker every frame, which reset both the stillness count and the
            // straight-line fit before either could accumulate.
            const float over = bandLevel[(size_t) b] - bandBaseline[(size_t) b];
            const bool  up   = bandHot[(size_t) b] > 0 ? over >= params.plateauRiseDb - 3.0f
                                                       : over >= params.plateauRiseDb;
            if (! up) { bandHot[(size_t) b] = 0; continue; }
            if (bandHot[(size_t) b] <= 0) bandEntry[(size_t) b] = bandLevel[(size_t) b];
            ++bandHot[(size_t) b];
        }

        // Runs of adjacent bands that have all been up for long enough.
        int b = 0;
        while (b < numBands)
        {
            if (bandHot[(size_t) b] < params.plateauFrames) { ++b; continue; }
            int e = b;
            while (e + 1 < numBands && bandHot[(size_t) (e + 1)] >= params.plateauFrames) ++e;

            const int   span   = e - b + 1;
            const float loHz   = lo * std::pow (ratio, (float) b);
            const float hiHz   = lo * std::pow (ratio, (float) (e + 1));

            // How much the run has gained since it first went up. A ring keeps
            // climbing; a held vowel arrives and stays put.
            float grew = 1.0e9f;
            for (int k = b; k <= e; ++k)
                grew = juce::jmin (grew, bandLevel[(size_t) k] - bandEntry[(size_t) k]);

            // ...and whether that climb is a straight line in dB.
            float slope = 0.0f, residual = 1.0e9f;
            straightness (b, e, slope, residual);

            probe = PlateauProbe { loHz, hiHz, grew, slope, residual,
                                   rippleDb (loHz, hiHz), span, bandHot[(size_t) b], false };

            if (span >= params.plateauBands && hiHz - loHz >= params.plateauMinHz
                && grew >= params.plateauGrowDb
                && slope > 0.0f && residual <= params.plateauStraight
                && rippleDb (loHz, hiHz) <= params.plateauRipple)
            {
                float top = -140.0f;
                for (int k = b; k <= e; ++k) top = juce::jmax (top, bandLevel[(size_t) k]);

                Event ev;
                ev.freq      = std::sqrt (loHz * hiHz);      // geometric centre
                ev.levelDb   = top;
                ev.growing   = true;
                ev.path      = 4;                            // plateau
                ev.ageMs     = 1000.0f * (float) (bandHot[(size_t) b] * hopSize) / (float) sampleRate;
                ev.widthLoHz = loHz;
                ev.widthHiHz = hiHz;
                pushEvent (ev);
                probe.fired = true;

                // Cool down, or it fires every frame for as long as the room hums.
                for (int k = b; k <= e; ++k) bandHot[(size_t) k] = -40;
            }
            b = e + 1;
        }
    }

    /**
        Locate a peak from the phase advance between frames, falling back to
        magnitude interpolation on the first frame or if the phase estimate is
        implausible. Standard phase-vocoder reassignment: the phase a bin actually
        advanced, minus the advance its centre frequency predicts, is the offset
        from that centre.
    */
    /**
        Who is in front of the microphone this frame.

        A fundamental is a peak carrying partials above it that is NOT itself a
        partial of something lower - otherwise a voice's own second harmonic
        counts as a second voice and every singer reads as a duet.
    */
    void updateContext (float rmsDb) noexcept
    {
        Context c;
        c.levelDb = rmsDb;

        // Spectral flatness over the watched band: geometric mean over arithmetic.
        // Noise sits near 1, a tone near 0, and it is what separates "empty room"
        // from "someone is actually making a sound".
        {
            double logSum = 0.0, sum = 0.0; int n = 0;
            const int lo = juce::jmax (2, (int) (params.minFreq / binHz));
            const int hi = juce::jmin (numBins - 2, (int) (juce::jmin (8000.0f, params.maxFreq) / binHz));
            for (int i = lo; i <= hi; ++i)
            {
                const double lin = juce::Decibels::decibelsToGain ((double) mag[(size_t) i]);
                logSum += std::log (juce::jmax (1.0e-9, lin));
                sum    += lin;
                ++n;
            }
            if (n > 0 && sum > 0.0)
                c.flatness = (float) juce::jlimit (0.0, 1.0,
                    std::exp (logSum / n) / (sum / n));
        }

        int   rawFamilies = 0;
        float lowestF0    = 0.0f;
        for (int i = 0; i < peakCount; ++i)
        {
            if (! hasPartialsAbove (i)) continue;
            const float f = peakFreq[(size_t) i];

            // Is this peak a partial of ANY lower peak, whether or not that one
            // was itself judged to carry a family? The first version required the
            // lower one to be flagged too, which fails whenever a fundamental is
            // weak - and then a singer's own second and third harmonics each
            // counted as a voice, so one person read as a trio.
            bool isPartial = false;
            for (int j = 0; j < peakCount && ! isPartial; ++j)
            {
                if (j == i) continue;
                const float lower = peakFreq[(size_t) j];
                if (lower <= 20.0f || lower >= f - 1.0f) continue;
                const float ratio = f / lower;
                if (std::abs (ratio - std::round (ratio)) < 0.04f && std::round (ratio) >= 2.0f)
                    isPartial = true;
            }
            if (isPartial) continue;

            ++rawFamilies;
            if (lowestF0 <= 0.0f || f < lowestF0) lowestF0 = f;
        }

        // Hold the verdict over ~0.4 s. A scene that flips every 5 ms is useless
        // to read and worse to act on: consonants, breaths and the gaps between
        // notes are not a change of who is in the room, and calling those "room"
        // while someone is plainly singing was the second fault the rig found.
        ctxFamilies[(size_t) ctxPos] = rawFamilies;
        ctxF0[(size_t) ctxPos]       = lowestF0;
        ctxLevel[(size_t) ctxPos]    = rmsDb;
        ctxPos = (ctxPos + 1) % ctxHistory;
        if (ctxCount < ctxHistory) ++ctxCount;

        int pitched = 0, poly = 0;
        float loudest = -140.0f, f0Sum = 0.0f; int f0Count = 0;
        for (int i = 0; i < ctxCount; ++i)
        {
            if (ctxFamilies[(size_t) i] >= 1) { ++pitched; f0Sum += ctxF0[(size_t) i]; ++f0Count; }
            if (ctxFamilies[(size_t) i] >= 2) ++poly;
            loudest = juce::jmax (loudest, ctxLevel[(size_t) i]);
        }

        c.levelDb  = loudest;                       // the loudest moment, not this instant
        c.f0Hz     = f0Count > 0 ? f0Sum / (float) f0Count : 0.0f;
        c.families = ctxCount == 0 ? 0
                   : (poly * 2 >= ctxCount)     ? 2     // polyphonic most of the window
                   : (pitched * 3 >= ctxCount)  ? 1     // pitched some of the time: a voice
                                                : 0;
        ctx = c;
    }

    /// Sub-bin interpolation without phase history, for the long analysis.
    static float parabolicIn (const float* arr, int k, float bw) noexcept
    {
        const float ym1 = arr[k - 1], y0 = arr[k], yp1 = arr[k + 1];
        const float denom = (ym1 - 2.0f * y0 + yp1);
        const float delta = (std::abs (denom) > 1.0e-6f) ? 0.5f * (ym1 - yp1) / denom : 0.0f;
        return ((float) k + juce::jlimit (-0.5f, 0.5f, delta)) * bw;
    }

    float refineFrequency (int k, float levelDb) noexcept
    {
        // A flat-topped peak is not one sinusoid. Measured at the rig 2026-10-03
        // on a step straight to 18 dB of margin: a cluster of modes 70 Hz wide
        // started together, each bin carrying its own component, and the loudest
        // bin hopped between them frame to frame. The phase estimate below is
        // exact for each bin, so the suspect read "unstable" - spread 53 Hz
        // against 15.8 - from -77 dB to -37 dB, and the sustain path took 1.3 s
        // to call it at -22. On a gentle climb only the top mode exceeds unity,
        // the peak is clean from the start, and the same mode is caught at -68.
        //
        // The discriminator is the window itself. One Hann-windowed sinusoid puts
        // at most TWO bins within 5.5 dB of its top - the third is 9.5 dB down
        // by the window's shape, wherever the tone sits between bins. Three or
        // more bins within that cannot be one tone. Only then is the magnitude
        // centroid of the neighbourhood used, as the cluster's centre; it
        // barely moves while the loudest bin hops. Everything else keeps the
        // sub-bin phase estimate below, which the vibrato and location tests
        // depend on: a centroid applied to every flat-ish top notched the
        // singer in four unit tests at once. Anything wider than flatTopMaxBins
        // is a formant, not a ring, and keeps the old estimate for the width
        // gate to refuse.
        // And only above the voice (flatTopMinHz): a vibrato partial sweeping
        // half a bin inside one frame smears into three bins too, and the
        // centroid costs the vibrato test its precision (T4 notched a sung
        // 400 Hz note). Vocal partials up there are weak and the modes are
        // dense; the clusters were measured at 9.9 and 14.6 kHz.
        // Counted over a neighbourhood, not a contiguous run: the same night a
        // 14.6 kHz ring came up as a DOUBLET - two modes 47 Hz apart with a 5 dB
        // dip between them - and the loudest bin hopped across the dip while a
        // contiguous rule counted two bins and stood aside (1232 ms to call it,
        // at -31 dB). One sinusoid cannot put three bins within flatTopDb of its
        // top anywhere within +-3 bins, dip or no dip. The centroid is taken
        // over the whole neighbourhood so it does not jump when a bin crosses
        // the threshold.
        if ((float) k * binHz >= params.flatTopMinHz)
        {
            constexpr int reach = 3;
            const int lo = juce::jmax (1, k - reach), hi = juce::jmin (numBins - 2, k + reach);
            int within = 0;
            for (int b = lo; b <= hi; ++b)
                if (mag[(size_t) b] >= levelDb - params.flatTopDb) ++within;
            if (within >= 3 && within <= params.flatTopMaxBins)
            {
                double num = 0.0, den = 0.0;
                for (int b = lo; b <= hi; ++b)
                {
                    const double w = std::pow (10.0, (double) mag[(size_t) b] / 20.0);
                    num += w * (double) b; den += w;
                }
                if (den > 0.0) return (float) (num / den) * binHz;
            }
        }

        // parabolic on magnitudes - the fallback, and the sanity check
        const float ym1 = mag[(size_t) (k - 1)], y0 = levelDb, yp1 = mag[(size_t) (k + 1)];
        const float denom = (ym1 - 2.0f * y0 + yp1);
        const float delta = (std::abs (denom) > 1.0e-6f) ? 0.5f * (ym1 - yp1) / denom : 0.0f;
        const float parabolic = (k + juce::jlimit (-0.5f, 0.5f, delta)) * binHz;

        if (! hasPrevFrame) return parabolic;

        const float rn = (*curRe)[(size_t) k],  in = (*curIm)[(size_t) k];
        const float rp = (*prevRe)[(size_t) k], ip = (*prevIm)[(size_t) k];

        // arg(z_now * conj(z_prev)) - one atan2, no unwrapping of absolute phase
        const float cr = rn * rp + in * ip;
        const float ci = in * rp - rn * ip;
        if (cr == 0.0f && ci == 0.0f) return parabolic;

        constexpr float twoPi = 6.283185307179586f;
        const float expected = twoPi * (float) hopSize * (float) k / (float) fftSize;
        float d = std::atan2 (ci, cr) - expected;
        d -= twoPi * std::round (d / twoPi);                 // wrap to +-pi

        const float trueBin = (float) k + d * (float) fftSize / (twoPi * (float) hopSize);

        // A tone at this peak cannot really be a bin away; if the phase says it is,
        // the bin is not a clean sinusoid and the magnitude fit is the safer answer.
        if (std::abs (trueBin - (float) k) > 1.0f) return parabolic;
        return trueBin * binHz;
    }

    /**
        True if the candidate's pitch is wobbling like vibrato rather than sitting still.

        This is the one discriminator that works on character rather than frequency:
        a sung note oscillates a few times a second, an acoustic feedback loop does
        not move at all. The stability gate cannot see it because a wobble takes
        150-250 ms to complete and that gate looks at ~32 ms - through a slit, where
        every note looks steady.
    */
    bool looksLikeVibrato (const Suspect& s, int n) const noexcept
    {
        if (n < 12) return false;

        float sum = 0.0f;
        for (int i = 1; i <= n; ++i)
            sum += s.wFreq[(size_t) ((s.windowPos - i + windowSize) % windowSize)];
        const float mean = sum / (float) n;
        if (mean <= 0.0f) return false;

        const float dead = mean * 0.0005f;   // ignore dither about the mean
        float lo = 1.0e9f, hi = -1.0e9f;
        int crossings = 0, prevSign = 0;

        for (int i = n; i >= 1; --i)          // oldest -> newest
        {
            const float f = s.wFreq[(size_t) ((s.windowPos - i + windowSize) % windowSize)];
            lo = juce::jmin (lo, f);
            hi = juce::jmax (hi, f);

            const float d = f - mean;
            const int sign = d > dead ? 1 : (d < -dead ? -1 : 0);
            if (sign != 0)
            {
                if (prevSign != 0 && sign != prevSign) ++crossings;
                prevSign = sign;
            }
        }

        const float depth   = (hi - lo) / mean;                                   // relative swing
        const float seconds = (float) (n * hopSize) / (float) sampleRate;
        const float rateHz  = (float) crossings / (2.0f * seconds);               // full cycles/sec

        return depth  >= params.vibratoDepth
            && rateHz >= params.vibratoMinHz
            && rateHz <= params.vibratoMaxHz;
    }

    /**
        Both tolerances scale with frequency, because sub-bin interpolation jitter
        does. A flat +-5 Hz is 0.05% at 10 kHz - far tighter than the estimator's
        own noise - so high rings never held a track long enough to qualify. That
        is the band this room actually rings in.
    */
    float matchTolHz (float f) const noexcept
    {
        return juce::jmax (juce::jmax (3.0f * params.stabilityHz, 0.005f * f), binHz);
    }

    /// <summary>
    /// How far a peak may wander and still count as steady.
    ///
    /// Three floors, and the third is the one that was missing: the estimator
    /// cannot place a peak more precisely than roughly a quarter of a bin, so
    /// demanding +-5 Hz at 188 Hz - where a bin is 47 Hz wide - asks for precision
    /// the measurement does not have, and no low ring can ever qualify. High
    /// frequencies were fixed by the proportional term; low frequencies need the
    /// resolution term.
    /// </summary>
    float stabilityTolHz (float f) const noexcept
    {
        // The quarter-bin resolution floor is gone: phase reassignment locates a
        // tone to a couple of cents at any frequency, so the gate can go back to
        // asking what it actually wants - a few Hz of drift - instead of being
        // widened to accommodate a measurement that could not see straight. That
        // widening was what let a singer's vibrato through.
        return juce::jmax (params.stabilityHz, 0.0008f * f);
    }

    /// <summary>Frequency spread across the last n frames of a suspect's window.</summary>
    float spreadOver (const Suspect& s, int n) const noexcept
    {
        float lo = 1.0e9f, hi = -1.0e9f;
        for (int i = 1; i <= n; ++i)
        {
            const float f = s.wFreq[(size_t) ((s.windowPos - i + windowSize) % windowSize)];
            lo = juce::jmin (lo, f);
            hi = juce::jmax (hi, f);
        }
        return hi - lo;
    }

    /// How much the level has risen across the last n frames of the window.
    /// Feedback arrives from nothing and builds; a room mode, a monitor hiss or an
    /// HVAC tone is simply always there, and reads ~0 here.
    float riseOver (const Suspect& s, int n) const noexcept
    {
        const int newest = (s.windowPos - 1 + windowSize) % windowSize;
        const int oldest = (s.windowPos - n + windowSize) % windowSize;
        return s.wLevel[(size_t) newest] - s.wLevel[(size_t) oldest];
    }

    /// Is this peak one tooth of an evenly-spaced comb - i.e. a harmonic series?
    ///
    /// For each other peak below it, take the gap as a candidate fundamental and
    /// count how many further peaks land on that grid. Differences, not ratios, so
    /// an error in the estimate does not multiply with the partial number. Two
    /// other teeth are required, which random peaks rarely supply and a voice
    /// always does.
    /**
        Does this peak carry a harmonic family ABOVE it?

        sitsOnAComb looks downward, so it can never flag a fundamental - there is
        nothing below one - and the vibrato veto cannot help either: +-0.5% of
        110 Hz is 0.55 Hz, a fortieth of a short-window bin. That is how a sung
        110 Hz note lost its own fundamental to a notch (T41).

        This was written once before and reverted, because at 23.4 Hz bins a low
        voice's partials smear together and never reach the peak list at all. With
        the long window below the crossover they are 19 bins apart and resolve.
    */
    bool hasPartialsAbove (int i) const noexcept
    {
        const float f = peakFreq[(size_t) i];
        if (f <= 0.0f) return false;

        int found = 0;
        for (int mult = 2; mult <= 4; ++mult)
        {
            const float want = f * (float) mult;
            const float tol  = juce::jmax (0.03f * want, 2.0f * loBinHz);
            bool hit = false;
            for (int k = 0; k < peakCount && ! hit; ++k)
                if (k != i && std::abs (peakFreq[(size_t) k] - want) <= tol) hit = true;
            // ...and in the long window, where a low voice's partials actually
            // resolve. Either list will do; a partial is a partial.
            for (int k = 0; k < loPeakCount && ! hit; ++k)
                if (std::abs (loPeakFreq[(size_t) k] - want) <= tol) hit = true;
            if (hit) ++found;
        }
        return found >= 2;          // two of the first three partials is a voice
    }

    bool sitsOnAComb (int i) const noexcept
    {
        const float f = peakFreq[(size_t) i];

        for (int j = 0; j < peakCount; ++j)
        {
            if (j == i) continue;
            const float d = f - peakFreq[(size_t) j];
            // A sung fundamental. Below ~70 Hz the grid is so fine that anything
            // lands on it; above ~500 Hz it is not a voice.
            if (d < 70.0f || d > 500.0f) continue;

            // The peak must sit ON the grid, not merely near something spaced d
            // away: if d is really the fundamental then f is a whole number of
            // them. Two arbitrary peaks always define SOME spacing, which is why
            // the first version of this - two teeth and nothing else - found a
            // comb through almost anything once the spectrum was busy. Measured:
            // with 45 unrelated tones present a real ring was never caught at all,
            // because it was being called harmonic every frame.
            const float partial = f / d;
            if (std::abs (partial - std::round (partial)) > 0.06f) continue;
            if (std::round (partial) < 2.0f) continue;

            const float tol = juce::jmax (0.10f * d, 1.5f * binHz);
            int teeth = 0;
            bool reachesDown = false;
            for (int k = 0; k < peakCount; ++k)
            {
                if (k == i) continue;
                const float other = peakFreq[(size_t) k];
                const float off = std::abs (other - f);
                const float nearest = std::round (off / d);
                if (nearest >= 1.0f && nearest <= 12.0f && std::abs (off - nearest * d) < tol)
                {
                    ++teeth;
                    // A voice has partials all the way down to its fundamental. A
                    // grid that only exists up here is a coincidence.
                    // Relative to the spacing, not an absolute frequency. This
                    // asked for a tooth below 1200 Hz, which for a voice at 290 Hz
                    // means harmonic four or lower - and when only the fifth and up
                    // are prominent peaks the test fails and the whole series gets
                    // notched. Measured on the rig 2026-09-06: a sung D4 with
                    // harmonics 7 through 13 caught, 22% of busy windows showing
                    // the comb, 20% of all catches below 1500 Hz.
                    //
                    // A series that reaches within six of its own fundamental is a
                    // series. Which absolute frequency that lands on depends on the
                    // note being sung, and the test should not.
                    if (other < f && other <= 6.5f * d) reachesDown = true;
                }
            }
            if (teeth >= 3 && reachesDown) return true;
        }
        return false;
    }

    /// The -20 dB skirts of the peak nearest f. Answers "how wide was the thing
    /// you notched", which is what decides whether one filter could ever have
    /// covered it.
    ///
    /// Twenty dB, not six: a pure tone through a Hann window is already about 6 dB
    /// down in the bins either side of its centre, so a -6 dB rule stops before it
    /// starts and reports every peak as zero wide. Nor does the walk require the
    /// spectrum to descend monotonically - with real noise on the skirts it does
    /// not, and insisting on it truncates at the first ripple.
    /// Width in whichever analysis actually resolves this peak. A 70 Hz ring is
    /// three bins in the short window and twelve in the long one, and measuring it
    /// in the short one is how every low ring came back as "70 Hz wide" - the
    /// resolution floor, not the ring.
    void peakWidthIn (const float* arr, int nBins, float bw, float f,
                      float& loHz, float& hiHz) const noexcept
    {
        loHz = hiHz = f;
        const int centre = (int) std::round (f / bw);
        if (centre < 2 || centre >= nBins - 2) return;

        constexpr float dropDb = 20.0f;
        const int maxSpan = juce::jmax (8, (int) (1500.0f / bw));

        const float peak = arr[centre];
        int lo = centre;
        while (lo > 1 && centre - lo < maxSpan && peak - arr[lo - 1] < dropDb) --lo;
        int hi = centre;
        while (hi < nBins - 2 && hi - centre < maxSpan && peak - arr[hi + 1] < dropDb) ++hi;

        loHz = (float) lo * bw;
        hiHz = (float) hi * bw;
    }

    void peakWidth (float f, float& loHz, float& hiHz) const noexcept
    {
        // Below the crossover the long window is the honest one.
        if (loValid && f < params.crossoverHz)
        {
            peakWidthIn (magLo.data(), loNumBins, loBinHz, f, loHz, hiHz);
            return;
        }

        loHz = hiHz = f;
        const int centre = (int) std::round (f / binHz);
        if (centre < 2 || centre >= numBins - 2) return;

        constexpr float dropDb = 20.0f;
        constexpr int   maxSpan = 64;          // ~1.5 kHz; past that it is not a peak

        const float peak = mag[(size_t) centre];
        int lo = centre;
        while (lo > 1 && centre - lo < maxSpan && peak - mag[(size_t) (lo - 1)] < dropDb) --lo;
        int hi = centre;
        while (hi < numBins - 2 && hi - centre < maxSpan && peak - mag[(size_t) (hi + 1)] < dropDb) ++hi;

        loHz = (float) lo * binHz;
        hiHz = (float) hi * binHz;
    }

    float ageMsOf (const Suspect& s) const noexcept
    {
        return (float) s.frames * (float) hopSize / (float) sampleRate * 1000.0f;
    }

    /**
        Is this peak part of what the singer is doing RIGHT NOW?

        The harmonic tests ask whether a peak looks like part of some voice. That
        is not the same question, and the difference is what hurt at the rig: a
        ring at 287 Hz, sitting 2% from a sung D4, was shielded by the voice
        protections and cut by nothing for over a second.

        A peak the singer is not producing gets no voice protection, whatever its
        neighbours look like. With no voice present at all, nothing is protected.
    */
    bool belongsToTheVoice (float f) const noexcept
    {
        // Only strip protection when we are CONFIDENT someone is singing and this
        // peak is not part of it. With no voice detected - silence, the first
        // 150 ms of a note before the pitch settles, an unpitched consonant - the
        // old conservative rules stand.
        //
        // Getting this backwards notched four voice tests at once, including a
        // sung note being cut on its own attack.
        if (ctx.families <= 0 || ctx.f0Hz <= 20.0f) return true;

        // Both frequencies belonging to ONE series, not one being a multiple of
        // the other. A voice whose fundamental is missing - a thin vocal through a
        // high-passed channel - is estimated at a higher partial, and then its own
        // partials are not integer multiples of it: 756 is 1.5x 504, and T31 lost
        // its 3rd and 5th partials to exactly that.
        //
        // So: does f/f0 land on a ratio of small whole numbers?
        for (int m = 1; m <= 8; ++m)
        {
            const float target = f * (float) m;
            const float k = target / ctx.f0Hz;
            const float nearest = std::round (k);
            if (nearest < 1.0f) continue;
            if (std::abs (target - nearest * ctx.f0Hz) <= params.voiceTolerance * target)
                return true;
        }
        return false;
    }

    void track (float freq, float levelDb, bool harmonic, bool family) noexcept
    {
        // Associate with an existing candidate (a wider window than the stability
        // test - the peak may wander a little before it locks).
        for (auto& s : suspects)
        {
            if (! s.active) continue;
            if (std::abs (s.freq - freq) > matchTolHz (freq)) continue;

            s.missed    = 0;
            s.frames   += 1;
            s.lastLevel = levelDb;
            s.harmonic  = harmonic;   // re-judged every frame, never sticky
            s.hasFamily = family;
            if (harmonic || family) ++s.voiceFrames;
            s.freq      = 0.8f * s.freq + 0.2f * freq;

            s.wFreq[(size_t) s.windowPos]  = freq;
            s.wLevel[(size_t) s.windowPos] = levelDb;
            s.windowPos = (s.windowPos + 1) % windowSize;

            // §4.2/§4.3 over the last N frames only.
            //
            // Anything that looks harmonic is judged over a much longer window. A
            // partial carrying vibrato moves across FFT bins, and the scalloping
            // that produces is amplitude modulation: over 32 ms a rising quarter of
            // that wobble supplies the ~1 dB the growth gate wants, and the partial
            // is called a building ring. Over ~300 ms the modulation completes more
            // than a cycle and nets out to nothing, while a real ring - which is
            // still climbing the whole time - passes either way.
            //
            // This is what was notching the voice: measured live, harmonics 2
            // through 13 of a sung B3, 49 catches a second. Path B already refuses
            // harmonics; path A was the way through.
            const int growthWindow = s.harmonic ? params.voiceFrames : params.persistFrames;
            const int n = juce::jlimit (1, windowSize, juce::jmin (s.frames, growthWindow));
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
            const float stabTol = 2.0f * stabilityTolHz (s.freq);

            // Feedback climbs; it does not lurch. One frame collapsing more than
            // 2 dB inside the window is a transient, not a loop building.
            bool monotonic = true;
            for (int i = 1; i < n; ++i)
            {
                const int newer = (s.windowPos - i + windowSize) % windowSize;
                const int older = (s.windowPos - i - 1 + windowSize) % windowSize;
                if (s.wLevel[(size_t) newer] < s.wLevel[(size_t) older] - 2.0f) { monotonic = false; break; }
            }

            // Growth is a RATE. Comparing a fixed dB figure against whatever the
            // window happens to be couples the threshold to the hop size: halving
            // the hop once made this test 3x stricter by accident, so only violently
            // building rings qualified and steady ones were ignored until they were
            // already loud. Scale the requirement to the window's real duration.
            const float windowSec  = (float) (n * hopSize) / (float) sampleRate;
            // Judged here because the bar itself depends on it.
            // The voice protections only apply to what the voice is actually
            // producing. Everything else is judged on the fast path, however
            // harmonic-looking its neighbours happen to be.
            const bool  mine        = belongsToTheVoice (s.freq);
            const bool  inVoiceBand = mine && (s.freq < params.voiceBandHz || s.harmonic);
            const float growthBar  = inVoiceBand ? params.growthDb : params.growthDbFast;
            const float needGrowth = growthBar * (windowSec / 0.1f);
            // In the voice band, stability alone cannot tell a held note from a ring,
            // so look for ~300 ms and veto anything that wobbles. Above it, keep the
            // fast path: a voice's upper harmonics swing too many Hz to pass the
            // stability gate anyway, and that is where the real feedback lives.
            // Vibrato is worth testing for wherever a harmonic series suggests a
            // voice, not only below 2 kHz - a singer's upper partials sit well
            // above that and wobble proportionally harder. And a short stability
            // window can never reject vibrato on its own: at the turning points of
            // the wobble the pitch is momentarily still, which is exactly when a
            // 32 ms look sees a rock-steady tone.
            const bool  voiceBand = inVoiceBand;
            const int   baseNeed  = voiceBand ? params.voiceFrames : params.persistFrames;
            const int   need   = baseNeed + (s.harmonic ? params.harmonicExtra : 0);

            if (! s.reported)
            {
                const bool wobbling = voiceBand && mine
                    && looksLikeVibrato (s, juce::jmin (s.frames, params.voiceFrames));

                // Path A - the classic attack: stable, and climbing.
                bool fire = s.frames >= need && spread <= stabTol
                            && growth >= needGrowth && monotonic;
                int firedPath = fire ? 1 : 0;

                // Path B - the slow creep. A ring hovering near unity loop gain
                // grows too slowly to trip the growth gate, but it sits dead
                // still for hundreds of ms, which nothing musical does. Held
                // notes arrive with harmonics, so the guard covers those.
                // Judged over the suspect's LIFE, not this frame. A voice partial
                // looks harmonic most of the time and occasionally does not - one
                // such frame was all the slow path needed, which is how T41's
                // leak moved from the fundamental to its ninth partial. A ring
                // never looks harmonic at all, so the bar is low.
                const bool everVoice = mine && s.voiceFrames * 4 >= s.frames;

                bool sustainWander = false;
                if (! fire && ! s.harmonic && ! s.hasFamily && ! everVoice)
                {
                    const int sustainN = juce::jlimit (
                        1, windowSize - 1,
                        (int) (params.sustainSeconds * (float) sampleRate / (float) hopSize + 0.5f));
                    const bool longEnough = s.frames >= sustainN + 1;
                    const bool steady     = spreadOver (s, sustainN) <= stabTol;
                    // ...and it must have got here from somewhere. Stability alone
                    // describes every steady tone in the room, so this path was
                    // notching room modes and monitor hiss and then holding them
                    // down for as long as they existed - which is always. Measured
                    // at the rig: 97.3% of notches pinned at target, 0.1% ever
                    // releasing, and a guard that muffled the vocal with no
                    // feedback anywhere near it. A ring arrives from nothing and
                    // builds; furniture reads ~0 dB here.
                    // Measured over the whole window, not just the sustain span:
                    // the slowest ring recorded at the rig climbs 3.6 dB/s, which is
                    // only 1.1 dB across 300 ms - inside the noise. Over the full
                    // ~680 ms it is 2.5 dB, and furniture is still 0.
                    const int  riseN = juce::jlimit (sustainN, windowSize - 1, s.frames - 1);
                    const bool grew  = riseOver (s, riseN) >= params.sustainRiseDb;
                    fire = longEnough && steady && grew;
                    if (fire) firedPath = 2;
                    // Steady over 30 ms but wandering over 300 ms is a different
                    // failure from growing too slowly, and wants a different fix.
                    sustainWander = longEnough && ! steady;
                }

                // Say why, when it has waited long enough that "not yet" is an
                // answer rather than impatience. Throttled per suspect so a
                // sustained near-miss reports once every ~250 ms, not every frame.
                if (! (fire && ! wobbling) && s.frames >= need)
                {
                    if (++s.sinceReject >= 48)
                    {
                        s.sinceReject = 0;
                        const Reason why = wobbling            ? Reason::Vibrato
                                         : spread > stabTol    ? Reason::Unstable
                                         : s.harmonic          ? Reason::Harmonic
                                         : sustainWander       ? Reason::Drifting
                                                               : Reason::NoGrowth;
                        pushReject ({ s.freq, levelDb, (int) why, s.frames, (float) spread, (float) stabTol });
                    }
                }

                if (fire && ! wobbling)
                {
                    s.reported    = true;
                    s.reportLevel = s.lastLevel;
                    s.sinceReport = 0;
                    if (s.lastLevel >= params.actionDb)
                    {
                        float wlo = 0.0f, whi = 0.0f;
                        peakWidth (s.freq, wlo, whi);
                        pushEvent ({ s.freq, s.lastLevel, true, firedPath, ageMsOf (s), wlo, whi });
                    }
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
                // "Still there" is not news. Analysis runs PRE-notch, so a tone the
                // filter is holding down perfectly still reads at full strength
                // here - "surviving" is therefore true for as long as the room has
                // the resonance, which is forever. Re-firing on it refreshed the
                // notch's hold every 250 ms, so the release timer never elapsed and
                // the filter stood at full depth permanently: 97.3% of notch
                // samples pinned at target, 0.1% ever releasing, and a guard that
                // muffled the vocal with nothing ringing.
                //
                // If the notch is genuinely not enough, the tone climbs again and
                // `climbing` says so. That is the informative signal; mere presence
                // is not.
                // Re-enabled, narrowly, and the reason the old blanket version was
                // wrong no longer holds.
                //
                // Measured at the rig 2026-09-30: a 9533 Hz ring screaming into
                // the microphone at +45 dB for eleven seconds with ZERO
                // detections logged. It had already won and gone steady, so it
                // was not climbing, so nothing re-fired, so the 44 dB notch
                // holding it down bled away underneath it at exactly the release
                // rate - cut -44.1 dB, then -30.6, then -26.9, while its output
                // climbed +1 dB to +18 dB. The guard grabbed the howl and then
                // let go of it while the singer was still listening.
                //
                // The old comment is right that mere PRESENCE is not news: a tone
                // being held down perfectly still reads at full strength here,
                // because analysis is pre-notch, so "surviving" is true forever
                // for any room resonance. That is why it used to pin two dozen
                // filters at full depth and sound like a high shelf.
                //
                // Two things make this safe now. It takes LOUD, not merely
                // present - above -25 dB, which is the top 5% of 52,053 real
                // detections, so room furniture cannot qualify. And it reports
                // growing=false, so the three-rung ladder (added since that
                // comment was written) refuses to escalate on it: the filter
                // holds where it is and stops bleeding, which is all that was
                // ever needed. It cannot dig.
                // Once every ~160 ms, not every 16. It exists to refresh a hold
                // measured in seconds, and at three frames it fired sixty times a
                // second - same protection, ten times the telemetry and ten times
                // the log.
                // ...and ONLY in the band howls live in, and never on the singer.
                // Shipped without this on 2026-09-30 and measured the next day,
                // the first time the guard had ever been the only path to the
                // monitors: speech cut by a median 14 dB at 80-200 Hz and 11 dB at
                // 200-400 Hz, worst bin -27 dB. Seven filters sitting on a spoken
                // vowel. A filter wrongly placed on a voice used to bleed away in
                // two seconds; the hold kept it there for as long as the voice
                // did, which is exactly the risk the regroup named and shipped.
                // Howls are above 2 kHz (85% of every catch on record); a voice's
                // fundamental and first harmonics are not.
                const bool surviving = s.sinceReport >= 30
                                    && s.lastLevel >= params.sustainHoldDb
                                    && s.freq >= params.sustainHoldMinHz
                                    && ! partialOfTheVoice (s.freq);
                if (climbing || surviving)
                {
                    s.reportLevel = s.lastLevel;
                    s.sinceReport = 0;
                    // Only a tone that is still CLIMBING has earned a deeper cut.
                    // Analysis runs pre-notch, so a notch can never be seen to
                    // succeed: a room tone stays fully visible however well it is
                    // being suppressed, "surviving" is therefore true forever, and
                    // it used to escalate on that alone. Measured at the rig: 97.3%
                    // of notch samples pinned at their target, 0.1% ever releasing,
                    // a fifth of them at the cap - two dozen deep filters held
                    // permanently, which is a high shelf, and it sounded like one.
                    if (s.lastLevel >= params.actionDb)
                    {
                        float wlo = 0.0f, whi = 0.0f;
                        peakWidth (s.freq, wlo, whi);
                        pushEvent ({ s.freq, s.lastLevel, climbing, 3, ageMsOf (s), wlo, whi });
                    }
                }
            }
            return;
        }

        Suspect* slot = nullptr;
        for (auto& s : suspects)
            if (! s.active) { slot = &s; break; }

        // A full table used to mean the newcomer had to be LOUDER than something
        // already tracked to get a slot at all. That is exactly backwards for
        // feedback, which arrives quiet and becomes loud: a ring emerging at -93 dB
        // into a table of -70 dB tones evicted nothing and was dropped, every
        // frame, until it had grown loud enough to displace someone.
        //
        // Measured at the rig on a marked miss: a 10.6 kHz ring climbed 50 dB in
        // 1.5 s and was first seen at -43 dB, right at the top, having been audible
        // and visible on the meter for two seconds. It was not rejected by a gate -
        // it was never tracked. The rejection log could not show this either, since
        // nothing that is not a suspect can be reported as one.
        //
        // Evict on staleness, not on level. A suspect nobody has seen for a few
        // frames is finished; a quiet newcomer might be the next ring.
        if (slot == nullptr)
        {
            int stalest = 0;
            for (auto& s : suspects)
                if (! s.reported && s.missed >= stalest) { stalest = s.missed; slot = &s; }

            // Still nothing? Take the quietest un-fired suspect regardless of how
            // it compares to the newcomer - being new is not a reason to lose.
            if (slot == nullptr)
            {
                float weakest = 1.0e9f;
                for (auto& s : suspects)
                    if (! s.reported && s.lastLevel < weakest) { weakest = s.lastLevel; slot = &s; }
            }
            if (slot == nullptr) return;    // every slot is already reporting
        }

        *slot = Suspect{};
        slot->active     = true;
        slot->harmonic   = harmonic;
        slot->hasFamily  = family;
        slot->freq       = freq;
        slot->lastLevel  = levelDb;
        slot->frames     = 1;
        slot->wFreq[0]   = freq;
        slot->wLevel[0]  = levelDb;
        slot->windowPos  = 1;
    }

    /**
        How uneven the top of a feature is: peak minus median across its own span.

        A sung vowel's formant carries the harmonic ripple of the voice under it,
        so this is large. A broad feedback plateau is smooth, so it is small. That
        difference is the only thing that makes a wide feature safe to act on.
    */
    float rippleDb (float loHz, float hiHz) noexcept
    {
        const int lo = juce::jmax (0, (int) (loHz / binHz));
        const int hi = juce::jmin (numBins - 1, (int) (hiHz / binHz));
        if (hi - lo < 3) return 1000.0f;                  // too few bins to judge

        int count = 0;
        float top = -1000.0f;
        for (int i = lo; i <= hi && count < (int) medianScratch.size(); ++i)
        {
            medianScratch[(size_t) count++] = mag[(size_t) i];
            top = juce::jmax (top, mag[(size_t) i]);
        }
        if (count == 0) return 1000.0f;

        const int mid = count / 2;
        std::nth_element (medianScratch.begin(), medianScratch.begin() + mid,
                          medianScratch.begin() + count);
        return top - medianScratch[(size_t) mid];
    }

    /// The low band again, at four times the frequency resolution. Same ring, a
    /// longer window: 8192 points is 171 ms of audio, which is long enough to
    /// separate partials 110 Hz apart and short enough that a ring building at
    /// 20 dB/s only moves 3 dB across it.
    void computeLowSpectrum() noexcept
    {
        loValid = false;
        if (samplesSeen < loFftSize) return;

        int r = (writePos - loFftSize) & (ringSize - 1);
        for (int i = 0; i < loFftSize; ++i)
        {
            loScratch[(size_t) i] = ring[(size_t) r];
            r = (r + 1) & (ringSize - 1);
        }
        std::fill (loScratch.begin() + loFftSize, loScratch.end(), 0.0f);
        loWindow.multiplyWithWindowingTable (loScratch.data(), (size_t) loFftSize);
        loFft.performFrequencyOnlyForwardTransform (loScratch.data());

        constexpr float norm = 2.0f / loFftSize;
        const int top = juce::jmin (loNumBins, (int) (params.crossoverHz * 1.5f / loBinHz) + 4);
        for (int i = 0; i < top; ++i)
            magLo[(size_t) i] = juce::Decibels::gainToDecibels (loScratch[(size_t) i] * norm, -120.0f);
        loValid = true;
    }

    /// Peaks in the long window, below the crossover. These never become suspects
    /// and are never tracked - they exist so the comb and family tests can see a
    /// voice's partials where the short window smears them together.
    void collectLowPeaks() noexcept
    {
        loPeakCount = 0;
        if (! loValid) return;

        const int first = juce::jmax (2, (int) (params.minFreq / loBinHz));
        const int last  = juce::jmin (loNumBins - 3, (int) (params.crossoverHz / loBinHz));

        for (int i = first; i <= last && loPeakCount < maxLoPeaks; ++i)
        {
            const float here = magLo[(size_t) i];
            if (here < params.floorDb) continue;
            if (here <= magLo[(size_t) (i - 1)] || here <= magLo[(size_t) (i + 1)]) continue;
            if (here - medianAroundIn (magLo.data(), loNumBins, loBinHz, i, 300.0f) < params.prominenceDb)
                continue;
            loPeakFreq[(size_t) loPeakCount++] = parabolicIn (magLo.data(), i, loBinHz);
        }
    }

    /// Local median of whichever spectrum a peak came from, over a window given
    /// in Hz rather than bins - the two analysers have different bin widths, and
    /// a fixed bin count would mean a 470 Hz window in one and 117 Hz in the other.
    float medianAroundIn (const float* arr, int nBins, float bw, int centre, float halfHz) noexcept
    {
        const int half = juce::jmax (3, (int) (halfHz / bw));
        const int lo = juce::jmax (0, centre - half);
        const int hi = juce::jmin (nBins - 1, centre + half);
        int count = 0;
        const int guard = juce::jmax (2, (int) (35.0f / bw));     // skip the peak itself
        for (int i = lo; i <= hi && count < (int) medianScratch.size(); ++i)
            if (std::abs (i - centre) > guard) medianScratch[(size_t) count++] = arr[i];

        if (count == 0) return -120.0f;
        const int mid = count / 2;
        std::nth_element (medianScratch.begin(), medianScratch.begin() + mid,
                          medianScratch.begin() + count);
        return medianScratch[(size_t) mid];
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

    void pushReject (Reject r) noexcept
    {
        const int next = (rejWrite + 1) % rejQueueSize;
        if (next == rejRead) return;
        rejects[(size_t) rejWrite] = r;
        rejWrite = next;
    }

    void pushEvent (Event e) noexcept
    {
        noteTrack (e.freq);

        const int next = (eventWrite + 1) % eventQueueSize;
        if (next == eventRead) return;         // full: drop, we will see it again next frame
        events[(size_t) eventWrite] = e;
        eventWrite = next;
    }

public:
    /**
        Follow one ring ACROSS its hops.

        A suspect is matched to a peak within matchTolHz, which at 9293 Hz is
        +-46 Hz. Measured at the rig on 2026-09-27 a ring went 9293 -> 10008 ->
        10716 Hz: jumps of 715 Hz, fifteen times outside that window. Every hop
        therefore built a brand-new suspect with no history, the bank opened a
        fresh filter at -12 dB, and it climbed the ladder from the bottom while
        the ring carried over every decibel it had already built. Three times.
        Nothing in the system could know it was the same ring, so nothing did.

        A track sits above the suspects and says so. It is deliberately loose -
        a neighbourhood and a memory, not a match - because the whole point is
        to survive a jump that the tight matcher is right to reject.

        This only WATCHES for now. It is reported and drawn before it is allowed
        to change a filter, because the last mechanism I was sure of turned out
        to fix nothing, and this one should have to show itself first.
    */
    void noteTrack (float f) noexcept
    {
        if (f <= 0.0f) return;
        const double now = nowSeconds();

        Track* best = nullptr;
        float bestGap = 1.0e9f;
        for (auto& t : tracks)
        {
            if (! t.active) continue;
            if (now - t.lastSeen > trackHoldSeconds) { t = Track{}; continue; }

            // Near the span it has already covered, not just its last reading:
            // a ring that has walked 9293 -> 10716 owns that whole stretch.
            const float gap = f < t.loHz ? (t.loHz - f)
                            : f > t.hiHz ? (f - t.hiHz) : 0.0f;
            if (gap <= trackNearFrac * f && gap < bestGap) { best = &t; bestGap = gap; }
        }

        if (best == nullptr)
        {
            // Take a free slot, else the one nothing has fed for longest.
            double oldest = 1.0e18;
            for (auto& t : tracks)
            {
                if (! t.active) { best = &t; break; }
                if (t.lastSeen < oldest) { oldest = t.lastSeen; best = &t; }
            }
            if (best == nullptr) return;
            *best = Track{};
            best->active = true;
            best->loHz = best->hiHz = f;
        }
        else if (std::abs (f - best->freq) > matchTolHz (f))
        {
            ++best->hops;                       // cleared the matcher: a real jump
        }

        best->freq     = f;
        best->loHz     = std::min (best->loHz, f);
        best->hiHz     = std::max (best->hiHz, f);
        best->lastSeen = now;
        ++best->hits;
    }

    /// Advance the track clock without analysing audio - so a test can replay a
    /// measured hop ladder with its real timing.
    void advanceForTest (double seconds) noexcept
    {
        framesAnalysed += (int64_t) (seconds * sampleRate / (double) hopSize);
    }

private:

    /// Is f a low-order partial of the voice the context tracker hears right now?
    /// Unlike belongsToTheVoice() this is false when no voice is detected: a
    /// howl in an empty room must still be held.
    bool partialOfTheVoice (float f) const noexcept
    {
        if (ctx.families <= 0 || ctx.f0Hz <= 20.0f) return false;
        for (int m = 1; m <= 12; ++m)
            if (std::abs (f - (float) m * ctx.f0Hz) <= 0.02f * f) return true;
        return false;
    }

    double nowSeconds() const noexcept
    {
        return (double) framesAnalysed * (double) hopSize / sampleRate;
    }

    // Must hold the LONGEST window, not the shortest: the low analyser reads
    // loFftSize samples back.
    static constexpr int ringSize = loFftSize * 2;   // power of two

    juce::dsp::FFT fft;
    juce::dsp::FFT loFft;
    juce::dsp::WindowingFunction<float> window;
    juce::dsp::WindowingFunction<float> loWindow;

    std::array<float, (size_t) ringSize>   ring {};
    std::array<float, (size_t) fftSize * 2> scratch {};
    std::array<float, (size_t) loFftSize * 2> loScratch {};
    std::array<float, (size_t) loNumBins>     magLo {};
    static constexpr int maxLoPeaks = 48;
    std::array<float, (size_t) maxLoPeaks> loPeakFreq {};
    int   loPeakCount = 0;
    float loBinHz = 5.9f;
    bool  loValid = false;
    std::array<float, (size_t) numBins>    mag {};
    std::array<float, (size_t) numBins>    reA {}, imA {}, reB {}, imB {};
    std::array<float, (size_t) numBins>*   curRe  = &reA;
    std::array<float, (size_t) numBins>*   curIm  = &imA;
    std::array<float, (size_t) numBins>*   prevRe = &reB;
    std::array<float, (size_t) numBins>*   prevIm = &imB;
    bool hasPrevFrame = false;
    int  samplesSeen  = 0;
public:
    int floorHalfWidthOverride = 0;   // test hook
private:
    std::array<float, (size_t) 64 * 2 + 4> medianScratch {};

    // ---- the plateau path's state (see scanBands) ---------------------------
    static constexpr int numBands = 48;             // ~1/6 octave over the band
    std::array<float, (size_t) numBands> bandLevel {};
    std::array<float, (size_t) numBands> bandBaseline {};
    std::array<int,   (size_t) numBands> bandHot {};
    std::array<float, (size_t) numBands> bandEntry {};   // level when it first went up
    // Per-band level history. The first version tracked the RUN frame to frame and
    // fitted a line to its top level, but the run's own edges wobble as bands cross
    // the threshold, which reset the fit before it could accumulate: the probe
    // reported "still 3" and a residual of 1e9 on a perfectly straight rise. Bands
    // do not wobble, so the history lives here instead.
    static constexpr int histLen = 32;
    std::array<std::array<float, (size_t) histLen>, (size_t) numBands> bandHist {};
    int histPos = 0, histCount = 0;
    PlateauProbe probe {};
    VoiceProbe   vprobe {};
    Context      ctx {};
    static constexpr int ctxHistory = 80;        // ~0.43 s at a 5.3 ms hop
    std::array<int,   (size_t) ctxHistory> ctxFamilies {};
    std::array<float, (size_t) ctxHistory> ctxF0 {};
    std::array<float, (size_t) ctxHistory> ctxLevel {};
    int ctxPos = 0, ctxCount = 0;

    /**
        Least-squares fit of the run's recent level against time, in dB.

        Feedback grows exponentially, which is a straight line in dB at whatever
        slope the excess loop gain dictates - so the slope is not the test, the
        RESIDUAL is. Music climbs in lumps and fits a line badly.
    */
    void straightness (int lo, int hi, float& slope, float& residual) const noexcept
    {
        const int n = juce::jmin (histCount, histLen);
        if (n < 8) { slope = 0.0f; residual = 1.0e9f; return; }

        float sx = 0.0f, sy = 0.0f, sxx = 0.0f, sxy = 0.0f;
        for (int i = 0; i < n; ++i)
        {
            const int idx = (histPos - n + i + histLen) % histLen;
            float y = -140.0f;
            for (int k = lo; k <= hi; ++k) y = juce::jmax (y, bandHist[(size_t) k][(size_t) idx]);
            const float x = (float) i;
            sx += x; sy += y; sxx += x * x; sxy += x * y;
        }
        const float denom = (float) n * sxx - sx * sx;
        if (std::abs (denom) < 1.0e-6f) { slope = 0.0f; residual = 1.0e9f; return; }

        slope = ((float) n * sxy - sx * sy) / denom;
        const float intercept = (sy - slope * sx) / (float) n;

        float sum = 0.0f;
        for (int i = 0; i < n; ++i)
        {
            const int idx = (histPos - n + i + histLen) % histLen;
            float y = -140.0f;
            for (int k = lo; k <= hi; ++k) y = juce::jmax (y, bandHist[(size_t) k][(size_t) idx]);
            const float err = y - (slope * (float) i + intercept);
            sum += err * err;
        }
        residual = std::sqrt (sum / (float) n);
    }
    bool bandsPrimed = false;
    std::array<std::atomic<float>, (size_t) numBins> publishedMag;

    std::array<float, maxPeaks> peakFreq {}, peakLevel {}, peakProm {};
    int peakCount = 0;
public:
    mutable int maxSeenPeaks = 0;
private:
public:


    std::array<Suspect, maxSuspects> suspects {};
    std::array<Event, eventQueueSize> events {};
    int eventRead = 0, eventWrite = 0;

    static constexpr int rejQueueSize = 32;
    std::array<Reject, rejQueueSize> rejects {};
    int rejRead = 0, rejWrite = 0;

    Params params;
    double sampleRate = 48000.0;
    float  binHz      = 23.4375f;
    int    writePos   = 0;
    int    sinceHop   = 0;
};

} // namespace fk
