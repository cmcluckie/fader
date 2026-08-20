namespace Fader.Bridge.Osc;

/// <summary>
/// Addressing and value maths for the X32's 31-band GEQ, as it sits on the
/// insert FX slots 5-8. Verified against a real X32 Rack (firmware 2.07): each
/// band is <c>/fx/&lt;slot&gt;/par/&lt;NN&gt;</c> (NN = 01..31) carrying a 0..1
/// value with 0.5 = flat. The ±15 dB range is the standard X32 GEQ; the
/// endpoints should be double-checked at the rig (deliverable-5).
/// </summary>
public static class X32Geq
{
    public const int BandCount = 31;
    public const float RangeDb = 15f;   // 0.5 par = 0 dB, 0.0 = -15 dB, 1.0 = +15 dB

    /// <summary>Standard ISO 1/3-octave centre frequencies, 20 Hz - 20 kHz.</summary>
    public static readonly float[] BandHz =
    {
        20f, 25f, 31.5f, 40f, 50f, 63f, 80f, 100f, 125f, 160f,
        200f, 250f, 315f, 400f, 500f, 630f, 800f, 1000f, 1250f, 1600f,
        2000f, 2500f, 3150f, 4000f, 5000f, 6300f, 8000f, 10000f, 12500f, 16000f, 20000f,
    };

    /// <summary>OSC address for one GEQ band. slot is the FX slot (5-8), band is 1..31.</summary>
    public static string Band(int slot, int band) => $"/fx/{slot}/par/{band:D2}";

    public static float ParToDb(float par) => (par - 0.5f) * (2f * RangeDb);

    public static float DbToPar(float db) => Math.Clamp(0.5f + db / (2f * RangeDb), 0f, 1f);

    /// <summary>1-based band (1..31) whose centre is closest to <paramref name="freqHz"/> in log space.</summary>
    public static int NearestBand(float freqHz)
    {
        if (freqHz <= 0f)
        {
            return 1;
        }
        var best = 1;
        var bestErr = float.MaxValue;
        var target = MathF.Log(freqHz);
        for (var i = 0; i < BandCount; i++)
        {
            var err = MathF.Abs(target - MathF.Log(BandHz[i]));
            if (err < bestErr)
            {
                bestErr = err;
                best = i + 1;
            }
        }
        return best;
    }
}
