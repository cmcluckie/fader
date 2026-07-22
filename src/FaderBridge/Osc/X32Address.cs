namespace Fader.Bridge.Osc;

/// <summary>
/// X32 OSC address construction. Channel numbers are 1-based and zero-padded
/// to two digits (/ch/01/..., /ch/32/...).
/// </summary>
public static class X32Address
{
    public static string Fader(int channel) => $"/ch/{channel:D2}/mix/fader";

    /// <summary>
    /// Mute state. NOTE THE INVERTED SENSE: 1 = channel on (unmuted), 0 = muted.
    /// This is the single most common source of backwards mute behaviour.
    /// </summary>
    public static string MixOn(int channel) => $"/ch/{channel:D2}/mix/on";

    public static string Name(int channel) => $"/ch/{channel:D2}/config/name";

    public static string Color(int channel) => $"/ch/{channel:D2}/config/color";

    /// <summary>Solo is global and 1-based across the console's whole channel list.</summary>
    public static string Solo(int channel) => $"/-stat/solosw/{channel:D2}";

    /// <summary>Currently selected channel, as a 0-based index.</summary>
    public const string SelectedIndex = "/-stat/selidx";

    /// <summary>
    /// Parse "/ch/07/mix/fader" into (7, "mix/fader"). Returns false for any
    /// address that is not a /ch/NN/ parameter.
    /// </summary>
    public static bool TryParseChannel(string address, out int channel, out string parameter)
    {
        channel = 0;
        parameter = string.Empty;

        // "/ch/NN/" is 7 characters before the parameter starts.
        if (address.Length < 8 || !address.StartsWith("/ch/", StringComparison.Ordinal))
        {
            return false;
        }

        if (!int.TryParse(address.AsSpan(4, 2), out channel) || address[6] != '/')
        {
            return false;
        }

        parameter = address[7..];
        return true;
    }
}
