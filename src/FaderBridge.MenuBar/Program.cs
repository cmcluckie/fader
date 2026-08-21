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

        // Hidden: render a themed ComboBox + CheckBox to a PNG, to verify the
        // control theme is actually loaded (templated controls draw, not blank).
        if (args is ["--render-config", var cpath])
        {
            RenderConfigControls(cpath);
            return;
        }

        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args, ShutdownMode.OnExplicitShutdown);
    }

    private static void RenderSpectrum(string path)
    {
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

    private static void RenderConfigControls(string path)
    {
        // A real windowing root is required for Application.Styles (the theme) to
        // apply, so use the headless platform with real (Skia) drawing and capture
        // the rendered frame.
        AppBuilder.Configure<App>()
            .UseSkia()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
            .SetupWithoutStarting();

        var window = new Window
        {
            Width = 320,
            Height = 170,
            Background = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.FromRgb(0x12, 0x14, 0x1A)),
            Content = new StackPanel
            {
                Margin = new Thickness(20),
                Spacing = 14,
                Children =
                {
                    new TextBlock { Text = "Device", Foreground = Avalonia.Media.Brushes.Gray },
                    new ComboBox
                    {
                        PlaceholderText = "Select audio device…",
                        ItemsSource = new[] { "Universal Audio Thunderbolt", "MacBook Pro Microphone" },
                        SelectedIndex = 0,
                        Width = 260,
                    },
                    new CheckBox { Content = "Ch 1", IsChecked = true },
                },
            },
        };

        window.Show();
        Dispatcher.UIThread.RunJobs();

        using var frame = window.CaptureRenderedFrame();
        frame!.Save(path);
        Console.WriteLine($"wrote {path}");
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
