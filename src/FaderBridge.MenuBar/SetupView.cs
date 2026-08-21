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
    private readonly TextBlock _discCaret = Ui.Mono("▾", 13, Tokens.InkFaint);

    private readonly Dictionary<int, ChannelStrip> _strip = new();
    private int[] _built = Array.Empty<int>();
    private bool _syncing;
    private bool _discOpen;

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

        var disclosure = BuildDisclosure();

        var root = new DockPanel { Margin = new Thickness(20), LastChildFill = true };
        var top = Ui.Stack(Orientation.Vertical, 0, toolbar, _hint, header);
        DockPanel.SetDock(top, Dock.Top);
        DockPanel.SetDock(disclosure, Dock.Bottom);
        root.Children.Add(top);
        root.Children.Add(disclosure);
        root.Children.Add(new ScrollViewer
        {
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
            Content = _strips,
        });
        Content = root;

        _feedback.DevicesChanged += OnDevices;
        _feedback.ChannelsChanged += OnChannels;
        _feedback.SpectrumChanged += OnSpectrum;
        _feedback.NotchesChanged += OnNotches;

        RefreshDevices();
        RebuildStrips();
    }

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

    private void OnDevices() => Dispatcher.UIThread.Post(RefreshDevices);
    private void OnChannels() => Dispatcher.UIThread.Post(RebuildStrips);

    private void OnSpectrum(FkSpectrum spec)
    {
        var physical = _feedback.PhysicalForSlot(spec.Channel);
        if (physical < 0) return;
        var peak = float.NegativeInfinity;
        foreach (var m in spec.Magnitudes) if (m > peak) peak = m;
        var level = Math.Clamp((peak + 80f) / 80f, 0f, 1f);
        Dispatcher.UIThread.Post(() =>
        {
            if (_strip.TryGetValue(physical, out var s)) s.SetLevel(level);
        });
    }

    private void OnNotches(int slot, FkNotch[] notches)
    {
        var physical = _feedback.PhysicalForSlot(slot);
        if (physical < 0) return;
        var active = notches.Where(n => n.Active).Select(n => n.FreqHz).ToArray();
        Dispatcher.UIThread.Post(() =>
        {
            if (_strip.TryGetValue(physical, out var s)) s.SetNotches(active);
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
