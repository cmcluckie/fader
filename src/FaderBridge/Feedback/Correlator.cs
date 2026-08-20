using Fader.Bridge.Osc;

namespace Fader.Bridge.Feedback;

/// <summary>One ring seen from both sides: the engine on a mic, the X32 RTA on the system.</summary>
public sealed record CorrelatedRing(
    int Channel, float EngineHz, float EngineDb, int RtaBand, float RtaHz, float RtaDb);

/// <summary>
/// Correlates the feedback engine's per-mic detections with the X32's RTA. A ring
/// the engine flags on a vocal channel and a peak the console's RTA shows at the
/// same frequency are the same event from two vantage points - worth surfacing,
/// and something no commercial box does (§6).
///
/// Stateless: given a detection and the current RTA frame, it decides whether the
/// RTA corroborates the ring near that frequency.
/// </summary>
public sealed class Correlator
{
    private readonly float _tolFraction;
    private readonly float _rtaThresholdDb;

    public Correlator(float tolFraction = 0.06f, float rtaThresholdDb = -50f)
    {
        _tolFraction = tolFraction;
        _rtaThresholdDb = rtaThresholdDb;
    }

    /// <summary>The RTA band whose centre is nearest <paramref name="hz"/>.</summary>
    public static int NearestRtaBand(float hz)
    {
        if (hz <= 0f)
        {
            return 0;
        }
        var i = (int) MathF.Round(99f * MathF.Log(hz / 20f) / MathF.Log(1000f));
        return Math.Clamp(i, 0, X32Rta.BandCount - 1);
    }

    /// <summary>
    /// Whether this engine detection is corroborated by the RTA: a local peak
    /// near the same frequency and above the threshold. Null if not.
    /// </summary>
    public CorrelatedRing? Match(FkDetection detection, float[] rtaFrame)
    {
        if (rtaFrame.Length != X32Rta.BandCount || detection.Hz <= 0f)
        {
            return null;
        }

        var band = NearestRtaBand(detection.Hz);
        var bandHz = X32Rta.BandHz(band);

        // Frequencies must agree within tolerance (in log space).
        if (MathF.Abs(MathF.Log(bandHz) - MathF.Log(detection.Hz)) > MathF.Log(1f + _tolFraction))
        {
            return null;
        }

        var db = rtaFrame[band];
        if (db < _rtaThresholdDb || !IsLocalPeak(rtaFrame, band))
        {
            return null;
        }

        return new CorrelatedRing(detection.Channel, detection.Hz, detection.LevelDb, band, bandHz, db);
    }

    private static bool IsLocalPeak(float[] frame, int band)
    {
        var lo = band > 0 ? frame[band - 1] : float.NegativeInfinity;
        var hi = band < frame.Length - 1 ? frame[band + 1] : float.NegativeInfinity;
        return frame[band] >= lo && frame[band] >= hi;
    }
}
