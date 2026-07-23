using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform;
using Avalonia.Threading;

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
        if (status.Error is { } error)
        {
            _bridgeItem!.Header = $"⚠ {error}";
        }
        else
        {
            _bridgeItem!.Header = status.Running ? "● Bridge running" : "○ Bridge stopped";
        }

        _x32Item!.Header = status.Running
            ? $"X32 {status.X32Endpoint} — {(status.X32Reachable ? "reachable" : "no reply")}"
            : $"X32 {status.X32Endpoint}";

        _midiItem!.Header = $"MIDI: {status.MidiPort}";

        _startStopItem!.Header = status.Running ? "Stop" : "Start";

        _tray!.ToolTipText = status.Running
            ? $"FaderPort ⇄ X32 — {(status.X32Reachable ? "connected" : "no reply")}"
            : "FaderPort ⇄ X32 — stopped";
    }

    private async void OnStartStopClick(object? sender, EventArgs e)
    {
        if (_controller is null)
        {
            return;
        }

        if (_controller.Running)
        {
            await _controller.StopAsync();
        }
        else
        {
            await _controller.StartAsync();
        }
    }

    private async void OnQuitClick(object? sender, EventArgs e)
    {
        if (_controller is not null)
        {
            await _controller.DisposeAsync();
        }

        (ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.Shutdown();
    }
}
