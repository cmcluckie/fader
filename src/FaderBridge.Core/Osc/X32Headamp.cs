namespace Fader.Bridge.Osc;

/// <summary>
/// Headamp (input preamp) gain addressing, wrapping the §7 trap: the headamp
/// lives with the physical input, not the channel, and is only reachable when a
/// channel is actually sourced from that input. Verified on a real X32 Rack
/// (firmware 2.07): <c>/ch/03/config/source</c> = 3 (local input 3), and its
/// preamp is <c>/headamp/002/gain</c> = 0.5833 (≈ +30 dB). Local inputs 1..32
/// map to headamp index source-1; the gain range is −12…+60 dB.
/// </summary>
public static class X32Headamp
{
    public const float MinDb = -12f;
    public const float MaxDb = 60f;
    public const float RangeDb = MaxDb - MinDb;   // 72 dB

    /// <summary>Gain address for a 0-based headamp index (000..127).</summary>
    public static string Gain(int index) => $"/headamp/{index:D3}/gain";

    /// <summary><c>/ch/NN/config/source</c> - read this to learn what feeds a channel.</summary>
    public static string ChannelSource(int channel) => $"/ch/{channel:D2}/config/source";

    /// <summary>
    /// Headamp index for a channel's <c>config/source</c> value, or -1 when the
    /// channel is not fed from a local XLR input (1..32) - in which case the
    /// preamp is on a stagebox and reached differently, so callers must not
    /// blindly address a local headamp.
    /// </summary>
    public static int HeadampForSource(int source) => source is >= 1 and <= 32 ? source - 1 : -1;

    public static float ParToDb(float par) => MinDb + par * RangeDb;

    public static float DbToPar(float db) => Math.Clamp((db - MinDb) / RangeDb, 0f, 1f);
}
