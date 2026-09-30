#pragma once
#include <cmath>
#include <array>
#include <algorithm>

namespace fk
{
constexpr double kPi = 3.14159265358979323846;

/** Direct-form-II transposed biquad. No allocation, no branching in process(). */
struct Biquad
{
    double b0 = 1.0, b1 = 0.0, b2 = 0.0, a1 = 0.0, a2 = 0.0;
    double z1 = 0.0, z2 = 0.0;

    inline float process (float x) noexcept
    {
        const double y = b0 * x + z1;
        z1 = b1 * x - a1 * y + z2;
        z2 = b2 * x - a2 * y;
        return static_cast<float> (y);
    }

    void reset() noexcept { z1 = z2 = 0.0; }

    void setBypass() noexcept { b0 = 1.0; b1 = b2 = a1 = a2 = 0.0; }

    /** RBJ peaking EQ. gainDb is negative for a cut. */
    void setPeaking (double fs, double f, double q, double gainDb) noexcept
    {
        if (f <= 20.0 || f >= fs * 0.48 || q <= 0.01) { setBypass(); return; }

        const double A     = std::pow (10.0, gainDb / 40.0);
        const double w0    = 2.0 * kPi * f / fs;
        const double cw    = std::cos (w0);
        const double sw    = std::sin (w0);
        const double alpha = sw / (2.0 * q);
        const double a0    = 1.0 + alpha / A;

        b0 = (1.0 + alpha * A) / a0;
        b1 = (-2.0 * cw)       / a0;
        b2 = (1.0 - alpha * A) / a0;
        a1 = (-2.0 * cw)       / a0;
        a2 = (1.0 - alpha / A) / a0;
    }
};

/** One deployed filter. */
struct NotchSlot
{
    bool   active   = false;
    bool   locked   = false;   // never auto-released or auto-deepened
    bool   manual   = false;   // placed by hand rather than detected
    double freq     = 1000.0;
    double originHz = 0.0;     // where it was placed; tracking may not ratchet away from this
    double q        = 40.0;
    double targetDb = 0.0;     // negative
    double currentDb= 0.0;     // smoothed toward targetDb
    int    capHits    = 0;     // re-triggers while already at the ceiling
    int    calmHits   = 0;     // consecutive re-triggers with no growth
    int    futileHits = 0;     // re-triggers where it grew LOUDER at maximum cut
    bool   ineffective= false; // concluded: this filter is not in the loop
    double lastLevelDb= -1000.0;
    double pulsePhase = 0.0;   // where this filter sits in the pulse cycle, 0..1
    double lastHitS = 0.0;     // transport seconds of last (re)trigger
    double lastRelS = 0.0;     // transport seconds of last release step
};

/**
    Fixed-size notch pool with the adaptive-suppression policy (spec §5-8).

    Depth   : peaking EQ at Q ~40; new notches open at -6 dB (or -12 for a known
              repeat offender) and deepen -6 dB per re-trigger to a -18 dB soft
              cap, then to a -24 dB hard cap while the tone keeps coming back.
    Release : a notch that has not re-triggered for `holdSeconds` bleeds back
              toward 0 at `bleedDbPerSec`, no faster than one step per
              `minReleaseGap`, and retires once it is shallower than `retireDb`.
    Pool    : triggers within 1/12 octave coalesce onto one slot; when every slot
              is busy the least-recently-hit unlocked one is stolen.
    Memory  : a decaying 1/24-octave histogram counts how often each frequency has
              offended; three strikes and new notches there open deep (fast-track).

    Everything is preallocated; safe to drive from the audio thread.
*/
template <int MaxNotches>
class NotchBank
{
public:
    static constexpr int maxNotches = MaxNotches;

    void prepare (double sampleRate, int blockSize) noexcept
    {
        fs = sampleRate;
        // Gain smoothing is deliberately asymmetric, one coefficient per direction.
        // Deepening is the emergency - it must land almost immediately or the ring
        // wins the race - while coming back out must stay slow, because that is
        // where a fast gain change would be audible as a zip. 5 ms down, 40 ms up.
        const double blockSeconds = (double) blockSize / sampleRate;
        attackCoeff  = 1.0 - std::exp (-blockSeconds / 0.005);
        releaseCoeff = 1.0 - std::exp (-blockSeconds / 0.040);
        for (auto& b : filters) b.reset();
        // the histogram's decay clock rides the transport, which restarts here
        for (auto& h : histogram) h = Bucket{};
    }

    void reset() noexcept { for (auto& b : filters) b.reset(); }

