using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using FeedbackFader;

using Fader.Shared.Ui;

namespace FeedbackFader.App;

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
    // What the mic is hearing, so the singer does not have to report it while
    // singing: how loud, what note, and whether that is a voice, a backing
    // track, or an empty room.
    private readonly TextBlock _sceneValue = Ui.Mono("—", 15, Tokens.InkDim, FontWeight.SemiBold);
    private readonly TextBlock _noteValue  = Ui.Mono("—", 15, Tokens.Ink, FontWeight.SemiBold);
    private readonly TextBlock _rigValue = Ui.Mono("—", 15, Tokens.Ink, FontWeight.SemiBold);

    private readonly GuardSpectrum _spectrum = new();
    private readonly WrapPanel _tiles = new() { Orientation = Orientation.Horizontal };
    private readonly TextBlock _lastCatch = Ui.Mono("nothing caught yet", 15, Tokens.InkDim, FontWeight.SemiBold);
    private readonly TapButton _bypass = new("Guard On", "tap to bypass");
    private readonly HoldButton _panic = new("Panic", "hold 1s · clears every notch", Tokens.Clip);
    // The desk bypass: the only thing this app ever writes on the X32 (README,
    // Rule Zero), so it is a hold, its hint names the exact channels it will
    // open and mute, and it is not on the screen at all until Setup names them.
    private readonly HoldButton _desk = new("Bypass to desk", "hold 1s", Tokens.Clip) { IsVisible = false };
    // Third action, and the only one that is not about the audio: it is how the
    // ear gets recorded. Pressed during rehearsal when a ring fires - especially
    // the ones heard before the detector reacts.
    private readonly TapButton _mark = new("That Was Feedback", "tap · saves the last 6 s");
    // Capture belongs here, not in Setup. It is something you switch on mid-show
    // because a song is misbehaving, and you need to see at a glance that it is
    // running while you are stood at the mic - not go hunting through soundcheck
    // screens for it and then have no idea whether it took.
    private readonly TapButton _capture = new("Capture", "tap · logs every catch")
    {
        LitColour = Tokens.CatchColor,   // recording, not protecting
    };

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
            Readout("Hearing", _sceneValue),
            Readout("Note", _noteValue),
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
        _spectrum.SetFloor(feedback.FloorDb);

        _bypass.Clicked += () => _feedback.SetBypass(!_feedback.IsBypassed);
        _capture.Clicked += () => _feedback.SetCapture(!_feedback.CaptureEnabled);
        _panic.Fired += () => _feedback.ClearAll(includeLocked: true);
        _desk.Fired += () => _ = SwapDeskAsync();
        _mark.Clicked += () =>
        {
            var saved = _feedback.MarkFeedback();
            _lastCatch.Text = saved is null ? "nothing to label yet"
                                            : $"labelled — {System.IO.Path.GetFileName(saved)}";
            _lastCatch.Foreground = Tokens.Accent;
        };

        var ribbon = Ui.Card(Ui.Stack(Orientation.Vertical, 4,
                Ui.Caption("Last catch"),
                _lastCatch),
            pad: 14);
        ribbon.CornerRadius = Tokens.RadiusLg;

        var actions = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto,Auto,Auto,Auto") };
        Grid.SetColumn(ribbon, 0);
        Grid.SetColumn(_mark, 1);
        _mark.Margin = new Thickness(14, 0, 0, 0);
        Grid.SetColumn(_capture, 2);
        _capture.Margin = new Thickness(14, 0, 0, 0);
        Grid.SetColumn(_bypass, 3);
        _bypass.Margin = new Thickness(14, 0, 0, 0);
        Grid.SetColumn(_desk, 4);
        _desk.Margin = new Thickness(14, 0, 0, 0);
        Grid.SetColumn(_panic, 5);
        _panic.Margin = new Thickness(14, 0, 0, 0);
        actions.Children.Add(ribbon);
        actions.Children.Add(_mark);
        actions.Children.Add(_capture);
        actions.Children.Add(_bypass);
        actions.Children.Add(_desk);
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
        _feedback.BypassChanged += OnStateFlag;
        _feedback.CaptureChanged += OnStateFlag;
        _feedback.DeskBypassChanged += OnDesk;
        _feedback.ConsoleAddressChanged += OnDesk;

        RebuildTiles();
        RenderState();
        // A read, so the button's first words are what the desk says, not a guess.
        if (_feedback.DeskBypassConfigured) _ = _feedback.RefreshDeskStateAsync();
    }

    /// <summary>
    /// The hold fired: swap the vocal between the guarded channels and their
    /// spares, in the direction the button said it would, and put the desk's own
    /// answer in the ribbon. Every write is also in the app log.
    /// </summary>
    private async Task SwapDeskAsync()
    {
        var toDesk = _feedback.DeskBypassState != DeskState.Bypassed;
        _lastCatch.Text = toDesk ? "desk: opening the bypass channels…" : "desk: opening the guarded channels…";
        _lastCatch.Foreground = Tokens.InkDim;
        var ok = await _feedback.SwapToDeskAsync(toDesk);
        _lastCatch.Text = _feedback.DeskMessage;
        _lastCatch.Foreground = ok ? Tokens.Accent : Tokens.Clip;
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
        _feedback.BypassChanged -= OnStateFlag;
        _feedback.CaptureChanged -= OnStateFlag;
        _feedback.DeskBypassChanged -= OnDesk;
        _feedback.ConsoleAddressChanged -= OnDesk;
    }

    private void OnDesk() => Dispatcher.UIThread.Post(RenderState);

    /// <summary>
    /// Named so Teardown can actually remove it. BypassChanged was subscribed with
    /// a lambda and never unsubscribed, so every rebuild of this view left another
    /// one behind, repainting a dead view on the next toggle. That exact pattern -
    /// a leaked subscription on this screen - has already crashed this app once.
    /// </summary>
    private void OnStateFlag(bool _) => Dispatcher.UIThread.Post(RenderState);

    /// <summary>Driven by the window's single animation timer.</summary>
    public void Tick()
    {
        _guardLed.Tick();
        _panic.Tick();
        _desk.Tick();
        foreach (var tile in _byPhysical.Values) tile.Tick();
        _spectrum.InvalidateVisual();
    }

    // Both edges AND the floor - Show mirrors the whole box, or the two screens
    // disagree about what is being policed.
    private void OnRange() => Dispatcher.UIThread.Post(() =>
    {
        _spectrum.SetRange(_feedback.MinHz, _feedback.MaxHz);
        _spectrum.SetFloor(_feedback.FloorDb);
    });

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
        // The desk says the vocal is on the spare channels: the guard may be on,
        // but nobody is hearing it. The pill must not pulse teal over that.
        var onDesk = _feedback.DeskBypassState == DeskState.Bypassed;

        // Four states, four unmistakable looks - never a sentence to parse.
        var (text, colour, pulsing) = !ok
            ? ("ENGINE DOWN", Tokens.Clip, false)
            : onDesk
                ? ("ON THE DESK", (IBrush) Tokens.InkDim, false)
                : bypassed
                    ? ("GUARD OFF", (IBrush) Tokens.InkDim, false)
                    : ("GUARD ON", Tokens.Accent, true);

        _guardText.Text = text;
        _guardText.Foreground = colour;
        _guardLed.Colour = colour;
        _guardLed.Pulsing = pulsing;
        _guardPill.BorderBrush = colour;
        _guardPill.Background = !ok ? Tokens.ClipSoft : (bypassed || onDesk) ? Tokens.Panel2 : Tokens.AccentSoft;

        // The desk swap exists on this screen only once Setup names the channels.
        // Rule Zero: the hint says exactly which channels a hold opens and mutes.
        var deskReady = _feedback.DeskBypassConfigured;
        _desk.IsVisible = deskReady;
        if (deskReady)
        {
            var guarded = string.Join("/", _feedback.DeskChannels);
            var spares = string.Join("/", _feedback.BypassChannels);
            _desk.Label = onDesk ? "Back to guard" : "Bypass to desk";
            _desk.Hint = onDesk
                ? $"hold 1s · opens Ch {guarded}, mutes Ch {spares}"
                : $"hold 1s · opens Ch {spares}, mutes Ch {guarded}";
        }

        // One vocabulary, everywhere. The button says the SAME words as the pill so
        // the two can never appear to disagree; the small line says what a tap does.
        // Lit teal = protecting, dark = not. State first, action second.
        _bypass.IsLit = ok && !bypassed;
        _bypass.Label = !ok ? "ENGINE DOWN" : bypassed ? "GUARD OFF" : "GUARD ON";
        _bypass.Hint = bypassed ? "tap to protect" : "tap to bypass";
        _bypass.InvalidateVisual();

        // Same vocabulary rule as the guard button: the state is the label, so it
        // can never be ambiguous whether it is recording. Lit = recording.
        var capturing = _feedback.CaptureEnabled;
        _capture.IsLit = capturing;
        _capture.Label = capturing ? "CAPTURING" : "CAPTURE";
        _capture.Hint = capturing ? "tap to stop · writing every catch" : "tap · logs every catch";
        _capture.InvalidateVisual();

        _rigValue.Text = ok ? "running" : "down";
        _rigValue.Foreground = ok ? Tokens.Safe : Tokens.Clip;
    }

    /// <summary>Report the engine's CPU load (pushed by the window from telemetry).</summary>
    public void SetCpu(float load) => _cpuValue.Text = $"{load * 100f:0.0}%";

    /// <summary>
    /// The room, in three words. Level tells you whether anything is happening,
    /// the scene whether it is one voice or several things at once, and the note
    /// is the fundamental the detector is currently protecting.
    /// </summary>
    private int _notInLoop;

    /// <summary>
    /// Filters cutting at maximum depth while their ring got LOUDER anyway.
    ///
    /// This is a wiring fault, not a feedback one, and it is worth shouting
    /// about because the symptom is indistinguishable from the software simply
    /// failing: the room howls, and the guard sits there reporting itself busy.
    /// Measured at the rig 2026-09-28 - a 7235 Hz ring grew 27 dB under a 45 dB
    /// notch, which no real loop can do. The console was not listening to our
    /// return, so every decibel we spent was tone damage in a fight we were not
    /// part of, and a session was abandoned before a note was sung.
    /// </summary>
    public void SetNotInLoop(int n)
    {
        if (n == _notInLoop) return;
        _notInLoop = n;
        if (n > 0) ShowWarning();
    }

    private void ShowWarning()
    {
        _sceneValue.Text = "NOT IN THE LOOP — check the return";
        _sceneValue.Foreground = Tokens.Catch;
    }

    public void SetContext(FeedbackFader.FkContext c)
    {
        // The wiring fault outranks anything we could say about the audio.
        if (_notInLoop > 0) { ShowWarning(); return; }

        _sceneValue.Text = c.Scene switch
        {
            "voice"  => $"VOICE  {c.LevelDb:0} dB",
            "music"  => $"MUSIC ({c.Families})  {c.LevelDb:0} dB",
            "room"   => $"room  {c.LevelDb:0} dB",
            _        => "silent",
        };
        _sceneValue.Foreground = c.Scene switch
        {
            "voice" => Tokens.Accent,
            "music" => Tokens.Catch,
            "room"  => Tokens.InkDim,
            _       => Tokens.InkFaint,
        };

        _noteValue.Text = c.Note.Length > 0 ? $"{c.Note}  {c.F0Hz:0} Hz" : "—";
        _noteValue.Foreground = c.Note.Length > 0 ? Tokens.Ink : Tokens.InkFaint;
    }

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
