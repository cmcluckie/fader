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
        int    harmonicExtra  = 36;     // extra frames (~190 ms) required if harmonic-related
        float  floorDb        = -70.0f; // ignore bins quieter than this
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
        float  inputGateDb    = -55.0f; // skip detection when broadband input is below this
        float  minFreq        = 200.0f; // low edge of the watched band
        float  maxFreq        = 16000.0f; // high edge; still clamped to Nyquist by bin count

        // A sung harmonic is prominent, steady and growing - it passes every test
        // feedback does. These three separate them.
        float  sustainRiseDb  = 1.5f;   // the sustain path also needs to have GROWN this
                                        // much: steady-forever is furniture, not feedback
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
    };

    /// Why a candidate that looked like a peak did NOT become a detection.
    /// Logging only what fired means every "it missed one" has to be
    /// reverse-engineered by simulation; this says which gate stopped it.
    enum class Reason { None = 0, Harmonic = 1, Unstable = 2, NoGrowth = 3, Vibrato = 4, Drifting = 5 };

    struct Reject
    {
        float freq    = 0.0f;
        float levelDb = 0.0f;
        int   reason  = 0;
        int   frames  = 0;
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
    static constexpr int maxSuspects = 24;
    static constexpr int maxPeaks    = 48;   // prominent peaks kept per frame
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
            if (samplesSeen < fftSize) ++samplesSeen;
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

        for (auto& s : suspects) if (s.active) ++s.missed;
        peakCount = 0;

        // §4.5 input gate: don't chase noise between songs (spectrum still published)
        if (rmsDb >= params.inputGateDb)
        {
            constexpr int floorHalfWidth = 20;   // ~+-470 Hz of local median
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

                if (peakCount < maxPeaks)
                {
                    peakFreq[(size_t) peakCount]  = freq;
                    peakLevel[(size_t) peakCount] = here;
                    peakProm[(size_t) peakCount]  = here - localFloor;
                    ++peakCount;
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

            // A harmonic-series member must be markedly more prominent to be believed.
            if (harmonic && peakProm[(size_t) i] < params.prominenceDb + params.harmonicPromDb)
                continue;

            track (peakFreq[(size_t) i], peakLevel[(size_t) i], harmonic);
        }

        for (auto& s : suspects)
            if (s.active && s.missed > 2) s = Suspect{};

        hasPrevFrame = true;
    }

    /**
        Locate a peak from the phase advance between frames, falling back to
        magnitude interpolation on the first frame or if the phase estimate is
        implausible. Standard phase-vocoder reassignment: the phase a bin actually
        advanced, minus the advance its centre frequency predicts, is the offset
        from that centre.
    */
    float refineFrequency (int k, float levelDb) noexcept
    {
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

    void track (float freq, float levelDb, bool harmonic) noexcept
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
            const float needGrowth = params.growthDb * (windowSec / 0.1f);
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
            const bool  voiceBand = s.freq < params.voiceBandHz || s.harmonic;
            const int   baseNeed  = voiceBand ? params.voiceFrames : params.persistFrames;
            const int   need   = baseNeed + (s.harmonic ? params.harmonicExtra : 0);

            if (! s.reported)
            {
                const bool wobbling = voiceBand
                    && looksLikeVibrato (s, juce::jmin (s.frames, params.voiceFrames));

                // Path A - the classic attack: stable, and climbing.
                bool fire = s.frames >= need && spread <= stabTol
                            && growth >= needGrowth && monotonic;

                // Path B - the slow creep. A ring hovering near unity loop gain
                // grows too slowly to trip the growth gate, but it sits dead
                // still for hundreds of ms, which nothing musical does. Held
                // notes arrive with harmonics, so the guard covers those.
                bool sustainWander = false;
                if (! fire && ! s.harmonic)
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
                        pushReject ({ s.freq, levelDb, (int) why, s.frames });
                    }
                }

                if (fire && ! wobbling)
                {
                    s.reported    = true;
                    s.reportLevel = s.lastLevel;
                    s.sinceReport = 0;
                    if (s.lastLevel >= params.actionDb)
                        pushEvent ({ s.freq, s.lastLevel, true });
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
                    // Only a tone that is still CLIMBING has earned a deeper cut.
                    // Analysis runs pre-notch, so a notch can never be seen to
                    // succeed: a room tone stays fully visible however well it is
                    // being suppressed, "surviving" is therefore true forever, and
                    // it used to escalate on that alone. Measured at the rig: 97.3%
                    // of notch samples pinned at their target, 0.1% ever releasing,
                    // a fifth of them at the cap - two dozen deep filters held
                    // permanently, which is a high shelf, and it sounded like one.
                    if (s.lastLevel >= params.actionDb)
                        pushEvent ({ s.freq, s.lastLevel, climbing });
                }
            }
            return;
        }

        Suspect* slot = nullptr;
        for (auto& s : suspects)
            if (! s.active) { slot = &s; break; }

        // With every slot taken this used to return silently, so a peak arriving
        // into a full table was never tracked at all - and peaks are offered in
        // ascending frequency order, so persistent low clutter would hold the
        // table and a new ring above it could stay invisible for as long as the
        // clutter lasted. Take the quietest un-fired suspect instead: something
        // already reported is doing real work, and the loudest of the rest is the
        // likelier ring. Not the proven cause of any miss seen at the rig, but a
        // way to go blind that should not exist.
        if (slot == nullptr)
        {
            float weakest = levelDb;
            for (auto& s : suspects)
                if (! s.reported && s.lastLevel < weakest) { weakest = s.lastLevel; slot = &s; }
            if (slot == nullptr) return;    // everything here outranks the newcomer
        }

        *slot = Suspect{};
        slot->active     = true;
        slot->harmonic   = harmonic;
        slot->freq       = freq;
        slot->lastLevel  = levelDb;
        slot->frames     = 1;
        slot->wFreq[0]   = freq;
        slot->wLevel[0]  = levelDb;
        slot->windowPos  = 1;
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
    std::array<float, (size_t) numBins>    reA {}, imA {}, reB {}, imB {};
    std::array<float, (size_t) numBins>*   curRe  = &reA;
    std::array<float, (size_t) numBins>*   curIm  = &imA;
    std::array<float, (size_t) numBins>*   prevRe = &reB;
    std::array<float, (size_t) numBins>*   prevIm = &imB;
    bool hasPrevFrame = false;
    int  samplesSeen  = 0;
    std::array<float, (size_t) 64 * 2 + 4> medianScratch {};
    std::array<std::atomic<float>, (size_t) numBins> publishedMag;

    std::array<float, maxPeaks> peakFreq {}, peakLevel {}, peakProm {};
    int peakCount = 0;

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
