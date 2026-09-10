using Commons.Music.Midi;

namespace Fader.Bridge.Midi;

/// <summary>
/// Opens the FaderPort's MIDI input and output and presents the surface as
/// semantic events rather than raw bytes.
/// </summary>
public sealed class FaderPortDevice : IControlSurface, IAsyncDisposable
{
    private readonly MidiStreamParser _parser = new();
    private readonly object _outputLock = new();
    private IMidiInput? _input;
    private IMidiOutput? _output;

    public event Action<int, int>? FaderMoved;        // strip, 14-bit value
    public event Action<int, bool>? FaderTouched;     // strip, touched
    public event Action<int, bool>? ButtonChanged;    // note, pressed
    public event Action<string>? Log;

    public string InputName { get; private set; } = "(none)";
    public string OutputName { get; private set; } = "(none)";

    public async Task OpenAsync(string portNameFilter, IMidiAccess? access = null)
    {
        // The caller may pass a shared access (e.g. a supervisor that also polls
        // for the device's presence) so we do not create a second MIDI client.
        if (access is null)
        {
            access = MidiBackend.Create(out var backend);
            Log?.Invoke($"MIDI backend: {backend}");
        }

        var inputPort = Match(access.Inputs.ToList(), portNameFilter, "input");
        var outputPort = Match(access.Outputs.ToList(), portNameFilter, "output");

        _input = await access.OpenInputAsync(inputPort.Id);
        _output = await access.OpenOutputAsync(outputPort.Id);
        InputName = inputPort.Name;
        OutputName = outputPort.Name;

        _parser.PitchBend += (channel, value) => FaderMoved?.Invoke(channel, value);

        _parser.NoteOn += (_, note, _) =>
        {
            if (McuProtocol.IsFaderTouch(note))
            {
                FaderTouched?.Invoke(note - McuProtocol.TouchBase, true);
            }
            else
            {
                ButtonChanged?.Invoke(note, true);
            }
        };

        _parser.NoteOff += (_, note) =>
        {
            if (McuProtocol.IsFaderTouch(note))
            {
                FaderTouched?.Invoke(note - McuProtocol.TouchBase, false);
            }
            else
            {
                ButtonChanged?.Invoke(note, false);
            }
        };

        _input.MessageReceived += (_, e) => _parser.Feed(e.Data, e.Start, e.Length);
    }

    private static IMidiPortDetails Match(
        List<IMidiPortDetails> ports, string filter, string direction)
    {
        if (ports.Count == 0)
        {
            throw new InvalidOperationException($"No MIDI {direction} ports found.");
        }

        // Exact match wins outright. Windows enumerates a FaderPort as both
        // "PreSonus FP8" and "MIDIIN2 (PreSonus FP8)", so the first name is a
        // substring of the second and pure substring matching is ambiguous for
        // the very name the user is most likely to type.
        var exact = ports
            .Where(p => string.Equals(p.Name, filter, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (exact.Count == 1)
        {
            return exact[0];
        }

        var matches = ports
            .Where(p => p.Name.Contains(filter, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (matches.Count == 1)
        {
            return matches[0];
        }

        var available = string.Join("\n  ", ports.Select(p => $"\"{p.Name}\""));
        throw new InvalidOperationException(matches.Count == 0
            ? $"No MIDI {direction} port matched \"{filter}\". Available:\n  {available}"
            : $"\"{filter}\" matched {matches.Count} {direction} ports - use one of these names exactly:\n  {available}");
    }

    private void Send(byte[] data)
    {
        // MIDI callbacks and the OSC receive loop both reach this; serialise so
        // a motor update can't interleave with a SysEx scribble write.
        lock (_outputLock)
        {
            try
            {
                _output?.Send(data, 0, data.Length, 0);
            }
            catch (Exception ex)
            {
                Log?.Invoke($"MIDI send failed: {ex.Message}");
            }
        }
    }

    public void SetFaderPosition(int strip, int value14) =>
        Send(McuProtocol.FaderPosition(strip, value14));

    public void SetLed(int note, bool on) => Send(McuProtocol.Led(note, on));

    public void SetScribble(int strip, int row, string text) =>
        Send(McuProtocol.ScribbleText(strip, row, text));

    public void SetScribbleLine(int row, string text) =>
        Send(McuProtocol.ScribbleLine(row, text));

    /// <summary>Darken every LED and blank both scribble rows, for a clean exit.</summary>
    public void Reset(int stripCount)
    {
        foreach (var b in new[]
                 {
                     McuProtocol.RecBase, McuProtocol.SoloBase,
                     McuProtocol.MuteBase, McuProtocol.SelectBase,
                 })
        {
            for (var strip = 0; strip < stripCount; strip++)
            {
                SetLed(b + strip, false);
            }
        }

        for (var strip = 0; strip < stripCount; strip++)
        {
            SetScribble(strip, 0, string.Empty);
            SetScribble(strip, 1, string.Empty);
            SetFaderPosition(strip, 0);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_input is not null)
        {
            try { await _input.CloseAsync(); } catch { /* shutting down */ }
        }

        if (_output is not null)
        {
            try { await _output.CloseAsync(); } catch { /* shutting down */ }
        }
    }
}
