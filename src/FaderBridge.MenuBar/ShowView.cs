using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Fader.Bridge.Feedback;

namespace Fader.MenuBar;

/// <summary>
/// Show mode: the screen you leave open during the set.
///
/// It answers three questions without a sentence to read - is the guard on, what is
/// it hearing, and did it just catch something - and offers exactly two actions,
/// both big enough for a thumb. Everything configurable lives in Setup; nothing here
/// can open a dialog or surprise you mid-song.
/// </summary>
public sealed class ShowView : UserControl
{
    private readonly FeedbackController _feedback;

    private readonly Led _guardLed = new(Tokens.Accent, 13);
    private readonly TextBlock _guardText = Ui.Text("GUARD ON", 15, Tokens.Accent, FontWeight.Bold);
    private readonly Border _guardPill;
    private readonly TextBlock _cpuValue = Ui.Mono("—", 15, Tokens.Ink, FontWeight.SemiBold);
    private readonly TextBlock _rigValue = Ui.Mono("—", 15, Tokens.Ink, FontWeight.SemiBold);

    private readonly GuardSpectrum _spectrum = new();
    private readonly WrapPanel _tiles = new() { Orientation = Orientation.Horizontal };
    private readonly TextBlock _lastCatch = Ui.Mono("nothing caught yet", 15, Tokens.InkDim, FontWeight.SemiBold);
    private readonly TapButton _bypass = new("Guard On", "tap to bypass");
    private readonly HoldButton _panic = new("Panic", "hold 1s · clears every notch", Tokens.Clip);

    private readonly Dictionary<int, ChannelTile> _byPhysical = new();
    private readonly Dictionary<int, float[]> _slotBands = new();
    private int[] _built = Array.Empty<int>();

    public ShowView(FeedbackController feedback)
    {
        _feedback = feedback;

        _guardPill = new Border
        {
            Background = Tokens.AccentSoft,
            BorderBrush = Tokens.AccentLine,
            BorderThickness = new Thickness(1),
            CornerRadius = Tokens.Pill,
            Padding = new Thickness(20, 10),
            VerticalAlignment = VerticalAlignment.Center,
            Child = Ui.Stack(Orientation.Horizontal, 11, _guardLed, _guardText),
        };
        _guardText.LetterSpacing = 2.0;

        var rig = Ui.Stack(Orientation.Horizontal, 26,
            Readout("Engine", _rigValue),
            Readout("Load", _cpuValue));
        rig.HorizontalAlignment = HorizontalAlignment.Right;

        var top = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"), Margin = new Thickness(0, 0, 0, 14) };
        Grid.SetColumn(_guardPill, 0);
        Grid.SetColumn(rig, 2);
        top.Children.Add(_guardPill);
        top.Children.Add(rig);

        _spectrum.Height = 230;
        _spectrum.SetRange(feedback.MinHz, feedback.MaxHz);

        _bypass.Clicked += () => _feedback.SetBypass(!_feedback.IsBypassed);
        _panic.Fired += () => _feedback.ClearAll(includeLocked: true);

        var ribbon = Ui.Card(Ui.Stack(Orientation.Vertical, 4,
                Ui.Caption("Last catch"),
                _lastCatch),
            pad: 14);
        ribbon.CornerRadius = Tokens.RadiusLg;

        var actions = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto") };
        Grid.SetColumn(ribbon, 0);
        Grid.SetColumn(_bypass, 1);
        _bypass.Margin = new Thickness(14, 0, 0, 0);
        Grid.SetColumn(_panic, 2);
        _panic.Margin = new Thickness(14, 0, 0, 0);
        actions.Children.Add(ribbon);
        actions.Children.Add(_bypass);
        actions.Children.Add(_panic);

        var root = new DockPanel { Margin = new Thickness(20), LastChildFill = true };
        DockPanel.SetDock(top, Dock.Top);
        DockPanel.SetDock(actions, Dock.Bottom);
        var tileScroll = new ScrollViewer
        {
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
            Content = _tiles,
            Margin = new Thickness(0, 16, 0, 16),
        };
        var middle = new DockPanel { LastChildFill = true };
        DockPanel.SetDock(_spectrum, Dock.Top);
        middle.Children.Add(_spectrum);
        middle.Children.Add(tileScroll);

        root.Children.Add(top);
        root.Children.Add(actions);
        root.Children.Add(middle);
        Content = root;

        _feedback.SpectrumChanged += OnSpectrum;
        _feedback.NotchesChanged += OnNotches;
        _feedback.DetectionReceived += OnDetection;
        _feedback.ChannelsChanged += OnChannels;
        _feedback.EngineOkChanged += OnEngineOk;
        _feedback.SearchRangeChanged += OnRange;
        _feedback.BypassChanged += _ => Dispatcher.UIThread.Post(RenderState);

        RebuildTiles();
        RenderState();
    }

