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
            Console.WriteLine($"  [{i}] \"{inputs[i].Name}\"   (id: {inputs[i].Id})");
        }

        // The bridge needs an output too (motors, LEDs, scribble strips), so
        // show both lists - copy the right name into config.json's midiPortName.
        var outputs = access.Outputs.ToList();
        Console.WriteLine($"\nAvailable output ports ({outputs.Count}):");
        foreach (var o in outputs)
        {
            Console.WriteLine($"      \"{o.Name}\"   (id: {o.Id})");
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

        if (args.Contains("--test-output"))
        {
            await TestOutputAsync(access, chosen.Name);
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

    /// <summary>
    /// Drive the surface briefly to prove the outbound path: scribble strips
    /// (MCU SysEx), button LEDs, and the motors. Everything it changes is put
    /// back before it returns. Nothing here touches audio - the FaderPort is
    /// the only device written to.
    /// </summary>
    private static async Task TestOutputAsync(IMidiAccess access, string portName)
    {
        Console.WriteLine("\n--- output test ---");

        IMidiOutput output;
        try
        {
            var port = access.Outputs.FirstOrDefault(p =>
                           string.Equals(p.Name, portName, StringComparison.OrdinalIgnoreCase))
                       ?? access.Outputs.First(p =>
                           p.Name.Contains(portName, StringComparison.OrdinalIgnoreCase));

            output = await access.OpenOutputAsync(port.Id);
            Console.WriteLine($"Output port: \"{port.Name}\"");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Could not open an output port: {ex.Message}");
            return;
        }

        void Send(byte[] data) => output.Send(data, 0, data.Length, 0);

        Console.WriteLine("Writing scribble strips (expect \"FADER 1\".. across the displays)...");
        for (var strip = 0; strip < 8; strip++)
        {
            Send(McuProtocol.ScribbleText(strip, 0, $"FADER {strip + 1}"));
            Send(McuProtocol.ScribbleText(strip, 1, "MCU OK"));
        }
        await Task.Delay(800);

        Console.WriteLine("Lighting Select LEDs left to right...");
        for (var strip = 0; strip < 8; strip++)
        {
            Send(McuProtocol.Led(McuProtocol.SelectBase + strip, true));
            await Task.Delay(90);
        }
        for (var strip = 0; strip < 8; strip++)
        {
            Send(McuProtocol.Led(McuProtocol.SelectBase + strip, false));
        }

        Console.WriteLine("Sweeping fader 1 (it should physically move, then return)...");
        foreach (var value in new[] { 4096, 8192, 12287, 8192, 0 })
        {
            Send(McuProtocol.FaderPosition(0, value));
            await Task.Delay(320);
        }

        Console.WriteLine("Clearing...");
        for (var strip = 0; strip < 8; strip++)
        {
            Send(McuProtocol.ScribbleText(strip, 0, string.Empty));
            Send(McuProtocol.ScribbleText(strip, 1, string.Empty));
        }
        await Task.Delay(200);
        await output.CloseAsync();

        Console.WriteLine("""
            --- end of output test ---

            Did the displays show text? Did fader 1 move? Did the LEDs chase?
            Anything that did NOT happen tells us what the FaderPort rejects.
            """);
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
            // Exact match wins: "PreSonus FP8" is a substring of
            // "MIDIIN2 (PreSonus FP8)", so substring alone is ambiguous.
            var exact = inputs
                .Where(p => string.Equals(p.Name, filter, StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (exact.Count == 1)
            {
                return exact[0];
            }

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
