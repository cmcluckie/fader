#pragma once
#include <juce_audio_devices/juce_audio_devices.h>
#include <atomic>
#include <array>
#include "FeedbackDetector.h"
#include "NotchBank.h"

namespace fk
{
constexpr int kMaxNotches = 12;
constexpr int kMaxInputs  = 8;   // simultaneous feedback channels

/**
    Headless real-time engine. PluginProcessor::processBlock adapted to a Core
    Audio callback, generalised to N channels: the device is opened with just the
    checked input channels, and slot i drives the i-th of them (ascending order).
    Each active slot both analyses (for the display/log) and notches its input.

    Audio-thread rules hold: nothing here allocates, locks, or logs. Control comes
    in through an SPSC command queue; telemetry goes out through an SPSC event
    queue and racy-by-design snapshot reads. Everything is preallocated for the
    maximum channel count.
*/
class AudioEngine : public juce::AudioIODeviceCallback
{
public:
    static constexpr int maxChans = kMaxInputs;

    // ---- control (called from the OSC thread) -------------------------------
    /// <summary>How many input slots are live (= number of checked channels).</summary>
    void setActiveChannels (int n) noexcept { activeChans.store (juce::jlimit (0, maxChans, n)); }
    int  activeChannels() const noexcept    { return activeChans.load(); }

    /// <summary>
    /// Where each slot's processed audio goes, as a RANK into the device's enabled
    /// output channels (JUCE hands the callback one dense pointer per enabled
    /// channel, ascending). Slot i defaults to rank i - the old mirror behaviour.
    /// Call only while the device is being (re)configured, not mid-callback.
    /// </summary>
    void setOutputMap (const int* ranks, int count) noexcept
    {
        for (int i = 0; i < maxChans; ++i)
            outRank[(size_t) i] = (i < count && ranks[i] >= 0) ? ranks[i] : i;
    }

    /// <summary>Pass audio through untouched, keeping every notch's state intact.</summary>
    void setBypass (bool b) noexcept         { bypassed.store (b); }
    bool isBypassed() const noexcept         { return bypassed.load(); }

    /// <summary>
    /// Return-path diagnostic. Replaces what every armed slot writes to its return:
    ///   0  = off (normal audio)
    ///  <0  = hard silence
    ///  >0  = sine at that frequency
    /// This answers the only question a routing bug ever asks - "is the console
    /// actually listening to MY output?" - without touching the signal chain.
    /// </summary>
    void setTestTone (float hz) noexcept     { testTone.store (hz); }

    void setMaxCutDb (float v) noexcept      { maxCutDb.store (v); }
    void setNotchQ (float v) noexcept        { notchQ.store (v); }
    void setReleaseSeconds (float v) noexcept{ releaseSeconds.store (v); }
    void setProminenceDb (float v) noexcept  { prominenceDb.store (v); }
    void setPersistFrames (int v) noexcept   { persistFrames.store (v); }
    void setPitchTolerance (float v) noexcept{ pitchTolerance.store (v); }
    void setHarmonicDb (float v) noexcept    { harmonicDb.store (v); }
    void setFloorDb (float v) noexcept       { floorDb.store (v); }
    void setMinFreq (float v) noexcept       { minFreq.store (v); }
    void setMaxFreq (float v) noexcept       { maxFreq.store (v); }
    void setStabilityHz (float v) noexcept   { stabilityHz.store (v); }
    void setGrowthDb (float v) noexcept      { growthDb.store (v); }
    void setInputGate (float v) noexcept     { inputGate.store (v); }

    void placeNotch  (int ch, float hz, float depthDb) noexcept { push ({ Cmd::Place, ch, 0, hz, depthDb }); }
    void removeNotch (int ch, int slot) noexcept                { push ({ Cmd::Remove, ch, slot, 0, 0 }); }
    void lockNotch   (int ch, int slot, bool on) noexcept       { push ({ on ? Cmd::Lock : Cmd::Unlock, ch, slot, 0, 0 }); }
    void clearNotches(int ch, bool includeLocked) noexcept      { push ({ includeLocked ? Cmd::ClearAll : Cmd::Clear, ch, 0, 0, 0 }); }
    void lockAll     (int ch) noexcept                          { push ({ Cmd::LockAll, ch, 0, 0, 0 }); }