    private static Control Readout(string label, TextBlock value)
    {
        var s = Ui.Stack(Orientation.Vertical, 3, Ui.Caption(label), value);
        s.HorizontalAlignment = HorizontalAlignment.Right;
        value.HorizontalAlignment = HorizontalAlignment.Right;
        return s;
    }

    public void Teardown()
    {
        _feedback.SpectrumChanged -= OnSpectrum;
        _feedback.NotchesChanged -= OnNotches;
        _feedback.DetectionReceived -= OnDetection;
        _feedback.ChannelsChanged -= OnChannels;
        _feedback.EngineOkChanged -= OnEngineOk;
        _feedback.SearchRangeChanged -= OnRange;
    }

    /// <summary>Driven by the window's single animation timer.</summary>
    public void Tick()
    {
        _guardLed.Tick();
        _panic.Tick();
        foreach (var tile in _byPhysical.Values) tile.Tick();
        _spectrum.InvalidateVisual();
    }

    private void OnRange() => Dispatcher.UIThread.Post(
        () => _spectrum.SetRange(_feedback.MinHz, _feedback.MaxHz));

    private void OnChannels() => Dispatcher.UIThread.Post(RebuildTiles);
    private void OnEngineOk(bool _) => Dispatcher.UIThread.Post(RenderState);

    private void OnSpectrum(FkSpectrum spec)
    {
        var physical = _feedback.PhysicalForSlot(spec.Channel);
        if (physical < 0) return;

        var bands = ToLogAxis(spec);
        var peak = float.NegativeInfinity;
        foreach (var m in spec.Magnitudes) if (m > peak) peak = m;
        var level = Math.Clamp((peak + 80f) / 80f, 0f, 1f);

        Dispatcher.UIThread.Post(() =>
        {
            _slotBands[spec.Channel] = bands;
            _spectrum.SetSlot(spec.Channel, bands);
            if (_byPhysical.TryGetValue(physical, out var tile)) tile.SetLevel(level, peak);
        });
    }

    private void OnNotches(int slot, FkNotch[] notches)
    {
        var physical = _feedback.PhysicalForSlot(slot);
        if (physical < 0) return;
        var active = notches.Where(n => n.Active).Select(n => (n.FreqHz, n.CurrentDb)).ToArray();

        Dispatcher.UIThread.Post(() =>
        {
            if (_byPhysical.TryGetValue(physical, out var tile)) tile.SetNotches(active.Length);
            // markers are the union across armed channels; the tiles say who owns what
            _notchesBySlot[slot] = active;
            _spectrum.SetNotches(_notchesBySlot.Values.SelectMany(x => x));
        });
    }

    private readonly Dictionary<int, (float FreqHz, float CurrentDb)[]> _notchesBySlot = new();

    private void OnDetection(FkDetection d)
    {
        var physical = _feedback.PhysicalForSlot(d.Channel);
        var name = physical >= 0 ? _feedback.ChannelName(physical) : $"slot {d.Channel}";
        Dispatcher.UIThread.Post(() =>
        {
            _spectrum.AddCatch(d.Hz);
            if (physical >= 0 && _byPhysical.TryGetValue(physical, out var tile)) tile.FlagCatch();
            _lastCatch.Text = $"{Hz(d.Hz)}  ·  {d.LevelDb:0.#} dB on {name}";
            _lastCatch.Foreground = Tokens.Catch;
        });
    }

    private static string Hz(float hz) => hz >= 1000f ? $"{hz / 1000f:0.##} kHz" : $"{hz:0} Hz";

    private void RenderState()
    {
        var ok = _feedback.EngineOk;
        var bypassed = _feedback.IsBypassed;

        // Three states, three unmistakable looks - never a sentence to parse.
        var (text, colour, pulsing) = !ok
            ? ("ENGINE DOWN", Tokens.Clip, false)
            : bypassed
                ? ("GUARD OFF", (IBrush) Tokens.InkDim, false)
                : ("GUARD ON", Tokens.Accent, true);

        _guardText.Text = text;
        _guardText.Foreground = colour;
        _guardLed.Colour = colour;
        _guardLed.Pulsing = pulsing;
        _guardPill.BorderBrush = colour;
        _guardPill.Background = !ok ? Tokens.ClipSoft : bypassed ? Tokens.Panel2 : Tokens.AccentSoft;

        // One vocabulary, everywhere. The button says the SAME words as the pill so
        // the two can never appear to disagree; the small line says what a tap does.
        // Lit teal = protecting, dark = not. State first, action second.
        _bypass.IsLit = ok && !bypassed;
        _bypass.Label = !ok ? "ENGINE DOWN" : bypassed ? "GUARD OFF" : "GUARD ON";
        _bypass.Hint = bypassed ? "tap to protect" : "tap to bypass";
        _bypass.InvalidateVisual();

        _rigValue.Text = ok ? "running" : "down";
        _rigValue.Foreground = ok ? Tokens.Safe : Tokens.Clip;
    }

