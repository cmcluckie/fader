using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;

namespace Fader.MenuBar;

/// <summary>
/// A physical-feeling ON/OFF switch. A checkbox asks "is this ticked?"; a switch
/// says ON. On a dark stage that difference is the whole point, so armed channels
/// get a lit teal track and a knob that has visibly travelled.
/// </summary>
public sealed class ArmSwitch : Control
{
    private bool _isOn;
    private double _knob;   // 0..1, animated toward _isOn

    public ArmSwitch()
    {
        Width = 52;
        Height = 30;
        Cursor = new Cursor(StandardCursorType.Hand);
        Focusable = true;
    }

    public event Action<bool>? Toggled;

    public bool IsOn
    {
        get => _isOn;
        set
        {
            if (_isOn == value) return;
            _isOn = value;
            InvalidateVisual();
        }
    }

    /// <summary>Set state without raising <see cref="Toggled"/> (for syncing from the model).</summary>
    public void SetSilently(bool on)
    {
        _isOn = on;
        _knob = on ? 1 : 0;
        InvalidateVisual();
    }

    /// <summary>Advance the knob travel. Returns true if a repaint happened.</summary>
    public bool Tick()
    {
        var target = _isOn ? 1.0 : 0.0;
        if (Math.Abs(_knob - target) < 0.01)
        {
            if (_knob != target) { _knob = target; InvalidateVisual(); return true; }
            return false;
        }
        _knob += (target - _knob) * 0.35;
        InvalidateVisual();
        return true;
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        IsOn = !_isOn;
        Toggled?.Invoke(_isOn);
        e.Handled = true;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key is Key.Space or Key.Enter)
        {
            IsOn = !_isOn;
            Toggled?.Invoke(_isOn);
            e.Handled = true;
        }
        base.OnKeyDown(e);
    }

    public override void Render(DrawingContext ctx)
    {
        var w = Bounds.Width;
        var h = Bounds.Height;
        var track = new RoundedRect(new Rect(0, 0, w, h), h / 2);

        var trackFill = _knob > 0.5 ? Tokens.AccentSoft : Tokens.Panel3;
        var border = _knob > 0.5 ? Tokens.Accent : Tokens.Line;
        ctx.DrawRectangle(trackFill, new Pen(border, 1), track);

        var pad = 3.0;
        var d = h - pad * 2;
        var x = pad + _knob * (w - d - pad * 2);
        var knobBrush = _knob > 0.5 ? Tokens.Accent : Tokens.InkFaint;
        ctx.DrawEllipse(knobBrush, null, new Point(x + d / 2, h / 2), d / 2, d / 2);
    }
}

/// <summary>
/// A status LED that breathes. Motion is load-bearing here: a pulsing dot reads as
/// "running" from across a room, where a static dot could be a stuck UI.
/// </summary>
public sealed class Led : Control
{
    private double _phase;

    public Led(IBrush colour, double size = 12)
    {
        Colour = colour;
        Width = size;
        Height = size;
        VerticalAlignment = VerticalAlignment.Center;
    }

    public IBrush Colour { get; set; }

    /// <summary>When false the LED sits still and dim — a stopped engine shouldn't look alive.</summary>
    public bool Pulsing { get; set; } = true;

    public void Tick()
    {
        if (!Pulsing) return;
        _phase += 0.045;
        InvalidateVisual();
    }

    public override void Render(DrawingContext ctx)
    {
        var r = Math.Min(Bounds.Width, Bounds.Height) / 2;
        var c = new Point(Bounds.Width / 2, Bounds.Height / 2);

        if (Pulsing)
        {
            var glow = 0.5 + 0.5 * Math.Sin(_phase);
            if (Colour is ISolidColorBrush s)
            {
                var halo = new SolidColorBrush(Color.FromArgb((byte) (70 * glow), s.Color.R, s.Color.G, s.Color.B));
                ctx.DrawEllipse(halo, null, c, r * 2.1, r * 2.1);
            }
        }
        ctx.DrawEllipse(Colour, null, c, r, r);
    }
}

/// <summary>Small helpers for building the panels without a wall of property setters.</summary>
internal static class Ui
{
    public static TextBlock Text(string text, double size, IBrush brush, FontWeight weight = FontWeight.Normal) =>
        new() { Text = text, FontSize = size, Foreground = brush, FontWeight = weight };

    /// <summary>An uppercase, letter-spaced caption — the label voice used throughout.</summary>
    public static TextBlock Caption(string text) => new()
    {
        Text = text.ToUpperInvariant(),
        FontSize = 10.5,
        FontFamily = Tokens.Mono,
        Foreground = Tokens.InkFaint,
        LetterSpacing = 1.2,
    };

    public static TextBlock Mono(string text, double size, IBrush brush, FontWeight weight = FontWeight.Normal) =>
        new() { Text = text, FontSize = size, FontFamily = Tokens.Mono, Foreground = brush, FontWeight = weight };

    public static Border Card(Control child, IBrush? background = null, IBrush? border = null, double pad = 16) => new()
    {
        Background = background ?? Tokens.Panel,
        BorderBrush = border ?? Tokens.Line,
        BorderThickness = new Thickness(1),
        CornerRadius = Tokens.RadiusLg,
        Padding = new Thickness(pad),
        Child = child,
    };

    /// <summary>A small button in the app's own language, not the platform default.</summary>
    public static Button Small(string text, IBrush? ink = null) => new()
    {
        Content = new TextBlock { Text = text, FontSize = 12.5, Foreground = ink ?? Tokens.Ink },
        Background = Tokens.Panel2,
        BorderBrush = Tokens.Line,
        BorderThickness = new Thickness(1),
        CornerRadius = Tokens.RadiusMd,
        Padding = new Thickness(12, 7),
    };

    public static StackPanel Stack(Orientation o, double spacing, params Control[] children)
    {
        var p = new StackPanel { Orientation = o, Spacing = spacing };
        foreach (var c in children) p.Children.Add(c);
        return p;
    }
}
