using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Fader.Bridge;

public sealed class BridgeConfig
{
    /// <summary>X32 Rack IP address. Find it on the console under Setup > Network.</summary>
    public string X32IpAddress { get; set; } = "192.168.1.100";

    public int X32Port { get; set; } = 10023;

    /// <summary>Substring matched against the MIDI port name (case-insensitive).</summary>
    public string MidiPortName { get; set; } = "FaderPort";

    /// <summary>Faders on the surface. One FaderPort 8 bank = 8.</summary>
    public int StripCount { get; set; } = 8;

    /// <summary>Input channels on the console the bank window slides across.</summary>
    public int X32ChannelCount { get; set; } = 32;

    /// <summary>
    /// How often to re-send /xremote. The X32 stops sending updates ~10 s after
    /// the last one, so this must stay comfortably under that.
    /// </summary>
    public double KeepaliveSeconds { get; set; } = 9.0;

    /// <summary>Show the console channel number on the scribble strip's lower row.</summary>
    public bool ShowChannelNumbers { get; set; } = true;

    /// <summary>
    /// How often to re-query the whole bank. The X32 does not reliably push
    /// every parameter after a scene/snapshot recall, so a periodic pull is what
    /// actually keeps the surface honest. Touch gating and echo suppression stop
    /// it fighting the user. Set to 0 to disable.
    /// </summary>
    public double ResyncSeconds { get; set; } = 5.0;

    /// <summary>
    /// How long after sending a value to ignore the console echoing it back.
    /// Too low and faders judder; too high and fast console-side moves are missed.
    /// </summary>
    public int EchoSuppressionMs { get; set; } = 150;

    [JsonIgnore]
    public IPAddress ResolvedAddress { get; private set; } = IPAddress.None;

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    public static BridgeConfig Load(string path)
    {
        BridgeConfig config;

        if (File.Exists(path))
        {
            var json = File.ReadAllText(path);
            config = JsonSerializer.Deserialize<BridgeConfig>(json, Options)
                     ?? throw new InvalidDataException($"{path} is empty or not valid JSON.");
        }
        else
        {
            config = new BridgeConfig();
            File.WriteAllText(path, JsonSerializer.Serialize(config, Options));
            Console.WriteLine($"No config found - wrote a template to {path}");
        }

        config.Validate();
        return config;
    }

    private void Validate()
    {
        if (!IPAddress.TryParse(X32IpAddress, out var address))
        {
            throw new InvalidDataException(
                $"x32IpAddress \"{X32IpAddress}\" is not a valid IP address.");
        }
        ResolvedAddress = address;

        if (StripCount is < 1 or > 16)
        {
            throw new InvalidDataException("stripCount must be between 1 and 16.");
        }

        if (X32ChannelCount < StripCount)
        {
            throw new InvalidDataException(
                "x32ChannelCount cannot be smaller than stripCount.");
        }

        if (KeepaliveSeconds is <= 0 or > 10)
        {
            throw new InvalidDataException(
                "keepaliveSeconds must be > 0 and <= 10 (the X32 times out at ~10 s).");
        }
    }
}
