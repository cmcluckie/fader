using System.Net;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform;
using Avalonia.Threading;
using FeedbackFader;

using Fader.Shared.Net;
using Fader.Shared.Ui;

namespace FeedbackFader.App;

/// <summary>
/// Feedback Fader: a tray-only Avalonia application around the feedback engine.
///
/// The engine itself is supervised out-of-process by <see cref="FeedbackController"/>
/// - it does real-time DSP on the audio thread and must not die with the UI, or
/// take the UI down with it. The tray is a launcher and a glance; everything that
/// configures anything lives in Show/Setup inside the window.
///
/// This was one half of a combined app that also bridged a FaderPort to an X32.
/// The two shared a tray icon and nothing else: the feedback code never referenced
/// MIDI, and the feedback views never referenced the bridge. Only the console
/// address was shared, and that is Feedback Fader's own setting now.
/// </summary>
public sealed class App : Application
{
    private FeedbackController? _feedback;
    private TrayIcon? _tray;

    private string? _enginePath;
    private string _dataDir = "";
    private string? _startupError;
    private FkAudioStore? _audioStore;

    private NativeMenuItem? _statusItem;
    private NativeMenuItem? _engineItem;
    private NativeMenuItem? _openItem;
    private NativeMenuItem? _spectrumItem;
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
    /// Set for the hidden render-to-PNG modes: build the app shell for its styles
    /// but start no tray icon and no engine.
    /// </summary>
    public static bool RenderOnly { get; set; }

    public override void OnFrameworkInitializationCompleted()
    {
        if (RenderOnly)
        {
            base.OnFrameworkInitializationCompleted();
            return;
        }

        _enginePath = FindEngine();
        _dataDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "FeedbackKiller");
        _audioStore = new FkAudioStore(Path.Combine(_dataDir, "audio.json"));

        _statusItem = new NativeMenuItem(_enginePath is null ? "Engine not built" : "Off")
        {
            IsEnabled = false,
        };

        _engineItem = new NativeMenuItem("Feedback engine")
        {
            ToggleType = NativeMenuItemToggleType.CheckBox,
            IsChecked = false,
            IsEnabled = _enginePath is not null,
        };
        _engineItem.Click += OnEngineClick;

        _openItem = new NativeMenuItem("Open Feedback Fader…") { IsEnabled = false };
        _openItem.Click += (_, _) => OpenWindow();

        _spectrumItem = new NativeMenuItem("X32 RTA overlay…") { IsEnabled = false };
        _spectrumItem.Click += OnSpectrumClick;

        var lockAll = new NativeMenuItem("Lock all filters");
        lockAll.Click += (_, _) => _feedback?.LockAll();
        var clear = new NativeMenuItem("Clear all filters");
        clear.Click += (_, _) => _feedback?.ClearAll(includeLocked: true);

        var quitItem = new NativeMenuItem("Quit");
        quitItem.Click += OnQuitClick;

        var menu = new NativeMenu
        {
            Items =
            {
                _statusItem,
                _engineItem,
                _openItem,
                _spectrumItem,
                lockAll,
                clear,
                new NativeMenuItemSeparator(),
                quitItem,
            },
        };

        // Two tray icons for the one mark, as FaderBridge has. macOS wants a
        // template image - black plus alpha, inverted by the system for a dark
        // menu bar and tinted while the menu is open; the Windows notification
        // area has no such notion, so it gets the coloured twin instead.
        var trayAsset = OperatingSystem.IsMacOS() ? "tray.png" : "tray-color.png";

        _tray = new TrayIcon
        {
            Icon = new WindowIcon(AssetLoader.Open(
                new Uri($"avares://FeedbackFader/Assets/{trayAsset}"))),
            ToolTipText = "Feedback Fader",
            Menu = menu,
            IsVisible = true,
        };

        if (OperatingSystem.IsMacOS())
        {
            MacOSProperties.SetIsTemplateIcon(_tray, true);
        }

        TrayIcon.SetIcons(this, new TrayIcons { _tray });

        // Come back as it was left: device, channels and notches are replayed by
        // the controller from their own stores.
        if (_enginePath is not null && (_audioStore?.Load().Enabled ?? false))
        {
            EnableEngine();
        }

        Render();
        base.OnFrameworkInitializationCompleted();

