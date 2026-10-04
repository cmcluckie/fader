#pragma once

/*
    What the engine runs with until the app says otherwise - and what the test
    library runs with, so that every gate layer exercises the bank and detector
    the rig actually runs.

    They were not shared before, and the two machines had drifted far enough
    apart that the gate passed while the room screamed: merge window Q 12 against
    the bank's own 25, first strike -18 (the app's Attack 2) against -12, caps
    -12/-18 against -18/-24, prominence 12 against 10. See NotchBank::configure
    and fk_loopdsp's applyEngineDefaults.

    The app's Attack levels, for reference (FeedbackController.PushAttack):
        0 gentle  : persistFrames 10, initialCut  -6
        1 normal  : persistFrames  6, initialCut -12
        2 fast    : persistFrames  6, initialCut -18    <- the rig, 2026-10
    and audio.json carries MinHz, MaxHz, FloorDb, MaxCutDb, HarmBudget.

    Found again on 2026-10-04, by reading rather than by a room screaming: how
    long a quiet filter is held before it starts to leave. The engine has held
    for 10 s since its first day (its own start-up value); the test library and
    the fuzz never set it and ran the bank's built-in 2 s. Every gate layer was
    measuring a guard that let go five times sooner than the one in the room.
    It lives here now.
*/
namespace fk::defaults
{
    // detector
    constexpr float prominenceDb  = 12.0f;
    constexpr int   persistFrames = 6;
    constexpr float floorDb       = -70.0f;
    constexpr float minFreq       = 200.0f;
    constexpr float maxFreq       = 16000.0f;
    constexpr float stabilityHz   = 5.0f;
    constexpr float growthDb      = 3.0f;
    // The input gate was -55 from the August spec ("don't chase noise between
    // songs"). In a quiet room with an open microphone that is backwards: nothing
    // is analysed until the howl itself is loud enough to open the gate. Measured
    // on the 17 real howls in the fixture set, 2026-10-03: at -55 the median catch
    // was 213 ms before the ring was visible and one howl was never detected; at
    // -90 (the detector's own default) 533 ms and 17 of 17, voice hits unchanged.
    constexpr float inputGateDb   = -90.0f;
    // bank
    constexpr float notchQ        = 12.0f;
    constexpr float initialCutDb  = -6.0f;
    constexpr float maxCutDb      = -18.0f;
    constexpr float releaseSeconds = 10.0f;   // quiet time before a filter starts to leave (NotchBank::holdSeconds)
}