    /**
        A detector hit at f. Deepen the coalesced notch if one already covers f,
        otherwise open a fresh one (deeper if f is a known repeat offender).
        Returns the slot index, or -1 only if every slot is locked. `nowSeconds`
        is the transport clock.
    */
    int trigger (double f, double nowSeconds, bool growing = true,
                 double levelDb = -1000.0, double widthHz = 0.0, bool wide = false) noexcept
    {
        juce::ignoreUnused (levelDb);
        const int existing = findNear (f);

        // Futility accounting happens BEFORE any refusal, deliberately.
        //
        // It lived inside the re-trigger branch first, and could therefore never
        // fire: a deeply cut frequency is refused as AlreadyCovered several lines
        // below and returns early, so the one signal that says "we are cutting
        // this hard and it is STILL getting louder" was thrown away exactly when
        // it was true. The closed-loop gate caught it - the guard sat in a
        // disconnected loop, parked a filter 15 dB past the dial, and noticed
        // nothing at all.
        //
        // Every trigger is evidence, whatever we decide to do about it.
        if (existing >= 0 && levelDb > -900.0)
        {
            auto& e = slots[(size_t) existing];
            const bool louder = levelDb > e.lastLevelDb + 1.0;
            if (cutAtDb (f, -1) <= futileCutDb && louder && e.lastLevelDb > -900.0)
                ++e.futileHits;
            else if (! louder && e.futileHits > 0)
                --e.futileHits;
            e.lastLevelDb = levelDb;

            if (loopVerdict && e.futileHits >= futileHitsBeforeGivingUp)
            {
                e.ineffective = true;
                // Latch the CONCLUSION separately from the filter that reached
                // it. The verdict is about the wiring, and wiring does not stop
                // being wrong because one ring happened to stop: the filters
                // that proved it bleed out within seconds, taking the flag with
                // them, and the operator looks up at a clean display while the
                // room is still howling. Measured in the disconnected gate -
                // slots hit 18 futile hits against a threshold of 6, and the
                // instantaneous count read zero by the end of the run.
                lastIneffectiveS = nowSeconds;
                ++ineffectiveEver;
            }
            else if (e.futileHits == 0) e.ineffective = false;

        }

        // Why a trigger was refused. Reasoning about which check fired, from logs
        // that recorded the outcome and not the cause, was wrong twice.
        lastRefusal = Refusal::None;

        // Already handled: another filter here would only stack depth on a
        // frequency that is under control, which is how the top end got dulled.
        // Measured per frequency with the real filter response, not estimated - a
        // linear falloff scored a filter 179 Hz away as 21.6 dB of coverage when
        // the truth was 8, and refused to cover a ring carrying almost nothing.
        //
        // There WAS a regional version of this too: refuse if the surrounding
        // half-octave was already dark, scaled by how loud the ring was. It is
        // gone, measured out rather than argued out. Across four simulated rooms it
        // declined 30 to 40 percent of triggers while the caught frequency itself
        // carried 2.3 to 3.6 dB and the nearest filter sat 500 to 800 Hz away -
        // which is exactly the rig's complaint, 3.1 dB at 300 to 500 Hz,
        // reproduced. With it off: every trigger placed, event counts roughly
        // halved because rings die instead of persisting, summed EQ the same or
        // better in three rooms of four, and ASG unchanged within 0.4 dB in all
        // four. It cost coverage and bought nothing.
        if (cutAtDb (f, existing) <= coveredDb)
        {
            lastRefusal = Refusal::AlreadyCovered;
            return -1;
        }

        if (existing >= 0)
        {
            auto& s = slots[(size_t) existing];
            s.lastHitS = nowSeconds;
            if (! s.locked)
            {
                // Follow the tone. Feedback wanders as the loop builds - a single
                // mode was measured drifting 460 Hz - and a filter this narrow
                // slides off a peak that moves even a little. Without this, a notch
                // can sit at its deepest cut while the ring grows beside it.
                //
                // But tracking has to be anchored, or it RATCHETS: the merge window
                // travels with the filter, so a tone at the edge pulls the notch part
                // of the way over, which opens a fresh window further out, and the
                // filter random-walks across the spectrum dragged by whatever knocked
                // last. Measured at the rig: notches wandering 650-900 Hz, more than
                // twice their own bandwidth at Q25, ending up covering neither the
                // ring they were placed for nor the one that pulled them away. One
                // was caught sliding 275 Hz off a ring that then climbed 28 dB
                // through the gap while the abandoned filter released.
                //
                // Follow drift up to half a bandwidth from where it was placed, and
                // no further. A tone beyond that is a different tone and deserves its
                // own filter - which, with 48 of them, it can have.
                // Measured from the anchor, not the current centre: a leash that
                // grows as the notch moves is a slower ratchet, not a limit.
                const double leash = std::max (10.0, s.originHz / (2.0 * s.q));
                s.freq = std::clamp (s.freq + (f - s.freq) * freqTrack,
                                     s.originHz - leash, s.originHz + leash);

                // deepen a step; allow the hard cap only once we are already at the
                // soft cap and the tone is still knocking (spec §5).
                // The hard cap is for tones that are still building. One that is
                // merely still present gets held at the soft cap, not driven deeper
                // every quarter second for as long as the room hums.
                // Emergency: the filter is already at the ceiling and the ring is
                // STILL climbing. Measured at the rig, 2026-09-27: a 9293 Hz ring
                // caught at 21 ms and -52 dB, escalated to the -18.7 dB cap within
                // 110 ms, re-triggered 22 more times - and grew 46 dB anyway,
                // because it carried more than 18 dB of excess loop gain. A notch
                // shallower than the excess loses however fast it lands; that is
                // arithmetic, not detection.
                //
                // The dial governs how the guard SOUNDS in the steady state. It has
                // no business governing how hard it may fight for its life. Past
                // the cap while still losing, it goes as deep as it needs and
                // releases back to the dial once the ring is dead.
                // Evidence of growth must survive a PAUSE. A ring that rises in
                // steps - climb, sit, climb, sit - is the shape this bank was
                // blind to: zeroing the count on a single calm re-trigger meant
                // it could never assemble three in a row, so it walked up to the
                // dial over a minute and sat there breaking through, forever.
                // It takes a sustained calm spell to conclude the ring is beaten,
                // and one further climb to undo that conclusion.
                if (growing && s.targetDb <= hardCapDb + 0.25) ++s.capHits;
                if (growing) s.calmHits = 0;
                else if (++s.calmHits >= calmHitsToForget) { s.capHits = 0; s.calmHits = 0; }

                // ---- are we even IN the loop? --------------------------------
                //
                // Measured at the rig 2026-09-28. A 7235 Hz ring: no filter near
                // it, then -30 dB, then -45.3 dB within 250 ms - and its level
                // went -50.6 -> -23.3 dB ANYWAY, while that 45 dB notch sat on
                // it. Then it did the same thing twice more.
                //
                // That is not a stubborn ring. It is arithmetic saying we are not
                // in its loop. No studio path carries 45 dB of excess gain; a cut
                // that deep ends any real feedback instantly. If it does not, the
                // signal we are filtering is not the signal going round - the
                // console is feeding the wedge from somewhere upstream of our
                // return, and every decibel we spend is pure tone damage in a
                // fight we are not part of.
                //
                // So: count re-triggers where the level RISES while we are already
                // at emergency depth. A few is normal - the cut takes a moment to
                // bite. A run of them means stop digging, come back to the dial,
                // and tell somebody, because the fix is a routing cable and not
                // another 6 dB.
                const bool emergency = s.capHits >= capHitsBeforeEmergency;

                // Not in the loop: hold at the operator's dial. Not shallower -
                // if the routing comes back we want a filter already there - and
                // no deeper, because past the dial it is pure tone damage spent
                // on a fight we are not part of. Everything else about the filter
                // carries on as normal: it still tracks, still holds, still
                // releases. Only the DEPTH is capped.
                // The verdict binds the whole BANK, not just the filter that
                // reached it. If our output is not in the loop then it is not in
                // the loop for any frequency, and letting thirty-three other
                // slots carry on escalating past the operator's dial spends the
                // same tone for the same nothing. The disconnected gate caught
                // exactly that: one filter correctly parked at the dial while
                // another sat at -33 dB.
                // Two different questions, and conflating them disarmed the
                // guard for half a minute after any verdict, right or wrong:
                //
                //   what the OPERATOR is shown - latched for 30 s, because a
                //     wiring fault does not heal when one ring goes quiet
                //     (notInLoopRecently, used for telemetry only);
                //
                //   what the guard is ALLOWED TO DO - governed by evidence that
                //     is live RIGHT NOW. The moment a ring responds to a cut the
                //     verdict is wrong, and depth has to come back instantly,
                //     because that is the case where we are about to need it.
                const bool outOfLoop = s.ineffective || ineffectiveNow() > 0;
                const double floorDb = outOfLoop
                                     ? hardCapDb
                                     : (emergency
                                          ? emergencyCapDb
                                          : ((growing && s.targetDb <= softCapDb + 0.25) ? hardCapDb : softCapDb));

                // A trigger may only ever DEEPEN. Clamping with max() alone would
                // yank a filter sitting at emergency depth straight back to the
                // dial the moment its ring stopped growing - 27 dB in one call,
                // which is a thump, and it hands the ring back the gain it just
                // lost, so it grows again and the pair oscillate. Coming back up
                // is release()'s job, on the bleed slew, and only once nothing
                // has re-triggered for holdSeconds. That is what makes the
                // emergency self-limiting: it costs depth for as long as the ring
                // keeps knocking, and not one bleed step longer.
                s.targetDb = std::min (s.targetDb, std::max (floorDb, s.targetDb + stepDb));

                // The one case where a trigger may make a filter SHALLOWER.
                //
                // The never-shallow rule above exists to stop a filter and its
                // ring oscillating: shallow, it grows, deepen, it stops, shallow
                // again. That cannot happen here, because a filter that is not in
                // the loop does not affect its ring at all - which is exactly what
                // "ineffective" means and how it was diagnosed. So there is no
                // feedback path to oscillate through, and holding 45 dB of cut we
                // have proven does nothing is the worst of both: full tone damage,
                // no protection, and the operator's dial ignored.
                //
                // Coming up is smoothed by releaseCoeff like every other change,
                // so this is a swell over ~130 ms rather than a step.
                if (outOfLoop) s.targetDb = std::max (s.targetDb, hardCapDb);

                noteDepth (s.freq, s.targetDb, nowSeconds);

                // Emergency depth is paid for by emergency NARROWNESS. Harm goes
                // as depth x width, so going 20 dB deeper at the same Q triples
                // the tone cost - which is exactly what the rig measured when the
                // emergency first went in (harm 391 -> 481 across five seeds, for
                // 4% fewer runaways). But the width was never justified: a room
                // mode is B = 2.2/RT60 wide, one to three Hz, while Q 20 at 9 kHz
                // is 450 Hz. We were 150x wider than the thing we were killing.
                //
                // By the time a filter is in emergency it has been re-triggered
                // at one frequency five or more times, so it is far better
                // localised than the single reading it opened on. Spend that
                // confidence on narrowing rather than on breadth.
                if (emergency && s.q < emergencyQ)
                    s.q = std::min (emergencyQ, s.q * 1.6);
            }
            return existing;
        }

        // The budget applies to NEW filters only. A re-trigger on a filter that
        // already exists is a ring still knocking on a door we already built -
        // proven value, and refusing it was the bug: measured at the rig, budget 30
        // refused 890 of 901 catches because deepening counted against the ceiling
        // too, which left the guard switched off after its first eleven filters.
        //
        // And when the budget IS full, take the money back rather than refusing.
        // The stalest filter - one nothing has re-triggered for holdSeconds - is
        // by definition the least useful thing the budget is being spent on.
        if (harmBudget > 0.0 && harmNow() >= harmBudget)
        {
            const int stale = budgetReallocates ? stalestBefore (nowSeconds - holdSeconds) : -1;
            if (stale < 0)
            {
                lastRefusal = Refusal::RegionTooDark;   // every filter is still earning its keep
                return -1;
            }
            slots[(size_t) stale] = NotchSlot{};
            filters[(size_t) stale].reset();
        }

        int slot = findFree();
        if (slot < 0) slot = stealLru();          // pool full: evict the coldest notch
        if (slot < 0) { lastRefusal = Refusal::AllLocked; return -1; }

        double openDb = (offenderCount (f, nowSeconds) >= fastTrackStrikes)
                            ? fastTrackCutDb : initialCutDb;

        // Reopen where we left off, not at the bottom of the ladder. Measured at
        // the rig 2026-09-27: a 9293 Hz ring was fought to the cap, released when
        // it went quiet, came back, and re-climbed the whole ladder from -18 -
        // then did it 22 more times. Each climb is ~5 re-triggers of free growth
        // handed back to a ring we had already characterised.
        //
        // Memory buys SPEED, not depth. The reopen is clamped at the dial however
        // deep it went last time, and a margin shallower than it ended, because a
        // room that has changed deserves the benefit of the doubt. Going past the
        // dial still costs fresh evidence that it is still climbing (T18, T43).
        int primedCapHits = 0;
        const double was = rememberedDepth (f, nowSeconds);
        if (was < 0.0)
        {
            openDb = std::clamp (std::min (openDb, was + reopenMarginDb), hardCapDb, 0.0);

            // And if it needed emergency depth last time, it starts one growing
            // re-trigger away from it rather than three. A ring that has already
            // proven it carries more excess gain than the dial does not get to
            // make us learn that again from scratch every time it returns.
            if (was <= hardCapDb + 0.25) primedCapHits = capHitsBeforeEmergency - 1;
        }

        // A wide, flat feature is not one ring and does not deserve one filter.
        // Answer it with a comb: a few narrow slots spread across it, together
        // covering only combCoverage of its width. Feedback needs a winner, and a
        // region cut into ridges has none - while narrow slots spaced apart cost
        // far less tone than one cut the whole width of the hump.
        // Only a PLATEAU gets a comb. Measured on the room ladder: combing
        // ordinary catches cost room 4 six filters for nothing (ASG 6.1 -> 5.9 on
        // 45 filters instead of 25). Those rooms ring as narrow modes, where one
        // correctly sized filter is already the right answer.
        if (wide && widthHz > combMinFraction * f && f > 0.0)
            return openComb (f, widthHz, openDb, nowSeconds, slot);

        auto& s = slots[(size_t) slot];
        s = NotchSlot{};
        s.active   = true;
        s.freq     = f;
        s.originHz = f;
        s.q        = qForWidth (f, widthHz);
        // Golden-ratio stagger: consecutive slots land as far apart in the cycle
        // as possible, so thirty filters pulsing is a steady low total cut rather
        // than thirty of them flickering in unison - which is audible flutter.
        s.pulsePhase = std::fmod (pulseStagger * 0.6180339887 * (double) slot, 1.0);
        s.targetDb = openDb;
        s.capHits  = primedCapHits;
        s.currentDb= 0.0;
        s.lastHitS = nowSeconds;
        s.lastRelS = nowSeconds;
        filters[(size_t) slot].reset();

        noteOffender (f, nowSeconds);              // one strike per fresh occurrence
        return slot;
    }

