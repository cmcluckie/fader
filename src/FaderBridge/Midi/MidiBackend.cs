using Commons.Music.Midi;

namespace Fader.Bridge.Midi;

/// <summary>
/// Picks a MIDI backend explicitly rather than trusting MidiAccessManager.Default,
/// which can resolve to the RtMidi backend on macOS and then fail on a missing
/// (or wrong-architecture) librtmidi.
///
/// managed-midi's CoreMIDI backend P/Invokes the system framework and ships no
/// native binaries, so it runs on Apple Silicon and Intel alike. RtMidi.Core was
/// rejected for this reason: it bundles an x86_64-only librtmidi.dylib.
/// </summary>
public static class MidiBackend
{
    public static IMidiAccess Create(out string name)
    {
        if (OperatingSystem.IsMacOS())
        {
            try
            {
                var access = new Commons.Music.Midi.CoreMidiApi.CoreMidiAccess();
                name = "CoreMIDI (native, no external dependency)";
                return access;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"CoreMIDI unavailable ({ex.GetType().Name}), trying RtMidi...");
                name = "RtMidi (needs `brew install rtmidi`)";
                return new Commons.Music.Midi.RtMidi.RtMidiAccess();
            }
        }

        // Windows/Linux: lets the diagnostics be exercised away from the Mac.
        name = $"MidiAccessManager.Default ({MidiAccessManager.Default.GetType().Name})";
        return MidiAccessManager.Default;
    }

    public static string Troubleshooting => """

        Troubleshooting:
          - Is the FaderPort powered on, connected by USB, and in MCU mode?
            (Hold NEXT while powering on to reach the mode menu; pick "MCU".)
          - Check it appears in Audio MIDI Setup > Window > Show MIDI Studio.
          - Do not run this over SSH. CoreMIDI will not enumerate devices without
            a logged-in GUI session - use Terminal on the Mac itself.
          - If CoreMIDI failed and RtMidi is being used: `brew install rtmidi`.
        """;
}
