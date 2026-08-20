// fk-engine — headless feedback-suppression engine.
//
// UNVERIFIED-COMPILE in this sandbox: no cmake / full Xcode here, so this has
// not been built. It is written to compile against JUCE 7/8 and is the "prove
// the plumbing" target: audio passthrough + OSC control/telemetry, detection
// defaulting to ASSIST (analyse + report, never cut) per §8.
//
// Build: see engine/CMakeLists.txt.

#include <juce_audio_devices/juce_audio_devices.h>
#include <juce_osc/juce_osc.h>
#include "AudioEngine.h"

namespace fk
{
constexpr int   kEngineListenPort = 10024;              // app -> engine
constexpr int   kAppTelemetryPort = 10025;              // engine -> app (loopback, fixed)
constexpr auto  kLoopback         = "127.0.0.1";

/** Owns the device, the engine, and the two OSC endpoints; lives on the message thread. */
class Engine : private juce::OSCReceiver::Listener<juce::OSCReceiver::MessageLoopCallback>,
               private juce::Timer
{
public:
    bool start (const juce::String& preferredDevice, int sampleRate, int bufferSize)
    {
        // ---- audio -----------------------------------------------------------
        juce::AudioDeviceManager::AudioDeviceSetup setup;
        setup.outputDeviceName = preferredDevice;
        setup.inputDeviceName  = preferredDevice;
        setup.sampleRate       = sampleRate  > 0 ? (double) sampleRate : 48000.0;
        setup.bufferSize       = bufferSize  > 0 ? bufferSize          : 64;
        setup.useDefaultInputChannels  = true;   // ADAT pair chosen in a later /fk/audio revision
        setup.useDefaultOutputChannels = true;

        const auto err = devices.initialise (numChans, numChans, nullptr, true, preferredDevice, &setup);
        if (err.isNotEmpty())
        {
            juce::Logger::writeToLog ("audio init failed: " + err);
            // Non-fatal: stay up so the app can pick a device via /fk/audio and see /fk/status.
        }
        devices.addAudioCallback (&engine);

        // ---- osc -------------------------------------------------------------
        if (! receiver.connect (kEngineListenPort))
        {
            juce::Logger::writeToLog ("could not bind OSC " + juce::String (kEngineListenPort));
            return false;
        }
        receiver.addListener (this);
        sender.connect (kLoopback, kAppTelemetryPort);

        startTimerHz (20);   // telemetry cadence; spectrum + status derive from it
        return true;
    }

    void stop()
    {
        stopTimer();
        receiver.removeListener (this);
        receiver.disconnect();
        devices.removeAudioCallback (&engine);
        devices.closeAudioDevice();
    }

private:
    // ------------------------------------------------------------------ control
    void oscMessageReceived (const juce::OSCMessage& m) override
    {
        const auto a = m.getAddressPattern().toString();

        if (a == "/fk/mode"        && m.size() >= 1) engine.setMode ((AudioEngine::Mode) m[0].getInt32());
        else if (a == "/fk/notch/place"  && m.size() >= 3) engine.placeNotch  (m[0].getInt32(), m[1].getFloat32(), m[2].getFloat32());
        else if (a == "/fk/notch/remove" && m.size() >= 2) engine.removeNotch (m[0].getInt32(), m[1].getInt32());
        else if (a == "/fk/notch/lock"   && m.size() >= 3) engine.lockNotch   (m[0].getInt32(), m[1].getInt32(), m[2].getInt32() != 0);
        else if (a == "/fk/clear"        && m.size() >= 2) engine.clearNotches (m[0].getInt32(), m[1].getInt32() != 0);
        else if (a == "/fk/lockall"      && m.size() >= 1) engine.lockAll     (m[0].getInt32());
        else if (a == "/fk/param"        && m.size() >= 2) applyParam (m[0].getString(), m[1].getFloat32());
        else if (a == "/fk/audio"        && m.size() >= 3) reconfigure (m[0].getString(), m[1].getInt32(), m[2].getInt32());
        else if (a == "/fk/subscribe"    && m.size() >= 1) subscribeMask = m[0].getInt32();
        else if (a == "/fk/ping")                          sendStatus();
    }

    void applyParam (const juce::String& name, float v)
    {
        if      (name == "maxCutDb")       engine.setMaxCutDb (v);
        else if (name == "notchQ")         engine.setNotchQ (v);
        else if (name == "releaseSeconds") engine.setReleaseSeconds (v);
        else if (name == "prominenceDb")   engine.setProminenceDb (v);
        else if (name == "persistFrames")  engine.setPersistFrames ((int) v);
        else if (name == "pitchTolerance") engine.setPitchTolerance (v);
        else if (name == "harmonicDb")     engine.setHarmonicDb (v);
        else if (name == "floorDb")        engine.setFloorDb (v);
    }

    void reconfigure (const juce::String& device, int sampleRate, int bufferSize)
    {
        devices.removeAudioCallback (&engine);
        juce::AudioDeviceManager::AudioDeviceSetup setup;
        setup.outputDeviceName = device; setup.inputDeviceName = device;
        setup.sampleRate = (double) sampleRate; setup.bufferSize = bufferSize;
        setup.useDefaultInputChannels = true; setup.useDefaultOutputChannels = true;
        devices.setAudioDeviceSetup (setup, true);
        devices.addAudioCallback (&engine);
        sendAudioState (device, sampleRate, bufferSize);
    }

    // ------------------------------------------------------------------ telemetry
    void timerCallback() override
    {
        AudioEngine::EventOut e;
        while (engine.popEvent (e))                          // detection events, always
            sender.send (juce::OSCMessage ("/fk/event", e.ch, e.hz, e.levelDb));

        if (subscribeMask & 0x2) if (++notchDiv % 2 == 0) sendNotches();   // ~10 Hz
        if (subscribeMask & 0x4) sendSpectrum();                           // ~20 Hz
        if (subscribeMask & 0x8) if (++statusDiv % 10 == 0) sendStatus();  // ~2 Hz
    }

    void sendNotches()
    {
        for (int ch = 0; ch < numChans; ++ch)
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
        for (int ch = 0; ch < numChans; ++ch)
        {
            auto& det = engine.detector (ch);
            const float hzPerOut = det.getBinHz() * (float) FeedbackDetector::numBins / (float) outBins;

            juce::MemoryBlock blob;
            const juce::uint16 count = (juce::uint16) outBins;
            blob.append (&count, sizeof (count));
            blob.append (&hzPerOut, sizeof (hzPerOut));

            const int group = FeedbackDetector::numBins / outBins;   // max-pool down to display res
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

    void sendStatus()      { sender.send (juce::OSCMessage ("/fk/status", (int) (engine.running() ? 1 : 0), engine.cpuLoad())); }
    void sendAudioState (const juce::String& d, int sr, int bs)
    {
        juce::OSCMessage msg ("/fk/audio/state"); msg.addString (d); msg.addInt32 (sr); msg.addInt32 (bs);
        msg.addInt32 (engine.running() ? 1 : 0); sender.send (msg);
    }

    juce::AudioDeviceManager devices;
    AudioEngine              engine;
    juce::OSCReceiver        receiver;
    juce::OSCSender          sender;
    int subscribeMask = 0xF;
    int notchDiv = 0, statusDiv = 0;
};

} // namespace fk

int main (int argc, char* argv[])
{
    juce::ScopedJuceInitialiser_GUI juceInit;   // MessageManager for audio + osc + timers

    juce::String device = argc > 1 ? juce::String (argv[1]) : juce::String();  // "" = default
    const int    rate   = argc > 2 ? juce::String (argv[2]).getIntValue() : 48000;
    const int    buffer = argc > 3 ? juce::String (argv[3]).getIntValue() : 64;

    auto engine = std::make_unique<fk::Engine>();
    if (! engine->start (device, rate, buffer))
        return 1;

    juce::Logger::writeToLog ("fk-engine up: listening OSC " + juce::String (fk::kEngineListenPort)
                              + ", telemetry -> " + juce::String (fk::kAppTelemetryPort));
    juce::MessageManager::getInstance()->runDispatchLoop();   // until the supervisor kills us

    engine->stop();
    return 0;
}
