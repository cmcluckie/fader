namespace Fader.Bridge.Midi;

/// <summary>
/// The control-surface operations the bridge depends on. Kept as an interface so
/// the bridge logic - touch gating, mute inversion, bank windowing - can be
/// exercised without a FaderPort plugged in.
/// </summary>
public interface IControlSurface
{
    /// <summary>Strip index, 14-bit fader value.</summary>
    event Action<int, int>? FaderMoved;

    /// <summary>Strip index, whether the fader is currently under a finger.</summary>
    event Action<int, bool>? FaderTouched;

    /// <summary>MCU note number, whether pressed.</summary>
    event Action<int, bool>? ButtonChanged;

    void SetFaderPosition(int strip, int value14);

    void SetLed(int note, bool on);

    void SetScribble(int strip, int row, string text);

    /// <summary>Write a whole row (all strips) at once - for a scrolling marquee.</summary>
    void SetScribbleLine(int row, string text);
}