    /**
        Cut a wide feature into ridges rather than flattening it.

        combTeeth slots across the measured width, each one combCoverage/combTeeth
        of that width, so the total glass removed is combCoverage of the hump. The
        centre tooth is placed first and its index returned, so a re-trigger at the
        reported frequency merges onto it and deepens the comb from the middle.
    */
    int openComb (double f, double widthHz, double openDb, double nowSeconds, int firstSlot) noexcept
    {
        const double toothBw = std::max (4.0, combCoverage * widthHz / combTeeth);
        const int    centre  = combTeeth / 2;
        int returned = -1;

        for (int t = 0; t < combTeeth; ++t)
        {
            // Centre tooth first: it takes the slot already chosen by the caller,
            // and is the one a re-trigger will find.
            const int order = (t == 0) ? centre : (t <= centre ? centre - t : t - centre);
            if (order < 0 || order >= combTeeth) continue;

            const double centreHz = f - widthHz / 2.0 + widthHz * ((double) order + 0.5) / combTeeth;
            if (centreHz <= 0.0) continue;

            int slot = (t == 0) ? firstSlot : findFree();
            if (slot < 0) slot = stealLru();
            if (slot < 0) break;                      // pool exhausted: fewer teeth

            auto& s = slots[(size_t) slot];
            s = NotchSlot{};
            s.active   = true;
            s.freq     = centreHz;
            s.originHz = centreHz;
            s.q        = std::clamp (centreHz / toothBw, qMin, qMax);
            s.pulsePhase = std::fmod (pulseStagger * 0.6180339887 * (double) slot, 1.0);
            s.targetDb = openDb;
            s.currentDb= 0.0;
            s.lastHitS = nowSeconds;
            s.lastRelS = nowSeconds;
            filters[(size_t) slot].reset();
            if (t == 0) returned = slot;
        }

        noteOffender (f, nowSeconds);
        return returned;
    }

