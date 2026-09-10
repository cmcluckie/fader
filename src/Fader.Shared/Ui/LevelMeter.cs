using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace Fader.Shared.Ui;

/// <summary>
/// A horizontal level meter with real ballistics: fast attack, slow release, and a
/// peak-hold marker that sits for a beat before falling.
///
/// Ballistics are the whole point. A meter that snaps to the raw value flickers and
/// teaches nothing; one that rises instantly and falls smoothly reads as level. The
/// owner pushes raw values with <see cref="SetTarget"/> and drives <see cref="Tick"/>
/// from one shared timer, so N meters cost one timer rather than N.
/// </summary>
public sealed class LevelMeter : Control
{
    private const double AttackPerTick = 0.55;    // toward the target, per tick
    private const double ReleasePerTick = 0.085;  // decay when falling
    private const double PeakFallPerTick = 0.012;
    private const int PeakHoldTicks = 22;         // ~0.7 s at 30 ms

    private double _target;
    private double _level;
    private double _peak;
    private int _peakHeld;

    /// <summary>Dim the whole meter — used for channels that aren't armed.</summary>
    public bool Muted { get; set; }

    public void SetTarget(double level01) => _target = Math.Clamp(level01, 0, 1);

    /// <summary>Advance the ballistics one frame. Returns true if a repaint is needed.</summary>
    public bool Tick()
    {
        var before = _level;
        var beforePeak = _peak;

        if (Muted)
        {
            _target = 0;
        }

        _level += _target > _level
            ? (_target - _level) * AttackPerTick
            : (_target - _level) * ReleasePerTick;

        if (_level < 0.001) _level = 0;

        if (_level >= _peak)
        {
            _peak = _level;
            _peakHeld = PeakHoldTicks;
        }
        else if (_peakHeld > 0)
        {
            _peakHeld--;
        }
        else
        {
            _peak = Math.Max(_level, _peak - PeakFallPerTick);
        }

        var changed = Math.Abs(_level - before) > 0.0015 || Math.Abs(_peak - beforePeak) > 0.0015;
        if (changed) InvalidateVisual();
        return changed;
    }

    public override void Render(DrawingContext ctx)
    {
        var w = Bounds.Width;
        var h = Bounds.Height;
        if (w <= 0 || h <= 0) return;

        var radius = h / 2;
        var track = new RoundedRect(new Rect(0, 0, w, h), radius);
        ctx.DrawRectangle(Tokens.Panel3, null, track);

        if (Muted || _level <= 0) return;

        // Clip the gradient to the filled width so colour maps to absolute level,
        // not to the width of the fill - the same signal always shows the same hue.
        using (ctx.PushClip(new Rect(0, 0, w * _level, h)))
        {
            ctx.DrawRectangle(Tokens.MeterGradient(), null, track);
        }

        if (_peak > 0.02)
        {
            var x = Math.Min(w - 2, w * _peak);
            var brush = new SolidColorBrush(Tokens.MeterColor(_peak));
            ctx.FillRectangle(brush, new Rect(x - 1.5, 0, 2.5, h));
        }
    }
}
