using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform;
using Avalonia.Threading;
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

    // Feedback engine (Phase 1): supervised out-of-process, controlled over OSC.
    private EngineSupervisor? _engine;
    private string? _enginePath;
    private FkMode _fkMode = FkMode.Assist;
    private NativeMenuItem? _fkStatusItem;
    private NativeMenuItem? _fkEngineItem;
    private NativeMenuItem? _fkOff;
    private NativeMenuItem? _fkAssist;
    private NativeMenuItem? _fkAuto;

    // No XAML; everything is built in code.
    public override void Initialize()
    {
    }

    public override void OnFrameworkInitializationCompleted()
    {
        var configPath = Path.Combine(AppContext.BaseDirectory, "config.json");
        _controller = new BridgeController(configPath);
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
                fkModeMenu,
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
        _engine?.Client.SetMode(mode);
    }

    private void OnFkEngineClick(object? sender, EventArgs e)
    {
        if (_enginePath is null || _fkEngineItem is null)
        {
            return;
        }

        if (_engine is null)
        {
            var supervisor = new EngineSupervisor(_enginePath);
            supervisor.EngineOkChanged += _ => Dispatcher.UIThread.Post(RenderFk);
            supervisor.EngineStarted += () => supervisor.Client.SetMode(_fkMode);   // replay after each (re)start
            _engine = supervisor;
            supervisor.Start();
            _fkEngineItem.IsChecked = true;
        }
        else
        {
            var supervisor = _engine;
            _engine = null;
            _fkEngineItem.IsChecked = false;
            _ = supervisor.DisposeAsync();
        }

        RenderFk();
    }

    private void RenderFk()
    {
        if (_fkStatusItem is null)
        {
            return;
        }

        _fkStatusItem.Header = _engine is null
            ? (_enginePath is null ? "Feedback: engine not built" : "Feedback: off")
            : (_engine.EngineOk ? "Feedback: ● engine up" : "Feedback: ◍ engine starting…");
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
        if (_engine is not null)
        {
            await _engine.DisposeAsync();
        }

        if (_controller is not null)
        {
            await _controller.DisposeAsync();
        }

        (ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.Shutdown();
    }
}
