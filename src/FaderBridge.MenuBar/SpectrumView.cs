using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace Fader.MenuBar;

/// <summary>
/// Custom-drawn spectrum display: the engine's LEAD and BGV spectra and the X32
/// RTA on one shared log-frequency axis (20 Hz - 20 kHz), with notch markers and
/// detection/correlation flags. Data arrays are aligned to the RTA's 100 log
/// bands, so band index maps linearly to the x-axis. The window feeds it and
/// pokes InvalidateVisual on a timer.
/// </summary>
public sealed class SpectrumView : Control
{
    private const int Bands = 100;
    private const float MinDb = -100f;

    private float[] _lead = New();
    private float[] _bgv = New();
    private float[] _rta = New();
    private IReadOnlyList<(float Hz, bool Locked)> _leadNotches = Array.Empty<(float, bool)>();
    private IReadOnlyList<(float Hz, bool Locked)> _bgvNotches = Array.Empty<(float, bool)>();
    private readonly List<(float Hz, bool Correlated, long Ticks)> _detections = new();

    private static readonly IBrush Background = new SolidColorBrush(Color.FromRgb(0x12, 0x14, 0x1A));
    private static readonly IPen Grid = new Pen(new SolidColorBrush(Color.FromRgb(0x2A, 0x2E, 0x38)), 1);
    private static readonly IBrush Label = new SolidColorBrush(Color.FromRgb(0x6B, 0x72, 0x80));
    private static readonly IBrush LeadFill = new SolidColorBrush(Color.FromArgb(0x59, 0x3B, 0x82, 0xF6));
    private static readonly IPen LeadLine = new Pen(new SolidColorBrush(Color.FromRgb(0x60, 0xA5, 0xFA)), 1.5);
    private static readonly IPen BgvLine = new Pen(new SolidColorBrush(Color.FromRgb(0xA7, 0x8B, 0xFA)), 1.2);
    private static readonly IPen RtaLine = new Pen(new SolidColorBrush(Color.FromRgb(0x34, 0xD3, 0x99)), 1.5);
    private static readonly Color NotchLocked = Color.FromRgb(0xF5, 0x9E, 0x0B);
    private static readonly Color NotchLive = Color.FromRgb(0xEF, 0x44, 0x44);

    private static float[] New() => Enumerable.Repeat(MinDb, Bands).ToArray();

    public void SetEngine(int channel, float[] logBands)
    {
        if (logBands.Length == Bands)
        {
            if (channel == 0) _lead = logBands; else _bgv = logBands;
        }
    }

    public void SetRta(float[] bands)
    {
        if (bands.Length == Bands) _rta = bands;
    }

    public void SetNotches(int channel, IReadOnlyList<(float Hz, bool Locked)> notches)
    {
        if (channel == 0) _leadNotches = notches; else _bgvNotches = notches;
    }

    public void AddDetection(float hz, bool correlated) =>
        _detections.Add((hz, correlated, Environment.TickCount64));

    public override void Render(DrawingContext ctx)
    {
        var w = Bounds.Width;
        var h = Bounds.Height;
        ctx.FillRectangle(Background, new Rect(0, 0, w, h));

        // --- grid --------------------------------------------------------------
        foreach (var db in new[] { -20f, -40f, -60f, -80f })
        {
            var y = YForDb(db, h);
            ctx.DrawLine(Grid, new Point(0, y), new Point(w, y));
            ctx.DrawText(Text($"{db:F0}"), new Point(2, y - 14));
        }
        foreach (var (hz, text) in new[] { (100f, "100"), (1000f, "1k"), (10000f, "10k") })
        {
            var x = XForHz(hz, w);
            ctx.DrawLine(Grid, new Point(x, 0), new Point(x, h));
            ctx.DrawText(Text(text), new Point(x + 2, h - 16));
        }

        // --- spectra -----------------------------------------------------------
        ctx.DrawGeometry(LeadFill, null, Area(_lead, w, h));
        ctx.DrawGeometry(null, LeadLine, Line(_lead, w, h));
        ctx.DrawGeometry(null, BgvLine, Line(_bgv, w, h));
        ctx.DrawGeometry(null, RtaLine, Line(_rta, w, h));

        // --- notch markers -----------------------------------------------------
        DrawNotches(ctx, _leadNotches, h, w);
        DrawNotches(ctx, _bgvNotches, h, w);

        // --- detections (fade over ~2.5 s) -------------------------------------
        var now = Environment.TickCount64;
        _detections.RemoveAll(d => now - d.Ticks > 2500);
        foreach (var d in _detections)
        {
            var age = (now - d.Ticks) / 2500f;
            var alpha = (byte) (200 * (1 - age));
            var colour = d.Correlated ? NotchLive : Color.FromRgb(0xFB, 0xBF, 0x24);
            var brush = new SolidColorBrush(Color.FromArgb(alpha, colour.R, colour.G, colour.B));
            var x = XForHz(d.Hz, w);
            var r = d.Correlated ? 5.0 : 3.0;
            ctx.DrawEllipse(brush, null, new Point(x, 8), r, r);
        }
    }

    private void DrawNotches(DrawingContext ctx, IReadOnlyList<(float Hz, bool Locked)> notches, double h, double w)
    {
        foreach (var (hz, locked) in notches)
        {
            if (hz <= 0f) continue;
            var x = XForHz(hz, w);
            var colour = locked ? NotchLocked : NotchLive;
            var pen = new Pen(new SolidColorBrush(Color.FromArgb(0xAA, colour.R, colour.G, colour.B)), 1,
                dashStyle: new DashStyle(new double[] { 3, 3 }, 0));
            ctx.DrawLine(pen, new Point(x, 0), new Point(x, h));
        }
    }

    private static Geometry Area(float[] bands, double w, double h)
    {
        var geo = new StreamGeometry();
        using var g = geo.Open();
        g.BeginFigure(new Point(0, h), isFilled: true);
        for (var i = 0; i < bands.Length; i++)
        {
            g.LineTo(new Point(XForBand(i, w), YForDb(bands[i], h)));
        }
        g.LineTo(new Point(w, h));
        g.EndFigure(isClosed: true);
        return geo;
    }

    private static Geometry Line(float[] bands, double w, double h)
    {
        var geo = new StreamGeometry();
        using var g = geo.Open();
        g.BeginFigure(new Point(XForBand(0, w), YForDb(bands[0], h)), isFilled: false);
        for (var i = 1; i < bands.Length; i++)
        {
            g.LineTo(new Point(XForBand(i, w), YForDb(bands[i], h)));
        }
        g.EndFigure(isClosed: false);
        return geo;
    }

    private static double XForBand(double band, double w) => band / (Bands - 1) * w;

    private static double XForHz(double hz, double w) =>
        XForBand(Math.Clamp(99.0 * Math.Log(hz / 20.0) / Math.Log(1000.0), 0, Bands - 1), w);

    private static double YForDb(double db, double h) =>
        Math.Clamp((0 - db) / -MinDb, 0, 1) * h;

    private static FormattedText Text(string s) =>
        new(s, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            Typeface.Default, 11, Label);
}
