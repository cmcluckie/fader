#pragma once
#include <algorithm>
#include <cmath>

namespace fk
{
/**
    The broadband rescue.

    Everything else in this engine is narrow: find the ring, put a filter on it.
    That is the right tool until the ring is faster than the tool. Measured at
    the rig 2026-10-03, on cold jumps to 20-22 dB over the ring point: the noise
    floor to -21 dB in 150 ms, about 700 dB/s, five decibels a frame. The
    detector has to find it, the bank has to place and deepen a filter on the
    right frequency, and each frame either costs is five decibels more in the
    room. On those nights the thing that ended it was a test script dropping a
    desk fader.

    A broadband cut needs no frequency. It lowers the loop gain everywhere at
    once, the ring stops growing on the next trip round the loop, and the narrow
    filters get the tenth of a second they need. Then it comes back up, slowly,
    and if the ring comes back with it, it goes down again: attack first, hard,
    then earn the way back. The survey of the field lists a plain gain reduction
    as the usual last resort (van Waterschoot & Moonen 2011, III-B); this is
    that, with a release.

    What it costs is the reason it is fenced: a false trigger is not a notch, it
    is a dropout. So it is only ever asked for by two things -

      runaway : the detector's full runaway test (never passed on the six sung
                fixtures), on a ring that has ALREADY had its filter and is
                still running at or above runawayDb;
      loud    : an isolated line above the voice, at howl level, for several
                frames - read off the raw spectrum, deliberately independent of
                the suspect logic, for the night the detector is wrong.

    Both live in the callers (the engine and the test library call the same two
    functions below); this class is only the gain and its timing.
*/
class RescueDuck
{
public:
    enum class Reason { None = 0, Runaway = 1, LoudLine = 2 };

    void prepare (double sampleRate) noexcept
    {
        fs = sampleRate;
        reset();
    }

    void reset() noexcept
    {
        gainDb = targetDb = 0.0;
        lastTriggerS = lastStepS = downSinceS = -1.0e9;
        futileUntilS = -1.0e9;
        startLevelDb = -200.0f;
    }

    /**
        Ask for the duck. `levelDb` is the offending line's level, for the
        futility test: a duck that has been down a second while the line has not
        fallen is not in that line's loop, and holding it only mutes the room.
    */
    void trigger (double nowS, float levelDb, float hz, Reason why) noexcept
    {
        if (! enabled || nowS < futileUntilS) return;

        const bool fresh = targetDb > -0.01 && gainDb > -0.5;
        if (fresh)
        {
            targetDb     = firstDb;
            downSinceS   = nowS;
            startLevelDb = levelDb;
            ++episodes;
        }
        else
        {
            // Not in the loop: we have been well down for futileS and the line is
            // no quieter than when we started. Give the room back and say so.
            if (nowS - downSinceS >= futileS && gainDb <= firstDb + 1.0 && levelDb >= startLevelDb - 3.0f)
            {
                futileUntilS = nowS + futileLatchS;
                targetDb = 0.0;
                ++futileEver;
                return;
            }
            if (nowS - lastStepS >= stepGapS)
                targetDb = std::max (floorDb, std::min (targetDb, gainDb) + stepDb);
        }
        lastStepS = lastTriggerS = nowS;
        lastHz = hz; lastLevelDb = levelDb; lastReason = why;
        deepestDb = std::min (deepestDb, targetDb);
        ++triggers;
    }

    /** Apply to one block in place, ramped across it. Call every block. */
    void process (float* data, int numSamples, double nowS) noexcept
    {
        const double blockS = (double) numSamples / fs;

        // Earn the way back: after the hold, the TARGET climbs at the release
        // rate. A trigger on the way up pulls it straight back down.
        if (targetDb < 0.0 && nowS - lastTriggerS >= holdS)
            targetDb = std::min (0.0, targetDb + releaseDbPerS * blockS);

        // Going down is the emergency and takes a couple of milliseconds; coming
        // up simply follows the target, which is already slow.
        const double before = gainDb;
        if (targetDb < gainDb) gainDb += (targetDb - gainDb) * (1.0 - std::exp (-blockS / attackS));
        else                   gainDb = targetDb;
        if (gainDb > -0.01 && targetDb > -0.01) gainDb = 0.0;

        if (data == nullptr || (before > -0.005 && gainDb > -0.005)) return;

        const double g0 = std::pow (10.0, before / 20.0), g1 = std::pow (10.0, gainDb / 20.0);
        const double step = (g1 - g0) / (double) std::max (1, numSamples);
        double g = g0;
        for (int n = 0; n < numSamples; ++n) { g += step; data[n] = (float) (data[n] * g); }
    }

    double depthDb() const noexcept   { return gainDb; }
    bool   active() const noexcept    { return gainDb < -0.1 || targetDb < -0.1; }
    bool   futile (double nowS) const noexcept { return nowS < futileUntilS; }

    // ---- policy ----------------------------------------------------------
    bool   enabled       = true;
    double firstDb       = -12.0;   // the first strike
    double stepDb        = -6.0;    // each further trigger while down
    double floorDb       = -30.0;
    double stepGapS      = 0.010;   // no more than one step per two detector frames
    double attackS       = 0.002;
    double holdS         = 0.250;   // after the LAST trigger
    double releaseDbPerS = 20.0;
    double futileS       = 1.0;     // down this long, line no quieter: not in the loop
    double futileLatchS  = 30.0;
    float  runawayDb     = -48.0f;  // a runaway is first called near -75 with a -45 dB
                                    // filter; still running 27 dB later, it is losing.
                                    // At -55 this also took three brief HF lines in
                                    // near-silence in the 09-27 session (peaks -51 to -54,
                                    // gone in 0.3 s), one of them a second before a
                                    // phrase began; a 700 dB/s ring crosses the extra
                                    // 7 dB in 10 ms.
    int    loudFrames    = 3;       // the loud-line trigger needs this many frames (16 ms)

    // ---- for telemetry and tests -------------------------------------------
    int    triggers = 0, episodes = 0, futileEver = 0;
    double deepestDb = 0.0;
    float  lastHz = 0.0f, lastLevelDb = -200.0f;
    Reason lastReason = Reason::None;

private:
    double fs = 48000.0;
    double gainDb = 0.0, targetDb = 0.0;
    double lastTriggerS = -1.0e9, lastStepS = -1.0e9, downSinceS = -1.0e9, futileUntilS = -1.0e9;
    float  startLevelDb = -200.0f;
};

/**
    The two questions, asked the same way by the engine and by the test library.

    `Event` is FeedbackDetector::Event; `Det` is FeedbackDetector. Templates so
    this header does not have to include the detector.
*/
template <class Event>
inline void rescueOnEvent (RescueDuck& duck, const Event& ev, double nowS) noexcept
{
    if (ev.runaway && ev.levelDb >= duck.runawayDb)
        duck.trigger (nowS, ev.levelDb, ev.freq, RescueDuck::Reason::Runaway);
}

template <class Det>
inline void rescueOnFrame (RescueDuck& duck, const Det& det, int& lastSeenFrames, double nowS) noexcept
{
    // Once when it qualifies, and again every loudFrames while it stays there.
    const int n = det.loudLineFrames();
    if (n < lastSeenFrames) lastSeenFrames = 0;
    if (n >= duck.loudFrames && n - lastSeenFrames >= duck.loudFrames)
    {
        lastSeenFrames = n;
        duck.trigger (nowS, det.loudLineDb(), det.loudLineHz(), RescueDuck::Reason::LoudLine);
    }
}
}
