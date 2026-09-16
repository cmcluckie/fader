// fk-engine — headless feedback-suppression engine.
//
// Audio passthrough + OSC control/telemetry, detection defaulting to ASSIST
// (analyse + report, never cut) per §8. Built with JUCE via FetchContent; see
// engine/CMakeLists.txt.
//
// A console app has no Cocoa run loop, so we do NOT rely on the JUCE message
// thread: OSC is delivered on its own socket-reader thread (RealtimeCallback),
// telemetry runs on a plain std::thread, and audio runs on the driver's own
// thread (Core Audio on macOS, ASIO or WASAPI on Windows). main() just keeps the
// process alive; the C# supervisor stops it.

#include <juce_audio_devices/juce_audio_devices.h>
#include <juce_osc/juce_osc.h>
#include <atomic>
#include <thread>
#include <csignal>
#include <memory>
#include <cstdio>
#include <vector>
#include <algorithm>
#include "AudioEngine.h"

// Cleared by SIGINT/SIGTERM or by /fk/quit; main() then stops the engine cleanly.
namespace { std::atomic<bool> gRun { true }; }

namespace fk
{
constexpr int   kEngineListenPort = 10024;              // app -> engine
constexpr int   kAppTelemetryPort = 10025;              // engine -> app (loopback, fixed)
constexpr auto  kLoopback         = "127.0.0.1";

class Engine : private juce::OSCReceiver::Listener<juce::OSCReceiver::RealtimeCallback>
{
public:
    bool start (const juce::String& preferredDevice, int sampleRate, int bufferSize)
    {
        // ---- audio -----------------------------------------------------------
        juce::AudioDeviceManager::AudioDeviceSetup setup;
        setup.outputDeviceName = preferredDevice;
        setup.inputDeviceName  = preferredDevice;
        setup.sampleRate       = sampleRate > 0 ? (double) sampleRate : 48000.0;
        setup.bufferSize       = bufferSize > 0 ? bufferSize          : 64;
        setup.useDefaultInputChannels  = true;
        setup.useDefaultOutputChannels = true;

        auto err = devices.initialise (AudioEngine::maxChans, AudioEngine::maxChans,
                                       nullptr, true, preferredDevice, &setup);

        // Given a setup, initialise() opens the name inside the first device type
        // only - WASAPI on Windows - so an ASIO device needs a second go.
        if (selectTypeFor (preferredDevice))
            err = devices.setAudioDeviceSetup (setup, true);

        if (err.isNotEmpty())
            juce::Logger::writeToLog ("audio init: " + err + " (staying up; pick a device via /fk/audio)");
        devices.addAudioCallback (&engine);

        // ---- osc -------------------------------------------------------------
        if (! receiver.connect (kEngineListenPort))
        {
            juce::Logger::writeToLog ("could not bind OSC " + juce::String (kEngineListenPort));
            return false;
        }
        receiver.addListener (this);
        sender.connect (kLoopback, kAppTelemetryPort);

        keepRunning.store (true);
        telemetry = std::thread ([this] { telemetryLoop(); });
        return true;
    }

