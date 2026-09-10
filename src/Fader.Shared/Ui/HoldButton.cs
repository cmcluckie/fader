using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;

namespace Fader.Shared.Ui;

/// <summary>
/// A button that must be held to fire, with the hold drawn as a filling bar.
///
/// This is the confirmation model for anything destructive in Show mode. A modal
/// dialog mid-song is unacceptable - it stops the show to ask a question - but an
/// accidental brush shouldn't wipe the guard either. A one-second hold is
/// confirmation proportional to the blast radius, and the fill makes the commitment
/// visible while it happens, so releasing early is an obvious escape.
/// </summary>
public sealed class HoldButton : Control
{
    private readonly string _label;
    private readonly string _hint;
    private readonly IBrush _accent;
    private readonly double _holdSeconds;

    private long _pressedAt;
    private bool _fired;

    public HoldButton(string label, string hint, IBrush accent, double holdSeconds = 1.0)
    {
        _label = label.ToUpperInvariant();
        _hint = hint;
        _accent = accent;
        _holdSeconds = holdSeconds;
        Cursor = new Cursor(StandardCursorType.Hand);
        Focusable = true;
        MinWidth = 150;
        Height = 62;
    }

    public event Action? Fired;

    private string LabelText => _label;
    private string HintText => _hint;

    /// <summary>Size to the widest of label and hint — a button must never clip its own text.</summary>
    protected override Size MeasureOverride(Size availableSize)
    {
        var label = new FormattedText(LabelText, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            new Typeface(Tokens.Display, FontStyle.Normal, FontWeight.Bold), 14, Tokens.Ink);
        var hint = new FormattedText(HintText, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            new Typeface(Tokens.Mono), 10.5, Tokens.InkFaint);
        return new Size(Math.Max(MinWidth, Math.Max(label.Width, hint.Width) + 36), Height);
    }


    /// <summary>0..1 progress of the current hold.</summary>
    private double Progress => _pressedAt == 0
        ? 0
        : Math.Clamp((Environment.TickCount64 - _pressedAt) / (_holdSeconds * 1000.0), 0, 1);

    public bool Tick()
    {
        if (_pressedAt == 0) return false;

        if (!_fired && Progress >= 1.0)
        {
            _fired = true;
            Fired?.Invoke();
        }
        InvalidateVisual();
        return true;
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        _pressedAt = Environment.TickCount64;
        _fired = false;
        e.Pointer.Capture(this);
        InvalidateVisual();
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        _pressedAt = 0;
        _fired = false;
        e.Pointer.Capture(null);
        InvalidateVisual();
    }

    public override void Render(DrawingContext ctx)
    {
        var w = Bounds.Width;
        var h = Bounds.Height;
        var rect = new RoundedRect(new Rect(0, 0, w, h), Tokens.RadiusLg.TopLeft);

        var colour = (_accent as ISolidColorBrush)?.Color ?? Tokens.ClipColor;
        var bg = new SolidColorBrush(Color.FromArgb(0x14, colour.R, colour.G, colour.B));
        ctx.DrawRectangle(bg, new Pen(new SolidColorBrush(Color.FromArgb(0x66, colour.R, colour.G, colour.B)), 1), rect);

        // the hold, made visible
        var p = Progress;
        if (p > 0)
        {
            using (ctx.PushClip(new Rect(0, 0, w * p, h)))
            {
                var fill = new SolidColorBrush(Color.FromArgb(0x4D, colour.R, colour.G, colour.B));
                ctx.DrawRectangle(fill, null, rect);
            }
        }

        var label = new FormattedText(_label, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            new Typeface(Tokens.Display, FontStyle.Normal, FontWeight.Bold), 14, _accent);
        ctx.DrawText(label, new Point((w - label.Width) / 2, h / 2 - label.Height + 2));

        var hint = new FormattedText(_hint, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            new Typeface(Tokens.Mono), 10.5, Tokens.InkFaint);
        ctx.DrawText(hint, new Point((w - hint.Width) / 2, h / 2 + 4));
    }
}

/// <summary>A plain tap button in the same visual language (used for Bypass).</summary>
public sealed class TapButton : Control
{
    private bool _hover;

    public TapButton(string label, string hint)
    {
        Label = label.ToUpperInvariant();
        Hint = hint;
        Cursor = new Cursor(StandardCursorType.Hand);
        Focusable = true;
        MinWidth = 150;
        Height = 62;
    }

    public string Label { get; set; }

    /// <summary>The small line under the label: what tapping will DO.</summary>
    public string Hint { get; set; }

    /// <summary>Lit means the thing this button names is currently active.</summary>
    public bool IsLit { get; set; }

    /// <summary>
    /// Colour when lit. Teal by default, which is "this is protecting you". A
    /// recording state wants to read differently from a protecting one even at a
    /// glance from behind a microphone, so Capture lights pink instead - two lit
    /// teal buttons side by side say the same thing twice.
    /// </summary>
    public Color LitColour { get; set; } = Tokens.AccentColor;

    public event Action? Clicked;

    private string LabelText => Label;
    private string HintText => Hint;

    /// <summary>Size to the widest of label and hint — a button must never clip its own text.</summary>
    protected override Size MeasureOverride(Size availableSize)
    {
        var label = new FormattedText(LabelText, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            new Typeface(Tokens.Display, FontStyle.Normal, FontWeight.Bold), 14, Tokens.Ink);
        var hint = new FormattedText(HintText, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            new Typeface(Tokens.Mono), 10.5, Tokens.InkFaint);
        return new Size(Math.Max(MinWidth, Math.Max(label.Width, hint.Width) + 36), Height);
    }

    protected override void OnPointerEntered(PointerEventArgs e) { _hover = true; InvalidateVisual(); base.OnPointerEntered(e); }
    protected override void OnPointerExited(PointerEventArgs e) { _hover = false; InvalidateVisual(); base.OnPointerExited(e); }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        Clicked?.Invoke();
        e.Handled = true;
    }

    public override void Render(DrawingContext ctx)
    {
        var w = Bounds.Width;
        var h = Bounds.Height;
        var rect = new RoundedRect(new Rect(0, 0, w, h), Tokens.RadiusLg.TopLeft);

        var lit = new SolidColorBrush(LitColour);
        var litSoft = new SolidColorBrush(Color.FromArgb(0x22, LitColour.R, LitColour.G, LitColour.B));
        var bg = IsLit ? (IBrush) litSoft : _hover ? Tokens.Panel3 : Tokens.Panel2;
        var border = IsLit ? (IBrush) lit : Tokens.Line;
        ctx.DrawRectangle(bg, new Pen(border, 1), rect);

        var ink = IsLit ? (IBrush) lit : Tokens.Ink;
        var label = new FormattedText(Label, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            new Typeface(Tokens.Display, FontStyle.Normal, FontWeight.Bold), 14, ink);
        ctx.DrawText(label, new Point((w - label.Width) / 2, h / 2 - label.Height + 2));

        var hint = new FormattedText(Hint, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            new Typeface(Tokens.Mono), 10.5, Tokens.InkFaint);
        ctx.DrawText(hint, new Point((w - hint.Width) / 2, h / 2 + 4));
    }
}
