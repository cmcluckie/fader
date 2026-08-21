using Avalonia;
using Avalonia.Media;

namespace Fader.MenuBar;

/// <summary>
/// The design tokens for the Fader window.
///
/// Colour has jobs, not moods - this is the whole rule the UI is built on:
///   · <see cref="Accent"/> (teal)    - interactive, armed, selected. "Your hand did this."
///   · <see cref="Catch"/> (magenta)  - a feedback tone was caught. Never used for level,
///                                      so it can never be misread as one.
///   · <see cref="Safe"/>/<see cref="Hot"/>/<see cref="Clip"/> - metering ONLY, never chrome.
///
/// Everything is a static readonly brush: these are shared across every control and
/// repaint, so we allocate them once rather than per frame.
/// </summary>
internal static class Tokens
{
    // ---- grounds & structure ------------------------------------------------
    public static readonly Color GroundColor = Color.FromRgb(0x09, 0x0B, 0x10);
    public static readonly IBrush Ground = New(GroundColor);
    public static readonly IBrush Ground2 = New(0x0C, 0x0F, 0x16);
    public static readonly IBrush Panel = New(0x12, 0x16, 0x1F);
    public static readonly IBrush Panel2 = New(0x17, 0x1C, 0x27);
    public static readonly IBrush Panel3 = New(0x1D, 0x24, 0x31);
    public static readonly IBrush Line = New(0x24, 0x2C, 0x3A);
    public static readonly IBrush LineSoft = New(0x1B, 0x22, 0x2E);

    // ---- ink ----------------------------------------------------------------
    public static readonly IBrush Ink = New(0xEA, 0xEE, 0xF5);
    public static readonly IBrush InkDim = New(0x98, 0xA2, 0xB3);
    public static readonly IBrush InkFaint = New(0x5C, 0x66, 0x75);

    // ---- semantic -----------------------------------------------------------
    public static readonly Color AccentColor = Color.FromRgb(0x35, 0xC9, 0xD6);
    public static readonly IBrush Accent = New(AccentColor);
    public static readonly IBrush AccentSoft = New(Color.FromArgb(0x1E, 0x35, 0xC9, 0xD6));
    public static readonly IBrush AccentLine = New(Color.FromArgb(0x80, 0x35, 0xC9, 0xD6));

    public static readonly Color CatchColor = Color.FromRgb(0xF4, 0x5D, 0x9C);
    public static readonly IBrush Catch = New(CatchColor);
    public static readonly IBrush CatchSoft = New(Color.FromArgb(0x28, 0xF4, 0x5D, 0x9C));

    public static readonly Color SafeColor = Color.FromRgb(0x35, 0xD0, 0x7A);
    public static readonly Color HotColor = Color.FromRgb(0xF4, 0xB7, 0x40);
    public static readonly Color ClipColor = Color.FromRgb(0xFF, 0x51, 0x55);
    public static readonly IBrush Safe = New(SafeColor);
    public static readonly IBrush Clip = New(ClipColor);
    public static readonly IBrush ClipSoft = New(Color.FromArgb(0x18, 0xFF, 0x51, 0x55));

    // ---- type ---------------------------------------------------------------
    // Two roles: a tight grotesque for state/labels that must read at four feet,
    // and a mono for anything numeric so digits line up in columns.
    public static readonly FontFamily Display = new("Avenir Next, Helvetica Neue, sans-serif");
    public static readonly FontFamily Mono = new("SF Mono, Menlo, monospace");

    // ---- geometry -----------------------------------------------------------
    public static readonly CornerRadius RadiusLg = new(14);
    public static readonly CornerRadius RadiusMd = new(10);
    public static readonly CornerRadius Pill = new(999);

    /// <summary>Meter fill for a 0..1 level: green until hot, amber, then red.</summary>
    public static Color MeterColor(double level) =>
        level >= 0.94 ? ClipColor : level >= 0.80 ? HotColor : SafeColor;

    /// <summary>The horizontal meter gradient (green → amber → red across the track).</summary>
    public static LinearGradientBrush MeterGradient() => new()
    {
        StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
        EndPoint = new RelativePoint(1, 0, RelativeUnit.Relative),
        GradientStops =
        {
            new GradientStop(SafeColor, 0.0),
            new GradientStop(SafeColor, 0.72),
            new GradientStop(HotColor, 0.88),
            new GradientStop(ClipColor, 1.0),
        },
    };

    private static SolidColorBrush New(byte r, byte g, byte b) => New(Color.FromRgb(r, g, b));

    private static SolidColorBrush New(Color c)
    {
        var brush = new SolidColorBrush(c);
        brush.ToImmutable();
        return brush;
    }
}
