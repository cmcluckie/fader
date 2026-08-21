using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Media.Imaging;
using Avalonia.Threading;

namespace Fader.MenuBar;

internal static class Program
{
    // Tray-only app: no main window, so the process must be kept alive explicitly
    // (OnExplicitShutdown) and torn down from the Quit menu item.
    [STAThread]
    public static void Main(string[] args)
    {
        // Hidden: render one SpectrumView frame of synthetic data to a PNG, for
        // verifying the drawing without a live engine/console or a display.
        if (args is ["--render-spectrum", var path])
        {
            RenderSpectrum(path);
            return;
        }

        // Hidden: render the Show/Setup controls with synthetic data, to verify the
        // control theme is loaded and every custom control draws.
        if (args is ["--render-controls", var cpath])
        {
            RenderControls(cpath);
            return;
        }

        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args, ShutdownMode.OnExplicitShutdown);
    }

    private static void RenderSpectrum(string path)
    {
        App.RenderOnly = true;
        BuildAvaloniaApp().SetupWithoutStarting();

        var view = new SpectrumView();
        view.SetRta(SynthRta());
        view.SetEngine(0, SynthEngine(peakHz: 900f, spread: 0.35f, top: -18f));
        view.SetEngine(1, SynthEngine(peakHz: 2500f, spread: 0.30f, top: -34f));
        view.SetNotches(0, new (float, bool)[] { (1000f, true), (3150f, false) });
        view.AddDetection(1000f, correlated: true);

        var size = new Size(760, 380);
        view.Measure(size);
        view.Arrange(new Rect(size));

        var bitmap = new RenderTargetBitmap(new PixelSize(760, 380), new Vector(96, 96));
        bitmap.Render(view);
        bitmap.Save(path);
        Console.WriteLine($"wrote {path}");
    }

    /// <summary>
    /// Render the real custom controls with synthetic data, so the Show/Setup visual
    /// language can be checked without a rig, an engine, or a display. These are the
    /// same control classes the app runs - not a mock-up of them - so a rendering
    /// regression shows up here.
    /// </summary>
    private static void RenderControls(string path)
    {
        App.RenderOnly = true;
        // A real windowing root is required for Application.Styles (the control theme)
        // to apply, so use the headless platform with real (Skia) drawing.
        AppBuilder.Configure<App>()
            .UseSkia()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
            .SetupWithoutStarting();

        var spectrum = new GuardSpectrum { Height = 200 };
        spectrum.SetSlot(0, SynthSpectrum());
        spectrum.SetNotches(new[] { (2140f, -12f), (4700f, -6f) });
        spectrum.AddCatch(2140f);

        var tiles = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 12 };
        var lead = new ChannelTile("Lead", "CH 4");
        lead.SetLevel(0.74, -8f);
        lead.SetNotches(2);
        lead.FlagCatch();
        var bgv = new ChannelTile("BGV L", "CH 5");
        bgv.SetLevel(0.46, -16f);
        bgv.SetNotches(1);
        tiles.Children.Add(lead);
        tiles.Children.Add(bgv);

        var strip = new ChannelStrip(4, "Lead", "Input 4 (Thunderbolt)");
        strip.SyncArm(true);
        strip.SetLevel(0.72);
        strip.SetNotches(new[] { 2140f, 4700f });

        // both guard states side by side, so the wording can be checked at a glance
        var guardOn = new TapButton("Guard On", "tap to bypass") { IsLit = true };
        var guardOff = new TapButton("Guard Off", "tap to protect") { IsLit = false };
        var buttons = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 14 };
        buttons.Children.Add(guardOn);
        buttons.Children.Add(guardOff);
        buttons.Children.Add(new HoldButton("Panic", "hold 1s · clears every notch", Tokens.Clip));

        var window = new Window
        {
            Width = 900,
            Height = 560,
            Background = Tokens.Ground,
            Content = new StackPanel
            {
                Margin = new Thickness(20),
                Spacing = 16,
                Children = { spectrum, tiles, strip, buttons },
            },
        };

        window.Show();

        // Ballistics settle over a few frames; tick them so the meters aren't at zero.
        for (var i = 0; i < 40; i++)
        {
            lead.Tick();
            bgv.Tick();
            strip.Tick();
            Dispatcher.UIThread.RunJobs();
        }

        using var frame = window.CaptureRenderedFrame();
        frame!.Save(path);
        Console.WriteLine($"wrote {path}");
    }

    /// <summary>A ringing spectrum: a noise floor with two resonant peaks.</summary>
    private static float[] SynthSpectrum()
    {
        var bands = new float[GuardSpectrum.Bands];
        for (var i = 0; i < bands.Length; i++)
        {
            var floor = -74f + 5f * MathF.Sin(i * 0.7f);
            var peak1 = 44f * MathF.Exp(-MathF.Pow((i - 62) / 1.6f, 2));
            var peak2 = 30f * MathF.Exp(-MathF.Pow((i - 74) / 1.8f, 2));
            bands[i] = floor + peak1 + peak2;
        }
        return bands;
    }

    private static float[] SynthRta()
    {
        var f = Enumerable.Repeat(-72f, 100).ToArray();
        f[56] = -9f; f[55] = -22f; f[57] = -24f;   // a sharp ~1 kHz ring
        f[70] = -20f;                               // a smaller ~3 kHz one
        return f;
    }

    private static float[] SynthEngine(float peakHz, float spread, float top)
    {
        var f = new float[100];
        var peakBand = 99f * MathF.Log(peakHz / 20f) / MathF.Log(1000f);
        for (var i = 0; i < 100; i++)
        {
            var d = (i - peakBand) / (spread * 100f);
            f[i] = top - 55f * (d * d) - 8f;   // a broad hump around peakHz
        }
        return f;
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .LogToTrace();
}
