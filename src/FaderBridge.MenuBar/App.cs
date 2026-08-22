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
/// A tray-only Avalonia application: the menu-bar icon plus the bridge and the
/// feedback engine. The bridge is hosted in-process by <see cref="BridgeController"/>;
/// the feedback engine is supervised out-of-process by <see cref="FeedbackController"/>,
/// and its per-channel setup lives in a <see cref="ConfigWindow"/>.
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

    private FeedbackController? _feedback;
    private string? _enginePath;
    private string _configPath = "";
    private string _fkDataDir = "";
    private string? _fkStartupError;
    private FkAudioStore? _fkAudioStore;
    private NativeMenuItem? _fkStatusItem;
    private NativeMenuItem? _fkEngineItem;
    private NativeMenuItem? _fkConfigItem;
    private NativeMenuItem? _fkSpectrumItem;
    private SpectrumWindow? _spectrumWindow;
    private FaderWindow? _faderWindow;

    public override void Initialize()
    {
        // Code-only app (no App.axaml): load a control theme so templated controls
        // (ComboBox, CheckBox, ScrollViewer, …) actually render. Dark to match.
        Styles.Add(new Avalonia.Themes.Fluent.FluentTheme());
        RequestedThemeVariant = Avalonia.Styling.ThemeVariant.Dark;
    }

    /// <summary>
    /// Set for the hidden render-to-PNG modes: build the app shell for its styles but
    /// start no tray icon, bridge, or engine.
    /// </summary>
    public static bool RenderOnly { get; set; }

    public override void OnFrameworkInitializationCompleted()
    {
        if (RenderOnly)
        {
            base.OnFrameworkInitializationCompleted();
            return;
        }

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

        // ---- feedback section -----------------------------------------------
        _enginePath = FindEngine();
        _fkDataDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "FeedbackKiller");
        _fkAudioStore = new FkAudioStore(Path.Combine(_fkDataDir, "audio.json"));

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

        // The tray is a launcher and a glance, not a config surface: everything that
        // used to live here as menu items is now Show/Setup inside the window.
        _fkConfigItem = new NativeMenuItem("Open Fader…") { IsEnabled = false };
        _fkConfigItem.Click += OnOpenFaderClick;

        _fkSpectrumItem = new NativeMenuItem("X32 RTA overlay…") { IsEnabled = false };
        _fkSpectrumItem.Click += OnFkSpectrumClick;

        var fkLockAll = new NativeMenuItem("Lock all feedback filters");
        fkLockAll.Click += (_, _) => _feedback?.LockAll();
        var fkClear = new NativeMenuItem("Clear feedback filters");
        fkClear.Click += (_, _) => _feedback?.ClearAll(includeLocked: true);

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
                _fkConfigItem,
                _fkSpectrumItem,
                fkLockAll,
                fkClear,
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

        _ = _controller.StartAsync();

        // Restore the feedback engine if it was left running (device, channels and
        // notches are then replayed by the controller from their own stores).
        if (_enginePath is not null && (_fkAudioStore?.Load().Enabled ?? false))
        {
            EnableFeedback();
        }

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
        if (_controller is null) return;
        if (_controller.Active) await _controller.StopAsync();
        else await _controller.StartAsync();
    }

    private void OnMarqueeClick(object? sender, EventArgs e)
    {
        if (_controller is null || _marqueeItem is null) return;
        var on = !_controller.MarqueeEnabled;
        _controller.SetMarqueeEnabled(on);
        _marqueeItem.IsChecked = on;
    }

    // ---- feedback engine ----------------------------------------------------
    private void OnFkEngineClick(object? sender, EventArgs e)
    {
        if (_feedback is null) EnableFeedback();
        else DisableFeedback();
    }

    private void EnableFeedback()
    {
        if (_enginePath is null || _fkEngineItem is null || _feedback is not null) return;

        // Feedback is one feature of the app, not the app. If it can't start - a
        // held telemetry port, a missing engine - the bridge must keep running and
        // the tray must say why, rather than the process dying on launch.
        FeedbackController controller;
        try
        {
            controller = new FeedbackController(_enginePath, _fkDataDir);
        }
        catch (Exception ex)
        {
            _fkStartupError = ex.Message;
            _fkEngineItem.IsChecked = false;
            RenderFk();
            return;
        }

        _fkStartupError = null;
        controller.EngineOkChanged += _ => Dispatcher.UIThread.Post(RenderFk);
        _feedback = controller;
        controller.Start();   // device, inputs, and notches are replayed by the controller
        _fkEngineItem.IsChecked = true;
        PersistEnabled(true);
        RenderFk();
    }

    private void DisableFeedback()
    {
        if (_fkEngineItem is null || _feedback is null) return;

        _faderWindow?.Close();
        var controller = _feedback;
        _feedback = null;
        _fkEngineItem.IsChecked = false;
        PersistEnabled(false);
        _ = controller.DisposeAsync();
        RenderFk();
    }

    // Persist only the on/off flag, leaving the saved device + channel choice intact.
    private void PersistEnabled(bool enabled)
    {
        if (_fkAudioStore is null) return;
        _fkAudioStore.Save(_fkAudioStore.Load() with { Enabled = enabled });
    }

    private void RenderFk()
    {
        if (_fkStatusItem is null) return;

        var on = _feedback is not null;
        _fkStatusItem.Header = _feedback is null
            ? (_fkStartupError is not null ? $"Feedback: unavailable - {_fkStartupError}"
               : _enginePath is null ? "Feedback: engine not built" : "Feedback: off")
            : _feedback.EngineOk ? "Feedback: ● engine up" : "Feedback: ◍ engine down — audio bypassed";

        if (_fkConfigItem is not null) _fkConfigItem.IsEnabled = on;
        if (_fkSpectrumItem is not null) _fkSpectrumItem.IsEnabled = on;
    }

    private void OnOpenFaderClick(object? sender, EventArgs e) => OpenFader();

    private void OpenFader()
    {
        if (_feedback is null) return;
        if (_faderWindow is not null) { _faderWindow.Activate(); return; }

        IPAddress console;
        try { console = BridgeConfig.Load(_configPath).ResolvedAddress; }
        catch { console = IPAddress.None; }

        var window = new FaderWindow(_feedback, console);
        window.Closed += (_, _) => _faderWindow = null;
        _faderWindow = window;
        window.Show();
    }

    private void OnFkSpectrumClick(object? sender, EventArgs e)
    {
        if (_feedback is null) return;
        if (_spectrumWindow is not null) { _spectrumWindow.Activate(); return; }

        IPAddress address;
        try { address = BridgeConfig.Load(_configPath).ResolvedAddress; }
        catch { return; }

        var window = new SpectrumWindow(_feedback, address);
        window.Closed += (_, _) => _spectrumWindow = null;
        _spectrumWindow = window;
        window.Show();
    }

    private static string? FindEngine()
    {
        var env = Environment.GetEnvironmentVariable("FK_ENGINE_PATH");
        if (!string.IsNullOrEmpty(env) && File.Exists(env)) return env;

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
        if (_feedback is not null) await _feedback.DisposeAsync();
        if (_controller is not null) await _controller.DisposeAsync();
        (ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.Shutdown();
    }
}