    void stop()
    {
        keepRunning.store (false);
        if (telemetry.joinable()) telemetry.join();
        receiver.removeListener (this);
        receiver.disconnect();
        devices.removeAudioCallback (&engine);
        devices.closeAudioDevice();
    }

private:
    // ------------------------------------------------------------------ control
    // Runs on the OSC socket-reader thread. Only lock-free engine calls here.
    void oscMessageReceived (const juce::OSCMessage& m) override
    {
        const auto a = m.getAddressPattern().toString();

        if      (a == "/fk/notch/place"  && m.size() >= 3) engine.placeNotch  (m[0].getInt32(), m[1].getFloat32(), m[2].getFloat32());
        else if (a == "/fk/notch/remove" && m.size() >= 2) engine.removeNotch (m[0].getInt32(), m[1].getInt32());
        else if (a == "/fk/notch/lock"   && m.size() >= 3) engine.lockNotch   (m[0].getInt32(), m[1].getInt32(), m[2].getInt32() != 0);
        else if (a == "/fk/clear"        && m.size() >= 2) engine.clearNotches (m[0].getInt32(), m[1].getInt32() != 0);
        else if (a == "/fk/lockall"      && m.size() >= 1) engine.lockAll     (m[0].getInt32());
        else if (a == "/fk/param"        && m.size() >= 2) applyParam (m[0].getString(), m[1].getFloat32());
        else if (a == "/fk/audio"        && m.size() >= 3) requestAudio (m[0].getString(), m[1].getInt32(), m[2].getInt32());
        else if (a == "/fk/inputs")                        requestInputs (m);
        else if (a == "/fk/outputs")                       requestOutputs (m);
        else if (a == "/fk/bypass"       && m.size() >= 1) engine.setBypass (m[0].getInt32() != 0);
        else if (a == "/fk/analysis"     && m.size() >= 1) engine.setAnalysis (m[0].getInt32() != 0);
        else if (a == "/fk/testtone"     && m.size() >= 1) engine.setTestTone (m[0].isFloat32() ? m[0].getFloat32() : (float) m[0].getInt32());
        else if (a == "/fk/subscribe"    && m.size() >= 1) subscribeMask.store (m[0].getInt32());
        else if (a == "/fk/listdevices")                   devicesDirty.store (true);
        else if (a == "/fk/ping")                          sendStatus();
        // Close the device and exit. Windows has no SIGTERM to send a child
        // process, and a hard kill skips closing the audio device - which some
        // ASIO drivers do not recover from until the interface is replugged.
        else if (a == "/fk/quit")                          gRun.store (false);
    }

    void applyParam (const juce::String& name, float v)
    {
        if      (name == "maxCutDb")       engine.setMaxCutDb (v);
        else if (name == "initialCut")     engine.setInitialCutDb (v);
        else if (name == "notchQ")         engine.setNotchQ (v);
        else if (name == "releaseSeconds") engine.setReleaseSeconds (v);
        else if (name == "prominenceDb")   engine.setProminenceDb (v);
        else if (name == "persistFrames")  engine.setPersistFrames ((int) v);
        else if (name == "floorDb")        engine.setFloorDb (v);
        else if (name == "minFreq")        engine.setMinFreq (v);
        else if (name == "maxFreq")        engine.setMaxFreq (v);
        else if (name == "stabilityHz")    engine.setStabilityHz (v);
        else if (name == "growthDb")       engine.setGrowthDb (v);
        else if (name == "inputGate")      engine.setInputGate (v);
    }

    // Device/channel changes are marshalled to the telemetry thread;
    // AudioDeviceManager is not safe to reconfigure from the OSC reader thread.
    void requestAudio (const juce::String& device, int sampleRate, int bufferSize)
    {
        const juce::ScopedLock sl (reconfigLock);
        _desired.device = device;
        _desired.sampleRate = sampleRate;
        _desired.bufferSize = bufferSize;
        _dirty = true;
    }

    // The checked physical input channel indices (0-based), any count up to the
    // engine's max. Sorted ascending so a slot maps to the i-th enabled input.
    void requestInputs (const juce::OSCMessage& m)
    {
        std::vector<int> ins;
        for (int i = 0; i < m.size() && (int) ins.size() < AudioEngine::maxChans; ++i)
            if (m[i].isInt32()) ins.push_back (m[i].getInt32());
        std::sort (ins.begin(), ins.end());
        ins.erase (std::unique (ins.begin(), ins.end()), ins.end());

        const juce::ScopedLock sl (reconfigLock);
        _desired.inputs = std::move (ins);
        _dirty = true;
    }

