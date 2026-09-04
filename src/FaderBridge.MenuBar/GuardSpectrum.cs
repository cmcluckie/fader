using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;

using Fader.Bridge.Feedback;

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

    // Usable travel for the band edges. The 1.5x gap keeps them from crossing or
    // pinching into a slot too narrow to detect anything in.
    // The drag range matches what is actually useful. Raising this line does not
    // filter junk - prominence, stability and growth already do that - it only
    // blinds the detector until a ring is loud, so the top of the range is well
    // below where it used to be.
    public const float MinFloorDb = -95f;
    public const float MaxFloorDb = -55f;
    public const float MinEdgeHz = 40f;
    public const float MaxLowHz = 8000f;
    public const float MinHighHz = 1000f;
    public const float MaxEdgeHz = 18000f;

    private float _minHz = 200f, _maxHz = 16000f;
    private float _floorDb = -70f;
    private int _dragging;                          // -1 low edge, +1 high edge, 2 floor, 0 none

    private readonly Dictionary<int, float[]> _slots = new();       // engine slot -> log bands
    private readonly List<(float Hz, float Depth, long Ticks)> _notches = new();
    private readonly List<(float Hz, long Ticks)> _catches = new();

    private static readonly IPen GridPen = new Pen(new SolidColorBrush(Color.FromArgb(0x18, 0xFF, 0xFF, 0xFF)), 1);
    private static readonly IPen Curve = new Pen(new SolidColorBrush(Color.FromArgb(0xCC, 0x78, 0xF0, 0xAA)), 1.5);
    private static readonly IBrush LabelBrush = Tokens.InkFaint;
    private static readonly IPen EqPen =
        new Pen(new SolidColorBrush(Color.FromArgb(0xF0, 0xF4, 0x5D, 0x9C)), 1.8);
    private static readonly IBrush EqFill = new SolidColorBrush(Color.FromArgb(0x3A, 0xF4, 0x5D, 0x9C));
    private static readonly IPen InputLine =
        new Pen(new SolidColorBrush(Color.FromArgb(0x55, 0x78, 0xF0, 0xAA)), 1.0);
    private static readonly IPen NotchTick =
        new Pen(new SolidColorBrush(Color.FromArgb(0x70, 0xF4, 0x5D, 0x9C)), 1.5);

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

    /// <summary>Raised while dragging the floor line, with the new level.</summary>
    public event Action<float>? FloorDragged;

    public void SetFloor(float db)
    {
        _floorDb = db;
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

        // Two traces, because one of them was misleading on its own.
        //
        // What was drawn here is the signal the DETECTOR sees, and the detector
        // analyses before the filters - so a ring the guard was killing perfectly
        // still appeared at full height, and the only honest reading of this
        // display was "it found it and did nothing". Reported from the rig exactly
        // that way, of a ring that measurement shows is caught within 1 dB of the
        // action threshold.
        //
        // The filled trace is now what LEAVES: the input with the notch response
        // applied. The faint line above it is the input, so the size of the gap
        // between them is the guard's work, visible.
        var composite = Composite();
        if (composite is not null)
        {
            var outp = AfterNotches(composite);
            ctx.DrawGeometry(AreaFill(h), null, Area(outp, w, h));
            ctx.DrawGeometry(null, InputLine, Line(composite, w, h));
            ctx.DrawGeometry(null, Curve, Line(outp, w, h));
        }

        DrawBand(ctx, w, h);
        DrawEqCurve(ctx, w, h);
        DrawNotches(ctx, w, h);
        DrawCatches(ctx, w, h);
    }

    /// <summary>Loudest armed channel per band - the "is anything ringing" view.</summary>
    /// <summary>
    /// The input with the live filters applied - what the desk actually receives.
    /// Computed rather than measured: the engine analyses pre-notch, so this is the
    /// only way to show the result without a second FFT on the audio thread. It
    /// uses the same filter maths as the engine (see NotchResponse), so it is exact
    /// to the extent that those agree.
    /// </summary>
    private float[] AfterNotches(float[] input)
    {
        var live = _notches.Where(n => n.Hz > 0f && n.Depth < -0.1f)
                           .Select(n => (n.Hz, n.Depth))
                           .ToList();
        if (live.Count == 0) return input;

        var outp = new float[Bands];
        for (var i = 0; i < Bands; i++)
        {
            var hz = 20.0 * Math.Pow(1000.0, i / (double) (Bands - 1));
            outp[i] = (float) Math.Max(input[i] + NotchResponse.SumDb(live, hz), MinDb);
        }
        return outp;
    }

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
        var lo = HandleX(_minHz, w);
        var hi = HandleX(_maxHz, w);
        var shade = new SolidColorBrush(Color.FromArgb(0x9E, 0x09, 0x0B, 0x10));
        if (lo > 0) ctx.FillRectangle(shade, new Rect(0, 0, lo, h - 16));
        if (hi < w) ctx.FillRectangle(shade, new Rect(hi, 0, w - hi, h - 16));

        // The floor closes the box: with the two edges it reads as one rule -
        // "cut things inside this rectangle" - which is the whole model in a glance.
        var fy = FloorY(_floorDb, h);
        ctx.FillRectangle(shade, new Rect(lo, fy, Math.Max(0, hi - lo), Math.Max(0, (h - 16) - fy)));

        var floorPen = new Pen(Tokens.Accent, Editable ? 2 : 1) { DashStyle = new DashStyle(new double[] { 4, 3 }, 0) };
        ctx.DrawLine(floorPen, new Point(lo, fy), new Point(hi, fy));
        if (Editable)
            ctx.DrawEllipse(Tokens.Ground, new Pen(Tokens.Accent, 2), new Point((lo + hi) / 2, fy), 8, 8);

        var pen = new Pen(Tokens.Accent, Editable ? 2 : 1);
        foreach (var x in new[] { lo, hi })
        {
            ctx.DrawLine(pen, new Point(x, 0), new Point(x, h - 16));
            if (Editable)   // a grip you can see is a grip you can find in the dark
                ctx.DrawEllipse(Tokens.Ground, new Pen(Tokens.Accent, 2), new Point(x, (h - 16) / 2), 8, 8);
        }
    }

    /// <summary>
    /// Where an edge is DRAWN and grabbed, held clear of the borders. Pinned to the
    /// axis it would otherwise sit exactly on the plot edge at the extremes, where a
    /// handle is both invisible and impossible to grab back.
    /// </summary>
    private static double HandleX(double hz, double w) =>
        Math.Clamp(XForHz(hz, w), 8, Math.Max(8, w - 8));

    private static double FloorY(double db, double h) =>
        Math.Clamp(YForDb(db, h), 8, Math.Max(8, (h - 16) - 8));

    /// <summary>Inverse of YForDb: top of the plot is 0 dB, bottom is MinDb.</summary>
    private static double DbForY(double y, double h) =>
        Math.Clamp(MinDb * (y / Math.Max(1, h - 18)), MinFloorDb, MaxFloorDb);

    /// <summary>
    /// The summed response of every live filter, drawn hanging from the top of the
    /// plot: how much of the sound the guard is currently removing, at every
    /// frequency, at a glance.
    ///
    /// The markers alone could never show this. Each filter is a tidy single line,
    /// and twenty-two tidy lines 400-800 Hz apart - each over a kilohertz wide -
    /// look like twenty-two small surgical cuts while adding up to a high-cut
    /// filter. Measured on the rig: -13.6 dB average across the top end and -28 dB
    /// at 6 kHz, with nothing on screen saying so.
    /// </summary>
    private void DrawEqCurve(DrawingContext ctx, double w, double h)
    {
        if (_notches.Count == 0) return;

        var live = _notches.Where(n => n.Hz > 0f && n.Depth < -0.1f)
                           .Select(n => (n.Hz, n.Depth))
                           .ToList();
        if (live.Count == 0) return;

        // Full scale is 30 dB of cut over the top half of the plot: enough room
        // that a shelf looks like a shelf rather than a crease at the ceiling.
        // Sampled every pixel - every second pixel aliased the narrow filters into
        // a comb, which read as lots of small cuts instead of one broad one.
        const double fullScaleDb = 30.0;
        var band = (h - 18) * 0.5;

        var pts = new List<Point>((int) w + 1);
        for (var px = 0; px <= (int) w; px++)
        {
            var cut = NotchResponse.SumDb(live, HzForX(px, w));
            pts.Add(new Point(px, Math.Clamp(-cut / fullScaleDb, 0, 1) * band));
        }

        // Filled down from the top edge: the shaded region IS what is being taken
        // away, which is the thing to see at a glance from behind a mic.
        var fill = new StreamGeometry();
        using (var g = fill.Open())
        {
            g.BeginFigure(new Point(0, 0), true);
            foreach (var pt in pts) g.LineTo(pt);
            g.LineTo(new Point(w, 0));
            g.EndFigure(true);
        }
        ctx.DrawGeometry(EqFill, null, fill);

        var line = new StreamGeometry();
        using (var g = line.Open())
        {
            g.BeginFigure(pts[0], false);
            for (var i = 1; i < pts.Count; i++) g.LineTo(pts[i]);
            g.EndFigure(false);
        }
        ctx.DrawGeometry(null, EqPen, line);

        // One number for "how muffled am I": the average cut across the top end,
        // which is the part a vocal misses first.
        var avg = NotchResponse.AverageDb(live, 4000, 16000);
        if (avg <= -0.5)
        {
            // Top left: the only reliably empty corner. The floor handle prints
            // its own dB at top right, the range badges sit mid-top, and the
            // frequency ticks own the bottom edge.
            ctx.DrawText(Label($"guard is removing {-avg:F1} dB of 4-16k"), new Point(8, 4));
        }
    }

    private void DrawNotches(DrawingContext ctx, double w, double h)
    {
        // Short ticks on the baseline, not full-height bars. Twenty-two bars
        // across the plot hide the very thing they are reporting - the summed
        // curve above them - and the curve already shows where the cuts are. The
        // ticks are just the tally.
        foreach (var (hz, depth, _) in _notches)
        {
            if (hz <= 0f) continue;
            var x = XForHz(hz, w);
            var len = 6 + Math.Clamp(-depth / 24.0, 0, 1) * 8;
            ctx.DrawLine(NotchTick, new Point(x, h - 18), new Point(x, h - 18 - len));
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
        var y = e.GetPosition(this).Y;
        var h = Bounds.Height;
        var dLo = Math.Abs(x - HandleX(_minHz, w));
        var dHi = Math.Abs(x - HandleX(_maxHz, w));

        if (Math.Min(dLo, dHi) <= 30)
            _dragging = dLo <= dHi ? -1 : 1;
        else if (Math.Abs(y - FloorY(_floorDb, h)) <= 16
                 && x > HandleX(_minHz, w) && x < HandleX(_maxHz, w))
            _dragging = 2;
        else
            return;

        e.Pointer.Capture(this);
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);

        if (_dragging == 0)
        {
            if (!Editable) return;
            var px = e.GetPosition(this).X;
            var py = e.GetPosition(this).Y;
            var nearEdge = Math.Min(Math.Abs(px - HandleX(_minHz, Bounds.Width)),
                                    Math.Abs(px - HandleX(_maxHz, Bounds.Width))) <= 30;
            var nearFloor = !nearEdge && Math.Abs(py - FloorY(_floorDb, Bounds.Height)) <= 16;
            Cursor = new Cursor(nearEdge ? StandardCursorType.SizeWestEast
                              : nearFloor ? StandardCursorType.SizeNorthSouth
                              : StandardCursorType.Arrow);
            return;
        }
        if (_dragging == 2)
        {
            _floorDb = (float) DbForY(e.GetPosition(this).Y, Bounds.Height);
            FloorDragged?.Invoke(_floorDb);
            InvalidateVisual();
            return;
        }

        var hz = HzForX(e.GetPosition(this).X, Bounds.Width);
        if (_dragging < 0) _minHz = (float) Math.Clamp(hz, MinEdgeHz, Math.Min(_maxHz / 1.5, MaxLowHz));
        else               _maxHz = (float) Math.Clamp(hz, Math.Max(_minHz * 1.5, MinHighHz), MaxEdgeHz);
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
