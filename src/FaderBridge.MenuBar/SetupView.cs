using System.Net;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Fader.Bridge.Feedback;

namespace Fader.MenuBar;

/// <summary>
/// Setup mode: the screen you open at soundcheck and close before the set.
///
/// Same data as Show, opposite posture - calm, dense, honest. Pick the interface,
/// switch on the mics to guard, name them the way the band does, and the engine's
/// real detection spec sits behind one disclosure for anyone who wants to check it.
/// </summary>
public sealed class SetupView : UserControl
{
    private readonly FeedbackController _feedback;
    private readonly ComboBox _deviceBox = new() { MinWidth = 260, PlaceholderText = "Select audio device…" };
    private readonly StackPanel _strips = new() { Spacing = 0 };
    private readonly TextBlock _hint = Ui.Text("", 14, Tokens.InkDim);
    private readonly StackPanel _discBody = new() { Spacing = 10 };
    private readonly GuardSpectrum _band = new() { Height = 150, Editable = true };
    private readonly TextBlock _bandLabel = Ui.Mono("", 12.5, Tokens.InkDim);
    private readonly ComboBox _lowEdge = new() { MinWidth = 150 };
    private readonly Button _floorAuto;
    private readonly ComboBox _attack = new() { MinWidth = 130 };
    private readonly ComboBox _maxCut = new() { MinWidth = 130 };
    private bool _syncingEdge;

    // Named in what the operator is deciding, not in what it sets underneath.
    private static readonly string[] Attacks = { "Gentle", "Normal", "Fast" };
    private static readonly (string Label, float Db)[] Cuts =
    {
        ("Light — −18 dB", -18f),
        ("Normal — −24 dB", -24f),
        ("Deep — −30 dB", -30f),
    };

    // Somewhere to start without guessing. A vocal wedge rings above ~1 kHz, so
    // "1 kHz - vocal mic" is the one most rigs want.
    private static readonly (string Label, float Hz)[] LowEdges =
    {
        ("40 Hz — everything", 40f),
        ("200 Hz — default", 200f),
        ("500 Hz", 500f),
        ("800 Hz", 800f),
        ("1 kHz — vocal mic", 1000f),
        ("1.5 kHz", 1500f),
        ("2 kHz — HF only", 2000f),
    };
    private readonly TextBlock _discCaret = Ui.Mono("▾", 13, Tokens.InkFaint);

    private readonly StackPanel _notchList = new() { Spacing = 6 };
    private readonly TextBlock _pathState = Ui.Text("", 13, Tokens.InkDim);
    private readonly Button _pathCheck;
    private readonly TextBlock _ringOutState = Ui.Text("", 12.5, Tokens.InkFaint);
    private readonly Button _lockFound;
    private string _notchSignature = "";

    private readonly Dictionary<int, ChannelStrip> _strip = new();
    private int[] _built = Array.Empty<int>();
    private bool _syncing;
    private bool _discOpen;
    private IPAddress _consoleAddress = IPAddress.None;

    public SetupView(FeedbackController feedback)
    {
        _feedback = feedback;

        _deviceBox.SelectionChanged += (_, _) =>
        {
            if (_syncing) return;
            if (_deviceBox.SelectedItem is string device) _feedback.SetDevice(device);
        };

        var toolbar = Ui.Stack(Orientation.Horizontal, 18,
            Field("Audio device", _deviceBox),
            Field("Sample rate", Pill("48 kHz")),
            Field("Buffer", Pill("64")));
        toolbar.Margin = new Thickness(0, 0, 0, 16);

        var header = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions(ChannelStrip.Columns),
            Margin = new Thickness(10, 0, 10, 6),
        };
        AddCol(header, Ui.Caption("Arm"), 0);
        AddCol(header, Ui.Caption("Channel"), 1);
        AddCol(header, Ui.Caption("Input"), 2);
        AddCol(header, Ui.Caption("Return"), 3);
        AddCol(header, Ui.Caption("Level"), 4);
        var notchCap = Ui.Caption("Notches");
        notchCap.HorizontalAlignment = HorizontalAlignment.Right;
        AddCol(header, notchCap, 5);

