using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;

namespace Fader.MenuBar;

/// <summary>
/// The Show-mode spectrum: what the guard is hearing, right now.
///
/// Unlike the old two-channel display this is N-channel - it draws the loudest
/// armed channel per band, because on stage you care that <em>something</em> is
/// ringing at 2 kHz, not which mic got there first (the tiles below say which).
///
/// Colour follows the system: the spectrum is metering, so it is green; a notch is
/// an event the guard caused, so it is magenta and can never be confused for level.
/// </summary>
public sealed class GuardSpectrum : Control
{
    public const int Bands = 100;
    private const float MinDb = -100f;

    private float _minHz = 200f, _maxHz = 16000f;
    private int _dragging;                                          // -1 low edge, +1 high edge, 0 none

    private readonly Dictionary<int, float[]> _slots = new();       // engine slot -> log bands
    private readonly List<(float Hz, float Depth, long Ticks)> _notches = new();
    private readonly List<(float Hz, long Ticks)> _catches = new();

    private static readonly IPen GridPen = new Pen(new SolidColorBrush(Color.FromArgb(0x18, 0xFF, 0xFF, 0xFF)), 1);
    private static readonly IPen Curve = new Pen(new SolidColorBrush(Color.FromArgb(0xCC, 0x78, 0xF0, 0xAA)), 1.5);
    private static readonly IBrush LabelBrush = Tokens.InkFaint;

    private static readonly (float Hz, string Text)[] Ticks =
        { (100f, "100"), (1000f, "1k"), (3000f, "3k"), (6000f, "6k"), (12000f, "12k") };

    /// <summary>
    /// When true the band edges can be dragged. Off in Show - nothing there may
    /// change the setup mid-song - and on in Setup, where the spectrum is exactly
    /// the context you want while choosing where the detector looks.
    /// </summary>
    public bool Editable { get; set; }

    /// <summary>Raised while dragging an edge, with the new (min, max).</summary>
    public event Action<float, float>? RangeDragged;

    public void SetRange(float minHz, float maxHz)
    {
        _minHz = minHz;
        _maxHz = maxHz;
        InvalidateVisual();
    }

    /// <summary>Push a frame for one engine slot. Pass null to drop a slot that went idle.</summary>
    public void SetSlot(int slot, float[]? logBands)
    {
        if (logBands is null) _slots.Remove(slot);
        else if (logBands.Length == Bands) _slots[slot] = logBands;
    }

    public void ClearSlots() => _slots.Clear();

    /// <summary>Replace the notch markers (frequency + how deep the cut is).</summary>
    public void SetNotches(IEnumerable<(float Hz, float Depth)> notches)
    {
        _notches.Clear();
        foreach (var (hz, depth) in notches) _notches.Add((hz, depth, 0));
    }

    /// <summary>Flag a fresh catch, which pulses for a couple of seconds.</summary>
    public void AddCatch(float hz) => _catches.Add((hz, Environment.TickCount64));

    public override void Render(DrawingContext ctx)
    {
        var w = Bounds.Width;
        var h = Bounds.Height;
        if (w <= 2 || h <= 2) return;

        ctx.FillRectangle(Tokens.Ground2, new Rect(0, 0, w, h), (float) Tokens.RadiusMd.TopLeft);

        foreach (var (hz, text) in Ticks)
        {
            var x = XForHz(hz, w);
            ctx.DrawLine(GridPen, new Point(x, 0), new Point(x, h - 16));
            ctx.DrawText(Label(text), new Point(x + 4, h - 15));
        }

        var composite = Composite();
        if (composite is not null)
        {
            ctx.DrawGeometry(AreaFill(h), null, Area(composite, w, h));
            ctx.DrawGeometry(null, Curve, Line(composite, w, h));
        }

        DrawBand(ctx, w, h);
        DrawNotches(ctx, w, h);
        DrawCatches(ctx, w, h);
    }

    /// <summary>Loudest armed channel per band - the "is anything ringing" view.</summary>
    private float[]? Composite()
    {
        if (_slots.Count == 0) return null;
        var outp = new float[Bands];
        Array.Fill(outp, MinDb);
        foreach (var bands in _slots.Values)
        {
            for (var i = 0; i < Bands; i++)
            {
                if (bands[i] > outp[i]) outp[i] = bands[i];
            }
        }
        return outp;
    }

    /// <summary>The watched band: everything outside it is dimmed and ignored.</summary>
    private void DrawBand(DrawingContext ctx, double w, double h)
    {
        var lo = XForHz(_minHz, w);
        var hi = XForHz(_maxHz, w);
        var shade = new SolidColorBrush(Color.FromArgb(0x9E, 0x09, 0x0B, 0x10));
        if (lo > 0) ctx.FillRectangle(shade, new Rect(0, 0, lo, h - 16));
        if (hi < w) ctx.FillRectangle(shade, new Rect(hi, 0, w - hi, h - 16));

        var pen = new Pen(Tokens.Accent, Editable ? 2 : 1);
        foreach (var x in new[] { lo, hi })
        {
            if (x <= 0 || x >= w) continue;
            ctx.DrawLine(pen, new Point(x, 0), new Point(x, h - 16));
            if (Editable)   // a grip you can see is a grip you can find in the dark
                ctx.DrawEllipse(Tokens.Ground, new Pen(Tokens.Accent, 2), new Point(x, (h - 16) / 2), 8, 8);
        }
    }

