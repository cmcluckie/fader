using Fader.Shared;

namespace FeedbackFader;

/// <summary>
/// Wires the engine's detections to the X32's RTA: whenever the engine flags a
/// ring on a mic, it checks the console's current RTA for a matching peak and,
/// if it finds one, raises a <see cref="CorrelatedRing"/>. The reusable product
/// form of the correlation the SpectrumScope diagnostic demonstrates - a GUI or
/// the tray subscribes to <see cref="Correlated"/> to surface it.
/// </summary>
public sealed class CorrelationMonitor : IDisposable
{
    private readonly FkEngineClient _engine;
    private readonly X32Rta _rta;
    private readonly Correlator _correlator;

    public CorrelationMonitor(FkEngineClient engine, X32Rta rta, Correlator? correlator = null)
    {
        _engine = engine;
        _rta = rta;
        _correlator = correlator ?? new Correlator();
        _engine.DetectionReceived += OnDetection;
    }

    /// <summary>Raised when an engine detection is corroborated by the RTA.</summary>
    public event Action<CorrelatedRing>? Correlated;

    private void OnDetection(FkDetection detection)
    {
        if (_correlator.Match(detection, _rta.Latest) is { } ring)
        {
            Correlated?.Invoke(ring);
        }
    }

    public void Dispose() => _engine.DetectionReceived -= OnDetection;
}
