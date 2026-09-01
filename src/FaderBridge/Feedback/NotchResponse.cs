namespace Fader.Bridge.Feedback;

/// <summary>
/// The combined frequency response of a set of notches - what the guard is
/// actually doing to the sound.
///
/// This exists because the answer was invisible. Every filter the bank places is
/// individually correct, and the display showed them as tidy individual markers,
/// so nothing on screen said that twenty-two of them 400-800 Hz apart, each over a
/// kilohertz wide, add up to a high-cut filter. Measured on the rig on 2026-08-27:
/// -13.6 dB average across 3.8-16 kHz, -28 dB at 6 kHz, with the least attenuated
/// point up there still down 5.6 dB. It sounded muffled because it was.
///
/// Mirrors the engine exactly: RBJ peaking EQ, and the same depth-dependent Q as
/// NotchBank::qForDepth. If either changes, both must.
/// </summary>
public static class NotchResponse
{
    /// <summary>The engine runs at 48 kHz; the curve is drawn in its terms.</summary>
    private const double SampleRate = 48000.0;
    private const double BaseQ = 25.0;

    /// <summary>
    /// Q rises with depth to hold the audible width roughly constant - see
    /// NotchBank::qForDepth. Never below the base Q, so a filter only narrows.
    /// </summary>
    public static double QForDepth(double cutDb) => BaseQ * Math.Max(1.0, Math.Abs(cutDb) / 18.0);

    /// <summary>One peaking filter's gain at a frequency, in dB (negative = cut).</summary>
    public static double OneDb(double atHz, double centreHz, double cutDb)
    {
        if (centreHz <= 0 || atHz <= 0 || cutDb >= -0.01) return 0.0;

        var q = QForDepth(cutDb);
        var a = Math.Pow(10.0, cutDb / 40.0);
        var w = 2.0 * Math.PI * centreHz / SampleRate;
        var alpha = Math.Sin(w) / (2.0 * q);
        var big = 2.0 * Math.PI * atHz / SampleRate;

        double Mag(double c0, double c1, double c2)
        {
            var re = c0 + c1 * Math.Cos(big) + c2 * Math.Cos(2.0 * big);
            var im = -(c1 * Math.Sin(big) + c2 * Math.Sin(2.0 * big));
            return Math.Sqrt(re * re + im * im);
        }

        var num = Mag(1.0 + alpha * a, -2.0 * Math.Cos(w), 1.0 - alpha * a);
        var den = Mag(1.0 + alpha / a, -2.0 * Math.Cos(w), 1.0 - alpha / a);
        return 20.0 * Math.Log10(Math.Max(num / den, 1e-12));
    }

    /// <summary>Every filter summed, at one frequency.</summary>
    public static double SumDb(IReadOnlyList<(float Hz, float Depth)> notches, double atHz)
    {
        var total = 0.0;
        for (var i = 0; i < notches.Count; i++) total += OneDb(atHz, notches[i].Hz, notches[i].Depth);
        return total;
    }

    /// <summary>
    /// Average cut between two frequencies, sampled logarithmically. This is the
    /// number that corresponds to "it sounds muffled": one figure for how much of
    /// the top end the guard is currently removing.
    /// </summary>
    public static double AverageDb(IReadOnlyList<(float Hz, float Depth)> notches,
                                   double fromHz, double toHz, int points = 48)
    {
        if (notches.Count == 0 || points < 2) return 0.0;
        var total = 0.0;
        for (var i = 0; i < points; i++)
        {
            var f = fromHz * Math.Pow(toHz / fromHz, i / (double) (points - 1));
            total += SumDb(notches, f);
        }
        return total / points;
    }
}
