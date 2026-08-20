using System.Net;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform;
using Avalonia.Threading;
using Fader.Bridge;
using Fader.Bridge.Feedback;

namespace Fader.MenuBar;

/// <summary>
/// A tray-only Avalonia application: no windows, just a menu-bar icon whose
/// menu starts/stops the bridge and shows its live status. All the real work is
/// the referenced FaderBridge project, hosted in-process by <see cref="BridgeController"/>.
/// </summary>
public sealed class App : Application
{
    private BridgeController? _controller;
    private TrayIcon? _tray;

    private NativeMenuItem? _bridgeItem;
    private NativeMenuItem? _x32Item;
    private NativeMenuItem? _midiItem;
    private NativeMenuItem? _startStopItem;
    private NativeMenuItem? _marqueeItem;

    // Feedback engine: supervised out-of-process, controlled over OSC, with
    // locked-notch persistence and CSV logging owned here (Phases 1-2).
    private FeedbackController? _feedback;
    private string? _enginePath;
    private string _configPath = "";
    private FkMode _fkMode = FkMode.Assist;
    private NativeMenuItem? _fkStatusItem;
    private NativeMenuItem? _fkEngineItem;
    private NativeMenuItem? _fkOff;
    private NativeMenuItem? _fkAssist;
    private NativeMenuItem? _fkAuto;
    private NativeMenuItem? _fkSpectrumItem;
    private NativeMenuItem? _fkDeviceMenu;
    private NativeMenuItem? _fkInputsMenu;
    private NativeMenu? _fkLeadMenu;
    private NativeMenu? _fkBgvMenu;
    private SpectrumWindow? _spectrumWindow;

    // No XAML; everything is built in code.
    public override void Initialize()
    {
    }

    public override void OnFrameworkInitializationCompleted()
    {
        _configPath = Path.Combine(AppContext.BaseDirectory, "config.json");
        _controller = new BridgeController(_configPath);
        _controller.StatusChanged += OnStatusChanged;

        _bridgeItem = new NativeMenuItem("Starting…") { IsEnabled = false };
        _x32Item = new NativeMenuItem("X32: —") { IsEnabled = false };
        _midiItem = new NativeMenuItem("MIDI: —") { IsEnabled = false };

        _startStopItem = new NativeMenuItem("Stop");
        _startStopItem.Click += OnStartStopClick;

        _marqueeItem = new NativeMenuItem("Scroll now-playing on displays")
        {
            ToggleType = NativeMenuItemToggleType.CheckBox,
            IsChecked = _controller.MarqueeEnabled,
        };
        _marqueeItem.Click += OnMarqueeClick;

        // ---- feedback engine section ----------------------------------------
        _enginePath = FindEngine();

        _fkStatusItem = new NativeMenuItem(_enginePath is null ? "Feedback: engine not built" : "Feedback: off")
        {
            IsEnabled = false,
        };

        _fkEngineItem = new NativeMenuItem("Feedback engine")
        {
            ToggleType = NativeMenuItemToggleType.CheckBox,
            IsChecked = false,
            IsEnabled = _enginePath is not null,
        };
        _fkEngineItem.Click += OnFkEngineClick;

        _fkOff    = FkModeItem("Off",    FkMode.Off);
        _fkAssist = FkModeItem("Assist", FkMode.Assist);
        _fkAuto   = FkModeItem("Auto",   FkMode.Auto);
        _fkAssist.IsChecked = true;   // default assist
        var fkModeMenu = new NativeMenuItem("Feedback mode")
        {
            Menu = new NativeMenu { Items = { _fkOff, _fkAssist, _fkAuto } },
        };

        var fkLockAll = new NativeMenuItem("Lock all feedback filters");
        fkLockAll.Click += (_, _) => _feedback?.LockAll();
        var fkClear = new NativeMenuItem("Clear feedback filters");
        fkClear.Click += (_, _) => _feedback?.ClearAll(includeLocked: true);

        _fkSpectrumItem = new NativeMenuItem("Show spectrum window") { IsEnabled = false };
        _fkSpectrumItem.Click += OnFkSpectrumClick;

        _fkDeviceMenu = new NativeMenuItem("Audio input") { IsEnabled = false, Menu = new NativeMenu() };

        _fkLeadMenu = new NativeMenu();
        _fkBgvMenu = new NativeMenu();
        _fkInputsMenu = new NativeMenuItem("Feedback inputs")
        {
            IsEnabled = false,
            Menu = new NativeMenu
            {
                Items =
                {
                    new NativeMenuItem("LEAD input") { Menu = _fkLeadMenu },
                    new NativeMenuItem("BGV input") { Menu = _fkBgvMenu },
                },
            },
        };

        var quitItem = new NativeMenuItem("Quit");
        quitItem.Click += OnQuitClick;

        var menu = new NativeMenu
        {
            Items =
            {
                _bridgeItem,
                _x32Item,
                _midiItem,
                new NativeMenuItemSeparator(),
                _startStopItem,
                _marqueeItem,
                new NativeMenuItemSeparator(),
                _fkStatusItem,
                _fkEngineItem,
                _fkDeviceMenu,
                _fkInputsMenu,
                fkModeMenu,
                fkLockAll,
                fkClear,
                _fkSpectrumItem,
                new NativeMenuItemSeparator(),
                quitItem,
            },
        };

        _tray = new TrayIcon
        {
            Icon = new WindowIcon(AssetLoader.Open(
                new Uri("avares://FaderBridgeMenuBar/Assets/tray.png"))),
            ToolTipText = "FaderPort ⇄ X32",
            Menu = menu,
            IsVisible = true,
        };

        TrayIcon.SetIcons(this, new TrayIcons { _tray });

        // Auto-start the bridge on launch; the menu reflects success or failure.
        _ = _controller.StartAsync();

        base.OnFrameworkInitializationCompleted();
    }

