using System.Net;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using FeedbackFader;

using Fader.Shared.Ui;

namespace FeedbackFader.App;

/// <summary>
/// Setup, in three tabs (rebuilt 2026-10-09 from the mock-up Chris approved):
///
///   Audio device  - the interface, the console, the desk bypass, the path check.
///   Levels        - EVERY input the device has, each with a live meter and an
///                   Arm button. Sing; the input that moves is your microphone.
///   Inputs        - only the armed channels: name, Return (None = listen only),
///                   level, notches; then the guard settings, ring-out, capture
///                   and the filters in place.
///
/// A status strip above the tabs says the same thing on every tab: engine, build,
/// ASSIST or GUARD, how many are armed and how many are in the audio path.
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
    private readonly Button _floorAuto = Ui.Small("", Tokens.Accent);
    private readonly ComboBox _attack = new() { MinWidth = 130 };
    private readonly ComboBox _maxCut = new() { MinWidth = 130 };
    private readonly ComboBox _budget = new() { MinWidth = 190 };
    private readonly TextBox _console = new() { MinWidth = 170, Watermark = "192.168.1.100" };
    // The one-click desk bypass: which X32 channels the guarded mics land on and
    // which muted spares carry the same mics straight from the Console. Blank
    // keeps the feature hidden and the app read-only on the desk (README, Rule Zero).
    private readonly TextBox _deskCh = new() { MinWidth = 90, Watermark = "1 2" };
    private readonly TextBox _bypassCh = new() { MinWidth = 90, Watermark = "11 12" };
    private bool _syncingEdge;

    // Named in what the operator is deciding, not in what it sets underneath.
    private static readonly string[] Attacks = { "Gentle", "Normal", "Fast" };
    private static readonly (string Label, float Db)[] Cuts =
    {
        ("Light — −18 dB", -18f),
        ("Normal — −24 dB", -24f),
        ("Deep — −30 dB", -30f),
    };

    // How much of the singer the guard may spend, in ear-weighted dB-ERB: depth x
    // width-in-ear-bandwidths x how much that frequency matters. Depth alone was
    // never the unit - the same 100 Hz notch costs 1.4 ear-bandwidths at 300 Hz
    // and 0.12 at 8 kHz.
    //
    // Measured on 135 s of singing with a ring building at 20 dB/s (the rate the
    // rig produced): no ceiling removed 209 units of voice, a ceiling of 30
    // removed 30 - and killed the ring just as completely.
    private static readonly (string Label, float Budget)[] Budgets =
    {
        ("Protect the voice — 15", 15f),
        ("Recommended — 30",       30f),
        ("Balanced — 60",          60f),
        ("Loose — 120",           120f),
        ("No limit (old default)",  0f),
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
    private readonly Button _pathCheck = Ui.Small("Check signal path", Tokens.Accent);
    private readonly Button _capture = Ui.Small("Start capture", Tokens.InkDim);
    private readonly TextBlock _captureState = Ui.Text("", 12.5, Tokens.InkFaint);
    private readonly TextBlock _ringOutState = Ui.Text("", 12.5, Tokens.InkFaint);
    private readonly Button _lockFound = Ui.Small("Lock found filters", Tokens.Accent);
    private string _notchSignature = "";

    private readonly Dictionary<int, ChannelStrip> _strip = new();
    private int[] _built = Array.Empty<int>();
    private bool _syncing;
    private bool _discOpen;
    private IPAddress _consoleAddress = IPAddress.None;

    // ---- the three tabs and the strip above them --------------------------
    private const int TabDevice = 0, TabLevels = 1, TabInputs = 2;
    private static int s_lastTab = -1;                 // survives the window being closed and reopened
    private int _tab = -1;
    private readonly Control[] _pages = new Control[3];
    private readonly Border[] _tabPills = new Border[3];
    private readonly TextBlock[] _tabLabels = new TextBlock[3];
    private readonly Led _statusLed = new(Tokens.Safe, 9);
    private readonly TextBlock _statusText = Ui.Mono("", 12, Tokens.InkDim);
    private readonly TextBlock _deviceInfo = Ui.Mono("", 12.5, Tokens.InkDim);
    private readonly TextBlock _consoleState = Ui.Text("", 12.5, Tokens.InkDim);
    private readonly TextBlock _deskState = Ui.Mono("", 11, Tokens.InkDim);
    private readonly TextBlock _inputsEmpty = Ui.Text("", 13, Tokens.InkDim);
    private readonly StackPanel _levelRows = new() { Spacing = 2 };
    private readonly List<LevelRow> _levelRowList = new();
    private int[] _levelsBuilt = Array.Empty<int>();
    private int _frame;

    public SetupView(FeedbackController feedback)
    {
        _feedback = feedback;

        _deviceBox.SelectionChanged += (_, _) =>
        {
            if (_syncing) return;
            if (_deviceBox.SelectedItem is string device) _feedback.SetDevice(device);
        };

        // Which console to ask. Only the RTA overlay and the signal-path check
        // need it, and both say so plainly when it is blank - so this is a field,
        // not a wizard. The app also searches the LAN for it at start-up.
        _console.Text = _feedback.ConsoleAddress ?? "";
        SyncConsole();
        _console.LostFocus += (_, _) => CommitConsole();
        _console.KeyDown += (_, e) =>
        {
            if (e.Key == Avalonia.Input.Key.Enter) CommitConsole();
        };

        _deskCh.Text = string.Join(" ", _feedback.DeskChannels);
        _bypassCh.Text = string.Join(" ", _feedback.BypassChannels);
        foreach (var box in new[] { _deskCh, _bypassCh })
        {
            box.LostFocus += (_, _) => CommitDesk();
            box.KeyDown += (_, e) =>
            {
                if (e.Key == Avalonia.Input.Key.Enter) CommitDesk();
            };
        }

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

        _budget.ItemsSource = Budgets.Select(b => b.Label).ToArray();
        _budget.SelectedIndex = Array.FindIndex(Budgets, b => Math.Abs(b.Budget - _feedback.HarmBudget) < 0.5f)
                                is var bi && bi >= 0 ? bi : Budgets.Length - 1;
        _budget.SelectionChanged += (_, _) =>
        {
            if (_syncingEdge || _budget.SelectedIndex < 0) return;
            _feedback.SetHarmBudget(Budgets[_budget.SelectedIndex].Budget);
        };

        // The three pages.
        _pages[TabDevice] = BuildDevicePage();
        _pages[TabLevels] = BuildLevelsPage();
        _pages[TabInputs] = BuildInputsPage();

        var pages = new Panel();
        foreach (var p in _pages) pages.Children.Add(p);

        var root = new DockPanel { Margin = new Thickness(20, 14, 20, 20), LastChildFill = true };
        var top = Ui.Stack(Orientation.Vertical, 12, BuildStatusStrip(), BuildTabStrip());
        top.Margin = new Thickness(0, 0, 0, 16);
        DockPanel.SetDock(top, Dock.Top);
        root.Children.Add(top);
        root.Children.Add(pages);
        Content = root;

        _feedback.SearchRangeChanged += OnSearchRange;
        _feedback.DevicesChanged += OnDevices;
        _feedback.ChannelsChanged += OnChannels;
        _feedback.SpectrumChanged += OnSpectrum;
        _feedback.NotchesChanged += OnNotches;
        _feedback.ConsoleAddressChanged += OnConsoleAddress;
        _feedback.EngineOkChanged += OnEngineOk;
        _feedback.BypassChanged += OnEngineOk;
        _feedback.DeskBypassChanged += OnDesk;

        RefreshDevices();
        RebuildStrips();
        RebuildLevelRows();
        RebuildNotchList();   // show the empty state before any telemetry arrives
        RenderStatus();

        // Open where the work is: Inputs when something is armed, else the device.
        SelectTab(s_lastTab >= 0 ? s_lastTab : _feedback.EnabledInputs.Count > 0 ? TabInputs : TabDevice);
    }

    // ---- the strip and the tabs --------------------------------------------

    private Control BuildStatusStrip()
    {
        var pill = new Border
        {
            Background = Tokens.Panel,
            BorderBrush = Tokens.Line,
            BorderThickness = new Thickness(1),
            CornerRadius = Tokens.Pill,
            Padding = new Thickness(12, 6),
            HorizontalAlignment = HorizontalAlignment.Left,
            Child = Ui.Stack(Orientation.Horizontal, 10, _statusLed, _statusText),
        };
        _statusLed.VerticalAlignment = VerticalAlignment.Center;
        _statusText.VerticalAlignment = VerticalAlignment.Center;
        return pill;
    }

    private Control BuildTabStrip()
    {
        var names = new[] { "Audio device", "Levels", "Inputs" };
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
        for (var i = 0; i < 3; i++)
        {
            var label = Ui.Text(names[i], 14, Tokens.InkDim, FontWeight.SemiBold);
            label.FontFamily = Tokens.Display;
            var pill = new Border
            {
                Padding = new Thickness(14, 9),
                BorderThickness = new Thickness(0, 0, 0, 2),
                BorderBrush = Brushes.Transparent,
                Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand),
                Child = label,
            };
            var index = i;
            pill.PointerPressed += (_, _) => SelectTab(index);
            _tabPills[i] = pill;
            _tabLabels[i] = label;
            row.Children.Add(pill);
        }
        return new Border
        {
            BorderBrush = Tokens.Line,
            BorderThickness = new Thickness(0, 0, 0, 1),
            Child = row,
        };
    }

    private void SelectTab(int tab)
    {
        _tab = tab;
        s_lastTab = tab;
        for (var i = 0; i < 3; i++)
        {
            var on = i == tab;
            _pages[i].IsVisible = on;
            _tabPills[i].BorderBrush = on ? Tokens.Accent : Brushes.Transparent;
            _tabLabels[i].Foreground = on ? Tokens.Accent : Tokens.InkDim;
        }
        if (tab == TabLevels) RebuildLevelRows();
    }

    /// <summary>
    /// One sentence that is true on every tab: engine, build, what the guard is
    /// doing, how many mics are armed, how many are actually in the audio path.
    /// </summary>
    private void RenderStatus()
    {
        var alive = _feedback.ProcessAlive;
        var ok = _feedback.EngineOk;
        var armed = _feedback.EnabledInputs.Count;
        var inPath = _feedback.InPathCount;
        var build = _feedback.EngineBuild is { Length: > 0 } b ? b.Split(' ')[0] : "build ?";
        var mode = !alive ? "ENGINE DOWN"
                 : !ok ? "NO AUDIO INTERFACE"
                 : armed == 0 ? "NOTHING ARMED"
                 : !_feedback.InPath ? "ASSIST"
                 : _feedback.IsBypassed ? "GUARD OFF"
                 : "GUARD ON";
        _statusText.Text = $"engine {(ok ? "running" : alive ? "up, no audio" : "down")} · {build} · {mode} · {armed} armed · {inPath} in the audio path";
        _statusLed.Colour = !alive || !ok ? Tokens.Clip : armed == 0 ? Tokens.InkDim : Tokens.Safe;
        _statusLed.Pulsing = ok && armed > 0 && _feedback.InPath && !_feedback.IsBypassed;

        var ins = _feedback.InputChannels.Count;
        var outs = _feedback.OutputChannels.Count;
        _deviceInfo.Text = ins == 0 ? "no device open" : $"{ins} inputs · {outs} outputs · 48 kHz · 64 samples (1.3 ms)";

        _consoleState.Text = _feedback.ConsoleAddress is null
            ? "Not set. The app searches the LAN for the desk at start-up; type the address only if that finds nothing."
            : "Set. The RTA overlay and the meters read from it; the app writes nothing to the desk.";

        // In assist (no Return anywhere) there is nowhere to put the tone. Say so
        // instead of letting the check report a failure that is not one.
        var canTone = _feedback.InPath;
        _pathCheck.IsEnabled = canTone;
        if (!canTone)
        {
            _pathState.Text = "No Return is set, so there is no tone to send. Choose a Return on Inputs first.";
            _pathState.Foreground = Tokens.InkDim;
        }

        var desk = _feedback.DeskBypassConfigured;
        _deskState.Text = desk ? "ON · BYPASS TO DESK IS ON SHOW" : "OFF · BLANK KEEPS THE BUTTON OFF SHOW";
        _deskState.Foreground = desk ? Tokens.Accent : Tokens.InkDim;
    }

    // ---- tab 1: the audio device --------------------------------------------

    private Control BuildDevicePage()
    {
        var deviceRow = Ui.Stack(Orientation.Horizontal, 18,
            Field("Device", _deviceBox),
            Field("Sample rate", Pill("48 kHz")),
            Field("Buffer", Pill("64")));
        var deviceCard = Ui.Card(Ui.Stack(Orientation.Vertical, 12,
            Ui.Caption("Audio device"),
            deviceRow,
            _deviceInfo));

        var found = Ui.Stack(Orientation.Horizontal, 12, Field("Address", _console));
        var consoleCard = Ui.Card(Ui.Stack(Orientation.Vertical, 12,
            Ui.Caption("Console (X32)"),
            found,
            _consoleState,
            BuildPathCheck()));

        var deskHead = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        var deskCap = Ui.Caption("Desk bypass");
        Grid.SetColumn(deskCap, 0);
        Grid.SetColumn(_deskState, 1);
        deskHead.Children.Add(deskCap);
        deskHead.Children.Add(_deskState);
        var deskCard = Ui.Card(Ui.Stack(Orientation.Vertical, 12,
            deskHead,
            Ui.Stack(Orientation.Horizontal, 14,
                Field("Desk channels", _deskCh),
                Field("Bypass channels", _bypassCh)),
            Wrapped("The one thing the app can write on the desk: a mute swap, from a one-second hold on Show. Blank keeps that button off the screen.",
                    12.5, Tokens.InkDim)));

        var twoUp = new Grid { ColumnDefinitions = new ColumnDefinitions("*,*") };
        Grid.SetColumn(consoleCard, 0);
        Grid.SetColumn(deskCard, 1);
        deskCard.Margin = new Thickness(14, 0, 0, 0);
        twoUp.Children.Add(consoleCard);
        twoUp.Children.Add(deskCard);

        var rule = new Border
        {
            Background = Tokens.Ground2,
            BorderBrush = Tokens.LineSoft,
            BorderThickness = new Thickness(1),
            CornerRadius = Tokens.RadiusMd,
            Padding = new Thickness(16, 12),
            Child = Ui.Stack(Orientation.Horizontal, 12,
                new Led(Tokens.Accent, 8) { VerticalAlignment = VerticalAlignment.Center },
                Wrapped("Rule Zero. This screen never changes a Console or X32 setting. What the engine listens to, and where it returns, is on the Inputs tab.",
                        13, Tokens.InkDim, VerticalAlignment.Center)),
        };

        return new ScrollViewer
        {
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
            Content = Ui.Stack(Orientation.Vertical, 14, deviceCard, twoUp, rule),
        };
    }

    // ---- tab 2: levels on every input -----------------------------------------

    private Control BuildLevelsPage()
    {
        var hint = Ui.Text("Sing. The input that moves is your microphone. Arm it here or on Inputs; an armed input listens only until a Return is chosen.",
                           14, Tokens.InkDim);
        hint.TextWrapping = TextWrapping.Wrap;
        hint.Margin = new Thickness(0, 0, 0, 12);

        var header = new Grid { ColumnDefinitions = new ColumnDefinitions(LevelRow.Columns), Margin = new Thickness(10, 0, 10, 6) };
        AddCol(header, Ui.Caption("#"), 0);
        AddCol(header, Ui.Caption("Input"), 1);
        AddCol(header, Ui.Caption("Name"), 2);
        AddCol(header, Ui.Caption("Level · peak held 2 s"), 3);
        var dbCap = Ui.Caption("dB");
        dbCap.HorizontalAlignment = HorizontalAlignment.Right;
        AddCol(header, dbCap, 4);
        var armCap = Ui.Caption("Arm");
        armCap.HorizontalAlignment = HorizontalAlignment.Right;
        AddCol(header, armCap, 5);

        var card = Ui.Card(Ui.Stack(Orientation.Vertical, 4, header, _levelRows), background: Tokens.Ground2, pad: 12);

        var footer = Ui.Text("The meters are what the engine hears, read from the same stream it analyses. Nothing here touches an output.",
                             12.5, Tokens.InkFaint);
        footer.Margin = new Thickness(0, 12, 0, 0);

        var root = new DockPanel { LastChildFill = true };
        DockPanel.SetDock(hint, Dock.Top);
        DockPanel.SetDock(footer, Dock.Bottom);
        root.Children.Add(hint);
        root.Children.Add(footer);
        root.Children.Add(new ScrollViewer
        {
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
            Content = card,
        });
        return root;
    }

    /// <summary>One row per device input, in device order, with a caption where the family changes.</summary>
    private void RebuildLevelRows()
    {
        var channels = _feedback.InputChannels
            .Where(c => !c.Name.StartsWith("NONE", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        var indices = channels.Select(c => c.Index).ToArray();

        if (indices.SequenceEqual(_levelsBuilt))
        {
            foreach (var row in _levelRowList)
                row.SyncArm(_feedback.IsInputEnabled(row.Index), _feedback.ChannelName(row.Index), _feedback.InputChannels.FirstOrDefault(c => c.Index == row.Index).Name);
            return;
        }
        _levelsBuilt = indices;

        _levelRows.Children.Clear();
        _levelRowList.Clear();
        if (channels.Length == 0)
        {
            _levelRows.Children.Add(Ui.Text("Pick an audio device first.", 13, Tokens.InkDim));
            return;
        }

        string? group = null;
        foreach (var (index, hardwareName) in channels)
        {
            var g = GroupOf(hardwareName);
            if (g != group)
            {
                group = g;
                var cap = Ui.Caption(g);
                cap.Margin = new Thickness(10, 10, 0, 2);
                _levelRows.Children.Add(cap);
            }
            var row = new LevelRow(index, hardwareName);
            row.SyncArm(_feedback.IsInputEnabled(index), _feedback.ChannelName(index), hardwareName);
            row.ArmClicked += () => _feedback.SetInputEnabled(index, !_feedback.IsInputEnabled(index));
            _levelRowList.Add(row);
            _levelRows.Children.Add(row);
        }
    }

    /// <summary>Which family an input belongs to, from the name the device gives it.</summary>
    private static string GroupOf(string name)
    {
        var u = name.ToUpperInvariant();
        if (u.Contains("ADAT")) return "ADAT";
        if (u.Contains("SPDIF") || u.Contains("S/PDIF") || u.Contains("AES")) return "S/PDIF · AES";
        if (u.Contains("VIRTUAL") || u.Contains("LOOP")) return "Virtual";
        return "Analog";
    }

    // ---- tab 3: the armed inputs and the guard ------------------------------

    private Control BuildInputsPage()
    {
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

        _hint.Margin = new Thickness(0, 0, 0, 14);
        _hint.TextWrapping = TextWrapping.Wrap;

        var armMore = Ui.Small("Arm another → Levels", Tokens.Accent);
        armMore.Click += (_, _) => SelectTab(TabLevels);
        var armRow = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Margin = new Thickness(10, 4, 10, 0) };
        _inputsEmpty.VerticalAlignment = VerticalAlignment.Center;
        _inputsEmpty.TextWrapping = TextWrapping.Wrap;
        Grid.SetColumn(_inputsEmpty, 0);
        Grid.SetColumn(armMore, 1);
        armRow.Children.Add(_inputsEmpty);
        armRow.Children.Add(armMore);

        var bandHead = Ui.Stack(Orientation.Horizontal, 12,
            Ui.Caption("Guard · listen band"), _bandLabel);
        var guardCard = Ui.Card(Ui.Stack(Orientation.Vertical, 8,
            bandHead,
            _band,
            Ui.Stack(Orientation.Horizontal, 16,
                Ui.Stack(Orientation.Vertical, 6, Ui.Caption("Low edge"), _lowEdge),
                Ui.Stack(Orientation.Vertical, 6, Ui.Caption("Attack"), _attack),
                Ui.Stack(Orientation.Vertical, 6, Ui.Caption("Max cut"), _maxCut),
                Ui.Stack(Orientation.Vertical, 6, Ui.Caption("Voice budget"), _budget),
                Ui.Stack(Orientation.Vertical, 6, Ui.Caption("Floor"), _floorAuto)),
            Ui.Text("It cuts what lands inside the box: between the teal handles and above the dashed line. Drag any of the three.",
                    12.5, Tokens.InkFaint)), background: Tokens.Ground2);

        var ringOut = BuildRingOut();
        var captureCard = BuildCapture();
        var filters = BuildFilterList();
        var disclosure = BuildDisclosure();

        var root = new DockPanel { LastChildFill = true };
        var top = Ui.Stack(Orientation.Vertical, 0, _hint, header);
        DockPanel.SetDock(top, Dock.Top);
        DockPanel.SetDock(disclosure, Dock.Bottom);
        root.Children.Add(top);
        root.Children.Add(disclosure);
        root.Children.Add(new ScrollViewer
        {
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
            Content = Ui.Stack(Orientation.Vertical, 18, _strips, armRow, guardCard, ringOut, captureCard, filters),
        });
        return root;
    }

    /// <summary>Where the X32 lives, so the path check knows who to ask.</summary>
    public void SetConsoleAddress(IPAddress address) => _consoleAddress = address;

    /// The start-up search can find the desk after this screen was built. Show
    /// what it found, unless the operator is in the middle of typing there.
    private void OnConsoleAddress() => Dispatcher.UIThread.Post(() =>
    {
        var text = _feedback.ConsoleAddress ?? "";
        if (!_console.IsFocused && _console.Text != text)
        {
            _console.Text = text;
            _consoleAddress = IPAddress.TryParse(text, out var ip) ? ip : IPAddress.None;
            SyncConsole();
        }
        RenderStatus();
    });

    private void OnEngineOk(bool _) => Dispatcher.UIThread.Post(RenderStatus);
    private void OnDesk() => Dispatcher.UIThread.Post(RenderStatus);

    /// Take what was typed, keep it if it parses, and say so either way.
    private void CommitConsole()
    {
        if (_feedback.SetConsoleAddress(_console.Text))
        {
            _consoleAddress = IPAddress.TryParse(_console.Text, out var ip) ? ip : IPAddress.None;
        }
        SyncConsole();
    }

    private void SyncConsole()
    {
        var text = _console.Text ?? "";
        var ok = text.Length == 0 || IPAddress.TryParse(text, out _);
        _console.Foreground = ok ? Tokens.Ink : Tokens.Clip;
    }

    /// Both lists go in together: the pairing is the whole meaning, so one list
    /// without the other, or two of different lengths, is kept out and shown red.
    private void CommitDesk()
    {
        var ok = _feedback.SetDeskBypassChannels(_deskCh.Text, _bypassCh.Text);
        _deskCh.Foreground = ok ? Tokens.Ink : Tokens.Clip;
        _bypassCh.Foreground = ok ? Tokens.Ink : Tokens.Clip;
    }

    public void Teardown()
    {
        _feedback.SearchRangeChanged -= OnSearchRange;
        _feedback.DevicesChanged -= OnDevices;
        _feedback.ChannelsChanged -= OnChannels;
        _feedback.SpectrumChanged -= OnSpectrum;
        _feedback.NotchesChanged -= OnNotches;
        _feedback.ConsoleAddressChanged -= OnConsoleAddress;
        _feedback.EngineOkChanged -= OnEngineOk;
        _feedback.BypassChanged -= OnEngineOk;
        _feedback.DeskBypassChanged -= OnDesk;
    }

    /// <summary>Driven by the window's single animation timer; only the visible tab's meters move.</summary>
    public void Tick()
    {
        _frame++;
        _statusLed.Tick();
        if (_tab == TabInputs)
        {
            foreach (var s in _strip.Values) s.Tick();
            _band.InvalidateVisual();
        }
        else if (_tab == TabLevels)
        {
            var levels = _feedback.InputLevels;
            foreach (var row in _levelRowList)
            {
                if (row.Index < levels.Length) row.SetLevel(levels[row.Index].LevelDb, levels[row.Index].HoldDb);
                row.Tick();
            }
        }
        if (_frame % 16 == 0) RenderStatus();   // ~twice a second is plenty for a sentence
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

    /// <summary>A sentence that wraps instead of running off the card.</summary>
    private static TextBlock Wrapped(string text, double size, IBrush brush, VerticalAlignment align = VerticalAlignment.Top)
    {
        var t = Ui.Text(text, size, brush);
        t.TextWrapping = TextWrapping.Wrap;
        t.VerticalAlignment = align;
        return t;
    }

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
        _pathState.TextWrapping = TextWrapping.Wrap;
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
            finally { _pathCheck.IsEnabled = _feedback.InPath; }
        };

        return Ui.Stack(Orientation.Vertical, 8,
            Ui.Stack(Orientation.Horizontal, 12, _pathCheck,
                Wrapped("Soundcheck only — this puts a brief tone through the PA.", 12.5, Tokens.InkFaint, VerticalAlignment.Center)),
            _pathState);
    }

    /// <summary>
    /// Capture: a diagnostic you switch on when something needs answering, and
    /// off again afterwards. It writes a row per detection with how long the ring
    /// took to be called feedback and how wide it was, keeps the audio, and keeps
    /// detection running while the guard is bypassed - so a song played half on
    /// and half off produces two comparable sets instead of one set and a silence.
    /// </summary>
    private Control BuildCapture()
    {
        _capture.Click += (_, _) =>
        {
            _feedback.SetCapture(!_feedback.CaptureEnabled);
            RenderCapture();
        };
        RenderCapture();
        _captureState.TextWrapping = TextWrapping.Wrap;
        _captureState.VerticalAlignment = VerticalAlignment.Center;
        return Ui.Card(Ui.Stack(Orientation.Vertical, 10,
            Ui.Caption("Capture"),
            Ui.Stack(Orientation.Horizontal, 12, _capture, _captureState)),
            background: Tokens.Ground2);
    }

    private void RenderCapture()
    {
        var on = _feedback.CaptureEnabled;
        _capture.Content = on ? "Stop capture" : "Start capture";
        _capture.Foreground = on ? Tokens.Catch : Tokens.InkDim;
        _captureState.Text = on
            ? "Recording every catch and the audio, 17 MB a minute. Guard off is measured too."
            : "Off. Logs every catch with its detection time and width, and keeps the audio.";
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

    private void OnSearchRange() => Dispatcher.UIThread.Post(() =>
    {
        _band.SetRange(_feedback.MinHz, _feedback.MaxHz);
        _band.SetFloor(_feedback.FloorDb);
        _bandLabel.Text = $"listening {Hz(_feedback.MinHz)} – {Hz(_feedback.MaxHz)}";
        SyncFloorAuto();
    });

    private void OnDevices() => Dispatcher.UIThread.Post(RefreshDevices);
    private void OnChannels() => Dispatcher.UIThread.Post(() => { RebuildStrips(); RebuildLevelRows(); RenderStatus(); });

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

    /// <summary>The Inputs tab shows only the armed channels; arming happens on Levels.</summary>
    private void RebuildStrips()
    {
        var channels = _feedback.InputChannels
            .Where(c => !c.Name.StartsWith("NONE", StringComparison.OrdinalIgnoreCase))
            .Where(c => _feedback.IsInputEnabled(c.Index))
            .ToArray();
        var indices = channels.Select(c => c.Index).ToArray();

        var any = _feedback.InputChannels.Count > 0;
        _hint.Text = !any
            ? "Pick an audio device on the Audio device tab to see its inputs."
            : "Return is the output the cut signal is sent back on, or None to listen and log without touching any output. Double-click a name to rename.";
        _inputsEmpty.Text = channels.Length == 0
            ? "Nothing armed. Go to Levels, sing, and arm the input that moves."
            : _feedback.InPath
                ? $"{channels.Length} armed, {_feedback.InPathCount} in the audio path."
                : $"{channels.Length} armed, all on None: the engine listens and logs, nothing reaches an output. Pick a Return only to put it in the path.";

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

/// <summary>
/// One device input on the Levels tab: index, the device's name for it, the
/// operator's name if it is armed, a live meter with a two-second peak hold,
/// the held peak in dB, and an Arm button.
/// </summary>
public sealed class LevelRow : Border
{
    public const string Columns = "40,160,90,*,64,90";

    private readonly LevelMeter _meter = new() { Height = 9, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _db = Ui.Mono("silent", 12.5, Tokens.InkDim);
    private readonly TextBlock _chip = Ui.Mono("", 11, Tokens.Accent, FontWeight.Bold);
    private readonly Border _chipBox;
    private readonly TextBlock _name;
    private readonly Button _arm = Ui.Small("arm", Tokens.InkDim);
    private bool _armed;

    public int Index { get; }

    public LevelRow(int index, string hardwareName)
    {
        Index = index;
        Padding = new Thickness(10, 5);
        CornerRadius = Tokens.RadiusMd;

        var idx = Ui.Mono($"{index:00}", 12.5, Tokens.InkDim);
        _name = Ui.Text(hardwareName, 14, Tokens.InkDim);
        _name.TextTrimming = TextTrimming.CharacterEllipsis;
        _chip.LetterSpacing = 1;
        _chipBox = new Border
        {
            Background = Tokens.AccentSoft,
            CornerRadius = Tokens.Pill,
            Padding = new Thickness(8, 2),
            HorizontalAlignment = HorizontalAlignment.Left,
            Child = _chip,
            IsVisible = false,
        };
        _db.HorizontalAlignment = HorizontalAlignment.Right;
        _arm.HorizontalAlignment = HorizontalAlignment.Right;
        _arm.Click += (_, _) => ArmClicked?.Invoke();

        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions(Columns) };
        Add(grid, idx, 0);
        Add(grid, _name, 1);
        Add(grid, _chipBox, 2);
        _meter.Margin = new Thickness(0, 0, 14, 0);
        Add(grid, _meter, 3);
        Add(grid, _db, 4);
        Add(grid, _arm, 5);
        Child = grid;
    }

    public event Action? ArmClicked;

    private static void Add(Grid g, Control c, int col)
    {
        Grid.SetColumn(c, col);
        c.VerticalAlignment = VerticalAlignment.Center;
        g.Children.Add(c);
    }

    public void SyncArm(bool armed, string label, string hardwareName)
    {
        _armed = armed;
        Background = armed ? Tokens.AccentSoft : Brushes.Transparent;
        _name.Foreground = armed ? Tokens.Ink : Tokens.InkDim;
        var showChip = armed && !string.Equals(label, hardwareName, StringComparison.Ordinal);
        _chipBox.IsVisible = showChip;
        _chip.Text = showChip ? label.ToUpperInvariant() : "";
        _arm.Content = new TextBlock
        {
            Text = armed ? "ARMED" : "arm",
            FontSize = 11.5,
            FontWeight = armed ? FontWeight.Bold : FontWeight.Normal,
            Foreground = armed ? Tokens.Accent : Tokens.InkDim,
            LetterSpacing = armed ? 1 : 0,
        };
        _arm.BorderBrush = armed ? Tokens.AccentLine : Tokens.Line;
        _arm.Background = armed ? Tokens.AccentSoft : Tokens.Panel2;
        _meter.Muted = false;
    }

    /// <param name="levelDb">peak over the last reporting interval, dBFS</param>
    /// <param name="holdDb">the peak held for two seconds, dBFS</param>
    public void SetLevel(float levelDb, float holdDb)
    {
        _meter.SetTarget(Math.Clamp((levelDb + 72f) / 72f, 0f, 1f));
        if (holdDb <= -90f)
        {
            _db.Text = "silent";
            _db.Foreground = Tokens.InkDim;
        }
        else
        {
            _db.Text = $"{holdDb:0}";
            _db.Foreground = holdDb > -3f ? Tokens.Clip : holdDb > -50f ? Tokens.Ink : Tokens.InkDim;
        }
    }

    public void Tick() => _meter.Tick();
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
        // "None" first: listen only. The engine analyses and logs this input and
        // writes nothing anywhere - assist mode, and what a newly armed mic gets
        // until an output is chosen on purpose.
        var outs = outputs.Prepend((Index: FeedbackController.NoReturn, Name: "None — listen only")).ToArray();
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