    int placeManual (double f, double depthDb, double nowSeconds) noexcept
    {
        int slot = findFree();
        if (slot < 0) slot = stealLru();
        if (slot < 0) return -1;
        auto& s = slots[(size_t) slot];
        s = NotchSlot{};
        s.active   = true;
        s.locked   = true;
        s.manual   = true;
        s.freq     = f;
        s.originHz = f;
        s.q        = defaultQ;
        s.targetDb = depthDb;
        s.currentDb= 0.0;
        s.lastHitS = nowSeconds;
        s.lastRelS = nowSeconds;
        filters[(size_t) slot].reset();
        return slot;
    }

    /**
        Bleed unlocked notches back out. Call periodically with the transport
        clock; the per-slot timers enforce the 2 s hold and the 0.5 s minimum gap
        between steps regardless of how often this is called (spec §6).
    */
    void release (double nowSeconds) noexcept
    {
        for (auto& s : slots)
        {
            if (! s.active || s.locked) continue;
            if (nowSeconds - s.lastHitS < holdSeconds)               // still holding:
            {
                s.lastRelS = nowSeconds;                             // bleed clock only
                continue;                                            // runs after hold
            }
            const double dt = nowSeconds - s.lastRelS;
            if (dt < minReleaseGap) continue;                        // not yet

            s.targetDb += bleedDbPerSec * dt;                        // toward 0
            s.lastRelS  = nowSeconds;
            // A filter on its way out is not losing a fight; clear the verdict
            // so the next ring here starts with an open mind.
            s.ineffective = false; s.futileHits = 0; s.lastLevelDb = -1000.0;
            if (s.targetDb >= retireDb) { s.active = false; s.targetDb = 0.0; }
        }
    }

    void clear (bool includeLocked) noexcept
    {
        for (size_t i = 0; i < slots.size(); ++i)
        {
            if (slots[i].locked && ! includeLocked) continue;
            slots[i] = NotchSlot{};
            filters[i].reset();
        }
    }

    void removeAt (int index) noexcept
    {
        if (index < 0 || index >= MaxNotches) return;
        slots[(size_t) index] = NotchSlot{};
        filters[(size_t) index].reset();
    }

    /** Lock every currently active notch — used at the end of a ring-out pass. */
    void lockAll() noexcept
    {
        for (auto& s : slots) if (s.active) s.locked = true;
    }

