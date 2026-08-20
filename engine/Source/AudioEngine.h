#pragma once
#include <juce_audio_devices/juce_audio_devices.h>
#include <atomic>
#include <array>
#include "FeedbackDetector.h"
#include "NotchBank.h"

namespace fk
{
constexpr int kMaxNotches = 12;

/**
    Headless real-time engine. This is PluginProcessor::processBlock adapted to a
    Core Audio callback: same DSP (FeedbackDetector, NotchBank), same lock-free
    command intake and event telemetry, no GUI and no file logging (the C# app
    owns logging via /fk/event).

    Audio-thread rules hold exactly as in the plugin: nothing here allocates,
    locks, or logs. Control comes in through an SPSC command queue; telemetry
    goes out through an SPSC event queue and racy-by-design snapshot reads.
*/
class AudioEngine : public juce::AudioIODeviceCallback
{
public:
    enum class Mode { Off = 0, Assist = 1, Auto = 2 };
    static constexpr int numChans = 2;                       // 0 = LEAD, 1 = BGV

    // ---- control (called from the OSC thread) -------------------------------
    void setMode (Mode m) noexcept          { mode.store ((int) m); }
    void setMaxCutDb (float v) noexcept      { maxCutDb.store (v); }
    void setNotchQ (float v) noexcept        { notchQ.store (v); }
    void setReleaseSeconds (float v) noexcept{ releaseSeconds.store (v); }
    void setProminenceDb (float v) noexcept  { prominenceDb.store (v); }
    void setPersistFrames (int v) noexcept   { persistFrames.store (v); }
    void setPitchTolerance (float v) noexcept{ pitchTolerance.store (v); }
    void setHarmonicDb (float v) noexcept    { harmonicDb.store (v); }
    void setFloorDb (float v) noexcept       { floorDb.store (v); }

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

    /** Copy one channel's slot state. Racy by design; a torn field just makes one display frame odd. */
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
        for (int ch = 0; ch < numChans; ++ch)
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

        // Level-triggered params, reassembled cheaply each block.
        const auto m = (Mode) mode.load();
        FeedbackDetector::Params p;
        p.prominenceDb   = prominenceDb.load();
        p.persistFrames  = persistFrames.load();
        p.pitchTolerance = pitchTolerance.load();
        p.harmonicDb     = harmonicDb.load();
        p.floorDb        = floorDb.load();
        const float q    = notchQ.load();

        const int chans = juce::jmin (numChans, juce::jmin (numInputs, numOutputs));

        for (int ch = 0; ch < chans; ++ch)
        {
            const float* in  = inputs[ch];
            float*       out = outputs[ch];
            for (int n = 0; n < numSamples; ++n) out[n] = in[n];   // passthrough first

            auto& det  = detectors[(size_t) ch];
            auto& bank = banks[(size_t) ch];
            det.setParams (p);
            bank.defaultQ = q;

            det.push (out, numSamples);   // analyse pre-notch signal on the ring buffer

            FeedbackDetector::Event ev;
            while (det.popEvent (ev))
            {
                if (m == Mode::Auto) bank.trigger (ev.freq, stepDb, maxCutDb.load(), elapsed);
                pushEvent ({ ch, ev.freq, ev.levelDb });          // C# logs it
            }

            bank.process (out, numSamples, m == Mode::Off);       // Off passes audio untouched
        }

        // any output channels we don't drive get silence, never garbage
        for (int ch = chans; ch < numOutputs; ++ch)
            if (outputs[ch] != nullptr) juce::FloatVectorOperations::clear (outputs[ch], numSamples);

        elapsed += numSamples / sr;
        if (elapsed - lastReleaseCheck > 1.0)
        {
            lastReleaseCheck = elapsed;
            for (auto& b : banks) b.release (elapsed, (double) releaseSeconds.load(), 1.0);
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

            auto& b = banks[(size_t) juce::jlimit (0, numChans - 1, c.ch)];
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

    std::array<NotchBank<kMaxNotches>, numChans> banks;
    std::array<FeedbackDetector, numChans>       detectors;

    std::atomic<int>   mode { (int) Mode::Assist };            // default assist (§8)
    std::atomic<float> maxCutDb { -12.0f }, notchQ { 20.0f }, releaseSeconds { 120.0f };
    std::atomic<float> prominenceDb { 12.0f }, pitchTolerance { 0.006f }, harmonicDb { 20.0f }, floorDb { -70.0f };
    std::atomic<int>   persistFrames { 5 };
    std::atomic<float> cpu { 0.0f };
    std::atomic<bool>  isRunning { false };

    static constexpr float stepDb = 3.0f;
    static constexpr int   cmdQueueSize = 128;
    static constexpr int   evtQueueSize = 64;

    std::array<Cmd, cmdQueueSize> cmdQueue {};
    std::atomic<int> cmdRead { 0 }, cmdWrite { 0 };
    std::array<EventOut, evtQueueSize> evtQueue {};
    int evtRead = 0, evtWrite = 0;

    double sr = 48000.0, elapsed = 0.0, lastReleaseCheck = 0.0;
};

} // namespace fk