    /// <summary>Report the engine's CPU load (pushed by the window from telemetry).</summary>
    public void SetCpu(float load) => _cpuValue.Text = $"{load * 100f:0.0}%";

    private void RebuildTiles()
    {
        var armed = _feedback.EnabledInputs.ToArray();
        if (armed.SequenceEqual(_built))
        {
            foreach (var (physical, tile) in _byPhysical) tile.SetName(_feedback.ChannelName(physical));
            return;
        }
        _built = armed;

        _tiles.Children.Clear();
        _byPhysical.Clear();

        if (armed.Length == 0)
        {
            _tiles.Children.Add(EmptyHint());
            return;
        }

        foreach (var physical in armed)
        {
            var tile = new ChannelTile(_feedback.ChannelName(physical), $"CH {physical + 1}");
            _byPhysical[physical] = tile;
            _tiles.Children.Add(tile);
        }
    }

    private static Control EmptyHint()
    {
        var t = Ui.Text("No channels armed — open Setup and switch on the mics you want guarded.", 15, Tokens.InkDim);
        t.TextWrapping = TextWrapping.Wrap;
        t.MaxWidth = 460;
        return t;
    }

    /// <summary>Resample the engine's linear spectrum onto the display's log bands.</summary>
    private static float[] ToLogAxis(FkSpectrum spec)
    {
        var outp = new float[GuardSpectrum.Bands];
        for (var i = 0; i < GuardSpectrum.Bands; i++)
        {
            var hz = 20.0 * Math.Pow(1000.0, i / (double) (GuardSpectrum.Bands - 1));
            var bin = (int) Math.Round(hz / Math.Max(1f, spec.HzPerBin));
            outp[i] = bin >= 0 && bin < spec.Magnitudes.Length ? spec.Magnitudes[bin] : -120f;
        }
        return outp;
    }
}

/// <summary>
/// One armed channel, sized and contrasted to be read at four feet: the name in
/// display type, a meter, and - when the guard fires - the whole tile going magenta.
/// </summary>
public sealed class ChannelTile : Border
{
    private readonly TextBlock _name;
    private readonly TextBlock _sub;
    private readonly TextBlock _levelText = Ui.Mono("—", 11.5, Tokens.InkDim);
    private readonly TextBlock _notchText = Ui.Mono("0 notches", 11.5, Tokens.InkDim);
    private readonly LevelMeter _meter = new() { Height = 8 };
    private readonly Border _armDot;

    private long _caughtAt;

    public ChannelTile(string name, string sub)
    {
        Width = 168;
        Padding = new Thickness(14);
        CornerRadius = Tokens.RadiusLg;
        BorderThickness = new Thickness(1);
        Background = Tokens.Panel;
        BorderBrush = Tokens.AccentLine;

        _name = Ui.Text(name.ToUpperInvariant(), 17, Tokens.Ink, FontWeight.Bold);
        _name.FontFamily = Tokens.Display;
        _name.LetterSpacing = 0.6;
        _sub = Ui.Mono(sub, 11, Tokens.InkFaint);

        _armDot = new Border
        {
            Width = 12, Height = 12, CornerRadius = new CornerRadius(6),
            Background = Tokens.Accent,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top,
        };

        var header = new Grid();
        var titles = Ui.Stack(Orientation.Vertical, 2, _name, _sub);
        header.Children.Add(titles);
        header.Children.Add(_armDot);

        var foot = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        Grid.SetColumn(_levelText, 0);
        Grid.SetColumn(_notchText, 1);
        foot.Children.Add(_levelText);
        foot.Children.Add(_notchText);

        Child = Ui.Stack(Orientation.Vertical, 12, header, _meter, foot);
    }

    public void SetName(string name) => _name.Text = name.ToUpperInvariant();

    public void SetLevel(double level01, float peakDb)
    {
        _meter.SetTarget(level01);
        _levelText.Text = peakDb <= -79f ? "idle" : $"{peakDb:0.#} dB";
    }

    public void SetNotches(int count)
    {
        _notchText.Text = count == 1 ? "1 notch" : $"{count} notches";
        _notchText.Foreground = count > 0 ? Tokens.Catch : Tokens.InkDim;
    }

    public void FlagCatch() => _caughtAt = Environment.TickCount64;

    public void Tick()
    {
        _meter.Tick();

        // A catch holds the tile lit for two seconds - long enough to see from the
        // stage, short enough not to linger into the next song.
        var caught = _caughtAt != 0 && Environment.TickCount64 - _caughtAt < 2000;
        var target = caught ? Tokens.Catch : Tokens.AccentLine;
        if (!ReferenceEquals(BorderBrush, target))
        {
            BorderBrush = target;
            Background = caught ? Tokens.CatchSoft : Tokens.Panel;
            _armDot.Background = caught ? Tokens.Catch : Tokens.Accent;
        }
    }
}