    private void OnStatusChanged(BridgeStatus status) =>
        Dispatcher.UIThread.Post(() => Render(status));

    private void Render(BridgeStatus status)
    {
        _bridgeItem!.Header = status switch
        {
            { Error: { } error } => $"⚠ {error}",
            { Active: false } => "○ Bridge stopped",
            { Bridging: false } => "◍ Waiting for FaderPort…",
            _ => "● Bridge running",
        };

        _x32Item!.Header = status.Bridging
            ? $"X32 {status.X32Endpoint} — {(status.X32Reachable ? "reachable" : "no reply")}"
            : $"X32 {status.X32Endpoint}";

        _midiItem!.Header = $"MIDI: {status.MidiPort}";

        _startStopItem!.Header = status.Active ? "Stop" : "Start";

        _tray!.ToolTipText = status switch
        {
            { Active: false } => "FaderPort ⇄ X32 — stopped",
            { Bridging: false } => "FaderPort ⇄ X32 — waiting for FaderPort",
            { X32Reachable: true } => "FaderPort ⇄ X32 — connected",
            _ => "FaderPort ⇄ X32 — X32 not replying",
        };
    }

    private async void OnStartStopClick(object? sender, EventArgs e)
    {
        if (_controller is null)
        {
            return;
        }

        if (_controller.Active)
        {
            await _controller.StopAsync();
        }
        else
        {
            await _controller.StartAsync();
        }
    }

    private void OnMarqueeClick(object? sender, EventArgs e)
    {
        if (_controller is null || _marqueeItem is null)
        {
            return;
        }

        var on = !_controller.MarqueeEnabled;
        _controller.SetMarqueeEnabled(on);
        _marqueeItem.IsChecked = on;
    }

    // ---- feedback engine ----------------------------------------------------
    private NativeMenuItem FkModeItem(string label, FkMode mode)
    {
        var item = new NativeMenuItem(label) { ToggleType = NativeMenuItemToggleType.Radio };
        item.Click += (_, _) => SelectFkMode(mode);
        return item;
    }

    private void SelectFkMode(FkMode mode)
    {
        _fkMode = mode;
        _fkOff!.IsChecked = mode == FkMode.Off;
        _fkAssist!.IsChecked = mode == FkMode.Assist;
        _fkAuto!.IsChecked = mode == FkMode.Auto;
        _feedback?.SetMode(mode);
    }

    private void OnFkEngineClick(object? sender, EventArgs e)
    {
        if (_enginePath is null || _fkEngineItem is null)
        {
            return;
        }

        if (_feedback is null)
        {
            var dataDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "FeedbackKiller");
            var controller = new FeedbackController(_enginePath, dataDir);
            controller.EngineOkChanged += _ => Dispatcher.UIThread.Post(RenderFk);
            controller.DevicesChanged += () => Dispatcher.UIThread.Post(RebuildDeviceMenu);
            controller.ChannelsChanged += () => Dispatcher.UIThread.Post(RebuildChannelMenus);
            _feedback = controller;
            controller.SetMode(_fkMode);   // reflect the menu's current mode
            controller.Start();
            _fkEngineItem.IsChecked = true;
        }
        else
        {
            var controller = _feedback;
            _feedback = null;
            _fkEngineItem.IsChecked = false;
            _ = controller.DisposeAsync();
        }

