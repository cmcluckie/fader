using System.Net;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using FeedbackFader;

using Fader.Shared.Ui;

namespace FeedbackFader.App;

/// <summary>
/// The graphical spectrum window. Draws the engine's LEAD/BGV spectra and the
/// X32 RTA together (via <see cref="SpectrumView"/>), owns the RTA subscription,
/// and marks detections - highlighting ones the RTA corroborates. Opened from the
/// tray; closing it tears down the RTA subscription.
/// </summary>
public sealed class SpectrumWindow : Window
{
    private const int Bands = 100;

    private readonly FeedbackController _feedback;
    private readonly X32Rta _rta;
    private readonly Correlator _correlator = new();
    private readonly SpectrumView _view = new();
    private readonly DispatcherTimer _redraw;

    public SpectrumWindow(FeedbackController feedback, IPAddress rtaAddress)
    {
        _feedback = feedback;

        Title = "Feedback spectrum";
        Width = 760;
        Height = 380;
        Background = new SolidColorBrush(Color.FromRgb(0x12, 0x14, 0x1A));
        Content = _view;

        _rta = new X32Rta(rtaAddress);

        _feedback.SpectrumChanged += OnSpectrum;
        _feedback.NotchesChanged += OnNotches;
        _feedback.DetectionReceived += OnDetection;
        _rta.FrameReceived += OnRta;

        _redraw = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(33) };  // ~30 fps
        _redraw.Tick += (_, _) => _view.InvalidateVisual();

        Opened += (_, _) => { _rta.Start(); _redraw.Start(); };
        Closed += (_, _) => Teardown();
    }

    private void OnSpectrum(FkSpectrum spec) =>
        Dispatcher.UIThread.Post(() => _view.SetEngine(spec.Channel, ToLogAxis(spec)));

    private void OnRta(float[] frame) =>
        Dispatcher.UIThread.Post(() => _view.SetRta(frame));

    private void OnNotches(int channel, FkNotch[] notches)
    {
        var active = notches.Where(n => n.Active).Select(n => (n.FreqHz, n.Locked)).ToArray();
        Dispatcher.UIThread.Post(() => _view.SetNotches(channel, active));
    }

    private void OnDetection(FkDetection d)
    {
        var correlated = _correlator.Match(d, _rta.Latest) is not null;
        Dispatcher.UIThread.Post(() => _view.AddDetection(d.Hz, correlated));
    }

    private void Teardown()
    {
        _redraw.Stop();
        _feedback.SpectrumChanged -= OnSpectrum;
        _feedback.NotchesChanged -= OnNotches;
        _feedback.DetectionReceived -= OnDetection;
        _rta.FrameReceived -= OnRta;
        _ = _rta.DisposeAsync();
    }

    /// <summary>Resample the engine's linear-frequency spectrum onto the RTA's 100 log bands.</summary>
    private static float[] ToLogAxis(FkSpectrum spec)
    {
        var outp = new float[Bands];
        for (var i = 0; i < Bands; i++)
        {
            var bin = (int) MathF.Round(X32Rta.BandHz(i) / MathF.Max(1f, spec.HzPerBin));
            outp[i] = bin >= 0 && bin < spec.Magnitudes.Length ? spec.Magnitudes[bin] : -120f;
        }
        return outp;
    }
}