        // Where the detector looks. Dragging the edges on the live spectrum is the
        // fix for a voice being notched - you can see your own energy sitting below
        // the low edge while you set it.
        _band.SetRange(_feedback.MinHz, _feedback.MaxHz);
        _floorAuto.Click += (_, _) => { _feedback.SetFloorAuto(!_feedback.FloorAuto); SyncFloorAuto(); };
        SyncFloorAuto();

        _band.RangeDragged += (lo, hi) =>
        {
            _bandLabel.Text = $"listening {Hz(lo)} – {Hz(hi)}";
            _feedback.SetSearchRange(lo, hi);
            SyncLowEdge();
        };
        _bandLabel.Text = $"listening {Hz(_feedback.MinHz)} – {Hz(_feedback.MaxHz)}";

        _lowEdge.ItemsSource = LowEdges.Select(e => e.Label).ToArray();
        SyncLowEdge();
        _lowEdge.SelectionChanged += (_, _) =>
        {
            if (_syncingEdge) return;
            var i = _lowEdge.SelectedIndex;
            if (i < 0 || i >= LowEdges.Length) return;
            _feedback.SetSearchRange(LowEdges[i].Hz, _feedback.MaxHz);
            _band.SetRange(_feedback.MinHz, _feedback.MaxHz);
            _bandLabel.Text = $"listening {Hz(_feedback.MinHz)} – {Hz(_feedback.MaxHz)}";
        };

        _band.SetFloor(_feedback.FloorDb);
        _band.FloorDragged += db =>
        {
            _feedback.SetFloor(db);
            _bandLabel.Text = $"listening {Hz(_feedback.MinHz)} – {Hz(_feedback.MaxHz)} above {db:0} dB";
        };

        _attack.ItemsSource = Attacks;
        _attack.SelectedIndex = _feedback.Attack;
        _attack.SelectionChanged += (_, _) =>
        {
            if (_syncingEdge || _attack.SelectedIndex < 0) return;
            _feedback.SetAttack(_attack.SelectedIndex);
        };

        _maxCut.ItemsSource = Cuts.Select(c => c.Label).ToArray();
        _maxCut.SelectedIndex = Array.FindIndex(Cuts, c => Math.Abs(c.Db - _feedback.MaxCutDb) < 0.5f) is var ci && ci >= 0 ? ci : 1;
        _maxCut.SelectionChanged += (_, _) =>
        {
            if (_syncingEdge || _maxCut.SelectedIndex < 0) return;
            _feedback.SetMaxCut(Cuts[_maxCut.SelectedIndex].Db);
        };

        var bandHead = Ui.Stack(Orientation.Horizontal, 12,
            Ui.Caption("Listen band"), _bandLabel);
        var bandBox = Ui.Stack(Orientation.Vertical, 8,
            bandHead,
            _band,
            Ui.Stack(Orientation.Horizontal, 16,
                Ui.Stack(Orientation.Vertical, 6, Ui.Caption("Low edge"), _lowEdge),
                Ui.Stack(Orientation.Vertical, 6, Ui.Caption("Attack"), _attack),
                Ui.Stack(Orientation.Vertical, 6, Ui.Caption("Max cut"), _maxCut)),
            Ui.Text("It cuts what lands inside the box: between the teal handles and above the dashed line. Drag any of the three.",
                    12.5, Tokens.InkFaint));
        bandBox.Margin = new Thickness(0, 0, 0, 16);

        _floorAuto = Ui.Small("", Tokens.Accent);
        _pathCheck = Ui.Small("Check signal path", Tokens.Accent);
        _lockFound = Ui.Small("Lock found filters", Tokens.Accent);
        var pathCard = BuildPathCheck();
        var ringOut = BuildRingOut();
        var filters = BuildFilterList();

        var disclosure = BuildDisclosure();

        var root = new DockPanel { Margin = new Thickness(20), LastChildFill = true };
        var top = Ui.Stack(Orientation.Vertical, 0, toolbar, bandBox, _hint, header);
        DockPanel.SetDock(top, Dock.Top);
        DockPanel.SetDock(disclosure, Dock.Bottom);
        root.Children.Add(top);
        root.Children.Add(disclosure);
        root.Children.Add(new ScrollViewer
        {
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
            Content = Ui.Stack(Orientation.Vertical, 18, _strips, pathCard, ringOut, filters),
        });
        Content = root;

