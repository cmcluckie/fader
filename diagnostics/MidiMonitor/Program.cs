using Commons.Music.Midi;
using Fader.Bridge;
using Fader.Bridge.Midi;

namespace Fader.Diagnostics.MidiMonitor;

/// <summary>
/// Diagnostic #1: open the FaderPort MIDI input and print every incoming
/// message, decoded. Confirms the port name, the MCU note map, and the 14-bit
/// fader resolution against the real hardware.
///
/// Uses the bridge's own MidiStreamParser and McuProtocol, so a clean run here
/// means the bridge's decoding is correct too.
///
///   dotnet run --project diagnostics/MidiMonitor
///   dotnet run --project diagnostics/MidiMonitor -- FaderPort
/// </summary>
internal static class Program
{
    private static long _count;

    private static async Task<int> Main(string[] args)
    {
        Console.WriteLine("FaderPort 8 - MIDI input monitor");
        Console.WriteLine("================================\n");

        IMidiAccess access;
        try
        {
            access = MidiBackend.Create(out var backendName);
            Console.WriteLine($"MIDI backend : {backendName}");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Could not initialise a MIDI backend: {ex.Message}");
            Console.Error.WriteLine(MidiBackend.Troubleshooting);
            return 1;
        }

        var inputs = access.Inputs.ToList();
        if (inputs.Count == 0)
        {
            Console.Error.WriteLine("\nNo MIDI input ports found.");
            Console.Error.WriteLine(MidiBackend.Troubleshooting);
            return 1;
        }

        Console.WriteLine($"\nAvailable input ports ({inputs.Count}):");
        for (var i = 0; i < inputs.Count; i++)
        {
            Console.WriteLine($"  [{i}] {inputs[i].Name}   (id: {inputs[i].Id})");
        }

        var chosen = SelectPort(inputs, args.FirstOrDefault());
        if (chosen is null)
        {
            return 1;
        }

        Console.WriteLine($"\nOpening: {chosen.Name}");

        IMidiInput input;
        try
        {
            input = await access.OpenInputAsync(chosen.Id);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Failed to open port: {ex.Message}");
            return 1;
        }

        var parser = new MidiStreamParser();

        parser.PitchBend += (strip, value) => Print(
            "FADER",
            $"strip {strip + 1}  value {value,5} / 16383  ({value / 16383.0,6:P1})  " +
            $"-> X32 float ~{FaderScaling.McuToX32(value):F4}");

        parser.NoteOn += (_, note, velocity) => Print(
            McuProtocol.IsFaderTouch(note) ? "TOUCH DOWN " : "BUTTON DOWN",
            $"note {note,3}  {McuProtocol.Describe(note)}  (vel {velocity})");

        parser.NoteOff += (_, note) => Print(
            McuProtocol.IsFaderTouch(note) ? "TOUCH UP   " : "BUTTON UP  ",
            $"note {note,3}  {McuProtocol.Describe(note)}");

        parser.ControlChange += (channel, cc, value) =>
        {
            var detail = cc is >= 16 and <= 23
                ? $"V-Pot {cc - 15} rotate {((value & 0x40) != 0 ? "CCW" : "CW")} x{value & 0x3F}"
                : cc == 60
                    ? $"Jog wheel {((value & 0x40) != 0 ? "CCW" : "CW")} x{value & 0x3F}"
                    : $"CC {cc} = {value}";
            Print("CC", $"ch {channel}  {detail}");
        };

        parser.SysEx += data =>
        {
            var text = McuProtocol.TryDecodeScribble(data);
            Print("SYSEX", text is null
                ? $"{data.Length} bytes  [{Convert.ToHexString(data)}]"
                : $"scribble-strip write, text \"{text}\"");
        };

        parser.Other += (status, d1, d2) =>
            Print("OTHER", $"status 0x{status:X2}  d1 {d1}  d2 {d2}");

        input.MessageReceived += (_, e) => parser.Feed(e.Data, e.Start, e.Length);

        Console.WriteLine("""

            Listening. Try each of these and check the decode matches the label:
              - move every fader (watch the value hit 0 and 16383 at the extremes)
              - touch and release a fader (should print TOUCH DOWN/UP for that strip)
              - press Select / Mute / Solo / Arm on a few strips
              - press the transport and Bank buttons

            Anything printing "(unmapped)" is a button the standard MCU map does
            not cover - note the number.

            Ctrl+C to stop.
            """);

        var quit = new TaskCompletionSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; quit.TrySetResult(); };
        await quit.Task;

        await input.CloseAsync();
        Console.WriteLine($"\nClosed. Messages seen: {_count}");
        return 0;
    }

    private static void Print(string kind, string detail)
    {
        _count++;
        Console.WriteLine($"{DateTime.Now:HH:mm:ss.fff}  {kind,-11}  {detail}");
    }

    private static IMidiPortDetails? SelectPort(List<IMidiPortDetails> inputs, string? filter)
    {
        if (!string.IsNullOrWhiteSpace(filter))
        {
            var matches = inputs
                .Where(p => p.Name.Contains(filter, StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (matches.Count == 1)
            {
                return matches[0];
            }

            Console.Error.WriteLine(matches.Count == 0
                ? $"\nNo input port matched \"{filter}\"."
                : $"\n\"{filter}\" matched {matches.Count} ports - be more specific.");
            return null;
        }

        if (inputs.Count == 1)
        {
            return inputs[0];
        }

        Console.Write("\nPort number to open: ");
        if (int.TryParse(Console.ReadLine(), out var index) &&
            index >= 0 && index < inputs.Count)
        {
            return inputs[index];
        }

        Console.Error.WriteLine("Not a valid port number.");
        return null;
    }
}