    /**
        Q that holds the audible width roughly constant as the cut deepens.

        A peaking EQ's SHAPE is fixed by Q, so pushing it deeper drags the skirts
        down with it. Measured at 5 kHz, Q=25: a -6 dB notch is 428 Hz wide at the
        -1 dB points, a -24 dB notch is 1449 Hz and a -30 dB one is 2043 Hz. The
        deep cuts are therefore doing audible damage across more than a kilohertz
        each, and with dozens live their skirts sum - the rig was measured applying
        -13 dB of average cut above 4 kHz, which is not a set of notches any more,
        it is a shelf.

        Scaling Q with depth keeps the ring just as suppressed while shrinking what
        goes with it. Deliberately conservative: never below the configured Q, so
        this can only ever narrow a filter, never widen one. Shallow cuts are left
        exactly as they were; only the deep ones, which are the ones doing the
        damage, get tightened.
    */
    static double qForDepth (double baseQ, double cutDb) noexcept
    {
        return baseQ * std::max (1.0, std::abs (cutDb) / 18.0);
    }

    /** Smooth gains, refresh coefficients, filter in place. */
    void process (float* data, int numSamples, bool bypassAudio) noexcept
    {
        pulseSamples += (int64_t) numSamples;

        for (size_t i = 0; i < slots.size(); ++i)
        {
            auto& s = slots[i];
            if (! s.active && std::abs (s.currentDb) < 0.01) { filters[i].setBypass(); continue; }

            const double target = s.active ? s.targetDb : 0.0;
            const double coeff = (target < s.currentDb) ? attackCoeff : releaseCoeff;
            s.currentDb += (target - s.currentDb) * coeff;

            const double applied = s.currentDb * pulseGain (s);
            if (std::abs (applied) < 0.01) filters[i].setBypass();
            else                           filters[i].setPeaking (fs, s.freq, qForDepth (s.q, applied), applied);
        }

        if (bypassAudio) return;

        for (int n = 0; n < numSamples; ++n)
        {
            float x = data[n];
            for (size_t i = 0; i < filters.size(); ++i)
                if (slots[i].active || std::abs (slots[i].currentDb) >= 0.01)
                    x = filters[i].process (x);
            data[n] = x;
        }
    }

    /**
        Where this filter is in its pulse cycle, 0 (out of the way) to 1 (full depth).

        The loop integrates whatever it is given, so a filter at 20% duty costs the
        ring a fifth of its depth. The EAR does not integrate the same way: loudness
        is averaged over 30-200 ms and a brief narrowband dip is partly filled in by
        the bands either side. That asymmetry is the whole reason to pulse.

        The rate matters as much as the duty. 4-20 Hz is the most audible modulation
        there is - that is where flutter lives - so the default cycle is 12 ms, and
        the edges are raised cosines because hard-switching a biquad's gain splatters
        sidebands that sound like clicks.
    */
    double pulseGain (const NotchSlot& s) const noexcept
    {
        if (dutyCycle >= 1.0) return 1.0;
        if (dutyCycle <= 0.0) return 0.0;

        const double t  = (double) pulseSamples / fs;
        double ph = std::fmod (t * pulseHz + s.pulsePhase, 1.0);
        if (ph < 0.0) ph += 1.0;
        if (ph >= dutyCycle) return 0.0;

        const double edge = std::clamp (pulseEdgeMs * 0.001 * pulseHz, 0.02, dutyCycle * 0.49);
        const double x = std::min (std::min (1.0, ph / edge), std::min (1.0, (dutyCycle - ph) / edge));
        return 0.5 - 0.5 * std::cos (juce::MathConstants<double>::pi * x);
    }

    /// What is being cut at f RIGHT NOW, pulse included. cutAtDb reports what each
    /// filter does when it is on; this reports what the ring actually meets.
    double effectiveCutAtDb (double f) const noexcept
    {
        if (dutyCycle >= 1.0) return cutAtDb (f, -1);

        double total = 0.0;
        for (int i = 0; i < MaxNotches; ++i)
        {
            const auto& s = slots[(size_t) i];
            if (! s.active || s.targetDb > -0.1) continue;
            const double g = pulseGain (s);
            if (g <= 0.0) continue;
            // One filter at a time against an empty bank: its own contribution.
            total += cutOfSlot (i, f) * g;
        }
        return total;
    }

    NotchSlot  getSlot (int i) const noexcept { return slots[(size_t) i]; }
    NotchSlot& slotRef  (int i)       noexcept { return slots[(size_t) i]; }

    /** Index of the active notch within `tolOctaves` of f, closest first, or -1. */
    int findNear (double f) const noexcept
    {
        if (f <= 0.0) return -1;
        // The merge window is the filter's own half-bandwidth (f / 2Q), not a fixed
        // musical interval. A tone further away than that is barely attenuated by
        // this notch, so merging onto it would deepen a filter that misses the tone
        // while the ring grows beside it. Anything outside gets its own slot.
        const double window = std::max (10.0, f / (2.0 * defaultQ));
        int    best = -1;
        double bestErr = window;
        for (int i = 0; i < MaxNotches; ++i)
        {
            const auto& s = slots[(size_t) i];
            if (! s.active) continue;
            // Each filter now has its own width, so the window that decides "is this
            // the same tone" is that filter's, not one global figure.
            const double own = std::max (10.0, s.freq / (2.0 * s.q));
            if (std::abs (s.freq - f) > own && std::abs (s.freq - f) > window) continue;
            // Must be near the filter AND reachable from its anchor. Merging onto a
            // tone the notch is leashed away from would deepen a filter that cannot
            // move to cover it - the exact failure this window exists to prevent.
            if (std::abs (s.originHz - f) > 2.0 * window) continue;
            const double err = std::abs (s.freq - f);
            if (err < bestErr) { bestErr = err; best = i; }
        }
        return best;
    }

    /// The ear's bandwidth at f (Glasberg & Moore). A notch much narrower than
    /// this is largely filled in by the auditory filter whatever its depth.
    static double erbHz (double f) noexcept { return 24.7 * (0.00437 * f + 1.0); }

    /// How much a dB removed HERE matters: hearing and intelligibility both peak
    /// near 2.5 kHz, so the same cut costs a fraction as much at 200 Hz or 12 kHz.
    static double importance (double f) noexcept
    {
        const double oct = std::log2 (f / 2500.0) / 1.6;
        return 1.0 / (1.0 + oct * oct);
    }