    private void DrawNotches(DrawingContext ctx, double w, double h)
    {
        foreach (var (hz, depth, _) in _notches)
        {
            if (hz <= 0f) continue;
            ctx.DrawLine(new Pen(Tokens.Catch, 2), new Point(XForHz(hz, w), 6), new Point(XForHz(hz, w), h - 18));
        }

        // Label only the deepest few. Twenty tags at once collide into unreadable
        // mush, and the ones that matter on stage are the deep cuts, not every line.
        var labelled = _notches.Where(n => n.Hz > 0f)
                               .OrderBy(n => n.Depth)
                               .Take(4)
                               .OrderBy(n => n.Hz)
                               .ToList();
        double lastRight = double.NegativeInfinity;
        foreach (var (hz, depth, _) in labelled)
        {
            var x = XForHz(hz, w);
            var ft = new FormattedText($"{Hz(hz)} · {depth:0.#} dB", CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight, new Typeface(Tokens.Mono), 10.5, Tokens.Catch);
            var tx = Math.Clamp(x - ft.Width / 2, 2, Math.Max(2, w - ft.Width - 2));
            if (tx < lastRight + 6) continue;          // still overlapping: drop it
            lastRight = tx + ft.Width + 10;

            var box = new Rect(tx - 5, 6, ft.Width + 10, ft.Height + 5);
            ctx.DrawRectangle(Tokens.CatchSoft, new Pen(Tokens.Catch, 1), new RoundedRect(box, 5));
            ctx.DrawText(ft, new Point(tx, 8.5));
        }
    }

    // ---- dragging the band edges -------------------------------------------
    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (!Editable) return;
        var x = e.GetPosition(this).X;
        var w = Bounds.Width;
        var dLo = Math.Abs(x - XForHz(_minHz, w));
        var dHi = Math.Abs(x - XForHz(_maxHz, w));
        if (Math.Min(dLo, dHi) > 30) return;
        _dragging = dLo <= dHi ? -1 : 1;
        e.Pointer.Capture(this);
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);

        if (_dragging == 0)
        {
            if (!Editable) return;
            var px = e.GetPosition(this).X;
            var near = Math.Min(Math.Abs(px - XForHz(_minHz, Bounds.Width)),
                                Math.Abs(px - XForHz(_maxHz, Bounds.Width))) <= 30;
            Cursor = new Cursor(near ? StandardCursorType.SizeWestEast : StandardCursorType.Arrow);
            return;
        }
        var hz = HzForX(e.GetPosition(this).X, Bounds.Width);
        if (_dragging < 0) _minHz = (float) Math.Clamp(hz, 40, _maxHz - 200);
        else _maxHz = (float) Math.Clamp(hz, _minHz + 200, 20000);
        RangeDragged?.Invoke(_minHz, _maxHz);
        InvalidateVisual();
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        _dragging = 0;
        e.Pointer.Capture(null);
    }

    private static double HzForX(double x, double w) =>
        20.0 * Math.Pow(1000.0, Math.Clamp(x / Math.Max(1, w), 0, 1));

    /// <summary>Recent catches pulse and fade — the moment the guard did its job.</summary>
    private void DrawCatches(DrawingContext ctx, double w, double h)
    {
        var now = Environment.TickCount64;
        _catches.RemoveAll(c => now - c.Ticks > 2200);
        foreach (var (hz, ticks) in _catches)
        {
            var age = (now - ticks) / 2200.0;
            var alpha = (byte) (190 * (1 - age));
            var brush = new SolidColorBrush(Color.FromArgb(alpha, Tokens.CatchColor.R, Tokens.CatchColor.G, Tokens.CatchColor.B));
            var x = XForHz(hz, w);
            var r = 4 + 10 * age;   // expanding ring
            ctx.DrawEllipse(null, new Pen(brush, 2), new Point(x, h / 2), r, r);
        }
    }

    private static LinearGradientBrush AreaFill(double h) => new()
    {
        StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
        EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
        GradientStops =
        {
            new GradientStop(Color.FromArgb(0x6E, 0x35, 0xD0, 0x7A), 0),
            new GradientStop(Color.FromArgb(0x06, 0x35, 0xD0, 0x7A), 1),
        },
    };

    private static string Hz(float hz) => hz >= 1000f ? $"{hz / 1000f:0.##}k" : $"{hz:0}";

    private static Geometry Area(float[] bands, double w, double h)
    {
        var geo = new StreamGeometry();
        using var g = geo.Open();
        g.BeginFigure(new Point(0, h), isFilled: true);
        for (var i = 0; i < bands.Length; i++) g.LineTo(new Point(XForBand(i, w), YForDb(bands[i], h)));
        g.LineTo(new Point(w, h));
        g.EndFigure(isClosed: true);
        return geo;
    }

    private static Geometry Line(float[] bands, double w, double h)
    {
        var geo = new StreamGeometry();
        using var g = geo.Open();
        g.BeginFigure(new Point(XForBand(0, w), YForDb(bands[0], h)), isFilled: false);
        for (var i = 1; i < bands.Length; i++) g.LineTo(new Point(XForBand(i, w), YForDb(bands[i], h)));
        g.EndFigure(isClosed: false);
        return geo;
    }

    private static double XForBand(double band, double w) => band / (Bands - 1) * w;

    public static double XForHz(double hz, double w) =>
        XForBand(Math.Clamp(99.0 * Math.Log(hz / 20.0) / Math.Log(1000.0), 0, Bands - 1), w);

    private static double YForDb(double db, double h) =>
        Math.Clamp((0 - db) / -MinDb, 0, 1) * (h - 18);

    private static FormattedText Label(string s) =>
        new(s, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            new Typeface(Tokens.Mono), 10, LabelBrush);
}
