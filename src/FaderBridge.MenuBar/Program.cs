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

        // Hidden: drive a real pointer drag at the listen-band handle and report
        // whether the control received it. Pointer plumbing is easy to get wrong and
        // impossible to eyeball from a screenshot.
        // Hidden: compose the whole Setup screen against a real controller (never
        // started, so no engine) and render it - the screen has grown enough panels
        // that "does it still lay out" is worth proving.
        if (args is ["--render-setup", var spath])
        {
            RenderSetup(spath);
            return;
        }

        // Hidden: open and close the window repeatedly while the controller raises
        // the events the auto-floor raises, hunting the crash reported from the rig.
        if (args is ["--test-window-churn"])
        {
            TestWindowChurn();
            return;
        }

        if (args is ["--test-drag"])
        {
            TestDrag();
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

        // twenty notches, as reported from the rig - the label crowding case
        var spectrum = new GuardSpectrum { Height = 200, Editable = true };
        spectrum.SetSlot(0, SynthSpectrum());
        spectrum.SetRange(1000f, 16000f);
        spectrum.SetFloor(-55f);
        // The exact filter set read off the rig on 2026-08-27, when the guard was
        // reported as muffled: 22 live filters, twelve at the -24 dB cap, summing
        // to -13.6 dB across the top end. This is the case the EQ curve exists to
        // make visible, so it is the case the render harness should show.
        var many = new List<(float, float)>
        {
            (3803f, -24f), (4342f, -24f), (4985f, -18f), (5316f, -24f), (5819f, -24f),
            (6029f, -24f), (6464f, -10.5f), (7020f, -18f), (7403f, -12f), (8107f, -12f),
            (8648f, -12f), (9304f, -18f), (9744f, -12f), (10235f, -12f), (10621f, -24f),
            (11438f, -24f), (11889f, -18f), (12912f, -24f), (13792f, -24f), (14567f, -24f),
            (15095f, -24f), (15864f, -18f),
        };
        spectrum.SetNotches(many);
        spectrum.AddCatch(6029f);

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
        var capOff = new TapButton("CAPTURE", "tap · logs every catch") { IsLit = false };
        var capOn  = new TapButton("CAPTURING", "tap to stop · writing every catch")
        {
            IsLit = true, LitColour = Tokens.CatchColor,
        };
        var buttons = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 14 };
        buttons.Children.Add(capOff);
        buttons.Children.Add(capOn);
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

    private static void RenderSetup(string path)
    {
        App.RenderOnly = true;
        AppBuilder.Configure<App>()
            .UseSkia()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
            .SetupWithoutStarting();

        var dir = Path.Combine(Path.GetTempPath(), "fk-render-" + Guid.NewGuid().ToString("N"));
        var feedback = new Fader.Bridge.Feedback.FeedbackController("/nonexistent/fk-engine", dir);
        var view = new SetupView(feedback);

        var window = new Window { Width = 1000, Height = 760, Background = Tokens.Ground, Content = view };
        window.Show();
        for (var i = 0; i < 10; i++) { view.Tick(); Dispatcher.UIThread.RunJobs(); }

        using var frame = window.CaptureRenderedFrame();
        frame!.Save(path);
        Console.WriteLine($"wrote {path}");
        try { Directory.Delete(dir, true); } catch { }
    }

    private static void TestWindowChurn()
    {
        App.RenderOnly = true;
        AppBuilder.Configure<App>()
            .UseSkia()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
            .SetupWithoutStarting();

        var dir = Path.Combine(Path.GetTempPath(), "fk-churn-" + Guid.NewGuid().ToString("N"));
        var feedback = new Fader.Bridge.Feedback.FeedbackController("/nonexistent/fk-engine", dir);

        try
        {
            for (var round = 1; round <= 3; round++)
            {
                var window = new FaderWindow(feedback, System.Net.IPAddress.Loopback);
                window.Show();
                Dispatcher.UIThread.RunJobs();

                // what the auto-floor does every two seconds
                for (var i = 0; i < 3; i++)
                {
                    feedback.SetFloorAuto(i % 2 == 0);
                    Dispatcher.UIThread.RunJobs();
                }

                window.Close();
                Dispatcher.UIThread.RunJobs();
                Console.WriteLine($"  round {round}: opened and closed");

                // and again AFTER the window is gone - the case a closed view
                // still being subscribed would blow up on
                for (var i = 0; i < 3; i++)
                {
                    feedback.SetFloorAuto(i % 2 == 0);
                    Dispatcher.UIThread.RunJobs();
                }
                Console.WriteLine($"  round {round}: survived events after close");
            }
            Console.WriteLine("PASS: no exception");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"REPRODUCED: {ex.GetType().Name}: {ex.Message}");
            Console.WriteLine(ex.StackTrace);
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    private static void TestDrag()
    {
        App.RenderOnly = true;
        AppBuilder.Configure<App>()
            .UseSkia()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
            .SetupWithoutStarting();

        var band = new GuardSpectrum { Height = 150, Editable = true };
        band.SetSlot(0, SynthSpectrum());
        band.SetRange(200f, 16000f);
        (float Lo, float Hi) range = (200f, 16000f);
        var floor = -70f;
        band.SetFloor(floor);
        band.RangeDragged += (lo, hi) => range = (lo, hi);
        band.FloorDragged += db => floor = db;

        var window = new Window { Width = 900, Height = 200, Background = Tokens.Ground, Content = band };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        var w = band.Bounds.Width;
        var h = band.Bounds.Height;
        var y = h / 2;
        var ok = true;

        double HandleX(double hz) =>
            Math.Clamp(GuardSpectrum.XForHz(hz, w), 8, w - 8);

        void Drag(string what, double fromX, double toX)
        {
            window.MouseDown(new Point(fromX, y), Avalonia.Input.MouseButton.Left);
            Dispatcher.UIThread.RunJobs();
            window.MouseMove(new Point(toX, y));
            Dispatcher.UIThread.RunJobs();
            window.MouseUp(new Point(toX, y), Avalonia.Input.MouseButton.Left);
            Dispatcher.UIThread.RunJobs();

            var lx = HandleX(range.Lo);
            var hx = HandleX(range.Hi);
            var onScreen = lx >= 0 && lx <= w && hx >= 0 && hx <= w;
            var sane = range.Lo >= GuardSpectrum.MinEdgeHz && range.Lo <= GuardSpectrum.MaxLowHz
                       && range.Hi <= GuardSpectrum.MaxEdgeHz && range.Hi > range.Lo;
            if (!onScreen || !sane) ok = false;
            Console.WriteLine($"  {what,-34} -> {range.Lo,7:0} Hz .. {range.Hi,6:0} Hz   handles x={lx:0}/{hx:0}  {(onScreen && sane ? "ok" : "BAD")}");
        }

        Console.WriteLine($"band {w:0}x{h:0}, dragging past both edges:");
        Drag("low handle shoved far LEFT", HandleX(range.Lo), -400);
        Drag("low handle shoved far RIGHT", HandleX(range.Lo), w + 400);
        Drag("high handle shoved far RIGHT", HandleX(range.Hi), w + 400);
        Drag("high handle shoved far LEFT", HandleX(range.Hi), -400);
        Drag("low handle back to the middle", HandleX(range.Lo), w * 0.4);

        Console.WriteLine(ok ? "PASS: handles stayed on screen and in range"
                             : "FAIL: a handle escaped");
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