    /**
        What the current filter set costs the listener, in ear-weighted dB-ERB.

        Depth alone is the wrong unit: a 100 Hz notch spans 1.4 ear-bandwidths at
        300 Hz and 0.12 at 8 kHz, so the same filter is expensive in one place and
        nearly free in the other. This is the number a budget should be set in,
        because it is the one the listener actually pays.
    */
    double harmNow() const noexcept
    {
        double harm = 0.0;
        for (double f = 100.0; f <= 16000.0; f *= 1.02)
            harm += std::abs (cutAtDb (f, -1)) * ((f * 0.02) / erbHz (f)) * importance (f);
        // A pulsed filter is only there for part of the time, and the ear averages.
        return harm * std::min (1.0, dutyCycle);
    }

    /**
        The filter's width, taken from the ring's OWN measured width rather than one
        Q for the whole spectrum.

        Measured on 20,190 catches at a studio: rings are ~70-140 Hz wide wherever
        they are, so a single Q is wrong at both ends. At Q12 a filter is 13 Hz wide
        at 150 Hz - a fifth of the ring, which is why low-end feedback survived - and
        370 Hz wide at 4 kHz, carving four times the ring out of the voice, which is
        where "muffled" came from.

        Q = f / width. Clamped: too narrow and a wandering tone slides out between
        re-triggers, too wide and it is the old tone damage by another name.
    */
    double qForWidth (double f, double widthHz) const noexcept
    {
        if (widthHz <= 0.0 || f <= 0.0) return defaultQ;
        return std::clamp (f / widthHz, qMin, qMax);
    }

    // ---- depth policy (spec §5, §10) ----------------------------------------
    // Q is wider than the spec's 30-60 because the rig disagreed with the spec:
    // the room's worst mode wanders ~500 Hz, which a Q40 notch (240 Hz wide) cannot
    // hold. Q25 spans ~390 Hz, so one filter keeps its grip instead of the tone
    // sliding out and spawning a fresh shallow notch each time.
    double defaultQ       = 25.0;   // fallback when a width was not measured
    double qMin           = 2.0;    // widest a filter may open (f/2 wide)
    // A feature this wide relative to its own centre is not a ring: nothing
    // natural is that broad and that flat. It gets a comb, not a filter.
    double combMinFraction = 0.25;  // width > a quarter of the centre frequency
    int    combTeeth       = 5;
    double combCoverage    = 0.20;  // total glass removed, as a fraction of width
    double harmBudget      = 0.0;   // ear-weighted dB-ERB; 0 = no budget
    bool   budgetReallocates = true;  // full budget: retire the stalest, or refuse?
    // Pulsed suppression. 1.0 = continuous, which is what ships until the ear
    // half of the bet has been tested in a room.
    double dutyCycle       = 1.0;
    double pulseHz         = 83.0;  // 12 ms: clear of the 4-20 Hz flutter band
    double pulseEdgeMs     = 1.5;   // raised-cosine edge, long enough not to click
    // How far apart in the cycle consecutive filters sit. 1 = maximally spread,
    // which is quietest for the ear; 0 = all together, which is louder but lets
    // overlapping skirts reinforce on the same ring. The trade is measurable.
    double pulseStagger    = 1.0;
    // Measured at the rig: a 10.6 kHz ring walked 9250 -> 10850 Hz within seconds,
    // and a 296 Hz filter (Q36) let it out - one guarded run in three failed at the
    // bypassed level. Up high the filter has to cover where the tone is GOING.
    // A ring does not sit still, and the filter has to cover where it is GOING.
    //
    // Measured at the rig: the hop is 7.7% (9293 -> 10006 -> 10716). Q20 is
    // 5.0% wide, so the ring steps cleanly outside the notch we just put on it,
    // every time, and 59% of the high-frequency rings that survived in the logs
    // had more than 15 dB of cut on them and lived anyway. Not a depth problem.
    // A coverage problem.
    //
    // Q13 is 7.7% - the measured hop exactly. And up here that is nearly free:
    // one ERB at 9 kHz is about 1000 Hz, so covering the whole hop range costs
    // around 0.7 of a critical band, where the same fraction at 500 Hz would
    // cost four of them. The ear sets the price and it is cheap at the top.
    double qMax           = 20.0;   // 5.0% wide: the cap below the hop band
    double qMaxHigh       = 13.0;   // 7.7% wide: one measured hop
    double qWidenAboveHz  = 2000.0;

    /// Kept, unused, with its measurement: widening the notch to cover a whole
    /// 7.7% hop is the obvious fix for a ring that steps outside its filter, and
    /// it does not work. Flat everywhere: harm +30% for 1% fewer runaways. Only
    /// above 2 kHz, on a rig where spikes CAN hop: runaways 52% -> 49% but kills
    /// 86% -> 77% and latency 589 -> 880 ms. The ring stepping outside the notch
    /// is real and well evidenced; making the notch bigger is not the answer.
    double qMaxAt (double f) const noexcept
    {
        return f >= qWidenAboveHz ? qMaxHigh : qMax;
    }

    double initialCutDb   = -12.0;   // first strike
    double fastTrackCutDb = -18.0;   // first strike on a known repeat offender
    double stepDb         = -6.0;    // deepen per re-trigger (negative)
    double softCapDb      = -18.0;   // normal ceiling
    double hardCapDb      = -24.0;   // absolute ceiling for a stubborn tone
    // ...and past THAT, for a ring that is winning anyway. Not a setting the
    // operator chooses: it is what the guard is allowed to do when the choice is
    // between tone damage and a runaway.
    double emergencyCapDb = -45.0;
    int    capHitsBeforeEmergency = 3;   // ~3 re-triggers at the cap, tens of ms
    int    calmHitsToForget = 8;         // calm re-triggers before growth is forgotten
    int    futileHitsBeforeGivingUp = 6; // louder-while-deeply-cut before we stop digging
    // Off only for REPLAYS. A recording cannot respond to a cut, so against one
    // the verdict fires by construction, snaps every filter to the dial, clears,
    // and re-escalates - a sawtooth that says nothing about the guard and hides
    // whether it holds. Live it stays on: that is the wiring alarm.
    bool   loopVerdict = true;
    // 20 dB is past any excess gain a real room path carries, so a ring
    // that keeps climbing through it is not being fought - it is being
    // missed, and the signal going round is not the one we are filtering.
    double futileCutDb = -20.0;
    double reopenMarginDb = 6.0;         // reopen this much shallower than last time
    double emergencyQ     = 80.0;        // 118 Hz at 9.4 kHz, still 40x a room mode