    // Per-slot return outputs, parallel to the SORTED input list: the i-th value
    // is the physical output channel slot i writes to. Empty = mirror the inputs
    // (the old behaviour). This is what lets the engine sit as an insert - e.g.
    // read a mic on ANALOG 5 and return it to the console on ADAT 3.
    void requestOutputs (const juce::OSCMessage& m)
    {
        std::vector<int> outs;
        for (int i = 0; i < m.size() && (int) outs.size() < AudioEngine::maxChans; ++i)
            if (m[i].isInt32()) outs.push_back (m[i].getInt32());

        const juce::ScopedLock sl (reconfigLock);
        _desired.outputs = std::move (outs);
        _dirty = true;
    }

    // ------------------------------------------------------------------ telemetry
    void telemetryLoop()
    {
        int tick = 0;
        while (keepRunning.load())
        {
            applyPendingReconfigure();
            if (devicesDirty.exchange (false)) { sendDevices(); sendChannels(); }

            const int mask = subscribeMask.load();

            AudioEngine::RejectOut rj;
            while (engine.popReject (rj))
                if (mask & 1)
                    sender.send (juce::OSCMessage ("/fk/reject", rj.ch, rj.hz, rj.levelDb,
                                                   rj.reason, rj.frames));

            AudioEngine::EventOut e;
            while (engine.popEvent (e))
                sender.send (juce::OSCMessage ("/fk/event", e.ch, e.hz, e.levelDb,
                                               e.ageMs, e.widthLoHz, e.widthHiHz, e.path, e.refused));

            if ((mask & 0x2) && tick % 2 == 0)  sendNotches();    // ~10 Hz
            if  (mask & 0x4)                    sendSpectrum();    // ~20 Hz
            if ((mask & 0x8) && tick % 10 == 0) sendStatus();     // ~2 Hz

            ++tick;
            juce::Thread::sleep (50);   // ~20 Hz base cadence
        }
    }

    // JUCE opens a named device inside the *current* device type only. macOS has
    // one type, Core Audio, so that never mattered; Windows creates WASAPI first
    // and ASIO last, so "Focusrite USB ASIO" would be sought as a WASAPI device
    // and fail. Switch to whichever type lists the name. Returns true if it did.
    bool selectTypeFor (const juce::String& deviceName)
    {
        if (deviceName.isEmpty()) return false;

        for (auto* type : devices.getAvailableDeviceTypes())
        {
            type->scanForDevices();
            if (! type->getDeviceNames (true).contains (deviceName)
                 && ! type->getDeviceNames (false).contains (deviceName))
                continue;

            if (devices.getCurrentAudioDeviceType() == type->getTypeName())
                return false;

            devices.setCurrentAudioDeviceType (type->getTypeName(), true);
            return true;
        }
        return false;
    }

    void applyPendingReconfigure()
    {
        Desired d;
        {
            const juce::ScopedLock sl (reconfigLock);
            if (! _dirty) return;
            d = _desired; _dirty = false;
        }

        devices.removeAudioCallback (&engine);
        selectTypeFor (d.device);

        juce::AudioDeviceManager::AudioDeviceSetup setup;
        setup.outputDeviceName = d.device;
        setup.inputDeviceName  = d.device;
        setup.sampleRate       = (double) d.sampleRate;
        setup.bufferSize       = d.bufferSize;

        if (d.inputs.empty())
        {
            // Nothing checked: keep the device open on defaults but idle.
            setup.useDefaultInputChannels  = true;
            setup.useDefaultOutputChannels = true;
        }
        // Slot i reads the i-th checked input (ascending) and writes its chosen
        // return output - or, with no explicit returns, mirrors its input index.
        std::vector<int> returns;
        for (size_t i = 0; i < d.inputs.size(); ++i)
            returns.push_back (i < d.outputs.size() && d.outputs[i] >= 0 ? d.outputs[i]
                                                                         : d.inputs[i]);

        if (d.inputs.empty())
        {
            // Nothing checked: keep the device open on defaults but idle.
            setup.useDefaultInputChannels  = true;
            setup.useDefaultOutputChannels = true;
        }
        else
        {
            setup.useDefaultInputChannels  = false;
            setup.useDefaultOutputChannels = false;
            setup.inputChannels.clear();
            setup.outputChannels.clear();
            for (int idx : d.inputs)  setup.inputChannels.setBit (idx);
            for (int idx : returns)   setup.outputChannels.setBit (idx);
        }

        devices.setAudioDeviceSetup (setup, true);

        // JUCE hands the callback one dense pointer per enabled output channel,
        // ascending - so each slot's return becomes a rank into that dense list.
        // Two slots sharing a return simply write the same channel (last wins).
        {
            std::vector<int> sortedOuts (returns);
            std::sort (sortedOuts.begin(), sortedOuts.end());
            sortedOuts.erase (std::unique (sortedOuts.begin(), sortedOuts.end()), sortedOuts.end());

            std::array<int, AudioEngine::maxChans> ranks {};
            for (size_t i = 0; i < returns.size() && i < ranks.size(); ++i)
                ranks[i] = (int) (std::lower_bound (sortedOuts.begin(), sortedOuts.end(), returns[i])
                                  - sortedOuts.begin());
            engine.setOutputMap (ranks.data(), (int) returns.size());
        }

        devices.addAudioCallback (&engine);
        engine.setActiveChannels ((int) d.inputs.size());

        auto* cur = devices.getCurrentAudioDevice();
        sendAudioState (cur ? cur->getName() : d.device,
                        cur ? (int) cur->getCurrentSampleRate() : d.sampleRate,
                        cur ? cur->getCurrentBufferSizeSamples() : d.bufferSize);
        sendChannels();
    }

