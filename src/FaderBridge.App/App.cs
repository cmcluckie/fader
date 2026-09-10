using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform;
using Avalonia.Threading;
using Fader.Bridge;

namespace Fader.Bridge.App;

/// <summary>
/// FaderBridge: a tray-only Avalonia application around the FaderPort ⇄ X32
/// bridge, which is hosted in-process by <see cref="BridgeController"/>.
///
/// There is no window - the bridge has nothing to show that the console and the
/// control surface do not show better. The tray reports whether the surface is
/// attached and whether the X32 is replying, and offers Start/Stop.
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

    public override void Initialize()
    {
        // Code-only app (no App.axaml): load a control theme so templated controls
        // render. Dark to match.
        Styles.Add(new Avalonia.Themes.Fluent.FluentTheme());
        RequestedThemeVariant = Avalonia.Styling.ThemeVariant.Dark;
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
                quitItem,
            },
        };

        // Two menu-bar icons for the one mark. macOS wants a template image -
        // black plus alpha, which the system inverts for a dark menu bar and
        // tints when the menu is open; the Windows notification area has no
        // such notion, so a black-on-transparent icon would vanish into a dark
        // taskbar and it gets the coloured twin instead.
        var trayAsset = OperatingSystem.IsMacOS() ? "tray.png" : "tray-color.png";

        _tray = new TrayIcon
        {
            Icon = new WindowIcon(AssetLoader.Open(
                new Uri($"avares://FaderBridge/Assets/{trayAsset}"))),
            ToolTipText = "FaderPort ⇄ X32",
            Menu = menu,
            IsVisible = true,
        };

        if (OperatingSystem.IsMacOS())
        {
            MacOSProperties.SetIsTemplateIcon(_tray, true);
        }

        TrayIcon.SetIcons(this, new TrayIcons { _tray });

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

    private async void OnQuitClick(object? sender, EventArgs e)
    {
        if (_controller is not null) await _controller.DisposeAsync();
        (ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.Shutdown();
    }
}