    // ---- release policy (spec §6) -------------------------------------------
    double holdSeconds    = 2.0;     // quiet time before a notch starts leaving
    double bleedDbPerSec  = 1.5;     // walk-out rate
    double retireDb       = -1.0;    // shallower than this -> drop the notch
    double minReleaseGap  = 0.5;     // min seconds between release steps



    //
    // The range matters more than either end. It was -5 to -7, a two-decibel
    // spread, which meant two or three filters anywhere in the region blocked
    // everything - including a ring screaming at -17 dB with 5 dB on it and 33 free
    // slots in the pool. Measured at the rig: 7108 Hz and 9897 Hz caught dozens of
    // times each, the nearest filter 344 and 508 Hz away, the cut on them never
    // moving off -5 and -3 dB. The budget was refusing to cover them.
    //
    // Widening the urgent end does not touch the quiet case - T29 is unchanged at
    // -9.3 dB across it - because level is what selects between them. Dullness
    // while singing and dullness during a howl are different questions and level
    // is what tells them apart.
public:
    enum class Refusal { None = 0, AlreadyCovered = 1, RegionTooDark = 2, AllLocked = 3 };
    Refusal lastRefusal = Refusal::None;

    /// How many filters have concluded they are not in the loop: cutting at
    /// maximum depth while their ring got louder anyway. Non-zero means the
    /// guard's output is very likely not the signal reaching the speakers, and
    /// the fix is routing, not more suppression.
    int ineffectiveEver = 0;
    double lastIneffectiveS = -1.0e9;
    /// True if we concluded we are not in the loop within the last `seconds`.
    /// This is what an operator should be shown: a wiring fault does not heal
    /// because the ring that revealed it went quiet.
    bool notInLoopRecently (double nowSeconds, double seconds = 30.0) const noexcept
    {
        return ineffectiveEver > 0 && (nowSeconds - lastIneffectiveS) < seconds;
    }
    int ineffectiveNow() const noexcept
    {
        int n = 0;
        for (const auto& s : slots) if (s.active && s.ineffective) ++n;
        return n;
    }

    // A frequency already cut this deep does not need another filter on it.
    double coveredDb      = -15.0;



    double freqTrack      = 0.30;    // how fast a notch follows a drifting tone

private:
    /** How much cut is already sitting on this exact frequency.

        The real peaking-EQ magnitude, summed, not an approximation. It used to
        weight each filter linearly over six bandwidths, and that was wrong in the
        direction that matters: measured at the rig, a ring at 7274 Hz with a filter
        179 Hz away was scored as already having 21.6 dB on it, so the budget
        refused to cover it - while the actual response there was 8 dB. The ring
        climbed to -13.6 dB under 8 dB of cut, and across the whole session the
        median cut ON a caught frequency was 3.1 dB, with a quarter of catches
        getting less than 2.

        A budget that guesses generously about coverage does not limit dullness, it
        just leaves rings uncovered. This runs on triggers, not per sample, so the
        trigonometry is affordable.
    */
public:
    double cutAtDb (double f, int skip) const noexcept
    {
        if (f <= 0.0) return 0.0;
        const double w = 2.0 * juce::MathConstants<double>::pi * f / fs;
        const double cosW = std::cos (w), cos2W = std::cos (2.0 * w);
        const double sinW = std::sin (w), sin2W = std::sin (2.0 * w);

        juce::ignoreUnused (cosW, cos2W, sinW, sin2W);
        double total = 0.0;
        for (int i = 0; i < MaxNotches; ++i)
        {
            if (i == skip) continue;        // the filter that will handle f itself
            total += cutOfSlot (i, f);
        }
        return total;
    }

    /// One filter's own contribution at f, in dB (negative). The exact RBJ peaking
    /// magnitude, not an approximation: a linear falloff once scored a filter
    /// 179 Hz away as 21.6 dB of coverage when the truth was 8.
    double cutOfSlot (int i, double f) const noexcept
    {
        const auto& s = slots[(size_t) i];
        // targetDb, not currentDb: the smoothed value lags by the attack time, so a
        // budget read from it lets a burst of triggers all pass the check before any
        // of them has taken effect.
        if (! s.active || s.targetDb > -0.1 || f <= 0.0) return 0.0;

        const double w = 2.0 * juce::MathConstants<double>::pi * f / fs;
        const double cosW = std::cos (w), cos2W = std::cos (2.0 * w);
        const double sinW = std::sin (w), sin2W = std::sin (2.0 * w);

        const double q  = qForDepth (s.q, s.targetDb);
        const double A  = std::pow (10.0, s.targetDb / 40.0);
        const double w0 = 2.0 * juce::MathConstants<double>::pi * s.freq / fs;
        const double a  = std::sin (w0) / (2.0 * q);
        const double c0 = std::cos (w0);

        auto mag = [&] (double k0, double k1, double k2)
        {
            const double re = k0 + k1 * cosW + k2 * cos2W;
            const double im = -(k1 * sinW + k2 * sin2W);
            return std::sqrt (re * re + im * im);
        };
        const double num = mag (1.0 + a * A, -2.0 * c0, 1.0 - a * A);
        const double den = mag (1.0 + a / A, -2.0 * c0, 1.0 - a / A);
        return 20.0 * std::log10 (std::max (num / den, 1.0e-12));
    }

    int findFree() const noexcept
    {
        for (int i = 0; i < MaxNotches; ++i) if (! slots[(size_t) i].active) return i;
        return -1;
    }

