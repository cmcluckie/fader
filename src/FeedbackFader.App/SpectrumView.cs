using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

using Fader.Shared.Ui;

namespace FeedbackFader.App;

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

    // ---- heat: is this neighbourhood MOVING? --------------------------------
    // The display gives the whole spectrum 100 columns, so one column spans 7.2%
    // in frequency. Measured at the rig on 2026-09-27, a ring hopped 9293 ->
    // 10008 -> 10716 Hz, which is three columns: on screen that is one peak
    // jittering, not three rings being fought and replaced. The information is
    // there - those are 30 analysis bins apart - the axis just throws it away.
    //
    // Colour does not have that problem. A peak that is walking gets hot while
    // staying where it is, so motion is legible at a resolution where position
    // is not. And the quantity is not decoration: "how far has this
    // neighbourhood wandered lately" is exactly what the bank needs in order to
    // recognise a returning ring that has moved further than one memory bucket
    // (1/24 octave, 2.9%) can see. Shown here first, on purpose, so it can be
    // watched before anything is allowed to act on it.
    // The heat is NOT computed here. It is the engine's own track state, sent on
    // /fk/track, so what is drawn is the guard's actual belief about which rings
    // are the same ring - not a separate app-side guess that could agree with the
    // picture while the guard believed something else. When the bank is later
    // allowed to act on a hot track, this display is already the audit of it.
    private IReadOnlyList<FkTrack> _tracks = Array.Empty<FkTrack>();

    private static readonly Color Cold = Color.FromRgb(0x38, 0xBD, 0xF8);
    private static readonly Color Hot = Color.FromRgb(0xEF, 0x44, 0x44);

    public void SetTracks(IReadOnlyList<FkTrack> tracks) => _tracks = tracks;

    /// <summary>Hottest track covering this frequency, 0 if none does.</summary>
    private float HeatAt(double hz)
    {
        var best = 0f;
        foreach (var t in _tracks)
        {
            if (t.Hz <= 0f) continue;
            var gap = hz < t.LoHz ? t.LoHz - hz : hz > t.HiHz ? hz - t.HiHz : 0.0;
            if (gap <= 0.15 * hz && t.Heat > best) best = t.Heat;
        }
        return best;
    }

    /// Cold to hot. Heat also drives width and opacity, so the signal survives
    /// a monochrome screenshot and a red-green colour deficiency.
    private static Color Temperature(float heat)
    {
        var k = Math.Clamp(heat, 0f, 1f);
        return Color.FromRgb((byte) (Cold.R + (Hot.R - Cold.R) * k),
                             (byte) (Cold.G + (Hot.G - Cold.G) * k),
                             (byte) (Cold.B + (Hot.B - Cold.B) * k));
    }

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

        // --- where rings have been walking -------------------------------------
        DrawTrackSpans(ctx, h, w);

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
            var heat = HeatAt(d.Hz);
            var colour = heat > 0.05f ? Temperature(heat)
                       : d.Correlated ? NotchLive : Color.FromRgb(0xFB, 0xBF, 0x24);
            var brush = new SolidColorBrush(Color.FromArgb(alpha, colour.R, colour.G, colour.B));
            var x = XForHz(d.Hz, w);
            var r = (d.Correlated ? 5.0 : 3.0) + 2.0 * heat;
            ctx.DrawEllipse(brush, null, new Point(x, 8), r, r);
        }
    }

    private void DrawNotches(DrawingContext ctx, IReadOnlyList<(float Hz, bool Locked)> notches, double h, double w)
    {
        foreach (var (hz, locked) in notches)
        {
            if (hz <= 0f) continue;
            var x = XForHz(hz, w);
            // A locked notch is the operator's decision and keeps its own colour.
            // Everything else is tinted by how much its ring is moving.
            var heat = locked ? 0f : HeatAt(hz);
            var colour = locked ? NotchLocked : Temperature(heat);
            var alpha = (byte) (0xAA + 0x55 * heat);
            var pen = new Pen(new SolidColorBrush(Color.FromArgb(alpha, colour.R, colour.G, colour.B)),
                1 + 1.5 * heat,
                dashStyle: new DashStyle(new double[] { 3, 3 }, 0));
            ctx.DrawLine(pen, new Point(x, 0), new Point(x, h));
        }
    }

    /// The stretch a walking ring has covered, drawn as a band behind everything
    /// else. This is the part a log axis destroys: 9293 -> 10716 Hz is three
    /// columns wide, so as a shaded span it is visible where a moving dot is not.
    private void DrawTrackSpans(DrawingContext ctx, double h, double w)
    {
        foreach (var t in _tracks)
        {
            if (t.Hz <= 0f || t.Heat <= 0.05f || t.HiHz <= t.LoHz) continue;
            var x0 = XForHz(t.LoHz, w);
            var x1 = XForHz(t.HiHz, w);
            var c = Temperature(t.Heat);
            var brush = new SolidColorBrush(Color.FromArgb((byte) (0x18 + 0x30 * t.Heat), c.R, c.G, c.B));
            ctx.FillRectangle(brush, new Rect(x0, 0, Math.Max(2.0, x1 - x0), h));
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