    // Report every input and output channel name of the current device, so the
    // pickers (mic in, return out) can show real names like "ADAT 3".
    void sendChannels()
    {
        auto* cur = devices.getCurrentAudioDevice();
        if (cur == nullptr) return;
        const auto ins = cur->getInputChannelNames();
        for (int i = 0; i < ins.size(); ++i)
            sender.send (juce::OSCMessage ("/fk/channel", i, ins[i]));
        const auto outs = cur->getOutputChannelNames();
        for (int i = 0; i < outs.size(); ++i)
            sender.send (juce::OSCMessage ("/fk/outchannel", i, outs[i]));
    }

    void sendNotches()
    {
        for (int ch = 0; ch < engine.activeChannels(); ++ch)
        {
            std::array<NotchSlot, kMaxNotches> slots;
            engine.snapshotNotches (ch, slots);

            juce::MemoryBlock blob;
            for (const auto& s : slots)
            {
                const juce::uint8 flags[4] = { (juce::uint8) s.active, (juce::uint8) s.locked, (juce::uint8) s.manual, 0 };
                blob.append (flags, 4);
                const float f[3] = { (float) s.freq, (float) s.currentDb, (float) s.targetDb };
                blob.append (f, sizeof (f));
            }
            juce::OSCMessage msg ("/fk/notches"); msg.addInt32 (ch); msg.addBlob (blob);
            sender.send (msg);
        }
    }

    void sendSpectrum()
    {
        constexpr int outBins = 256;
        for (int ch = 0; ch < engine.activeChannels(); ++ch)
        {
            auto& det = engine.detector (ch);
            const float hzPerOut = det.getBinHz() * (float) FeedbackDetector::numBins / (float) outBins;

            juce::MemoryBlock blob;
            const juce::uint16 count = (juce::uint16) outBins;
            blob.append (&count, sizeof (count));
            blob.append (&hzPerOut, sizeof (hzPerOut));

            const int group = FeedbackDetector::numBins / outBins;   // max-pool to display res
            for (int o = 0; o < outBins; ++o)
            {
                float peak = -120.0f;
                for (int k = 0; k < group; ++k) peak = juce::jmax (peak, det.getBinDb (o * group + k));
                blob.append (&peak, sizeof (peak));
            }
            juce::OSCMessage msg ("/fk/spectrum"); msg.addInt32 (ch); msg.addBlob (blob);
            sender.send (msg);
        }
    }

