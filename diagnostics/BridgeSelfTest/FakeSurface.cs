using Fader.Bridge.Midi;

namespace Fader.Diagnostics.BridgeSelfTest;

/// <summary>A FaderPort stand-in that records what the bridge sends to it.</summary>
public sealed class FakeSurface : IControlSurface
{
    private readonly object _lock = new();

    public event Action<int, int>? FaderMoved;
    public event Action<int, bool>? FaderTouched;
    public event Action<int, bool>? ButtonChanged;

    public Dictionary<int, int> MotorPositions { get; } = new();
    public Dictionary<int, bool> Leds { get; } = new();
    public Dictionary<(int Strip, int Row), string> Scribbles { get; } = new();
    public List<int> MotorWriteLog { get; } = new();

    public void SetFaderPosition(int strip, int value14)
    {
        lock (_lock)
        {
            MotorPositions[strip] = value14;
            MotorWriteLog.Add(strip);
        }
    }

    public void SetLed(int note, bool on)
    {
        lock (_lock) { Leds[note] = on; }
    }

    public void SetScribble(int strip, int row, string text)
    {
        lock (_lock) { Scribbles[(strip, row)] = text; }
    }

    public Dictionary<int, string> ScribbleLines { get; } = new();

    public void SetScribbleLine(int row, string text)
    {
        lock (_lock) { ScribbleLines[row] = text; }
    }

    // --- inbound: pretend the user did something physical -------------------

    public void MoveFader(int strip, int value14) => FaderMoved?.Invoke(strip, value14);

    public void TouchFader(int strip, bool touched) => FaderTouched?.Invoke(strip, touched);

    public void PressButton(int note)
    {
        ButtonChanged?.Invoke(note, true);
        ButtonChanged?.Invoke(note, false);
    }

    public int MotorWritesFor(int strip)
    {
        lock (_lock) { return MotorWriteLog.Count(s => s == strip); }
    }

    public void ClearLog()
    {
        lock (_lock) { MotorWriteLog.Clear(); }
    }
}