    // ---- telemetry (called from the OSC thread) -----------------------------
    struct EventOut { int ch; float hz; float levelDb; };
    bool popEvent (EventOut& out) noexcept
    {
        if (evtRead == evtWrite) return false;
        out = evtQueue[(size_t) evtRead];
        evtRead = (evtRead + 1) % evtQueueSize;
        return true;
    }

    /** Copy one slot's notch state. Racy by design; a torn field just makes one display frame odd. */
    void snapshotNotches (int ch, std::array<NotchSlot, kMaxNotches>& out) const noexcept
    {
        for (int i = 0; i < kMaxNotches; ++i) out[(size_t) i] = banks[(size_t) ch].getSlot (i);
    }

    FeedbackDetector& detector (int ch) noexcept { return detectors[(size_t) ch]; }
    float cpuLoad() const noexcept { return cpu.load(); }
    bool  running() const noexcept { return isRunning.load(); }

    // ---- AudioIODeviceCallback ---------------------------------------------
    void audioDeviceAboutToStart (juce::AudioIODevice* device) override
    {
        sr = device->getCurrentSampleRate();
        const int block = device->getCurrentBufferSizeSamples();
        for (int ch = 0; ch < maxChans; ++ch)
        {
            banks[(size_t) ch].prepare (sr, block);
            detectors[(size_t) ch].prepare (sr);
        }
        elapsed = 0.0;
        lastReleaseCheck = 0.0;
        isRunning.store (true);
    }

    void audioDeviceStopped() override { isRunning.store (false); }

    void audioDeviceIOCallbackWithContext (const float* const* inputs, int numInputs,
                                           float* const* outputs, int numOutputs,
                                           int numSamples,
                                           const juce::AudioIODeviceCallbackContext&) override
    {
        juce::ScopedNoDenormals noDenormals;

        drainCommands();

        FeedbackDetector::Params p;
        p.prominenceDb   = prominenceDb.load();
        p.persistFrames  = persistFrames.load();
        p.pitchTolerance = pitchTolerance.load();
        p.harmonicDb     = harmonicDb.load();
        p.floorDb        = floorDb.load();
        p.minFreq        = minFreq.load();
        p.maxFreq        = maxFreq.load();
        p.stabilityHz    = stabilityHz.load();
        p.growthDb       = growthDb.load();
        p.inputGateDb    = inputGate.load();
        const float q       = notchQ.load();
        const float softCap = maxCutDb.load();

        const int  active = juce::jmin (activeChans.load(), maxChans, numInputs);
        const bool bypass = bypassed.load();

        // Clear every output first; slots then write into their mapped returns.
        // Anything not driven is silence, never garbage.
        for (int c = 0; c < numOutputs; ++c)
            if (outputs[c] != nullptr) juce::FloatVectorOperations::clear (outputs[c], numSamples);

        for (int ch = 0; ch < active; ++ch)
        {
            const int rank = outRank[(size_t) ch];
            if (rank < 0 || rank >= numOutputs) continue;
            if (inputs[ch] == nullptr || outputs[rank] == nullptr) continue;

            const float* in  = inputs[ch];
            float*       out = outputs[rank];
            for (int n = 0; n < numSamples; ++n) out[n] = in[n];   // passthrough first

            auto& det  = detectors[(size_t) ch];
            auto& bank = banks[(size_t) ch];
            det.setParams (p);
            bank.defaultQ    = q;
            bank.softCapDb   = softCap;              // user "max cut" == the soft cap
            bank.hardCapDb   = softCap - 6.0f;       // one more step for a stubborn tone
            bank.holdSeconds = (double) releaseSeconds.load();

            det.push (out, numSamples);   // analyse pre-notch signal on the ring buffer

            FeedbackDetector::Event ev;
            while (det.popEvent (ev))
            {
                bank.trigger (ev.freq, elapsed);           // checked = cut; bank owns depth
                pushEvent ({ ch, ev.freq, ev.levelDb });   // C# logs + displays it
            }

            // Bypassed: notch state still tracks, the audio just isn't filtered.
            bank.process (out, numSamples, bypass);
        }

        // Diagnostic override, applied last so it replaces whatever the slot wrote.
        const float tone = testTone.load();
        if (tone != 0.0f)
        {
            for (int ch = 0; ch < active; ++ch)
            {
                const int rank = outRank[(size_t) ch];
                if (rank < 0 || rank >= numOutputs || outputs[rank] == nullptr) continue;
                float* out = outputs[rank];
                if (tone < 0.0f)
                {
                    juce::FloatVectorOperations::clear (out, numSamples);
                }
                else
                {
                    const double step = 2.0 * juce::MathConstants<double>::pi * tone / sr;
                    double p = tonePhase;
                    for (int n = 0; n < numSamples; ++n) { out[n] = 0.2f * (float) std::sin (p); p += step; }
                    if (ch == active - 1) tonePhase = std::fmod (p, 2.0 * juce::MathConstants<double>::pi);
                }
            }
        }

        elapsed += numSamples / sr;
        if (elapsed - lastReleaseCheck > 1.0)
        {
            lastReleaseCheck = elapsed;
            for (int ch = 0; ch < active; ++ch)
                banks[(size_t) ch].release (elapsed);   // bank owns hold/bleed/retire
        }
    }

private:
    struct Cmd
    {
        enum Type { Place, Remove, Lock, Unlock, Clear, ClearAll, LockAll } type = Place;
        int ch = 0; int slot = 0; float hz = 0; float depth = 0;
    };