        _feedback.SearchRangeChanged += () => Dispatcher.UIThread.Post(() =>
        {
            _band.SetRange(_feedback.MinHz, _feedback.MaxHz);
            _band.SetFloor(_feedback.FloorDb);
            _bandLabel.Text = $"listening {Hz(_feedback.MinHz)} – {Hz(_feedback.MaxHz)}";
            SyncFloorAuto();
        });
        _feedback.DevicesChanged += OnDevices;
        _feedback.ChannelsChanged += OnChannels;
        _feedback.SpectrumChanged += OnSpectrum;
        _feedback.NotchesChanged += OnNotches;

        RefreshDevices();
        RebuildStrips();
        RebuildNotchList();   // show the empty state before any telemetry arrives
    }

    /// <summary>Where the X32 lives, so the path check knows who to ask.</summary>
    public void SetConsoleAddress(IPAddress address) => _consoleAddress = address;

    public void Teardown()
    {
        _feedback.DevicesChanged -= OnDevices;
        _feedback.ChannelsChanged -= OnChannels;
        _feedback.SpectrumChanged -= OnSpectrum;
        _feedback.NotchesChanged -= OnNotches;
    }

    public void Tick()
    {
        foreach (var s in _strip.Values) s.Tick();
        _band.InvalidateVisual();
    }

    private static string Hz(float hz) => hz >= 1000f ? $"{hz / 1000f:0.##} kHz" : $"{hz:0} Hz";

    /// <summary>
    /// The floor is the one control here nobody can judge by eye, so it tracks the
    /// room by default and says so. Dragging it takes it off auto; this puts it back.
    /// </summary>
    private void SyncFloorAuto()
    {
        var auto = _feedback.FloorAuto;
        _floorAuto.Content = new TextBlock
        {
            Text = auto ? $"Auto — {Hz2(_feedback.FloorDb)}" : "Manual — tap for auto",
            FontSize = 12.5,
            Foreground = auto ? Tokens.Accent : Tokens.InkDim,
        };
    }

    private static string Hz2(float db) => $"{db:0} dB";

    /// <summary>Point the dropdown at whichever preset is closest to the real value.</summary>
    private void SyncLowEdge()
    {
        _syncingEdge = true;
        var best = 0;
        for (var i = 1; i < LowEdges.Length; i++)
            if (Math.Abs(LowEdges[i].Hz - _feedback.MinHz) < Math.Abs(LowEdges[best].Hz - _feedback.MinHz))
                best = i;
        _lowEdge.SelectedIndex = best;
        _syncingEdge = false;
    }

    private static void AddCol(Grid g, Control c, int col)
    {
        Grid.SetColumn(c, col);
        g.Children.Add(c);
    }

    private static Control Field(string label, Control control) =>
        Ui.Stack(Orientation.Vertical, 6, Ui.Caption(label), control);

    private static Control Pill(string text) => new Border
    {
        Background = Tokens.Panel,
        BorderBrush = Tokens.Line,
        BorderThickness = new Thickness(1),
        CornerRadius = Tokens.RadiusMd,
        Padding = new Thickness(14, 11),
        Child = Ui.Text(text, 15, Tokens.Ink, FontWeight.Medium),
    };

    /// <summary>
    /// The engine's real parameters, collapsed. Present for the curious and invisible
    /// for everyone else — density of control is not the same as depth of capability.
    /// </summary>
    private Control BuildDisclosure()
    {
        var head = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,Auto,*"), Margin = new Thickness(0, 0, 0, 0) };
        var title = Ui.Text("Detector — advanced", 15, Tokens.Ink, FontWeight.SemiBold);
        var hint = Ui.Text("good defaults · you'll rarely open this", 13, Tokens.InkFaint);
        hint.HorizontalAlignment = HorizontalAlignment.Right;
        Grid.SetColumn(_discCaret, 0);
        Grid.SetColumn(title, 1);
        title.Margin = new Thickness(12, 0, 0, 0);
        Grid.SetColumn(hint, 2);
        head.Children.Add(_discCaret);
        head.Children.Add(title);
        head.Children.Add(hint);

        var headBtn = new Button
        {
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(16, 14),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Content = head,
        };
        headBtn.Click += (_, _) => ToggleDisclosure();

        _discBody.Margin = new Thickness(16, 0, 16, 16);
        _discBody.IsVisible = false;
        var grid = new WrapPanel { Orientation = Orientation.Horizontal };
        foreach (var (label, value) in new[]
                 {
                     ("Search band", "200 Hz – 16 kHz"),
                     ("Prominence", "10 dB over floor"),
                     ("Stability", "±5 Hz / 32 ms"),
                     ("Growth", "30 dB / sec"),
                     ("Hold → bleed", "10 s, then 1.5 dB/s"),
                     ("Notch depth", "−12 → −30 dB, Q25"),
                 })
        {
            grid.Children.Add(SpecCard(label, value));
        }
        _discBody.Children.Add(grid);

        return new Border
        {
            Background = Tokens.Ground2,
            BorderBrush = Tokens.Line,
            BorderThickness = new Thickness(1),
            CornerRadius = Tokens.RadiusMd,
            Margin = new Thickness(0, 14, 0, 0),
            Child = Ui.Stack(Orientation.Vertical, 0, headBtn, _discBody),
        };
    }

    private void ToggleDisclosure()
    {
        _discOpen = !_discOpen;
        _discBody.IsVisible = _discOpen;
        _discCaret.Text = _discOpen ? "▴" : "▾";
    }

    private static Control SpecCard(string label, string value) => new Border
    {
        Width = 180,
        Background = Tokens.Panel,
        BorderBrush = Tokens.LineSoft,
        BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(9),
        Padding = new Thickness(13, 11),
        Child = Ui.Stack(Orientation.Vertical, 5,
            Ui.Caption(label),
            Ui.Mono(value, 14, Tokens.Ink, FontWeight.SemiBold)),
    };

    /// <summary>
    /// The check that would have saved a day of debugging: put a tone on the
    /// return and let the console's own meters say whether it arrived. When a
    /// Console mute takes the app out of the path, everything here still looks
    /// healthy - detections log, filters deploy - and nothing is cut.
    /// </summary>
    private Control BuildPathCheck()
    {
        _pathState.Text = "Put a tone on the return and see if the desk hears it.";
        _pathCheck.Click += async (_, _) =>
        {
            _pathCheck.IsEnabled = false;
            _pathState.Text = "Listening at the desk…";
            _pathState.Foreground = Tokens.InkDim;
            try
            {
                var result = await _feedback.CheckSignalPathAsync(_consoleAddress);
                _pathState.Text = result.Message;
                _pathState.Foreground = result.Reached ? Tokens.Safe : Tokens.Clip;
            }
            catch (Exception ex)
            {
                _pathState.Text = ex.Message;
                _pathState.Foreground = Tokens.Clip;
            }
            finally { _pathCheck.IsEnabled = true; }
        };

        return Ui.Card(Ui.Stack(Orientation.Vertical, 10,
            Ui.Caption("Signal path"),
            Ui.Stack(Orientation.Horizontal, 12, _pathCheck,
                Ui.Text("Soundcheck only — this puts a brief tone through the PA.",
                        12.5, Tokens.InkFaint)),
            _pathState), background: Tokens.Ground2);
    }

    /// <summary>
    /// Ring-out: the standard live practice, which the engine could already do but
    /// never offered. Clear the unlocked filters, push the monitors until the room
    /// rings, let it find the modes, then hold them - so the show starts with the
    /// real modes already notched instead of learning them during song one.
    /// </summary>
    private Control BuildRingOut()
    {
        var start = Ui.Small("Start ring-out");
        start.Click += (_, _) =>
        {
            _feedback.ClearAll(includeLocked: false);
            _ringOutState.Text = "listening — push the monitors until they ring";
            _ringOutState.Foreground = Tokens.Accent;
        };

        _lockFound.Click += (_, _) =>
        {
            _feedback.LockAll();
            _ringOutState.Text = "held — these filters now survive a restart";
            _ringOutState.Foreground = Tokens.Accent;
        };

        var unlock = Ui.Small("Release all");
        unlock.Click += (_, _) =>
        {
            _feedback.UnlockAll();
            _ringOutState.Text = "released — filters will fade out again on their own";
            _ringOutState.Foreground = Tokens.InkFaint;
        };

        _ringOutState.Text = "clear the filters, ring the room, then hold what it finds";

        return Ui.Card(Ui.Stack(Orientation.Vertical, 10,
            Ui.Caption("Ring out"),
            Ui.Stack(Orientation.Horizontal, 10, start, _lockFound, unlock),
            _ringOutState), background: Tokens.Ground2);
    }

    private Control BuildFilterList() =>
        Ui.Card(Ui.Stack(Orientation.Vertical, 10,
            Ui.Caption("Filters in place"),
            _notchList), background: Tokens.Ground2);

    /// <summary>
    /// One row per deployed filter, so a wrong grab can be removed on its own
    /// instead of reaching for Panic and losing every good filter with it.
    /// </summary>
    private void RebuildNotchList()
    {
        var rows = new List<(int Slot, int Index, string Name, FkNotch N)>();
        foreach (var physical in _feedback.EnabledInputs)
        {
            var slot = _feedback.EnabledInputs.ToList().IndexOf(physical);
            foreach (var (index, n) in _feedback.ActiveNotches(slot))
                rows.Add((slot, index, _feedback.ChannelName(physical), n));
        }

        var sig = string.Join(";", rows.Select(r => $"{r.Slot}:{r.Index}:{(int) r.N.FreqHz}:{r.N.Locked}"));
        if (sig == _notchSignature) return;      // same filters: don't churn the UI
        _notchSignature = sig;

        _notchList.Children.Clear();
        _lockFound.Content = new TextBlock
        {
            Text = rows.Count == 0 ? "Lock found filters" : $"Lock {rows.Count} found",
            FontSize = 12.5, Foreground = Tokens.Accent,
        };

        if (rows.Count == 0)
        {
            _notchList.Children.Add(Ui.Text("Nothing deployed. Filters appear here as feedback is caught.",
                12.5, Tokens.InkFaint));
            return;
        }

        foreach (var row in rows.OrderBy(r => r.N.FreqHz))
        {
            _notchList.Children.Add(BuildNotchRow(row.Slot, row.Index, row.Name, row.N));
        }
    }

    private Control BuildNotchRow(int slot, int index, string channel, FkNotch n)
    {
        var hold = Ui.Small(n.Locked ? "Held" : "Hold", n.Locked ? Tokens.Accent : Tokens.InkDim);
        hold.Click += (_, _) => _feedback.LockNotch(slot, index, !n.Locked);

        var kill = Ui.Small("Remove", Tokens.Clip);
        kill.Click += (_, _) => _feedback.RemoveNotch(slot, index);

        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("1.1*,90,80,Auto,Auto") };
        void Add(Control c, int col, double left = 0)
        {
            Grid.SetColumn(c, col);
            c.VerticalAlignment = VerticalAlignment.Center;
            c.Margin = new Thickness(left, 0, 0, 0);
            grid.Children.Add(c);
        }
        Add(Ui.Text(channel, 13.5, Tokens.Ink, FontWeight.Medium), 0);
        Add(Ui.Mono(Hz(n.FreqHz), 13.5, Tokens.Catch, FontWeight.SemiBold), 1, 10);
        Add(Ui.Mono($"{n.CurrentDb:0.#} dB", 13, Tokens.InkDim), 2, 10);
        Add(hold, 3, 10);
        Add(kill, 4, 8);

        return new Border
        {
            Background = n.Locked ? Tokens.AccentSoft : Tokens.Panel,
            BorderBrush = n.Locked ? Tokens.AccentLine : Tokens.LineSoft,
            BorderThickness = new Thickness(1),
            CornerRadius = Tokens.RadiusMd,
            Padding = new Thickness(10, 7),
            Child = grid,
        };
    }

    private void OnDevices() => Dispatcher.UIThread.Post(RefreshDevices);
    private void OnChannels() => Dispatcher.UIThread.Post(RebuildStrips);

    private void OnSpectrum(FkSpectrum spec)
    {
        var physical = _feedback.PhysicalForSlot(spec.Channel);
        if (physical < 0) return;
        var peak = float.NegativeInfinity;
        foreach (var m in spec.Magnitudes) if (m > peak) peak = m;
        var level = Math.Clamp((peak + 80f) / 80f, 0f, 1f);
        var bands = ToLogAxis(spec);
        Dispatcher.UIThread.Post(() =>
        {
            if (_strip.TryGetValue(physical, out var s)) s.SetLevel(level);
            _band.SetSlot(spec.Channel, bands);
        });
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

    private void OnNotches(int slot, FkNotch[] notches)
    {
        var physical = _feedback.PhysicalForSlot(slot);
        if (physical < 0) return;
        var active = notches.Where(n => n.Active).Select(n => n.FreqHz).ToArray();
        Dispatcher.UIThread.Post(() =>
        {
            if (_strip.TryGetValue(physical, out var s)) s.SetNotches(active);
            RebuildNotchList();
        });
    }

    private void RefreshDevices()
    {
        _syncing = true;
        _deviceBox.ItemsSource = _feedback.Devices;
        _deviceBox.SelectedItem = _feedback.CurrentDevice;
        _syncing = false;
    }

    private void RebuildStrips()
    {
        var channels = _feedback.InputChannels
            .Where(c => !c.Name.StartsWith("NONE", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        var indices = channels.Select(c => c.Index).ToArray();

        _hint.Text = channels.Length == 0
            ? "Pick an audio device above to see its input channels."
            : "Switch on the mics to guard; Return is the output the cut signal is sent back on. Double-click a name to rename.";
        _hint.Margin = new Thickness(0, 0, 0, 14);

        if (indices.SequenceEqual(_built))
        {
            var outs = _feedback.OutputChannels;
            foreach (var (physical, strip) in _strip)
            {
                strip.SyncArm(_feedback.IsInputEnabled(physical));
                strip.SetName(_feedback.ChannelName(physical));
                strip.SyncReturn(outs, _feedback.ReturnFor(physical));
            }
            return;
        }
        _built = indices;

        _strips.Children.Clear();
        _strip.Clear();
        if (channels.Length == 0) return;

        foreach (var (index, hardwareName) in channels)
        {
            var strip = new ChannelStrip(index, _feedback.ChannelName(index), hardwareName);
            strip.SyncArm(_feedback.IsInputEnabled(index));
            strip.SyncReturn(_feedback.OutputChannels, _feedback.ReturnFor(index));
            strip.ArmToggled += on => _feedback.SetInputEnabled(index, on);
            strip.Renamed += name => _feedback.SetChannelName(index, name);
            strip.ReturnChanged += output => _feedback.SetReturn(index, output);
            _strip[index] = strip;
            _strips.Children.Add(strip);
        }
    }
}

/// <summary>One input channel as a row: arm, name, hardware input, level, notches.</summary>
public sealed class ChannelStrip : Border
{
    public const string Columns = "62,1.1*,1.1*,1.0*,1.3*,Auto";

    private readonly ArmSwitch _arm = new();
    private readonly TextBox _nameBox;
    private readonly TextBlock _nameText;
    private readonly LevelMeter _meter = new() { Height = 9, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _notches = Ui.Mono("—", 12.5, Tokens.InkDim);
    private readonly ComboBox _return = new() { FontSize = 12.5, MinWidth = 110 };
    private (int Index, string Name)[] _outs = Array.Empty<(int, string)>();
    private bool _syncingReturn;

    public ChannelStrip(int physical, string name, string hardwareName)
    {
        Padding = new Thickness(10, 10);
        CornerRadius = Tokens.RadiusMd;
        BorderThickness = new Thickness(0, 0, 0, 1);
        BorderBrush = Tokens.LineSoft;

        _arm.Toggled += on => { ArmToggled?.Invoke(on); Restyle(); };

        _nameText = Ui.Text(name, 16, Tokens.Ink, FontWeight.SemiBold);
        _nameBox = new TextBox
        {
            Text = name, FontSize = 16, IsVisible = false,
            Background = Tokens.Ground, BorderBrush = Tokens.Accent,
            Padding = new Thickness(6, 3),
        };
        _nameText.DoubleTapped += (_, _) => BeginRename();
        _nameBox.LostFocus += (_, _) => CommitRename();
        _nameBox.KeyDown += (_, e) =>
        {
            if (e.Key == Avalonia.Input.Key.Enter) CommitRename();
            if (e.Key == Avalonia.Input.Key.Escape) { _nameBox.Text = _nameText.Text; CommitRename(); }
        };

        var nameCell = new Panel();
        nameCell.Children.Add(_nameText);
        nameCell.Children.Add(_nameBox);

        var input = Ui.Mono(hardwareName, 12.5, Tokens.InkFaint);
        input.VerticalAlignment = VerticalAlignment.Center;
        input.TextTrimming = TextTrimming.CharacterEllipsis;

        _notches.HorizontalAlignment = HorizontalAlignment.Right;
        _notches.VerticalAlignment = VerticalAlignment.Center;
        _notches.MinWidth = 140;

        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions(Columns) };
        Add(grid, _arm, 0);
        nameCell.Margin = new Thickness(14, 0, 0, 0);
        Add(grid, nameCell, 1);
        input.Margin = new Thickness(14, 0, 0, 0);
        Add(grid, input, 2);
        _return.Margin = new Thickness(14, 0, 0, 0);
        _return.SelectionChanged += (_, _) =>
        {
            if (_syncingReturn) return;
            if (_return.SelectedIndex >= 0 && _return.SelectedIndex < _outs.Length)
                ReturnChanged?.Invoke(_outs[_return.SelectedIndex].Index);
        };
        Add(grid, _return, 3);
        _meter.Margin = new Thickness(14, 0, 14, 0);
        Add(grid, _meter, 4);
        Add(grid, _notches, 5);
        Child = grid;

        Restyle();
    }

    public event Action<bool>? ArmToggled;
    public event Action<string>? Renamed;
    public event Action<int>? ReturnChanged;

    /// <summary>Populate the return picker with the device's outputs and select this channel's.</summary>
    public void SyncReturn(IReadOnlyList<(int Index, string Name)> outputs, int selected)
    {
        _syncingReturn = true;
        var outs = outputs.ToArray();
        if (!outs.SequenceEqual(_outs))
        {
            _outs = outs;
            _return.ItemsSource = outs.Select(o => o.Name).ToArray();
        }
        var sel = Array.FindIndex(_outs, o => o.Index == selected);
        _return.SelectedIndex = sel;
        _return.PlaceholderText = sel < 0 ? $"Out {selected + 1}" : null;
        _syncingReturn = false;
    }

    private static void Add(Grid g, Control c, int col)
    {
        Grid.SetColumn(c, col);
        c.VerticalAlignment = VerticalAlignment.Center;
        g.Children.Add(c);
    }

    public void SyncArm(bool on)
    {
        _arm.SetSilently(on);
        Restyle();
    }

    public void SetName(string name)
    {
        if (!_nameBox.IsVisible) _nameText.Text = name;
    }

    public void SetLevel(double level01) => _meter.SetTarget(level01);

    public void SetNotches(float[] hz)
    {
        if (hz.Length == 0)
        {
            _notches.Text = "—";
            _notches.Foreground = Tokens.InkFaint;
            return;
        }
        var list = string.Join(" ", hz.OrderBy(x => x).Take(3).Select(Hz));
        _notches.Text = hz.Length > 3 ? $"{hz.Length} · {list} …" : $"{hz.Length} · {list}";
        _notches.Foreground = Tokens.Catch;
    }

    private static string Hz(float hz) => hz >= 1000f ? $"{hz / 1000f:0.#}k" : $"{hz:0}";

    public void Tick()
    {
        _arm.Tick();
        _meter.Tick();
    }

    private void BeginRename()
    {
        _nameBox.Text = _nameText.Text;
        _nameBox.IsVisible = true;
        _nameText.IsVisible = false;
        _nameBox.Focus();
        _nameBox.SelectAll();
    }

    private void CommitRename()
    {
        if (!_nameBox.IsVisible) return;
        _nameBox.IsVisible = false;
        _nameText.IsVisible = true;
        var text = _nameBox.Text ?? "";
        if (text != _nameText.Text)
        {
            _nameText.Text = text;
            Renamed?.Invoke(text);
        }
    }

    private void Restyle()
    {
        var on = _arm.IsOn;
        Background = on ? Tokens.AccentSoft : Brushes.Transparent;
        _nameText.Foreground = on ? Tokens.Ink : Tokens.InkDim;
        _meter.Muted = !on;
    }
}