        _ = LocateConsoleAsync();
    }

    /// <summary>
    /// Find the console in the background so its address is ready before the user
    /// opens a window that needs it. Uses the last address that answered, else
    /// the saved one as a hint, else a LAN search - and writes whatever answers
    /// back to the store, so "the last detected console" is what we use next time.
    /// Runs off the UI thread and never throws into startup.
    /// </summary>
    private async Task LocateConsoleAsync()
    {
        if (_audioStore is null)
        {
            return;
        }

        try
        {
            var seed = _audioStore.Load().ConsoleAddress;
            var found = await Task.Run(() =>
                X32Locator.ResolveAsync(new X32AddressStore(), seed));

            if (found is not null && found.ToString() != seed)
            {
                _audioStore.Save(_audioStore.Load() with { ConsoleAddress = found.ToString() });
            }
        }
        catch
        {
            // Discovery is a convenience; if it fails the user can still type the
            // address in Setup exactly as before.
        }
    }

    private void OnEngineClick(object? sender, EventArgs e)
    {
        if (_feedback is null) EnableEngine();
        else DisableEngine();
    }

    private void EnableEngine()
    {
        if (_enginePath is null || _engineItem is null || _feedback is not null) return;

        // A failure to start - a held telemetry port, a missing engine - must leave
        // the app up and the tray saying why, rather than the process dying on
        // launch with nothing to read.
        FeedbackController controller;
        try
        {
            controller = new FeedbackController(_enginePath, _dataDir);
        }
        catch (Exception ex)
        {
            _startupError = ex.Message;
            _engineItem.IsChecked = false;
            Render();
            return;
        }

        _startupError = null;
        controller.EngineOkChanged += _ => Dispatcher.UIThread.Post(Render);
        controller.ConsoleAddressChanged += () => Dispatcher.UIThread.Post(Render);
        _feedback = controller;
        controller.Start();
        _engineItem.IsChecked = true;
        PersistEnabled(true);
        Render();
    }

    private void DisableEngine()
    {
        if (_engineItem is null || _feedback is null) return;

        _faderWindow?.Close();
        var controller = _feedback;
        _feedback = null;
        _engineItem.IsChecked = false;
        PersistEnabled(false);
        _ = controller.DisposeAsync();
        Render();
    }

    /// Persist only the on/off flag, leaving the saved device and channels intact.
    private void PersistEnabled(bool enabled)
    {
        if (_audioStore is null) return;
        _audioStore.Save(_audioStore.Load() with { Enabled = enabled });
    }

    private void Render()
    {
        if (_statusItem is null) return;

        var on = _feedback is not null;
        _statusItem.Header = _feedback is null
            ? (_startupError is not null ? $"Unavailable - {_startupError}"
               : _enginePath is null ? "Engine not built" : "Off")
            : _feedback.EngineOk ? "● Engine up" : "◍ Engine down — audio bypassed";

        if (_openItem is not null) _openItem.IsEnabled = on;

        // The RTA overlay needs a console to read from. Without one it would open
        // an empty window, so the item says why instead of doing nothing.
        var console = !Equals(ConsoleAddress(), IPAddress.None);
        if (_spectrumItem is not null)
        {
            _spectrumItem.IsEnabled = on && console;
            _spectrumItem.Header = on && !console
                ? "X32 RTA overlay — set the console in Setup"
                : "X32 RTA overlay…";
        }

        if (_tray is not null)
            _tray.ToolTipText = on
                ? (_feedback!.EngineOk ? "Feedback Fader — guarding" : "Feedback Fader — engine down")
                : "Feedback Fader — off";
    }

    /// <summary>
    /// The console this rig is attached to, for the RTA overlay and the signal-path
    /// check. Feedback Fader's own setting - it used to be read out of the bridge's
    /// config file, which was the last thing tying the two products together.
    /// </summary>
    private IPAddress ConsoleAddress()
    {
        // The controller is the live authority once it is up - Setup writes there,
        // and the store is only its backing file.
        var saved = _feedback?.ConsoleAddress ?? _audioStore?.Load().ConsoleAddress;
        return IPAddress.TryParse(saved, out var ip) ? ip : IPAddress.None;
    }

    private void OpenWindow()
    {
        if (_feedback is null) return;
        if (_faderWindow is not null) { _faderWindow.Activate(); return; }

        var window = new FaderWindow(_feedback, ConsoleAddress());
        window.Closed += (_, _) => _faderWindow = null;
        _faderWindow = window;
        window.Show();
    }

    private void OnSpectrumClick(object? sender, EventArgs e)
    {
        if (_feedback is null) return;
        if (_spectrumWindow is not null) { _spectrumWindow.Activate(); return; }

        var address = ConsoleAddress();
        if (Equals(address, IPAddress.None)) return;

        var window = new SpectrumWindow(_feedback, address);
        window.Closed += (_, _) => _spectrumWindow = null;
        _spectrumWindow = window;
        window.Show();
    }

    private static string? FindEngine()
    {
        var env = Environment.GetEnvironmentVariable("FK_ENGINE_PATH");
        if (!string.IsNullOrEmpty(env) && File.Exists(env)) return env;

        // A plain console binary: fk-engine on macOS, fk-engine.exe on Windows.
        var exe = OperatingSystem.IsWindows() ? "fk-engine.exe" : "fk-engine";

        var candidates = new List<string> { Path.Combine(AppContext.BaseDirectory, exe) };
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (var i = 0; i < 8 && dir is not null; i++, dir = dir.Parent)
        {
            candidates.Add(Path.Combine(dir.FullName, "engine", "build",
                "fk-engine_artefacts", "Release", exe));
        }
        return candidates.FirstOrDefault(File.Exists);
    }

    private async void OnQuitClick(object? sender, EventArgs e)
    {
        if (_feedback is not null) await _feedback.DisposeAsync();
        (ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.Shutdown();
    }
}