        RenderFk();
        RebuildDeviceMenu();
        RebuildChannelMenus();
    }

    private void RenderFk()
    {
        if (_fkStatusItem is null)
        {
            return;
        }

        _fkStatusItem.Header = _feedback is null
            ? (_enginePath is null ? "Feedback: engine not built" : "Feedback: off")
            : (_feedback.EngineOk ? "Feedback: ● engine up" : "Feedback: ◍ engine down — audio bypassed");

        if (_fkSpectrumItem is not null)
        {
            _fkSpectrumItem.IsEnabled = _feedback is not null;
        }
        if (_fkDeviceMenu is not null)
        {
            _fkDeviceMenu.IsEnabled = _feedback is not null;
        }
        if (_fkInputsMenu is not null)
        {
            _fkInputsMenu.IsEnabled = _feedback is not null;
        }
    }

    private void RebuildChannelMenus()
    {
        if (_fkLeadMenu is null || _fkBgvMenu is null)
        {
            return;
        }

        _fkLeadMenu.Items.Clear();
        _fkBgvMenu.Items.Clear();
        if (_feedback is null)
        {
            return;
        }

        foreach (var (index, name) in _feedback.InputChannels)
        {
            // Skip the device's unpatched slots.
            if (name.StartsWith("NONE", StringComparison.OrdinalIgnoreCase) || name == "None")
            {
                continue;
            }
            _fkLeadMenu.Items.Add(ChannelItem(index, name, isLead: true));
            _fkBgvMenu.Items.Add(ChannelItem(index, name, isLead: false));
        }
    }

    private NativeMenuItem ChannelItem(int index, string name, bool isLead)
    {
        var current = isLead ? _feedback!.LeadChannel : _feedback!.BgvChannel;
        var item = new NativeMenuItem(name)
        {
            ToggleType = NativeMenuItemToggleType.Radio,
            IsChecked = index == current,
        };
        item.Click += (_, _) =>
        {
            if (_feedback is null)
            {
                return;
            }
            if (isLead) _feedback.SetChannels(index, _feedback.BgvChannel);
            else _feedback.SetChannels(_feedback.LeadChannel, index);
        };
        return item;
    }

    private void RebuildDeviceMenu()
    {
        if (_fkDeviceMenu?.Menu is not { } menu)
        {
            return;
        }

        menu.Items.Clear();
        if (_feedback is null)
        {
            return;
        }

        var current = _feedback.CurrentDevice;
        foreach (var device in _feedback.Devices)
        {
            var name = device;
            var item = new NativeMenuItem(name)
            {
                ToggleType = NativeMenuItemToggleType.Radio,
                IsChecked = name == current,
            };
            item.Click += (_, _) => _feedback?.SetDevice(name);
            menu.Items.Add(item);
        }
    }

    private void OnFkSpectrumClick(object? sender, EventArgs e)
    {
        if (_feedback is null)
        {
            return;
        }

        if (_spectrumWindow is not null)
        {
            _spectrumWindow.Activate();
            return;
        }

        IPAddress address;
        try
        {
            address = BridgeConfig.Load(_configPath).ResolvedAddress;
        }
        catch
        {
            return;   // no usable console address; nothing to show the RTA from
        }

        var window = new SpectrumWindow(_feedback, address);
        window.Closed += (_, _) => _spectrumWindow = null;
        _spectrumWindow = window;
        window.Show();
    }

    private static string? FindEngine()
    {
        var env = Environment.GetEnvironmentVariable("FK_ENGINE_PATH");
        if (!string.IsNullOrEmpty(env) && File.Exists(env))
        {
            return env;
        }

        var candidates = new List<string> { Path.Combine(AppContext.BaseDirectory, "fk-engine") };
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (var i = 0; i < 8 && dir is not null; i++, dir = dir.Parent)
        {
            candidates.Add(Path.Combine(dir.FullName, "engine", "build",
                "fk-engine_artefacts", "Release", "fk-engine"));
        }
        return candidates.FirstOrDefault(File.Exists);
    }

    private async void OnQuitClick(object? sender, EventArgs e)
    {
        if (_feedback is not null)
        {
            await _feedback.DisposeAsync();
        }

        if (_controller is not null)
        {
            await _controller.DisposeAsync();
        }

        (ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.Shutdown();
    }
}