    // Enumerate input-capable devices and report each, plus the current one.
    void sendDevices()
    {
        juce::StringArray seen;
        for (auto* type : devices.getAvailableDeviceTypes())
        {
            type->scanForDevices();
            for (const auto& name : type->getDeviceNames (true))   // true = inputs
            {
                if (! seen.contains (name))
                {
                    seen.add (name);
                    sender.send (juce::OSCMessage ("/fk/device", name));
                }
            }
        }

        auto* current = devices.getCurrentAudioDevice();
        const int sr = current ? (int) current->getCurrentSampleRate() : 0;
        const int bs = current ? current->getCurrentBufferSizeSamples() : 0;
        sendAudioState (current ? current->getName() : juce::String(), sr, bs);
    }

    void sendStatus() { sender.send (juce::OSCMessage ("/fk/status", (int) (engine.running() ? 1 : 0), engine.cpuLoad())); }
    void sendAudioState (const juce::String& d, int sr, int bs)
    {
        juce::OSCMessage msg ("/fk/audio/state"); msg.addString (d); msg.addInt32 (sr); msg.addInt32 (bs);
        msg.addInt32 (engine.running() ? 1 : 0); sender.send (msg);
    }

    struct Desired
    {
        juce::String device;
        int sampleRate = 48000;
        int bufferSize = 64;
        std::vector<int> inputs;    // checked physical inputs, sorted
        std::vector<int> outputs;   // per-slot returns, parallel to inputs; empty = mirror
    };

    juce::AudioDeviceManager devices;
    AudioEngine              engine;
    juce::OSCReceiver        receiver;
    juce::OSCSender          sender;
    std::thread              telemetry;
    std::atomic<bool>        keepRunning { false };
    std::atomic<int>         subscribeMask { 0xF };
    std::atomic<bool>        devicesDirty { true };   // send the device list once at start
    juce::CriticalSection    reconfigLock;
    Desired                  _desired;
    bool                     _dirty = false;
};

} // namespace fk

int main (int argc, char* argv[])
{
    juce::ScopedJuceInitialiser_GUI juceInit;   // creates the MessageManager AudioDeviceManager wants

    std::signal (SIGINT,  [] (int) { gRun.store (false); });
    std::signal (SIGTERM, [] (int) { gRun.store (false); });

    // Don't outlive the app. Windows does not kill child processes when their
    // parent dies, and this loop otherwise runs forever - so a crashed or
    // force-killed app used to leave an orphaned engine holding the audio
    // device, which then blocked the next engine from opening it. When launched
    // by the supervisor (FK_PARENT_WATCH set), the app keeps our stdin pipe open
    // for its whole life; the pipe breaking (EOF) means the app is gone, so we
    // stop. Standalone runs don't set the flag and are unaffected.
    if (juce::SystemStats::getEnvironmentVariable ("FK_PARENT_WATCH", {}).isNotEmpty())
    {
        std::thread ([]
        {
            while (std::getchar() != EOF) { /* parent alive; it sends nothing */ }
            gRun.store (false);
        }).detach();
    }

    const juce::String device = argc > 1 ? juce::String (argv[1]) : juce::String();  // "" = default
    const int          rate   = argc > 2 ? juce::String (argv[2]).getIntValue() : 48000;
    const int          buffer = argc > 3 ? juce::String (argv[3]).getIntValue() : 64;

    // Heap, not stack. Engine holds eight FeedbackDetectors by value, each with
    // multi-kilobyte FFT ring/scratch/mag arrays, plus the notch banks and event
    // queues - together far more than the 1 MB default main-thread stack on
    // Windows, so a stack local overflows on construction before main() runs a
    // line (macOS's 8 MB stack hid this). make_unique puts it on the heap.
    auto engine = std::make_unique<fk::Engine>();
    if (! engine->start (device, rate, buffer))
        return 1;

    juce::Logger::writeToLog ("fk-engine up: OSC in " + juce::String (fk::kEngineListenPort)
                              + ", telemetry -> " + juce::String (fk::kAppTelemetryPort));

    while (gRun.load()) juce::Thread::sleep (200);

    engine->stop();
    juce::Logger::writeToLog ("fk-engine stopped");
    return 0;
}