    /** Evict the least-recently-hit unlocked notch. -1 if all are locked. */
    /// The least-recently-hit unlocked filter, provided nothing has re-triggered
    /// it since `before`. -1 if every filter is still being knocked on.
    int stalestBefore (double before) const noexcept
    {
        int    victim = -1;
        double oldest = 1.0e18;
        for (int i = 0; i < MaxNotches; ++i)
        {
            const auto& s = slots[(size_t) i];
            if (! s.active || s.locked || s.manual) continue;
            if (s.lastHitS > before) continue;                // still earning its keep
            if (s.lastHitS < oldest) { oldest = s.lastHitS; victim = i; }
        }
        return victim;
    }

    int stealLru() noexcept
    {
        int    victim = -1;
        double oldest = 1.0e18;
        for (int i = 0; i < MaxNotches; ++i)
        {
            const auto& s = slots[(size_t) i];
            if (! s.active || s.locked) continue;
            if (s.lastHitS < oldest) { oldest = s.lastHitS; victim = i; }
        }
        if (victim >= 0) { slots[(size_t) victim] = NotchSlot{}; filters[(size_t) victim].reset(); }
        return victim;
    }

    // ---- offender histogram (spec §8) ---------------------------------------
    // 1/24-octave buckets across the watched band; each bucket's count decays by
    // half every `histHalfLife` seconds (lazily, on touch).
    struct Bucket { double count = 0.0; double stamp = 0.0; double deepestDb = 0.0; };

    static constexpr double histBaseHz    = 200.0;    // band low edge
    static constexpr double histTopHz     = 16000.0;  // band high edge
    static constexpr int    histBuckets   = 160;      // 24/oct * log2(80) ~= 152
    static constexpr double histHalfLife  = 300.0;    // halve every 5 min
    static constexpr int    fastTrackStrikes = 3;
    static constexpr int    hopBuckets       = 3;   // +-8.9%: one measured hop

    int bucketOf (double f) const noexcept
    {
        if (f <= 0.0) return -1;
        const int b = (int) std::lround (24.0 * std::log2 (f / histBaseHz));
        if (b < 0 || b >= histBuckets) return -1;
        return b;
    }

    double decayed (Bucket& h, double nowSeconds) const noexcept
    {
        const double dt = nowSeconds - h.stamp;
        if (dt > 0.0) { h.count *= std::exp2 (-dt / histHalfLife); h.stamp = nowSeconds; }
        return h.count;
    }

    double offenderCount (double f, double nowSeconds) noexcept
    {
        const int b = bucketOf (f);
        return b < 0 ? 0.0 : decayed (histogram[(size_t) b], nowSeconds);
    }

    /**
        Seed the offender histogram from a profile learned in EARLIER sessions.

        The histogram half-lives in five minutes and dies with the process, so
        every night the guard starts out knowing nothing - and then spends the
        first few minutes relearning what the room does every single time.

        Measured at the rig: 51,501 catches over 27 sessions, and 50% of them
        land in eight 1/6-octave buckets, 80% in fourteen, out of 46 occupied.
        That is not a room fingerprint that has to be remeasured; it is stable
        across sessions, because what shapes the loop up there is the mic's
        presence peak and the ear canal, and neither moves when the room does.

        Called before audio starts, from the message thread. `strikes` is in the
        same units as noteOffender's count, so fastTrackStrikes worth of history
        makes the first ring in a known-bad region open at the fast-track depth
        instead of climbing the ladder from -12 dB.
    */
    void seedOffender (double f, double strikes, double nowSeconds) noexcept
    {
        const int b = bucketOf (f);
        if (b < 0 || strikes <= 0.0) return;
        auto& h = histogram[(size_t) b];
        h.stamp  = nowSeconds;
        h.count += strikes;
    }

    void noteOffender (double f, double nowSeconds) noexcept
    {
        const int b = bucketOf (f);
        if (b < 0) return;
        auto& h = histogram[(size_t) b];
        decayed (h, nowSeconds);
        h.count += 1.0;
    }

    /** Record how deep this frequency actually had to be cut before it gave up. */
    void noteDepth (double f, double db, double nowSeconds) noexcept
    {
        const int b = bucketOf (f);
        if (b < 0) return;
        auto& h = histogram[(size_t) b];
        decayed (h, nowSeconds);
        h.deepestDb = std::min (h.deepestDb, db);
    }

    /** How deep it took last time, or 0 once the bucket has faded. */
    /**
        How deep this frequency, OR ONE NEXT DOOR, had to be cut last time.

        The neighbourhood is the whole point. Measured at the rig 2026-09-27: a
        ring was fought to the cap at 9293 Hz, went quiet, and came back at
        10006, then 10716 - hops of 7.7%. Buckets are 1/24 octave, 2.9%, so the
        returning ring landed three buckets from everything we had learned and
        opened at -12 dB as a total stranger, while it kept every decibel it had
        already built. It did that three times and won every time.

        We were never too slow to see it. The log has the first catch at 21 ms.
        We were throwing away the fight each time it stepped sideways.

        So the search spans +-3 buckets, +-8.9%, which covers a measured hop and
        little more. Still clamped at the dial by the caller, so this buys the
        SPEED of not re-climbing the ladder and never permission to cut deeper.
    */
    double rememberedDepth (double f, double nowSeconds) noexcept
    {
        const int b = bucketOf (f);
        if (b < 0) return 0.0;

        double best = 0.0;
        for (int d = -hopBuckets; d <= hopBuckets; ++d)
        {
            const int i = b + d;
            if (i < 0 || i >= histBuckets) continue;
            auto& h = histogram[(size_t) i];
            if (decayed (h, nowSeconds) < 0.5) { h.deepestDb = 0.0; continue; }
            best = std::min (best, h.deepestDb);
        }
        return best;
    }

    std::array<NotchSlot, MaxNotches> slots {};
    std::array<Biquad,    MaxNotches> filters {};
    std::array<Bucket, (size_t) histBuckets> histogram {};
    double fs = 48000.0;
    double attackCoeff = 0.23;    // toward a deeper cut - fast
    double releaseCoeff = 0.03;   // back toward flat - slow
    int64_t pulseSamples = 0;     // the pulse clock, advanced by process()
};

} // namespace fk