    void push (const Cmd& c) noexcept
    {
        const int next = (cmdWrite + 1) % cmdQueueSize;
        if (next == cmdRead.load()) return;                        // full: drop
        cmdQueue[(size_t) cmdWrite.load()] = c;
        cmdWrite.store (next);
    }

    void drainCommands() noexcept
    {
        while (cmdRead.load() != cmdWrite.load())
        {
            const int r = cmdRead.load();
            const Cmd c = cmdQueue[(size_t) r];
            cmdRead.store ((r + 1) % cmdQueueSize);

            if (c.ch < 0 || c.ch >= maxChans) continue;
            auto& b = banks[(size_t) c.ch];
            switch (c.type)
            {
                case Cmd::Place:    b.placeManual (c.hz, c.depth, elapsed); break;
                case Cmd::Remove:   b.removeAt (c.slot); break;
                case Cmd::Lock:     if (c.slot >= 0 && c.slot < kMaxNotches) b.slotRef (c.slot).locked = true;  break;
                case Cmd::Unlock:   if (c.slot >= 0 && c.slot < kMaxNotches) b.slotRef (c.slot).locked = false; break;
                case Cmd::Clear:    b.clear (false); break;
                case Cmd::ClearAll: b.clear (true);  break;
                case Cmd::LockAll:  b.lockAll();     break;
            }
        }
    }

    void pushEvent (const EventOut& e) noexcept
    {
        const int next = (evtWrite + 1) % evtQueueSize;
        if (next == evtRead) return;
        evtQueue[(size_t) evtWrite] = e;
        evtWrite = next;
    }

    std::array<NotchBank<kMaxNotches>, maxChans> banks;
    std::array<FeedbackDetector, maxChans>       detectors;

    std::atomic<int>   activeChans { 0 };                      // default: nothing checked = nothing cut
    std::atomic<float> maxCutDb { -18.0f }, notchQ { 40.0f }, releaseSeconds { 2.0f };  // spec §5-6 defaults
    std::atomic<float> prominenceDb { 12.0f }, pitchTolerance { 0.006f }, harmonicDb { 20.0f }, floorDb { -70.0f };
    std::atomic<float> minFreq { 200.0f }, maxFreq { 16000.0f };
    std::atomic<float> stabilityHz { 5.0f }, growthDb { 3.0f }, inputGate { -55.0f };
    std::atomic<int>   persistFrames { 9 };
    std::atomic<bool>  bypassed { false };
    std::atomic<float> testTone { 0.0f };
    double             tonePhase = 0.0;
    std::array<int, maxChans> outRank { 0, 1, 2, 3, 4, 5, 6, 7 };   // slot -> dense output rank
    std::atomic<float> cpu { 0.0f };
    std::atomic<bool>  isRunning { false };

    static constexpr int   cmdQueueSize = 128;
    static constexpr int   evtQueueSize = 128;

    std::array<Cmd, cmdQueueSize> cmdQueue {};
    std::atomic<int> cmdRead { 0 }, cmdWrite { 0 };
    std::array<EventOut, evtQueueSize> evtQueue {};
    int evtRead = 0, evtWrite = 0;

    double sr = 48000.0, elapsed = 0.0, lastReleaseCheck = 0.0;
};

} // namespace fk
