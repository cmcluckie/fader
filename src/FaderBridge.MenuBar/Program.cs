using Avalonia;
using Avalonia.Controls;
using Avalonia.Media.Imaging;

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
